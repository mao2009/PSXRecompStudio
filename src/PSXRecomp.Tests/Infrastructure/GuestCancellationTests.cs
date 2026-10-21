using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #820: a guest run can be stopped by a cancellation request. A stop is classified as <c>Cancelled</c>, never as a
/// success or a runtime failure, and it leaves no artifact process behind. Every test here has a bounded wait, so a
/// stop that never returns fails the test instead of hanging it.
/// </summary>
[Test]
public sealed class GuestCancellationTests
{
    /// <summary>The longest any test waits for a run to end after a stop request.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(60);

    /// <summary>A self-branch (<c>beq $zero,$zero,-1</c>) with a delay-slot nop: the guest never leaves this block.</summary>
    private static readonly uint[] SpinningGuest = [0x1000FFFFu, MipsEncoding.Nop];

    private static TitleExecutionRequest Request(uint entry, uint budget) =>
        new(entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: budget);

    private static string BuiltBinary(TempDirectory dir)
    {
#pragma warning disable AARC003 // Test-layer I/O for verification
        return Directory.GetFiles(dir.FullPath, "recompiled-artifact*").Single(f => !f.EndsWith(".o") && !f.EndsWith(".c"));
#pragma warning restore AARC003
    }

    /// <summary>
    /// True while a process named like the artifact exists. The check is by name only: each test uses its own temp
    /// directory, and the artifact's file name is the only handle the test has on the child.
    /// </summary>
    private static bool ArtifactProcessRunning(string binaryPath) =>
        ProcessNameProbe.IsRunning(Path.GetFileNameWithoutExtension(binaryPath));

    /// <summary>Waits, with a bound, for <paramref name="condition"/> to hold; a missed condition fails the test.</summary>
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

    /// <summary>An engine that blocks in <see cref="RunSegment"/> until it is asked to stop, then reports the stop.</summary>
    private sealed class BlockingEngine : IRecompiledExecutionEngine, ICancellableExecutionEngine
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LoadCalls { get; private set; }
        public int SegmentCalls { get; private set; }
        public int RequestCancelCalls { get; private set; }

        /// <summary>Completes when the segment has begun, so a test can stop the run mid-segment without timing.</summary>
        public Task Started => _started.Task;

        public string Name => "blocking-test-engine";

        public void Load(TitleExecutionRequest request) => LoadCalls++;

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
        {
            SegmentCalls++;
            _started.TrySetResult();
            // Bounded: a test that never receives the stop fails here instead of hanging.
            _stop.Wait(StopWait).Should().BeTrue("the stop request must reach the running segment");
            return RecompilerExecutionResult.Failed(RecompilerExecutionStatus.ExecutionFailed, "STOPPED_BY_TEST", "stopped");
        }

        public void RequestCancel()
        {
            RequestCancelCalls++;
            _stop.Set();
        }

        public void Dispose() => _stop.Dispose();
    }

    [Fact]
    public void StopRequestedBeforeTheRun_EndsCancelled_WithoutLoadingOrRunningAnything()
    {
        using var engine = new BlockingEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(0x80001000u, 64), cancellationToken: cts.Token);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);
        engine.LoadCalls.Should().Be(0, "a run stopped before it starts must not load the engine");
        engine.SegmentCalls.Should().Be(0, "a run stopped before it starts must not run a segment");
    }

    [Fact]
    public async Task StopRequestedDuringASegment_IsDeliveredToTheEngine_AndEndsCancelled()
    {
        using var engine = new BlockingEngine();
        using var cts = new CancellationTokenSource();

        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(0x80001000u, 64), cancellationToken: cts.Token));
        await engine.Started.WaitAsync(StopWait);
        cts.Cancel();

        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled, "the stop is a Cancelled outcome, not a RuntimeFailure");
        engine.RequestCancelCalls.Should().Be(1, "the stop reaches the engine exactly once");
    }

    [Fact]
    public void RepeatedStopRequests_AreIdempotent()
    {
        using var engine = new BlockingEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        cts.Cancel();

        var act = () => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(0x80001000u, 64), cancellationToken: cts.Token);

        act.Should().NotThrow();
        engine.RequestCancel();
        engine.RequestCancel();
    }

    [Fact]
    public void RealArtifact_StopRequestedBeforeTheRun_LaunchesNoProcess()
    {
        var program = ReachableProgramBuilder.Build(Entry, SpinningGuest, Entry, []);
        using var dir = new TempDirectory();
        using (var builder = new RecompiledHostExecutionEngine(
            program, SpinningGuest, Entry, new GeneratedHostBuildService(), dir.FullPath, embedImage: true))
        {
            new ExecutionOrchestrator().Execute(builder, handoff: null, Request(Entry, 64))
                .FinalSnapshot.Should().NotBeNull("the embedded-image build itself must run");
        }

        var binary = BuiltBinary(dir);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary,
            blockEntryPcs: new HashSet<uint>(program.Blocks.Select(b => b.EntryPc)),
            imageWords: SpinningGuest,
            imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        ArtifactProcessRunning(binary).Should().BeFalse("nothing may be launched after a stop request");
    }

    [Fact]
    public async Task RealArtifact_StopDuringASpinningGuest_EndsCancelled_AndLeavesNoProcess()
    {
        var program = ReachableProgramBuilder.Build(Entry, SpinningGuest, Entry, []);
        using var dir = new TempDirectory();
        using (var builder = new RecompiledHostExecutionEngine(
            program, SpinningGuest, Entry, new GeneratedHostBuildService(), dir.FullPath, embedImage: true))
        {
            new ExecutionOrchestrator().Execute(builder, handoff: null, Request(Entry, 64))
                .FinalSnapshot.Should().NotBeNull("the embedded-image build itself must run");
        }

        var binary = BuiltBinary(dir);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary,
            blockEntryPcs: new HashSet<uint>(program.Blocks.Select(b => b.EntryPc)),
            imageWords: SpinningGuest,
            imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();

        // The budget is effectively unbounded, so only the stop can end this run. The stop is sent only once the child is
        // seen running, so this test exercises the kill of a live child rather than the pre-launch check.
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
        WaitUntil(() => ProcessNameProbe.IsRunning(artifactName), StopWait);
        cts.Cancel();

        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);
        ArtifactProcessRunning(binary).Should().BeFalse("the stopped artifact process must be gone when the run returns");
    }

    [Fact]
    public void RealArtifact_StopAfterACompletedRun_KeepsTheCompletedResult()
    {
        uint[] words = [.. Li(T0, 0x1234u), Nop];
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        using var dir = new TempDirectory();
        using (var builder = new RecompiledHostExecutionEngine(
            program, words, Entry, new GeneratedHostBuildService(), dir.FullPath, embedImage: true))
        {
            new ExecutionOrchestrator().Execute(builder, handoff: null, Request(Entry, 64))
                .FinalSnapshot.Should().NotBeNull("the embedded-image build itself must run");
        }

        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            BuiltBinary(dir),
            blockEntryPcs: new HashSet<uint>(program.Blocks.Select(b => b.EntryPc)),
            imageWords: words,
            imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();
        var completed = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64), cancellationToken: cts.Token);

        cts.Cancel();
        engine.RequestCancel();

        completed.FinalSnapshot.Should().NotBeNull($"{completed.DiagnosticCode} {completed.DiagnosticMessage}");
        completed.FinalSnapshot!.Gpr[(int)T0].Should().Be(0x1234u);
        completed.State.Should().NotBe(TitleExecutionState.Cancelled, "a run that finished before the stop keeps its result");
    }
}
