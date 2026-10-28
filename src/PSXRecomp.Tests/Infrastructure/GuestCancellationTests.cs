using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Core;
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

        public IDisposable AttachCancellation(CancellationToken cancellationToken) => new NoAttachment();

        private sealed class NoAttachment : IDisposable
        {
            public void Dispose()
            {
            }
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

        result.State.Should().Be(TitleExecutionState.Cancelled, $"{result.DiagnosticCode} {result.DiagnosticMessage}");
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
        // The program ends at the runtime boundary (its own termination): that is its state, and the stop that came later does not change it.
        completed.State.Should().Be(TitleExecutionState.RuntimeHandoff, "the program ended on its own before the stop: its own state stands");
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.NotStopped, "no stop was processed; the natural end does not check residual children (Issue #829)");
    }

    /// <summary>
    /// A shell "artifact" for the real engine path (Linux): it prints a valid snapshot block, starts a grandchild that keeps its stdout open (the
    /// grandchild is not in the tree once its parent ends), and then runs <paramref name="tail"/>.
    /// </summary>
    private static string WriteStdoutHoldingArtifact(TempDirectory dir, string tail)
    {
        var path = Path.Combine(dir.FullPath, "holder.sh");
        // The snapshot's pc is the program's entry block (a pc with no block would be an unresolved transfer, not the child's result).
        var script = "#!/bin/sh\n"
            + $"printf 'RSNAPSHOT_BEGIN\\ntermination=0\\npc=0x{Entry:x8}\\nhi=0x0\\nlo=0x0\\n' >&2\n"
            + "i=0; while [ $i -lt 32 ]; do printf 'gpr[%d]=0x0\\n' $i >&2; i=$((i+1)); done\n"
            + "printf 'RSNAPSHOT_END\\n' >&2\n"
            + "sh -c 'sleep 60 2>/dev/null &'\n"
            + tail + "\n";
        File.WriteAllText(path, script);
#pragma warning disable AARC003 // Test-layer fixture: the shell artifact must be executable to be launched as the prebuilt child
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore AARC003
        return path;
    }

    private static RecompiledHostExecutionEngine HoldingEngine(string binary)
    {
        uint[] words = [.. Li(T0, 0x1234u), Nop];
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary,
            blockEntryPcs: new HashSet<uint>(program.Blocks.Select(b => b.EntryPc)),
            imageWords: words,
            imageLoadAddress: Entry,
            runTimeout: TimeSpan.FromSeconds(60));
        engine.OutputBudgetMsForTest = 1_000;
        return engine;
    }

    [SkippableFact]
    public void RealEngine_BridgeEnabled_ChildAliveAtItsBudget_WithAStalledPump_IsOutputDrainTimeout()
    {
        // Owner spec B (round 17, MEDIUM 2): the same rule as the null-bridge path. The host-transfer bridge is enabled, so its pump reads the
        // child's stdout. The child is still running at its budget and is killed, and a grandchild keeps stdout open: the pump does not end, so
        // the result is ARTIFACT_OUTPUT_DRAIN_TIMEOUT, and the holder is a residual (unidentified).
        Skip.If(OperatingSystem.IsWindows(), "a POSIX shell artifact whose grandchild holds stdout runs on Linux and macOS; the Windows residual path is covered by the child tests");
        using var dir = new TempDirectory();
        var binary = WriteStdoutHoldingArtifact(dir, "sleep 30");
        uint[] words = [.. Li(T0, 0x1234u), Nop];
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary,
            blockEntryPcs: new HashSet<uint>(program.Blocks.Select(b => b.EntryPc)),
            imageWords: words,
            imageLoadAddress: Entry,
            biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardSink(), reader, writer),
            runTimeout: TimeSpan.FromSeconds(2));
        engine.OutputBudgetMsForTest = 1_000;

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64));

        result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.OutputDrainTimeoutDiagnosticCode, "the guest did not finish, and its output could not be drained");
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual, "the holder of the open pipe is a residual");
        engine.LastStopDiagnostics!.Residuals.Should().Contain(r => r.ProcessId == null && r.Reason.Contains("holder is unidentified"));
    }

    // Round 19, MEDIUM 2: the shutdown diagnostics of a run, derived from the stop's facts by one function. These unit tests state the facts and the
    // expected shutdown directly, on every platform (the same function serves the engine's run and the Windows harness).

    [Fact]
    public void ShutdownDiagnostics_NaturalEndWithAHolder_IsResidual_WithTheHolderUnidentified()
    {
        // Owner spec: the child ended on its own; a grandchild holds the stdout. No stop was requested. The holder is a residual of the cleanup,
        // unidentified, so the shutdown is Residual and never NotStopped.
        var evidence = new RecompiledHostExecutionEngine.ShutdownEvidence(
            ProcessId: 4242,
            StopRecorded: false,
            StopRequested: false,
            ChildEndedBeforeStop: true,
            KillConfirmed: false,
            Refused: null,
            Residuals: [new ResidualProcess(null, ResidualState.Unverified, "an output pipe is still held; the holder is unidentified (pid not known)")]);

        var diagnostics = RecompiledHostExecutionEngine.ShutdownDiagnostics(evidence);

        diagnostics.Should().NotBeNull("a residual is reported");
        diagnostics!.Clean.Should().BeFalse();
        diagnostics.Confirmed.Should().BeFalse("no stop was requested");
        diagnostics.Refused.Should().BeNull();
        diagnostics.Residuals.Should().ContainSingle().Which.ProcessId.Should().BeNull("the holder's pid is not reported");
    }

    [Fact]
    public void ShutdownDiagnostics_StopSucceededWithAHolder_IsResidual_AndTheKillIsConfirmed()
    {
        // Owner spec: the live child was killed and its exit seen (the stop succeeded, the result is Cancelled elsewhere). The stdout drain that a
        // holder kept open was cut: that is a residual reason, so the shutdown is Residual, while the kill is confirmed and nothing is refused.
        var evidence = new RecompiledHostExecutionEngine.ShutdownEvidence(
            ProcessId: 4242,
            StopRecorded: true,
            StopRequested: true,
            ChildEndedBeforeStop: false,
            KillConfirmed: true,
            Refused: null,
            Residuals: [new ResidualProcess(null, ResidualState.Unverified, "the stdout drain timed out after the stop; the holder is unidentified")]);

        var diagnostics = RecompiledHostExecutionEngine.ShutdownDiagnostics(evidence);

        diagnostics!.Confirmed.Should().BeTrue("the live child was killed and its exit was seen");
        diagnostics.Refused.Should().BeNull("the stop itself was not refused");
        diagnostics.Clean.Should().BeFalse("a holder left behind is a residual");
        diagnostics.ProcessId.Should().Be(4242);
    }

    [Fact]
    public void ShutdownDiagnostics_StopSucceededWithNothingLeft_IsClean()
    {
        // Owner spec: a stop that killed the live child, with nothing left behind, is a clean shutdown.
        var evidence = new RecompiledHostExecutionEngine.ShutdownEvidence(
            ProcessId: 4242,
            StopRecorded: true,
            StopRequested: true,
            ChildEndedBeforeStop: false,
            KillConfirmed: true,
            Refused: null,
            Residuals: []);

        var diagnostics = RecompiledHostExecutionEngine.ShutdownDiagnostics(evidence);

        diagnostics!.Clean.Should().BeTrue("the stop succeeded and left nothing");
        diagnostics.Confirmed.Should().BeTrue();
    }

    [Fact]
    public void ShutdownDiagnostics_RefusedStop_IsResidual_WithTheRefusalRecorded()
    {
        // Owner spec: the kill was refused, so the child is still running. The refusal is a residual of the shutdown, with its reason; the kill is
        // not confirmed. The result is the child's timeout (decided elsewhere), never a stop result.
        var evidence = new RecompiledHostExecutionEngine.ShutdownEvidence(
            ProcessId: 4242,
            StopRecorded: true,
            StopRequested: true,
            ChildEndedBeforeStop: false,
            KillConfirmed: false,
            Refused: "the kill was refused: access is denied",
            Residuals: []);

        var diagnostics = RecompiledHostExecutionEngine.ShutdownDiagnostics(evidence);

        diagnostics!.Clean.Should().BeFalse("a refused stop is not a clean shutdown");
        diagnostics.Confirmed.Should().BeFalse("the child was not killed");
        diagnostics.Refused.Should().Contain("kill was refused");
        diagnostics.ProcessId.Should().Be(4242, "the diagnostics name the child that was refused");
    }

    [Fact]
    public void ShutdownDiagnostics_NoStopAndNoResidual_IsNull_NotStopped()
    {
        // Owner spec: no stop was involved and nothing was left behind: there are no diagnostics, and the shutdown is NotStopped.
        var evidence = new RecompiledHostExecutionEngine.ShutdownEvidence(
            ProcessId: 4242,
            StopRecorded: false,
            StopRequested: false,
            ChildEndedBeforeStop: false,
            KillConfirmed: false,
            Refused: null,
            Residuals: []);

        RecompiledHostExecutionEngine.ShutdownDiagnostics(evidence).Should().BeNull("no stop and no residual: NotStopped");
    }

    /// <summary>Ends the run at the handoff: the guest exited on its own, so the run is Completed.</summary>
    private sealed class ExitHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            new(TitleExecutionHandoffAction.Exit);
    }

    [SkippableFact]
    public void RealEngine_NaturalEnd_WithAGrandchildHoldingStdout_ShutdownIsResidual_AndTheChildsResultStands()
    {
        // Owner spec (round 17), written from the spec: the child ended on its own and printed a valid snapshot, so its result (Completed) stands.
        // A grandchild keeps stdout open past the drain budget: a residual of the cleanup, with an unidentified holder. No stop was requested, so
        // the shutdown is not NotStopped. This goes through the real engine's RunSegment, not a test copy of it.
        Skip.If(OperatingSystem.IsWindows(), "a POSIX shell artifact whose grandchild holds stdout runs on Linux and macOS; the Windows residual path is covered by the child tests");
        using var dir = new TempDirectory();
        using var engine = HoldingEngine(WriteStdoutHoldingArtifact(dir, "exit 0"));

        // A snapshot whose termination is Success is a transfer the engine has no code for, so the handoff decides; this one exits.
        var result = new ExecutionOrchestrator().Execute(engine, handoff: new ExitHandoff(), Request(Entry, 64));

        result.FinalSnapshot.Should().NotBeNull("the child printed its snapshot and ended on its own");
        result.DiagnosticCode.Should().BeNull("the stalled stdout is a cleanup residual, not the guest's result");
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual, "the held pipe is a residual, so the shutdown is not clean or NotStopped");
        engine.LastStopDiagnostics!.Confirmed.Should().BeFalse("no stop was requested");
        engine.LastStopDiagnostics.Refused.Should().BeNull("no stop was refused");
        var residual = engine.LastStopDiagnostics.Residuals.Should().ContainSingle().Subject;
        residual.ProcessId.Should().BeNull("the holder is not identified: no pid is reported");
        residual.Reason.Should().Contain("holder is unidentified");
    }

    [SkippableFact]
    public async Task RealEngine_StopSucceeded_WithAGrandchildHoldingStdout_IsCancelled_AndShutdownIsResidual()
    {
        // Owner spec (round 17): a stop that killed the live child and saw its exit has succeeded, so the result is Cancelled, whatever the
        // cleanup left. The grandchild that holds stdout is a residual with an unidentified holder, so the shutdown is Residual.
        Skip.If(OperatingSystem.IsWindows(), "a POSIX shell artifact whose grandchild holds stdout runs on Linux and macOS; the Windows residual path is covered by the child tests");
        using var dir = new TempDirectory();
        using var engine = HoldingEngine(WriteStdoutHoldingArtifact(dir, "sleep 30"));
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64), cancellationToken: cts.Token));

        // The root is alive (it sleeps) and its grandchild holds stdout by then; a second is enough for both.
        SpinFor(TimeSpan.FromSeconds(1));
        run.IsCompleted.Should().BeFalse("the artifact must still be running when the stop is sent");
        cts.Cancel();
        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled, "the live artifact was killed and its exit was seen");
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual, "the stdout holder is a residual, so the shutdown is not clean");
        engine.LastStopDiagnostics!.Confirmed.Should().BeTrue("the kill of the live child was confirmed");
        engine.LastStopDiagnostics.Refused.Should().BeNull("the stop itself was not refused");
        engine.LastStopDiagnostics.Residuals.Should().Contain(r => r.ProcessId == null && r.Reason.Contains("holder is unidentified"));
    }

    [Fact]
    public async Task RealArtifact_StopSucceeded_WithNoResidual_ShutdownIsClean()
    {
        // Owner spec (round 17): a stop that killed the live artifact, with nothing left behind, is a clean shutdown on every platform.
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
        WaitUntil(() => run.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
        run.IsCompleted.Should().BeFalse("the artifact must still be running when the stop is sent");
        cts.Cancel();
        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.Clean, "a stop that left nothing behind is clean");
        engine.LastStopDiagnostics!.Residuals.Should().BeEmpty();
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

            // Attempt 0 is deterministic (Issue #827 review). The child really runs and ends normally, and the stop settles in the window
            // where the run's first wait still reports "not ended". The run must end Completed, never as a failure. Attempt 1 stops before
            // the start: that must be Cancelled, with nothing launched. The other attempts race the stop with the program's own end.
            var stopBeforeStart = attempt == 1;
            var endsNormallyUnderStop = attempt == 0;
            if (endsNormallyUnderStop)
            {
                var observed = false;
                engine.WaitObservedForTest = (record, child, phase, ended) =>
                {
                    if (observed)
                    {
                        return ended;
                    }

                    observed = true;
#pragma warning disable AARC003 // Test-layer observation of the child's end, to place the stop exactly (Issue #827 review)
                    WaitUntil(() => child.HasExited, StopWait);
#pragma warning restore AARC003
                    cts.Cancel();
                    WaitUntil(() => record.Settled, StopWait);
                    return false;
                };
            }

            if (stopBeforeStart)
            {
                cts.Cancel();
            }

            var stopper = stopBeforeStart || endsNormallyUnderStop ? Task.CompletedTask : Task.Run(() =>
            {
                SpinFor(delay);
                cts.Cancel();
            });

            var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64), cancellationToken: cts.Token);
            await stopper;

            // The other attempts race the stop with the program's own end: either the program finished first and keeps its result,
            // or the stop won and the run is Cancelled. A failure is never acceptable.
            if (stopBeforeStart)
            {
                result.State.Should().Be(TitleExecutionState.Cancelled, "a stop before the start ends the run as Cancelled");
            }
            else if (endsNormallyUnderStop)
            {
                result.FinalSnapshot.Should().NotBeNull($"attempt 0: a child that ended normally keeps its result: {result.DiagnosticCode} {result.DiagnosticMessage}");
                result.FinalSnapshot!.Gpr[(int)T0].Should().Be(0x1234u, "attempt 0");
            }
            else if (result.State == TitleExecutionState.Cancelled)
            {
                result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode, $"attempt {attempt}");
            }
            else
            {
                result.FinalSnapshot.Should().NotBeNull($"attempt {attempt}: {result.DiagnosticCode} {result.DiagnosticMessage}");
                result.FinalSnapshot!.Gpr[(int)T0].Should().Be(0x1234u, $"attempt {attempt}");
            }

            // Bounded poll (round 17): the OS may list an exited process a moment longer. A survivor after five seconds is named with its id.
            var survivors = SurvivingProcesses(artifactName);
            survivors.Should().BeEmpty($"attempt {attempt}: no artifact process may outlive its run (still running: {string.Join(", ", survivors)})");
        }
    }

    /// <summary>
    /// The processes still running under an artifact's name, after a bounded poll (up to five seconds, every 100 ms). Each survivor is
    /// reported with its process id and its name; the list is empty when none is left.
    /// </summary>
#pragma warning disable AARC003 // Test-layer process observation for verification (as the other process helpers): the survivors are listed by name and id.
    private static IReadOnlyList<string> SurvivingProcesses(string artifactName)
    {
        var clock = Stopwatch.StartNew();
        while (ProcessNameProbe.IsRunning(artifactName) && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            SpinFor(TimeSpan.FromMilliseconds(100));
        }

        var survivors = new List<string>();
        foreach (var process in Process.GetProcessesByName(artifactName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        survivors.Add($"pid {process.Id} ({artifactName})");
                    }
                }
                catch (InvalidOperationException)
                {
                    // Gone while it was read.
                }
            }
        }

        return survivors;
    }

    /// <summary>The ids of the live processes with a name (for an identity check of a diagnostic's pid).</summary>
    private static IReadOnlyList<int> LiveProcessIds(string name)
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        ids.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Gone while it was read.
                }
            }
        }

        return ids;
    }

    /// <summary>The result of a run, waiting no longer than the test's bound (a run that does not end fails the test, it does not hang it).</summary>
    private static T ResultWithin<T>(Task<T> task)
    {
        if (!task.Wait(StopWait))
        {
            throw new TimeoutException("The run did not end within the test's bound.");
        }

        return task.Result;
    }
#pragma warning restore AARC003

    [Fact]
    public async Task SyntheticFrameRun_StopDuringFrameEmission_EndsCancelled_AndPublishesNoFrameAfterTheRunReturns()
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
        slot.TryGetNewer(0, out var atReturn).Should().BeTrue("the run emitted frames before the stop");
        SpinFor(TimeSpan.FromMilliseconds(300));
        slot.TryGetNewer(0, out var afterWait).Should().BeTrue();
        afterWait!.Sequence.Should().Be(atReturn!.Sequence, "no new frame is published after the run has returned");
    }

#pragma warning disable AARC003 // Test-layer process control for verification (as TestToolchain does); no product code path uses it.

    /// <summary>A private copy of a system binary under a unique name, so a test finds and stops only the processes it started.</summary>
    private static string NamedCopy(TempDirectory dir, string source, string tag)
    {
        var copy = Path.Combine(dir.FullPath, $"{tag}-{Path.GetFileNameWithoutExtension(dir.FullPath)[..6]}{Path.GetExtension(source)}");
        File.Copy(source, copy);
        return copy;
    }

    /// <summary>A binary that keeps running for about a minute: ping on Windows, sleep on Linux.</summary>
    private static string SleepBinary =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "PING.EXE") : "/bin/sleep";

    private static string[] SleepArguments =>
        OperatingSystem.IsWindows() ? ["-n", "60", "127.0.0.1"] : ["60"];

    /// <summary>The same sleeper for about one second: it ends on its own during a test's stop.</summary>
    private static string[] ShortSleepArguments =>
        OperatingSystem.IsWindows() ? ["-n", "2", "127.0.0.1"] : ["1"];

    /// <summary>Stops the processes named like a test's private copy (never another process).</summary>
    private static void KillNamed(string copyPath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(copyPath)))
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Ends the one process named like a private copy, without its tree: the children are left behind (an orphan).</summary>
    private static void KillOnlyNamed(string copyPath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(copyPath)))
        {
            try
            {
                process.Kill(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// A harness child that prints a valid snapshot block first (the engine's own rule: exit 0 with a snapshot is the child's result), then runs the
    /// given lines, and exits 0 when they do. On Windows it is a batch file run by cmd; on POSIX a shell script run by sh (round 19, MEDIUM 2).
    /// </summary>
    private static (string FileName, string[] Arguments) SnapshotChild(TempDirectory dir, string[] windowsLines, string[] posixLines)
    {
        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(dir.FullPath, "snapshot.cmd");
            var text = "@echo off\r\necho RSNAPSHOT_BEGIN 1>&2\r\necho termination=0 1>&2\r\necho pc=0x80010000 1>&2\r\necho hi=0x0 1>&2\r\necho lo=0x0 1>&2\r\n"
                + "for /L %%i in (0,1,31) do @echo gpr[%%i]=0x0 1>&2\r\necho RSNAPSHOT_END 1>&2\r\n"
                + string.Concat(windowsLines.Select(line => line + "\r\n"))
                + "exit /b 0\r\n";
            File.WriteAllText(batch, text);
            return ("cmd.exe", ["/c", batch]);
        }

        var script = Path.Combine(dir.FullPath, "snapshot.sh");
        var body = "printf 'RSNAPSHOT_BEGIN\\ntermination=0\\npc=0x80010000\\nhi=0x0\\nlo=0x0\\n' >&2\n"
            + "i=0; while [ $i -lt 32 ]; do printf 'gpr[%d]=0x0\\n' $i >&2; i=$((i+1)); done\n"
            + "printf 'RSNAPSHOT_END\\n' >&2\n"
            + string.Concat(posixLines.Select(line => line + "\n"));
        File.WriteAllText(script, body);
        return ("/bin/sh", [script]);
    }

    /// <summary>Starts a sleeper process: a real survivor for the tests that need one.</summary>
    private static Process StartSleeper(TempDirectory dir)
    {
        var copy = NamedCopy(dir, SleepBinary, "sleeper");
        var startInfo = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in SleepArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)!;
    }

    [Fact]
    public async Task RealArtifact_DisposeDuringARun_DoesNotBlockTheCaller_AndStopsTheChild()
    {
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue)));
        try
        {
            WaitUntil(() => run.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
            run.IsCompleted.Should().BeFalse("the child must still be running when the caller disposes the engine");

            // The UI thread disposes the engine; the stop of the live child waits up to 5 s, so Dispose must not wait for it.
            var clock = Stopwatch.StartNew();
            engine.Dispose();
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "Dispose must not wait for the child");
        }
        finally
        {
            engine.Dispose();
        }

        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);
        ArtifactProcessRunning(binary).Should().BeFalse("the disposed engine must not leave its child running");
    }

    [Fact]
    public async Task RealArtifact_DescendantSurvivingTheKill_EndsCancelled_WithTheSurvivorAsAResidual()
    {
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        var sleeper = StartSleeper(dir);
        try
        {
            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
                binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);

            // The seam reports the sleeper as a descendant of the child. It is still running after the kill, so the stop is refused.
            engine.StopDescendantsProbe = _ => [Process.GetProcessById(sleeper.Id)];
            using var cts = new CancellationTokenSource();
            var artifactName = Path.GetFileNameWithoutExtension(binary);
            var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
            try
            {
                WaitUntil(() => run.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
                run.IsCompleted.Should().BeFalse("the child must still be running when the stop is sent");
            }
            finally
            {
                cts.Cancel();
            }

            var result = await run.WaitAsync(StopWait);

            // Owner spec (round 14): the live child was killed and its exit seen, so the result is Cancelled. The survivor is a residual, not a
            // refusal: it does not change the result, and the shutdown is not clean.
            result.State.Should().Be(TitleExecutionState.Cancelled, "the live child was killed: the stop succeeded, whatever the cleanup left");
            engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual, "a residual process means the shutdown is not clean");
            engine.LastStopDiagnostics!.Refused.Should().BeNull("the stop itself was not refused");
            var residual = engine.LastStopDiagnostics.Residuals.Should().ContainSingle().Subject;
            residual.ProcessId.Should().Be(sleeper.Id, "the residual carries the survivor's process id");
            residual.State.Should().Be(ResidualState.EndedAfterKill, "the stop killed the survivor and saw it exit");
            residual.ObservedAtUtc.Kind.Should().Be(DateTimeKind.Utc, "the residual is stamped with its observation time (UTC)");
            residual.ObservedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
            ArtifactProcessRunning(binary).Should().BeFalse("the artifact itself was killed");

            // The survivor is a tracked process, so the stop reaps it through its own handle and reports it as left running.
            sleeper.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue("the stop reaps the tracked survivor");
        }
        finally
        {
            if (!sleeper.HasExited)
            {
                sleeper.Kill(true);
            }

            sleeper.Dispose();
        }
    }

    [Fact]
    public void AliveGrandchild_IsReportedWithItsPid_AndStillRunning_WhileTheResultIsCancelled()
    {
        // Owner spec (round 14): a survivor the stop cannot end is a residual, reported with its process id and its live state. The live child
        // was killed, so the result is Cancelled. The grandchild is a real sleeper; the OS kill refusal is the test seam (not constructible).
        using var dir = new TempDirectory();
        var root = NamedCopy(dir, SleepBinary, "aliveroot");
        var rootName = Path.GetFileNameWithoutExtension(root);
        var grandchild = StartSleeper(dir);
        try
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                root,
                SleepArguments,
                60_000,
                cts.Token,
                descendantsProbe: _ => [Process.GetProcessById(grandchild.Id)],
                descendantKill: _ => throw new InvalidOperationException("the OS refused the kill (test seam)")));
            WaitUntil(() => ProcessNameProbe.IsRunning(rootName), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(500));
            cts.Cancel();

            var outcome = ResultWithin(run);
            outcome.Confirmed.Should().BeTrue("the root was killed and its exit was seen");
            outcome.Refused.Should().BeNull("the stop itself was not refused");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the stop succeeded for the live child");
            outcome.Result.DiagnosticCode.Should().NotBe(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode, "a successful stop is never rewritten to a refusal");
            outcome.Leftover.Should().Contain($"descendant process {grandchild.Id} (", "the survivor is reported with its process id");
            outcome.Leftover.Should().Contain("still running; kill refused", "its state is alive: the stop could not end it");
            grandchild.HasExited.Should().BeFalse("the survivor really is still running, checked on the OS and not only in the record");
        }
        finally
        {
            if (!grandchild.HasExited)
            {
                grandchild.Kill(true);
            }

            grandchild.Dispose();
            KillNamed(root);
        }
    }

    [Fact]
    public void StopRequestedAfterTheSeal_IsNotReportedAsNotStopped()
    {
        // Owner LOW (round 14): a stop that is requested after the record was sealed (never processed) is not NotStopped: its descendants were not
        // checked, so the shutdown is a residual. The run's own result stands.
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

        // The seam runs after the record is sealed and before the diagnostics are taken: the request arrives too late for the record.
        engine.BeforeFrameCaptureForTest = () => engine.RequestCancel();
        var completed = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64));

        completed.State.Should().Be(TitleExecutionState.RuntimeHandoff, "the program ended on its own before the stop was processed: its own state stands");
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual, "a stop that was requested is never reported as NotStopped");
        engine.LastStopDiagnostics!.Refused.Should().Contain("sealed", "the reason says the descendants were not checked");
    }

    [Fact]
    public void OrphanGrandchildHoldingTheOutputPipe_IsReapedAtTheStopAndRecorded()
    {
        // The shell starts a sleeper with the output inherited (so it holds the pipe), stays alive for two seconds, and exits. The sleeper
        // is tracked while the shell is alive. The stop arrives after the shell has ended and while the run waits for the pipe to drain: it
        // reaps the tracked sleeper through its own handle, which closes the pipe. The one outcome: a recorded leftover, no refusal, no confirmation.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "orphan");
        var name = Path.GetFileNameWithoutExtension(copy);
        var (fileName, arguments) = SnapshotChild(
            dir,
            [$"start /b \"\" \"{copy}\" -n 60 127.0.0.1", "ping -n 2 127.0.0.1 >nul"],
            [$"\"{copy}\" 60 &", "sleep 2"]);
        try
        {
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                WaitUntil(() => ProcessNameProbe.IsRunning(name), StopWait);

                // The shell ends about two seconds after it starts; the stop lands three seconds in, inside the pipe drain.
                SpinFor(TimeSpan.FromSeconds(3));
                cts.Cancel();
            });

            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(fileName, arguments, timeoutMs: 60_000, cts.Token);
            stopper.Wait(StopWait).Should().BeTrue("the stopper finished within the test's bound");

            outcome.Leftover.Should().Contain("still running after its owner ended (killed)", "the sleeper is reaped and recorded");
            outcome.Refused.Should().BeNull("the reaped sleeper released the pipe, so the drain completed");
            outcome.Confirmed.Should().BeFalse("a leftover is not a clean stop");
            ProcessNameProbe.IsRunning(name).Should().BeFalse("the sleeper is gone");

            // The child had ended on its own before the stop reached it: its result stands, and the descendant is a diagnostic, not a refusal.
            outcome.Result.DiagnosticCode.Should().NotBe(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode);
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Completed, "the child's own result is kept");

            // The residual is reported apart from the result: the shutdown is not clean, although the result is the child's own.
            (outcome.Refused is null && outcome.Leftover is null).Should().BeFalse("a residual process means the shutdown is not clean");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [SkippableFact]
    public void ClaimTimeIdentityMismatch_RefusesTheStop_AndClaimsNothingBelowIt()
    {
        // Issue #827 review (2b): the claim-time identity comparison is what is tested. The middle process's start time reads back as a
        // different value after it was claimed, so its identity cannot be verified: its child (the grandchild) is not claimed, and the stop
        // is refused with that reason. The seam only changes the value read for the middle; the comparison itself runs as in production.
        Skip.IfNot(OperatingSystem.IsWindows(), "the middle is a copy of the Windows shell");
        using var dir = new TempDirectory();
        var middle = NamedCopy(dir, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "mismatch");
        var grandchild = NamedCopy(dir, SleepBinary, "mgrand");
        var middleName = Path.GetFileNameWithoutExtension(middle);
        var grandName = Path.GetFileNameWithoutExtension(grandchild);
        var reads = 0;
        try
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                "cmd.exe",
                ["/c", middle, "/c", grandchild, "-n", "60", "127.0.0.1", "&", "ping", "-n", "30", "127.0.0.1", ">nul"],
                60_000,
                cts.Token,
                startTime: process =>
                {
                    // The first read of the middle (its claim) is real; every later read names a different start time.
                    if (process.ProcessName != middleName)
                    {
                        return process.StartTime;
                    }

                    reads++;
                    return reads == 1 ? process.StartTime : DateTime.MinValue;
                }));

            WaitUntil(() => ProcessNameProbe.IsRunning(grandName), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(500));
            cts.Cancel();
            var outcome = ResultWithin(run);

            // Owner spec (round 15): the root was killed and its exit seen, so the stop succeeded and the result is Cancelled. The unverifiable
            // identity of the middle is an uncertainty in the diagnostics: its children are unidentified and are not claimed or reaped.
            outcome.Confirmed.Should().BeTrue("the root was killed and its exit was seen");
            outcome.Refused.Should().BeNull("the kill itself was not refused");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the stop succeeded for the live child");
            outcome.Leftover.Should().Contain("could not be verified, so its unclaimed children are unidentified", "the uncertainty is recorded");
            outcome.Leftover.Should().NotContain("after its owner ended (killed)", "nothing below the unverifiable middle is reaped");
        }
        finally
        {
            KillNamed(grandchild);
            KillNamed(middle);
        }
    }

    [Fact]
    public void ProcessTreeListing_SeesARealChild_OnEveryPlatformThatSupportsIt()
    {
        // Regression guard (Issue #820, round 15): macOS listed nothing, so every stop was refused. On Windows, Linux and macOS a real child is listed
        // with its parent id; any other platform reports that the listing is unavailable.
        using var dir = new TempDirectory();
        var child = StartSleeper(dir);
        try
        {
            var listing = ProcessTreeInterop.TryReadParentIds();
            if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                listing.Should().BeNull("this platform cannot list the process tree");
                return;
            }

            listing.Should().NotBeNull("the process tree can be listed on this platform");
            listing!.Should().Contain(entry => entry.Pid == child.Id && entry.Ppid == Environment.ProcessId, "a real child is listed under this test process");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
            }

            child.Dispose();
        }
    }

    [Fact]
    public void KillRefused_IsARefusal_NotCancelled_OnEveryPlatform()
    {
        // Owner spec (round 15), cross-platform: a kill the OS refuses leaves the child alive, so the stop did not succeed. The result is not
        // Cancelled, and the refusal is recorded with its reason. The OS refusal is the test seam (not constructible on demand).
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "killrefused");
        var name = Path.GetFileNameWithoutExtension(copy);
        try
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                copy,
                SleepArguments,
                60_000,
                cts.Token,
                kill: _ => throw new InvalidOperationException("access denied (test seam)")));
            WaitUntil(() => ProcessNameProbe.IsRunning(name), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(300));
            cts.Cancel();

            var outcome = ResultWithin(run);
            outcome.Confirmed.Should().BeFalse("the child was not killed");
            outcome.Refused.Should().Contain("the kill was refused", "the refusal is recorded with its reason");
            // Owner spec (round 18, MEDIUM 1): the child is alive at the end of its wait, so its own result is its timeout; the refusal is a residual.
            outcome.TimedOut.Should().BeTrue("the child was alive when the wait ended, so the wait is a timeout");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.TimedOut, "a refused stop keeps the child's timeout as its result");
            outcome.Result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.TimeoutDiagnosticCode);
            ProcessNameProbe.IsRunning(name).Should().BeTrue("the child is really still running");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [Fact]
    public void ReapAfterTheBudgetIsSpent_ProcessesJustKilled_AreNotReportedAlive()
    {
        // Owner rule 7, round 19 (MEDIUM 1): the root's natural-end wait spends the reap's budget while two live grandchildren are tracked. Each is
        // then killed with no budget left. Only the one shared second pass judges them: a process that dies just after its kill is reported as
        // killed (ended), and a process still running after that would be alive. Neither is reported alive here.
        using var dir = new TempDirectory();
        var root = NamedCopy(dir, SleepBinary, "reaproot");
        var rootName = Path.GetFileNameWithoutExtension(root);
        // Each sleeper fixture names its copy after a fixed tag, so the second one lives in its own directory.
        using var secondDir = new TempDirectory();
        var first = StartSleeper(dir);
        var second = StartSleeper(secondDir);
        try
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                root,
                SleepArguments,
                60_000,
                cts.Token,
                descendantsProbe: _ => [Process.GetProcessById(first.Id), Process.GetProcessById(second.Id)]));
            WaitUntil(() => ProcessNameProbe.IsRunning(rootName), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(500));
            var clock = Stopwatch.StartNew();
            cts.Cancel();
            var outcome = ResultWithin(run);

            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12), "the reap is bounded by its budget and one shared second pass");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the root was killed and its exit was seen");
            outcome.Leftover.Should().Contain($"descendant process {first.Id} (", "the first grandchild is reported");
            outcome.Leftover.Should().Contain($"descendant process {second.Id} (", "the second grandchild is reported");
            outcome.Leftover.Should().NotContain("still running after its kill", "no process killed by the reap is reported alive on the spent budget");
            first.WaitForExit(StopWait).Should().BeTrue("the first grandchild is dead");
            second.WaitForExit(StopWait).Should().BeTrue("the second grandchild is dead");
        }
        finally
        {
            foreach (var process in new[] { first, second })
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                process.Dispose();
            }

            KillNamed(root);
        }
    }

    [Fact]
    public void StartTimeUnreadable_EndsCancelled_AndRecordsTheUnidentifiedDescendants()
    {
        // Owner spec (round 15), written independently of the implementation: a stop that kills the live child and sees its exit has succeeded,
        // so the result is Cancelled, even when the child's start time cannot be read. The unreadable start time is an uncertainty in the
        // diagnostics: its descendants are unidentified and no pid is reported. Runs on Windows and Linux: the child is the platform's sleep copy.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "nostart");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                copy,
                SleepArguments,
                30_000,
                cts.Token,
                startTime: _ => null);

            outcome.Confirmed.Should().BeTrue("the child was killed and its exit was seen");
            outcome.Refused.Should().BeNull("the kill itself was not refused");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the stop succeeded for the live child");
            outcome.Result.DiagnosticCode.Should().NotBe(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode);
            outcome.Leftover.Should().Contain("unidentified", "the descendants that cannot be attributed are reported as unidentified");
            ProcessNameProbe.IsRunning(Path.GetFileNameWithoutExtension(copy)).Should().BeFalse("the child is dead");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [SkippableFact]
    public void GrandchildOfAnIntermediateThatEndedFirst_IsTrackedWhileAliveAndReapedAtTheStop()
    {
        // Issue #827 review (three levels): root (cmd) -> middle (a copy of cmd) -> grandchild (sleeper). The root stays alive after the middle
        // ends (its second command). The middle is ended alone (without its tree), so the grandchild is orphaned. The grandchild was tracked while the middle
        // was alive. The stop then finds it through its own handle and records it.
        Skip.IfNot(OperatingSystem.IsWindows(), "the three-level case depends on parent ids that Linux does not keep for orphans");
        using var dir = new TempDirectory();
        var middle = NamedCopy(dir, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "middle");
        var grandchild = NamedCopy(dir, SleepBinary, "grand");
        var grandName = Path.GetFileNameWithoutExtension(grandchild);
        var middleName = Path.GetFileNameWithoutExtension(middle);
        try
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                "cmd.exe",
                ["/c", middle, "/c", grandchild, "-n", "60", "127.0.0.1", "&", "ping", "-n", "30", "127.0.0.1", ">nul"],
                60_000,
                cts.Token));

            // The grandchild runs under the middle; the tree is sampled for half a second while both are alive.
            WaitUntil(() => ProcessNameProbe.IsRunning(grandName), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(500));

            // The test ends the middle alone, without its tree, so the grandchild is left running with no living parent. The root is still
            // alive, waiting on its second command.
            KillOnlyNamed(middle);
            WaitUntil(() => !ProcessNameProbe.IsRunning(middleName), StopWait);
            run.IsCompleted.Should().BeFalse("the root is still alive when the stop is sent");
            ProcessNameProbe.IsRunning(grandName).Should().BeTrue("the grandchild is an orphan, still running");

            cts.Cancel();
            var outcome = ResultWithin(run);

            outcome.Leftover.Should().Contain("still running after its owner ended (killed)", "the orphan is reaped and recorded");
            outcome.Confirmed.Should().BeTrue("the root was killed and its exit seen: the orphan is a residual, not a refusal");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the stop succeeded for the live child");
            outcome.Refused.Should().BeNull("the reaped orphan released what it held");
            ProcessNameProbe.IsRunning(grandName).Should().BeFalse("the grandchild is reaped through its own handle");
        }
        finally
        {
            KillNamed(grandchild);
            KillNamed(middle);
        }
    }

    [Fact]
    public void NaturalEnd_WithAGrandchildHoldingStdout_KeepsTheChildsResult_AndRecordsTheHolderAsAResidual()
    {
        // Owner spec (round 16), written from the spec and not from the implementation: the guest's result and the process cleanup are decided
        // apart. The child ended on its own with exit 0, so its result is Completed and stands. A grandchild it started still holds stdout after
        // the drain budget: that is a residual of the cleanup, with an unidentified holder, and it does not change the result. No stop was requested.
        // The budget is injected (500 ms), so the test does not wait for the run's real budget.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "drain");
        var (fileName, arguments) = SnapshotChild(dir, [$"start /b \"\" \"{copy}\" -n 60 127.0.0.1"], [$"\"{copy}\" 60 2>/dev/null &"]);
        try
        {
            var clock = Stopwatch.StartNew();
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                fileName, arguments, timeoutMs: 60_000, CancellationToken.None, outputBudgetMs: 500);

            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
            outcome.TimedOut.Should().BeFalse("the child ended on its own");
            outcome.ExitCode.Should().Be(0, "the child ended normally");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Completed, "the child ended on its own: its own result stands");
            outcome.Result.DiagnosticCode.Should().BeNull("the stalled stdout is not the guest's result");
            outcome.Confirmed.Should().BeFalse("no stop was requested");
            outcome.Refused.Should().BeNull("no stop was refused");
            outcome.Leftover.Should().Contain("holder is unidentified", "the pipe holder is a residual with its pid not known");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [Fact]
    public void ChildStillAlive_WithAStalledOutputDrain_IsOutputDrainTimeout()
    {
        // Owner spec (round 16): the output drain-timeout code is the result only while the guest is still running at its budget, and its
        // stdout cannot be drained after the kill. The grandchild holds the stdout and is not in the tree, so the cleanup cannot end it. The
        // budget is injected (1.5 s); the cleanup budget is the stdout's own (5 s), so the test takes about seven seconds.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "stalled");
        var middle = OperatingSystem.IsWindows() ? NamedCopy(dir, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "stalledmid") : string.Empty;
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", middle, "/c", "start", "/b", copy, "-n", "60", "127.0.0.1", "&", "ping", "-n", "30", "127.0.0.1", ">nul" })
            : ("/bin/sh", new[] { "-c", $"sh -c '{copy} 60 &'; sleep 30" });
        try
        {
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(fileName, arguments, timeoutMs: 1_500, CancellationToken.None);

            outcome.TimedOut.Should().BeTrue("the child was still running at its budget");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.ExecutionFailed, "the guest did not finish, and its output could not be drained");
            outcome.Result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.OutputDrainTimeoutDiagnosticCode);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                KillNamed(middle);
            }

            KillNamed(copy);
        }
    }

    [Fact]
    public void StopDuringTheDrain_CapsTheRestOfTheDrainAtTheCleanupBudget()
    {
        // Review round 12 (MEDIUM 3): a stop caps the drain only for a child that is alive when the stop arrives. Here the root stays alive, while a
        // grandchild it started through a middle process (which exits at once) holds the stdout: the grandchild is not in the root's tree. The stop
        // finds the root alive, kills its tree, and the drain is still held: the cap applies (five seconds from the stop), so the run ends soon after.
        // The grandchild is not tracked (the probe lists nothing), so the stop does not reap it.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "capped");
        var name = Path.GetFileNameWithoutExtension(copy);
        // The Windows middle is a copy of the shell (Linux has no cmd.exe: its middle is sh, named in the arguments below).
        var middle = OperatingSystem.IsWindows() ? NamedCopy(dir, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "capmid") : string.Empty;
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", middle, "/c", "start", "/b", copy, "-n", "60", "127.0.0.1", "&", "ping", "-n", "30", "127.0.0.1", ">nul" })
            : ("/bin/sh", new[] { "-c", $"sh -c '{copy} 60 &'; sleep 30" });
        try
        {
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                WaitUntil(() => ProcessNameProbe.IsRunning(name), StopWait);
                SpinFor(TimeSpan.FromMilliseconds(500));
                cts.Cancel();
            });

            var clock = Stopwatch.StartNew();
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                fileName,
                arguments,
                timeoutMs: 60_000,
                cts.Token,
                descendantsProbe: _ => Array.Empty<Process>(),
                outputBudgetMs: 60_000);
            stopper.Wait(StopWait).Should().BeTrue("the stopper finished within the test's bound");

            // The stop lands about 0.5 s in; the cap allows five seconds after it. Ten seconds is the bound; the 60 s budget is not waited for.
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the stop caps the drain of a live child at five seconds after it");
            // The live root was killed and its exit seen, so the stop succeeded: the result is Cancelled. The untracked grandchild still holds the
            // stdout pipe, so the drain is cut at the stop's cap. Both facts are residuals in the diagnostics, not in the result.
            outcome.Confirmed.Should().BeTrue("the live root was killed and its exit was seen");
            outcome.Refused.Should().BeNull("the stop itself was not refused");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the stop succeeded for the live child, whatever the cleanup left");
            outcome.Leftover.Should().Contain("an output pipe is still held", "the grandchild outlived the child and still holds the stdout pipe");
            outcome.Leftover.Should().Contain("the stdout drain timed out after the stop", "the drain cut at the cap is recorded, not hidden");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [SkippableFact]
    public void DescendantStartTimeUnreadable_RefusesTheStop_AndClaimsNothingBelowIt()
    {
        // Issue #827 review (LOW): a descendant without a start time is not claimed, as the root without one is not. The middle's start time
        // cannot be read, so the grandchild below it is never claimed: nothing below the middle is reaped, and the stop is refused.
        Skip.IfNot(OperatingSystem.IsWindows(), "the middle is a copy of the Windows shell");
        using var dir = new TempDirectory();
        var middle = NamedCopy(dir, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "nullmid");
        var grandchild = NamedCopy(dir, SleepBinary, "nullgrand");
        var grandName = Path.GetFileNameWithoutExtension(grandchild);
        try
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                "cmd.exe",
                ["/c", middle, "/c", grandchild, "-n", "60", "127.0.0.1", "&", "ping", "-n", "30", "127.0.0.1", ">nul"],
                60_000,
                cts.Token,
                startTime: process => process.ProcessName.StartsWith("nullmid", StringComparison.OrdinalIgnoreCase) ? null : process.StartTime));

            WaitUntil(() => ProcessNameProbe.IsRunning(grandName), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(500));
            cts.Cancel();
            var outcome = ResultWithin(run);

            // Owner spec (round 15): the middle was killed with the root's tree and its exit seen (the root tree kill), so the result is Cancelled.
            // The middle's start time cannot be read: its descendants are unidentified, a diagnostic, and nothing below it is reaped by the stop.
            outcome.Confirmed.Should().BeTrue("the root was killed and its exit was seen");
            outcome.Refused.Should().BeNull("the kill itself was not refused");
            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Cancelled, "the stop succeeded for the live child");
            outcome.Leftover.Should().Contain("cannot be read", "the unreadable start time is recorded as an uncertainty");
            outcome.Leftover.Should().NotContain("after its owner ended (killed)", "nothing below the unattributable process is reaped");
        }
        finally
        {
            KillNamed(grandchild);
            KillNamed(middle);
        }
    }

    /// <summary>Starts a real child that ends on its own after about one second (the host-transfer case: the child is gone, its output is not).</summary>
    private static Process StartShortLivedChild(TempDirectory dir)
    {
        var copy = NamedCopy(dir, SleepBinary, "pumpchild");
        var startInfo = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in ShortSleepArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)!;
    }

    [Fact]
    public void HostTransferPump_OpenAfterTheChildEnded_IsReportedAsADrainTimeout()
    {
        // Issue #827 review (MEDIUM 1): the bridge path. The child ends, and the pump of its output never reaches its end (a grandchild holds the
        // pipe; a task that never completes stands for that pipe). The wait ends after the injected output budget (500 ms), reported as a drain
        // timeout, not as a run timeout, and never after the run's real budget (60 s).
        using var dir = new TempDirectory();
        using var child = StartShortLivedChild(dir);
        using var stop = new StopRecord();
        var pump = new TaskCompletionSource().Task;

        var clock = Stopwatch.StartNew();
        var completed = RecompiledHostExecutionEngine.WaitPumpWithinOutputBudget(pump, child, runTimeoutMs: 60_000, outputBudgetMs: 500, stop, out var drainTimedOut);

        completed.Should().BeFalse();
        drainTimedOut.Should().BeTrue("the child ended and its output did not end in time");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        child.HasExited.Should().BeTrue();
    }

    [Fact]
    public void HostTransferPump_OpenAfterTheChildEnded_WithAStop_KeepsTheOutputBudget_NotCappedByTheStop()
    {
        // Review round 12 (MEDIUM 3): the same case with a stop during the wait. The child ended first, so the stop does not cap the wait: the wait
        // keeps the output budget (8 s here), counted from the child's end. Capped from the stop it would end about five seconds after the stop.
        using var dir = new TempDirectory();
        using var child = StartShortLivedChild(dir);
        using var cts = new CancellationTokenSource();
        using var stop = new StopRecord();
        using var stopSeal = stop.Attach(child, cts.Token);
        var pump = new TaskCompletionSource().Task;

        // The stop lands after the child has ended (about one second in); the pump is still open.
        var stopper = Task.Run(() =>
        {
            WaitUntil(() => child.HasExited, StopWait);
            SpinFor(TimeSpan.FromMilliseconds(300));
            cts.Cancel();
        });
        var clock = Stopwatch.StartNew();
        var completed = RecompiledHostExecutionEngine.WaitPumpWithinOutputBudget(pump, child, runTimeoutMs: 60_000, outputBudgetMs: 8_000, stop, out var drainTimedOut);
        stopper.Wait(StopWait).Should().BeTrue("the stopper finished within the test's bound");

        // The child ends about one second in; the output budget runs to about nine seconds. A cap from the stop would have ended the wait near 5.3 s.
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(7), "the child ended first: the stop does not cap the wait");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the wait ends by the output budget");
        completed.Should().BeFalse();
        drainTimedOut.Should().BeTrue("the output did not end before the cleanup budget from the stop ran out");
        stop.Settled.Should().BeTrue();
        stop.ChildEndedBeforeStop.Should().BeTrue("the child ended on its own before the stop reached it");
    }

    [Fact]
    public void StoppedTreeWithRealDescendants_IsConfirmedOnlyOnceTheyAreGone()
    {
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "tree");
        var name = Path.GetFileNameWithoutExtension(copy);
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", copy, "-n", "60", "127.0.0.1" })
            : ("/bin/sh", new[] { "-c", $"{copy} 60; :" });
        try
        {
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                // The stop is sent only once the descendant exists, so the tree really has one to confirm.
                WaitUntil(() => ProcessNameProbe.IsRunning(name), StopWait);
                cts.Cancel();
            });

            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(fileName, arguments, timeoutMs: 60_000, cts.Token);
            stopper.Wait(StopWait).Should().BeTrue("the stopper finished within the test's bound");

            outcome.Refused.Should().BeNull("every process of the tree was seen exited");
            outcome.Confirmed.Should().BeTrue();
            ProcessNameProbe.IsRunning(name).Should().BeFalse("the descendant must be gone when the stop is confirmed");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [Fact]
    public void StopKillsTheChild_ItsPipesEnd_AndNoHeldPipeIsReported_EvenWhileTheTextContinuationHasNotRun()
    {
        // Stop-racing flake: a pipe is reported as held when its text continuation has not run yet, although the pipe's pump
        // has ended. The seam holds the continuation pending (a scheduler that never runs it). The child is killed by the stop, and its pipes
        // end with it: no holder exists, so no residual may name a held pipe.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "pipeend");
        var name = Path.GetFileNameWithoutExtension(copy);
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", copy, "-n", "60", "127.0.0.1" })
            : ("/bin/sh", new[] { "-c", $"{copy} 60; :" });
        try
        {
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                WaitUntil(() => ProcessNameProbe.IsRunning(name), StopWait);
                cts.Cancel();
            });

            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                fileName, arguments, timeoutMs: 60_000, cts.Token, stderrContinuationScheduler: new NeverRunsTaskScheduler());
            stopper.Wait(StopWait).Should().BeTrue("the stopper finished within the test's bound");

            (outcome.Leftover ?? string.Empty).Should().NotContain("an output pipe is still held", "the child's pipes ended with the child: no holder is left");
            ProcessNameProbe.IsRunning(name).Should().BeFalse("the child is gone");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    /// <summary>A scheduler that never runs a queued task: a continuation given to it stays pending (a test seam for the timing race).</summary>
    private sealed class NeverRunsTaskScheduler : TaskScheduler
    {
        protected override void QueueTask(Task task)
        {
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => Array.Empty<Task>();
    }

    [Fact]
    public void ACompletedRun_WithARecordedRefusal_KeepsItsResult_AndTheRefusalStaysInTheDiagnostics()
    {
        // Issue #827, decided in review round 10: the run's result stands; a refusal is a diagnostic. It is not written into the result.
        var stop = new StopRecord();
        stop.RefuseIfUnset("a refusal recorded by a stop");
        stop.Seal();
        var completed = new RecompilerExecutionResult(RecompilerExecutionStatus.Completed, null, null, null);

        var result = RecompiledHostExecutionEngine.ApplyStopOutcome(completed, stop);

        result.Should().BeSameAs(completed, "the run's own result is kept");
        stop.Outcome.Refused.Should().Be("a refusal recorded by a stop", "the refusal is reported in the diagnostics");
    }

    [Fact]
    public void ACompletedRun_WithoutAStop_KeepsItsResult()
    {
        // A completed run with no stop at all keeps its result.
        var stop = new StopRecord();
        stop.Seal();
        var completed = new RecompilerExecutionResult(RecompilerExecutionStatus.Completed, null, null, null);

        RecompiledHostExecutionEngine.ApplyStopOutcome(completed, stop).Should().BeSameAs(completed);
    }

    [Fact]
    public async Task RealArtifact_DescendantThatCannotBeChecked_EndsCancelled_WithAnUnidentifiedResidual()
    {
        // Failure injection (Issue #827 review): a descendant whose handle is no longer usable models an access the OS refuses. The
        // stop cannot tell whether it is gone, so it must say so, not report a plain failure.
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        var sleeper = StartSleeper(dir);
        try
        {
            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
                binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
            engine.StopDescendantsProbe = _ =>
            {
                var handle = Process.GetProcessById(sleeper.Id);
                handle.Dispose();
                return [handle];
            };
            using var cts = new CancellationTokenSource();
            var artifactName = Path.GetFileNameWithoutExtension(binary);
            var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
            try
            {
                WaitUntil(() => run.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
                run.IsCompleted.Should().BeFalse("the child must still be running when the stop is sent");
            }
            finally
            {
                cts.Cancel();
            }

            var result = await run.WaitAsync(StopWait);

            // Owner spec (round 15): the artifact was killed and its exit seen, so the result is Cancelled. The descendant that cannot be checked is
            // an unidentified uncertainty in the diagnostics: the shutdown is not clean, and no pid is claimed for it.
            result.State.Should().Be(TitleExecutionState.Cancelled, "the artifact was killed and its exit was seen");
            result.DiagnosticCode.Should().NotBe(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode);
            engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual);
            engine.LastStopDiagnostics!.Refused.Should().BeNull("the stop itself was not refused");
            var uncertainty = engine.LastStopDiagnostics.Residuals.Should().ContainSingle().Subject;
            uncertainty.ProcessId.Should().BeNull("the descendant's identity cannot be verified, so no pid is reported");
            uncertainty.State.Should().Be(ResidualState.Unverified);
            uncertainty.Reason.Should().Contain("could not be checked");
            ArtifactProcessRunning(binary).Should().BeFalse("the artifact itself was killed");
        }
        finally
        {
            if (!sleeper.HasExited)
            {
                sleeper.Kill(true);
            }

            sleeper.Dispose();
        }
    }

    [Fact]
    public async Task RealArtifact_StopDuringTheRhostWait_EndsCancelled_AfterTheWaitIsObserved()
    {
        // Issue #827 acceptance: a stop during the RHOST wait. The artifact is a host-bridge child that never sends a protocol line, so
        // the run waits on its output. The test counts those waits and sends the stop after three of them (about 300 ms).
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);

        // A BIOS runtime factory attaches the host-transfer bridge, so the run waits on the child's protocol output (the RHOST wait).
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            binary,
            blockEntryPcs: blockEntryPcs,
            imageWords: SpinningGuest,
            imageLoadAddress: Entry,
            biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer));
        using var cts = new CancellationTokenSource();
        var hostWaits = 0;
        engine.WaitObservedForTest = (_, _, phase, ended) =>
        {
            if (phase == "pump" && !ended)
            {
                hostWaits++;
                if (hostWaits == 3)
                {
                    cts.Cancel();
                }
            }

            return ended;
        };

        var run = Task.Run(() => new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, uint.MaxValue), cancellationToken: cts.Token));
        var result = await run.WaitAsync(StopWait);

        result.State.Should().Be(TitleExecutionState.Cancelled);
        result.DiagnosticCode.Should().Be(ExecutionOrchestrator.CancelledCode);
        hostWaits.Should().BeGreaterThanOrEqualTo(3, "the stop was sent while the run was waiting for the host protocol");
        ArtifactProcessRunning(binary).Should().BeFalse("the stopped artifact process must be gone when the run returns");
    }

    [Fact]
    public void RealArtifact_RefusedStop_ChildStillAlive_ResultIsItsTimeout_AndShutdownIsResidual()
    {
        // Owner spec (round 18, MEDIUM 1), written from the spec and not from the implementation. The OS refuses the kill of a live child, so the
        // stop did not succeed. The run's result is then the child's own result: it is still running at its end, which is a timeout
        // (ARTIFACT_TIMEOUT). The refusal and the cut stdout drain are residuals in the diagnostics; neither a STOP_REFUSED nor an
        // ARTIFACT_OUTPUT_DRAIN_TIMEOUT result replaces it. The shutdown is Residual, and the refused child's pid is the live artifact's.
        // The same contract on Windows and POSIX: the artifact is the real spinning guest.
        using var dir = new TempDirectory();
        var binary = UniqueArtifactCopy(BuildSpinningArtifact(dir, out var blockEntryPcs), dir);
        var artifactName = Path.GetFileNameWithoutExtension(binary);
        var kills = 0;
        try
        {
            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
                binary, blockEntryPcs: blockEntryPcs, imageWords: SpinningGuest, imageLoadAddress: Entry);
            engine.KillOverrideForTest = _ =>
            {
                kills++;
                throw new System.ComponentModel.Win32Exception(5, "Access is denied.");
            };
            var gpr = new uint[TitleExecutionRequest.GprCount];
            engine.Load(new TitleExecutionRequest(Entry, gpr, 0, 0, [], outerBudget: 1, segmentBudget: uint.MaxValue));
            var segment = Task.Run(() => engine.RunSegment(new TitleExecutionSegmentRequest(gpr, 0, 0, Entry, uint.MaxValue)));
            WaitUntil(() => segment.IsCompleted || ProcessNameProbe.IsRunning(artifactName), StopWait);
            segment.IsCompleted.Should().BeFalse("the child must still be running when the stop is sent");
            engine.RequestCancel();

            var result = ResultWithin(segment);

            result.Status.Should().Be(RecompilerExecutionStatus.TimedOut, "the refused stop left the live child running: its own result is its timeout");
            result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.TimeoutDiagnosticCode, "not STOP_REFUSED, and not the output drain timeout");
            engine.ShutdownStatus.Should().Be(ShutdownOutcome.Residual, "a refused kill is a residual shutdown");
            engine.LastStopDiagnostics!.Refused.Should().Contain("kill was refused", "the refusal is recorded with its reason");
            engine.LastStopDiagnostics.Confirmed.Should().BeFalse("the child was not killed");
            LiveProcessIds(artifactName).Should().Contain(engine.LastStopDiagnostics.ProcessId, "the diagnostics name the live artifact process");
            kills.Should().BeGreaterThanOrEqualTo(1, "the stop's kill was attempted and refused");
            ArtifactProcessRunning(binary).Should().BeTrue("the OS refused the kill, so the child is still running");
        }
        finally
        {
            KillNamed(binary);
        }
    }

    [Fact]
    public void ExceptionAfterANaturalEnd_IsNotConvertedToARunException()
    {
        // Owner rule 3, round 18 (LOW 4): a failure that follows the child's own natural end is not a stop failure. It is not converted into
        // ARTIFACT_RUN_EXCEPTION, which would replace the child's own result; it propagates. The seam requests the stop at the child's end and
        // waits for the stop to settle (it found the child already ended), then fails.
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
        engine.WaitObservedForTest = (record, child, phase, ended) =>
        {
            if (!ended)
            {
                return false;
            }

            engine.RequestCancel();
            WaitUntil(() => record.Settled, StopWait);
            throw new InvalidOperationException("the seam fails after the child's natural end (test)");
        };
        var gpr = new uint[TitleExecutionRequest.GprCount];
        engine.Load(new TitleExecutionRequest(Entry, gpr, 0, 0, [], outerBudget: 1, segmentBudget: 64));

        Action act = () => engine.RunSegment(new TitleExecutionSegmentRequest(gpr, 0, 0, Entry, 64));

        act.Should().Throw<InvalidOperationException>().WithMessage("*after the child's natural end*",
            "the failure is not a stop failure, so it is not replaced by ARTIFACT_RUN_EXCEPTION");
    }

    [Fact]
    public void ChildEndingWhileItsTreeIsListed_IsNotConfirmedAsCancelled()
    {
        // Issue #827 review (MEDIUM 2): the child ends while the stop lists its tree. The stop must see the natural end: nothing is
        // confirmed, nothing is refused, and the run is not reported as a cancellation. Deterministic: the stop is sent while the child is
        // alive, and the listing waits for the child to end.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "race");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                copy,
                ShortSleepArguments,
                30_000,
                cts.Token,
                descendantsProbe: child =>
                {
#pragma warning disable AARC003 // Test-layer observation of the child's end, to place the race exactly (Issue #827 review)
                    // Bounded (round 18, LOW 2): a child that does not end fails the test instead of hanging it.
                    child.WaitForExit(StopWait).Should().BeTrue("the child must end within the test's bound");
#pragma warning restore AARC003
                    return Array.Empty<Process>();
                });

            outcome.Confirmed.Should().BeFalse("a child that ended on its own is not a confirmed stop");
            outcome.Refused.Should().BeNull();
            outcome.Leftover.Should().BeNull();
            outcome.TimedOut.Should().BeFalse();
            outcome.ExitCode.Should().Be(0, "the child ended normally");
        }
        finally
        {
            KillNamed(copy);
        }
    }


#pragma warning restore AARC003

    [Fact]
    public void FrameIsNotRead_WhenTheStopIsRequestedAfterTheStopRecordIsSealed()
    {
        // Issue #827 review (MAJOR, window a): the child ends on its own (a short budget). The stop is requested after the run has sealed its stop
        // record, just before the frame decision. The stop was requested, so no frame may be read: zero reads, and no frame kept. A null result alone
        // is not enough, so the number of reads is checked too.
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
        // The stop request is synchronous here (RequestCancel sets the flag at once), so the window is placed exactly, with no asynchronous callback.
        engine.BeforeFrameCaptureForTest = () => engine.RequestCancel();

        // A short budget: the child ends by itself, so its pump ends and the frame decision is reached.
        var request = new TitleExecutionRequest(entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: 200_000u);
        new ExecutionOrchestrator().Execute(engine, handoff: null, request);

        engine.FrameCaptureCalls.Should().Be(0, "a stop was requested before the frame decision, so no frame is read");
        engine.LastFrameBgra32.Should().BeNull();
    }

    [Fact]
    public void StopRequestDuringAFrameRead_WaitsForTheRead_AndTheFlagIsNotVisibleInsideIt()
    {
        // Issue #827, decided in review round 10 (window between the flag check and the GPU read). The read section is the flag check and the
        // read start under one lock. A stop requested from another thread while the section is open must wait for it: the flag is not visible
        // inside the read, the read happens once, and the flag is set only after it.
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

        Task? requestTask = null;
        var requestThreadStarted = new ManualResetEventSlim(false);
        var requestWaitedForTheRead = false;
        var flagVisibleInsideTheRead = true;
        engine.DuringFrameReadForTest = () =>
        {
            // The read section is open here: the check passed and the GPU read has not started. The request runs on another thread and signals
            // that it has started; it must then be blocked inside RequestCancel while this section is open.
            requestTask = Task.Run(() =>
            {
                requestThreadStarted.Set();
                engine.RequestCancel();
            });
            requestThreadStarted.Wait(StopWait).Should().BeTrue("the request thread started within the test's bound");
            SpinFor(TimeSpan.FromMilliseconds(300));
            flagVisibleInsideTheRead = engine.StopRequestFlagForTest;
            requestWaitedForTheRead = requestThreadStarted.IsSet && !requestTask.IsCompleted;
        };

        var request = new TitleExecutionRequest(entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: 200_000u);
        new ExecutionOrchestrator().Execute(engine, handoff: null, request);

        requestTask.Should().NotBeNull("the stop was requested inside the read section");
        requestTask!.Wait(StopWait).Should().BeTrue("the request completes once the read has ended");
        requestWaitedForTheRead.Should().BeTrue("the stop request waited while the read section was open");
        flagVisibleInsideTheRead.Should().BeFalse("the flag is not visible inside the read");
        engine.FrameCaptureCalls.Should().Be(1, "the read happened once, before the stop request was applied");
        engine.StopRequestFlagForTest.Should().BeTrue("the request is applied after the read");
    }

    /// <summary>
    /// Runs the synthetic program with a reader polling the live slot (so the live VBlank path reads the VRAM), and stops it by <paramref name="stop"/>
    /// once live reads have happened. Returns the reads so far and the reads at the stop request (Issue #827, owner requirement). The span checked
    /// is the stop request up to the end of the pump: the run returns only after its pump has ended.
    /// </summary>
    private static (int Reads, int ReadsAtStop) RunLiveReadsUntilStop(Action<RecompiledHostExecutionEngine, CancellationTokenSource> stop)
    {
        using var dir = new TempDirectory();
        var slot = new LiveFrameSlot();
        var words = SyntheticLiveProgram.Words();
        var entry = SyntheticLiveProgram.EntryPc;
        var program = ReachableProgramBuilder.Build(entry, words, entry, []);
        var engine = new RecompiledHostExecutionEngine(
            program,
            words,
            entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardSink(), reader, writer),
            embedImage: true,
            liveFrames: slot);
        using var readerDone = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var last = 0L;
            var spin = new SpinWait();
            while (!readerDone.IsCancellationRequested)
            {
                if (slot.TryGetNewer(last, out var frame))
                {
                    last = frame.Sequence;
                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }
        });
        // The segment is driven directly, not through the orchestrator: the orchestrator would also call RequestCancel on a token cancel, and then
        // the test could not tell the external token from the request (review round 14, LOW).
        engine.Load(new TitleExecutionRequest(entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: 20_000_000u));
        using var cts = new CancellationTokenSource();
        using var attachment = engine.AttachCancellation(cts.Token);
        var gpr = new uint[TitleExecutionRequest.GprCount];
        var run = Task.Run(() => engine.RunSegment(new TitleExecutionSegmentRequest(gpr, 0, 0, entry, 20_000_000u)));
        int readsAtStop;
        try
        {
            WaitUntil(() => engine.FrameReadStats.Reads >= 3 || run.IsCompleted, StopWait);
            run.IsCompleted.Should().BeFalse("the run must still be emitting VBlank frames when the stop is requested");
            stop(engine, cts);

            // Taken right after the stop: a read that had started before it is counted (the stats wait for the gate); every later read is refused.
            readsAtStop = engine.FrameReadStats.Reads;
            run.Wait(StopWait).Should().BeTrue("the stopped run returns");
        }
        finally
        {
            readerDone.Cancel();
            reader.Wait(StopWait).Should().BeTrue("the reader ended within the test's bound");
            engine.Dispose();
        }

        return (engine.FrameReadStats.Reads, readsAtStop);
    }

    [Fact]
    public void LiveVBlankReads_StopByToken_ReadNothingAfterTheStopRequest()
    {
        // The live path reads at VBlank on the pump thread. After the stop request, no VBlank reads: the count at the request equals the count at the
        // end of the run (its pump has ended by then). Before the request there were reads, so the check is not vacuous.
        var (reads, readsAtStop) = RunLiveReadsUntilStop((_, cts) => cts.Cancel());

        reads.Should().BeGreaterThanOrEqualTo(3, "the live path read the VRAM while the run emitted frames");
        reads.Should().Be(readsAtStop, "no VRAM read runs from the stop request until the pump ends");
    }

    [Fact]
    public void LiveVBlankReads_StopByDispose_ReadNothingAfterTheStopRequest()
    {
        // The same check for the dispose path: Dispose sets the flag through the same gate as RequestCancel, then cancels the token on the pool.
        var (reads, readsAtStop) = RunLiveReadsUntilStop((engine, _) => engine.Dispose());

        reads.Should().BeGreaterThanOrEqualTo(3, "the live path read the VRAM while the run emitted frames");
        reads.Should().Be(readsAtStop, "no VRAM read runs from the dispose until the pump ends");
    }

    /// <summary>A synthetic engine with the live frame slot, for tests that drive the engine's gate or segment directly.</summary>
    private static RecompiledHostExecutionEngine SyntheticEngine(TempDirectory dir, LiveFrameSlot? slot)
    {
        var words = SyntheticLiveProgram.Words();
        var entry = SyntheticLiveProgram.EntryPc;
        var program = ReachableProgramBuilder.Build(entry, words, entry, []);
        return new RecompiledHostExecutionEngine(
            program,
            words,
            entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardSink(), reader, writer),
            embedImage: true,
            liveFrames: slot);
    }

    [Fact]
    public void ExternalCancelAlone_ReadsNothingAfterTheCancel_WithoutAnyRequestCancel()
    {
        // Review round 12 (MEDIUM 1): the window close cancels the external token only. The engine is driven directly (no orchestrator), so no
        // RequestCancel ever runs. The live VBlank reads still stop at the cancel: the gate checks the external token under its lock.
        using var dir = new TempDirectory();
        var slot = new LiveFrameSlot();
        using var engine = SyntheticEngine(dir, slot);
        var entry = SyntheticLiveProgram.EntryPc;
        var gpr = new uint[TitleExecutionRequest.GprCount];
        engine.Load(new TitleExecutionRequest(entry, gpr, 0, 0, [], outerBudget: 1, segmentBudget: 20_000_000u));
        using var cts = new CancellationTokenSource();
        using var attachment = engine.AttachCancellation(cts.Token);
        using var readerDone = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var last = 0L;
            var spin = new SpinWait();
            while (!readerDone.IsCancellationRequested)
            {
                if (slot.TryGetNewer(last, out var frame))
                {
                    last = frame.Sequence;
                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }
        });

        var segment = Task.Run(() => engine.RunSegment(new TitleExecutionSegmentRequest(gpr, 0, 0, entry, 20_000_000u)));
        int readsAtCancel;
        try
        {
            WaitUntil(() => engine.FrameReadStats.Reads >= 3 || segment.IsCompleted, StopWait);
            segment.IsCompleted.Should().BeFalse("the segment still emits VBlank frames when the external token is cancelled");
            cts.Cancel();

            // Taken after the cancel: a read that had started before it is counted (the stats wait for the gate); every later read is refused.
            readsAtCancel = engine.FrameReadStats.Reads;
            segment.Wait(StopWait).Should().BeTrue("the segment ends by its own budget (nothing kills it here)");
        }
        finally
        {
            readerDone.Cancel();
            reader.Wait(StopWait).Should().BeTrue("the reader ended within the test's bound");
        }

        readsAtCancel.Should().BeGreaterThanOrEqualTo(3, "the live path read the VRAM before the cancel");
        engine.FrameReadStats.Reads.Should().Be(readsAtCancel, "no VRAM read runs after the external cancel, through the end of the segment");
    }

    [Fact]
    public void LiveReadGuard_RefusesEveryRead_AfterAnExternalCancelAlone()
    {
        // Review round 12 (MEDIUM 2): the guard called directly. It reads before the cancel and refuses after it, with the external token alone.
        using var dir = new TempDirectory();
        using var engine = SyntheticEngine(dir, new LiveFrameSlot());
        using var cts = new CancellationTokenSource();
        using var attachment = engine.AttachCancellation(cts.Token);
        var reads = 0;

        engine.LiveReadGuardForTest(() => reads++).Should().BeTrue("before the cancel the guard reads");
        cts.Cancel();
        engine.LiveReadGuardForTest(() => reads++).Should().BeFalse("after the external cancel the guard refuses");

        reads.Should().Be(1, "only the read before the cancel ran");
        engine.FrameReadStats.Reads.Should().Be(1);
    }

    [Fact]
    public void LiveReadGuard_RefusesEveryRead_AfterDispose()
    {
        // Review round 12 (MEDIUM 2): Dispose sets the flag through the gate, so the guard refuses at once.
        using var dir = new TempDirectory();
        var engine = SyntheticEngine(dir, new LiveFrameSlot());
        var reads = 0;

        engine.LiveReadGuardForTest(() => reads++).Should().BeTrue("before the dispose the guard reads");
        engine.Dispose();
        engine.LiveReadGuardForTest(() => reads++).Should().BeFalse("after the dispose the guard refuses");

        reads.Should().Be(1);
    }

    [Fact]
    public void LiveReadGuard_RefusesEveryRead_AfterRequestCancel()
    {
        // Review round 12 (MEDIUM 2): the request flag alone refuses the guard's reads.
        using var dir = new TempDirectory();
        using var engine = SyntheticEngine(dir, new LiveFrameSlot());
        var reads = 0;

        engine.LiveReadGuardForTest(() => reads++).Should().BeTrue("before the request the guard reads");
        engine.RequestCancel();
        engine.LiveReadGuardForTest(() => reads++).Should().BeFalse("after the request the guard refuses");

        reads.Should().Be(1);
        engine.FrameReadStats.Reads.Should().Be(1);
    }

    [Fact]
    public void StopDuringADrain_OfAChildThatEndedFirst_KeepsItsCompletedResult()
    {
        // Review round 12 (MEDIUM 3): the child ends first, and a grandchild holds its stdout for about seven seconds. The stop arrives during the drain,
        // while the child is already gone. The drain keeps the run's own budget (60 s here): the output arrives at about seven seconds, and the result
        // stays Completed. A stop cap of five seconds would have timed the drain out at about 5.3 s.
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "heldout");
        var name = Path.GetFileNameWithoutExtension(copy);
        var (fileName, arguments) = SnapshotChild(dir, [$"start /b \"\" \"{copy}\" -n 8 127.0.0.1"], [$"\"{copy}\" 7 &"]);
        try
        {
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                // The stop lands after the child has started and ended: its grandchild (the holder of stdout) is running, and the child has exited.
                WaitUntil(() => ProcessNameProbe.IsRunning(name), StopWait);
                SpinFor(TimeSpan.FromMilliseconds(300));
                cts.Cancel();
            });

            var clock = Stopwatch.StartNew();
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                fileName,
                arguments,
                timeoutMs: 60_000,
                cts.Token,
                descendantsProbe: _ => Array.Empty<Process>(),
                outputBudgetMs: 60_000);
            stopper.Wait(StopWait).Should().BeTrue("the stopper finished within the test's bound");

            outcome.Result.Status.Should().Be(RecompilerExecutionStatus.Completed, "the child ended first: its own result stands");
            outcome.Result.DiagnosticCode.Should().BeNull("the stop did not turn the output into a drain timeout");
            outcome.Refused.Should().BeNull("the child ended before the stop reached it");
            clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(5.5), "the drain waited past the five-second cap: the output had not arrived yet");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [Fact]
    public void PreStartStop_IsRecordedAsACleanShutdown()
    {
        // Review round 12 (LOW): a stop that began before the child started is Cancelled, and its diagnostics say that nothing was left behind.
        using var dir = new TempDirectory();
        using var engine = SyntheticEngine(dir, null);
        var entry = SyntheticLiveProgram.EntryPc;
        var gpr = new uint[TitleExecutionRequest.GprCount];
        engine.Load(new TitleExecutionRequest(entry, gpr, 0, 0, [], outerBudget: 1, segmentBudget: 20_000u));
        engine.RequestCancel();
        WaitUntil(() => engine.StopIsRequestedForTest, StopWait);

        var result = engine.RunSegment(new TitleExecutionSegmentRequest(gpr, 0, 0, entry, 20_000u));

        result.Status.Should().Be(RecompilerExecutionStatus.Cancelled);
        engine.ShutdownStatus.Should().Be(ShutdownOutcome.Clean, "a stop was processed, and it left nothing behind");
    }

    /// <summary>Discards the guest's TTY bytes; the test only needs the Runtime to run.</summary>
    private sealed class DiscardSink : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }
}
