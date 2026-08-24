using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aDecoderGoldenTests
{
    [Theory]
    [InlineData(0x00432020u, R3000aOpcode.Add, 4, 2, 3)]
    [InlineData(0x01095021u, R3000aOpcode.Addu, 10, 8, 9)]
    [InlineData(0x02119024u, R3000aOpcode.And, 18, 16, 17)]
    public void Decode_GoldenVector_ThreeRegisterArithmetic_FixesCompleteShape(
        uint encodedWord, R3000aOpcode opcode, byte rd, byte rs, byte rt)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, opcode, R3000aInstructionFormat.R, operandCount: 3);
        AssertRegisterOperand(instruction, 0, rd);
        AssertRegisterOperand(instruction, 1, rs);
        AssertRegisterOperand(instruction, 2, rt);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x00149FC0u, R3000aOpcode.Sll, 19, 20, 31)]
    [InlineData(0x00149802u, R3000aOpcode.Srl, 19, 20, 0)]
    public void Decode_GoldenVector_ShiftByImmediate_FixesCompleteShape(
        uint encodedWord, R3000aOpcode opcode, byte rd, byte rt, byte shamt)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, opcode, R3000aInstructionFormat.R, operandCount: 3);
        AssertRegisterOperand(instruction, 0, rd);
        AssertRegisterOperand(instruction, 1, rt);
        AssertShamtOperand(instruction, 2, shamt);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Fact]
    public void Decode_GoldenVector_Jr_FixesCompleteShape()
    {
        var instruction = R3000aDecoder.Decode(0x03E00008u);

        instruction.Opcode.Should().Be(R3000aOpcode.Jr);
        instruction.Format.Should().Be(R3000aInstructionFormat.R);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.JumpRegister);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        instruction.OperandCount.Should().Be(1);
        AssertRegisterOperand(instruction, 0, 31);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
        AssertEncodedWordIsPreserved(instruction, 0x03E00008u);
    }

    [Fact]
    public void Decode_GoldenVector_Syscall_FixesCompleteShape()
    {
        var instruction = R3000aDecoder.Decode(0x0000000Cu);

        instruction.Opcode.Should().Be(R3000aOpcode.Syscall);
        instruction.Format.Should().Be(R3000aInstructionFormat.R);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Trap);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.OperandCount.Should().Be(0);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
        AssertEncodedWordIsPreserved(instruction, 0x0000000Cu);
    }

    [Fact]
    public void Decode_GoldenVector_NopWord_FixesCompleteSllZeroShape()
    {
        var instruction = R3000aDecoder.Decode(0x00000000u);

        AssertSequentialForm(instruction, R3000aOpcode.Sll, R3000aInstructionFormat.R, operandCount: 3);
        AssertRegisterOperand(instruction, 0, 0);
        AssertRegisterOperand(instruction, 1, 0);
        AssertShamtOperand(instruction, 2, 0);
        AssertEncodedWordIsPreserved(instruction, 0x00000000u);
    }

    [Theory]
    [InlineData(0x20228000u, R3000aOpcode.Addi, 2, 1, 0x8000)]
    [InlineData(0x2422FFFFu, R3000aOpcode.Addiu, 2, 1, 0xFFFF)]
    public void Decode_GoldenVector_SignExtendedImmediateArithmetic_KeepsRawHalfword(
        uint encodedWord, R3000aOpcode opcode, byte rt, byte rs, ushort rawImmediate)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, opcode, R3000aInstructionFormat.I, operandCount: 3);
        AssertRegisterOperand(instruction, 0, rt);
        AssertRegisterOperand(instruction, 1, rs);
        AssertImmediateOperand(instruction, 2, rawImmediate);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x30228000u, R3000aOpcode.Andi, 2, 1, 0x8000)]
    [InlineData(0x3422FFFFu, R3000aOpcode.Ori, 2, 1, 0xFFFF)]
    [InlineData(0x38228000u, R3000aOpcode.Xori, 2, 1, 0x8000)]
    public void Decode_GoldenVector_ZeroExtendedImmediateArithmetic_KeepsRawHalfword(
        uint encodedWord, R3000aOpcode opcode, byte rt, byte rs, ushort rawImmediate)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, opcode, R3000aInstructionFormat.I, operandCount: 3);
        AssertRegisterOperand(instruction, 0, rt);
        AssertRegisterOperand(instruction, 1, rs);
        AssertImmediateOperand(instruction, 2, rawImmediate);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x3C088000u, 8, 0x8000)]
    [InlineData(0x3C01FFFFu, 1, 0xFFFF)]
    public void Decode_GoldenVector_Lui_KeepsRawUpperHalfAsImmediateOperand(
        uint encodedWord, byte rt, ushort rawImmediate)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, R3000aOpcode.Lui, R3000aInstructionFormat.I, operandCount: 2);
        AssertRegisterOperand(instruction, 0, rt);
        AssertImmediateOperand(instruction, 1, rawImmediate);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x8FA88000u, R3000aOpcode.Lw, 8, 29, 0x8000, true, false)]
    [InlineData(0x83A98000u, R3000aOpcode.Lb, 9, 29, 0x8000, true, false)]
    [InlineData(0x8841FFFCu, R3000aOpcode.Lwl, 1, 2, 0xFFFC, true, true)]
    [InlineData(0x9841FFFCu, R3000aOpcode.Lwr, 1, 2, 0xFFFC, true, true)]
    [InlineData(0xAFA88000u, R3000aOpcode.Sw, 8, 29, 0x8000, false, false)]
    public void Decode_GoldenVector_LoadStore_FixesMemoryOffsetShape(
        uint encodedWord,
        R3000aOpcode opcode,
        byte register,
        byte baseRegister,
        ushort rawOffset,
        bool producesLoadDelay,
        bool pairSpecial)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, opcode, R3000aInstructionFormat.I, operandCount: 2, producesLoadDelay);
        AssertRegisterOperand(instruction, 0, register);
        AssertMemoryOffsetOperand(instruction, 1, baseRegister, rawOffset);
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().Be(producesLoadDelay);
        if (producesLoadDelay)
        {
            instruction.LoadDelayInfo.TargetRegister.Should().Be(register);
        }

        instruction.LoadDelayInfo.LwlLwrPairSpecial.Should().Be(pairSpecial);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x1100FFECu, R3000aOpcode.Beq, 8, 0, 0xFFEC)]
    [InlineData(0x152A0008u, R3000aOpcode.Bne, 9, 10, 0x0008)]
    public void Decode_GoldenVector_TwoRegisterBranch_KeepsRawOffsetAsImmediateOperand(
        uint encodedWord, R3000aOpcode opcode, byte rs, byte rt, ushort rawOffset)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        instruction.Opcode.Should().Be(opcode);
        instruction.Format.Should().Be(R3000aInstructionFormat.I);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.ConditionalBranch);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
        instruction.OperandCount.Should().Be(3);
        AssertRegisterOperand(instruction, 0, rs);
        AssertRegisterOperand(instruction, 1, rt);
        AssertImmediateOperand(instruction, 2, rawOffset);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x1A00FFECu, R3000aOpcode.Blez, 16, 0xFFEC)]
    [InlineData(0x1C200010u, R3000aOpcode.Bgtz, 1, 0x0010)]
    public void Decode_GoldenVector_CompareWithZeroBranch_KeepsRawOffsetAsImmediateOperand(
        uint encodedWord, R3000aOpcode opcode, byte rs, ushort rawOffset)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        instruction.Opcode.Should().Be(opcode);
        instruction.Format.Should().Be(R3000aInstructionFormat.I);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.ConditionalBranch);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
        instruction.OperandCount.Should().Be(2);
        AssertRegisterOperand(instruction, 0, rs);
        AssertImmediateOperand(instruction, 1, rawOffset);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x0500FFECu, R3000aOpcode.Bltz, R3000aControlFlowKind.ConditionalBranch, 8, 0xFFEC, false, 0)]
    [InlineData(0x05110010u, R3000aOpcode.Bgezal, R3000aControlFlowKind.LinkBranch, 8, 0x0010, true, 31)]
    public void Decode_GoldenVector_RegimmBranch_FixesLinkMetadataAndRawOffset(
        uint encodedWord,
        R3000aOpcode opcode,
        R3000aControlFlowKind controlFlow,
        byte rs,
        ushort rawOffset,
        bool writesLink,
        byte linkRegister)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        instruction.Opcode.Should().Be(opcode);
        instruction.Format.Should().Be(R3000aInstructionFormat.Regimm);
        instruction.ControlFlow.Should().Be(controlFlow);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
        instruction.OperandCount.Should().Be(2);
        AssertRegisterOperand(instruction, 0, rs);
        AssertImmediateOperand(instruction, 1, rawOffset);
        instruction.LinkInfo.WritesLink.Should().Be(writesLink);
        instruction.LinkInfo.LinkRegister.Should().Be(linkRegister);
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x0BFFFFFFu, R3000aOpcode.J, 0x03FFFFFFu, R3000aControlFlowKind.JumpAbsolute, false, 0)]
    [InlineData(0x0C000001u, R3000aOpcode.Jal, 0x00000001u, R3000aControlFlowKind.LinkBranch, true, 31)]
    public void Decode_GoldenVector_Jump_FixesJumpIndexAndLinkMetadata(
        uint encodedWord,
        R3000aOpcode opcode,
        uint jumpIndex,
        R3000aControlFlowKind controlFlow,
        bool writesLink,
        byte linkRegister)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        instruction.Opcode.Should().Be(opcode);
        instruction.Format.Should().Be(R3000aInstructionFormat.J);
        instruction.ControlFlow.Should().Be(controlFlow);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        instruction.OperandCount.Should().Be(1);
        AssertJumpIndexOperand(instruction, 0, jumpIndex);
        instruction.LinkInfo.WritesLink.Should().Be(writesLink);
        instruction.LinkInfo.LinkRegister.Should().Be(linkRegister);
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x40086000u, R3000aOpcode.Mfc0, R3000aCopOperationKind.MoveFromCoprocessor, 12)]
    [InlineData(0x40886000u, R3000aOpcode.Mtc0, R3000aCopOperationKind.MoveToCoprocessor, 12)]
    [InlineData(0x42000010u, R3000aOpcode.Rfe, R3000aCopOperationKind.ReturnFromException, 0)]
    public void Decode_GoldenVector_CoprocessorZeroTransfer_FixesCopMetadata(
        uint encodedWord, R3000aOpcode opcode, R3000aCopOperationKind operation, byte copRegisterNumber)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        instruction.Opcode.Should().Be(opcode);
        instruction.Format.Should().Be(R3000aInstructionFormat.Cop);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.OperandCount.Should().Be(0);
        instruction.CopInfo.Operation.Should().Be(operation);
        instruction.CopInfo.CoprocessorId.Should().Be(0);
        instruction.CopInfo.CopRegisterNumber.Should().Be(copRegisterNumber);
        instruction.CopInfo.Command.Should().Be(0u);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Fact]
    public void Decode_GoldenVector_CoprocessorTwoCommand_FixesCoFunMetadata()
    {
        var instruction = R3000aDecoder.Decode(0x4A000002u);

        instruction.Opcode.Should().Be(R3000aOpcode.Cop2Command);
        instruction.Format.Should().Be(R3000aInstructionFormat.Cop);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.OperandCount.Should().Be(0);
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.ExecuteCommand);
        instruction.CopInfo.CoprocessorId.Should().Be(2);
        instruction.CopInfo.Command.Should().Be(0x00000002u);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        AssertEncodedWordIsPreserved(instruction, 0x4A000002u);
    }

    [Theory]
    [InlineData(0xC9020004u, R3000aOpcode.Lwc2, 2, 8, 0x0004)]
    [InlineData(0xEBBEFFF0u, R3000aOpcode.Swc2, 30, 29, 0xFFF0)]
    public void Decode_GoldenVector_CoprocessorDataTransfer_FixesCopRegAndMemoryOffsetShape(
        uint encodedWord, R3000aOpcode opcode, byte copRegisterNumber, byte baseRegister, ushort rawOffset)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        AssertSequentialForm(instruction, opcode, R3000aInstructionFormat.I, operandCount: 2);
        instruction.GetOperand(0).Kind.Should().Be(R3000aOperandKind.CopReg);
        instruction.GetOperand(0).CoprocessorId.Should().Be(2);
        instruction.GetOperand(0).Register.Should().Be(copRegisterNumber);
        AssertMemoryOffsetOperand(instruction, 1, baseRegister, rawOffset);
        AssertEncodedWordIsPreserved(instruction, encodedWord);
    }

    [Theory]
    [InlineData(0x00221920u, R3000aOpcode.Add)]
    [InlineData(0x00221921u, R3000aOpcode.Addu)]
    [InlineData(0x20221234u, R3000aOpcode.Addi)]
    [InlineData(0x24221234u, R3000aOpcode.Addiu)]
    [InlineData(0x00221922u, R3000aOpcode.Sub)]
    [InlineData(0x00221923u, R3000aOpcode.Subu)]
    [InlineData(0x0022192Au, R3000aOpcode.Slt)]
    [InlineData(0x0022192Bu, R3000aOpcode.Sltu)]
    [InlineData(0x28221234u, R3000aOpcode.Slti)]
    [InlineData(0x2C221234u, R3000aOpcode.Sltiu)]
    [InlineData(0x00221924u, R3000aOpcode.And)]
    [InlineData(0x00221925u, R3000aOpcode.Or)]
    [InlineData(0x00221926u, R3000aOpcode.Xor)]
    [InlineData(0x00221927u, R3000aOpcode.Nor)]
    [InlineData(0x30221234u, R3000aOpcode.Andi)]
    [InlineData(0x34221234u, R3000aOpcode.Ori)]
    [InlineData(0x38221234u, R3000aOpcode.Xori)]
    [InlineData(0x3C021234u, R3000aOpcode.Lui)]
    [InlineData(0x00221900u, R3000aOpcode.Sll)]
    [InlineData(0x00221902u, R3000aOpcode.Srl)]
    [InlineData(0x00221903u, R3000aOpcode.Sra)]
    [InlineData(0x00221904u, R3000aOpcode.Sllv)]
    [InlineData(0x00221906u, R3000aOpcode.Srlv)]
    [InlineData(0x00221907u, R3000aOpcode.Srav)]
    [InlineData(0x00220018u, R3000aOpcode.Mult)]
    [InlineData(0x00220019u, R3000aOpcode.Multu)]
    [InlineData(0x0022001Au, R3000aOpcode.Div)]
    [InlineData(0x0022001Bu, R3000aOpcode.Divu)]
    [InlineData(0x00001810u, R3000aOpcode.Mfhi)]
    [InlineData(0x00200011u, R3000aOpcode.Mthi)]
    [InlineData(0x00001812u, R3000aOpcode.Mflo)]
    [InlineData(0x00200013u, R3000aOpcode.Mtlo)]
    [InlineData(0x80221234u, R3000aOpcode.Lb)]
    [InlineData(0x90221234u, R3000aOpcode.Lbu)]
    [InlineData(0x84221234u, R3000aOpcode.Lh)]
    [InlineData(0x94221234u, R3000aOpcode.Lhu)]
    [InlineData(0x8C221234u, R3000aOpcode.Lw)]
    [InlineData(0x88221234u, R3000aOpcode.Lwl)]
    [InlineData(0x98221234u, R3000aOpcode.Lwr)]
    [InlineData(0xC8221234u, R3000aOpcode.Lwc2)]
    [InlineData(0xA0221234u, R3000aOpcode.Sb)]
    [InlineData(0xA4221234u, R3000aOpcode.Sh)]
    [InlineData(0xAC221234u, R3000aOpcode.Sw)]
    [InlineData(0xA8221234u, R3000aOpcode.Swl)]
    [InlineData(0xB8221234u, R3000aOpcode.Swr)]
    [InlineData(0xE8221234u, R3000aOpcode.Swc2)]
    [InlineData(0x08800001u, R3000aOpcode.J)]
    [InlineData(0x0C800001u, R3000aOpcode.Jal)]
    [InlineData(0x00200008u, R3000aOpcode.Jr)]
    [InlineData(0x00201809u, R3000aOpcode.Jalr)]
    [InlineData(0x10221234u, R3000aOpcode.Beq)]
    [InlineData(0x14221234u, R3000aOpcode.Bne)]
    [InlineData(0x18201234u, R3000aOpcode.Blez)]
    [InlineData(0x1C201234u, R3000aOpcode.Bgtz)]
    [InlineData(0x04201234u, R3000aOpcode.Bltz)]
    [InlineData(0x04211234u, R3000aOpcode.Bgez)]
    [InlineData(0x04301234u, R3000aOpcode.Bltzal)]
    [InlineData(0x04311234u, R3000aOpcode.Bgezal)]
    [InlineData(0x0000000Cu, R3000aOpcode.Syscall)]
    [InlineData(0x0000000Du, R3000aOpcode.Break)]
    [InlineData(0x40021800u, R3000aOpcode.Mfc0)]
    [InlineData(0x40821800u, R3000aOpcode.Mtc0)]
    [InlineData(0x42000010u, R3000aOpcode.Rfe)]
    public void Decode_YamlMirrorVector_MapsEncodingToMirroredOpcode(uint encodedWord, R3000aOpcode expectedOpcode)
    {
        var instruction = R3000aDecoder.Decode(encodedWord);

        instruction.Opcode.Should().Be(expectedOpcode);
        ((byte)instruction.Opcode).Should().BeLessThan((byte)R3000aOpcode.Cop2Command);
        instruction.EncodedWord.Should().Be(encodedWord);
    }

    private static void AssertSequentialForm(
        R3000aInstruction instruction,
        R3000aOpcode opcode,
        R3000aInstructionFormat format,
        int operandCount,
        bool producesLoadDelay = false)
    {
        instruction.Opcode.Should().Be(opcode);
        instruction.Format.Should().Be(format);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Sequential);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.OperandCount.Should().Be(operandCount);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().Be(producesLoadDelay);
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
    }

    private static void AssertEncodedWordIsPreserved(R3000aInstruction instruction, uint encodedWord)
    {
        instruction.EncodedWord.Should().Be(encodedWord);
    }

    private static void AssertRegisterOperand(R3000aInstruction instruction, int index, byte register)
    {
        var operand = instruction.GetOperand(index);
        operand.Kind.Should().Be(R3000aOperandKind.Register);
        operand.Register.Should().Be(register);
    }

    private static void AssertImmediateOperand(R3000aInstruction instruction, int index, ushort rawImmediate)
    {
        var operand = instruction.GetOperand(index);
        operand.Kind.Should().Be(R3000aOperandKind.Immediate);
        operand.Value.Should().Be(rawImmediate);
    }

    private static void AssertMemoryOffsetOperand(
        R3000aInstruction instruction, int index, byte baseRegister, ushort rawOffset)
    {
        var operand = instruction.GetOperand(index);
        operand.Kind.Should().Be(R3000aOperandKind.MemoryOffset);
        operand.BaseRegister.Should().Be(baseRegister);
        operand.Value.Should().Be(rawOffset);
    }

    private static void AssertShamtOperand(R3000aInstruction instruction, int index, byte shamt)
    {
        var operand = instruction.GetOperand(index);
        operand.Kind.Should().Be(R3000aOperandKind.Shamt);
        operand.Value.Should().Be(shamt);
    }

    private static void AssertJumpIndexOperand(R3000aInstruction instruction, int index, uint jumpIndex)
    {
        var operand = instruction.GetOperand(index);
        operand.Kind.Should().Be(R3000aOperandKind.JumpIndex);
        operand.Value.Should().Be(jumpIndex);
    }
}
