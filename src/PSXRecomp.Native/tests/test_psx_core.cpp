#include <cassert>
#include <cstdio>
#include "psx_core.h"
#include "psx_cpu.h"
#include "psx_memory.h"

static int tests_run = 0;
static int tests_passed = 0;

#define TEST(name) \
    do { \
        tests_run++; \
        printf("  TEST: %s ... ", name); \
    } while(0)

#define PASS() \
    do { \
        tests_passed++; \
        printf("PASS\n"); \
    } while(0)

#define ASSERT_EQ(a, b) \
    do { \
        if ((a) != (b)) { \
            printf("FAIL (expected %u, got %u)\n", (unsigned)(b), (unsigned)(a)); \
            return; \
        } \
    } while(0)

static void test_create_destroy() {
    TEST("PSXCore_Create/Destroy");
    PSXCore* core = PSXCore_Create();
    assert(core != nullptr);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_initial_state() {
    TEST("CPU GPR initial state = 0");
    PSXCore* core = PSXCore_Create();
    for (int i = 0; i < 32; i++) {
        ASSERT_EQ(PSXCore_GetGPR(core, i), 0);
    }
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_pc_initial() {
    TEST("CPU PC initial = 0");
    PSXCore* core = PSXCore_Create();
    ASSERT_EQ(PSXCore_GetPC(core), 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_hi_lo_initial() {
    TEST("CPU HI/LO initial = 0");
    PSXCore* core = PSXCore_Create();
    ASSERT_EQ(PSXCore_GetHI(core), 0u);
    ASSERT_EQ(PSXCore_GetLO(core), 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_gpr_set_get() {
    TEST("CPU GPR set/get");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0xDEADBEEF);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0xDEADBEEF);
    PSXCore_SetGPR(core, 31, 0x12345678);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 0x12345678u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_pc_set_get() {
    TEST("CPU PC set/get");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetPC(core, 0x80030000);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80030000u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_hi_lo_set_get() {
    TEST("CPU HI/LO set/get");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetHI(core, 0xAABBCCDD);
    PSXCore_SetLO(core, 0x11223344);
    ASSERT_EQ(PSXCore_GetHI(core), 0xAABBCCDDu);
    ASSERT_EQ(PSXCore_GetLO(core), 0x11223344u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ram_size() {
    TEST("RAM size = 2MB");
    ASSERT_EQ(PSXCore_GetRAMSize(), 2u * 1024u * 1024u);
    PASS();
}

static void test_ram_access() {
    TEST("RAM basic access");
    PSXCore* core = PSXCore_Create();
    uint8_t* ram = PSXCore_GetRAM(core);
    assert(ram != nullptr);
    ram[0] = 0xFF;
    ram[1024] = 0x42;
    ASSERT_EQ(ram[0], 0xFF);
    ASSERT_EQ(ram[1024], 0x42);
    PSXCore_Destroy(core);
    PASS();
}

static void test_reset() {
    TEST("PSXCore_Reset clears state");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 5, 0x1234);
    PSXCore_SetPC(core, 0x80000000);
    PSXCore_SetHI(core, 0xAAAAAAAA);
    PSXCore_SetLO(core, 0xBBBBBBBB);
    PSXCore_GetRAM(core)[0] = 0x55;

    PSXCore_Reset(core);

    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0u);
    ASSERT_EQ(PSXCore_GetPC(core), 0u);
    ASSERT_EQ(PSXCore_GetHI(core), 0u);
    ASSERT_EQ(PSXCore_GetLO(core), 0u);
    ASSERT_EQ(PSXCore_GetRAM(core)[0], 0);
    PSXCore_Destroy(core);
    PASS();
}

static void test_null_safety() {
    TEST("Null pointer safety");
    PSXCore_Destroy(nullptr);
    ASSERT_EQ(PSXCore_GetGPR(nullptr, 0), 0u);
    PSXCore_SetGPR(nullptr, 0, 123);
    ASSERT_EQ(PSXCore_GetPC(nullptr), 0u);
    PSXCore_SetPC(nullptr, 123);
    ASSERT_EQ(PSXCore_GetHI(nullptr), 0u);
    ASSERT_EQ(PSXCore_GetLO(nullptr), 0u);
    assert(PSXCore_GetRAM(nullptr) == nullptr);
    PASS();
}

int main() {
    printf("PSXRecomp.Native Tests\n");
    printf("======================\n");

    test_create_destroy();
    test_cpu_initial_state();
    test_cpu_pc_initial();
    test_cpu_hi_lo_initial();
    test_cpu_gpr_set_get();
    test_cpu_pc_set_get();
    test_cpu_hi_lo_set_get();
    test_ram_size();
    test_ram_access();
    test_reset();
    test_null_safety();

    printf("\n======================\n");
    printf("Results: %d/%d passed\n", tests_passed, tests_run);

    return (tests_passed == tests_run) ? 0 : 1;
}
