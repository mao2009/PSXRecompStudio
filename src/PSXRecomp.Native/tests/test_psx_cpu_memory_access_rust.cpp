// Native tests for the PSXCpu aligned loads/stores and address translation
// migration surface (src/psx_cpu_memory_access.cpp; Rust migration slice #527).
// The existing tests below were moved verbatim out of test_psx_core.cpp (Issue
// #524). New tests for this slice are added to this file only and called from
// run_psx_cpu_memory_access_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "psx_cpu_memory_access.h"
#include "test_harness.h"

static void test_step_memory() {
    TEST("LB/LBU/LH/LHU/LW/SB/SH/SW instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000); // base address
    
    // Write test data to memory
    PSXCore_WriteMemory32(core, 0x1000, 0x12345678u);
    PSXCore_WriteMemory16(core, 0x1004, 0xABCDu);
    PSXCore_WriteMemory8(core, 0x1006, 0xEFu);

    // Program (NOPs give the load delay a cycle to commit the loaded value)
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u);  // LW $1, 0($29)
    PSXCore_WriteMemory32(core, 4, 0x00000000u);  // NOP
    PSXCore_WriteMemory32(core, 8, 0x87A20004u);  // LH $2, 4($29)
    PSXCore_WriteMemory32(core, 12, 0x00000000u); // NOP
    PSXCore_WriteMemory32(core, 16, 0x97A30004u); // LHU $3, 4($29)
    PSXCore_WriteMemory32(core, 20, 0x00000000u); // NOP
    PSXCore_WriteMemory32(core, 24, 0x83A40006u); // LB $4, 6($29)
    PSXCore_WriteMemory32(core, 28, 0x00000000u); // NOP
    PSXCore_WriteMemory32(core, 32, 0x93A50006u); // LBU $5, 6($29)
    PSXCore_WriteMemory32(core, 36, 0x00000000u); // NOP

    // LW
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u); // load delay: value not yet committed
    PSXCore_Step(core); // NOP
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x12345678u);

    // LH (sign extended)
    PSXCore_SetPC(core, 8);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0u); // load delay
    PSXCore_Step(core); // NOP
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0xFFFFABCDu);

    // LHU
    PSXCore_SetPC(core, 16);
    PSXCore_Step(core);
    PSXCore_Step(core); // NOP
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0x0000ABCDu);

    // LB (sign extended)
    PSXCore_SetPC(core, 24);
    PSXCore_Step(core);
    PSXCore_Step(core); // NOP
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 0xFFFFFFEFu);

    // LBU
    PSXCore_SetPC(core, 32);
    PSXCore_Step(core);
    PSXCore_Step(core); // NOP
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0x000000EFu);

    // SW $1, 8($29) = 0xAFA10008 (store, no delay)
    PSXCore_WriteMemory32(core, 40, 0xAFA10008u);
    PSXCore_SetPC(core, 40);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1008), 0x12345678u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_kseg_translation() {
    TEST("KSEG address translation (0x80000000->0x00000000, 0xA0000000->0x00000000)");
    PSXCore* core = PSXCore_Create();

    // Data at physical RAM 0x0000.
    PSXCore_WriteMemory32(core, 0x0000, 0x12345678u);

    // LW $1, 0($29) with $29 = KSEG0 0x80000000 -> physical 0x00000000
    PSXCore_SetGPR(core, 29, 0x80000000u);
    PSXCore_WriteMemory32(core, 0x100, 0x8FA10000u); // LW $1, 0($29)
    PSXCore_WriteMemory32(core, 0x104, 0x00000000u); // NOP (load delay)
    PSXCore_SetPC(core, 0x100);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x12345678u);

    // LW $2, 0($29) with $29 = KSEG1 0xA0000000 -> physical 0x00000000
    PSXCore_SetGPR(core, 29, 0xA0000000u);
    PSXCore_WriteMemory32(core, 0x108, 0x8FA20000u); // LW $2, 0($29)
    PSXCore_WriteMemory32(core, 0x10C, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0x108);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0x12345678u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_kseg_ram_end_bios() {
    TEST("KSEG0 0x801FFFFF->0x001FFFFF, KSEG1 0xBFC00000->0x1FC00000 (BIOS)");
    PSXCore* core = PSXCore_Create();

    // Physical RAM last byte 0x001FFFFF.
    PSXCore_WriteMemory8(core, 0x001FFFFF, 0xABu);
    // BIOS first byte 0x1FC00000.
    PSXCore_WriteMemory8(core, 0x1FC00000, 0xCDu);

    // LB $1, 0($29) with $29 = KSEG0 0x801FFFFF -> 0x001FFFFF
    PSXCore_SetGPR(core, 29, 0x801FFFFFu);
    PSXCore_WriteMemory32(core, 0x100, 0x83A10000u); // LB $1, 0($29)
    PSXCore_WriteMemory32(core, 0x104, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0x100);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xFFFFFFABu); // LB sign-extends

    // LBU $2, 0($29) with $29 = KSEG1 0xBFC00000 -> 0x1FC00000 (BIOS)
    PSXCore_SetGPR(core, 29, 0xBFC00000u);
    PSXCore_WriteMemory32(core, 0x108, 0x93A20000u); // LBU $2, 0($29)
    PSXCore_WriteMemory32(core, 0x10C, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0x108);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0x000000CDu);

    PSXCore_Destroy(core);
    PASS();
}

static void test_kseg_unmapped() {
    TEST("Unmapped read=0, write ignored (KSEG2 0xC0000000)");
    PSXCore* core = PSXCore_Create();

    // LBU $1, 0($29) with $29 unmapped -> returns 0
    PSXCore_SetGPR(core, 29, 0xC0000000u);
    PSXCore_WriteMemory32(core, 0x100, 0x93A10000u); // LBU $1, 0($29)
    PSXCore_WriteMemory32(core, 0x104, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0x100);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u);

    // SW $2, 0($29) with $29 unmapped -> ignored
    PSXCore_SetGPR(core, 2, 0xDEADBEEF);
    PSXCore_WriteMemory32(core, 0x108, 0xAFA20000u); // SW $2, 0($29)
    PSXCore_SetPC(core, 0x108);
    int result = PSXCore_Step(core);
    ASSERT_EQ(result, 0);
    // No crash and the write did not hit any mapped region.

    PSXCore_Destroy(core);
    PASS();
}

static void test_adel_misaligned_lh() {
    TEST("Misaligned LH raises AdEL with BadVaddr");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_WriteMemory32(core, 0, 0x87A10001u); // LH $1, 1($29) -> 0x1001
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x04u, 0u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x1001u); // BadVaddr
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u);       // no value loaded
    PSXCore_Destroy(core);
    PASS();
}

static void test_adel_misaligned_lhu() {
    TEST("Misaligned LHU raises AdEL");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_WriteMemory32(core, 0, 0x97A10001u); // LHU $1, 1($29)
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x04u, 0u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x1001u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_adel_misaligned_lw() {
    TEST("Misaligned LW raises AdEL with BadVaddr");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_WriteMemory32(core, 0x1000u, 0x12345678u);
    PSXCore_WriteMemory32(core, 0, 0x8FA10002u); // LW $1, 2($29) -> 0x1002
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x04u, 0u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x1002u);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ades_misaligned_sh() {
    TEST("Misaligned SH raises AdES with BadVaddr");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_SetGPR(core, 2, 0xBEEFu);
    PSXCore_WriteMemory32(core, 0x1000u, 0u);
    PSXCore_WriteMemory32(core, 0, 0xA7A20001u); // SH $2, 1($29) -> 0x1001
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x05u, 0u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x1001u);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1000u), 0u); // store suppressed
    PSXCore_Destroy(core);
    PASS();
}

static void test_ades_misaligned_sw() {
    TEST("Misaligned SW raises AdES with BadVaddr");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_SetGPR(core, 2, 0xDEADBEEFu);
    PSXCore_WriteMemory32(core, 0x1000u, 0u);
    PSXCore_WriteMemory32(core, 0, 0xAFA20003u); // SW $2, 3($29) -> 0x1003
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x05u, 0u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x1003u);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1000u), 0u); // store suppressed
    PSXCore_Destroy(core);
    PASS();
}

static void test_aligned_halfword_word_access_unaffected() {
    TEST("Aligned LH/LW/SH/SW still execute normally");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_SetGPR(core, 2, 0x1234u);
    PSXCore_WriteMemory32(core, 0, 0xA7A20002u); // SH $2, 2($29) -> 0x1002
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0u); // no exception
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Destroy(core);
    PASS();
}

// ---- Issue #527: Rust-backed address semantics ----

static void test_mem_access_rust_exports() {
    TEST("Rust mem-access exports: translate/is_mapped/classify/extend_load");
    ASSERT_EQ(sizeof(PSXMemAccess), 12u);
    ASSERT_EQ(psx_cpu_mem_translate(0x00001000u), 0x00001000u);
    ASSERT_EQ(psx_cpu_mem_translate(0x801FFFFFu), 0x001FFFFFu);
    ASSERT_EQ(psx_cpu_mem_translate(0xBFC00000u), 0x1FC00000u);
    ASSERT_EQ(psx_cpu_mem_translate(0xC0000000u), 0xFFFFFFFFu);
    ASSERT_EQ(psx_cpu_mem_is_mapped(0xFFFFFFFFu), 0u);
    ASSERT_EQ(psx_cpu_mem_is_mapped(0u), 1u);

    PSXMemAccess a = psx_cpu_mem_classify(0x1000u, -4, 4);
    ASSERT_EQ(a.vaddr, 0x0FFCu);
    ASSERT_EQ(a.phys, 0x0FFCu);
    ASSERT_EQ(a.status, PSX_MEM_ACCESS_OK);
    a = psx_cpu_mem_classify(0xFFFFFFFCu, 8, 4); // wraps past 0xFFFFFFFF
    ASSERT_EQ(a.vaddr, 4u);
    ASSERT_EQ(a.status, PSX_MEM_ACCESS_OK);
    a = psx_cpu_mem_classify(0x1000u, 1, 2);
    ASSERT_EQ(a.vaddr, 0x1001u);
    ASSERT_EQ(a.status, PSX_MEM_ACCESS_MISALIGNED);
    ASSERT_EQ(psx_cpu_mem_classify(0x1000u, 2, 4).status, PSX_MEM_ACCESS_MISALIGNED);
    ASSERT_EQ(psx_cpu_mem_classify(0x1001u, 0, 1).status, PSX_MEM_ACCESS_OK);
    ASSERT_EQ(psx_cpu_mem_classify(0xC0000001u, 0, 2).status, PSX_MEM_ACCESS_MISALIGNED);
    a = psx_cpu_mem_classify(0xC0000000u, 0, 4);
    ASSERT_EQ(a.phys, 0xFFFFFFFFu);
    ASSERT_EQ(a.status, PSX_MEM_ACCESS_UNMAPPED);

    ASSERT_EQ(psx_cpu_mem_extend_load(0xEFu, 1, 1), 0xFFFFFFEFu);
    ASSERT_EQ(psx_cpu_mem_extend_load(0xEFu, 1, 0), 0x000000EFu);
    ASSERT_EQ(psx_cpu_mem_extend_load(0xABCDu, 2, 1), 0xFFFFABCDu);
    ASSERT_EQ(psx_cpu_mem_extend_load(0xABCDu, 2, 0), 0x0000ABCDu);
    ASSERT_EQ(psx_cpu_mem_extend_load(0x87654321u, 4, 1), 0x87654321u);
    PASS();
}

static void test_negative_offset_and_wrap() {
    TEST("Effective address sign-extends the offset and wraps at 32 bits");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x1000u, 0x11223344u);
    PSXCore_WriteMemory32(core, 0x0004u, 0x55667788u);

    PSXCore_SetGPR(core, 29, 0x1004u);
    PSXCore_WriteMemory32(core, 0x100, 0x8FA1FFFCu); // LW $1, -4($29) -> 0x1000
    PSXCore_WriteMemory32(core, 0x104, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0x100);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x11223344u);

    PSXCore_SetGPR(core, 29, 0xFFFFFFFCu);
    PSXCore_WriteMemory32(core, 0x108, 0x8FA20008u); // LW $2, 8($29) -> wraps to 0x4
    PSXCore_WriteMemory32(core, 0x10C, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0x108);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0x55667788u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_unmapped_load_queues_zero() {
    TEST("Unmapped LW/LH/LB queue 0 through the load delay (no exception)");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x2000u, 0xDEADBEEFu); // what a bad 0x1FFFFFFF mask would hit
    PSXCore_SetGPR(core, 29, 0xC0002000u);
    const uint32_t loads[] = {0x8FA10000u, 0x87A10000u, 0x97A10000u, 0x83A10000u, 0x93A10000u};
    for (uint32_t op : loads) {
        PSXCore_SetGPR(core, 1, 0xCAFEBABEu);
        PSXCore_WriteMemory32(core, 0x100, op);          // L* $1, 0($29)
        PSXCore_WriteMemory32(core, 0x104, 0x00000000u); // NOP
        PSXCore_SetPC(core, 0x100);
        PSXCore_Step(core);
        ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xCAFEBABEu); // load delay
        PSXCore_Step(core);
        ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u);
        ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0u);
        ASSERT_EQ(PSXCore_GetPC(core), 0x108u);
    }
    PSXCore_Destroy(core);
    PASS();
}

static void test_unmapped_store_is_noop() {
    TEST("Unmapped SB/SH/SW are dropped (no exception, no memory write)");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x2000u, 0x11223344u);
    PSXCore_SetGPR(core, 29, 0xC0002000u);
    PSXCore_SetGPR(core, 2, 0xDEADBEEFu);
    const uint32_t stores[] = {0xA3A20000u, 0xA7A20000u, 0xAFA20000u};
    for (uint32_t op : stores) {
        PSXCore_WriteMemory32(core, 0x100, op); // S* $2, 0($29)
        PSXCore_SetPC(core, 0x100);
        ASSERT_EQ(PSXCore_Step(core), 0);
        ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0u);
        ASSERT_EQ(PSXCore_GetPC(core), 0x104u);
        ASSERT_EQ(PSXCore_ReadMemory32(core, 0x2000u), 0x11223344u);
    }
    PSXCore_Destroy(core);
    PASS();
}

static void test_misaligned_unmapped_raises_adel() {
    TEST("Misaligned LH at unmapped KSEG2 raises AdEL (alignment wins)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0xC0000000u);
    PSXCore_WriteMemory32(core, 0, 0x87A10001u); // LH $1, 1($29)
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x04u, 0u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0xC0000001u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_kseg1_stores_translate() {
    TEST("SB/SH/SW through KSEG1 write the translated physical address");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0xA0002000u);
    PSXCore_SetGPR(core, 2, 0x12345678u);
    PSXCore_WriteMemory32(core, 0x100, 0xAFA20000u); // SW $2, 0($29)
    PSXCore_WriteMemory32(core, 0x104, 0xA7A20004u); // SH $2, 4($29)
    PSXCore_WriteMemory32(core, 0x108, 0xA3A20006u); // SB $2, 6($29)
    PSXCore_SetPC(core, 0x100);
    PSXCore_Step(core);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x2000u), 0x12345678u);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x2004u), 0x00785678u);
    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_memory_access_rust_tests() {
    test_mem_access_rust_exports();
    test_negative_offset_and_wrap();
    test_unmapped_load_queues_zero();
    test_unmapped_store_is_noop();
    test_misaligned_unmapped_raises_adel();
    test_kseg1_stores_translate();
    test_step_memory();
    test_kseg_translation();
    test_kseg_ram_end_bios();
    test_kseg_unmapped();
    test_adel_misaligned_lh();
    test_adel_misaligned_lhu();
    test_adel_misaligned_lw();
    test_ades_misaligned_sh();
    test_ades_misaligned_sw();
    test_aligned_halfword_word_access_unaffected();
}
