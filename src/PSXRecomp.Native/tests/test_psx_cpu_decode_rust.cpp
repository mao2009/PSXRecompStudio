// Native tests for the PSXCpu instruction decode / dispatch (RI and CpU
// dispatch) migration surface (src/psx_cpu_decode.cpp; Rust migration slice
// #525). The existing tests below were moved verbatim out of test_psx_core.cpp
// (Issue #524). New tests for this slice are added to this file only and called
// from run_psx_cpu_decode_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "psx_cpu_decode.h"
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

// ---------------------------------------------------------------------------
// Rust decoder (Issue #525).
// ---------------------------------------------------------------------------

// Calls the Rust export directly through the C++ mirror, so a drift between
// PSXDecodeOp / PSXDecodedInstruction and cpu_decode.rs fails here.
static void test_rust_decode_classification_and_fields() {
    TEST("Rust psx_cpu_decode classifies and extracts fields through the C++ mirror");
    struct Case { uint32_t word; PSXDecodeOp op; };
    const Case cases[] = {
        {0x00000000u, PSXDecodeOp::Sll},      {0x00221821u, PSXDecodeOp::Addu},
        {0x0000002Bu, PSXDecodeOp::Sltu},     {0x00000014u, PSXDecodeOp::Reserved},
        {0x0000000Cu, PSXDecodeOp::Syscall},  {0x0000000Du, PSXDecodeOp::Break},
        {0x04000000u, PSXDecodeOp::Bltz},     {0x04110000u, PSXDecodeOp::Bgezal},
        {0x04020000u, PSXDecodeOp::Reserved}, {0x04120000u, PSXDecodeOp::Reserved},
        {0x08000000u, PSXDecodeOp::J},        {0x0C000000u, PSXDecodeOp::Jal},
        {0x3C000000u, PSXDecodeOp::Lui},      {0x8C000000u, PSXDecodeOp::Lw},
        {0xB8000000u, PSXDecodeOp::Swr},      {0x9C000000u, PSXDecodeOp::Reserved},
        {0x40000000u, PSXDecodeOp::Mfc0},     {0x40800000u, PSXDecodeOp::Mtc0},
        {0x42000010u, PSXDecodeOp::Rfe},      {0x42000001u, PSXDecodeOp::Reserved},
        {0x40410000u, PSXDecodeOp::Reserved}, {0x4A180001u, PSXDecodeOp::CopUnusable},
        {0xC0000000u, PSXDecodeOp::Reserved}, {0xE0000000u, PSXDecodeOp::Reserved},
        {0xEC000000u, PSXDecodeOp::CopUnusable}, {0xFFFFFFFFu, PSXDecodeOp::Reserved},
    };
    for (const Case& c : cases) {
        ASSERT_EQ(static_cast<uint32_t>(psx_cpu_decode(c.word).op), static_cast<uint32_t>(c.op));
    }

    const PSXDecodedInstruction ones = psx_cpu_decode(0xFFFFFFFFu);
    ASSERT_EQ(ones.rs, 31u);
    ASSERT_EQ(ones.rt, 31u);
    ASSERT_EQ(ones.rd, 31u);
    ASSERT_EQ(ones.shamt, 31u);
    ASSERT_EQ(ones.imm, 0xFFFFu);
    ASSERT_EQ(ones.target, 0x03FFFFFFu);
    ASSERT_EQ(ones.cop, 3u);

    const PSXDecodedInstruction lw = psx_cpu_decode(0x8FA8FFFCu); // LW $8, -4($29)
    ASSERT_EQ(lw.rs, 29u);
    ASSERT_EQ(lw.rt, 8u);
    ASSERT_EQ(static_cast<int16_t>(lw.imm), -4);
    PASS();
}

static void assert_reserved(const char* name, uint32_t instruction) {
    TEST(name);
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, instruction);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x0Au, 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ri_reserved_encoding_boundaries() {
    assert_reserved("LWC0 raises RI, not CpU", 0xC0000000u);
    assert_reserved("SWC0 raises RI, not CpU", 0xE0000000u);
    assert_reserved("REGIMM rt=0x12 (not BLTZAL/BGEZAL) raises RI", 0x04120000u);
    assert_reserved("COP0 rs=0x10 with funct!=0x10 (TLBR) raises RI", 0x42000001u);
    assert_reserved("Opcode 0x3F raises RI", 0xFC000000u);
}

static void test_cpu_unusable_every_form() {
    const uint32_t opcodes[] = {0x11u, 0x12u, 0x13u, 0x31u, 0x32u, 0x33u, 0x39u, 0x3Au, 0x3Bu};
    for (uint32_t opcode : opcodes) {
        TEST("COPz/LWCz/SWCz raises CpU with CAUSE.CE = opcode & 3");
        assert_cpu_unusable(opcode << 26, opcode & 3u);
    }
}

// Each operand field reaches the handler argument the pre-#525 switch gave it.
static void test_dispatch_routes_operand_fields() {
    TEST("Dispatch routes rs/rt/rd/shamt/immediate to the right handler arguments");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0x80000000u);
    PSXCore_SetGPR(core, 2, 4u);
    PSXCore_WriteMemory32(core, 0x00, 0x00011903u); // SRA   $3, $1, 4   (shamt)
    PSXCore_WriteMemory32(core, 0x04, 0x00412007u); // SRAV  $4, $1, $2  (rs)
    PSXCore_WriteMemory32(core, 0x08, 0x24058000u); // ADDIU $5, $0, 0x8000 (sign-extend)
    PSXCore_WriteMemory32(core, 0x0C, 0x34068000u); // ORI   $6, $0, 0x8000 (zero-extend)
    PSXCore_WriteMemory32(core, 0x10, 0x3C078000u); // LUI   $7, 0x8000
    PSXCore_SetPC(core, 0);
    for (int i = 0; i < 5; ++i) PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0xF8000000u);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 0xF8000000u);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0xFFFF8000u);
    ASSERT_EQ(PSXCore_GetGPR(core, 6), 0x00008000u);
    ASSERT_EQ(PSXCore_GetGPR(core, 7), 0x80000000u);
    ASSERT_EQ(PSXCore_GetPC(core), 0x14u);
    PSXCore_Destroy(core);
    PASS();
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
    test_rust_decode_classification_and_fields();
    test_ri_reserved_encoding_boundaries();
    test_cpu_unusable_every_form();
    test_dispatch_routes_operand_fields();
}
