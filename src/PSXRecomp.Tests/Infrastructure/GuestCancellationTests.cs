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
    public async Task RealArtifact_DescendantSurvivingTheKill_EndsRefused_AndIsNotReportedAsCancelled()
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

            result.State.Should().NotBe(TitleExecutionState.Cancelled, "a survivor means the stop is not confirmed");
            result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode);
            result.DiagnosticMessage.Should().Contain(sleeper.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
    public void OrphanGrandchildHoldingTheOutputPipe_StopReturnsWithinItsBound_AndIsRefused()
    {
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "orphan");
        // The shell exits at once and leaves the sleeper running, holding the pipe the stop must drain.
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "start", "", "/b", copy, "-n", "60", "127.0.0.1" })
            : ("/bin/sh", new[] { "-c", $"{copy} 60 &" });
        try
        {
            using var cts = new CancellationTokenSource();
            var stopper = Task.Run(() =>
            {
                WaitUntil(() => ProcessNameProbe.IsRunning(Path.GetFileNameWithoutExtension(copy)), StopWait);

                // The shell has exited by then, so the stop reaches no live root: the orphan is outside any tree the stop can kill.
                SpinFor(TimeSpan.FromSeconds(1));
                cts.Cancel();
            });

            var clock = Stopwatch.StartNew();
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(fileName, arguments, timeoutMs: 60_000, cts.Token);
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "a pipe held by a grandchild must not hold the stop past its bound");
            stopper.Wait(StopWait);

            // The orphan is either found and reaped (a leftover, on Windows) or it keeps the pipe (a refusal). Either way it is recorded.
            outcome.Confirmed.Should().BeFalse("a leftover or a held pipe is not a clean stop");
            (outcome.Leftover ?? outcome.Refused).Should().NotBeNull("the surviving orphan must be recorded, not reported as a clean end");
        }
        finally
        {
            KillNamed(copy);
        }
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
            stopper.Wait(StopWait);

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
    public void ACompletedRun_KeepsItsResult_EvenWhenAStopIsRefusedAfterIt()
    {
        var stop = new StopRecord();
        stop.RefuseIfUnset("a refusal that arrived after the run had completed");
        stop.Seal();
        var completed = new RecompilerExecutionResult(RecompilerExecutionStatus.Completed, null, null, null);

        RecompiledHostExecutionEngine.ApplyStopOutcome(completed, stop).Should().BeSameAs(completed);
    }

    [Fact]
    public async Task RealArtifact_DescendantThatCannotBeChecked_EndsRefused_NotAsAPlainFailure()
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

            result.State.Should().NotBe(TitleExecutionState.Cancelled, "an unchecked descendant is not confirmed");
            result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode);
            result.DiagnosticMessage.Should().Contain("could not be checked");
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
    public async Task RealArtifact_KillRefusedByTheOs_EndsRefused_NotAsARawException()
    {
        // Failure injection (Issue #827 review, MEDIUM 1): the OS refuses every kill (access denied). The injection counts the kill
        // attempts, so the test shows which paths were reached: the stop's own kill and the kill after the stop. Neither may escape as a
        // raw exception; the result is a refused stop with the reason.
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
            using var cts = new CancellationTokenSource();
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

            result.State.Should().NotBe(TitleExecutionState.Cancelled, "a refused kill is not a confirmed stop");
            result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.StopRefusedDiagnosticCode);
            result.DiagnosticMessage.Should().Contain("kill was refused");
            kills.Should().BeGreaterThanOrEqualTo(2, "the stop's own kill and the kill after the stop were both attempted");
            ArtifactProcessRunning(binary).Should().BeTrue("the OS refused the kill, so the child is still running, and the result says so");
        }
        finally
        {
            KillNamed(binary);
        }
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
                    child.WaitForExit();
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

    /// <summary>
    /// The shell starts the sleeper detached and then stays alive for about three seconds, so the sleeper is a tracked child of a live root
    /// (Windows; Linux reparents an orphan to init, which the tracking does not follow: unverified, see the PR).
    /// </summary>
    private static string[] DetachedSleeperThenRootStaysAlive(string copy) =>
        ["/c", "start", "/b", copy, "-n", "60", "127.0.0.1", "1>nul", "&", "ping", "-n", "3", "127.0.0.1", ">nul"];

    [SkippableFact]
    public void LeftoverDescendant_OfARootThatEndedOnItsOwn_IsReapedAndRecorded()
    {
        // Issue #827 review (MEDIUM 3, natural end): the root ends on its own while a child it started is still running. The child was tracked
        // while the root was alive; at the root's end it is reaped through its own handle and recorded. The run keeps its own exit (0).
        Skip.IfNot(OperatingSystem.IsWindows(), "a child is found by its parent's id on Windows only; Linux reparents an orphan");
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "leftover");
        var name = Path.GetFileNameWithoutExtension(copy);
        try
        {
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped("cmd.exe", DetachedSleeperThenRootStaysAlive(copy), 60_000, CancellationToken.None);

            outcome.TimedOut.Should().BeFalse();
            outcome.Leftover.Should().NotBeNull("a child still running after its root ended must be recorded");
            outcome.Leftover.Should().Contain("still running after its owner ended (killed)");
            outcome.Confirmed.Should().BeFalse("no stop was requested");
            ProcessNameProbe.IsRunning(name).Should().BeFalse("the leftover is reaped at the root's end");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [SkippableFact]
    public void LeftoverDescendant_WhoseIdentityChanged_IsNotKilled()
    {
        // Issue #827 review (MEDIUM 1): a tracked process is killed only when its identity still holds. The guard stands in for a process id
        // that has been reused: the tracked process is not killed, and the run records why.
        Skip.IfNot(OperatingSystem.IsWindows(), "a child is found by its parent's id on Windows only; Linux reparents an orphan");
        using var dir = new TempDirectory();
        var copy = NamedCopy(dir, SleepBinary, "identity");
        var name = Path.GetFileNameWithoutExtension(copy);
        try
        {
            var outcome = RecompiledHostExecutionEngine.RunChildUntilStopped(
                "cmd.exe",
                DetachedSleeperThenRootStaysAlive(copy),
                60_000,
                CancellationToken.None,
                reapGuard: _ => false);

            outcome.Leftover.Should().Contain("identity changed, not killed");
            ProcessNameProbe.IsRunning(name).Should().BeTrue("a process whose identity changed is not killed");
        }
        finally
        {
            KillNamed(copy);
        }
    }

    [SkippableFact]
    public void GrandchildOfAnIntermediateThatEndedFirst_IsTrackedWhileAliveAndReaped()
    {
        // Issue #827 review (three levels): root (cmd) -> middle (a copy of cmd) -> grandchild (sleeper). The middle ends first, while the root
        // is alive; the grandchild's parent id then names nothing the root can reach. The grandchild was tracked while the middle was alive,
        // so it is found at the root's end, reaped through its own handle, and recorded.
        Skip.IfNot(OperatingSystem.IsWindows(), "the three-level case depends on parent ids that Linux does not keep for orphans");
        using var dir = new TempDirectory();
#pragma warning disable AARC003 // Test-layer process fixture: the system shell is copied under a unique name (as the other fixtures do)
        var middle = NamedCopy(dir, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "middle");
#pragma warning restore AARC003
        var grandchild = NamedCopy(dir, SleepBinary, "grand");
        var grandName = Path.GetFileNameWithoutExtension(grandchild);
        var middleName = Path.GetFileNameWithoutExtension(middle);
        try
        {
            var run = Task.Run(() => RecompiledHostExecutionEngine.RunChildUntilStopped(
                "cmd.exe",
                ["/c", middle, "/c", grandchild, "-n", "60", "127.0.0.1"],
                60_000,
                CancellationToken.None));

            // The grandchild runs under the middle; the sampling runs for half a second while both are alive.
            WaitUntil(() => ProcessNameProbe.IsRunning(grandName), StopWait);
            SpinFor(TimeSpan.FromMilliseconds(500));

            // The middle ends first, by itself only (not its tree): the grandchild is orphaned and keeps running. The root ends with the
            // middle, as its only child.
            KillOnlyNamed(middle);
            var outcome = run.Result;

            outcome.Leftover.Should().NotBeNull("the grandchild outlived its middle and must be recorded");
            outcome.Leftover.Should().Contain($"descendant process");
            outcome.Confirmed.Should().BeFalse();
            ProcessNameProbe.IsRunning(grandName).Should().BeFalse("the grandchild is reaped through its own handle");
        }
        finally
        {
            KillNamed(grandchild);
            KillNamed(middle);
        }
    }

#pragma warning restore AARC003

    /// <summary>Discards the guest's TTY bytes; the test only needs the Runtime to run.</summary>
    private sealed class DiscardSink : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }
}
