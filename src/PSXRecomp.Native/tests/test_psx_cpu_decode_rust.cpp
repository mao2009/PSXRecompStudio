// Native tests for the PSXCpu instruction decode / dispatch (RI and CpU
// dispatch) migration surface (src/psx_cpu_decode.cpp; Rust migration slice
// #525). The existing tests below were moved verbatim out of test_psx_core.cpp
// (Issue #524). New tests for this slice are added to this file only and called
// from run_psx_cpu_decode_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "test_harness.h"

// RI / CpU / AdEL / AdES exception tests (Issue #376).
//
// Every one of these fails against the pre-#376 interpreter: undefined
// opcodes/functs/selectors and COP1/2/3 + LWC2/SWC2 all hit a bare `break;`
// (no exception, PC simply advances to +4), and the misaligned load/store and
// misaligned/unmapped fetch paths had no alignment check at all (the access
// went through to memory, or the fetch returned 0 and executed as a NOP). So
// on the old code CAUSE.Excode stayed 0 and PC was 4 rather than 0x80000080.

static void test_ri_undefined_opcode() {
    TEST("Undefined opcode raises RI (Excode=0x0A)");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x78000000u); // opcode 0x1E: reserved
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x0Au, 0u);
    // SR 3-level stack pushed: KUc/IEc cleared, previous level holds the old
    // current level (seeded 0 by Reset, so bits 0-5 are 0 after the push).
    ASSERT_EQ(PSXCore_GetCop0(core, 12) & 0x3Fu, 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ri_undefined_special_funct() {
    TEST("Undefined SPECIAL funct raises RI");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x00000014u); // SPECIAL, funct 0x14: reserved
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x0Au, 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ri_undefined_regimm() {
    TEST("Undefined REGIMM selector raises RI");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x04020000u); // REGIMM, rt=0x02: reserved
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x0Au, 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ri_undefined_cop0_form() {
    TEST("Unrecognised COP0 form (CFC0) raises RI");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x40410000u); // CFC0 $1, $0 (rs=0x02)
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x0Au, 0u);
    PSXCore_Destroy(core);
    PASS();
}

// CAUSE.CE (bits 28-29) carries the coprocessor number for CpU.
static void assert_cpu_unusable(uint32_t instruction, uint32_t expected_ce) {
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, instruction);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    if (((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2) != 0x0Bu ||
        ((PSXCore_GetCop0(core, 13) >> 28) & 3u) != expected_ce ||
        PSXCore_GetCop0(core, 14) != 0u ||
        PSXCore_GetPC(core) != 0x80000080u) {
        PSXCore_Destroy(core);
        printf("FAIL (instr %08X: expected CpU with CE=%u)\n",
               (unsigned)instruction, (unsigned)expected_ce);
        return;
    }
    PSXCore_Destroy(core);
    tests_passed++;
    printf("PASS\n");
}

static void test_cpu_unusable_cop1() {
    TEST("COP1 access raises CpU with CAUSE.CE=1");
    assert_cpu_unusable(0x44010000u, 1u); // MFC1 $1, $0
}

static void test_cpu_unusable_cop2() {
    TEST("COP2/GTE command raises CpU with CAUSE.CE=2 (Issue #377)");
    assert_cpu_unusable(0x4A180001u, 2u); // RTPS (GTE command)
}

static void test_cpu_unusable_cop3() {
    TEST("COP3 access raises CpU with CAUSE.CE=3");
    assert_cpu_unusable(0x4C010000u, 3u); // MFC3 $1, $0
}

static void test_cpu_unusable_lwc2() {
    TEST("LWC2 raises CpU with CAUSE.CE=2");
    assert_cpu_unusable(0xC8010000u, 2u); // LWC2 $1, 0($0)
}

static void test_cpu_unusable_swc2() {
    TEST("SWC2 raises CpU with CAUSE.CE=2");
    assert_cpu_unusable(0xE8010000u, 2u); // SWC2 $1, 0($0)
}

void run_psx_cpu_decode_rust_tests() {
    test_ri_undefined_opcode();
    test_ri_undefined_special_funct();
    test_ri_undefined_regimm();
    test_ri_undefined_cop0_form();
    test_cpu_unusable_cop1();
    test_cpu_unusable_cop2();
    test_cpu_unusable_cop3();
    test_cpu_unusable_lwc2();
    test_cpu_unusable_swc2();
}
