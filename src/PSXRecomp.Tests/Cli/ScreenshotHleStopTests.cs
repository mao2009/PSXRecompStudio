using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Runtime.Gpu;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Review of #751 (#798): the HLE capture core stop rules, driven by a scripted engine instead of a real ROM.
/// </summary>
[Test]
#pragma warning disable AARC003 // reads the output directory that the command under test wrote
public sealed class ScreenshotHleStopTests : IDisposable
{
    private const uint Entry = 0x80010000u;

    private readonly PSXCoreWrapper _core = new();
    private readonly GpuDevice _gpu = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly DeviceScheduler _scheduler;

    public ScreenshotHleStopTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
        _scheduler = new DeviceScheduler(_core, _interrupts, _gpu);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _gpu.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RuntimeHandoffReentry_ContinuesFromTheSnapshotWithoutReloadingTheEngine()
    {
        // A1 (MAJOR): a Runtime-boundary handoff must resume the live engine. Load resets RAM, GPU and the scheduler,
        // so the continuation must not call it again, and the registers and PC of the snapshot must carry over.
        using var dir = new TempDirectory();
        var engine = new ScriptedEngine((segment, request) =>
        {
            _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
            var gpr = request.Gpr.ToArray();
            if (segment == 1)
            {
                gpr[7] = 0x1234u;
                return RecompilerExecutionResult.Completed(new RecompilerStateSnapshot(
                    gpr, 0, 0, Entry + 0x40, termination: RecompilerIrTerminationReason.UnresolvedIndirectFlow));
            }
            return RecompilerExecutionResult.Completed(new RecompilerStateSnapshot(
                gpr, request.Hi, request.Lo, Entry + 0x80, termination: RecompilerIrTerminationReason.Success));
        });

        var result = ScreenshotCommand.RunScreenshotCaptureHle(
            engine, () => (_gpu, _scheduler), new ExitHandoff(), Request(), segmentBudget: 1000,
            dir.FullPath, vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        engine.LoadCount.Should().Be(1, "a continuation must not reload the engine (Load resets RAM, GPU and scheduler)");
        engine.Segments.Should().HaveCount(2);
        engine.Segments[1].Pc.Should().Be(Entry + 0x40);
        engine.Segments[1].Gpr[7].Should().Be(0x1234u, "the continuation carries the live register state");
        result.ExecutionResult.State.Should().Be(TitleExecutionState.Completed);
        result.MaxVblankReached.Should().Be(2, "both segments ran on the same scheduler");
    }

    [Fact]
    public void GoalFrameInTheSameSegmentAsAnAbnormalStop_IsAFailureNotAGoalStop()
    {
        // A2 (MAJOR): the goal frame and a CPU exception land in the same segment. The exception is the outcome.
        using var dir = new TempDirectory();
        var engine = new ScriptedEngine((segment, request) =>
        {
            _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
            return RecompilerExecutionResult.Completed(new RecompilerStateSnapshot(
                request.Gpr.ToArray(), request.Hi, request.Lo, Entry + 4,
                termination: RecompilerIrTerminationReason.Exception));
        });

        var result = ScreenshotCommand.RunScreenshotCaptureHle(
            engine, () => (_gpu, _scheduler), new ExitHandoff(), Request(), segmentBudget: 1000,
            dir.FullPath, vblankInterval: 1, maxScreenshots: 1, startVblank: 1, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.GoalReached.Should().BeTrue("the scenario needs the goal frame to be saved in that segment");
        result.ExecutionResult.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.StopReason.Should().Be("runtime_failure");
        ScreenshotCommand.IsSuccess(result).Should().BeFalse();
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("runtime_failure");
    }

    [Fact]
    public void RuntimeHandoffChain_IsBoundedByTheRequestBudgetForTheWholeRun()
    {
        // PR #798 review (MEDIUM): every continuation must spend the request's outer budget, not a fresh per-call budget.
        // With an outer budget of 3 and an engine that hands off forever, the run must stop after exactly 3 segments.
        using var dir = new TempDirectory();
        var engine = new ScriptedEngine((segment, request) =>
        {
            if (segment > 20) throw new InvalidOperationException("guard: the continuation ran past the request budget");
            return RecompilerExecutionResult.Completed(new RecompilerStateSnapshot(
                request.Gpr.ToArray(), request.Hi, request.Lo, Entry + 0x40,
                termination: RecompilerIrTerminationReason.UnresolvedIndirectFlow));
        });

        var result = ScreenshotCommand.RunScreenshotCaptureHle(
            engine, () => (_gpu, _scheduler), new ExitHandoff(), Request(outerBudget: 3), segmentBudget: 1000,
            dir.FullPath, vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        engine.Segments.Should().HaveCount(3, "the request budget (3 segments) bounds the whole run, not each continuation");
        result.ExecutionResult.State.Should().Be(TitleExecutionState.BudgetExhausted);
        result.StopReason.Should().Be("execution_budget");
        result.GoalReached.Should().BeFalse();
        ScreenshotCommand.IsSuccess(result).Should().BeFalse("a budget-exhausted run is not a success");
    }

    private static TitleExecutionRequest Request(uint outerBudget = 10) =>
        new(Entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget, 1000);

    private sealed class ExitHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) => TitleExecutionHandoffResult.Exit();
    }

    private sealed class ScriptedEngine(Func<int, TitleExecutionSegmentRequest, RecompilerExecutionResult> segment)
        : IRecompiledExecutionEngine
    {
        public string Name => "scripted-hle-stop-test";
        public int LoadCount { get; private set; }
        public List<TitleExecutionSegmentRequest> Segments { get; } = [];

        public void Load(TitleExecutionRequest request) => LoadCount++;

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
        {
            Segments.Add(segmentRequest);
            return segment(Segments.Count, segmentRequest);
        }

        public void Dispose()
        {
        }
    }
}
