using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable PSXR005

[Test]
public sealed class RecompilerStageAEndToEndTests
{
    [Fact]
    public void Interpreter_And_RecompiledHost_Match_On_AddThree()
    {
        var fixture = RecompilerFixtures.AddThree();
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        Assert.Equal(RecompilerExecutionStatus.Completed, result.Reference.Status);
        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"host executor failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        Assert.True(result.IsMatch, result.Diff!.Describe());

        // Sanity: the fixture produced the expected arithmetic result.
        Assert.Equal(12u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(12u, result.Actual.Snapshot!.Gpr[11]);
    }

    [Fact]
    public void Recompiled_Host_Snapshots_Are_Deterministic_Across_Independent_Runs()
    {
        var fixture = RecompilerFixtures.AddThree();
        var executor = new RecompilerHostExecutor();

        var first = executor.Execute(fixture);
        var second = executor.Execute(fixture);

        Assert.Equal(RecompilerExecutionStatus.Completed, first.Status);
        Assert.Equal(RecompilerExecutionStatus.Completed, second.Status);
        Assert.True(first.Snapshot is not null && second.Snapshot is not null);

        var diff = RecompilerStateDiff.Compare(first.Snapshot!, second.Snapshot!);
        Assert.True(diff.IsMatch, diff.Describe());
        Assert.Equal(first.Snapshot!.Termination, second.Snapshot!.Termination);
        Assert.Equal(first.Snapshot!.PC, second.Snapshot!.PC);
        Assert.Equal(first.Snapshot!.HI, second.Snapshot!.HI);
        Assert.Equal(first.Snapshot!.LO, second.Snapshot!.LO);
    }

    [Fact]
    public void Host_Source_Is_Valid_C_And_Deterministic()
    {
        var fixture = RecompilerFixtures.AddThree();
        var program = MipsToIrLowerer.LowerProgram(Decoded(fixture));

        var first = RecompilerHostCodeGen.Generate(program);
        var second = RecompilerHostCodeGen.Generate(program);

        Assert.True(first.Success, first.DiagnosticMessage);
        Assert.Equal(first.Source, second.Source);
    }

    private static List<(R3000aInstruction Instruction, uint EntryPc)> Decoded(RecompilerDifferentialFixture fixture)
    {
        var decoded = new List<(R3000aInstruction, uint)>();
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            decoded.Add((R3000aDecoder.Decode(fixture.Instructions[i]), fixture.PcOfInstruction(i)));
        }
        return decoded;
    }
}
#pragma warning restore PSXR005
