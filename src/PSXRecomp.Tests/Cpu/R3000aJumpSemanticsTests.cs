using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aJumpSemanticsTests
{
    private static uint JType(uint opcode, uint instrIndex)
    {
        return (opcode << 26) | instrIndex;
    }

    private static uint IType(uint opcode, uint rs, uint rt, ushort immediate)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | immediate;
    }

    public enum JumpKind
    {
        J = 0,
        Jal,
    }

    private static uint EncodeJump(JumpKind kind, uint instrIndex)
    {
        return kind switch
        {
            JumpKind.J => JType(0x02, instrIndex),
            JumpKind.Jal => JType(0x03, instrIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Theory]
    [InlineData(JumpKind.J)]
    [InlineData(JumpKind.Jal)]
    public void TryGetJumpTarget_ZeroIndex_TargetIsRegionBaseOfDelaySlotAddress(JumpKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeJump(kind, instrIndex: 0x00000000));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x00000000u);
    }

    [Theory]
    [InlineData(JumpKind.J)]
    [InlineData(JumpKind.Jal)]
    public void TryGetJumpTarget_NormalIndex_ShiftsLeftByTwoWithinRegion(JumpKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeJump(kind, instrIndex: 0x000400));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x00001000u);
    }

    [Theory]
    [InlineData(JumpKind.J)]
    [InlineData(JumpKind.Jal)]
    public void TryGetJumpTarget_MaximumIndex_FillsLowTwentyEightBits(JumpKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeJump(kind, instrIndex: 0x03FFFFFF));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x80001000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x8FFFFFFCu);
    }

    [Theory]
    [InlineData(JumpKind.J)]
    [InlineData(JumpKind.Jal)]
    public void TryGetJumpTarget_UpperFourBitsComeFromDelaySlotAddressNotInstructionPc(JumpKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeJump(kind, instrIndex: 0x00000400));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x0FFFFFFC, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x10001000u);
    }

    [Theory]
    [InlineData(JumpKind.J)]
    [InlineData(JumpKind.Jal)]
    public void TryGetJumpTarget_KsegRegion_KeepsHighNibbleAndAppliesIndex(JumpKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeJump(kind, instrIndex: 0x01000000));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0xBFC00000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0xB4000000u);
    }

    [Theory]
    [InlineData(JumpKind.J)]
    [InlineData(JumpKind.Jal)]
    public void TryGetJumpTarget_PcWraparound_TakesRegionBitsFromWrappedDelaySlotAddress(JumpKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeJump(kind, instrIndex: 0x00000100));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0xFFFFFFFC, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x00000400u);
    }

    [Fact]
    public void TryGetJumpTarget_KeepsRawEncodedWordAndJumpIndexUntouchedInDomainModel()
    {
        var encodedWord = EncodeJump(JumpKind.Jal, instrIndex: 0x02ABCDEF);
        var instruction = R3000aDecoder.Decode(encodedWord);

        R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x80001000, out _).Should().BeTrue();

        instruction.EncodedWord.Should().Be(encodedWord);
        instruction.GetOperand(0).Kind.Should().Be(R3000aOperandKind.JumpIndex);
        instruction.GetOperand(0).Value.Should().Be(0x02ABCDEFu);
    }

    [Theory]
    [InlineData(0x24080001u)]
    [InlineData(0x25090002u)]
    [InlineData(0x290A0003u)]
    [InlineData(0x2C0B0004u)]
    [InlineData(0x3C0C0010u)]
    [InlineData(0x8D0D0000u)]
    [InlineData(0xAD0E0000u)]
    [InlineData(0x00000000u)]
    [InlineData(0x0000000Cu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x0100F809u)]
    [InlineData(0x01A0F809u)]
    [InlineData(0x42000010u)]
    public void TryGetJumpTarget_NonJumpInstructions_ReturnFalse(uint encodedWord)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeFalse();
        target.Should().Be(0u);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x04)]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x07)]
    [InlineData(0x23)]
    [InlineData(0x2B)]
    [InlineData(0x32)]
    public void TryGetJumpTarget_BranchAndMemoryOpcodes_ReturnFalse(byte opcodeField)
    {
        var instruction = R3000aDecoder.Decode(IType(opcodeField, rs: 8, rt: 9, immediate: 0x0010));

        var result = R3000aJumpSemantics.TryGetJumpTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeFalse();
        target.Should().Be(0u);
    }
}
