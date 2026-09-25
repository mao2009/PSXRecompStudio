// Native tests for the PSXCpu step / fetch / load-delay / branch-delay pipeline
// migration surface (src/psx_cpu_pipeline.cpp; Rust migration slice #531). The
// existing tests below were moved verbatim out of test_psx_core.cpp (Issue
// #524). New tests for this slice are added to this file only and called from
// run_psx_cpu_pipeline_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "test_harness.h"

static void test_step_branch_delay_slot() {
    TEST("Branch delay slot executes on taken and not-taken");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 10);

    // addr 0: BEQ $1, $2, offset=1 (taken -> target = 0 + 4 + 4 = 8)
    // addr 4: ADDI $5, $0, 7       (delay slot; always executes)
    PSXCore_WriteMemory32(core, 0, 0x10220001u);
    PSXCore_WriteMemory32(core, 4, 0x20050007u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0u); // delay slot not yet executed
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 7u); // delay slot executed
    ASSERT_EQ(PSXCore_GetPC(core), 8u); // branch taken

    // Not taken: delay slot still executes, then falls through.
    PSXCore_SetGPR(core, 2, 20);
    PSXCore_SetGPR(core, 5, 0); // stale value from the taken phase; proves re-execution
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0u); // delay slot not yet (re)executed
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 7u); // delay slot (re)executed
    ASSERT_EQ(PSXCore_GetPC(core), 8u); // fall-through (4 + 4)

    PSXCore_Destroy(core);
    PASS();
}

static void test_load_delay() {
    TEST("Load delay: next instruction sees old register value");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, 0x11111111);
    PSXCore_WriteMemory32(core, 0x1000, 0x22222222u);

    // addr 0: LW $1, 0($29)
    // addr 4: ADDU $5, $1, $0      (load delay slot: $1 is still 0x11111111)
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u);
    PSXCore_WriteMemory32(core, 4, 0x00202821u); // ADDU $5, $1, $0
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core); // LW
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x11111111u); // not committed yet
    PSXCore_Step(core); // ADDU
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x22222222u); // committed by now
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0x11111111u); // used the old value

    // A write in the load delay slot overrides the pending load (writes in-order).
    PSXCore_SetGPR(core, 1, 0);
    PSXCore_WriteMemory32(core, 4, 0x34210001u); // ORI $1, $1, 1 (load delay slot)
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core); // LW
    PSXCore_Step(core); // ORI
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 1u);

    // Loads to $zero are suppressed.
    PSXCore_WriteMemory32(core, 0, 0x8FA00000u); // LW $0, 0($29)
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 0), 0u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_branch_in_delay_slot() {
    TEST("Branch in delay slot: outer branch applies (docs/cpu/pipeline.md)");
    PSXCore* core = PSXCore_Create();

    // Taken outer branch with branch in its delay slot:
    // addr 0:  BEQ $1, $2, 3        (outer, taken -> target = 16)
    // addr 4:  BNE $3, $4, 1        (inner branch in the delay slot)
    // addr 8:  ADDI $5, $0, 99      (inner's delay slot; always executes)
    // addr 12: ADDI $6, $0, 11      (inner target; must NOT be reached)
    // addr 16: ADDI $7, $0, 22      (outer target)
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 10); // outer BEQ taken
    PSXCore_SetGPR(core, 3, 5);
    PSXCore_SetGPR(core, 4, 6); // inner BNE taken in isolation
    PSXCore_WriteMemory32(core, 0, 0x10220003u);
    PSXCore_WriteMemory32(core, 4, 0x15240001u);
    PSXCore_WriteMemory32(core, 8, 0x20050063u);
    PSXCore_WriteMemory32(core, 12, 0x2006000Bu);
    PSXCore_WriteMemory32(core, 16, 0x20070016u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core); // outer BEQ
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Step(core); // inner BNE (in delay slot)
    ASSERT_EQ(PSXCore_GetPC(core), 8u);
    PSXCore_Step(core); // ADDI $5 (shared delay slot)
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 99u);
    ASSERT_EQ(PSXCore_GetPC(core), 16u); // OUTER branch applies
    PSXCore_Step(core); // ADDI $7 (outer target)
    ASSERT_EQ(PSXCore_GetGPR(core, 7), 22u);
    ASSERT_EQ(PSXCore_GetGPR(core, 6), 0u); // inner target never executed

    // Not-taken outer branch with branch in its delay slot: falls through.
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 10); // outer BNE not taken
    PSXCore_WriteMemory32(core, 0, 0x14220003u); // BNE $1, $2, 3
    PSXCore_WriteMemory32(core, 4, 0x10640001u); // BEQ $3, $4, 1
    PSXCore_WriteMemory32(core, 8, 0x2005004Du); // ADDI $5, $0, 77
    PSXCore_WriteMemory32(core, 12, 0x2006000Bu); // ADDI $6, $0, 11
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 77u);
    ASSERT_EQ(PSXCore_GetPC(core), 12u); // falls through after shared slot
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 6), 11u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_kseg_instruction_fetch() {
    TEST("Instruction fetch through KSEG0 executes program at 0x80000000");
    PSXCore* core = PSXCore_Create();

    // ADDIU $1, $0, 42 (0x2401002A) stored at physical RAM 0x0000,
    // executed via KSEG0 virtual address 0x80000000.
    PSXCore_WriteMemory32(core, 0x0000, 0x2401002Au);
    PSXCore_SetPC(core, 0x80000000u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 42u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_adel_misaligned_fetch() {
    TEST("Fetch from a misaligned PC raises AdEL");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x2401002Au); // ADDIU $1,$0,42 (must not run)
    PSXCore_SetPC(core, 2u);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x04u, 2u); // EPC = the faulting PC
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 2u); // BadVaddr
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_adel_unmapped_fetch() {
    TEST("Fetch from an unmapped PC raises AdEL (not a silent NOP)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetPC(core, 0xC0000000u); // KSEG2: unmapped in this model
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x04u, 0xC0000000u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0xC0000000u);
    PSXCore_Destroy(core);
    PASS();
}

// --- Issue #531: state transitions now computed in Rust (cpu_pipeline.rs) ---

static void test_pipeline_same_register_back_to_back_loads() {
    TEST("Pipeline (#531): back-to-back loads to one GPR -- last load wins, one step later");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, 0x11111111u);
    PSXCore_WriteMemory32(core, 0x1000, 0xAAAAAAAAu);
    PSXCore_WriteMemory32(core, 0x1004, 0xBBBBBBBBu);
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u); // LW $1, 0($29)
    PSXCore_WriteMemory32(core, 4, 0x8FA10004u); // LW $1, 4($29) (load delay slot)
    PSXCore_WriteMemory32(core, 8, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x11111111u);
    PSXCore_Step(core); // first load's commit is cancelled by the second
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x11111111u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xBBBBBBBBu);
    PSXCore_Destroy(core);
    PASS();
}

static void test_pipeline_pending_and_new_load_commit_in_order() {
    TEST("Pipeline (#531): pending + new load to different GPRs commit one step apart");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_WriteMemory32(core, 0x1000, 0xAAAAAAAAu);
    PSXCore_WriteMemory32(core, 0x1004, 0xBBBBBBBBu);
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u); // LW $1, 0($29)
    PSXCore_WriteMemory32(core, 4, 0x8FA20004u); // LW $2, 4($29)
    PSXCore_WriteMemory32(core, 8, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xAAAAAAAAu);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0u);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0xBBBBBBBBu);
    PSXCore_Destroy(core);
    PASS();
}

static void test_pipeline_exception_in_delay_slot_discards_branch() {
    TEST("Pipeline (#531): exception in a delay slot discards the pending branch");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetCop0(core, 12, 0); // BEV=0: vector 0x80000080
    PSXCore_WriteMemory32(core, 0, 0x10000003u);    // BEQ $0,$0,3 (taken -> 16)
    PSXCore_WriteMemory32(core, 4, 0x0000000Cu);    // SYSCALL (delay slot)
    PSXCore_WriteMemory32(core, 0x80, 0x00000000u); // NOP at the vector
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 1);
    ASSERT_EQ(PSXCore_GetExceptionInDelaySlot(core), 1);
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u); // EPC = the branch
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);
    PSXCore_Step(core); // handler runs sequentially; branch target 16 never applies
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000084u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_pipeline_set_pc_flushes_pending_load() {
    TEST("Pipeline (#531): SetPC flush commits a pending load immediately");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_WriteMemory32(core, 0x1000, 0xCAFEF00Du);
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u); // LW $1, 0($29)
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u);
    PSXCore_SetPC(core, 0x100);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xCAFEF00Du);
    ASSERT_EQ(PSXCore_GetPC(core), 0x100u);
    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_pipeline_rust_tests() {
    test_step_branch_delay_slot();
    test_load_delay();
    test_branch_in_delay_slot();
    test_kseg_instruction_fetch();
    test_adel_misaligned_fetch();
    test_adel_unmapped_fetch();
    test_pipeline_same_register_back_to_back_loads();
    test_pipeline_pending_and_new_load_commit_in_order();
    test_pipeline_exception_in_delay_slot_discards_branch();
    test_pipeline_set_pc_flushes_pending_load();
}
