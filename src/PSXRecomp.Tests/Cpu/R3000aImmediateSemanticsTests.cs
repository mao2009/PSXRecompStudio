using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aImmediateSemanticsTests
{
    private const byte AddiOpcodeField = 0x08;
    private const byte AddiuOpcodeField = 0x09;
    private const byte SltiOpcodeField = 0x0A;
    private const byte SltiuOpcodeField = 0x0B;
    private const byte AndiOpcodeField = 0x0C;
    private const byte OriOpcodeField = 0x0D;
    private const byte XoriOpcodeField = 0x0E;
    private const byte LuiOpcodeField = 0x0F;

    private static readonly byte[] SignExtendedOpcodeFields =
    {
        AddiOpcodeField,
        AddiuOpcodeField,
        SltiOpcodeField,
        SltiuOpcodeField,
    };

    private static readonly byte[] ZeroExtendedOpcodeFields =
    {
        AndiOpcodeField,
        OriOpcodeField,
        XoriOpcodeField,
    };

    private static uint IType(uint opcode, uint rs, uint rt, ushort immediate)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | immediate;
    }

    private static uint RType(uint rs, uint rt, uint rd, uint shamt, uint funct)
    {
        return (0x00u << 26) | (rs << 21) | (rt << 16) | (rd << 11) | (shamt << 6) | funct;
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0x0001, 1)]
    [InlineData(0x7FFF, 32767)]
    [InlineData(0x8000, -32768)]
    [InlineData(0xFFFF, -1)]
    public void TryGetImmediate_SignExtendedOpcodes_SignExtendsRawHalfword(int rawImmediate, int expected)
    {
        foreach (var opcodeField in SignExtendedOpcodeFields)
        {
            var encodedWord = IType(opcodeField, rs: 8, rt: 9, immediate: (ushort)rawImmediate);
            var instruction = R3000aDecoder.Decode(encodedWord);

            var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

            result.Should().BeTrue();
            value.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0x0001, 1)]
    [InlineData(0x7FFF, 32767)]
    [InlineData(0x8000, 32768)]
    [InlineData(0xFFFF, 65535)]
    public void TryGetImmediate_ZeroExtendedOpcodes_ZeroExtendsRawHalfword(int rawImmediate, int expected)
    {
        foreach (var opcodeField in ZeroExtendedOpcodeFields)
        {
            var encodedWord = IType(opcodeField, rs: 8, rt: 9, immediate: (ushort)rawImmediate);
            var instruction = R3000aDecoder.Decode(encodedWord);

            var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

            result.Should().BeTrue();
            value.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0x0010u)]
    [InlineData(0x8000u)]
    [InlineData(0xFFFFu)]
    public void TryGetImmediate_Lui_IsNotAnImmediateSemanticsTarget(uint rawImmediate)
    {
        var encodedWord = IType(LuiOpcodeField, rs: 0, rt: 12, immediate: (ushort)rawImmediate);
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(1).Kind.Should().Be(R3000aOperandKind.Immediate);
    }

    [Theory]
    [InlineData(0x11090002u)]
    [InlineData(0x1509FFF4u)]
    [InlineData(0x19000004u)]
    [InlineData(0x1D00FFFCu)]
    [InlineData(0x0500FFF4u)]
    [InlineData(0x05110010u)]
    public void TryGetImmediate_BranchOffsetImmediates_AreOutOfScope(uint encodedWord)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(instruction.OperandCount - 1).Kind.Should().Be(R3000aOperandKind.Immediate);
    }

    [Theory]
    [InlineData(0x00084940u, (byte)5)]
    [InlineData(0x000957C3u, (byte)31)]
    public void TryGetImmediate_ShiftAmounts_StayRawFiveBitValuesWithoutExtension(uint encodedWord, byte expectedShamt)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(2).Kind.Should().Be(R3000aOperandKind.Shamt);
        instruction.GetOperand(2).Value.Should().Be(expectedShamt);
    }

    [Theory]
    [InlineData(0x8DAE8000u)]
    [InlineData(0xA1AEFFFFu)]
    [InlineData(0xAD0D7FFFu)]
    [InlineData(0xC9020004u)]
    [InlineData(0xE9030008u)]
    public void TryGetImmediate_MemoryOffsets_AreNotTreatedAsImmediates(uint encodedWord)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(instruction.OperandCount - 1).Kind.Should().Be(R3000aOperandKind.MemoryOffset);
    }

    [Fact]
    public void TryGetImmediate_MemoryOffsetKeepsNegativeOffsetRawUntilOwnSemanticsApply()
    {
        var instruction = R3000aDecoder.Decode(IType(0x23, rs: 13, rt: 14, immediate: 0x8000));

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(1).Value.Should().Be(0x8000u);
    }

    [Theory]
    [InlineData(0x08000400u)]
    [InlineData(0x01000008u)]
    [InlineData(0x00000000u)]
    [InlineData(0x0000000Cu)]
    [InlineData(0x0000000Du)]
    [InlineData(0x400B6000u)]
    [InlineData(0x42000010u)]
    [InlineData(0x4A000001u)]
    [InlineData(0xFFFFFFFFu)]
    public void TryGetImmediate_NonImmediateInstructions_ReturnFalseAndZero(uint encodedWord)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryGetImmediate_JumpIndexOperandsAreNotMisclassifiedAsImmediates()
    {
        var instruction = R3000aDecoder.Decode(0x08000400u);

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(0).Kind.Should().Be(R3000aOperandKind.JumpIndex);
    }

    [Fact]
    public void TryGetImmediate_CoprocessorRegisterOperandsAreNotMisclassifiedAsImmediates()
    {
        var instruction = R3000aDecoder.Decode(IType(0x32, rs: 8, rt: 2, immediate: 0x0004));

        var result = R3000aImmediateSemantics.TryGetImmediate(instruction, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
        instruction.GetOperand(0).Kind.Should().Be(R3000aOperandKind.CopReg);
    }

    [Fact]
    public void TryGetImmediate_DoesNotMutateEncodedWordOrRawImmediate()
    {
        var encodedWord = IType(AddiOpcodeField, rs: 8, rt: 9, immediate: 0x8000);
        var instruction = R3000aDecoder.Decode(encodedWord);

        R3000aImmediateSemantics.TryGetImmediate(instruction, out _).Should().BeTrue();

        instruction.EncodedWord.Should().Be(encodedWord);
        instruction.GetOperand(2).Kind.Should().Be(R3000aOperandKind.Immediate);
        instruction.GetOperand(2).Value.Should().Be(0x8000u);
    }

    [Fact]
    public void TryGetImmediate_DoesNotMutateEncodedWordOnZeroExtensionPath()
    {
        var encodedWord = IType(OriOpcodeField, rs: 8, rt: 9, immediate: 0xFFFF);
        var instruction = R3000aDecoder.Decode(encodedWord);

        R3000aImmediateSemantics.TryGetImmediate(instruction, out _).Should().BeTrue();

        instruction.EncodedWord.Should().Be(encodedWord);
        instruction.GetOperand(2).Kind.Should().Be(R3000aOperandKind.Immediate);
        instruction.GetOperand(2).Value.Should().Be(0xFFFFu);
    }
}
