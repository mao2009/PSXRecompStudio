using System.Diagnostics;
using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// What one run did with its child process and the child's descendants (Issue #827).
/// <para>
/// Descendants are <em>tracked</em> while the child is alive: every wait of the run samples the process tree, and a process is claimed
/// only when its parent's identity (process id and start time) still holds. A tracked process is later stopped or reaped only through
/// its own handle, after its start time is checked again. A process id seen after its owner ended is never claimed.
/// </para>
/// <para>
/// A stop is <em>confirmed</em> only when a live child was killed and every tracked process was seen exited. A kill the OS refused, a
/// child or tracked process still running after the bounded wait, a process that could not be checked, or an output pipe still held after
/// the stop is <em>refused</em>, with its reason. A tracked process left running when a stop reaches a child that has already ended is a
/// <em>leftover</em>: it is reaped and recorded, and the stop is refused. A child that ends on its own, without a stop, is not handled
/// here: its descendants are not reaped at its end (the natural end is out of scope, Issue #829).
/// </para>
/// </summary>
/// <remarks>
/// The stop runs under <see cref="_gate"/>. The run seals the record (<see cref="Seal"/>) before it reads the outcome, so a stop that is
/// still in progress when the child ends on its own is waited for, and a stop that starts after the seal does nothing.
/// </remarks>
[Infrastructure]
internal sealed class StopRecord : IDisposable
{
    /// <summary>The longest a stop (or a reap) waits for a process to be seen exited.</summary>
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The one second pass of a reap (round 19, MEDIUM 1): a window shared by the survivors of the first pass, run once. It is the only grace a reap
    /// has; a process that is only just dead is judged here, not reported alive on the first pass's zero wait.
    /// </summary>
    internal static readonly TimeSpan ReapSecondPass = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The bound on the time a stop holds its gate: <see cref="ConfirmTimeout"/> (the shared budget of the root kill, the natural ends and the first
    /// reap pass) plus <see cref="ReapSecondPass"/> (once). It does not depend on the number of tracked processes: 5.1 s.
    /// </summary>
    internal static readonly TimeSpan GateHoldBound = ConfirmTimeout + ReapSecondPass;

    /// <summary>How often a bounded wait re-checks whether the stop has settled, and samples the process tree.</summary>
    internal const int PollMs = 100;

    private readonly object _gate = new();
    private readonly List<Tracked> _tracked = [];
    private readonly HashSet<int> _trackedIds = [];
    private IDisposable? _registration;
    private bool _sealed;
    // The child was killed and its exit was seen (the stop succeeded for the child). Residual processes do not change it (Issue #827, round 14).
    private bool _rootKilled;
    private string? _refused;
    private readonly List<ResidualProcess> _residuals = [];
    private int _rootId;
    private DateTime? _rootStart;
    private volatile bool _settled;

    /// <summary>
    /// A tracked process: its handle (which keeps its identity), its id, and its start time at the moment it was claimed.
    /// </summary>
    private sealed record Tracked(Process Handle, int Pid, DateTime? Start, string Name, int ParentPid);

    /// <summary>
    /// Supplies the descendants instead of sampling the process tree (the test seam). Null samples the tree.
    /// Returns null where the platform cannot list them.
    /// </summary>
    internal Func<Process, IReadOnlyList<Process>?>? DescendantsProbe { get; init; }

    /// <summary>
    /// Observes each wait of the run (the test seam). It receives the record, the child, the wait's name ("exit" or "pump"), and what
    /// the wait reported; it returns what the run should use. Null observes nothing.
    /// </summary>
    internal Func<StopRecord, Process, string, bool, bool>? ObserveForTest { get; init; }

    /// <summary>Replaces the kill of the child (the test seam): a test injects the OS refusal here. Null kills the process tree.</summary>
    internal Action<Process>? KillForTest { get; init; }

    /// <summary>Replaces the kill of a tracked descendant (the test seam): a test injects the OS refusal for a real grandchild here. Null kills it.</summary>
    internal Action<Process>? DescendantKillForTest { get; init; }

    /// <summary>
    /// Reads the start time of a process (the test seam): every identity comparison of the stop reads through it. Null reads the real
    /// start time.
    /// </summary>
    internal Func<Process, DateTime?>? StartTimeForTest { get; init; }

    /// <summary>
    /// Why a tracked parent's identity could not be verified, or null. Set while sampling; a stop that finds it set is refused, because the
    /// children of that parent cannot be attributed.
    /// </summary>
    private string? _unattributable;

    private DateTime? StartOf(Process process) =>
        StartTimeForTest is { } read ? read(process) : ProcessTree.StartTimeOf(process);

    /// <summary>The OS process id of the child the run targets, or 0 before the child starts.</summary>
    internal int ProcessId { get; private set; }

    private long? _stoppedAt;

    /// <summary>
    /// Guards the stop instant only. It is separate from <see cref="_gate"/>, which a stop holds for the whole decision (including its waits),
    /// so a reader of the instant never waits for a stop in progress (Issue #827 review).
    /// </summary>
    private readonly object _stopMark = new();

    /// <summary>The Stopwatch timestamp at which a stop arrived, or null when none has.</summary>
    internal long? StoppedAt
    {
        get
        {
            lock (_stopMark)
            {
                return _stoppedAt;
            }
        }
    }

    /// <summary>True once a stop has finished its decision (confirmed, refused, or found the child already gone).</summary>
    public bool Settled => _settled;

    /// <summary>The processes the stop left behind (reaped or still running), read after <see cref="Seal"/>. Each is a residual, not a refusal.</summary>
    internal IReadOnlyList<ResidualProcess> Residuals
    {
        get
        {
            lock (_gate)
            {
                return _residuals.ToArray();
            }
        }
    }

    /// <summary>Records a process the stop left behind. A residual never changes whether the child's kill was confirmed.</summary>
    internal void AddResidual(ResidualProcess residual)
    {
        lock (_gate)
        {
            _residuals.Add(residual);
        }
    }

    /// <summary>Applies the observation seam to a wait's report (identity when no seam is set).</summary>
    internal bool Observe(Process process, string phase, bool ended) =>
        ObserveForTest is null ? ended : ObserveForTest(this, process, phase, ended);

    /// <summary>Kills the child's process tree, or the test seam's replacement.</summary>
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
    /// Registers the stop for the child and records its identity. The child is killed only when the stop token is cancelled. Dispose the
    /// returned record (see <see cref="Seal"/>) before the process is disposed.
    /// </summary>
    internal IDisposable Attach(Process process, CancellationToken stopToken)
    {
        ProcessId = process.Id;
        _rootId = process.Id;
        _rootStart = StartOf(process);
        _registration = stopToken.Register(() => Stop(process));
        return this;
    }

    /// <summary>
    /// Samples the child's process tree while it is alive (called by every wait of the run). Only the snapshot is sampled; a probe
    /// (the test seam) is consulted at the stop and at the end of the run.
    /// </summary>
    internal void SampleTree()
    {
        if (DescendantsProbe is not null)
        {
            return;
        }

        // A stop in progress holds the gate for its whole decision: the wait does not block on it. The next wait samples again, and
        // a decision that runs now does not need a sample (Issue #827 review).
        if (!System.Threading.Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (!_sealed)
            {
                ClaimFromSnapshot();
            }
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// Unregisters the stop, disposes the tracked handles, and waits for a stop in progress to finish. After this returns, nothing changes
    /// the outcome.
    /// </summary>
    internal void Seal()
    {
        // Disposing the registration waits for a callback that is already running, so the outcome is final after it.
        _registration?.Dispose();
        lock (_gate)
        {
            _sealed = true;
            foreach (var tracked in _tracked)
            {
                tracked.Handle.Dispose();
            }
        }
    }

    public void Dispose() => Seal();

    /// <summary>
    /// The outcome of the stop, read after <see cref="Seal"/>. Confirmed means the live child was killed and its exit was seen: that alone decides
    /// the Cancelled result. Uncertainties and residual processes never change it; they are diagnostics. Refused is set only when the child was
    /// not killed (the stop itself failed).
    /// </summary>
    internal (bool Confirmed, string? Refused) Outcome
    {
        get
        {
            lock (_gate)
            {
                return (_rootKilled, _refused);
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

    /// <summary>
    /// Records an uncertainty of the stop's bookkeeping (a tree that cannot be listed, an identity that cannot be verified). It is a residual in the
    /// diagnostics, with no process id: the process it concerns is unidentified. It does not change the result when the child was killed (Issue #827,
    /// round 15).
    /// </summary>
    internal void NoteUncertainty(string reason)
    {
        lock (_gate)
        {
            _residuals.Add(new ResidualProcess(null, ResidualState.Unverified, reason));
        }
    }

    /// <summary>
    /// A stop failure. Before the child's kill it is a refusal (the child may still be running). After the kill it is an uncertainty, because the
    /// child is already dead and the stop has succeeded for it.
    /// </summary>
    private void FailStop(string reason)
    {
        if (_rootKilled)
        {
            NoteUncertainty(reason);
        }
        else
        {
            RefuseIfUnset(reason);
        }
    }

    /// <summary>True once a stop has found the child already ended on its own (no kill was needed).</summary>
    internal bool ChildEndedBeforeStop
    {
        get
        {
            lock (_gate)
            {
                return _childEndedBeforeStop;
            }
        }
    }

    private bool _childEndedBeforeStop;

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
        // The instant the stop arrived is recorded first, outside the decision's lock: the drains and the frame decision read it while the
        // decision waits (Issue #827).
        lock (_stopMark)
        {
            _stoppedAt ??= Stopwatch.GetTimestamp();
        }

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
                // Nothing escapes the stop thread, and nothing is left undecided: an unexpected failure is a refusal (an uncertainty after the kill).
                FailStop($"the stop failed unexpectedly: {exception.Message}");
            }
            finally
            {
                _settled = true;
            }
        }
    }

    private void Decide(Process process)
    {
        var killed = false;
        var listed = false;
        try
        {
            var clock = Stopwatch.StartNew();

            // Claimed while the child is alive; a dead root's children are claimed only if already tracked.
            listed = ClaimForStop(process);

            // Checked again after the claim: the child may have ended while its tree was listed. Then it is a natural end, and what it
            // left running is a leftover, reaped and recorded (for a stop that found the child ended). It is never a confirmed stop.
            if (HasExitedOrGone(process))
            {
                EndedBeforeStop();
                return;
            }

            // Bookkeeping uncertainties (round 15): recorded as unidentified residuals. The child's kill still decides the result.
            if (!listed)
            {
                NoteUncertainty("the child's process tree cannot be listed on this platform, so its descendants are unidentified");
            }

            // Without the child's start time, its descendants cannot be attributed to it: an uncertainty, not a refusal of the kill.
            if (_rootStart is null)
            {
                NoteUncertainty("the start time of the child cannot be read, so its descendants cannot be attributed (unidentified)");
            }

            // A tracked parent whose identity could not be verified while sampling: its unclaimed children are unidentified.
            if (_unattributable is { } reason)
            {
                NoteUncertainty(reason);
            }

            try
            {
                KillTree(process);
            }
            catch (Exception exception)
            {
                // The OS refused the kill: a refusal with its reason. A child that ended while the kill was sent ended before the stop could
                // act: that is not a refusal, and what it left running is recorded as usual.
                if (!HasExitedOrGone(process))
                {
                    RefuseIfUnset($"the kill was refused: {exception.Message}");
                    return;
                }

                EndedBeforeStop();
                return;
            }

            killed = true;

            // A wait with no time left still checks once: a child that exited at the deadline is not reported as alive (round 16, LOW 1).
            if (!process.WaitForExit(Remaining(clock)) && !HasExitedOrGone(process))
            {
                RefuseIfUnset($"the child {ProcessId} was still running {ConfirmTimeout.TotalSeconds:0} s after the kill");
                return;
            }

            // The child is dead: the stop has succeeded for it (the result is Cancelled). What its tracked descendants leave running is a
            // residual, reported apart from the result (Issue #827, round 14). The tree kill ends the tracked processes it reaches, but their
            // exit may still be in progress: they get the rest of the bound to end. Only what still runs after that is reaped through its own
            // handle; a survivor that cannot be reaped is a residual too.
            _rootKilled = true;
            WaitForNaturalEnds(Remaining(clock));
            var (reaped, unresolved) = ReapTracked(Remaining(clock));
            foreach (var residual in reaped.Concat(unresolved))
            {
                _residuals.Add(residual);
            }
        }
        catch (Exception exception)
        {
            // A kill that failed only because the child had already exited, after its tree was listed, is a natural end. Every other
            // failure is a refusal before the kill, and an uncertainty after it (FailStop).
            if (listed && !killed && HasExitedOrGone(process))
            {
                EndedBeforeStop();
                return;
            }

            FailStop($"the stop could not be confirmed: {exception.Message}");
            Trace.WriteLine($"Stopping the artifact process {ProcessId} failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The child ended before the stop could act on it (Issue #827 review). The stop did nothing to the child, so it is not a stop: it does not
    /// refuse, and it does not confirm. What the child's tracked descendants left running is reaped and recorded as a diagnostic.
    /// </summary>
    private void EndedBeforeStop()
    {
        _childEndedBeforeStop = true;
        var (endedLeft, endedUnresolved) = ReapTracked(ConfirmTimeout);
        _residuals.AddRange(endedLeft.Concat(endedUnresolved));
    }

    /// <summary>
    /// Claims the children of the child for a stop. Returns false when the platform cannot list them (no probe and no snapshot).
    /// </summary>
    private bool ClaimForStop(Process process)
    {
        if (DescendantsProbe is { } probe)
        {
            var listed = probe(process);
            if (listed is null)
            {
                return false;
            }

            foreach (var child in listed)
            {
                int pid;
                try
                {
                    pid = child.Id;
                }
                catch (InvalidOperationException exception)
                {
                    // A handle that cannot be read is not known to be gone: an unidentified residual, the kill still decides the result.
                    NoteUncertainty($"a descendant process could not be checked, so it is unidentified: {exception.Message}");
                    continue;
                }

                if (_trackedIds.Add(pid))
                {
                    var childStart = StartOf(child);
                    if (childStart is null)
                    {
                        // Without a start time the claim cannot be verified: the child is not claimed, and it is unidentified (no pid is reported).
                        NoteUncertainty("the start time of a descendant process cannot be read, so it is unidentified and not claimed");
                        child.Dispose();
                        continue;
                    }

                    _tracked.Add(new Tracked(child, pid, childStart, NameOf(child), 0));
                }
                else
                {
                    child.Dispose();
                }
            }

            return true;
        }

        return ClaimFromSnapshot();
    }

    /// <summary>
    /// Claims every child of a tracked (or the root) process whose identity still holds, from one snapshot of the process tree. A child is
    /// claimed once; its start time must not be earlier than its parent's (a child starts after its parent).
    /// </summary>
    private bool ClaimFromSnapshot()
    {
        var parents = ProcessTreeInterop.TryReadParentIds();
        if (parents is null)
        {
            return false;
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

        var queue = new Queue<(int Pid, DateTime? Start)>();
        queue.Enqueue((_rootId, _rootStart));
        foreach (var tracked in _tracked)
        {
            queue.Enqueue((tracked.Pid, tracked.Start));
        }

        while (queue.Count > 0)
        {
            var (pid, start) = queue.Dequeue();
            if (!children.TryGetValue(pid, out var kids))
            {
                continue;
            }

            // The parent's identity is checked before any of its children is claimed. A failed check means its unclaimed children cannot be
            // attributed: they are not claimed, and the uncertainty is recorded (see Decide). A parent that ended with nothing new below it is no uncertainty.
            if (!IdentityHolds(pid, start))
            {
                if (kids.Any(kid => !_trackedIds.Contains(kid)))
                {
                    _unattributable ??= "the identity of a tracked process could not be verified, so its unclaimed children are unidentified";
                }

                continue;
            }

            foreach (var kid in kids)
            {
                if (_trackedIds.Contains(kid))
                {
                    continue;
                }

                Process handle;
                try
                {
                    handle = Process.GetProcessById(kid);
                }
                catch (ArgumentException)
                {
                    // Gone before its handle was opened: not a survivor.
                    continue;
                }

                var kidStart = StartOf(handle);
                if (kidStart is null)
                {
                    // Without a start time the child cannot be verified, as for the root: it is not claimed, and it is unidentified (no pid is reported).
                    _unattributable ??= "the start time of a descendant process cannot be read, so it is unidentified and not claimed";
                    handle.Dispose();
                    continue;
                }

                if (start is { } parentStart && kidStart is { } childStart && childStart < parentStart)
                {
                    // Started before its parent: the id was reused, so this is not the parent's child.
                    handle.Dispose();
                    continue;
                }

                if (IsConsoleHost(handle))
                {
                    // Windows starts the console host as a child of a console program, and its lifetime belongs to the console. It is not
                    // claimed here, so it is never reaped on its own. A stop's tree kill may still end it, because it is in the tree then.
                    handle.Dispose();
                    continue;
                }

                _trackedIds.Add(kid);
                _tracked.Add(new Tracked(handle, kid, kidStart, NameOf(handle), pid));
                queue.Enqueue((kid, kidStart));
            }
        }

        return true;
    }

    /// <summary>
    /// True only when the process id <paramref name="pid"/> is still the process that was seen with <paramref name="start"/>. A process id that
    /// no longer exists, or that was reused, cannot be verified: the check fails (Issue #827, round 15).
    /// </summary>
    private bool IdentityHolds(int pid, DateTime? start)
    {
        Process current;
        try
        {
            current = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return false;
        }

        using (current)
        {
            return start is { } expected && StartOf(current) == expected;
        }
    }

    /// <summary>
    /// Reaps every tracked process still running. Each one is killed through its own handle, only when its start time still matches the
    /// claim. Returns the processes reaped and the ones that could not be confirmed gone.
    /// </summary>
    private (List<ResidualProcess> Reaped, List<ResidualProcess> Unresolved) ReapTracked(TimeSpan wait)
    {
        var reaped = new List<ResidualProcess>();
        var unresolved = new List<ResidualProcess>();
        var clock = Stopwatch.StartNew();
        var kill = DescendantKillForTest ?? (process => process.Kill(true));

        // Pass 1 (round 19, MEDIUM 1): each tracked process is killed and waited for within the reap's budget. Once the budget is spent, the wait is
        // zero; a process that is only just dead is not reported alive on that account. The survivors of pass 1 go to pass 2.
        var pending = new List<(Tracked Tracked, string Who)>();
        foreach (var tracked in _tracked)
        {
            var who = $"descendant process {tracked.Pid} ({tracked.Name}, parent {tracked.ParentPid})";
            try
            {
                if (tracked.Handle.WaitForExit(TimeSpan.Zero))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The identity was verified at the claim, not now: the id is not reported (round 15).
                unresolved.Add(new ResidualProcess(null, ResidualState.Unverified, $"a tracked descendant (unidentified) could not be checked: {exception.Message}"));
                continue;
            }

            // A claim without a start time cannot be verified, so the process is not killed (the same rule as for the root).
            // Identity is the pair (pid, start time). Without a matching start time the pid may belong to another process: the id is not reported.
            if (tracked.Start is null)
            {
                unresolved.Add(new ResidualProcess(null, ResidualState.Unverified, "a tracked descendant still running; its start time is unknown, so it is unidentified; not killed"));
                continue;
            }

            if (StartOf(tracked.Handle) != tracked.Start)
            {
                unresolved.Add(new ResidualProcess(null, ResidualState.Unverified, "a tracked descendant's identity changed (pid reused?), so it is unidentified; not killed"));
                continue;
            }

            try
            {
                kill(tracked.Handle);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                unresolved.Add(new ResidualProcess(tracked.Pid, ResidualState.Alive, $"{who} still running; kill refused: {exception.Message}"));
                continue;
            }

            // Pass 1: the budget left (zero once it is spent). A process that exits within it counts as gone.
            var left = wait - clock.Elapsed;
            try
            {
                var slice = left > TimeSpan.Zero ? left : TimeSpan.Zero;
                if (tracked.Handle.WaitForExit(slice) || HasExitedOrGone(tracked.Handle))
                {
                    reaped.Add(new ResidualProcess(tracked.Pid, ResidualState.EndedAfterKill, $"{who} still running after its owner ended (killed)"));
                }
                else
                {
                    pending.Add((tracked, who));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                unresolved.Add(new ResidualProcess(null, ResidualState.Unverified, $"a tracked descendant (unidentified) could not be checked after its kill: {exception.Message}"));
            }
        }

        // Pass 2 (once, for the survivors of pass 1 only): one shared window of ReapSecondPass. Only a process still running after it is alive.
        var secondPass = Stopwatch.StartNew();
        foreach (var (tracked, who) in pending)
        {
            try
            {
                var remaining = ReapSecondPass - secondPass.Elapsed;
                var slice = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                if (tracked.Handle.WaitForExit(slice) || HasExitedOrGone(tracked.Handle))
                {
                    reaped.Add(new ResidualProcess(tracked.Pid, ResidualState.EndedAfterKill, $"{who} still running after its owner ended (killed)"));
                }
                else
                {
                    unresolved.Add(new ResidualProcess(tracked.Pid, ResidualState.Alive, $"{who} still running after its kill"));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                unresolved.Add(new ResidualProcess(null, ResidualState.Unverified, $"a tracked descendant (unidentified) could not be checked after its kill: {exception.Message}"));
            }
        }

        return (reaped, unresolved);
    }

    /// <summary>Waits, within one shared budget, for every tracked process to end on its own. Survivors are left for the reap.</summary>
    private void WaitForNaturalEnds(TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        foreach (var tracked in _tracked)
        {
            try
            {
                var left = budget - clock.Elapsed;
                tracked.Handle.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Checked again by the reap, which reports a handle it cannot read.
            }
        }
    }

    /// <summary>
    /// True for the Windows console host (conhost). It is not claimed as a descendant, so the run never reaps it on its own. Its exit is
    /// part of the console's lifetime. A tree kill of the child does end it when it is in the tree, as the kill does for any process there.
    /// </summary>
    private static bool IsConsoleHost(Process process) =>
        string.Equals(NameOf(process), "conhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>The executable name of a tracked process, for the record (a name only; never used to choose what to kill).</summary>
    private static string NameOf(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static TimeSpan Remaining(Stopwatch clock)
    {
        var left = ConfirmTimeout - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

/// <summary>The shutdown of a run, apart from its result (Issue #827, round 10).</summary>
internal enum ShutdownOutcome
{
    /// <summary>No stop was processed for the run (the natural end is not checked for residual children).</summary>
    NotStopped,

    /// <summary>A stop was processed and left no refusal and no residual process.</summary>
    Clean,

    /// <summary>A stop was processed and left a refusal or a residual process.</summary>
    Residual,
}

/// <summary>
/// What a stop left behind, reported apart from the run's result (Issue #827, decided in review round 10). The result of the run stands on its own;
/// this record says whether the child's process cleanup was confirmed. A refusal or a residual process is reported here with the process id and
/// the reason, and makes the shutdown not clean, even when the run's result is a completed one.
/// </summary>
/// <param name="ProcessId">The child's process id.</param>
/// <param name="ChildEndedBeforeStop">True when the child ended on its own before the stop reached it.</param>
/// <param name="Confirmed">True when a live child was killed and its exit was seen. The result is Cancelled on this alone.</param>
/// <param name="Refused">The reason the stop itself was refused (the kill, the listing, or an identity), or null.</param>
/// <param name="Residuals">The processes the stop left behind, each with its id, its state, and the reason. Empty when none.</param>
[Infrastructure]
internal sealed record StopDiagnostics(int ProcessId, bool ChildEndedBeforeStop, bool Confirmed, string? Refused, IReadOnlyList<ResidualProcess> Residuals)
{
    /// <summary>When these diagnostics were taken (UTC).</summary>
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>True when the stop was not refused and left no residual process.</summary>
    public bool Clean => Refused is null && Residuals.Count == 0;

    /// <summary>The residual reasons joined into one text, or null when there are none.</summary>
    public string? ResidualText => Residuals.Count == 0 ? null : string.Join("; ", Residuals.Select(residual => residual.Reason));
}

/// <summary>What a stop left running or unverified (Issue #827, round 14).</summary>
internal enum ResidualState
{
    /// <summary>The process was still running and the stop could not end it.</summary>
    Alive,

    /// <summary>The process was still running when the stop began; the stop killed it and saw it exit.</summary>
    EndedAfterKill,

    /// <summary>The process could not be checked, so its state is not known.</summary>
    Unverified,
}

/// <summary>
/// A process a stop left behind. The id is null when the holder is not a tracked process (for example an output pipe's holder that the stop never
/// claimed). The reason says what was seen.
/// </summary>
[Infrastructure]
internal sealed record ResidualProcess(int? ProcessId, ResidualState State, string Reason)
{
    /// <summary>When the residual was observed (UTC).</summary>
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Reads process start times for the stop bookkeeping (Issue #827). The parent links come from <see cref="ProcessTreeInterop"/>.
/// </summary>
[Infrastructure]
internal static class ProcessTree
{
    /// <summary>The start time of a process, or null when it cannot be read.</summary>
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
