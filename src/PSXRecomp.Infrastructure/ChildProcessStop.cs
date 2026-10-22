using System.Diagnostics;
using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// What one stop request did to one child process (Issue #827). A stop is <em>confirmed</em> only when a live child was killed and
/// the child and every descendant it had at the stop were seen exited. A kill request alone never confirms it. A stop the OS refused,
/// a child still running after the bounded wait, a descendant still running or not checkable, or an output pipe still held after the
/// stop is <em>refused</em>, with its reason. A descendant left running after the child had ended on its own is a
/// <em>leftover</em>: it is reaped and recorded, and the stop does not claim it as a clean end.
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
    private string? _leftover;
    private DateTime? _rootStart;
    private volatile bool _settled;

    /// <summary>
    /// Lists the descendants of the child (the test seam). Null uses <see cref="ProcessTree.DescendantsOf"/>. Returns null where
    /// the platform cannot list them.
    /// </summary>
    internal Func<Process, IReadOnlyList<Process>?>? DescendantsProbe { get; init; }

    /// <summary>
    /// Observes each wait of the run (the test seam). It receives the record, the child, the wait's name ("exit" or "pump"), and what
    /// the wait reported; it returns what the run should use. Null observes nothing.
    /// </summary>
    internal Func<StopRecord, Process, string, bool, bool>? ObserveForTest { get; init; }

    /// <summary>
    /// Replaces the kill of the child (the test seam): a test injects the OS refusal here. Null kills the process tree.
    /// </summary>
    internal Action<Process>? KillForTest { get; init; }

    /// <summary>The OS process id of the child the stop targets, or 0 before the child starts.</summary>
    internal int ProcessId { get; private set; }

    /// <summary>True once a stop has finished its decision (confirmed, refused, or found the child already gone).</summary>
    public bool Settled => _settled;

    /// <summary>The descendants a stop left running after the child had ended on its own, or null.</summary>
    internal string? Leftover
    {
        get
        {
            lock (_gate)
            {
                return _leftover;
            }
        }
    }

    /// <summary>Applies the observation seam to a wait's report (identity when no seam is set).</summary>
    internal bool Observe(Process process, string phase, bool ended) =>
        ObserveForTest is null ? ended : ObserveForTest(this, process, phase, ended);

    /// <summary>Kills the process tree of <paramref name="process"/>, or the test seam's replacement.</summary>
    internal void KillTree(Process process)
    {
        if (KillForTest is { } kill)
        {
            kill(process);
            return;
        }

        process.Kill(true);
    }

    /// <summary>
    /// Registers the stop for the child. The child is killed only when the stop token is cancelled. Dispose the returned record
    /// (see <see cref="Seal"/>) before the process is disposed: a stop must never see a disposed process.
    /// </summary>
    internal IDisposable Attach(Process process, CancellationToken stopToken)
    {
        ProcessId = process.Id;
        _rootStart = ProcessTree.StartTimeOf(process);
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
        IReadOnlyList<Process>? descendants = null;
        try
        {
            var clock = Stopwatch.StartNew();

            // Listed before the kill, and also when the root has already ended: a child keeps its parent's process id, so its
            // descendants are still found (Issue #827 review). Listing comes before the exit check, so a leftover is never missed.
            descendants = DescendantsProbe is null ? ProcessTree.DescendantsOf(process, _rootStart) : DescendantsProbe(process);
            listed = true;

            // Checked again after the listing: the child may have ended while its tree was listed. Then it is a natural end, and a
            // descendant it left running is a leftover. It is never a confirmed stop.
            if (HasExitedOrGone(process))
            {
                ReapLeftovers(descendants);
                return;
            }

            if (descendants is null)
            {
                RefuseIfUnset("the child's process tree cannot be listed on this platform");
            }

            try
            {
                KillTree(process);
            }
            catch (Exception exception)
            {
                // The OS refused the kill: a refusal with its reason. A child that ended while the kill was sent is a natural end.
                if (!HasExitedOrGone(process))
                {
                    RefuseIfUnset($"the kill was refused: {exception.Message}");
                }

                return;
            }

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

    /// <summary>
    /// A descendant still running after its root ended on its own is a leftover. It is killed here (best effort), and the leftover is
    /// recorded with its outcome, so the run never reports the leak as a clean end.
    /// </summary>
    private void ReapLeftovers(IReadOnlyList<Process>? descendants)
    {
        if (descendants is null)
        {
            return;
        }

        var leftovers = new List<string>();
        foreach (var descendant in descendants)
        {
            var problem = AwaitGone(descendant, TimeSpan.Zero);
            if (problem is null)
            {
                continue;
            }

            try
            {
                descendant.Kill(true);
                leftovers.Add($"{problem} (killed)");
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                leftovers.Add($"{problem} (kill refused: {exception.Message})");
            }
        }

        if (leftovers.Count > 0)
        {
            lock (_gate)
            {
                _leftover = string.Join("; ", leftovers);
            }
        }
    }

    private static TimeSpan Remaining(Stopwatch clock)
    {
        var left = ConfirmTimeout - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Waits for one descendant to exit. Returns null when it is gone, otherwise the reason it is not confirmed.</summary>
    private static string? AwaitGone(Process descendant, Stopwatch clock) => AwaitGone(descendant, Remaining(clock));

    private static string? AwaitGone(Process descendant, TimeSpan wait)
    {
        try
        {
            return descendant.WaitForExit(wait)
                ? null
                : $"descendant process {DescribeId(descendant)} was still running";
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
    /// The root may already have exited: its children are found by the root's process id.
    /// </summary>
    internal static IReadOnlyList<Process>? DescendantsOf(Process root, DateTime? rootStart)
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
                Process child;
                try
                {
                    child = Process.GetProcessById(childId);
                }
                catch (ArgumentException)
                {
                    // Not running any more (the id names no process): it is not a survivor.
                    continue;
                }

                // A child always starts after its parent. A process that started earlier under a reused id is not a descendant.
                if (rootStart is { } start && StartTimeOf(child) is { } childStart && childStart < start)
                {
                    child.Dispose();
                    continue;
                }

                frontier.Enqueue(childId);
                found.Add(child);
            }
        }

        return found;
    }

    /// <summary>The start time of a process, or null when it cannot be read (the caller then keeps the process).</summary>
    internal static DateTime? StartTimeOf(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
