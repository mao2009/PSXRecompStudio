using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class ExecutionOrchestratorContinuationValidationTests
{
    private const uint Entry = 0x80001000u;

    [Fact]
    public void ContinueAt_WithMisalignedTarget_IsInvalidState()
    {
        using var engine = new SingleSnapshotEngine();
        var handoff = new ConstantHandoff(
            TitleExecutionHandoffResult.ContinueAt(0x80001001u));

        var result = new ExecutionOrchestrator().Execute(engine, handoff, Request());

        Assert.Equal(TitleExecutionState.InvalidState, result.State);
        Assert.Equal("INVALID_CONTINUATION_TARGET", result.DiagnosticCode);
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

    private sealed class SingleSnapshotEngine : IRecompiledExecutionEngine
    {
        public string Name => "continuation-validation-engine";

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
                termination: RecompilerIrTerminationReason.Success);
            return RecompilerExecutionResult.Completed(snapshot);
        }

        public void Dispose()
        {
        }
    }
}
