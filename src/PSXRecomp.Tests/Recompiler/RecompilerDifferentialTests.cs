using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable PSXR005

[Test]
public sealed class RecompilerDifferentialTests
{
    private static RecompilerStateSnapshot Snapshot(
        uint gpr8 = 0, uint gpr9 = 0, uint gpr11 = 0,
        uint hi = 0, uint lo = 0, uint pc = 0x80000000u,
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success)
    {
        var gpr = new uint[32];
        gpr[0] = 0;
        gpr[8] = gpr8;
        gpr[9] = gpr9;
        gpr[11] = gpr11;
        return new RecompilerStateSnapshot(gpr, hi, lo, pc, termination: termination);
    }

    [Fact]
    public void Equal_States_Produce_Match()
    {
        var a = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12);
        var b = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12);

        var diff = RecompilerStateDiff.Compare(a, b);

        Assert.Equal(RecompilerComparisonClassification.Match, diff.Classification);
        Assert.True(diff.IsMatch);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void OneGprMismatch_Produces_Mismatch_With_Location_And_Value()
    {
        var a = Snapshot(gpr8: 2, gpr9: 3);
        var b = Snapshot(gpr8: 99, gpr9: 3);

        var diff = RecompilerStateDiff.Compare(a, b);

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsMatch);
        var difference = Assert.Single(diff.Differences);
        Assert.Equal("gpr[8]", difference.FieldPath);
        Assert.Equal("0x00000002", difference.ExpectedText);
        Assert.Equal("0x00000063", difference.ActualText);
    }

    [Fact]
    public void HiLoPcAndTermination_Mismatches_Are_Reported()
    {
        var reference = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12);

        var oddHi = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12, hi: 0x1111);
        var diffWithHi = RecompilerStateDiff.Compare(reference, oddHi);
        Assert.Single(diffWithHi.Differences);
        Assert.Equal("hi", diffWithHi.Differences[0].FieldPath);

        var oddTerm = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12, pc: 0x80000004u, termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded);
        var diffWithPcTerm = RecompilerStateDiff.Compare(reference, oddTerm);
        Assert.True(diffWithPcTerm.Differences.Any(d => d.FieldPath == "pc"));
        Assert.True(diffWithPcTerm.Differences.Any(d => d.FieldPath == "termination"));
    }

    [Fact]
    public void Describe_And_MachineReadable_Are_Deterministic_For_Same_Diff()
    {
        var a = Snapshot(gpr8: 2);
        var b = Snapshot(gpr8: 3);

        var first = RecompilerStateDiff.Compare(a, b);
        var second = RecompilerStateDiff.Compare(a, b);

        Assert.Equal(first.Describe(), second.Describe());
        Assert.Equal(first.ToMachineReadable(), second.ToMachineReadable());
    }

    [Fact]
    public void Matching_Executors_Produce_Match()
    {
        var fixture = RecompilerFixtures.AddThree();
        var diff = RecompilerDifferentialRunner.Run(fixture, new StubExecutor(0), new StubExecutor(0));

        Assert.True(diff.BothCompleted);
        Assert.True(diff.IsMatch);
        Assert.Empty(diff.Diff!.Differences);
    }

    [Fact]
    public void Intentional_SemanticDivergence_Produces_Mismatch_And_Fails()
    {
        // Negative proof: an executor that genuinely diverges (flips GPR8) must be
        // detected by the harness as a MISMATCH with the diverging field localized.
        var fixture = RecompilerFixtures.AddThree();
        var reference = new StubExecutor(0);
        var corrupter = new StubExecutor(gpr8Override: 0xDEADBEEF);

        var result = RecompilerDifferentialRunner.Run(fixture, reference, corrupter);

        Assert.True(result.Reference.Status == RecompilerExecutionStatus.Completed);
        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed);
        Assert.True(result.BothCompleted);
        Assert.False(result.IsMatch);
        var difference = Assert.Single(result.Diff!.Differences);
        Assert.Equal("gpr[8]", difference.FieldPath);
        Assert.Equal("0x00000005", difference.ExpectedText);
        Assert.Equal("0xDEADBEEF", difference.ActualText);
    }

    [Fact]
    public void GenerationFailure_Is_Reported_And_Produces_No_BothCompleted()
    {
        var fixture = RecompilerFixtures.AddThree();
        var failing = new AlwaysFailingExecutor();

        var result = RecompilerDifferentialRunner.Run(fixture, new StubExecutor(0), failing);

        Assert.Equal(RecompilerExecutionStatus.GenerationFailed, result.Actual.Status);
        Assert.False(result.BothCompleted);
        Assert.False(result.IsMatch);
    }

    private sealed class StubExecutor : IRecompilerExecutor
    {
        private readonly uint _gpr8Override;

        public StubExecutor(uint gpr8Override = 0)
            => _gpr8Override = gpr8Override;

        public string Name => _gpr8Override == 0 ? "stub-faithful" : "stub-corrupting";

        public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
        {
            var gpr = new uint[32];
            gpr[0] = 0;
            gpr[8] = 5;
            gpr[9] = 7;
            gpr[11] = 12;
            if (_gpr8Override != 0) gpr[8] = _gpr8Override;
            var snapshot = new RecompilerStateSnapshot(
                gpr, hi: 0, lo: 0,
                pc: fixture.PcOfInstruction(fixture.Instructions.Count),
                termination: RecompilerIrTerminationReason.Success);
            return RecompilerExecutionResult.Completed(snapshot);
        }
    }

    private sealed class AlwaysFailingExecutor : IRecompilerExecutor
    {
        public string Name => "stub-failing";

        public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
            => RecompilerExecutionResult.Failed(RecompilerExecutionStatus.GenerationFailed, "LOWER_FAILED", "boom");
    }
}
#pragma warning restore PSXR005
