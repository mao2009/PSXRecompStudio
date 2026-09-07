using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Common synthetic fixtures shared by the #211 differential harness and the
// #209 vertical-slice tests, so both sides of the comparison consume the same
// input (Issue #211 "common test fixture"). Stage A is GPR-only arithmetic;
// Stage B (memory, Issue #209) provides store/load and load-delay fixtures with
// a memory window; Stage C (control flow) provides branch / call / loop fixtures
// whose two budgets (host blocks vs interpreter instructions) differ.
internal static class RecompilerFixtures
{
    private const uint EntryPc = 0x80000000u;

    // ADDIU $t0, $zero, 5   ($t0 = GPR8  = 5)
    // ADDIU $t1, $zero, 7   ($t1 = GPR9  = 7)
    // ADDU  $t3, $t0, $t1   ($t3 = GPR11 = 12)
    public static readonly uint[] AddThreeWords =
    {
        0x24080005u,
        0x24090007u,
        0x01095821u,
    };

    public static RecompilerDifferentialFixture AddThree() =>
        new("add-three", AddThreeWords, EntryPc, stepBudget: 3);

    // The Issue #209 example: ADDIU t0,zero,1; ADDIU t1,zero,2; ADDU t2,t0,t1 → t2=3.
    public static readonly uint[] Issue209Words =
    {
        0x24080001u,
        0x24090002u,
        0x01095021u,
    };

    public static RecompilerDifferentialFixture Issue209Add() =>
        new("issue-209-add", Issue209Words, EntryPc, stepBudget: 3);

    // ---- Stage B: memory (Issue #209) ----

    /// <summary>The guest address of the Stage B/C data area (bytes the tests sample).</summary>
    public const uint DataBase = 0x80001000u;

    /// <summary>
    /// Stores a 32-bit value as a word, halfword and byte, then loads each width
    /// back, proving store width, endianness (little-endian byte order) and the
    /// load hold. The memory window samples the stored bytes after execution.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209MemoryRoundTrip() =>
        new(
            "issue-209-memory-round-trip",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),               // LUI    $t0, 0x8000
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),               // ADDIU  $t0, $t0, 0x1000
                MipsEncoding.I(0x0F, rt: 9, rs: 0, immediate: 0x1122),               // LUI    $t1, 0x1122
                MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 0x3344),               // ADDIU  $t1, $t1, 0x3344
                MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),   // SW $t1, 0($t0)
                MipsEncoding.Load(R3000aOpcode.Sh, rt: 9, baseRegister: 8, offset: 4),   // SH $t1, 4($t0)
                MipsEncoding.Load(R3000aOpcode.Sb, rt: 9, baseRegister: 8, offset: 6),   // SB $t1, 6($t0)
                MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),  // LW $t2, 0($t0)
                MipsEncoding.Load(R3000aOpcode.Lh, rt: 11, baseRegister: 8, offset: 4),  // LH $t3, 4($t0)
                MipsEncoding.Load(R3000aOpcode.Lbu, rt: 12, baseRegister: 8, offset: 6), // LBU $t4, 6($t0)
                MipsEncoding.Nop,
            },
            entryPc: EntryPc,
            stepBudget: 11,
            memoryWindow: new uint[]
            {
                DataBase,
                DataBase + 1,
                DataBase + 2,
                DataBase + 3,
                DataBase + 4,
                DataBase + 5,
                DataBase + 6,
                DataBase + 7,
            });

    /// <summary>
    /// A load whose target register is read in the load-delay slot: the delay-slot
    /// instruction must observe the pre-load value and the one after it the loaded
    /// value (docs/cpu/pipeline.md). ReferenceStepBudget is one more than the host
    /// block budget because the load and its observer fuse into one host block.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209LoadDelay() =>
        new(
            "issue-209-load-delay",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
                MipsEncoding.I(0x0F, rt: 9, rs: 0, immediate: 0x1234),
                MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 0x5678),                // $t1 = 0x12345678
                MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 0),
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0x55),                 // pre-load $t2
                MipsEncoding.Load(R3000aOpcode.Lw, rt: 10, baseRegister: 8, offset: 0),
                MipsEncoding.R(0x21, rd: 11, rs: 10, rt: 0, shamt: 0),                // ADDU $t3, $t2, 0 (delay slot)
                MipsEncoding.R(0x21, rd: 12, rs: 10, rt: 0, shamt: 0),                // ADDU $t4, $t2, 0 (after the delay)
            },
            entryPc: EntryPc,
            stepBudget: 8,
            referenceStepBudget: 9);

    /// <summary>
    /// Reads pre-initialized guest memory at every load width, proving that the
    /// generated host run starts from the same RAM content as the interpreter and
    /// that zero- vs sign-extending loads differ (LHU/LH on a byte whose top bit is
    /// set). The data is provided by <see cref="RecompilerDifferentialFixture.InitialMemory"/>
    /// and verified through both registers and the memory window.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209MemoryInitLoads() =>
        new(
            "issue-209-memory-init-loads",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x1000),
                MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0),   // $t1 = 0x12345678
                MipsEncoding.Load(R3000aOpcode.Lhu, rt: 10, baseRegister: 8, offset: 4), // $t2 = 0x00008180
                MipsEncoding.Load(R3000aOpcode.Lh, rt: 11, baseRegister: 8, offset: 4),  // $t3 = 0xFFFF8180
                MipsEncoding.Nop,
            },
            entryPc: EntryPc,
            stepBudget: 6,
            initialMemory: new[]
            {
                new RecompilerInitialMemoryItem(DataBase, 0x78),
                new RecompilerInitialMemoryItem(DataBase + 1, 0x56),
                new RecompilerInitialMemoryItem(DataBase + 2, 0x34),
                new RecompilerInitialMemoryItem(DataBase + 3, 0x12),
                new RecompilerInitialMemoryItem(DataBase + 4, 0x80),
                new RecompilerInitialMemoryItem(DataBase + 5, 0x81),
            },
            memoryWindow: new uint[]
            {
                DataBase,
                DataBase + 1,
                DataBase + 2,
                DataBase + 3,
                DataBase + 4,
                DataBase + 5,
            });

    // ---- Stage C: control flow (Issue #209) ----

    /// <summary>
    /// A taken BEQ: the delay slot always retires, the fall-through is skipped and
    /// control lands on the target. The host retires four fused/straight blocks
    /// while the interpreter retires five MIPS instructions.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209BranchTaken() =>
        Issue209Branch(leftValue: 5, rightValue: 5, name: "issue-209-branch-taken");

    /// <summary>
    /// A not-taken BEQ: the delay slot retires and control falls through into the
    /// target block, for five host blocks vs six interpreter instructions.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209BranchNotTaken() =>
        Issue209Branch(leftValue: 5, rightValue: 7, name: "issue-209-branch-not-taken");

    private static RecompilerDifferentialFixture Issue209Branch(ushort leftValue, ushort rightValue, string name) =>
        new(
            name,
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: leftValue),                // 0x00
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: rightValue),               // 0x04
                MipsEncoding.Branch(0x04, rs: 8, rt: 9, pc: EntryPc + 8, target: EntryPc + 0x14),
                MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 1),                       // 0x0C delay slot -> $t3
                MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 0xBAD),                   // 0x10 fall-through -> $t4
                MipsEncoding.I(0x09, rt: 13, rs: 0, immediate: 9),                       // 0x14 target -> $t5
            },
            entryPc: EntryPc,
            stepBudget: leftValue == rightValue ? 4u : 5u,
            referenceStepBudget: leftValue == rightValue ? 5u : 6u);

    /// <summary>
    /// A JAL that links PC+8 into $ra, transfers to a two-instruction callee and
    /// never returns (the return address instruction is never executed). The host
    /// retires four blocks (the JAL fuses with its delay slot); the interpreter
    /// retires five MIPS instructions.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209Jal() =>
        new(
            "issue-209-jal",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                        // 0x00
                MipsEncoding.JumpAndLink(EntryPc + 0x10),                                // 0x04 JAL
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                        // 0x08 delay slot
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),                   // 0x0C $ra (not executed)
                MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 3),                       // 0x10 callee
                MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 4),                       // 0x14 callee
            },
            entryPc: EntryPc,
            stepBudget: 4,
            referenceStepBudget: 5);

    /// <summary>
    /// A plain J to a one-instruction target: the delay slot retires, the
    /// fall-through is skipped and control lands on the target. The host retires
    /// three blocks (the J fuses with its delay slot); the interpreter retires
    /// four MIPS instructions.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209Jump() =>
        new(
            "issue-209-jump",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                        // 0x00
                MipsEncoding.Jump(EntryPc + 0x10),                                       // 0x04 J
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                        // 0x08 delay slot
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),                   // 0x0C (skipped)
                MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 3),                       // 0x10 target
            },
            entryPc: EntryPc,
            stepBudget: 3,
            referenceStepBudget: 4);

    /// <summary>
    /// A JR whose target is held in a register. The native interpreter can follow
    /// it, but the recompiled host cannot statically resolve an indirect target, so
    /// it must fail closed with UnresolvedIndirectFlow rather than invent a
    /// transfer. This fixture is a host-side classification check, not a state
    /// match against the interpreter.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209IndirectJump() =>
        new(
            "issue-209-indirect-jump",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),                   // 0x00 LUI $t0, 0x8000
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x14),                     // 0x04 ADDIU $t0, $t0, 0x14
                MipsEncoding.JumpRegister(rs: 8),                                        // 0x08 JR $t0
                MipsEncoding.Nop,                                                        // 0x0C delay slot
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 3),                        // 0x10 (not reached by host)
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 4),                       // 0x14 $t0 target
            },
            entryPc: EntryPc,
            stepBudget: 3,
            referenceStepBudget: 5);

    /// <summary>
    /// A bounded backward NBNE loop (a triple-counted counter) followed by a final
    /// instruction. Straight-line instructions stay separate host blocks (only
    /// control-transfer and load-delay pairs fuse), so each of the three
    /// iterations retires three host blocks and the whole program twelve: prologue
    /// {0x00, 0x04}, three iterations of {0x08, 0x0C, {0x10, 0x14} fused}, then the
    /// final {0x18}. The interpreter retires fifteen MIPS instructions.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209BoundedLoop() =>
        new(
            "issue-209-bounded-loop",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 3),                        // 0x00 $t0 = 3
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0),                        // 0x04 $t1 = 0
                MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 10),                       // 0x08 loop body: $t1 += 10
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0xFFFF),                   // 0x0C $t0 -= 1
                MipsEncoding.Branch(0x05, rs: 8, rt: 0, pc: EntryPc + 0x10, target: EntryPc + 8),
                MipsEncoding.Nop,                                                        // 0x14 delay slot
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 99),                      // 0x18 $t2 = 99
            },
            entryPc: EntryPc,
            stepBudget: 12,
            referenceStepBudget: 15);

    /// <summary>
    /// An unbounded BEQ loop that never exits. Both sides must stop on their budget
    /// with the identical state (termination ExecutionBudgetExceeded, PC back at the
    /// loop top) rather than spin or fall through.
    /// </summary>
    public static RecompilerDifferentialFixture Issue209UnboundedLoop() =>
        new(
            "issue-209-unbounded-loop",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 0),                        // 0x00 $t0 = 0
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 1),                        // 0x04 loop body: $t0 += 1
                MipsEncoding.Branch(0x04, rs: 0, rt: 0, pc: EntryPc + 8, target: EntryPc + 4),
                MipsEncoding.Nop,                                                        // 0x0C delay slot
            },
            entryPc: EntryPc,
            stepBudget: 2,
            referenceStepBudget: 2);
}
