// Native tests for the PSXCpu branch / jump (BEQ..BGEZAL, J/JAL/JR/JALR)
// migration surface (src/psx_cpu_control.cpp; Rust migration slice #526). The
// existing tests below were moved verbatim out of test_psx_core.cpp (Issue
// #524). New tests for this slice are added to this file only and called from
// run_psx_cpu_control_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "test_harness.h"

static void test_step_branch() {
    TEST("BEQ/BNE/BLEZ/BGTZ/BLTZ/BGEZ instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 10);

    // BEQ $1, $2, offset=2 (branch taken) with NOP delay slot
    // Delay slot at PC=4 executes first, then PC = 0 + 4 + 2*4 = 12
    PSXCore_WriteMemory32(core, 0, 0x10220002u);
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u); // in delay slot
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 12u); // branch taken (distinct from 8)

    // BEQ $1, $2, offset=2 (not taken, different values)
    // Delay slot executes, then falls through past the slot: 4 + 4 = 8
    PSXCore_SetGPR(core, 2, 20);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 8u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_step_jump() {
    TEST("J/JAL/JR/JALR instructions");
    PSXCore* core = PSXCore_Create();

    // J target=3 -> PC = ((0+4) & 0xF0000000) | (3 << 2) = 12, after the delay slot
    PSXCore_WriteMemory32(core, 0, 0x08000003u);
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u); // delay slot
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 12u); // jumped to target (distinct from 8)

    // JAL target=3 -> $ra = PC+8 = 8, lands on the same target 12
    PSXCore_WriteMemory32(core, 0, 0x0C000003u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 8u);
    ASSERT_EQ(PSXCore_GetPC(core), 4u); // delay slot
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 12u); // jumped to target (distinct from 8)

    PSXCore_Destroy(core);
    PASS();
}

static void test_step_jr_jalr() {
    TEST("JR/JALR execute their delay slot before landing");
    PSXCore* core = PSXCore_Create();

    // JR $5 (target = 0x30), NOP delay slot at addr 4
    PSXCore_SetGPR(core, 5, 0x30);
    PSXCore_WriteMemory32(core, 0, 0x00A00008u); // JR $5
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u); // delay slot
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 0x30u); // jumped to GPR[5]

    // JALR $6, $5 (target = 0x30, $6 = PC+8 = 8)
    PSXCore_WriteMemory32(core, 0, 0x00A03009u); // JALR $6, $5
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 6), 8u);
    ASSERT_EQ(PSXCore_GetPC(core), 4u); // delay slot
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 0x30u); // jumped to GPR[5]

    PSXCore_Destroy(core);
    PASS();
}

static void test_branch_load_delay_interaction() {
    TEST("Link writes and load delay interaction (pipeline.md BIOS pattern)");
    PSXCore* core = PSXCore_Create();

    // BLTZAL links $31 (PC+8) even when the branch is not taken.
    PSXCore_SetGPR(core, 1, 10); // >= 0, not taken
    PSXCore_WriteMemory32(core, 0, 0x04300002u); // BLTZAL $1, 2
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 8u); // linked
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 8u); // not taken: delay slot + 4

    // BGEZAL links $31 and is taken.
    PSXCore_SetGPR(core, 1, 5); // >= 0, taken
    PSXCore_WriteMemory32(core, 0, 0x04310002u); // BGEZAL $1, 2
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 8u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 12u); // taken: 0 + 4 + 2*4

    // LW $31 followed by JAL in the load delay slot: the JAL link (PC+8) must
    // win over the pending load value (docs/cpu/pipeline.md, BIOS pattern).
    PSXCore_SetGPR(core, 31, 0);
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_WriteMemory32(core, 0x1000, 0xDEADBEEFu);
    PSXCore_WriteMemory32(core, 0, 0x8FBF0000u); // LW $31, 0($29)
    PSXCore_WriteMemory32(core, 4, 0x0C000005u); // JAL 5 (target = 20)
    PSXCore_WriteMemory32(core, 8, 0x00000000u); // NOP delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core); // LW: $31 still old (0)
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 0u);
    PSXCore_Step(core); // JAL in load delay slot: $31 = PC+8 = 12
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 12u); // link wins over pending load
    ASSERT_EQ(PSXCore_GetPC(core), 8u);
    PSXCore_Step(core); // delay slot
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 12u); // loaded value never committed
    ASSERT_EQ(PSXCore_GetPC(core), 20u); // target

    PSXCore_Destroy(core);
    PASS();
}

// Runs `instruction` at PC 0 with $1 = rs_value and a NOP delay slot, and
// returns the PC after the delay slot: 12 when an offset-2 branch is taken,
// 8 when it is not.
static uint32_t run_branch(PSXCore* core, uint32_t instruction, uint32_t rs_value) {
    PSXCore_SetGPR(core, 1, rs_value);
    PSXCore_WriteMemory32(core, 0, instruction);
    PSXCore_WriteMemory32(core, 4, 0x00000000u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    return PSXCore_GetPC(core);
}

static void test_rust_branch_zero_compares_are_signed() {
    TEST("BLEZ/BGTZ/BLTZ/BGEZ compare rs as signed (Rust, #526)");
    PSXCore* core = PSXCore_Create();
    const uint32_t blez = 0x18200002u, bgtz = 0x1C200002u; // $1, offset 2
    const uint32_t bltz = 0x04200002u, bgez = 0x04210002u;

    // 0x80000000 is negative, not a large positive value.
    ASSERT_EQ(run_branch(core, blez, 0x80000000u), 12u);
    ASSERT_EQ(run_branch(core, bgtz, 0x80000000u), 8u);
    ASSERT_EQ(run_branch(core, bltz, 0x80000000u), 12u);
    ASSERT_EQ(run_branch(core, bgez, 0x80000000u), 8u);
    // Zero boundary.
    ASSERT_EQ(run_branch(core, blez, 0u), 12u);
    ASSERT_EQ(run_branch(core, bgtz, 0u), 8u);
    ASSERT_EQ(run_branch(core, bltz, 0u), 8u);
    ASSERT_EQ(run_branch(core, bgez, 0u), 12u);
    // Largest positive.
    ASSERT_EQ(run_branch(core, blez, 0x7FFFFFFFu), 8u);
    ASSERT_EQ(run_branch(core, bgtz, 0x7FFFFFFFu), 12u);
    // BNE $1, $2 (not equal -> taken; equal -> not taken)
    PSXCore_SetGPR(core, 2, 0x80000000u);
    ASSERT_EQ(run_branch(core, 0x14220002u, 0x7FFFFFFFu), 12u);
    ASSERT_EQ(run_branch(core, 0x14220002u, 0x80000000u), 8u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_rust_branch_negative_offset_and_jump_region() {
    TEST("Backward branch offset and J upper-PC region (Rust, #526)");
    PSXCore* core = PSXCore_Create();

    // BEQ $0, $0, -2 at 0x100 -> 0x100 + 4 - 8 = 0xFC
    PSXCore_WriteMemory32(core, 0x100, 0x1000FFFEu);
    PSXCore_WriteMemory32(core, 0x104, 0x00000000u);
    PSXCore_SetPC(core, 0x100);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 0xFCu);

    // J 0x800 at KSEG0 0x80001000 keeps the 0x8 region: 0x80000000 | 0x2000
    PSXCore_WriteMemory32(core, 0x1000, 0x08000800u);
    PSXCore_WriteMemory32(core, 0x1004, 0x00000000u);
    PSXCore_SetPC(core, 0x80001000u);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80002000u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_rust_link_reads_source_before_linking() {
    TEST("BGEZAL/BLTZAL $31 and JALR rd==rs use the old source value (Rust, #526)");
    PSXCore* core = PSXCore_Create();

    // BGEZAL $31, 2 with $31 = -1: decided on the old -1 (not taken) even
    // though the link it writes (8) would be >= 0.
    PSXCore_SetGPR(core, 31, 0xFFFFFFFFu);
    PSXCore_WriteMemory32(core, 0, 0x07F10002u);
    PSXCore_WriteMemory32(core, 4, 0x00000000u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 8u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 8u); // not taken

    // BLTZAL $31, 2 with $31 = -1: taken on the old value.
    PSXCore_SetGPR(core, 31, 0xFFFFFFFFu);
    PSXCore_WriteMemory32(core, 0, 0x07F00002u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 8u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 12u); // taken

    // JALR $5, $5 with $5 = 0x30: jumps to the old 0x30, then $5 = 8.
    PSXCore_SetGPR(core, 5, 0x30);
    PSXCore_WriteMemory32(core, 0, 0x00A02809u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 8u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 0x30u);

    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_control_rust_tests() {
    test_step_branch();
    test_step_jump();
    test_step_jr_jalr();
    test_branch_load_delay_interaction();
    test_rust_branch_zero_compares_are_signed();
    test_rust_branch_negative_offset_and_jump_region();
    test_rust_link_reads_source_before_linking();
}
