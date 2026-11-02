using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #829: a stop requested while the host runs an interpreter fallback segment must end that segment, not only the child
/// process. The fallback runs on the host's own interpreter, so a stop that reaches only the child leaves the fallback running.
/// Every wait here is bounded; a fallback that never stops fails the test instead of hanging it.
/// </summary>
[Test]
public sealed class GuestStopFallbackTests
{
    /// <summary>The longest any test waits for a run to end after a stop request.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    /// <summary>Fetches the fallback must reach before the stop is sent, so the stop lands inside a fallback segment.</summary>
    private const long FetchesBeforeStop = 10_000;

    /// <summary>The longest the stop's decision waits (in the test seam) for the child's exit. It is shorter than <see cref="StopWait"/>.</summary>
    private static readonly TimeSpan StopDecisionWait = TimeSpan.FromSeconds(10);

    /// <summary>The longest the test waits, after the run, for the child's process id to be gone (the OS may list an exited process a moment).</summary>
    private static readonly TimeSpan ProcessGoneWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The main routine jumps through a register to <see cref="Target"/>, which has no compiled block. The target spins on itself,
    /// so the interpreter fallback never returns to the artifact on its own: only a stop can end the segment.
    /// </summary>
    private static uint[] FallbackSpinProgram()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(0x1000FFFFu, Nop); // beq $zero,$zero,-1 with a delay-slot nop
        return Image(main, target);
    }

    /// <summary>
    /// Issue #828 (the stop's outcome table, ApplyStopOutcome), measured path only. In this fallback the child has already handed over and
    /// ended, and the stop still reaches the run as "child ended before stop" (path 1 of the table). The order is fixed by the test seam
    /// <see cref="RecompiledHostExecutionEngine.StopDescendantsProbe"/>: the stop's decision waits in that probe until the child has exited,
    /// so the child's end is observed before the stop is classified, and the stop never reaches the kill. The four assertions are the
    /// original ones; the sequence, the bounded wait, the residue and the pipe drain are checked in addition.
    /// </summary>
    [Fact]
    public async Task StopDuringAFallbackSegment_MeasuredPath_ChildEndsBeforeKill_KeepsItsResult()
    {
        using var run = new StoppableFallback(holdStopUntilChildEnds: true);
        var result = await run.StopAfterFetches();

        var diagnostics = run.Diagnostics;
        diagnostics.Should().NotBeNull("a stop was requested during the run");
        diagnostics!.ChildEndedBeforeStop.Should().BeTrue("the stop reached the fallback after the artifact child had ended (measured path)");
        diagnostics.Confirmed.Should().BeFalse("no kill was sent to a child that had already ended");
        result.State.Should().Be(TitleExecutionState.RuntimeFailure, $"{result.DiagnosticCode} {result.DiagnosticMessage}");
        result.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.StoppedOnRequest);

        // (a) order: the stop request, then the probe's entry (the stop's decision), then the child's exit observed by the probe, then the
        // probe's return (the stop is classified after it), then the run's result. The pump's end is observed before the result is read.
        run.ProbeTimedOut.Should().BeFalse("the child's exit must be observed within the probe's bound after the stop request");
        run.Events.Should().ContainInOrder("stop-requested", "probe-entered", "child-exit-observed", "probe-returned", "result-read");
        run.Events.Should().ContainInOrder("pump-ended", "result-read");

        // Refusals and residues: none, and the child's process id is gone after the run (checked by id, after a bounded poll).
        diagnostics.Refused.Should().BeNull("the stop was classified, not refused");
        diagnostics.Residuals.Should().BeEmpty("the child ended on its own and left no tracked process behind");
        var childId = diagnostics.ProcessId;
        childId.Should().BeGreaterThan(0, "the stop was attached to the artifact process");
        WaitForProcessGone(childId, ProcessGoneWait).Should().BeTrue($"child process {childId} must not outlive its run");
    }

    /// <summary>
    /// The run's result is not the proof: the host's fallback segment must stop with the run. Independent of the stop's outcome path, so
    /// a failure here is not masked by a classification failure. The interpreter's fetch count must not move once the run has returned.
    /// </summary>
    [Fact]
    public async Task StopDuringAFallbackSegment_FetchesDoNotGrowAfterTheRunReturns()
    {
        using var run = new StoppableFallback();
        await run.StopAfterFetches();

        var atReturn = run.Fetches;
        SpinFor(TimeSpan.FromMilliseconds(300));
        run.Fetches.Should().Be(atReturn, "the fallback segment must stop with the run, not keep stepping the interpreter");
    }

    /// <summary>
    /// One engine running the spinning fallback, with the fetch count and the stop. Owns its engine, directory and token source, and
    /// disposes them in the order the engine needs (engine first).
    /// </summary>
    private sealed class StoppableFallback : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly RecompiledHostExecutionEngine _engine;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task<TitleExecutionResult> _run;
        private readonly object _eventGate = new();
        private readonly List<string> _events = [];
        private long _fetches;
        private volatile bool _probeTimedOut;
        private bool _pumpEndRecorded;

        /// <summary>
        /// Starts the run. With <paramref name="holdStopUntilChildEnds"/>, the stop's decision waits (bounded by <see cref="StopDecisionWait"/>)
        /// for the child's exit in the test seam, and the event log records the order of the decision and the child's end.
        /// </summary>
        public StoppableFallback(bool holdStopUntilChildEnds = false)
        {
            var words = FallbackSpinProgram();
            var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
            // The segment budget is effectively unbounded, so only the stop can end the fallback segment in test time.
            var fallback = new MixedFallbackOptions(SegmentInstructionBudget: uint.MaxValue);
            _engine = new RecompiledHostExecutionEngine(
                program,
                words,
                Entry,
                new GeneratedHostBuildService(),
                _dir.FullPath,
                (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer),
                mixedFallback: fallback);
            _engine.FallbackFetchObserver = (_, _) => Interlocked.Increment(ref _fetches);
            if (holdStopUntilChildEnds)
            {
                // Test seam (the existing StopDescendantsProbe, read once per stop decision before the stop's own exit check). The probe
                // returns an empty listing only after the child's exit was observed, so the stop classifies the child as ended before the
                // stop and never kills it. The wait is bounded: a child that does not end fails the test (the probe throws, the stop refuses,
                // and the flag is set). The listing is empty: the fallback child has no tracked descendants.
                _engine.StopDescendantsProbe = child =>
                {
                    Record("probe-entered");
                    var clock = Stopwatch.StartNew();
                    var spin = new SpinWait();
                    while (!StopRecord.HasExitedOrGone(child))
                    {
                        if (clock.Elapsed > StopDecisionWait)
                        {
                            _probeTimedOut = true;
                            throw new TimeoutException("The child did not exit within the stop decision's bound.");
                        }

                        spin.SpinOnce();
                    }

                    Record("child-exit-observed");
                    Record("probe-returned");
                    return [];
                };
                // Observes the pump's end only (the same wait, returned unchanged): the run's pipe drain is finished before the result is read.
                _engine.WaitObservedForTest = (_, _, phase, ended) =>
                {
                    if (phase == "pump" && ended && !_pumpEndRecorded)
                    {
                        _pumpEndRecorded = true;
                        Record("pump-ended");
                    }

                    return ended;
                };
            }

            var token = _cts.Token;
            _run = Task.Run(() => new ExecutionOrchestrator().Execute(_engine, new ExitAtZeroHandoff(), Request(), cancellationToken: token));
        }

        public long Fetches => Volatile.Read(ref _fetches);

        public StopDiagnostics? Diagnostics => _engine.LastStopDiagnostics;

        /// <summary>True when the stop's decision gave up waiting for the child's exit (the bound), which fails the test.</summary>
        public bool ProbeTimedOut => _probeTimedOut;

        /// <summary>The ordered events of the run (the stop's request, the decision and the child's end, the pump, the result).</summary>
        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_eventGate)
                {
                    return _events.ToArray();
                }
            }
        }

        private void Record(string name)
        {
            lock (_eventGate)
            {
                _events.Add(name);
            }
        }

        /// <summary>Waits for the fallback to reach the fetch point, sends the stop, and returns the run's result (bounded).</summary>
        public async Task<TitleExecutionResult> StopAfterFetches()
        {
            try
            {
                WaitUntil(() => Fetches >= FetchesBeforeStop, StopWait);
            }
            finally
            {
                // Even when the fallback never reaches the stop point, the spinning segment must not outlive the test.
                Record("stop-requested");
                _cts.Cancel();
            }

            var result = await _run.WaitAsync(StopWait);
            Record("result-read");
            return result;
        }

        public void Dispose()
        {
            _engine.Dispose();
            _cts.Dispose();
            _dir.Dispose();
        }
    }

    /// <summary>Busy-waits for <paramref name="duration"/> (a bounded, deterministic pause for the test layer).</summary>
    private static void SpinFor(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (clock.Elapsed < duration)
        {
            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Waits, with a bound, until no live process has <paramref name="pid"/> (or it has exited). Returns false when it still runs at the bound.
    /// Checked by id, after the run: the test holds no handle to the child.
    /// </summary>
#pragma warning disable AARC003 // Test-layer process observation for verification: the child's id is checked after its run (no residue).
    private static bool WaitForProcessGone(int pid, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // No process has this id.
                return true;
            }

            if (clock.Elapsed > bound)
            {
                return false;
            }

            spin.SpinOnce();
        }
    }
#pragma warning restore AARC003

    /// <summary>Waits, with a bound, for a condition to hold; a missed condition fails the test.</summary>
    private static void WaitUntil(Func<bool> condition, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (!condition())
        {
            if (clock.Elapsed > bound)
            {
                throw new TimeoutException("The condition did not hold within the test's bound.");
            }

            spin.SpinOnce();
        }
    }
}
