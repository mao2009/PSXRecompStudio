// Native tests for the PSXCpu LWL/LWR/SWL/SWR migration surface
// (src/psx_cpu_unaligned.cpp; Rust migration slice #528). The existing tests
// below were moved verbatim out of test_psx_core.cpp (Issue #524). New tests
// for this slice are added to this file only and called from
// run_psx_cpu_unaligned_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "test_harness.h"

// LWL/LWR tests
static void test_lwl_lwr_aligned() {
    TEST("LWL/LWR aligned load (LW equivalent)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_WriteMemory32(core, 0x1000, 0x12345678u);
    
    // For aligned full word load, use LW (opcode 0x23)
    // LW $1, 0($29): 0x8FA10000
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u);
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP: load delay cycle
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0u); // load delay
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x12345678u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_lwl_lwr_unchanged() {
    TEST("LWL/LWR with existing register value");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, 0xAABBCCDD);
    PSXCore_WriteMemory32(core, 0x1000, 0x12345678u);
    
    // For aligned full word load, use LW (opcode 0x23)
    // LW $1, 0($29): 0x8FA10000
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u);
    PSXCore_WriteMemory32(core, 4, 0x00000000u); // NOP: load delay cycle
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xAABBCCDDu); // still old value
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x12345678u);
    
    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_unaligned_rust_tests() {
    test_lwl_lwr_aligned();
    test_lwl_lwr_unchanged();
}
