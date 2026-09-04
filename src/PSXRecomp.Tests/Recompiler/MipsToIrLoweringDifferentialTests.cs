using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Differential tests for the memory and control-flow lowering: the same MIPS
/// program runs on the native R3000A interpreter and, lowered to IR, on the IR
/// evaluator, and the resulting GPRs and guest memory must agree. This is what
/// separates a lowering that has the right shape from one that has the right
/// meaning — sign extension, store width, branch outcome, delay-slot retirement,
/// and target arithmetic are all observable here.
/// </summary>
[Test]
public class MipsToIrLoweringDifferentialTests
{
    private const uint EntryPc = 0x80000000u;
    private const uint DataBase = 0x80001000u;
    private const byte BeqOpcodeField = 0x04;
    private const byte BneOpcodeField = 0x05;

    [Fact]
    public void Memory_LoadsAndStoresOfEveryWidth_MatchTheInterpreter()
    {
        // t0 = 0x80001000 ; t1 = 0xFFFFFFFF ; then store/load at each width.
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),               // LUI   $t0, 0x8000
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),               // ADDIU $t0, $t0, 0x1000
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0xFFFF),               // ADDIU $t1, $zero, -1
            MipsEncoding.Load(R3000aOpcode.Sb, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Lb, rt: 10, baseRegister: 8, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Lbu, rt: 11, baseRegister: 8, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Sh, rt: 9, baseRegister: 8, offset: 4),
            MipsEncoding.Load(R3000aOpcode.Lh, rt: 12, baseRegister: 8, offset: 4),
            MipsEncoding.Load(R3000aOpcode.Lhu, rt: 13, baseRegister: 8, offset: 4),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 8),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 14, baseRegister: 8, offset: 8),
        };

        var run = RunBoth(words, retiredInstructions: 11, dataWindowBytes: 16);

        // Sign-extending vs zero-extending loads are the point of the shift pair.
        run.Ir.Gpr[10].Should().Be(0xFFFFFFFFu, "LB sign-extends the stored byte");
        run.Ir.Gpr[11].Should().Be(0x000000FFu, "LBU zero-extends the stored byte");
        run.Ir.Gpr[12].Should().Be(0xFFFFFFFFu, "LH sign-extends the stored halfword");
        run.Ir.Gpr[13].Should().Be(0x0000FFFFu, "LHU zero-extends the stored halfword");
        run.Ir.Gpr[14].Should().Be(0xFFFFFFFFu);

        // SB and SH must not have widened past their access width.
        run.IrMemory[1].Should().Be(0);
        run.IrMemory[6].Should().Be(0);
    }

    [Fact]
    public void Memory_StoresNarrowerThanAWord_MatchTheInterpreterByteForByte()
    {
        // Store 0x11223344 as a word, then overwrite one byte and one halfword.
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
            MipsEncoding.I(0x0F, rt: 9, rs: 0, immediate: 0x1122),               // LUI   $t1, 0x1122
            MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 0x3344),               // ADDIU $t1, $t1, 0x3344
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Sb, rt: 9, baseRegister: 8, offset: 4),
            MipsEncoding.Load(R3000aOpcode.Sh, rt: 9, baseRegister: 8, offset: 8),
        };

        RunBoth(words, retiredInstructions: 7, dataWindowBytes: 12);
    }

    [Fact]
    public void Beq_Taken_SkipsTheFallThroughButRetiresTheDelaySlot()
    {
        var words = BuildConditionalBranch(BeqOpcodeField, leftValue: 5, rightValue: 5);

        var run = RunBoth(words, retiredInstructions: 5, dataWindowBytes: 0);

        run.Ir.Gpr[10].Should().Be(1u, "the delay slot always retires");
        run.Ir.Gpr[11].Should().Be(0u, "the fall-through path is skipped when taken");
        run.Ir.Gpr[13].Should().Be(7u, "the taken target runs");
    }

    [Fact]
    public void Beq_NotTaken_FallsThroughAfterTheDelaySlot()
    {
        var words = BuildConditionalBranch(BeqOpcodeField, leftValue: 5, rightValue: 6);

        var run = RunBoth(words, retiredInstructions: 7, dataWindowBytes: 0);

        run.Ir.Gpr[10].Should().Be(1u, "the delay slot always retires");
        run.Ir.Gpr[11].Should().Be(0xBADu, "the fall-through path runs when not taken");
        run.Ir.Gpr[13].Should().Be(7u);
    }

    [Fact]
    public void Bne_Taken_SkipsTheFallThrough()
    {
        var words = BuildConditionalBranch(BneOpcodeField, leftValue: 5, rightValue: 6);

        var run = RunBoth(words, retiredInstructions: 5, dataWindowBytes: 0);

        run.Ir.Gpr[11].Should().Be(0u);
        run.Ir.Gpr[13].Should().Be(7u);
    }

    [Fact]
    public void Bne_NotTaken_FallsThrough()
    {
        var words = BuildConditionalBranch(BneOpcodeField, leftValue: 5, rightValue: 5);

        var run = RunBoth(words, retiredInstructions: 7, dataWindowBytes: 0);

        run.Ir.Gpr[11].Should().Be(0xBADu);
    }

    [Fact]
    public void BackwardBranch_LoopsTheExpectedNumberOfTimes()
    {
        // t0 = 3 ; t1 = 0 ; loop { t1 += 10 ; t0 -= 1 } while (t0 != 0) ; t2 = 99
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 3),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0),
            MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 10),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0xFFFF),
            MipsEncoding.Branch(BneOpcodeField, rs: 8, rt: 0, pc: EntryPc + 0x10, target: EntryPc + 8),
            MipsEncoding.Nop,
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 99),
        };

        var run = RunBoth(words, retiredInstructions: 15, dataWindowBytes: 0);

        run.Ir.Gpr[8].Should().Be(0u);
        run.Ir.Gpr[9].Should().Be(30u, "the loop body ran three times");
        run.Ir.Gpr[10].Should().Be(99u);
    }

    [Fact]
    public void J_TransfersToTheComputedTargetAfterTheDelaySlot()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),      // 0x00
            MipsEncoding.Jump(EntryPc + 0x10),                     // 0x04
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),      // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD), // 0x0C skipped
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 4),     // 0x10 target
        };

        var run = RunBoth(words, retiredInstructions: 4, dataWindowBytes: 0);

        run.Ir.Gpr[8].Should().Be(1u);
        run.Ir.Gpr[9].Should().Be(2u, "the jump's delay slot always retires");
        run.Ir.Gpr[10].Should().Be(0u, "the instruction after the delay slot is skipped");
        run.Ir.Gpr[11].Should().Be(4u);
    }

    [Fact]
    public void Scenario_ArithmeticThenCompareThenBranch_MatchesTheInterpreter()
    {
        // t0 = 6 ; t1 = 2 ; t2 = t0 - t1 ; if (t2 == t3) skip ; ...
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 6),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),
            MipsEncoding.R(0x23, rd: 10, rs: 8, rt: 9, shamt: 0),                            // SUBU $t2, $t0, $t1
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 4),                               // $t3 = 4
            MipsEncoding.Branch(BeqOpcodeField, rs: 10, rt: 11, pc: EntryPc + 0x10, target: EntryPc + 0x1C),
            MipsEncoding.R(0x21, rd: 12, rs: 8, rt: 9, shamt: 0),                            // delay slot: ADDU $t4
            MipsEncoding.I(0x09, rt: 13, rs: 0, immediate: 0xBAD),                           // fall-through
            MipsEncoding.I(0x09, rt: 14, rs: 0, immediate: 1),                               // target
        };

        var run = RunBoth(words, retiredInstructions: 7, dataWindowBytes: 0);

        run.Ir.Gpr[10].Should().Be(4u);
        run.Ir.Gpr[12].Should().Be(8u, "the delay slot retires before the transfer");
        run.Ir.Gpr[13].Should().Be(0u, "the branch was taken");
        run.Ir.Gpr[14].Should().Be(1u);
    }

    [Fact]
    public void Scenario_AddressCalculationThenLoadStoreThenBranch_MatchesTheInterpreter()
    {
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),                           // LUI   $t0, 0x8000
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),                           // ADDIU $t0, $t0, 0x1000
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),                           // $t1 = 0x1234
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),
            MipsEncoding.Nop,                                                                // load-delay slot
            MipsEncoding.Branch(BeqOpcodeField, rs: 10, rt: 9, pc: EntryPc + 0x18, target: EntryPc + 0x24),
            MipsEncoding.Nop,                                                                // branch delay slot
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 0xBAD),                           // fall-through
            MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 1),                               // target
        };

        var run = RunBoth(words, retiredInstructions: 9, dataWindowBytes: 8);

        run.Ir.Gpr[10].Should().Be(0x1234u);
        run.Ir.Gpr[11].Should().Be(0u, "the reloaded value equals the stored one, so the branch is taken");
        run.Ir.Gpr[12].Should().Be(1u);
    }

    [Fact]
    public void LoadDelay_DependentInstruction_ReadsThePreLoadValue()
    {
        // The instruction in the load-delay slot must observe the *old* $t2, and
        // the one after it the loaded value (docs/cpu/pipeline.md).
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),               // $t0 = DataBase
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x55),                // pre-load $t2
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),
            MipsEncoding.R(0x21, rd: 11, rs: 10, rt: 0, shamt: 0),               // load-delay slot
            MipsEncoding.R(0x21, rd: 12, rs: 10, rt: 0, shamt: 0),               // after the delay
        };

        var run = RunBoth(words, retiredInstructions: 8, dataWindowBytes: 4);

        run.Ir.Gpr[11].Should().Be(0x55u, "the load-delay slot reads the pre-load register");
        run.Ir.Gpr[12].Should().Be(0x1234u, "the load has committed by the next instruction");
        run.Ir.Gpr[10].Should().Be(0x1234u);
    }

    [Fact]
    public void LoadDelay_IndependentThenDependentInstruction_MatchesTheInterpreter()
    {
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x55),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 13, rs: 0, immediate: 9),                   // independent delay slot
            MipsEncoding.R(0x21, rd: 11, rs: 10, rt: 0, shamt: 0),               // dependent, after the delay
        };

        var run = RunBoth(words, retiredInstructions: 8, dataWindowBytes: 4);

        run.Ir.Gpr[13].Should().Be(9u);
        run.Ir.Gpr[11].Should().Be(0x1234u, "the load delay is over by the second instruction");
    }

    [Fact]
    public void LoadDelay_ThenTakenBranch_ComparesThePreLoadValue()
    {
        // $t3 equals the *pre-load* $t2, so the branch is taken only if the BEQ
        // in the load-delay slot reads the old register. Committing the load
        // early would make it fall through instead.
        var run = RunBoth(BuildLoadDelayBranch(compareValue: 0x55), retiredInstructions: 10, dataWindowBytes: 4);

        run.Ir.Gpr[10].Should().Be(0x1234u, "the load commits before the branch delay slot");
        run.Ir.Gpr[12].Should().Be(1u, "the branch delay slot always retires");
        run.Ir.Gpr[13].Should().Be(0u, "the taken branch skips the fall-through");
        run.Ir.Gpr[14].Should().Be(7u);
    }

    [Fact]
    public void LoadDelay_ThenNotTakenBranch_ComparesThePreLoadValue()
    {
        // $t3 equals the *loaded* value, so a lowering that committed the load
        // early would take the branch; hardware does not.
        var run = RunBoth(BuildLoadDelayBranch(compareValue: 0x1234), retiredInstructions: 11, dataWindowBytes: 4);

        run.Ir.Gpr[10].Should().Be(0x1234u);
        run.Ir.Gpr[12].Should().Be(1u);
        run.Ir.Gpr[13].Should().Be(0xBADu, "the branch was not taken, so the fall-through runs");
        run.Ir.Gpr[14].Should().Be(7u);
    }

    [Fact]
    public void LoadDelay_WriteInTheDelaySlot_CancelsThePendingLoad()
    {
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x55),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 10, rs: 10, immediate: 1),                  // ADDIU $t2, $t2, 1
            MipsEncoding.R(0x21, rd: 11, rs: 10, rt: 0, shamt: 0),
        };

        var run = RunBoth(words, retiredInstructions: 8, dataWindowBytes: 4);

        run.Ir.Gpr[10].Should().Be(0x56u, "the delay-slot write reads the pre-load value and cancels the load");
        run.Ir.Gpr[11].Should().Be(0x56u);
    }

    [Fact]
    public void LoadDelay_IntoZeroRegister_PerformsTheAccessAndDiscardsTheValue()
    {
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 0, baseRegister: 8, offset: 0),
            MipsEncoding.R(0x21, rd: 11, rs: 0, rt: 0, shamt: 0),
        };

        var run = RunBoth(words, retiredInstructions: 6, dataWindowBytes: 4);

        run.Ir.Gpr[0].Should().Be(0u, "GPR[0] is immutable");
        run.Ir.Gpr[11].Should().Be(0u);
    }

    [Fact]
    public void LoadDelay_AtTerminationCommitsOneInstructionLater()
    {
        // The lowered program commits a trailing load at the end of the load's own
        // block, because there is no in-stream instruction to carry the delay. The
        // interpreter needs one more step to reach the same state — which is the
        // one-step offset every differential run here accounts for.
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x55),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),
        };

        var atTheLoad = RunInterpreter(words, stepBudget: 6, dataWindowBytes: 4);
        atTheLoad.Gpr[10].Should().Be(0x55u, "the load is still pending when its own step retires");

        var run = RunBoth(words, retiredInstructions: 6, dataWindowBytes: 4);
        run.Ir.Gpr[10].Should().Be(0x1234u);
    }

    [Fact]
    public void Stores_ToOverlappingAddresses_RetireInProgramOrder()
    {
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
            MipsEncoding.I(0x0F, rt: 9, rs: 0, immediate: 0x1122),
            MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 0x3344),               // $t1 = 0x11223344
            MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x00AA),
            MipsEncoding.Load(R3000aOpcode.Sb, rt: 10, baseRegister: 8, offset: 0),
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 0x5566),
            MipsEncoding.Load(R3000aOpcode.Sh, rt: 11, baseRegister: 8, offset: 0),
        };

        var run = RunBoth(words, retiredInstructions: 9, dataWindowBytes: 4);

        // The halfword store retires last and overwrites the byte store; the two
        // high bytes of the word store survive.
        run.IrMemory.Should().Equal(new byte[] { 0x66, 0x55, 0x22, 0x11 });
    }

    [Fact]
    public void Jal_LinksPcPlusEightAndTransfersAfterItsDelaySlot()
    {
        var callee = EntryPc + 0x10;
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                    // 0x00
            MipsEncoding.JumpAndLink(callee),                                    // 0x04
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                    // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),               // 0x0C return address, not run here
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 3),                   // 0x10 callee
        };

        var run = RunBoth(words, retiredInstructions: 4, dataWindowBytes: 0);

        run.Ir.Gpr[31].Should().Be(EntryPc + 0x0C, "JAL links the branch address + 8");
        run.Ir.Gpr[9].Should().Be(2u, "the call's delay slot always retires");
        run.Ir.Gpr[10].Should().Be(0u, "control transferred to the callee, not to the return address");
        run.Ir.Gpr[11].Should().Be(3u);
    }

    [Fact]
    public void JalThenJrRa_ReturnsToTheLinkedAddress()
    {
        // JAL links 0x0C; the callee's JR $ra leaves the lowered program, and the
        // interpreter's PC at that same boundary is the linked address.
        var callee = EntryPc + 0x14;
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                    // 0x00
            MipsEncoding.JumpAndLink(callee),                                    // 0x04
            MipsEncoding.Nop,                                                    // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 5),                    // 0x0C return address
            MipsEncoding.Nop,                                                    // 0x10
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 7),                   // 0x14 callee
            MipsEncoding.JumpRegister(rs: 31),                                   // 0x18
            MipsEncoding.Nop,                                                    // 0x1C return delay slot
        };

        // 0x00, 0x04, 0x08, 0x14, 0x18, 0x1C — the IR stops at the indirect flow.
        var run = RunBoth(
            words,
            retiredInstructions: 6,
            dataWindowBytes: 0,
            interpreterExtraSteps: 0,
            expectedIrTermination: RecompilerIrTerminationReason.UnresolvedIndirectFlow);

        run.Ir.Gpr[31].Should().Be(EntryPc + 0x0C);
        run.Ir.Gpr[10].Should().Be(7u, "the callee ran");
        run.Ir.Gpr[9].Should().Be(0u, "the IR run stops before the return lands");
        run.InterpreterPc.Should().Be(run.Ir.Gpr[31], "the interpreter returns to the linked address");
    }

    [Fact]
    public void Jalr_LinksThenLeavesTheProgramThroughItsRegisterTarget()
    {
        var words = new[]
        {
            MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),               // 0x00
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x0018),               // 0x04 $t0 = EntryPc + 0x18
            MipsEncoding.JumpAndLinkRegister(rd: 31, rs: 8),                     // 0x08
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 4),                    // 0x0C delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),               // 0x10
            MipsEncoding.Nop,                                                    // 0x14
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 6),                   // 0x18 register target
        };

        var run = RunBoth(
            words,
            retiredInstructions: 4,
            dataWindowBytes: 0,
            interpreterExtraSteps: 0,
            expectedIrTermination: RecompilerIrTerminationReason.UnresolvedIndirectFlow);

        run.Ir.Gpr[31].Should().Be(EntryPc + 0x10, "JALR links the branch address + 8");
        run.Ir.Gpr[9].Should().Be(4u, "the delay slot retires before the transfer");
        run.Ir.Gpr[11].Should().Be(0u, "the IR does not follow a register-held target");
        run.InterpreterPc.Should().Be(EntryPc + 0x18, "the interpreter transfers to the register target");
    }

    [Fact]
    public void BoundedLoop_StopsOnTheBlockBudgetWithTheInterpreterState()
    {
        // An unbounded loop: the IR run must terminate on its budget rather than
        // spin, and the state at that boundary must match the interpreter's.
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 0),                            // 0x00
            MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 1),                            // 0x04 loop body
            MipsEncoding.Branch(BeqOpcodeField, rs: 0, rt: 0, pc: EntryPc + 8, target: EntryPc + 4),
            MipsEncoding.Nop,                                                            // 0x0C delay slot
        };

        // Five retired blocks: 0x00, 0x04, (0x08+0x0C), 0x04, (0x08+0x0C) = seven
        // retired MIPS instructions.
        var ir = RunLoweredIr(words, blockBudget: 5, dataWindowBytes: 0,
            RecompilerIrTerminationReason.ExecutionBudgetExceeded);
        var interpreter = RunInterpreter(words, stepBudget: 7, dataWindowBytes: 0);

        ir.Gpr.Should().Equal(interpreter.Gpr);
        ir.Result.Pc.Should().Be(interpreter.Pc, "both stop at the top of the loop body");
        ir.Result.Termination.Should().Be(RecompilerIrTerminationReason.ExecutionBudgetExceeded);
        ir.Gpr[8].Should().Be(2u);
    }

    /// <summary>
    /// LW into $t2 followed by a BEQ in its load-delay slot, comparing against
    /// <paramref name="compareValue"/> in $t3. Layout: the load at 0x18, the branch
    /// at 0x1C, its delay slot at 0x20, the fall-through at 0x24 and the taken
    /// target at 0x28.
    /// </summary>
    private static uint[] BuildLoadDelayBranch(ushort compareValue) =>
    [
        MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),                            // 0x00
        MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),                            // 0x04
        MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0x1234),                            // 0x08
        MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),            // 0x0C
        MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x55),                             // 0x10 pre-load $t2
        MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: compareValue),                     // 0x14 $t3
        MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),           // 0x18
        MipsEncoding.Branch(BeqOpcodeField, rs: 10, rt: 11, pc: EntryPc + 0x1C, target: EntryPc + 0x28),
        MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 1),                                // 0x20 delay slot
        MipsEncoding.I(0x09, rt: 13, rs: 0, immediate: 0xBAD),                            // 0x24 fall-through
        MipsEncoding.I(0x09, rt: 14, rs: 0, immediate: 7),                                // 0x28 target
    ];

    /// <summary>
    /// t0 = <paramref name="leftValue"/>, t1 = <paramref name="rightValue"/>, then
    /// branch over the fall-through block. Layout: branch at 0x08, delay slot at
    /// 0x0C, fall-through at 0x10/0x14, target at 0x18.
    /// </summary>
    private static uint[] BuildConditionalBranch(byte opcodeField, ushort leftValue, ushort rightValue) =>
    [
        MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: leftValue),
        MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: rightValue),
        MipsEncoding.Branch(opcodeField, rs: 8, rt: 9, pc: EntryPc + 8, target: EntryPc + 0x18),
        MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 1),
        MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 0xBAD),
        MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 0xBAD),
        MipsEncoding.I(0x09, rt: 13, rs: 0, immediate: 7),
    ];

    /// <summary>
    /// Runs <paramref name="words"/> on the native interpreter and on the lowered
    /// IR, then asserts that the GPR file and the compared data window agree.
    /// </summary>
    /// <param name="retiredInstructions">
    /// How many MIPS instructions the program retires. The interpreter gets one
    /// extra step so that a trailing R3000A load delay commits before the state is
    /// read; the extra step lands on zeroed RAM, which decodes as NOP.
    /// </param>
    private static DifferentialRun RunBoth(uint[] words, uint retiredInstructions, uint dataWindowBytes) =>
        RunBoth(words, retiredInstructions, dataWindowBytes, interpreterExtraSteps: 1);

    /// <summary>
    /// The general form. <paramref name="interpreterExtraSteps"/> is 0 when the
    /// comparison boundary must be exact — a program whose IR run stops at an
    /// unresolved indirect transfer has no trailing load delay to settle, and an
    /// extra interpreter step would run instructions the IR never reached.
    /// </summary>
    private static DifferentialRun RunBoth(
        uint[] words,
        uint retiredInstructions,
        uint dataWindowBytes,
        uint interpreterExtraSteps,
        RecompilerIrTerminationReason expectedIrTermination = RecompilerIrTerminationReason.Success)
    {
        var interpreter = RunInterpreter(words, retiredInstructions + interpreterExtraSteps, dataWindowBytes);
        var ir = RunLoweredIr(words, retiredInstructions, dataWindowBytes, expectedIrTermination);

        ir.Gpr.Should().Equal(interpreter.Gpr, "the lowered IR must agree with the native interpreter on every GPR");
        ir.Memory.Should().Equal(interpreter.Memory, "the lowered IR must agree with the interpreter on guest memory");

        return new DifferentialRun(ir.Result, ir.Memory, interpreter.Pc);
    }

    private static (uint[] Gpr, byte[] Memory, uint Pc) RunInterpreter(uint[] words, uint stepBudget, uint dataWindowBytes)
    {
        using var core = new PSXCoreWrapper();
        core.Reset();

        var programBase = RecompilerGuestMemory.Translate(EntryPc);
        for (var i = 0; i < words.Length; i++)
        {
            core.WriteMemory32(programBase + (uint)(i * 4), words[i]);
        }

        core.Pc = EntryPc;
        for (uint step = 0; step < stepBudget; step++)
        {
            core.Step().Should().Be(0, "the fixture must not raise an exception on the interpreter");
        }

        var gpr = new uint[RecompilerDifferentialFixture.GprCount];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = core.GetGpr(i);
        }

        var dataBase = RecompilerGuestMemory.Translate(DataBase);
        var memory = new byte[dataWindowBytes];
        for (uint i = 0; i < dataWindowBytes; i++)
        {
            memory[i] = core.ReadMemory8(dataBase + i);
        }

        return (gpr, memory, core.Pc);
    }

    private static (uint[] Gpr, byte[] Memory, RecompilerIrEvaluationResult Result) RunLoweredIr(
        uint[] words,
        uint blockBudget,
        uint dataWindowBytes,
        RecompilerIrTerminationReason expectedTermination = RecompilerIrTerminationReason.Success)
    {
        var instructions = words
            .Select((word, index) => (R3000aDecoder.Decode(word), EntryPc + (uint)(index * 4)))
            .ToArray();

        var program = MipsToIrLowerer.LowerProgram(instructions);
        RecompilerIrValidator.Validate(program).IsValid
            .Should().BeTrue("the lowered program must satisfy the IR validator");

        var memory = new RecompilerGuestMemory();
        var result = RecompilerIrEvaluator.Run(
            program,
            EntryPc,
            new uint[RecompilerDifferentialFixture.GprCount],
            memory,
            blockBudget);

        result.Termination.Should().Be(expectedTermination);

        var window = new byte[dataWindowBytes];
        for (uint i = 0; i < dataWindowBytes; i++)
        {
            window[i] = memory.Read8(DataBase + i);
        }

        return (result.Gpr.ToArray(), window, result);
    }

    private sealed record DifferentialRun(RecompilerIrEvaluationResult Ir, byte[] IrMemory, uint InterpreterPc);
}
