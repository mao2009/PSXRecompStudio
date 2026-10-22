using System.Diagnostics;
using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// What one stop request did to one child process (Issue #827). A stop is <em>confirmed</em> only when the child and every
/// descendant it had at the stop were seen exited. A kill request alone never confirms it. A stop the OS refused, a child still
/// running after the bounded wait, a descendant still running or not checkable, or an output pipe still held after the stop is
/// <em>refused</em>, with its reason.
/// </summary>
/// <remarks>
/// The stop runs under <see cref="_gate"/>. The run seals the record (<see cref="Seal"/>) before it reads the outcome, so a stop that
/// is still in progress when the child ends on its own is waited for, and a stop that starts after the seal does nothing.
/// </remarks>
[Infrastructure]
internal sealed class StopRecord : IDisposable
{
    /// <summary>The longest a stop waits for the child and its descendants to be seen exited.</summary>
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often a bounded wait re-checks whether the stop has settled.</summary>
    internal const int PollMs = 100;

    private readonly object _gate = new();
    private IDisposable? _registration;
    private bool _sealed;
    private bool _confirmed;
    private string? _refused;
    private volatile bool _settled;

    /// <summary>
    /// Lists the descendants of the child before the kill (the test seam). Null uses <see cref="ProcessTree.DescendantsOf"/>.
    /// Returns null where the platform cannot list them, which is a refusal.
    /// </summary>
    internal Func<Process, IReadOnlyList<Process>?>? DescendantsProbe { get; init; }

    /// <summary>
    /// Observes each wait of the run (the test seam, Issue #827). It receives the record, the child, the wait's name ("exit" or
    /// "pump"), and what the wait reported; it returns what the run should use. Null observes nothing.
    /// </summary>
    internal Func<StopRecord, Process, string, bool, bool>? ObserveForTest { get; init; }

    /// <summary>The OS process id of the child the stop targets, or 0 before the child starts.</summary>
    internal int ProcessId { get; private set; }

    /// <summary>True once a stop has finished its decision (confirmed, refused, or found the child already gone).</summary>
    public bool Settled => _settled;

    /// <summary>Applies the observation seam to a wait's report (identity when no seam is set).</summary>
    internal bool Observe(Process process, string phase, bool ended) =>
        ObserveForTest is null ? ended : ObserveForTest(this, process, phase, ended);

    /// <summary>
    /// Registers the stop for the child. The child is killed only when the stop token is cancelled. Dispose the returned record
    /// (see <see cref="Seal"/>) before the process is disposed: a stop must never see a disposed process.
    /// </summary>
    internal IDisposable Attach(Process process, CancellationToken stopToken)
    {
        ProcessId = process.Id;
        _registration = stopToken.Register(() => Stop(process));
        return this;
    }

    /// <summary>
    /// Unregisters the stop and waits for a stop in progress to finish. After this returns, nothing changes the outcome.
    /// </summary>
    internal void Seal()
    {
        // Disposing the registration waits for a callback that is already running, so the outcome is final after it.
        _registration?.Dispose();
        lock (_gate)
        {
            _sealed = true;
        }
    }

    public void Dispose() => Seal();

    /// <summary>The outcome of the stop. Read it after <see cref="Seal"/>.</summary>
    internal (bool Confirmed, string? Refused) Outcome
    {
        get
        {
            lock (_gate)
            {
                // A refusal recorded after a confirmation (for example a pipe still held) still makes the stop refused.
                return (_confirmed && _refused is null, _refused);
            }
        }
    }

    /// <summary>Records a refusal, keeping the first reason. It takes precedence over a confirmation.</summary>
    internal void RefuseIfUnset(string reason)
    {
        lock (_gate)
        {
            _refused ??= reason;
        }
    }

    /// <summary>True when the child has exited, or its handle is no longer usable (the run already released it).</summary>
    internal static bool HasExitedOrGone(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void Stop(Process process)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                return;
            }

            try
            {
                Decide(process);
            }
            catch (Exception exception)
            {
                // Nothing escapes the stop thread, and nothing is left undecided: an unexpected failure is a refusal.
                RefuseIfUnset($"the stop failed unexpectedly: {exception.Message}");
            }
            finally
            {
                _settled = true;
            }
        }
    }

    private void Decide(Process process)
    {
        var listed = false;
        var killed = false;
        try
        {
            if (HasExitedOrGone(process))
            {
                // The child ended on its own before the stop reached it: that is not a stop.
                return;
            }

            var clock = Stopwatch.StartNew();

            // The descendants are listed before the kill: once the root is gone, its children are no longer attached to it.
            var descendants = DescendantsProbe is null ? ProcessTree.DescendantsOf(process) : DescendantsProbe(process);
            listed = true;
            try
            {
                if (descendants is null)
                {
                    RefuseIfUnset("the child's process tree cannot be listed on this platform");
                }

                process.Kill(true);
                killed = true;
                if (!process.WaitForExit(Remaining(clock)))
                {
                    RefuseIfUnset($"the child {ProcessId} was still running {ConfirmTimeout.TotalSeconds:0} s after the kill");
                    return;
                }

                if (descendants is not null)
                {
                    var problems = descendants.Select(descendant => AwaitGone(descendant, clock)).OfType<string>().ToList();
                    if (problems.Count > 0)
                    {
                        RefuseIfUnset(string.Join("; ", problems));
                        return;
                    }
                }

                if (_refused is null)
                {
                    _confirmed = true;
                }
            }
            finally
            {
                if (descendants is not null)
                {
                    foreach (var descendant in descendants)
                    {
                        descendant.Dispose();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // A kill that failed only because the child had already exited, after its tree was listed, is a natural end. Every other
            // failure (a refused kill, a tree that could not be checked) is a refused stop, never a plain failure (Issue #827).
            if (listed && !killed && HasExitedOrGone(process))
            {
                return;
            }

            RefuseIfUnset($"the stop could not be confirmed: {exception.Message}");
            Trace.WriteLine($"Stopping the artifact process {ProcessId} failed: {exception.Message}");
        }
    }

    private static TimeSpan Remaining(Stopwatch clock)
    {
        var left = ConfirmTimeout - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Waits for one descendant to exit. Returns null when it is gone, otherwise the reason it is not confirmed.</summary>
    private static string? AwaitGone(Process descendant, Stopwatch clock)
    {
        try
        {
            return descendant.WaitForExit(Remaining(clock))
                ? null
                : $"descendant process {DescribeId(descendant)} was still running after the kill";
        }
        catch (Exception exception)
        {
            // A handle that cannot be checked is not known to be gone: the stop is refused, never confirmed.
            return $"descendant process {DescribeId(descendant)} could not be checked: {exception.Message}";
        }
    }

    private static string DescribeId(Process process)
    {
        try
        {
            return process.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }
}

/// <summary>
/// Lists the live descendants of a process (Issue #827), so a stop can confirm that the whole tree is gone. The parent links come
/// from <see cref="ProcessTreeInterop"/>.
/// </summary>
[Infrastructure]
internal static class ProcessTree
{
    /// <summary>
    /// The descendants of <paramref name="root"/> as process handles (the caller disposes them), or null when unsupported. A process
    /// that is gone before its handle is opened is not a survivor. Any other failure to open a handle is thrown: the caller refuses.
    /// </summary>
    internal static IReadOnlyList<Process>? DescendantsOf(Process root)
    {
        var parents = ProcessTreeInterop.TryReadParentIds();
        if (parents is null)
        {
            return null;
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in parents)
        {
            if (!children.TryGetValue(ppid, out var list))
            {
                children[ppid] = list = [];
            }

            list.Add(pid);
        }

        var found = new List<Process>();
        var seen = new HashSet<int> { root.Id };
        var frontier = new Queue<int>();
        frontier.Enqueue(root.Id);
        while (frontier.Count > 0)
        {
            if (!children.TryGetValue(frontier.Dequeue(), out var childIds))
            {
                continue;
            }

            foreach (var childId in childIds.Where(seen.Add))
            {
                frontier.Enqueue(childId);
                try
                {
                    found.Add(Process.GetProcessById(childId));
                }
                catch (ArgumentException)
                {
                    // Not running any more (the id names no process): it is not a survivor.
                }
            }
        }

        return found;
    }
}
