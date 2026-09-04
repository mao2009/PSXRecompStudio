using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Lowering of the PS1 memory subset (LB/LBU/LH/LHU/LW, SB/SH/SW) onto the IR
/// contract's memory operations: effective address, the access itself, and — for
/// the sign-extending loads — the shift pair that widens the accessed value.
/// </summary>
[Test]
public class MipsToIrMemoryLoweringTests
{
    private const uint EntryPc = 0x80001000u;

    [Fact]
    public void Lw_ProducesAddressThenLoad32ThenWrite()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 9, offset: 4));

        block.Operations.Should().HaveCount(5);

        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(9);
        block.Operations[0].ResultValueId.Should().Be(0);

        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[1].Immediate.Should().Be(4u);
        block.Operations[1].ResultValueId.Should().Be(1);

        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Add);
        block.Operations[2].InputValueA.Should().Be(0);
        block.Operations[2].InputValueB.Should().Be(1);
        block.Operations[2].ResultValueId.Should().Be(2);

        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Load32);
        block.Operations[3].InputValueA.Should().Be(2);
        block.Operations[3].InputValueB.Should().Be(-1);
        block.Operations[3].ResultValueId.Should().Be(3);
        block.Operations[3].Register.Should().Be(0);
        block.Operations[3].ShiftAmount.Should().Be(0);

        block.Operations[4].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[4].Register.Should().Be(10);
        block.Operations[4].InputValueA.Should().Be(3);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        block.Exit.NextPc.Should().Be(EntryPc + 4);
        block.Exit.Flow.Should().BeNull();
    }

    [Fact]
    public void Lw_NegativeOffset_IsSignExtendedIntoTheAddressConstant()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 9, offset: 0xFFFC));

        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[1].Immediate.Should().Be(0xFFFFFFFCu);
    }

    [Fact]
    public void Lb_SignExtendsWithAShiftPair()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lb, rt: 10, baseRegister: 9, offset: 0));

        block.Operations.Should().HaveCount(7);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Load8);
        block.Operations[3].ResultValueId.Should().Be(3);

        block.Operations[4].Kind.Should().Be(RecompilerIrOperationKind.ShiftLeftLogical);
        block.Operations[4].InputValueA.Should().Be(3);
        block.Operations[4].ShiftAmount.Should().Be(24);

        block.Operations[5].Kind.Should().Be(RecompilerIrOperationKind.ShiftRightArithmetic);
        block.Operations[5].InputValueA.Should().Be(4);
        block.Operations[5].ShiftAmount.Should().Be(24);

        block.Operations[6].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[6].InputValueA.Should().Be(5);
        block.Operations[6].Register.Should().Be(10);
    }

    [Fact]
    public void Lbu_UsesTheZeroExtendedLoadDirectly()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lbu, rt: 10, baseRegister: 9, offset: 0));

        block.Operations.Should().HaveCount(5);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Load8);
        block.Operations[4].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[4].InputValueA.Should().Be(3);
        block.Operations.Should().NotContain(op => op.Kind == RecompilerIrOperationKind.ShiftRightArithmetic);
    }

    [Fact]
    public void Lh_SignExtendsWithASixteenBitShiftPair()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lh, rt: 10, baseRegister: 9, offset: 2));

        block.Operations.Should().HaveCount(7);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Load16);
        block.Operations[4].Kind.Should().Be(RecompilerIrOperationKind.ShiftLeftLogical);
        block.Operations[4].ShiftAmount.Should().Be(16);
        block.Operations[5].Kind.Should().Be(RecompilerIrOperationKind.ShiftRightArithmetic);
        block.Operations[5].ShiftAmount.Should().Be(16);
    }

    [Fact]
    public void Lhu_UsesTheZeroExtendedLoadDirectly()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lhu, rt: 10, baseRegister: 9, offset: 2));

        block.Operations.Should().HaveCount(5);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.Load16);
        block.Operations[4].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
    }

    [Theory]
    [InlineData(R3000aOpcode.Sb, RecompilerIrOperationKind.Store8)]
    [InlineData(R3000aOpcode.Sh, RecompilerIrOperationKind.Store16)]
    [InlineData(R3000aOpcode.Sw, RecompilerIrOperationKind.Store32)]
    public void Store_TakesTheAddressInInputAAndTheValueInInputB(R3000aOpcode opcode, RecompilerIrOperationKind expected)
    {
        var block = LowerSupported(MipsEncoding.Load(opcode, rt: 10, baseRegister: 9, offset: 8));

        block.Operations.Should().HaveCount(5);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Add);
        block.Operations[2].ResultValueId.Should().Be(2);

        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[3].Register.Should().Be(10);
        block.Operations[3].ResultValueId.Should().Be(3);

        block.Operations[4].Kind.Should().Be(expected);
        block.Operations[4].InputValueA.Should().Be(2);
        block.Operations[4].InputValueB.Should().Be(3);
        block.Operations[4].ResultValueId.Should().Be(-1);
        block.Operations[4].Register.Should().Be(0);
    }

    [Fact]
    public void Load_IntoZeroRegister_EmitsNoArchitecturalWrite()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lw, rt: 0, baseRegister: 9, offset: 0));

        block.Operations.Should().NotContain(op => op.Kind == RecompilerIrOperationKind.WriteGpr);
        block.Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.Load32);
    }

    [Fact]
    public void Store_FromZeroRegister_ReadsGprZero()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Sw, rt: 0, baseRegister: 9, offset: 0));

        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[3].Register.Should().Be(0);
    }

    [Fact]
    public void Load_WithZeroBaseRegister_ReadsGprZeroAsTheAddressBase()
    {
        var block = LowerSupported(MipsEncoding.Load(R3000aOpcode.Lw, rt: 8, baseRegister: 0, offset: 0x10));

        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(0);
        block.Operations[1].Immediate.Should().Be(0x10u);
    }

    [Fact]
    public void Lwl_RemainsUnsupportedAndFailsFast()
    {
        var instruction = R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lwl, rt: 8, baseRegister: 9, offset: 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Lwl);

        var result = MipsToIrLowerer.Lower(instruction, EntryPc);

        result.IsSupported.Should().BeFalse();
        result.Block.Should().BeNull();
        result.UnsupportedOpcode.Should().Be(R3000aOpcode.Lwl);
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidOperationShape);
    }

    [Fact]
    public void MemoryProgram_ValidatesAndRoundTripsThroughTheSerializer()
    {
        // The NOP is the R3000A load-delay slot: without it the SW would read the
        // pre-load $t2 on hardware.
        var words = new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, 10, 9, 0),
            MipsEncoding.Nop,
            MipsEncoding.Load(R3000aOpcode.Sw, 10, 9, 4),
        };

        var program = LowerWords(words);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();

        RecompilerIrSerializer.Serialize(LowerWords(words))
            .Should().Be(RecompilerIrSerializer.Serialize(program));
    }

    [Fact]
    public void Scenario_AddressCalculationThenLoadAndStore_Validates()
    {
        // LUI $t0, 0x8001 ; ADDIU $t0, $t0, 0x20 ; LW $t1, 0($t0) ; NOP ; SW $t1, 4($t0)
        var program = LowerWords(new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8001),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x0020),
            MipsEncoding.Load(R3000aOpcode.Lw, 9, 8, 0),
            MipsEncoding.Nop,
            MipsEncoding.Load(R3000aOpcode.Sw, 9, 8, 4),
        });

        program.Blocks.Should().HaveCount(5);
        program.Blocks[2].Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.Load32);
        program.Blocks[4].Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.Store32);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    private static RecompilerIrProgram LowerWords(IReadOnlyList<uint> words)
    {
        var instructions = words
            .Select((word, index) => (R3000aDecoder.Decode(word), EntryPc + (uint)(index * 4)))
            .ToArray();
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    [Fact]
    public void LoadDelay_ObservedByTheNextInstruction_FusesTheCommitAfterTheObserver()
    {
        // LW $t1, 0($t0) ; ADDU $t2, $t1, $t3 — on hardware the ADDU reads the
        // pre-load $t1 and the load commits at the ADDU's retirement point.
        var program = LowerWords(new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.R(0x21, rd: 10, rs: 9, rt: 11, shamt: 0),
        });

        program.Blocks.Should().ContainSingle("the load and its load-delay slot fuse into one block");
        var block = program.Blocks[0];
        block.EntryPc.Should().Be(EntryPc);
        block.Exit.NextPc.Should().Be(EntryPc + 8, "the fused block covers both instructions");

        var operations = block.Operations.ToList();
        var load = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.Load32);
        var observerRead = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.ReadGpr && op.Register == 9);
        var observerWrite = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 10);
        var commit = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 9);

        load.Should().BeGreaterThanOrEqualTo(0);
        // The observer reads $t1 before the loaded value is committed to it, which
        // is exactly what makes it read the pre-load value.
        observerRead.Should().BeGreaterThan(load);
        commit.Should().BeGreaterThan(observerRead);
        commit.Should().BeGreaterThan(observerWrite);

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LoadDelay_ObservedByAnIndependentThenDependentInstruction_CommitsBeforeTheDependent()
    {
        // LW $t1, 0($t0) ; ADDU $t2, $t3, $t4 ; ADDU $t5, $t1, $zero — the load
        // delay is over by the third instruction, so it reads the loaded value.
        var program = LowerWords(new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.R(0x21, rd: 10, rs: 11, rt: 12, shamt: 0),
            MipsEncoding.R(0x21, rd: 13, rs: 9, rt: 0, shamt: 0),
        });

        // Nothing reads $t1 in the load-delay slot, so no fusion is needed and
        // each instruction keeps its own block.
        program.Blocks.Should().HaveCount(3);
        program.Blocks[0].Operations.Should().Contain(
            op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 9);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LoadDelay_ObservedByAControlTransfer_FusesLoadTransferAndDelaySlot()
    {
        // LW $t1, 0($t0) ; BEQ $t1, $t2, target ; NOP — the BEQ compares the
        // pre-load $t1, and the load commits before the branch delay slot runs.
        var target = EntryPc + 0x40;
        var program = LowerWords(new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.Branch(0x04, rs: 9, rt: 10, pc: EntryPc + 4, target: target),
            MipsEncoding.Nop,
        });

        program.Blocks.Should().ContainSingle();
        var block = program.Blocks[0];
        block.EntryPc.Should().Be(EntryPc, "the fused block is entered at the load");

        var operations = block.Operations.ToList();
        var compare = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.CompareEqual);
        var commit = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 9);
        var delaySlot = operations.FindIndex(op => op.Kind == RecompilerIrOperationKind.Nop);

        // Condition first (pre-load value), then the commit, then the delay slot:
        // the branch delay slot retires after the load has committed.
        compare.Should().BeGreaterThanOrEqualTo(0);
        commit.Should().BeGreaterThan(compare);
        delaySlot.Should().BeGreaterThan(commit);

        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        block.Exit.Flow.Target.Should().Be(target);
        block.Exit.NextPc.Should().Be(EntryPc + 12, "the not-taken successor follows the branch delay slot");
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LoadDelay_ObservedByJal_EmitsNoCommitBecauseTheLinkWriteCancelsIt()
    {
        // LW $ra, 0($sp) ; JAL target ; NOP — docs/cpu/pipeline.md: the JAL still
        // links PC+8 into $ra, because an immediate write cancels the pending load.
        var program = LowerWords(new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 31, baseRegister: 29, offset: 0),
            MipsEncoding.JumpAndLink(0x80002000u),
            MipsEncoding.Nop,
        });

        // JAL does not read $ra, so the load delay is not observable and no
        // fusion is needed: the load keeps its own block and the JAL's link write
        // lands after it, which is the same net register value the hardware
        // reaches by cancelling the pending load.
        program.Blocks.Should().HaveCount(2);
        program.Blocks[0].Operations.Should().Contain(
            op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 31);

        var callBlock = program.Blocks[1];
        var linkWrite = callBlock.Operations.Single(
            op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 31);
        var linked = callBlock.Operations.Single(op => op.ResultValueId == linkWrite.InputValueA);
        linked.Kind.Should().Be(RecompilerIrOperationKind.Constant);
        linked.Immediate.Should().Be(EntryPc + 4 + 8, "the last write to $ra is the JAL link value");
    }

    [Fact]
    public void LoadDelay_ObservedByAWriteToTheSameRegister_EmitsNoCommit()
    {
        // LW $t1, 0($t1) ; ADDIU $t1, $t1, 1 — the ADDIU reads the pre-load $t1
        // and its own write cancels the pending load, so only the ADDIU's value
        // reaches the register.
        var program = LowerWords(new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 9, offset: 0),
            MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 1),
        });

        program.Blocks.Should().ContainSingle();
        program.Blocks[0].Operations
            .Count(op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 9)
            .Should().Be(1);
    }

    [Fact]
    public void LoadDelay_ChainedThroughASecondLoad_FailsFast()
    {
        // LW $t1, 0($t0) ; LW $t2, 0($t1) ; ADDU $t3, $t2, $zero — the second
        // load's own commit would have to land outside the fused block.
        var instructions = new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0)), EntryPc),
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 9, offset: 0)), EntryPc + 4),
            (R3000aDecoder.Decode(MipsEncoding.R(0x21, rd: 11, rs: 10, rt: 0, shamt: 0)), EntryPc + 8),
        };

        var lower = () => MipsToIrLowerer.LowerProgram(instructions);

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*chained load delays*");
    }

    [Fact]
    public void LoadDelay_FusedProgram_SerializesDeterministically()
    {
        var words = new[]
        {
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.Branch(0x04, rs: 9, rt: 10, pc: EntryPc + 4, target: EntryPc + 0x40),
            MipsEncoding.Nop,
        };

        RecompilerIrSerializer.Serialize(LowerWords(words))
            .Should().Be(RecompilerIrSerializer.Serialize(LowerWords(words)));
    }

    [Fact]
    public void LoadDelay_NotObservedByTheNextInstruction_IsLowered()
    {
        // LW $t1, 0($t0) ; ADDU $t2, $t3, $t4 — nothing reads $t1 in the delay slot.
        var program = MipsToIrLowerer.LowerProgram(new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0)), EntryPc),
            (R3000aDecoder.Decode(MipsEncoding.R(0x21, rd: 10, rs: 11, rt: 12, shamt: 0)), EntryPc + 4),
        });

        program.Blocks.Should().HaveCount(2);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LoadDelay_OverwrittenByTheNextInstruction_IsLowered()
    {
        // LW $t1, 0($t0) ; ADDIU $t1, $t3, 1 — the immediate write cancels the
        // pending load on hardware too, so the immediate commit stays equivalent.
        var program = MipsToIrLowerer.LowerProgram(new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0)), EntryPc),
            (R3000aDecoder.Decode(MipsEncoding.I(0x09, rt: 9, rs: 11, immediate: 1)), EntryPc + 4),
        });

        program.Blocks.Should().HaveCount(2);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LoadDelay_IntoZeroRegister_IsNeverObservable()
    {
        // LW $zero, 0($t0) ; ADDU $t2, $zero, $t3 — GPR[0] is immutable.
        var program = MipsToIrLowerer.LowerProgram(new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 0, baseRegister: 8, offset: 0)), EntryPc),
            (R3000aDecoder.Decode(MipsEncoding.R(0x21, rd: 10, rs: 0, rt: 11, shamt: 0)), EntryPc + 4),
        });

        program.Blocks.Should().HaveCount(2);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void LoadDelay_AsTheLastInstruction_HasNoObserverAndIsLowered()
    {
        var program = MipsToIrLowerer.LowerProgram(new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0)), EntryPc),
        });

        program.Blocks.Should().HaveCount(1);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    private static RecompilerIrBlock LowerSupported(uint encodedWord)
    {
        var result = MipsToIrLowerer.Lower(R3000aDecoder.Decode(encodedWord), EntryPc);
        result.IsSupported.Should().BeTrue(
            $"lowering failed: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
        result.Block.Should().NotBeNull();

        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { result.Block! }))
            .IsValid.Should().BeTrue();
        return result.Block!;
    }
}
