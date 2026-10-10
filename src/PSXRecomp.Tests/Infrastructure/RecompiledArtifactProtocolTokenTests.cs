using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Infrastructure;

[Test]
public sealed class RecompiledArtifactProtocolTokenTests
{
    [Theory]
    [InlineData("Tfoo 1\nN")]
    [InlineData("T 1\nNfoo")]
    [InlineData("T 1\nDfoo 0 2147487744 0 0")]
    [InlineData("T 1\nunknown")]
    [InlineData("T 1\nRfoo 0")]
    [InlineData("T 1\nWfoo 0 0")]
    [InlineData("T 1\nCfoo 0")]
    [InlineData("T 1\nT 2\nN")]
    public void InvalidTransferReply_FailsBeforeAnyGuestInstruction(string response)
    {
        using var dir = new TempDirectory();
        Run(Program([MipsEncoding.Nop]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", requireExactTime: true, transferReply: _ => response,
            closeAfterTransferReply: true);
        run.ExitCode.Should().Be(RecompiledArtifactCodeGen.RetiredProtocolExitCode);
        run.HasSnapshot.Should().BeFalse();
        run.Retired.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4294967296")]
    [InlineData("18446744073709551616")]
    [InlineData("malformed")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("1foo")]
    [InlineData("1e0")]
    [InlineData("1.0")]
    public void InvalidCreditNumericToken_FailsBeforeExecution(string credit)
    {
        using var dir = new TempDirectory();
        Run(Program([MipsEncoding.Nop]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", requireExactTime: true,
            transferReply: _ => $"T {credit}\nN", closeAfterTransferReply: true);
        run.ExitCode.Should().Be(RecompiledArtifactCodeGen.RetiredProtocolExitCode);
        run.HasSnapshot.Should().BeFalse();
        run.Retired.Should().BeEmpty();
    }

    [Theory]
    [InlineData("T 1\nT 2\nA")]
    [InlineData("T +1\nA")]
    [InlineData("T 1foo\nA")]
    public void InvalidRetiredReply_FailsWithoutSnapshot(string response)
    {
        using var dir = new TempDirectory();
        Run(Program([MipsEncoding.Nop]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", _ => response, eventCredit: 1,
            requireExactTime: true, sendCreditOnReports: false);
        run.ExitCode.Should().Be(RecompiledArtifactCodeGen.RetiredProtocolExitCode);
        run.HasSnapshot.Should().BeFalse();
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D 0 2147487744 0 0")]
    public void ValidExactTransferDecision_PreservesExecution(string decision)
    {
        using var dir = new TempDirectory();
        Run(Program([MipsEncoding.Nop]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", eventCredit: 1, requireExactTime: true,
            transferReply: line => line == RecompiledArtifactCodeGen.ProtocolInitLine ? decision : "N");
        run.ExitCode.Should().Be(0);
        run.HasSnapshot.Should().BeTrue();
        run.Retired.Sum(static n => (long)n).Should().Be(2);
    }
}
