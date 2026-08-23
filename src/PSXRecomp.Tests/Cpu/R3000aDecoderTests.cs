using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aDecoderTests
{
    private static uint RType(uint opcode, uint rs, uint rt, uint rd, uint shamt, uint funct)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | (rd << 11) | (shamt << 6) | funct;
    }

    private static uint IType(uint opcode, uint rs, uint rt, ushort immediate)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | immediate;
    }

    private static uint JType(uint opcode, uint index)
    {
        return (opcode << 26) | (index & 0x03FFFFFF);
    }

    private static void AssertReserved(R3000aInstruction instruction)
    {
        instruction.Opcode.Should().Be(R3000aOpcode.Reserved);
        instruction.Format.Should().Be(R3000aInstructionFormat.None);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Reserved);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.OperandCount.Should().Be(0);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
    }

    [Fact]
    public void Dec_R_001_AddFunction_DecodesAsAdd()
    {
        var instruction = R3000aDecoder.Decode(RType(0x00, 4, 5, 16, 0, 0x20));
        instruction.Opcode.Should().Be(R3000aOpcode.Add);
        instruction.Format.Should().Be(R3000aInstructionFormat.R);
    }

    [Fact]
    public void Dec_R_002_AdduFunction_DecodesAsAddu()
    {
        R3000aDecoder.Decode(RType(0x00, 4, 5, 16, 0, 0x21)).Opcode.Should().Be(R3000aOpcode.Addu);
    }

    [Fact]
    public void Dec_R_003_AndFunction_DecodesAsAnd()
    {
        R3000aDecoder.Decode(RType(0x00, 4, 5, 16, 0, 0x24)).Opcode.Should().Be(R3000aOpcode.And);
    }

    [Fact]
    public void Dec_R_004_SllFunction_DecodesAsSll()
    {
        R3000aDecoder.Decode(RType(0x00, 0, 8, 9, 4, 0x00)).Opcode.Should().Be(R3000aOpcode.Sll);
    }

    [Fact]
    public void Dec_R_005_JrFunction_DecodesAsJr()
    {
        R3000aDecoder.Decode(RType(0x00, 31, 0, 0, 0, 0x08)).Opcode.Should().Be(R3000aOpcode.Jr);
    }

    [Fact]
    public void Dec_R_006_SyscallFunction_DecodesAsSyscall()
    {
        R3000aDecoder.Decode(RType(0x00, 0, 0, 0, 0, 0x0C)).Opcode.Should().Be(R3000aOpcode.Syscall);
    }

    [Fact]
    public void Dec_I_001_AddiOpcode_DecodesAsAddi()
    {
        R3000aDecoder.Decode(IType(0x08, 4, 8, 100)).Opcode.Should().Be(R3000aOpcode.Addi);
    }

    [Fact]
    public void Dec_I_002_AddiuOpcode_DecodesAsAddiu()
    {
        R3000aDecoder.Decode(IType(0x09, 4, 8, 100)).Opcode.Should().Be(R3000aOpcode.Addiu);
    }

    [Fact]
    public void Dec_I_003_LwOpcode_DecodesAsLw()
    {
        R3000aDecoder.Decode(IType(0x23, 29, 8, 8)).Opcode.Should().Be(R3000aOpcode.Lw);
    }

    [Fact]
    public void Dec_I_004_SwOpcode_DecodesAsSw()
    {
        R3000aDecoder.Decode(IType(0x2B, 29, 8, 12)).Opcode.Should().Be(R3000aOpcode.Sw);
    }

    [Fact]
    public void Dec_I_005_BeqOpcode_DecodesAsBeq()
    {
        R3000aDecoder.Decode(IType(0x04, 8, 9, 12)).Opcode.Should().Be(R3000aOpcode.Beq);
    }

    [Fact]
    public void Dec_J_001_JumpOpcode_DecodesAsJ()
    {
        R3000aDecoder.Decode(JType(0x02, 0x0100000)).Opcode.Should().Be(R3000aOpcode.J);
    }

    [Fact]
    public void Dec_J_002_JumpAndLinkOpcode_DecodesAsJal()
    {
        R3000aDecoder.Decode(JType(0x03, 0x0100000)).Opcode.Should().Be(R3000aOpcode.Jal);
    }

    [Theory]
    [InlineData(0x02, R3000aOpcode.Srl)]
    [InlineData(0x03, R3000aOpcode.Sra)]
    [InlineData(0x04, R3000aOpcode.Sllv)]
    [InlineData(0x06, R3000aOpcode.Srlv)]
    [InlineData(0x07, R3000aOpcode.Srav)]
    [InlineData(0x10, R3000aOpcode.Mfhi)]
    [InlineData(0x11, R3000aOpcode.Mthi)]
    [InlineData(0x12, R3000aOpcode.Mflo)]
    [InlineData(0x13, R3000aOpcode.Mtlo)]
    [InlineData(0x18, R3000aOpcode.Mult)]
    [InlineData(0x19, R3000aOpcode.Multu)]
    [InlineData(0x1A, R3000aOpcode.Div)]
    [InlineData(0x1B, R3000aOpcode.Divu)]
    [InlineData(0x22, R3000aOpcode.Sub)]
    [InlineData(0x23, R3000aOpcode.Subu)]
    [InlineData(0x25, R3000aOpcode.Or)]
    [InlineData(0x26, R3000aOpcode.Xor)]
    [InlineData(0x27, R3000aOpcode.Nor)]
    [InlineData(0x2A, R3000aOpcode.Slt)]
    [InlineData(0x2B, R3000aOpcode.Sltu)]
    public void Decode_SpecialDefinedFuncts_MapToYamlOpcodes(uint funct, R3000aOpcode expected)
    {
        R3000aDecoder.Decode(RType(0x00, 1, 2, 3, 4, funct)).Opcode.Should().Be(expected);
    }

    [Fact]
    public void Decode_SpecialUndefinedFunct_ReturnsReserved()
    {
        AssertReserved(R3000aDecoder.Decode(RType(0x00, 1, 2, 3, 4, 0x01)));
        AssertReserved(R3000aDecoder.Decode(RType(0x00, 1, 2, 3, 4, 0x05)));
        AssertReserved(R3000aDecoder.Decode(RType(0x00, 1, 2, 3, 4, 0x0E)));
        AssertReserved(R3000aDecoder.Decode(RType(0x00, 1, 2, 3, 4, 0x2C)));
        AssertReserved(R3000aDecoder.Decode(RType(0x00, 1, 2, 3, 4, 0x3F)));
    }

    [Fact]
    public void Decode_EncodedWordZero_DecodesAsSllFormingNop()
    {
        var instruction = R3000aDecoder.Decode(0x00000000u);
        instruction.Opcode.Should().Be(R3000aOpcode.Sll);
        instruction.Format.Should().Be(R3000aInstructionFormat.R);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Sequential);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(0));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(0));
        instruction.GetOperand(2).Should().Be(R3000aOperand.CreateShamt(0));
    }

    [Theory]
    [InlineData(0x00, R3000aOpcode.Bltz)]
    [InlineData(0x01, R3000aOpcode.Bgez)]
    [InlineData(0x10, R3000aOpcode.Bltzal)]
    [InlineData(0x11, R3000aOpcode.Bgezal)]
    public void Decode_RegimmKnownSelectors_MapToYamlOpcodes(uint selector, R3000aOpcode expected)
    {
        var instruction = R3000aDecoder.Decode(IType(0x01, 4, selector, 8));
        instruction.Opcode.Should().Be(expected);
        instruction.Format.Should().Be(R3000aInstructionFormat.Regimm);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x0F)]
    [InlineData(0x12)]
    [InlineData(0x1F)]
    [InlineData(0x1E)]
    public void Decode_RegimmUnknownSelector_ReturnsReserved(uint selector)
    {
        AssertReserved(R3000aDecoder.Decode(IType(0x01, 4, (ushort)selector, 8)));
    }

    [Fact]
    public void Decode_J_HoldsJumpIndexOperandWithUnconditionalDelaySlot()
    {
        var instruction = R3000aDecoder.Decode(JType(0x02, 0x02ABCDEF));
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.JumpAbsolute);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        instruction.OperandCount.Should().Be(1);
        instruction.GetOperand(0).Kind.Should().Be(R3000aOperandKind.JumpIndex);
        instruction.GetOperand(0).Value.Should().Be(0x02ABCDEFu & 0x03FFFFFF);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
    }

    [Fact]
    public void Decode_Jal_IsLinkBranchWithRaLinkAndUnconditionalDelaySlot()
    {
        var instruction = R3000aDecoder.Decode(JType(0x03, 0x1234567));
        instruction.Opcode.Should().Be(R3000aOpcode.Jal);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.LinkBranch);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        instruction.LinkInfo.WritesLink.Should().BeTrue();
        instruction.LinkInfo.LinkRegister.Should().Be(31);
    }

    [Fact]
    public void Decode_Jr_HoldsSingleSourceRegisterAndUnconditionalDelaySlot()
    {
        var instruction = R3000aDecoder.Decode(RType(0x00, 9, 0, 0, 0, 0x08));
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.JumpRegister);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.OperandCount.Should().Be(1);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(9));
    }

    [Fact]
    public void Decode_Jalr_WritesEncodedRdLinkRegisterAndHoldsBothOperands()
    {
        var instruction = R3000aDecoder.Decode(RType(0x00, 9, 0, 10, 0, 0x09));
        instruction.Opcode.Should().Be(R3000aOpcode.Jalr);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.JumpRegister);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        instruction.LinkInfo.WritesLink.Should().BeTrue();
        instruction.LinkInfo.LinkRegister.Should().Be(10);
        instruction.OperandCount.Should().Be(2);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(10));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(9));
    }

    [Fact]
    public void Decode_Beq_HoldsThreeOperandsWithConditionalDelaySlot()
    {
        var instruction = R3000aDecoder.Decode(IType(0x04, 8, 9, 0xFFF0));
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.ConditionalBranch);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
        instruction.OperandCount.Should().Be(3);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(8));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(9));
        instruction.GetOperand(2).Value.Should().Be(0xFFF0u);
    }

    [Fact]
    public void Decode_BlezAndBgtz_HoldTwoOperands()
    {
        foreach (var (opcodeField, opcode) in new[] { ((uint)0x06, R3000aOpcode.Blez), ((uint)0x07, R3000aOpcode.Bgtz) })
        {
            var instruction = R3000aDecoder.Decode(IType(opcodeField, 16, 0, 4));
            instruction.Opcode.Should().Be(opcode);
            instruction.ControlFlow.Should().Be(R3000aControlFlowKind.ConditionalBranch);
            instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
            instruction.OperandCount.Should().Be(2);
            instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(16));
            instruction.GetOperand(1).Value.Should().Be(4u);
        }
    }

    [Fact]
    public void Decode_BltzAndBgez_AreConditionalBranchesWithoutLink()
    {
        foreach (var (selector, opcode) in new[] { ((ushort)0x00, R3000aOpcode.Bltz), ((ushort)0x01, R3000aOpcode.Bgez) })
        {
            var instruction = R3000aDecoder.Decode(IType(0x01, 4, selector, 8));
            instruction.ControlFlow.Should().Be(R3000aControlFlowKind.ConditionalBranch);
            instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
            instruction.LinkInfo.WritesLink.Should().BeFalse();
        }
    }

    [Fact]
    public void Decode_BltzalAndBgezal_AreLinkBranchesWithConditionalDelaySlotAndRaLink()
    {
        foreach (var (selector, opcode) in new[] { ((ushort)0x10, R3000aOpcode.Bltzal), ((ushort)0x11, R3000aOpcode.Bgezal) })
        {
            var instruction = R3000aDecoder.Decode(IType(0x01, 4, selector, 8));
            instruction.Opcode.Should().Be(opcode);
            instruction.ControlFlow.Should().Be(R3000aControlFlowKind.LinkBranch);
            instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
            instruction.LinkInfo.WritesLink.Should().BeTrue();
            instruction.LinkInfo.LinkRegister.Should().Be(R3000aLinkInfo.DefaultLinkRegister);
        }
    }

    [Fact]
    public void Decode_Loads_HoldRegisterPlusMemoryOffsetOperandsWithLoadDelay()
    {
        var expected = new[]
        {
            ((uint)0x20, R3000aOpcode.Lb),
            (0x21u, R3000aOpcode.Lh),
            (0x23u, R3000aOpcode.Lw),
            (0x24u, R3000aOpcode.Lbu),
            (0x25u, R3000aOpcode.Lhu),
        };

        foreach (var (opcodeField, opcode) in expected)
        {
            var instruction = R3000aDecoder.Decode(IType(opcodeField, 29, 8, 0xFFF8));
            instruction.Opcode.Should().Be(opcode);
            instruction.Format.Should().Be(R3000aInstructionFormat.I);
            instruction.OperandCount.Should().Be(2);
            instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(8));
            instruction.GetOperand(1).Should().Be(R3000aOperand.CreateMemoryOffset(29, 0xFFF8));
            instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeTrue();
            instruction.LoadDelayInfo.TargetRegister.Should().Be(8);
            instruction.LoadDelayInfo.LwlLwrPairSpecial.Should().BeFalse();
        }
    }

    [Fact]
    public void Decode_LwlAndLwr_MarkLoadDelayPairSpecial()
    {
        foreach (var (opcodeField, opcode) in new[] { ((uint)0x22, R3000aOpcode.Lwl), ((uint)0x26, R3000aOpcode.Lwr) })
        {
            var instruction = R3000aDecoder.Decode(IType(opcodeField, 2, 1, 3));
            instruction.Opcode.Should().Be(opcode);
            instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeTrue();
            instruction.LoadDelayInfo.TargetRegister.Should().Be(1);
            instruction.LoadDelayInfo.LwlLwrPairSpecial.Should().BeTrue();
            instruction.GetOperand(1).Should().Be(R3000aOperand.CreateMemoryOffset(2, 3));
        }
    }

    [Fact]
    public void Decode_Stores_HoldMemoryOffsetFormWithoutLoadDelay()
    {
        var expected = new[]
        {
            ((uint)0x28, R3000aOpcode.Sb),
            (0x29u, R3000aOpcode.Sh),
            (0x2Au, R3000aOpcode.Swl),
            (0x2Bu, R3000aOpcode.Sw),
            (0x2Eu, R3000aOpcode.Swr),
        };

        foreach (var (opcodeField, opcode) in expected)
        {
            var instruction = R3000aDecoder.Decode(IType(opcodeField, 29, 15, 0x10));
            instruction.Opcode.Should().Be(opcode);
            instruction.OperandCount.Should().Be(2);
            instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(15));
            instruction.GetOperand(1).Should().Be(R3000aOperand.CreateMemoryOffset(29, 0x10));
            instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        }
    }

    [Fact]
    public void Decode_Lwc2AndSwc2_UseStandardMemoryOffsetForm()
    {
        var lwc2 = R3000aDecoder.Decode(IType(0x32, 8, 2, 0x40));
        lwc2.Opcode.Should().Be(R3000aOpcode.Lwc2);
        lwc2.OperandCount.Should().Be(2);
        lwc2.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(2));
        lwc2.GetOperand(1).Should().Be(R3000aOperand.CreateMemoryOffset(8, 0x40));
        lwc2.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();

        var swc2 = R3000aDecoder.Decode(IType(0x3A, 8, 2, 0x40));
        swc2.Opcode.Should().Be(R3000aOpcode.Swc2);
        swc2.OperandCount.Should().Be(2);
        swc2.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(2));
        swc2.GetOperand(1).Should().Be(R3000aOperand.CreateMemoryOffset(8, 0x40));
    }

    [Fact]
    public void Decode_Lui_HoldsDestinationAndImmediate()
    {
        var instruction = R3000aDecoder.Decode(IType(0x0F, 0, 8, 0x8000));
        instruction.Opcode.Should().Be(R3000aOpcode.Lui);
        instruction.OperandCount.Should().Be(2);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(8));
        instruction.GetOperand(1).Value.Should().Be(0x8000u);
    }

    [Fact]
    public void Decode_ImmediateArithmetic_FollowsRtRsImmediateOrder()
    {
        var instruction = R3000aDecoder.Decode(IType(0x09, 4, 8, 0x1234));
        instruction.OperandCount.Should().Be(3);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(8));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(4));
        instruction.GetOperand(2).Value.Should().Be(0x1234u);
    }

    [Fact]
    public void Decode_RegisterArithmetic_FollowsRdRsRtOrder()
    {
        var instruction = R3000aDecoder.Decode(RType(0x00, 4, 5, 16, 0, 0x20));
        instruction.OperandCount.Should().Be(3);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(16));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(4));
        instruction.GetOperand(2).Should().Be(R3000aOperand.CreateRegister(5));
    }

    [Fact]
    public void Decode_ShiftByImmediate_FollowsRdRtShamtOrder()
    {
        var instruction = R3000aDecoder.Decode(RType(0x00, 0, 8, 9, 4, 0x00));
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(9));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(8));
        instruction.GetOperand(2).Should().Be(R3000aOperand.CreateShamt(4));
    }

    [Fact]
    public void Decode_ShiftByRegister_FollowsRdRtRsOrder()
    {
        var instruction = R3000aDecoder.Decode(RType(0x00, 4, 8, 9, 0, 0x04));
        instruction.Opcode.Should().Be(R3000aOpcode.Sllv);
        instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(9));
        instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(8));
        instruction.GetOperand(2).Should().Be(R3000aOperand.CreateRegister(4));
    }

    [Fact]
    public void Decode_MultiplyDivide_HoldsRsRtOperands()
    {
        foreach (var (funct, opcode) in new[] { (0x18u, R3000aOpcode.Mult), (0x1Bu, R3000aOpcode.Divu) })
        {
            var instruction = R3000aDecoder.Decode(RType(0x00, 4, 5, 0, 0, funct));
            instruction.Opcode.Should().Be(opcode);
            instruction.OperandCount.Should().Be(2);
            instruction.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(4));
            instruction.GetOperand(1).Should().Be(R3000aOperand.CreateRegister(5));
        }
    }

    [Fact]
    public void Decode_HiLoMoves_HoldSingleOperandOnCorrectSide()
    {
        var mfhi = R3000aDecoder.Decode(RType(0x00, 0, 0, 8, 0, 0x10));
        mfhi.Opcode.Should().Be(R3000aOpcode.Mfhi);
        mfhi.OperandCount.Should().Be(1);
        mfhi.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(8));

        var mthi = R3000aDecoder.Decode(RType(0x00, 9, 0, 0, 0, 0x11));
        mthi.Opcode.Should().Be(R3000aOpcode.Mthi);
        mthi.OperandCount.Should().Be(1);
        mthi.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(9));

        var mflo = R3000aDecoder.Decode(RType(0x00, 0, 0, 10, 0, 0x12));
        mflo.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(10));

        var mtlo = R3000aDecoder.Decode(RType(0x00, 11, 0, 0, 0, 0x13));
        mtlo.GetOperand(0).Should().Be(R3000aOperand.CreateRegister(11));
    }

    [Fact]
    public void Decode_SyscallAndBreak_AreTrapsWithoutOperandsOrDelaySlot()
    {
        var syscall = R3000aDecoder.Decode(RType(0x00, 0, 0, 0, 0, 0x0C));
        syscall.Opcode.Should().Be(R3000aOpcode.Syscall);
        syscall.ControlFlow.Should().Be(R3000aControlFlowKind.Trap);
        syscall.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        syscall.OperandCount.Should().Be(0);

        var @break = R3000aDecoder.Decode(RType(0x00, 0, 0, 0, 0, 0x0D));
        @break.Opcode.Should().Be(R3000aOpcode.Break);
        @break.ControlFlow.Should().Be(R3000aControlFlowKind.Trap);
    }

    [Fact]
    public void Decode_Mfc0_CapturesCoprocessorZeroRegisterFromRd()
    {
        var instruction = R3000aDecoder.Decode(RType(0x10, 0x00, 8, 12, 0, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Mfc0);
        instruction.Format.Should().Be(R3000aInstructionFormat.Cop);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);
        instruction.CopInfo.CoprocessorId.Should().Be(0);
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.MoveFromCoprocessor);
        instruction.CopInfo.CopRegisterNumber.Should().Be(12);
        instruction.OperandCount.Should().Be(0);
    }

    [Fact]
    public void Decode_Mtc0_CapturesCoprocessorZeroDestinationRegister()
    {
        var instruction = R3000aDecoder.Decode(RType(0x10, 0x04, 8, 12, 0, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Mtc0);
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.MoveToCoprocessor);
        instruction.CopInfo.CopRegisterNumber.Should().Be(12);
    }

    [Fact]
    public void Decode_Rfe_ClassifiesReturnFromException()
    {
        var instruction = R3000aDecoder.Decode(RType(0x10, 0x10, 0, 0, 0, 0x10));
        instruction.Opcode.Should().Be(R3000aOpcode.Rfe);
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.ReturnFromException);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x08)]
    [InlineData(0x0F)]
    [InlineData(0x1F)]
    public void Decode_CoprocessorZeroUnknownSelector_ReturnsReserved(uint selector)
    {
        AssertReserved(R3000aDecoder.Decode(RType(0x10, selector, 8, 12, 0, 0)));
    }

    [Fact]
    public void Decode_CoprocessorOneAndThree_ClassifyStructurallyUnusable()
    {
        var cop1 = R3000aDecoder.Decode(RType(0x11, 0x00, 8, 12, 0, 0));
        cop1.Opcode.Should().Be(R3000aOpcode.Cop1Unusable);
        cop1.Format.Should().Be(R3000aInstructionFormat.Cop);
        cop1.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);

        var cop3 = R3000aDecoder.Decode(RType(0x13, 0x02, 0, 0, 0, 0x1234));
        cop3.Opcode.Should().Be(R3000aOpcode.Cop3Unusable);
        cop3.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
    }

    [Fact]
    public void Decode_CoprocessorTwoCommand_HoldsRawCofun()
    {
        var instruction = R3000aDecoder.Decode(RType(0x12, 0x02, 0, 0, 0, 0x0001));
        instruction.Opcode.Should().Be(R3000aOpcode.Cop2Command);
        instruction.Format.Should().Be(R3000aInstructionFormat.Cop);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);
        instruction.CopInfo.CoprocessorId.Should().Be(2);
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.ExecuteCommand);
        instruction.CopInfo.Command.Should().Be(0x0001);
        instruction.OperandCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0x00, R3000aCopOperationKind.MoveFromCoprocessor)]
    [InlineData(0x04, R3000aCopOperationKind.MoveToCoprocessor)]
    [InlineData(0x06, R3000aCopOperationKind.MoveControlFromCoprocessor)]
    [InlineData(0x08, R3000aCopOperationKind.MoveControlToCoprocessor)]
    public void Decode_CoprocessorTwoTransfers_ExpressOperationsViaCopInfo(
        uint selector, R3000aCopOperationKind operation)
    {
        var instruction = R3000aDecoder.Decode(RType(0x12, selector, 0, 14, 0, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Cop2Command);
        instruction.CopInfo.CoprocessorId.Should().Be(2);
        instruction.CopInfo.Operation.Should().Be(operation);
        instruction.CopInfo.CopRegisterNumber.Should().Be(14);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x03)]
    [InlineData(0x05)]
    [InlineData(0x07)]
    [InlineData(0x0A)]
    [InlineData(0x1F)]
    public void Decode_CoprocessorTwoUnknownForm_ReturnsReserved(uint selector)
    {
        AssertReserved(R3000aDecoder.Decode(RType(0x12, selector, 0, 0, 0, 0)));
    }

    [Theory]
    [InlineData(0x14)]
    [InlineData(0x17)]
    [InlineData(0x1E)]
    [InlineData(0x1F)]
    [InlineData(0x27)]
    [InlineData(0x2C)]
    [InlineData(0x2D)]
    [InlineData(0x2F)]
    [InlineData(0x30)]
    [InlineData(0x31)]
    [InlineData(0x33)]
    [InlineData(0x38)]
    [InlineData(0x39)]
    [InlineData(0x3B)]
    [InlineData(0x3F)]
    public void Decode_UndefinedOpcodeField_ReturnsReserved(uint opcodeField)
    {
        AssertReserved(R3000aDecoder.Decode(opcodeField << 26));
    }

    [Fact]
    public void Decode_PreservesEncodedWordExactly()
    {
        foreach (var word in new[] { 0xDEADBEEFu, 0xFFFFFFFFu, 0x7FFFFFFFu, 0x80000000u })
        {
            R3000aDecoder.Decode(word).EncodedWord.Should().Be(word);
        }
    }
}
