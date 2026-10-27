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

    [Fact]
    public async Task StopDuringAFallbackSegment_EndsCancelled_AndTheFallbackStops()
    {
        var words = FallbackSpinProgram();
        using var dir = new TempDirectory();
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        // The segment budget is effectively unbounded, so only the stop can end the fallback segment in test time.
        var fallback = new MixedFallbackOptions(SegmentInstructionBudget: uint.MaxValue);
        using var engine = new RecompiledHostExecutionEngine(
            program,
            words,
            Entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer),
            mixedFallback: fallback);
        long fetches = 0;
        engine.FallbackFetchObserver = (_, _) => Interlocked.Increment(ref fetches);
        using var cts = new CancellationTokenSource();

        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, new ExitAtZeroHandoff(), Request(), cancellationToken: cts.Token));
        try
        {
            WaitUntil(() => Volatile.Read(ref fetches) >= FetchesBeforeStop, StopWait);
        }
        finally
        {
            // Even when the fallback never reaches the stop point, the spinning segment must not outlive the test.
            cts.Cancel();
        }

        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled, $"{result.DiagnosticCode} {result.DiagnosticMessage}");
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);

        // The run's result is not the proof: the host's fallback segment must have stopped too. The interpreter's fetch count
        // must not move once the run has returned, so a fallback still stepping in the background fails here.
        var atReturn = Volatile.Read(ref fetches);
        SpinFor(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref fetches).Should().Be(atReturn, "the fallback segment must stop with the run, not keep stepping the interpreter");
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
