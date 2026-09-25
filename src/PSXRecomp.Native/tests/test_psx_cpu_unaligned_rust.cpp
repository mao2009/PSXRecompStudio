// Native tests for the PSXCpu LWL/LWR/SWL/SWR migration surface
// (src/psx_cpu_unaligned.cpp; Rust migration slice #528). New tests for this
// slice are added to this file only and called from
// run_psx_cpu_unaligned_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "test_harness.h"

static const uint32_t OP_LW = 0x23, OP_LWL = 0x22, OP_LWR = 0x26, OP_SWL = 0x2A, OP_SWR = 0x2E;

static uint32_t EncodeI(uint32_t op, uint32_t rs, uint32_t rt, uint32_t imm) {
    return (op << 26) | (rs << 21) | (rt << 16) | (imm & 0xFFFFu);
}

// These two were moved from test_psx_core.cpp by #524 as "LWL/LWR" tests but
// execute a plain LW (opcode 0x23); renamed so they no longer claim LWL/LWR
// coverage (#528). Kept as the aligned-LW load-delay baseline.
static void test_lw_aligned_load_delay() {
    TEST("LW aligned load (load-delay baseline)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_WriteMemory32(core, 0x1000, 0x12345678u);

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

static void test_lw_replaces_existing_value() {
    TEST("LW aligned load replaces existing register value");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, 0xAABBCCDD);
    PSXCore_WriteMemory32(core, 0x1000, 0x12345678u);

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

// Memory bytes at 0x1000..0x1003 are 11 22 33 44 (little-endian word
// 0x44332211); the neighbouring words are sentinels that must never change.
static const uint32_t kMem = 0x44332211u;
static const uint32_t kReg = 0xAABBCCDDu;
static const uint32_t kSentinel = 0xEEEEEEEEu;

struct UnalignedCase {
    const char* name;
    uint32_t op;
    uint32_t base;     // $29 value; the effective address is base + 1 via the immediate
    uint32_t expected; // $1 for loads, word at 0x1000 for stores
};

// One instruction `op $1, 1($29)` (effective address = base + 1), followed by a
// NOP so a load's delayed write has retired.
static void run_unaligned_case(const UnalignedCase& c) {
    TEST(c.name);
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x0FFC, kSentinel);
    PSXCore_WriteMemory32(core, 0x1000, kMem);
    PSXCore_WriteMemory32(core, 0x1004, kSentinel);
    PSXCore_SetGPR(core, 29, c.base);
    PSXCore_SetGPR(core, 1, kReg);
    PSXCore_WriteMemory32(core, 0, EncodeI(c.op, 29, 1, 1));
    PSXCore_WriteMemory32(core, 4, 0);
    PSXCore_SetPC(core, 0);
    bool is_load = (c.op == OP_LWL || c.op == OP_LWR);

    PSXCore_Step(core);
    if (is_load) {
        ASSERT_EQ(PSXCore_GetGPR(core, 1), kReg); // load delay slot sees the old value
    }
    PSXCore_Step(core);
    if (is_load) {
        ASSERT_EQ(PSXCore_GetGPR(core, 1), c.expected);
        ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1000), kMem);
    } else {
        ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1000), c.expected);
        ASSERT_EQ(PSXCore_GetGPR(core, 1), kReg);
    }
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x0FFC), kSentinel);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1004), kSentinel);
    PSXCore_Destroy(core);
    PASS();
}

// All 16 op x (addr & 3) combinations with the real opcodes. `base` is
// 0x0FFF + n so the effective address is 0x1000 + n (n = addr & 3); the
// aligned word read/written is always 0x1000.
static void test_unaligned_all_offsets() {
    static const UnalignedCase cases[] = {
        {"LWL addr&3=0", OP_LWL, 0x0FFF, 0x11BBCCDDu},
        {"LWL addr&3=1", OP_LWL, 0x1000, 0x2211CCDDu},
        {"LWL addr&3=2", OP_LWL, 0x1001, 0x332211DDu},
        {"LWL addr&3=3", OP_LWL, 0x1002, 0x44332211u},
        {"LWR addr&3=0", OP_LWR, 0x0FFF, 0x44332211u},
        {"LWR addr&3=1", OP_LWR, 0x1000, 0xAA443322u},
        {"LWR addr&3=2", OP_LWR, 0x1001, 0xAABB4433u},
        {"LWR addr&3=3", OP_LWR, 0x1002, 0xAABBCC44u},
        {"SWL addr&3=0", OP_SWL, 0x0FFF, 0x443322AAu},
        {"SWL addr&3=1", OP_SWL, 0x1000, 0x4433AABBu},
        {"SWL addr&3=2", OP_SWL, 0x1001, 0x44AABBCCu},
        {"SWL addr&3=3", OP_SWL, 0x1002, 0xAABBCCDDu},
        {"SWR addr&3=0", OP_SWR, 0x0FFF, 0xAABBCCDDu},
        {"SWR addr&3=1", OP_SWR, 0x1000, 0xBBCCDD11u},
        {"SWR addr&3=2", OP_SWR, 0x1001, 0xCCDD2211u},
        {"SWR addr&3=3", OP_SWR, 0x1002, 0xDD332211u},
    };
    for (const UnalignedCase& c : cases) {
        run_unaligned_case(c);
    }
}

// Aligned base from a KSEG0 unaligned address: 0x80001003 reads/writes the
// word at physical 0x1000.
static void test_unaligned_kseg0_aligned_base() {
    run_unaligned_case({"LWL KSEG0 0x80001003 -> word 0x1000", OP_LWL, 0x80001002u, 0x44332211u});
    run_unaligned_case({"SWR KSEG0 0x80001003 -> word 0x1000", OP_SWR, 0x80001002u, 0xDD332211u});
}

// A consecutive `first $1, a($29)` / `second $1, b($29)` pair with no NOP
// between: the second instruction merges into the first one's still-pending
// load-delay value (docs/cpu/pipeline.md "Special LWL/LWR Behavior").
static void run_pair(const char* name, uint32_t first, uint32_t first_imm,
                     uint32_t second, uint32_t second_imm, uint32_t expected) {
    TEST(name);
    PSXCore* core = PSXCore_Create();
    // Bytes at 0x1000..0x1007: 10 21 32 43 54 65 76 87.
    PSXCore_WriteMemory32(core, 0x1000, 0x43322110u);
    PSXCore_WriteMemory32(core, 0x1004, 0x87766554u);
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, kReg);
    PSXCore_WriteMemory32(core, 0, EncodeI(first, 29, 1, first_imm));
    PSXCore_WriteMemory32(core, 4, EncodeI(second, 29, 1, second_imm));
    PSXCore_WriteMemory32(core, 8, 0);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), kReg);
    PSXCore_Step(core);
    PSXCore_Step(core); // NOP: the second load's delayed write retires
    ASSERT_EQ(PSXCore_GetGPR(core, 1), expected);
    PSXCore_Destroy(core);
    PASS();
}

// LWR addr / LWL addr+3 reconstructs the unaligned little-endian word at addr
// for every offset, in both instruction orders.
static void test_lwl_lwr_pair_reconstructs_word() {
    run_pair("LWR 0 / LWL 3 (aligned)",   OP_LWR, 0, OP_LWL, 3, 0x43322110u);
    run_pair("LWR 1 / LWL 4",             OP_LWR, 1, OP_LWL, 4, 0x54433221u);
    run_pair("LWR 2 / LWL 5",             OP_LWR, 2, OP_LWL, 5, 0x65544332u);
    run_pair("LWR 3 / LWL 6",             OP_LWR, 3, OP_LWL, 6, 0x76655443u);
    run_pair("LWL 4 / LWR 1 (reverse order)", OP_LWL, 4, OP_LWR, 1, 0x54433221u);
    run_pair("LWL 6 / LWR 3 (reverse order)", OP_LWL, 6, OP_LWR, 3, 0x76655443u);
}

// A pending load to a different register is not merged: LWL $1 uses $1's
// committed value while LW $2 is still in its delay slot.
static void test_lwl_ignores_pending_load_to_other_register() {
    TEST("LWL does not merge a pending load to another register");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x1000, kMem);
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, kReg);
    PSXCore_WriteMemory32(core, 0, EncodeI(OP_LW, 29, 2, 0));
    PSXCore_WriteMemory32(core, 4, EncodeI(OP_LWL, 29, 1, 1));
    PSXCore_WriteMemory32(core, 8, 0);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), kMem);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x2211CCDDu);
    PSXCore_Destroy(core);
    PASS();
}

// SWR addr / SWL addr+3 stores the whole register at an unaligned address and
// leaves every byte outside [addr, addr+3] untouched.
static void test_swl_swr_pair_stores_word() {
    TEST("SWR 1 / SWL 4 stores unaligned word, neighbours preserved");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x1000, kSentinel);
    PSXCore_WriteMemory32(core, 0x1004, kSentinel);
    PSXCore_SetGPR(core, 29, 0x1000);
    PSXCore_SetGPR(core, 1, kReg);
    PSXCore_WriteMemory32(core, 0, EncodeI(OP_SWR, 29, 1, 1));
    PSXCore_WriteMemory32(core, 4, EncodeI(OP_SWL, 29, 1, 4));
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1000), 0xEEu);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1001), 0xDDu);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1002), 0xCCu);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1003), 0xBBu);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1004), 0xAAu);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1005), 0xEEu);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1004), 0xEEEEEEAAu);
    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_unaligned_rust_tests() {
    test_lw_aligned_load_delay();
    test_lw_replaces_existing_value();
    test_unaligned_all_offsets();
    test_unaligned_kseg0_aligned_base();
    test_lwl_lwr_pair_reconstructs_word();
    test_lwl_ignores_pending_load_to_other_register();
    test_swl_swr_pair_stores_word();
}
