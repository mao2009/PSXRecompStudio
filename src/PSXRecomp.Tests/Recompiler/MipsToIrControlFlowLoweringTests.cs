using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Lowering of the PS1 control-flow subset. A control-transfer instruction and
/// the instruction in its branch delay slot form one IR block (ADR-004): the
/// delay slot always retires, so it cannot be split off into a block reached
/// after the transfer.
/// </summary>
[Test]
public class MipsToIrControlFlowLoweringTests
{
    private const uint EntryPc = 0x80001000u;
    private const byte BeqOpcodeField = 0x04;
    private const byte BneOpcodeField = 0x05;

    [Fact]
    public void Beq_ProducesACompareEqualConditionAndABranchFlow()
    {
        var target = EntryPc + 0x40;
        var block = LowerControlTransfer(
            MipsEncoding.Branch(BeqOpcodeField, rs: 8, rt: 9, pc: EntryPc, target: target),
            MipsEncoding.Nop);

        block.EntryPc.Should().Be(EntryPc);
        block.Operations.Should().HaveCount(4);

        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(8);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[1].Register.Should().Be(9);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.CompareEqual);
        block.Operations[2].InputValueA.Should().Be(0);
        block.Operations[2].InputValueB.Should().Be(1);
        block.Operations[2].ResultValueId.Should().Be(2);

        // The delay slot's operations follow the condition, so a delay slot that
        // overwrites a condition register cannot change the branch outcome.
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Nop);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        block.Exit.Flow.Target.Should().Be(target);
        block.Exit.Flow.ConditionValueId.Should().Be(2);

        // Not taken continues after the delay slot, never at the delay slot.
        block.Exit.NextPc.Should().Be(EntryPc + 8);
    }

    [Fact]
    public void Bne_ProducesACompareNotEqualCondition()
    {
        var block = LowerControlTransfer(
            MipsEncoding.Branch(BneOpcodeField, rs: 8, rt: 9, pc: EntryPc, target: EntryPc + 0x20),
            MipsEncoding.Nop);

        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.CompareNotEqual);
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        block.Exit.Flow.Target.Should().Be(EntryPc + 0x20);
    }

    [Fact]
    public void Branch_BackwardTarget_IsResolvedFromThePcRelativeOffset()
    {
        var target = EntryPc - 0x10;
        var block = LowerControlTransfer(
            MipsEncoding.Branch(BneOpcodeField, rs: 8, rt: 0, pc: EntryPc, target: target),
            MipsEncoding.Nop);

        block.Exit.Flow!.Target.Should().Be(target);
        block.Exit.NextPc.Should().Be(EntryPc + 8);
    }

    [Fact]
    public void Branch_AgainstZeroRegister_ReadsGprZeroAsAnOperand()
    {
        var block = LowerControlTransfer(
            MipsEncoding.Branch(BeqOpcodeField, rs: 8, rt: 0, pc: EntryPc, target: EntryPc + 0x10),
            MipsEncoding.Nop);

        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[1].Register.Should().Be(0);
    }

    [Fact]
    public void Branch_DelaySlotOperationsFollowTheCondition()
    {
        // Delay slot: ADDIU $t2, $zero, 1
        var block = LowerControlTransfer(
            MipsEncoding.Branch(BeqOpcodeField, rs: 8, rt: 9, pc: EntryPc, target: EntryPc + 0x10),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 1));

        block.Operations.Should().HaveCount(7);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.CompareEqual);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[4].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[5].Kind.Should().Be(RecompilerIrOperationKind.Add);
        block.Operations[6].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[6].Register.Should().Be(10);

        // The delay slot's values continue the block's numbering.
        block.Operations[3].ResultValueId.Should().Be(3);
        block.Exit.Flow!.ConditionValueId.Should().Be(2);
    }

    [Fact]
    public void J_ProducesAJumpFlowWithNoNextPc()
    {
        var target = 0x80002000u;
        var block = LowerControlTransfer(MipsEncoding.Jump(target), MipsEncoding.Nop);

        block.Operations.Should().HaveCount(1);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.Nop);
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        block.Exit.NextPc.Should().BeNull();
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Jump);
        block.Exit.Flow.Target.Should().Be(target);
        block.Exit.Flow.ConditionValueId.Should().Be(-1);
    }

    [Fact]
    public void J_TargetKeepsTheRegionOfTheDelaySlotAddress()
    {
        // The 26-bit index supplies bits 2..27; bits 28..31 come from the delay
        // slot address, not from the jump instruction's own address.
        var pc = 0x8FFFFFFCu;
        var instruction = R3000aDecoder.Decode(MipsEncoding.J(0x02, 0x00000100u));
        var result = MipsToIrLowerer.LowerControlTransfer(instruction, pc, R3000aDecoder.Decode(MipsEncoding.Nop));

        result.IsSupported.Should().BeTrue();
        result.Block!.Exit.Flow!.Target.Should().Be(0x90000100u);
    }

    [Fact]
    public void Jr_RetiresTheDelaySlotAndTerminatesAsUnresolvedIndirectFlow()
    {
        // The target is a runtime register value; RecompilerIrFlow.Target is a
        // static address, so the block states the frontier instead.
        var block = LowerControlTransfer(
            MipsEncoding.JumpRegister(rs: 31),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 1));

        block.Operations.Should().HaveCount(4);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[3].Register.Should().Be(10);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        block.Exit.NextPc.Should().BeNull();
        block.Exit.Flow.Should().BeNull();
    }

    [Theory]
    [InlineData(R3000aOpcode.Beq)]
    [InlineData(R3000aOpcode.Bne)]
    [InlineData(R3000aOpcode.J)]
    [InlineData(R3000aOpcode.Jr)]
    public void ControlTransfer_LoweredAlone_FailsFast(R3000aOpcode opcode)
    {
        var encoded = opcode switch
        {
            R3000aOpcode.Beq => MipsEncoding.Branch(BeqOpcodeField, 8, 9, EntryPc, EntryPc + 0x10),
            R3000aOpcode.Bne => MipsEncoding.Branch(BneOpcodeField, 8, 9, EntryPc, EntryPc + 0x10),
            R3000aOpcode.J => MipsEncoding.Jump(0x80002000u),
            _ => MipsEncoding.JumpRegister(31),
        };

        var instruction = R3000aDecoder.Decode(encoded);
        instruction.Opcode.Should().Be(opcode);

        var result = MipsToIrLowerer.Lower(instruction, EntryPc);

        result.IsSupported.Should().BeFalse();
        result.Block.Should().BeNull();
        result.UnsupportedOpcode.Should().Be(opcode);
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidFlow);
        result.DiagnosticMessage.Should().Contain("delay slot");
    }

    [Fact]
    public void Jal_LinksPcPlusEightBeforeTheDelaySlotAndExitsWithACallFlow()
    {
        var target = 0x80002000u;
        var block = LowerControlTransfer(MipsEncoding.JumpAndLink(target), MipsEncoding.Nop);

        block.Operations.Should().HaveCount(3);

        // The interpreter links before the transfer applies (PSXCpu::ExecJal), so
        // the link write precedes the delay slot in the fused block.
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[0].Immediate.Should().Be(EntryPc + 8, "JAL links the branch address + 8");
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[1].Register.Should().Be(31);
        block.Operations[1].InputValueA.Should().Be(block.Operations[0].ResultValueId);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Nop);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Call);
        block.Exit.Flow.Target.Should().Be(target);
        block.Exit.Flow.ConditionValueId.Should().Be(-1);
        block.Exit.NextPc.Should().Be(EntryPc + 8, "a call resumes at the return address");
    }

    [Fact]
    public void Jal_LinkValueComesFromTheCpuLinkSemantics()
    {
        var instruction = R3000aDecoder.Decode(MipsEncoding.JumpAndLink(0x80002000u));
        R3000aLinkSemantics.TryGetLinkValue(instruction, EntryPc, out var expected).Should().BeTrue();

        var block = LowerControlTransfer(MipsEncoding.JumpAndLink(0x80002000u), MipsEncoding.Nop);

        block.Operations[0].Immediate.Should().Be(expected);
        block.Exit.NextPc.Should().Be(expected);
    }

    [Fact]
    public void Jal_DelaySlotObservesTheLinkedReturnAddress()
    {
        // ADDU $t0, $ra, $zero in the delay slot reads the freshly linked $ra,
        // because the link write retires with the JAL, before the delay slot.
        var block = LowerControlTransfer(
            MipsEncoding.JumpAndLink(0x80002000u),
            MipsEncoding.R(0x21, rd: 8, rs: 31, rt: 0, shamt: 0));

        var operations = block.Operations.ToList();
        var linkWrite = operations.FindIndex(
            op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 31);
        var linkRead = operations.FindIndex(
            op => op.Kind == RecompilerIrOperationKind.ReadGpr && op.Register == 31);

        linkWrite.Should().BeGreaterThanOrEqualTo(0);
        linkRead.Should().BeGreaterThan(linkWrite);
    }

    [Fact]
    public void Jalr_LinksAndTerminatesAsUnresolvedIndirectFlow()
    {
        var block = LowerControlTransfer(MipsEncoding.JumpAndLinkRegister(rd: 31, rs: 8), MipsEncoding.Nop);

        // The target register is read before the link write: PSXCpu::ExecJalr
        // captures the target first so JALR rd, rd uses the pre-link value.
        block.Operations.Should().HaveCount(4);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(8);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[1].Immediate.Should().Be(EntryPc + 8);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[2].Register.Should().Be(31);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Nop);

        // The target is a runtime register value, which a static flow target
        // cannot carry, so the frontier stays explicit.
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        block.Exit.NextPc.Should().BeNull();
        block.Exit.Flow.Should().BeNull();
    }

    [Fact]
    public void Jalr_ReadsTheTargetRegisterBeforeLinkingWhenTheyAreTheSame()
    {
        var block = LowerControlTransfer(MipsEncoding.JumpAndLinkRegister(rd: 8, rs: 8), MipsEncoding.Nop);

        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(8);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[2].Register.Should().Be(8);
    }

    [Fact]
    public void Jalr_IntoZeroRegister_EmitsNoLinkWrite()
    {
        // JALR $zero, $t0 is architecturally a JR: GPR[0] is immutable.
        var block = LowerControlTransfer(MipsEncoding.JumpAndLinkRegister(rd: 0, rs: 8), MipsEncoding.Nop);

        block.Operations.Should().NotContain(op => op.Kind == RecompilerIrOperationKind.WriteGpr);
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
    }

    [Fact]
    public void JrRa_KeepsOrdinaryJrSemanticsWithoutAReturnFlow()
    {
        // A return-like JR is not special-cased: RecompilerIrFlowKind.Return needs
        // a register-held target the contract cannot carry, so JR $ra lowers
        // exactly like any other JR.
        var returnLike = LowerControlTransfer(MipsEncoding.JumpRegister(rs: 31), MipsEncoding.Nop);
        var ordinary = LowerControlTransfer(MipsEncoding.JumpRegister(rs: 8), MipsEncoding.Nop);

        returnLike.Exit.Should().Be(ordinary.Exit);
        returnLike.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        returnLike.Exit.Flow.Should().BeNull();
    }

    [Fact]
    public void CallProgram_LinksIntoTheCalleeAndBackToTheReturnAddress()
    {
        // JAL callee ; NOP ; <return address> ...
        var callee = EntryPc + 0x20;
        var program = LowerWords(EntryPc,
            MipsEncoding.JumpAndLink(callee),
            MipsEncoding.Nop,
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1));

        var callBlock = program.Blocks[0];
        callBlock.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Call);
        callBlock.Exit.Flow.Target.Should().Be(callee);
        callBlock.Exit.NextPc.Should().Be(EntryPc + 8);

        // The return address names a real block, so the call does not erase
        // reachability of the code after it.
        program.Blocks.Should().Contain(block => block.EntryPc == callBlock.Exit.NextPc);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(R3000aOpcode.Blez, (byte)0x06)]
    [InlineData(R3000aOpcode.Bgtz, (byte)0x07)]
    public void CompareWithZeroBranches_AreNotLoweredYet(R3000aOpcode opcode, byte opcodeField)
    {
        var instruction = R3000aDecoder.Decode(MipsEncoding.I(opcodeField, rt: 0, rs: 8, immediate: 4));
        instruction.Opcode.Should().Be(opcode);

        var result = MipsToIrLowerer.LowerControlTransfer(instruction, EntryPc, R3000aDecoder.Decode(MipsEncoding.Nop));

        result.IsSupported.Should().BeFalse();
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidFlow);
    }

    [Fact]
    public void BranchInADelaySlot_IsUnpredictableAndFailsFast()
    {
        var outer = R3000aDecoder.Decode(MipsEncoding.Branch(BeqOpcodeField, 8, 9, EntryPc, EntryPc + 0x10));
        var inner = R3000aDecoder.Decode(MipsEncoding.Branch(BneOpcodeField, 10, 11, EntryPc + 4, EntryPc + 0x20));

        var result = MipsToIrLowerer.LowerControlTransfer(outer, EntryPc, inner);

        result.IsSupported.Should().BeFalse();
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidFlow);
        result.DiagnosticMessage.Should().Contain("UNPREDICTABLE");
    }

    [Fact]
    public void LoadInADelaySlot_FailsFastBecauseItsShadowCrossesTheTransfer()
    {
        var branch = R3000aDecoder.Decode(MipsEncoding.Branch(BeqOpcodeField, 8, 9, EntryPc, EntryPc + 0x10));
        var load = R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 11, offset: 0));

        var result = MipsToIrLowerer.LowerControlTransfer(branch, EntryPc, load);

        result.IsSupported.Should().BeFalse();
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidMemoryAccess);
    }

    [Fact]
    public void UnsupportedDelaySlotInstruction_PropagatesADiagnostic()
    {
        var jump = R3000aDecoder.Decode(MipsEncoding.Jump(0x80002000u));
        var multiply = R3000aDecoder.Decode(MipsEncoding.R(0x18, rd: 0, rs: 8, rt: 9, shamt: 0));
        multiply.Opcode.Should().Be(R3000aOpcode.Mult);

        var result = MipsToIrLowerer.LowerControlTransfer(jump, EntryPc, multiply);

        result.IsSupported.Should().BeFalse();
        result.UnsupportedOpcode.Should().Be(R3000aOpcode.Mult);
        result.DiagnosticMessage.Should().Contain("delay-slot");
    }

    [Fact]
    public void NonControlInstruction_PassedAsAControlTransfer_FailsFast()
    {
        var addiu = R3000aDecoder.Decode(MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1));

        var result = MipsToIrLowerer.LowerControlTransfer(addiu, EntryPc, R3000aDecoder.Decode(MipsEncoding.Nop));

        result.IsSupported.Should().BeFalse();
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidFlow);
    }

    [Fact]
    public void LowerProgram_FusesTheDelaySlotIntoTheTransferBlock()
    {
        var program = LowerWords(EntryPc,
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                                  // ADDIU $t0, $zero, 1
            MipsEncoding.Branch(BeqOpcodeField, 8, 0, EntryPc + 4, EntryPc + 0x10),            // BEQ  $t0, $zero, +0x10
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                                  // delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 3),                                 // fall-through
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 4));                                // branch target

        // Five instructions, four blocks: the branch and its delay slot are one.
        program.Blocks.Should().HaveCount(4);
        program.Blocks.Select(b => b.EntryPc)
            .Should().Equal(EntryPc, EntryPc + 4, EntryPc + 0x0C, EntryPc + 0x10);

        var branchBlock = program.Blocks[1];
        branchBlock.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        branchBlock.Exit.Flow.Target.Should().Be(EntryPc + 0x10);
        branchBlock.Exit.NextPc.Should().Be(EntryPc + 0x0C);

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LowerProgram_ControlTransferWithoutADelaySlot_FailsFast()
    {
        var lower = () => LowerWords(EntryPc, MipsEncoding.Jump(0x80002000u));

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*has no delay-slot instruction*");
    }

    [Fact]
    public void LowerProgram_DelaySlotEntryAtTheWrongAddress_FailsFast()
    {
        var instructions = new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Jump(0x80002000u)), EntryPc),
            (R3000aDecoder.Decode(MipsEncoding.Nop), EntryPc + 0x20),
        };

        var lower = () => MipsToIrLowerer.LowerProgram(instructions);

        lower.Should().Throw<InvalidOperationException>().WithMessage("*delay slot*");
    }

    [Fact]
    public void LowerProgram_UnsupportedControlTransfer_FailsFast()
    {
        // BLEZ needs a signed comparison the contract does not have yet.
        var lower = () => LowerWords(EntryPc, MipsEncoding.I(0x06, rt: 0, rs: 8, immediate: 4), MipsEncoding.Nop);

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*InvalidFlow*");
    }

    [Fact]
    public void LowerProgram_BranchIntoADelaySlot_FailsFast()
    {
        // The delay slot at EntryPc + 0x0C is fused into the branch's block, so it
        // is not a block entry; entering it is not representable and must not be
        // silently read as "control left the program".
        var lower = () => LowerWords(EntryPc,
            MipsEncoding.Branch(BeqOpcodeField, rs: 8, rt: 9, pc: EntryPc, target: EntryPc + 0x0C),
            MipsEncoding.Nop,                                                        // 0x04 delay slot
            MipsEncoding.Branch(BneOpcodeField, rs: 8, rt: 9, pc: EntryPc + 8, target: EntryPc),
            MipsEncoding.Nop,                                                        // 0x0C delay slot, branch target
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 1));

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*is not a block entry*");
    }

    [Fact]
    public void LowerProgram_BranchToAnAddressOutsideTheStream_IsLeftAlone()
    {
        // Leaving the lowered program is legitimate: only a target that is lowered
        // but unreachable as a block entry is a defect.
        var program = LowerWords(EntryPc,
            MipsEncoding.Branch(BeqOpcodeField, rs: 8, rt: 9, pc: EntryPc, target: EntryPc + 0x400),
            MipsEncoding.Nop);

        program.Blocks.Should().ContainSingle();
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ReturnFlow_RemainsReservedAndIsRejectedByTheValidator()
    {
        // Nothing in this stage emits a Return flow; the validator still rejects
        // one, so the reserved extension point stays closed.
        var block = new RecompilerIrBlock(
            EntryPc,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: EntryPc + 8,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Return)));

        var diagnostics = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block })).Diagnostics;

        diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be(RecompilerIrDiagnosticCode.ReservedFlow);
    }

    [Fact]
    public void Scenario_ArithmeticThenCompareThenBranch_Validates()
    {
        var program = LowerWords(EntryPc,
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5),                        // ADDIU $t0, $zero, 5
            MipsEncoding.R(0x21, rd: 9, rs: 8, rt: 8, shamt: 0),                     // ADDU  $t1, $t0, $t0
            MipsEncoding.Branch(BneOpcodeField, 9, 8, EntryPc + 8, EntryPc),         // BNE   $t1, $t0, back to entry
            MipsEncoding.Nop,                                                        // delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 7));

        program.Blocks.Should().HaveCount(4);
        var branchBlock = program.Blocks[2];
        branchBlock.Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.CompareNotEqual);
        branchBlock.Exit.Flow!.Target.Should().Be(EntryPc);

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        RecompilerIrSerializer.Serialize(program).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ControlFlowProgram_SerializesDeterministically()
    {
        var words = new[]
        {
            MipsEncoding.Jump(0x80002000u),
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),
        };

        RecompilerIrSerializer.Serialize(LowerWords(EntryPc, words))
            .Should().Be(RecompilerIrSerializer.Serialize(LowerWords(EntryPc, words)));
    }

    private static RecompilerIrProgram LowerWords(uint entryPc, params uint[] words)
    {
        var instructions = words
            .Select((word, index) => (R3000aDecoder.Decode(word), entryPc + (uint)(index * 4)))
            .ToArray();
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    private static RecompilerIrBlock LowerControlTransfer(uint controlWord, uint delaySlotWord)
    {
        var result = MipsToIrLowerer.LowerControlTransfer(
            R3000aDecoder.Decode(controlWord), EntryPc, R3000aDecoder.Decode(delaySlotWord));

        result.IsSupported.Should().BeTrue(
            $"lowering failed: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
        result.Block.Should().NotBeNull();

        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { result.Block! }))
            .IsValid.Should().BeTrue();
        return result.Block!;
    }
}
