using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
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
    /// Builds the spinning guest as a prebuilt artifact whose budget is unbounded. The engine's embedded build bakes its budget
    /// into the artifact, and a prebuilt run ignores the budget of a later request, so a budget set on the request would not
    /// reach the child: the guest would end on its own within milliseconds (Issue #820). The budget is therefore set here, at
    /// build time, with the same codegen calls the engine's embedded build makes.
    /// </summary>
    private static string BuildSpinningArtifact(TempDirectory dir, out HashSet<uint> blockEntryPcs)
    {
        var program = ReachableProgramBuilder.Build(Entry, SpinningGuest, Entry, []);
        blockEntryPcs = new HashSet<uint>(program.Blocks.Select(b => b.EntryPc));
        var dispatch = RecompilerHostCodeGen.Generate(program, loadedCode: null, traceUnits: false, guardStats: false);
        var image = SpinningGuest.SelectMany(BitConverter.GetBytes).ToArray();
        var artifact = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, new uint[TitleExecutionRequest.GprCount], 0, 0, Entry, uint.MaxValue, [], Entry, image, hostBridge: true);
        artifact.Success.Should().BeTrue(artifact.DiagnosticMessage);
        var build = new GeneratedHostBuildService().Build(new GeneratedHostBuildRequest(artifact.Source!, dir.FullPath, "recompiled-artifact"));
        build.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        return build.Artifact!.BinaryPath;
    }

    /// <summary>
    /// A private copy of the built artifact under a unique name. Every test builds the same artifact, so the process name of a
    /// shared binary would match other tests' children; a unique name makes the child this test launched the only match.
    /// </summary>
    private static int _copyCounter;

    private static string UniqueArtifactCopy(string binary, TempDirectory dir)
    {
        // Linux reports a process name truncated to 15 characters, so the name is kept short. The token is the start of this
        // test's random temp directory name, which keeps the copy unique across tests and parallel test hosts.
        var token = Path.GetFileNameWithoutExtension(dir.FullPath)[..6];
        var copy = Path.Combine(dir.FullPath, $"stop{Interlocked.Increment(ref _copyCounter)}-{token}{Path.GetExtension(binary)}");
#pragma warning disable AARC003 // Test-layer I/O for verification
        File.Copy(binary, copy);
#pragma warning restore AARC003
        return copy;
    }

    /// <summary>True while a process named like the artifact copy exists.</summary>
    private static bool ArtifactProcessRunning(string binaryPath) =>
        ProcessNameProbe.IsRunning(Path.GetFileNameWithoutExtension(binaryPath));

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
            // Bounded: a test that never receives the stop fails here instead of hanging. A stopped segment reports Cancelled,
            // as the real engine does when the stop reached its live child.
            _stop.Wait(StopWait).Should().BeTrue("the stop request must reach the running segment");
            return RecompilerExecutionResult.Failed(RecompilerExecutionStatus.Cancelled, "ARTIFACT_CANCELLED", "stopped");
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
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        ArtifactProcessRunning(binary).Should().BeFalse("nothing may be launched after a stop request");
    }

    [Fact]
    public async Task RealArtifact_StopDuringASpinningGuest_EndsCancelled_AndLeavesNoProcess()
    {
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();

        // The artifact's budget is unbounded (see BuildSpinningArtifact), so only the stop can end this run. The stop is sent only
        // once this test's own child is seen running. If the run ends before that, the run's own result is reported instead.
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
        try
        {
            WaitUntil(() => run.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
            run.IsCompleted.Should().BeFalse("the child must still be running when the stop is sent; the run ended before its child was seen");
        }
        finally
        {
            // Even when the wait fails, the spinning child must not outlive the test.
            cts.Cancel();
        }

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

    /// <summary>An engine whose one segment fails on its own, then records a stop request that arrives after the failure.</summary>
    private sealed class FailingThenStoppedEngine(CancellationTokenSource stop) : IRecompiledExecutionEngine
    {
        public string Name => "failing-test-engine";

        public void Load(TitleExecutionRequest request)
        {
        }

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
        {
            var failure = RecompilerExecutionResult.Failed(RecompilerExecutionStatus.ExecutionFailed, "ARTIFACT_FAILED", "crashed");
            // The stop arrives after the failure is already known: the failure keeps its own classification.
            stop.Cancel();
            return failure;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void FailureBeforeAStop_KeepsItsOwnClassification()
    {
        using var stop = new CancellationTokenSource();
        using var engine = new FailingThenStoppedEngine(stop);

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(0x80001000u, 64), cancellationToken: stop.Token);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure, "the crash happened before the stop, so it is not a cancellation");
        result.DiagnosticCode.Should().Be("ARTIFACT_FAILED");
    }

    [Fact]
    public async Task RealArtifact_RepeatedStopRequests_EndCancelledOnce_AndLeaveNoProcess()
    {
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
        try
        {
            WaitUntil(() => run.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
            run.IsCompleted.Should().BeFalse("the child must still be running when the stop requests are sent");

            // Stop requests race from four threads, both through the token and directly on the engine: each one must be harmless.
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                cts.Cancel();
                engine.RequestCancel();
            })));
        }
        finally
        {
            cts.Cancel();
        }

        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);
        ArtifactProcessRunning(binary).Should().BeFalse("the stopped artifact process must be gone when the run returns");
    }

    [Fact]
    public async Task RealArtifact_StopRacingANaturalEnd_NeverFailsAndLeavesNoProcess()
    {
        uint[] words = [.. Li(T0, 0x1234u), Nop];
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        var blockEntryPcs = new HashSet<uint>(program.Blocks.Select(b => b.EntryPc));
        using var dir = new TempDirectory();
        using (var builder = new RecompiledHostExecutionEngine(
            program, words, Entry, new GeneratedHostBuildService(), dir.FullPath, embedImage: true))
        {
            new ExecutionOrchestrator().Execute(builder, handoff: null, Request(Entry, 64))
                .FinalSnapshot.Should().NotBeNull("the embedded-image build itself must run");
        }

        var binary = UniqueArtifactCopy(BuiltBinary(dir), dir);
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            // The stop lands at a varied moment (a fixed schedule, so the test is repeatable): before the child starts, while it
            // runs, or after it has finished.
            var delay = TimeSpan.FromMilliseconds(attempt * 7 % 40);
            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(binary, blockEntryPcs: blockEntryPcs, imageWords: words, imageLoadAddress: Entry);
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                SpinFor(delay);
                cts.Cancel();
            });

            var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64), cancellationToken: cts.Token);
            await stopper;

            // Either the program finished first and keeps its result, or the stop won and the run is Cancelled. A failure is never acceptable.
            if (result.State == TitleExecutionState.Cancelled)
            {
                result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode, $"attempt {attempt}");
            }
            else
            {
                result.FinalSnapshot.Should().NotBeNull($"attempt {attempt}: {result.DiagnosticCode} {result.DiagnosticMessage}");
                result.FinalSnapshot!.Gpr[(int)T0].Should().Be(0x1234u, $"attempt {attempt}");
            }

            ProcessNameProbe.IsRunning(artifactName).Should().BeFalse($"attempt {attempt}: no artifact process may outlive its run");
        }
    }

    [Fact]
    public async Task SyntheticFrameRun_StopDuringFrameEmission_EndsCancelled_AndTouchesNoGpuAfterTheStop()
    {
        using var dir = new TempDirectory();
        var slot = new LiveFrameSlot();
        var words = SyntheticLiveProgram.Words();
        var entry = SyntheticLiveProgram.EntryPc;
        var program = ReachableProgramBuilder.Build(entry, words, entry, []);
        using var engine = new RecompiledHostExecutionEngine(
            program,
            words,
            entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardSink(), reader, writer),
            embedImage: true,
            liveFrames: slot);
        using var cts = new CancellationTokenSource();
        var request = new TitleExecutionRequest(entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: 20_000_000u);
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, request, cancellationToken: cts.Token));
        try
        {
            // The stop is sent only once the GPU has produced a frame, so it lands while the program is emitting frames.
            WaitUntil(() => run.IsCompleted || slot.TryGetNewer(0, out _), StopWait);
            run.IsCompleted.Should().BeFalse("a frame must be emitted before the stop is sent");
            cts.Cancel();
        }
        finally
        {
            cts.Cancel();
        }

        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);
        engine.LastFrameBgra32.Should().BeNull("a stopped run does not read the GPU/VRAM after the stop");
        slot.TryGetNewer(0, out var latest).Should().BeTrue();
        SpinFor(TimeSpan.FromMilliseconds(300));
        slot.TryGetNewer(latest!.Sequence, out _).Should().BeFalse("no frame is published after the run has returned");
    }

    /// <summary>Discards the guest's TTY bytes; the test only needs the Runtime to run.</summary>
    private sealed class DiscardSink : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }
}
