using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;

namespace PSXRecomp.Tests.Infrastructure;

[Test]
public sealed class InImageTransferDiagnosticTests
{
    private const uint Load = 0x80010000u;
    private const int Words = 8; // image [0x80010000, 0x80010020)

    private static TitleExecutionResult Stop(uint pc, string code = "UNRESOLVED_TRANSFER") => new(
        TitleExecutionState.UnsupportedTransfer,
        new RecompilerStateSnapshot(new uint[32], 0, 0, pc),
        0,
        "engine",
        code,
        "original");

    private static RecompilerIrProgram ProgramWithBlockAt(params uint[] entries) => new(
        entries.Select(pc => new RecompilerIrBlock(
            pc, [], new RecompilerIrExit(RecompilerIrTerminationReason.Success, null))));

    [Theory]
    [InlineData(0x80010000u)]
    [InlineData(0x8001001Cu)]
    public void AlignedUncompiledPcInsideTheImage_IsRelabelled(uint pc)
    {
        var result = InImageTransferDiagnostic.Apply(Stop(pc), ProgramWithBlockAt(0x80010004u), Load, Words);

        result.DiagnosticCode.Should().Be(InImageTransferDiagnostic.Code);
        result.DiagnosticMessage.Should().Contain($"0x{pc:X8}").And.Contain("--entry-root");
        result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
    }

    [Theory]
    [InlineData(0x80010020u)] // first byte past the image
    [InlineData(0x8000FFFCu)] // before the image
    [InlineData(0x80010002u)] // in the image but not instruction aligned
    public void PcOutsideTheImageOrUnaligned_KeepsTheOriginalDiagnostic(uint pc)
    {
        var stop = Stop(pc);

        InImageTransferDiagnostic.Apply(stop, ProgramWithBlockAt(), Load, Words).Should().Be(stop);
    }

    [Fact]
    public void PcThatHasACompiledBlock_IsNotRelabelled()
    {
        var stop = Stop(0x80010008u);

        InImageTransferDiagnostic.Apply(stop, ProgramWithBlockAt(0x80010008u), Load, Words).Should().Be(stop);
    }

    [Fact]
    public void OtherDiagnosticCodes_AreNeverTouched()
    {
        var stop = Stop(0x8001000Cu, code: "BIOS_HLE_UNSUPPORTED_CALL");

        InImageTransferDiagnostic.Apply(stop, ProgramWithBlockAt(), Load, Words).Should().Be(stop);
    }
}
