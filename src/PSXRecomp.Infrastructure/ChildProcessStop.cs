using System.Diagnostics;
using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// What one stop request did to one child process (Issue #827). A stop is <em>confirmed</em> only when the child and every
/// descendant it had at the stop were seen exited. A kill request alone never confirms it. A stop the OS refused, a child still
/// running after the bounded wait, a descendant still running, or an output pipe still held after the stop is <em>refused</em>,
/// with its reason.
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

    /// <summary>The OS process id of the child the stop targets, or 0 before the child starts.</summary>
    internal int ProcessId { get; private set; }

    /// <summary>True once a stop has finished its decision (confirmed, refused, or found the child already gone).</summary>
    internal bool Settled => _settled;

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
            finally
            {
                _settled = true;
            }
        }
    }

    private void Decide(Process process)
    {
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
            try
            {
                if (descendants is null)
                {
                    RefuseIfUnset("the child's process tree cannot be listed on this platform");
                }

                process.Kill(true);
                if (!process.WaitForExit(Remaining(clock)))
                {
                    RefuseIfUnset($"the child {ProcessId} was still running {ConfirmTimeout.TotalSeconds:0} s after the kill");
                    return;
                }

                if (descendants is not null)
                {
                    var alive = descendants.Where(d => !WaitGone(d, clock)).Select(d => d.Id).ToList();
                    if (alive.Count > 0)
                    {
                        RefuseIfUnset($"descendant processes {string.Join(", ", alive)} were still running after the kill");
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
            // An exit between the check and the kill is a natural end of the child, not a refused stop.
            if (!HasExitedOrGone(process))
            {
                RefuseIfUnset($"the kill was refused: {exception.Message}");
                Trace.WriteLine($"Stopping the artifact process failed: {exception.Message}");
            }
        }
    }

    /// <summary>True when the child has exited, or its handle is no longer usable (the run already released it).</summary>
    private static bool HasExitedOrGone(Process process)
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

    private static TimeSpan Remaining(Stopwatch clock)
    {
        var left = ConfirmTimeout - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static bool WaitGone(Process descendant, Stopwatch clock)
    {
        try
        {
            return descendant.WaitForExit(Remaining(clock));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return descendant.HasExited;
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
    /// <summary>The descendants of <paramref name="root"/> as process handles (the caller disposes them), or null when unsupported.</summary>
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
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone between the listing and the handle: it is not a survivor.
                }
            }
        }

        return found;
    }
}
