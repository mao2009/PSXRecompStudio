using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aBranchSemanticsTests
{
    private static uint IType(uint opcode, uint rs, uint rt, ushort immediate)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | immediate;
    }

    private static uint RType(uint funct)
    {
        return (0x00u << 26) | (8u << 21) | (9u << 16) | (10u << 11) | (0u << 6) | funct;
    }

    public enum BranchKind
    {
        Beq = 0,
        Bne,
        Blez,
        Bgtz,
        Bltz,
        Bgez,
        Bltzal,
        Bgezal,
    }

    private static uint EncodeBranch(BranchKind kind, byte rs, ushort immediate)
    {
        return kind switch
        {
            BranchKind.Beq => IType(0x04, rs, 9, immediate),
            BranchKind.Bne => IType(0x05, rs, 9, immediate),
            BranchKind.Blez => IType(0x06, rs, 0, immediate),
            BranchKind.Bgtz => IType(0x07, rs, 0, immediate),
            BranchKind.Bltz => IType(0x01, rs, 0x00, immediate),
            BranchKind.Bgez => IType(0x01, rs, 0x01, immediate),
            BranchKind.Bltzal => IType(0x01, rs, 0x10, immediate),
            BranchKind.Bgezal => IType(0x01, rs, 0x11, immediate),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Fact]
    public void TryGetBranchTarget_AllEightKinds_DecodesWithConditionalDelaySlot()
    {
        foreach (var kind in Enum.GetValues<BranchKind>())
        {
            var instruction = R3000aDecoder.Decode(EncodeBranch(kind, rs: 8, immediate: 0x0004));
            instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional, because: $"branch kind {kind} must have a conditional delay slot");
        }
    }

    [Theory]
    [InlineData(BranchKind.Beq)]
    [InlineData(BranchKind.Bne)]
    [InlineData(BranchKind.Blez)]
    [InlineData(BranchKind.Bgtz)]
    [InlineData(BranchKind.Bltz)]
    [InlineData(BranchKind.Bgez)]
    [InlineData(BranchKind.Bltzal)]
    [InlineData(BranchKind.Bgezal)]
    public void TryGetBranchTarget_ZeroOffset_TargetIsPcPlusFour(BranchKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeBranch(kind, rs: 8, immediate: 0x0000));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x00001004u);
    }

    [Theory]
    [InlineData(BranchKind.Beq)]
    [InlineData(BranchKind.Bne)]
    [InlineData(BranchKind.Blez)]
    [InlineData(BranchKind.Bgtz)]
    [InlineData(BranchKind.Bltz)]
    [InlineData(BranchKind.Bgez)]
    [InlineData(BranchKind.Bltzal)]
    [InlineData(BranchKind.Bgezal)]
    public void TryGetBranchTarget_PositiveOffset_SignExtendsAndScalesByFour(BranchKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeBranch(kind, rs: 8, immediate: 0x000C));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x00001004u + (0x000Cu << 2));
    }

    [Theory]
    [InlineData(BranchKind.Beq)]
    [InlineData(BranchKind.Bne)]
    [InlineData(BranchKind.Blez)]
    [InlineData(BranchKind.Bgtz)]
    [InlineData(BranchKind.Bltz)]
    [InlineData(BranchKind.Bgez)]
    [InlineData(BranchKind.Bltzal)]
    [InlineData(BranchKind.Bgezal)]
    public void TryGetBranchTarget_NegativeOffset_SubtractsFromPcPlusFour(BranchKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeBranch(kind, rs: 8, immediate: 0xFFF4));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00010000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x00010004u - (12u << 2));
    }

    [Theory]
    [InlineData(BranchKind.Beq)]
    [InlineData(BranchKind.Bne)]
    [InlineData(BranchKind.Blez)]
    [InlineData(BranchKind.Bgtz)]
    [InlineData(BranchKind.Bltz)]
    [InlineData(BranchKind.Bgez)]
    [InlineData(BranchKind.Bltzal)]
    [InlineData(BranchKind.Bgezal)]
    public void TryGetBranchTarget_MaximumPositiveOffset_AddsHalfMegabyte(BranchKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeBranch(kind, rs: 8, immediate: 0x7FFF));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x80001000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x80001004u + (0x7FFFu << 2));
    }

    [Theory]
    [InlineData(BranchKind.Beq)]
    [InlineData(BranchKind.Bne)]
    [InlineData(BranchKind.Blez)]
    [InlineData(BranchKind.Bgtz)]
    [InlineData(BranchKind.Bltz)]
    [InlineData(BranchKind.Bgez)]
    [InlineData(BranchKind.Bltzal)]
    [InlineData(BranchKind.Bgezal)]
    public void TryGetBranchTarget_MaximumNegativeOffset_WrapsModulo32Bit(BranchKind kind)
    {
        var instruction = R3000aDecoder.Decode(EncodeBranch(kind, rs: 8, immediate: 0x8000));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x80040000, out var target);

        result.Should().BeTrue();
        target.Should().Be(0x80020004u);
    }

    [Theory]
    [InlineData(0xFFFFFFFCu, 0x0000u, 0x00000000u)]
    [InlineData(0xFFFFFFFFu, 0x0000u, 0x00000003u)]
    [InlineData(0xFFFFFFF8u, 0x0001u, 0x00000000u)]
    [InlineData(0x9FFFFFFEu, 0x7FFFu, 0xA001FFFEu)]
    [InlineData(0xBFFFFFFCu, 0x8000u, 0xBFFE0000u)]
    public void TryGetBranchTarget_PcBoundary_WrapsPerAdr005Formula(uint pc, ushort immediate, uint expected)
    {
        var instruction = R3000aDecoder.Decode(IType(0x04, rs: 1, rt: 2, immediate));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc, out var target);

        result.Should().BeTrue();
        target.Should().Be(expected);
    }

    [Fact]
    public void TryGetBranchTarget_KeepsRawImmediateUntouchedInDomainModel()
    {
        var encodedWord = IType(0x04, rs: 8, rt: 9, unchecked((ushort)0xFFFC));
        var instruction = R3000aDecoder.Decode(encodedWord);

        R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out _).Should().BeTrue();

        instruction.EncodedWord.Should().Be(encodedWord);
        instruction.GetOperand(2).Kind.Should().Be(R3000aOperandKind.Immediate);
        instruction.GetOperand(2).Value.Should().Be(0xFFFCu);
    }

    [Theory]
    [InlineData(0x24080001u)]
    [InlineData(0x25090002u)]
    [InlineData(0x290A0003u)]
    [InlineData(0x2C0B0004u)]
    [InlineData(0x3C0C0010u)]
    [InlineData(0x8D0D0000u)]
    [InlineData(0xAD0E0000u)]
    [InlineData(R3000aBranchSemanticsTests.JumpEncodedWord)]
    [InlineData(R3000aBranchSemanticsTests.JumpAndLinkEncodedWord)]
    [InlineData(R3000aBranchSemanticsTests.JumpRegisterEncodedWord)]
    [InlineData(R3000aBranchSemanticsTests.SyscallEncodedWord)]
    [InlineData(0xFFFFFFFFu)]
    public void TryGetBranchTarget_NonBranchInstructions_ReturnFalse(uint encodedWord)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeFalse();
        target.Should().Be(0u);
    }

    private const uint JumpEncodedWord = 0x08000400u;
    private const uint JumpAndLinkEncodedWord = 0x0C000400u;
    private const uint JumpRegisterEncodedWord = 0x0100F809u;
    private const uint SyscallEncodedWord = 0x0000000Cu;

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x08)]
    [InlineData(0x20)]
    [InlineData(0x30)]
    [InlineData(0x3F)]
    public void TryGetBranchTarget_NonBranchPrimaryOpcodes_ReturnFalse(byte opcodeField)
    {
        var instruction = R3000aDecoder.Decode(IType(opcodeField, rs: 8, rt: 0, immediate: 0x0010));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeFalse();
        target.Should().Be(0u);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x0F)]
    [InlineData(0x12)]
    [InlineData(0x13)]
    [InlineData(0x1E)]
    [InlineData(0x1F)]
    public void TryGetBranchTarget_UndefinedRegimmSelectors_ReturnFalse(byte selector)
    {
        var instruction = R3000aDecoder.Decode(IType(0x01, rs: 8, selector, immediate: 0x0010));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeFalse();
        target.Should().Be(0u);
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x09)]
    [InlineData(0x0C)]
    [InlineData(0x20)]
    [InlineData(0x23)]
    [InlineData(0x2B)]
    public void TryGetBranchTarget_SpecialFunctions_ReturnFalse(byte funct)
    {
        var instruction = R3000aDecoder.Decode(RType(funct));

        var result = R3000aBranchSemantics.TryGetBranchTarget(instruction, pc: 0x00001000, out var target);

        result.Should().BeFalse();
        target.Should().Be(0u);
    }
}
