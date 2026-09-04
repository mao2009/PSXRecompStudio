using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable PSXR005

[Test]
// Issue #209 Stage A vertical slice: the end-to-end ADDIU/ADDU fixture proves the
// recompiler slice (decode -> lower -> validate -> host codegen -> gcc -> run) is
// executable and matches the native interpreter, deterministically.
public sealed class RecompilerVerticalSliceTests
{
    [Fact]
    public void VerticalSlice_Matches_Interpreter_On_One_Plus_Two_Equals_Three()
    {
        var fixture = RecompilerFixtures.Issue209Add();
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        Assert.Equal(RecompilerExecutionStatus.Completed, result.Reference.Status);
        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        Assert.True(result.IsMatch, result.Diff!.Describe());

        // The sliced program leaves t2 (GPR10) = 1 + 2 = 3.
        Assert.Equal(1u, result.Reference.Snapshot!.Gpr[8]);
        Assert.Equal(2u, result.Reference.Snapshot!.Gpr[9]);
        Assert.Equal(3u, result.Reference.Snapshot!.Gpr[10]);
        Assert.Equal(3u, result.Actual.Snapshot!.Gpr[10]);
    }

    [Fact]
    public void VerticalSlice_RecompiledSnapshots_Are_Deterministic_Across_Independent_Runs()
    {
        var fixture = RecompilerFixtures.Issue209Add();
        var executor = new RecompilerHostExecutor();

        var first = executor.Execute(fixture);
        var second = executor.Execute(fixture);

        Assert.Equal(RecompilerExecutionStatus.Completed, first.Status);
        Assert.Equal(RecompilerExecutionStatus.Completed, second.Status);

        var diff = RecompilerStateDiff.Compare(first.Snapshot!, second.Snapshot!);
        Assert.True(diff.IsMatch, diff.Describe());
    }

    [Fact]
    public void VerticalSlice_Produced_Test_Binary_Is_Identical_Across_Runs()
    {
        // The issue asks to compare the produced test binary: build it once, run
        // it twice, and require byte-identical, stable output.
        var fixture = RecompilerFixtures.Issue209Add();
        var executor = new RecompilerHostExecutor();

        var compiled = executor.CompileRecompiledBinary(fixture);
        try
        {
            Assert.True(System.IO.File.Exists(compiled.BinaryPath));

            var first = executor.RunRecompiledBinary(compiled);
            var second = executor.RunRecompiledBinary(compiled);

            Assert.Equal(first, second);

            var parsed = SnapshotParser.Parse(first);
            Assert.NotNull(parsed);
            Assert.Equal(3u, parsed.Gpr[10]);
            Assert.Equal(RecompilerIrTerminationReason.Success, parsed.Termination);
        }
        finally
        {
            if (System.IO.Directory.Exists(compiled.DirectoryPath))
            {
                System.IO.Directory.Delete(compiled.DirectoryPath, true);
            }
        }
    }
}
#pragma warning restore PSXR005