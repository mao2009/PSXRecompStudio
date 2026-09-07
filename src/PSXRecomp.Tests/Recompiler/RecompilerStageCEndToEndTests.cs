using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable PSXR005

[Test]
// Issue #209 Stage C vertical slice: control flow. BEQ (taken and not taken),
// J and JAL (each fusing its delay slot into the transfer block), a bounded
// backward BNE loop, and a budget-exhausted infinite loop proven to stop on the
// same state on both sides. The host budget counts retired host blocks while the
// interpreter budget counts retired MIPS instructions (a transfer + its delay
// slot is one block but two instructions).
public sealed class RecompilerStageCEndToEndTests
{
    [Fact]
    public void TakenBranch_SkipsTheFallThrough_AndRetiresItsDelaySlot()
    {
        var fixture = RecompilerFixtures.Issue209BranchTaken();
        var result = RunDifferential(fixture);

        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(0x80000018u, result.Reference.Snapshot!.PC);
        Assert.Equal(0x80000018u, result.Actual.Snapshot!.PC);

        // The delay slot always retires; the fall-through is skipped; the target runs.
        Assert.Equal(1u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(0u, result.Reference.Snapshot!.Gpr[12]);
        Assert.Equal(9u, result.Reference.Snapshot!.Gpr[13]);
        Assert.Equal(9u, result.Actual.Snapshot!.Gpr[13]);
    }

    [Fact]
    public void NotTakenBranch_FallsThroughAfterTheDelaySlot()
    {
        var fixture = RecompilerFixtures.Issue209BranchNotTaken();
        var result = RunDifferential(fixture);

        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(0x80000018u, result.Reference.Snapshot!.PC);

        Assert.Equal(1u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(0xBADu, result.Reference.Snapshot!.Gpr[12]);
        Assert.Equal(9u, result.Reference.Snapshot!.Gpr[13]);
        Assert.Equal(0xBADu, result.Actual.Snapshot!.Gpr[12]);
    }

    [Fact]
    public void Jump_TransfersWithoutALink_AndSkipsTheFallThrough()
    {
        var fixture = RecompilerFixtures.Issue209Jump();
        var result = RunDifferential(fixture);

        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(0x80000014u, result.Reference.Snapshot!.PC);

        Assert.Equal(1u, result.Reference.Snapshot!.Gpr[8]);
        Assert.Equal(2u, result.Reference.Snapshot!.Gpr[9]);   // the delay slot retires
        Assert.Equal(0u, result.Reference.Snapshot!.Gpr[10]);  // the fall-through is skipped
        Assert.Equal(3u, result.Reference.Snapshot!.Gpr[11]);  // the target runs
        Assert.Equal(3u, result.Actual.Snapshot!.Gpr[11]);
    }

    [Fact]
    public void Jal_LinksPcPlusEight_AndTransfersWithoutReturning()
    {
        var fixture = RecompilerFixtures.Issue209Jal();
        var result = RunDifferential(fixture);

        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(0x80000018u, result.Reference.Snapshot!.PC);

        Assert.Equal(0x8000000Cu, result.Reference.Snapshot!.Gpr[31]); // $ra = PC + 8
        Assert.Equal(2u, result.Reference.Snapshot!.Gpr[9]);           // the delay slot retires
        Assert.Equal(0u, result.Reference.Snapshot!.Gpr[10]);          // the return address word never runs
        Assert.Equal(3u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(4u, result.Reference.Snapshot!.Gpr[12]);
        Assert.Equal(0x8000000Cu, result.Actual.Snapshot!.Gpr[31]);
    }

    [Fact]
    public void BoundedBackwardLoop_CountsDown_ThenFallsThrough()
    {
        var fixture = RecompilerFixtures.Issue209BoundedLoop();
        var result = RunDifferential(fixture);

        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(0x8000001Cu, result.Reference.Snapshot!.PC);

        // Three iterations of the body: $t1 accumulates 10, 20, 30; $t0 counts 3, 2, 1, 0.
        Assert.Equal(0u, result.Reference.Snapshot!.Gpr[8]);
        Assert.Equal(30u, result.Reference.Snapshot!.Gpr[9]);
        Assert.Equal(99u, result.Reference.Snapshot!.Gpr[10]);
        Assert.Equal(30u, result.Actual.Snapshot!.Gpr[9]);
    }

    [Fact]
    public void UnboundedLoop_ExhaustsItsBudget_AndBothSidesMatchTheState()
    {
        var fixture = RecompilerFixtures.Issue209UnboundedLoop();
        var result = RunDifferential(fixture);

        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.True(result.BothCompleted);

        // Both sides must stop on the identical cut-off state rather than spin or
        // fall through: budget exhausted, PC parked back at the loop top.
        Assert.Equal(RecompilerIrTerminationReason.ExecutionBudgetExceeded, result.Reference.Snapshot!.Termination);
        Assert.Equal(RecompilerIrTerminationReason.ExecutionBudgetExceeded, result.Actual.Snapshot!.Termination);
        Assert.Equal(0x80000008u, result.Reference.Snapshot!.PC);
        Assert.Equal(0x80000008u, result.Actual.Snapshot!.PC);
        Assert.Equal(1u, result.Reference.Snapshot!.Gpr[8]);
        Assert.Equal(1u, result.Actual.Snapshot!.Gpr[8]);
    }

    [Fact]
    public void IndirectJump_FailsClosed_AsUnresolvedIndirectFlow_OnTheHost()
    {
        // The recompiled host cannot statically resolve a register-held target:
        // it must terminate with UnresolvedIndirectFlow (host classification):
        var fixture = RecompilerFixtures.Issue209IndirectJump();
        var actual = new RecompilerHostExecutor().Execute(fixture);

        Assert.Equal(RecompilerExecutionStatus.Completed, actual.Status);
        Assert.NotNull(actual.Snapshot);
        Assert.Equal(RecompilerIrTerminationReason.UnresolvedIndirectFlow, actual.Snapshot!.Termination);
        Assert.Equal(0x80000008u, actual.Snapshot!.PC);           // parked at the JR block entry
        Assert.Equal(0x80000014u, actual.Snapshot!.Gpr[8]);       // its setup instructions retired
    }

    [Fact]
    public void IndirectJump_IsFollowedByThe_Interpreter_SoItIsNotADifferentialMatch()
    {
        // This is why an indirect transfer is a classification check and not a
        // comparative one: the interpreter CAN follow $t0 (the JR target runs), so
        // the two sides permanently diverge at the transfer. Records the intent.
        var fixture = RecompilerFixtures.Issue209IndirectJump();
        var result = RecompilerDifferentialRunner.Run(
            fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.Equal(RecompilerExecutionStatus.Completed, result.Reference.Status);
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(RecompilerIrTerminationReason.UnresolvedIndirectFlow, result.Actual.Snapshot!.Termination);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void ControlFlowHostSnapshots_Are_Deterministic_Across_Independent_Runs()
    {
        var executor = new RecompilerHostExecutor();
        foreach (var fixture in new[]
                 {
                     RecompilerFixtures.Issue209BranchTaken(),
                     RecompilerFixtures.Issue209BranchNotTaken(),
                     RecompilerFixtures.Issue209Jump(),
                     RecompilerFixtures.Issue209Jal(),
                     RecompilerFixtures.Issue209BoundedLoop(),
                     RecompilerFixtures.Issue209UnboundedLoop(),
                     RecompilerFixtures.Issue209IndirectJump(),
                 })
        {
            var first = executor.Execute(fixture);
            var second = executor.Execute(fixture);

            Assert.True(first.Status == RecompilerExecutionStatus.Completed,
                $"[{fixture.Name}] host executor failed: [{first.DiagnosticCode}] {first.DiagnosticMessage}");
            Assert.True(first.Status == RecompilerExecutionStatus.Completed
                        && second.Status == RecompilerExecutionStatus.Completed);
            var diff = RecompilerStateDiff.Compare(first.Snapshot!, second.Snapshot!);
            Assert.True(diff.IsMatch, $"[{fixture.Name}]: {diff.Describe()}");
        }
    }

    private static RecompilerDifferentialResult RunDifferential(RecompilerDifferentialFixture fixture)
    {
        var result = RecompilerDifferentialRunner.Run(
            fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.Equal(RecompilerExecutionStatus.Completed, result.Reference.Status);
        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        return result;
    }
}
#pragma warning restore PSXR005