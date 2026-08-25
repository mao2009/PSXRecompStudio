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

static void test_cpu_gpr_zero_always_zero() {
    TEST("CPU GPR[0] always zero (MIPS $zero)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 0, 0x12345678);
    ASSERT_EQ(PSXCore_GetGPR(core, 0), 0u);
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

// Instruction execution tests
static void test_step_basic() {
    TEST("PSXCore_Step executes one instruction");
    PSXCore* core = PSXCore_Create();
    // ADDI $1, $0, 42 (0x2001002A)
    PSXCore_WriteMemory32(core, 0, 0x2001002Au);
    PSXCore_SetPC(core, 0);
    
    int result = PSXCore_Step(core);
    ASSERT_EQ(result, 0);
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 42u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_add_addu() {
    TEST("ADD/ADDU instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 20);
    
    // ADD $3, $1, $2 (0x00221820)
    PSXCore_WriteMemory32(core, 0, 0x00221820u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 30u);
    
    // ADDU $4, $1, $2 (0x00222021)
    PSXCore_WriteMemory32(core, 4, 0x00222021u);
    PSXCore_SetPC(core, 4);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 30u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_sub_subu() {
    TEST("SUB/SUBU instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 50);
    PSXCore_SetGPR(core, 2, 30);
    
    // SUB $3, $1, $2 (0x00221822)
    PSXCore_WriteMemory32(core, 0, 0x00221822u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 20u);
    
    // SUBU $4, $1, $2 (0x00222023)
    PSXCore_WriteMemory32(core, 4, 0x00222023u);
    PSXCore_SetPC(core, 4);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 20u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_logic() {
    TEST("AND/OR/XOR/NOR instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0xF0F0);
    PSXCore_SetGPR(core, 2, 0x0F0F);
    
    // AND $3, $1, $2 (0x00221824)
    PSXCore_WriteMemory32(core, 0, 0x00221824u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0x0000u);
    
    // OR $4, $1, $2 (0x00222025)
    PSXCore_WriteMemory32(core, 4, 0x00222025u);
    PSXCore_SetPC(core, 4);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 0xFFFFu);
    
    // XOR $5, $1, $2 (0x00222826)
    PSXCore_WriteMemory32(core, 8, 0x00222826u);
    PSXCore_SetPC(core, 8);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0xFFFFu);
    
    // NOR $6, $1, $2 (0x00223027)
    PSXCore_WriteMemory32(core, 12, 0x00223027u);
    PSXCore_SetPC(core, 12);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 6), 0xFFFF0000u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_slt_sltu() {
    TEST("SLT/SLTU instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 20);
    
    // SLT $3, $1, $2 (0x0022182A)
    PSXCore_WriteMemory32(core, 0, 0x0022182Au);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 1u);
    
    // SLTU $4, $1, $2 (0x0022202B)
    PSXCore_WriteMemory32(core, 4, 0x0022202Bu);
    PSXCore_SetPC(core, 4);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 1u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_shift() {
    TEST("SLL/SRL/SRA/SLLV/SRLV/SRAV instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0x00000001);
    PSXCore_SetGPR(core, 2, 2);
    
    // SLL $3, $1, 2 (opcode=0, rs=0, rt=1, rd=3, shamt=2, funct=0) = 0x00011880
    PSXCore_WriteMemory32(core, 0, 0x00011880u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 4u);
    
    // SRL $4, $1, 1 (opcode=0, rs=0, rt=1, rd=4, shamt=1, funct=2) = 0x00012082
    PSXCore_WriteMemory32(core, 4, 0x00012082u);
    PSXCore_SetPC(core, 4);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 0u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_branch() {
    TEST("BEQ/BNE/BLEZ/BGTZ/BLTZ/BGEZ instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 10);
    
    // BEQ $1, $2, offset=1 (branch taken) -> PC = 4 + 1*4 = 8
    PSXCore_WriteMemory32(core, 0, 0x10220001u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 8u);
    
    // BEQ $1, $2, offset=1 (not taken, different values)
    PSXCore_SetGPR(core, 2, 20);
    PSXCore_WriteMemory32(core, 0, 0x10220001u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_jump() {
    TEST("J/JAL/JR/JALR instructions");
    PSXCore* core = PSXCore_Create();
    
    // J target=1 (PC = 4) - from PC=0
    PSXCore_WriteMemory32(core, 0, 0x08000001u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    
    // JAL target=1 (PC = 4, $ra = 4)
    PSXCore_WriteMemory32(core, 0, 0x0C000001u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    ASSERT_EQ(PSXCore_GetGPR(core, 31), 4u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_step_memory() {
    TEST("LB/LBU/LH/LHU/LW/SB/SH/SW instructions");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000); // base address
    
    // Write test data to memory
    PSXCore_WriteMemory32(core, 0x1000, 0x12345678u);
    PSXCore_WriteMemory16(core, 0x1004, 0xABCDu);
    PSXCore_WriteMemory8(core, 0x1006, 0xEFu);
    
    // LW $1, 0($29) = 0x8FA10000
    PSXCore_WriteMemory32(core, 0, 0x8FA10000u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 0x12345678u);
    
    // LH $2, 4($29) = 0x87A20004
    PSXCore_WriteMemory32(core, 4, 0x87A20004u);
    PSXCore_SetPC(core, 4);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0xFFFFABCDu); // sign extended
    
    // LHU $3, 4($29) = 0x97A30004
    PSXCore_WriteMemory32(core, 8, 0x97A30004u);
    PSXCore_SetPC(core, 8);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0x0000ABCDu);
    
    // LB $4, 6($29) = 0x83A40006
    PSXCore_WriteMemory32(core, 12, 0x83A40006u);
    PSXCore_SetPC(core, 12);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 0xFFFFFFEFu); // sign extended
    
    // LBU $5, 6($29) = 0x93A50006
    PSXCore_WriteMemory32(core, 16, 0x93A50006u);
    PSXCore_SetPC(core, 16);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0x000000EFu);
    
    // SW $1, 8($29) = 0xAFA10008
    PSXCore_WriteMemory32(core, 20, 0xAFA10008u);
    PSXCore_SetPC(core, 20);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1008), 0x12345678u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_run_multiple() {
    TEST("PSXCore_Run executes multiple instructions");
    PSXCore* core = PSXCore_Create();
    
    // ADDI $1, $0, 1 (0x20010001)
    // ADDI $2, $0, 2 (0x20020002)
    // ADDI $3, $0, 3 (0x20030003)
    PSXCore_WriteMemory32(core, 0, 0x20010001u);
    PSXCore_WriteMemory32(core, 4, 0x20020002u);
    PSXCore_WriteMemory32(core, 8, 0x20030003u);
    PSXCore_SetPC(core, 0);
    
    int result = PSXCore_Run(core, 3);
    ASSERT_EQ(result, 0);
    ASSERT_EQ(PSXCore_GetPC(core), 12u);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 1u);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 2u);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 3u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_run_early_exit() {
    TEST("PSXCore_Run respects maxInstructions limit");
    PSXCore* core = PSXCore_Create();
    
    PSXCore_WriteMemory32(core, 0, 0x20010001u);
    PSXCore_WriteMemory32(core, 4, 0x20020002u);
    PSXCore_WriteMemory32(core, 8, 0x20030003u);
    PSXCore_SetPC(core, 0);
    
    int result = PSXCore_Run(core, 2);
    ASSERT_EQ(result, 0);
    ASSERT_EQ(PSXCore_GetPC(core), 8u);
    ASSERT_EQ(PSXCore_GetGPR(core, 1), 1u);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 2u);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0u); // not executed
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_memory_read_write_api() {
    TEST("PSXCore_Read/WriteMemory API");
    PSXCore* core = PSXCore_Create();
    
    PSXCore_WriteMemory32(core, 0x1000, 0xDEADBEEFu);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x1000), 0xDEADBEEFu);
    
    PSXCore_WriteMemory16(core, 0x1004, 0xCAFEu);
    ASSERT_EQ(PSXCore_ReadMemory16(core, 0x1004), 0xCAFEu);
    
    PSXCore_WriteMemory8(core, 0x1006, 0xB0u);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0x1006), 0xB0u);
    
    PSXCore_Destroy(core);
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
    test_cpu_gpr_zero_always_zero();
    test_cpu_pc_set_get();
    test_cpu_hi_lo_set_get();
    test_ram_size();
    test_ram_access();
    test_reset();
    test_null_safety();
    test_step_basic();
    test_step_add_addu();
    test_step_sub_subu();
    test_step_logic();
    test_step_slt_sltu();
    test_step_shift();
    test_step_branch();
    test_step_jump();
    test_step_memory();
    test_run_multiple();
    test_run_early_exit();
    test_memory_read_write_api();

    printf("\n======================\n");
    printf("Results: %d/%d passed\n", tests_passed, tests_run);

    return (tests_passed == tests_run) ? 0 : 1;
}
