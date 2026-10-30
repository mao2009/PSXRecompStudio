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
    /// ended, and the stop still reaches the run as "child ended before stop" (path 1 of the table). The child is observed alive when
    /// the stop starts, and in measurements it ends during the stop's own enumeration, before the kill; that ordering is a race inside
    /// the stop, not something this test controls. The test therefore fixes the observed outcome of that path, not a deterministic
    /// schedule. The Cancelled path (a live child killed and confirmed) cannot be reached from a fallback segment; it is checked by the
    /// unit table (GuestCancellationTests.ApplyStopOutcome_*).
    /// </summary>
    [Fact]
    public async Task StopDuringAFallbackSegment_MeasuredPath_ChildEndsBeforeKill_KeepsItsResult()
    {
        using var run = new StoppableFallback();
        var result = await run.StopAfterFetches();

        var diagnostics = run.Diagnostics;
        diagnostics.Should().NotBeNull("a stop was requested during the run");
        diagnostics!.ChildEndedBeforeStop.Should().BeTrue("the stop reached the fallback after the artifact child had ended (measured path)");
        diagnostics.Confirmed.Should().BeFalse("no kill was sent to a child that had already ended");
        result.State.Should().Be(TitleExecutionState.RuntimeFailure, $"{result.DiagnosticCode} {result.DiagnosticMessage}");
        result.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.StoppedOnRequest);
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
        private long _fetches;

        public StoppableFallback()
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
            var token = _cts.Token;
            _run = Task.Run(() => new ExecutionOrchestrator().Execute(_engine, new ExitAtZeroHandoff(), Request(), cancellationToken: token));
        }

        public long Fetches => Volatile.Read(ref _fetches);

        public StopDiagnostics? Diagnostics => _engine.LastStopDiagnostics;

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
                _cts.Cancel();
            }

            return await _run.WaitAsync(StopWait);
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
