using System.Globalization;
using System.Text.RegularExpressions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public sealed class RecompiledArtifactCodeGenTests
{
    private static RecompilerHostCodeGenResult GenerateDispatch()
    {
        var instruction = R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = MipsToIrLowerer.LowerProgram([(instruction, 0x80010000u)]);
        return RecompilerHostCodeGen.Generate(program);
    }

    [Fact]
    public void Generate_ValidDispatch_AppendsProductionDriver()
    {
        var dispatch = GenerateDispatch();
        dispatch.Success.Should().BeTrue();

        var result = RecompiledArtifactCodeGen.Generate(dispatch);

        result.Success.Should().BeTrue();
        result.Source.Should().StartWith(dispatch.Source!);
        result.Source.Should().Contain("int main(int argc, char** argv)");
        result.Source.Should().Contain(RecompiledArtifactCodeGen.SnapshotBeginMarker);
        result.Source.Should().Contain(RecompiledArtifactCodeGen.SnapshotEndMarker);
        result.Source.Should().Contain(RecompiledArtifactCodeGen.HostTransferFlag);
        result.Source.Should().Contain("return (int)state.termination_reason;");
    }

    [Fact]
    public void Generate_IsDeterministic_ForIdenticalInput()
    {
        var dispatch = GenerateDispatch();

        var first = RecompiledArtifactCodeGen.Generate(dispatch);
        var second = RecompiledArtifactCodeGen.Generate(dispatch);

        first.Source.Should().Be(second.Source);
    }

    [Fact]
    public void Generate_UpstreamCodegenFailure_PropagatesAsFailure()
    {
        var failed = new RecompilerHostCodeGenResult(false, null, "SOME_CODE", "some message");

        var result = RecompiledArtifactCodeGen.Generate(failed);

        result.Success.Should().BeFalse();
        result.Source.Should().BeNull();
        result.DiagnosticCode.Should().Be("SOME_CODE");
    }

    [Fact]
    public void Generate_DriverInitLimit_IsEmittedFromMaxInitEntries_SoTheCBoundCannotDrift()
    {
        var dispatch = GenerateDispatch();

        var source = RecompiledArtifactCodeGen.Generate(dispatch).Source!;

        // The driver's PSX_MAX_INIT is emitted from MaxInitEntries (a verbatim
        // string cannot interpolate, so Generate substitutes it) — the emitted C
        // bound must equal the C# constant, in both directions, or this test
        // fails and forces the drift to be resolved deliberately.
        var emitted = Regex.Match(source, @"#define PSX_MAX_INIT (\d+)u");
        emitted.Success.Should().BeTrue("the driver must define PSX_MAX_INIT in the generated source");
        uint.Parse(emitted.Groups[1].Value, CultureInfo.InvariantCulture)
            .Should().Be((uint)RecompiledArtifactCodeGen.MaxInitEntries);
        source.Should().Contain($"#define PSX_MAX_INIT {RecompiledArtifactCodeGen.MaxInitEntries}u");
    }
}
