using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aLinkSemanticsTests
{
    private static uint IType(uint opcode, uint rs, uint rt, ushort immediate)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | immediate;
    }

    private static uint RType(uint rs, uint rt, uint rd, uint shamt, uint funct)
    {
        return (0x00u << 26) | (rs << 21) | (rt << 16) | (rd << 11) | (shamt << 6) | funct;
    }

    public enum LinkInstruction
    {
        Jal = 0,
        JalrDefaultRa,
        JalrCustomRd,
        Bltzal,
        Bgezal,
    }

    private static R3000aInstruction DecodeLink(LinkInstruction instruction)
    {
        return instruction switch
        {
            LinkInstruction.Jal => R3000aDecoder.Decode(IType(0x03, rs: 0, rt: 0, immediate: 0x000400)),
            LinkInstruction.JalrDefaultRa => R3000aDecoder.Decode(RType(rs: 8, rt: 0, rd: 31, shamt: 0, funct: 0x09)),
            LinkInstruction.JalrCustomRd => R3000aDecoder.Decode(RType(rs: 8, rt: 0, rd: 10, shamt: 0, funct: 0x09)),
            LinkInstruction.Bltzal => R3000aDecoder.Decode(IType(0x01, rs: 8, rt: 0x10, immediate: 0xFFF4)),
            LinkInstruction.Bgezal => R3000aDecoder.Decode(IType(0x01, rs: 8, rt: 0x11, immediate: 0x0010)),
            _ => throw new ArgumentOutOfRangeException(nameof(instruction)),
        };
    }

    [Theory]
    [InlineData(LinkInstruction.Jal, (byte)31)]
    [InlineData(LinkInstruction.JalrDefaultRa, (byte)31)]
    [InlineData(LinkInstruction.JalrCustomRd, (byte)10)]
    [InlineData(LinkInstruction.Bltzal, (byte)31)]
    [InlineData(LinkInstruction.Bgezal, (byte)31)]
    public void TryGetLinkValue_AllFourInstructions_LinkValueIsPcPlusEight(LinkInstruction kind, byte expectedLinkRegister)
    {
        var instruction = DecodeLink(kind);

        var result = R3000aLinkSemantics.TryGetLinkValue(instruction, pc: 0x00001000, out var linkValue);

        result.Should().BeTrue();
        linkValue.Should().Be(0x00001008u);
        instruction.LinkInfo.WritesLink.Should().BeTrue();
        instruction.LinkInfo.LinkRegister.Should().Be(expectedLinkRegister);
    }

    [Theory]
    [InlineData(LinkInstruction.Jal)]
    [InlineData(LinkInstruction.JalrDefaultRa)]
    [InlineData(LinkInstruction.JalrCustomRd)]
    [InlineData(LinkInstruction.Bltzal)]
    [InlineData(LinkInstruction.Bgezal)]
    public void TryGetLinkValue_TableDrivenAcrossAddresses_AlwaysDelaySlotPlusFour(LinkInstruction kind)
    {
        var instruction = DecodeLink(kind);

        foreach (var pc in new uint[] { 0x00000000u, 0x00001000u, 0x80001000u, 0xBFC00000u })
        {
            var result = R3000aLinkSemantics.TryGetLinkValue(instruction, pc, out var linkValue);

            result.Should().BeTrue();
            linkValue.Should().Be(pc + 8u);
        }
    }

    [Theory]
    [InlineData(0xFFFFFFF8u, 0x00000000u)]
    [InlineData(0xFFFFFFF9u, 0x00000001u)]
    [InlineData(0xFFFFFFFCu, 0x00000004u)]
    public void TryGetLinkValue_PcWraparound_WrapsModulo32Bit(uint pc, uint expected)
    {
        var instruction = DecodeLink(LinkInstruction.Jal);

        var result = R3000aLinkSemantics.TryGetLinkValue(instruction, pc, out var linkValue);

        result.Should().BeTrue();
        linkValue.Should().Be(expected);
    }

    [Fact]
    public void TryGetLinkValue_JalrKeepsVariableRdInMetadataWhileValueStaysPcPlusEight()
    {
        foreach (var rd in new byte[] { 0, 1, 10, 30, 31 })
        {
            var instruction = R3000aDecoder.Decode(RType(rs: 9, rt: 0, rd, shamt: 0, funct: 0x09));

            var result = R3000aLinkSemantics.TryGetLinkValue(instruction, pc: 0x80040000, out var linkValue);

            result.Should().BeTrue();
            linkValue.Should().Be(0x80040008u);
            instruction.LinkInfo.LinkRegister.Should().Be(rd);
        }
    }

    [Fact]
    public void TryGetLinkValue_KeepsRawEncodedWordUntouchedInDomainModel()
    {
        var encodedWord = IType(0x03, rs: 0, rt: 0, immediate: 0x1234);
        var instruction = R3000aDecoder.Decode(encodedWord);

        R3000aLinkSemantics.TryGetLinkValue(instruction, pc: 0x00001000, out _).Should().BeTrue();

        instruction.EncodedWord.Should().Be(encodedWord);
        instruction.GetOperand(0).Kind.Should().Be(R3000aOperandKind.JumpIndex);
    }

    [Theory]
    [InlineData(0x08000400u)]
    [InlineData(0x01000008u)]
    [InlineData(0x24080001u)]
    [InlineData(0x25090002u)]
    [InlineData(0x290A0003u)]
    [InlineData(0x3C0C0010u)]
    [InlineData(0x8D0D0000u)]
    [InlineData(0xAD0E0000u)]
    [InlineData(0x00000000u)]
    [InlineData(0x0000000Cu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x42000010u)]
    public void TryGetLinkValue_NonLinkInstructions_ReturnFalse(uint encodedWord)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aLinkSemantics.TryGetLinkValue(instruction, pc: 0x00001000, out var linkValue);

        result.Should().BeFalse();
        linkValue.Should().Be(0u);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x04)]
    [InlineData((byte)0x05)]
    [InlineData((byte)0x06)]
    [InlineData((byte)0x07)]
    public void TryGetLinkValue_ConditionalAndUnconditionalBranchesWithoutLink_ReturnFalse(byte opcodeField)
    {
        var instruction = R3000aDecoder.Decode(IType(opcodeField, rs: 8, rt: 0, immediate: 0x0010));

        var result = R3000aLinkSemantics.TryGetLinkValue(instruction, pc: 0x00001000, out var linkValue);

        result.Should().BeFalse();
        linkValue.Should().Be(0u);
    }
}
