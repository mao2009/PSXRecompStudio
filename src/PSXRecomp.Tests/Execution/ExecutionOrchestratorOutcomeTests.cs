using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class ExecutionOrchestratorOutcomeTests
{
    private const uint Entry = 0x80001000u;

    [Fact]
    public void ReturnHandoff_ReportsReturned()
    {
        using var engine = new SingleSnapshotEngine(RecompilerIrTerminationReason.Success);
        var handoff = new ConstantHandoff(TitleExecutionHandoffResult.Return());

        var result = new ExecutionOrchestrator().Execute(engine, handoff, Request());

        Assert.Equal(TitleExecutionState.Returned, result.State);
        Assert.Equal((uint)1, result.SegmentsRetired);
        Assert.Null(result.DiagnosticCode);
    }

    [Fact]
    public void PauseHandoff_ReportsRuntimeHandoff()
    {
        using var engine = new SingleSnapshotEngine(RecompilerIrTerminationReason.Success);
        var handoff = new ConstantHandoff(TitleExecutionHandoffResult.Pause());

        var result = new ExecutionOrchestrator().Execute(engine, handoff, Request());

        Assert.Equal(TitleExecutionState.RuntimeHandoff, result.State);
        Assert.Equal((uint)1, result.SegmentsRetired);
        Assert.Null(result.DiagnosticCode);
    }

    [Theory]
    [InlineData(RecompilerIrTerminationReason.UnsupportedInstruction)]
    [InlineData(RecompilerIrTerminationReason.UnsupportedIr)]
    [InlineData(RecompilerIrTerminationReason.UnsupportedMemory)]
    [InlineData(RecompilerIrTerminationReason.UnsupportedMmio)]
    [InlineData(RecompilerIrTerminationReason.StateMismatch)]
    public void UnsupportedOrMismatchTermination_IsRuntimeFailure(RecompilerIrTerminationReason termination)
    {
        using var engine = new SingleSnapshotEngine(termination);

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request());

        Assert.Equal(TitleExecutionState.RuntimeFailure, result.State);
        Assert.Equal(termination.ToString(), result.DiagnosticCode);
        Assert.Equal((uint)1, result.SegmentsRetired);
    }

    private static TitleExecutionRequest Request() =>
        new(
            entryPc: Entry,
            initialGpr: new uint[TitleExecutionRequest.GprCount],
            initialHi: 0,
            initialLo: 0,
            initialMemory: Array.Empty<RecompilerInitialMemoryItem>(),
            outerBudget: 1,
            segmentBudget: 8);

    private sealed class ConstantHandoff(TitleExecutionHandoffResult result) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) => result;
    }

    private sealed class SingleSnapshotEngine(RecompilerIrTerminationReason termination) : IRecompiledExecutionEngine
    {
        public string Name => "outcome-test-engine";

        public void Load(TitleExecutionRequest request)
        {
        }

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
        {
            var snapshot = new RecompilerStateSnapshot(
                segmentRequest.Gpr,
                segmentRequest.Hi,
                segmentRequest.Lo,
                Entry + 4,
                termination: termination);
            return RecompilerExecutionResult.Completed(snapshot);
        }

        public void Dispose()
        {
        }
    }
}
