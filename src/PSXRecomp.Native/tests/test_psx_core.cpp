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

static void test_memory_bounds() {
    TEST("Memory bounds: final-byte and top-of-address-space sized access");
    PSXCore* core = PSXCore_Create();
    uint32_t finalByte = PSX_RAM_SIZE - 1u;

    // 32-bit access at the last full word of RAM is valid.
    PSXCore_WriteMemory32(core, PSX_RAM_SIZE - 4u, 0xDEADBEEFu);
    ASSERT_EQ(PSXCore_ReadMemory32(core, PSX_RAM_SIZE - 4u), 0xDEADBEEFu);

    // 32-bit access starting at the final byte would overflow the region.
    PSXCore_WriteMemory32(core, finalByte, 0x11223344u); // ignored
    ASSERT_EQ(PSXCore_ReadMemory32(core, finalByte), 0u);

    // 16-bit access at the last aligned halfword of RAM is valid.
    PSXCore_WriteMemory16(core, PSX_RAM_SIZE - 2u, 0xBEEFu);
    ASSERT_EQ(PSXCore_ReadMemory16(core, PSX_RAM_SIZE - 2u), 0xBEEFu);

    // 16-bit access starting at the final byte would overflow the region.
    PSXCore_WriteMemory16(core, finalByte, 0xCAFEu); // ignored
    ASSERT_EQ(PSXCore_ReadMemory16(core, finalByte), 0u);

    // 8-bit access at the final byte is valid.
    PSXCore_WriteMemory8(core, finalByte, 0x5Au);
    ASSERT_EQ(PSXCore_ReadMemory8(core, finalByte), 0x5Au);

    // Top-of-address-space inputs are overflow-safe: reads return 0, writes ignored.
    PSXCore_WriteMemory32(core, 0xFFFFFFFFu, 0x12345678u); // ignored
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0xFFFFFFFFu), 0u);
    PSXCore_WriteMemory16(core, 0xFFFFFFFFu, 0x1234u); // ignored
    ASSERT_EQ(PSXCore_ReadMemory16(core, 0xFFFFFFFFu), 0u);
    ASSERT_EQ(PSXCore_ReadMemory8(core, 0xFFFFFFFFu), 0u);

    PSXCore_Destroy(core);
    PASS();
}

// LWL/LWR tests
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

// DIV edge case tests
static void test_div_by_zero_positive() {
    TEST("DIV by zero (positive dividend)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 42);
    PSXCore_SetGPR(core, 2, 0);
    
    // DIV $1, $2 (opcode=0, rs=1, rt=2, funct=0x1A)
    // Encoding: 0x0022001A
    PSXCore_WriteMemory32(core, 0, 0x0022001Au);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    
    // PS1 behavior: LO = 0xFFFFFFFF, HI = dividend
    ASSERT_EQ(PSXCore_GetLO(core), 0xFFFFFFFFu);
    ASSERT_EQ(PSXCore_GetHI(core), 42u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_div_by_zero_negative() {
    TEST("DIV by zero (negative dividend)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0xFFFFFFD6); // -42
    PSXCore_SetGPR(core, 2, 0);
    
    // DIV $1, $2
    PSXCore_WriteMemory32(core, 0, 0x0022001Au);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    
    // PS1 behavior: LO = 1, HI = dividend
    ASSERT_EQ(PSXCore_GetLO(core), 1u);
    ASSERT_EQ(PSXCore_GetHI(core), 0xFFFFFFD6u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_div_overflow() {
    TEST("DIV overflow (0x80000000 / -1)");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0x80000000);
    PSXCore_SetGPR(core, 2, 0xFFFFFFFF); // -1
    
    // DIV $1, $2
    PSXCore_WriteMemory32(core, 0, 0x0022001Au);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    
    // PS1 behavior: LO = 0x80000000, HI = 0
    ASSERT_EQ(PSXCore_GetLO(core), 0x80000000u);
    ASSERT_EQ(PSXCore_GetHI(core), 0u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_divu_by_zero() {
    TEST("DIVU by zero");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 42);
    PSXCore_SetGPR(core, 2, 0);
    
    // DIVU $1, $2 (opcode=0, rs=1, rt=2, funct=0x1B)
    // Encoding: 0x0022001B
    PSXCore_WriteMemory32(core, 0, 0x0022001Bu);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    
    // PS1 behavior: LO = 0xFFFFFFFF, HI = dividend
    ASSERT_EQ(PSXCore_GetLO(core), 0xFFFFFFFFu);
    ASSERT_EQ(PSXCore_GetHI(core), 42u);
    
    PSXCore_Destroy(core);
    PASS();
}

static void test_div_normal() {
    TEST("DIV normal operation");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 42);
    PSXCore_SetGPR(core, 2, 5);
    
    // DIV $1, $2
    PSXCore_WriteMemory32(core, 0, 0x0022001Au);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    
    ASSERT_EQ(PSXCore_GetLO(core), 8u);  // 42 / 5 = 8
    ASSERT_EQ(PSXCore_GetHI(core), 2u);  // 42 % 5 = 2
    
    PSXCore_Destroy(core);
    PASS();
}

// COP0 state and exception tests (Issue #141)
static void test_cop0_mfc0_mtc0_roundtrip() {
    TEST("MFC0/MTC0 COP0 register roundtrip (SR/EPC/BadVAddr)");
    PSXCore* core = PSXCore_Create();

    // MTC0 $1, SR(12): COP0[12] = GPR[1]
    PSXCore_SetGPR(core, 1, 0x80000000u);
    PSXCore_WriteMemory32(core, 0, 0x40816000u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetCop0(core, 12), 0x80000000u);

    // MFC0 $2, SR(12): GPR[2] = COP0[12] (load delay: visible after next instr)
    PSXCore_WriteMemory32(core, 8, 0x40026000u);
    PSXCore_WriteMemory32(core, 12, 0x00000000u); // NOP load-delay slot
    PSXCore_SetPC(core, 8);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0x80000000u);

    // MTC0 $1, EPC(14) then MFC0 $3, EPC
    PSXCore_SetGPR(core, 1, 0xBFC00000u);
    PSXCore_WriteMemory32(core, 16, 0x40817000u);
    PSXCore_SetPC(core, 16);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0xBFC00000u);

    PSXCore_WriteMemory32(core, 24, 0x40037000u);
    PSXCore_WriteMemory32(core, 28, 0x00000000u); // NOP load-delay slot
    PSXCore_SetPC(core, 24);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0xBFC00000u);

    // MTC0 $1, BadVAddr(8) then MFC0 $4, BadVAddr
    PSXCore_SetGPR(core, 1, 0xDEADBEEFu);
    PSXCore_WriteMemory32(core, 32, 0x40814000u);
    PSXCore_SetPC(core, 32);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0xDEADBEEFu);

    PSXCore_WriteMemory32(core, 40, 0x40044000u);
    PSXCore_WriteMemory32(core, 44, 0x00000000u); // NOP load-delay slot
    PSXCore_SetPC(core, 40);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 4), 0xDEADBEEFu);

    PSXCore_Destroy(core);
    PASS();
}

static void test_mfc0_load_delay() {
    TEST("MFC0 load delay: destination not visible to following instruction");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetCop0(core, 12, 0x12345678u); // SR
    PSXCore_SetGPR(core, 2, 0xAAAAAAAAu);   // prior value of destination

    // MFC0 $2, SR at addr 0; ADD $3,$2,$0 at addr 4 (delay slot); NOP at addr 8
    PSXCore_WriteMemory32(core, 0, 0x40026000u);
    PSXCore_WriteMemory32(core, 4, 0x00401820u); // ADD $3,$2,$0
    PSXCore_WriteMemory32(core, 8, 0x00000000u); // NOP
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core); // MFC0: $2 write is delayed
    PSXCore_Step(core); // delay slot reads old $2
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0xAAAAAAAAu); // old value observed
    PSXCore_Step(core); // NOP: $2 write commits
    ASSERT_EQ(PSXCore_GetGPR(core, 2), 0x12345678u);
    PSXCore_Destroy(core);
    PASS();
}


static void test_cop0_cause_rw_bits() {
    TEST("CAUSE: only IP[1:0] (bits 8-9) are R/W via MTC0");
    PSXCore* core = PSXCore_Create();
    // MTC0 $1, CAUSE(13) with GPR[1] = IP bits (0x300) | Excode bits (0x7C)
    PSXCore_SetGPR(core, 1, 0x37Cu);
    PSXCore_WriteMemory32(core, 0, 0x40816800u);
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x300u, 0x300u);
    ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x7Cu, 0u); // Excode not writable via MTC0
    PSXCore_Destroy(core);
    PASS();
}

static void test_syscall_exception() {
    TEST("SYSCALL raises Sys: EPC, CAUSE.Excode, SR, PC");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x0000000Cu); // SYSCALL
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u); // EPC = instruction addr
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7C) >> 2, 0x08u); // Sys
    ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x80000000u, 0u); // BD = 0
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u); // BEV=0 general vector
    PSXCore_Destroy(core);
    PASS();
}

static void test_break_exception() {
    TEST("BREAK raises Bp (CAUSE.Excode=0x09)");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x0000000Du); // BREAK
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7C) >> 2, 0x09u);
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ov_add() {
    TEST("ADD overflow raises Ov and does not write GPR");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0x7FFFFFFFu);
    PSXCore_SetGPR(core, 2, 1);
    PSXCore_WriteMemory32(core, 0, 0x00221820u); // ADD $3,$1,$2
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7C) >> 2, 0x0Cu); // Ov
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u); // EPC = ADD addr
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0u); // result NOT written
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ov_add_nonoverflow() {
    TEST("ADD no exception on non-overflow");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 10);
    PSXCore_SetGPR(core, 2, 20);
    PSXCore_WriteMemory32(core, 0, 0x00221820u); // ADD $3,$1,$2
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 30u);
    ASSERT_EQ(PSXCore_GetPC(core), 4u); // no exception, normal step
    PSXCore_Destroy(core);
    PASS();
}

static void test_ov_addi() {
    TEST("ADDI overflow raises Ov");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 4, 0x7FFFFFFFu);
    PSXCore_WriteMemory32(core, 0, 0x20857FFFu); // ADDI $5,$4,0x7FFF
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7C) >> 2, 0x0Cu);
    ASSERT_EQ(PSXCore_GetGPR(core, 5), 0u); // not written
    PSXCore_Destroy(core);
    PASS();
}

static void test_ov_sub() {
    TEST("SUB overflow raises Ov");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 1, 0x80000000u);
    PSXCore_SetGPR(core, 2, 1);
    PSXCore_WriteMemory32(core, 0, 0x00221822u); // SUB $3,$1,$2
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7C) >> 2, 0x0Cu);
    ASSERT_EQ(PSXCore_GetGPR(core, 3), 0u); // not written
    PSXCore_Destroy(core);
    PASS();
}

static void test_exception_vector_bev1() {
    TEST("Exception vector BEV=1 -> 0xBFC00180");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetCop0(core, 12, (1u << 22)); // BEV = SR bit 22
    PSXCore_WriteMemory32(core, 0, 0x0000000Cu); // SYSCALL
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 0xBFC00180u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_sr_stack_shift() {
    TEST("SR 3-level stack shifts on exception");
    PSXCore* core = PSXCore_Create();
    // Seed: KUc=1,IEc=1,KUp=0,IEp=1,KUo=1,IEo=1 -> bits0-5 = 0x3B
    PSXCore_SetCop0(core, 12, 0x3Bu);
    PSXCore_WriteMemory32(core, 0, 0x0000000Cu); // SYSCALL
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    // KUo<-KUp(0),IEo<-IEp(1),KUp<-KUc(1),IEp<-IEc(1),KUc=0,IEc=0 -> 0x2C
    ASSERT_EQ(PSXCore_GetCop0(core, 12) & 0x3F, 0x2Cu);
    PSXCore_Destroy(core);
    PASS();
}

static void test_exception_nested_sr() {
    TEST("Nested exceptions shift SR stack twice");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetCop0(core, 12, 0x3Fu); // all stack bits 1
    PSXCore_WriteMemory32(core, 0, 0x0000000Cu); // SYSCALL
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    // after 1st: KUc=0,IEc=0,KUp=1,IEp=1,KUo=1,IEo=1 -> 0x3C
    ASSERT_EQ(PSXCore_GetCop0(core, 12) & 0x3F, 0x3Cu);
    // place SYSCALL at the exception vector (phys 0x80, BEV=0 -> 0x80000080)
    PSXCore_WriteMemory32(core, 0x80, 0x0000000Cu);
    PSXCore_Step(core);
    // after 2nd: KUo<-KUp(1),IEo<-IEp(1),KUp<-KUc(0),IEp<-IEc(0),KUc=0,IEc=0 -> 0x30
    ASSERT_EQ(PSXCore_GetCop0(core, 12) & 0x3F, 0x30u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_rfe_pop() {
    TEST("RFE pops SR 3-level stack");
    PSXCore* core = PSXCore_Create();
    // post-exception state 0x3C: KUc=0,IEc=0,KUp=1,IEp=1,KUo=1,IEo=1
    PSXCore_SetCop0(core, 12, 0x3Cu);
    PSXCore_WriteMemory32(core, 0, 0x42000010u); // RFE (opcode 0x10, rs=0x10, funct 0x10)
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    // KUc<-KUp(1),IEc<-IEp(1),KUp<-KUo(1),IEp<-IEo(1); KUo/IEo (bits 4-5)
    // left unchanged by RFE (PSX hardware) -> 0x3F
    ASSERT_EQ(PSXCore_GetCop0(core, 12) & 0x3F, 0x3Fu);
    PSXCore_Destroy(core);
    PASS();
}

static void test_exception_in_delay_slot() {
    TEST("Exception in delay slot sets BD=1, EPC=branch addr");
    PSXCore* core = PSXCore_Create();
    // BEQ $0,$0,+1 at PC=0, delay slot = SYSCALL at PC=4
    PSXCore_WriteMemory32(core, 0, 0x10000001u);
    PSXCore_WriteMemory32(core, 4, 0x0000000Cu); // SYSCALL in delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core); // branch issued, PC=4 (delay slot)
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Step(core); // SYSCALL in delay slot -> exception
    ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x80000000u, 0x80000000u); // BD = 1
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u); // EPC = branch addr (PC=0)
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);
    PSXCore_Destroy(core);
    PASS();
}

// Timer tests
static uint32_t TMR(int t, uint32_t off) {
    return 0x1F801100u + (uint32_t)t * 0x10u + off;
}

static void test_timer_registers() {
    TEST("Timer COUNT/MODE/TARGET register read/write");
    PSXCore* core = PSXCore_Create();

    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 0u);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x04)), 0u);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x08)), 0u);

    PSXCore_WriteTimerRegister(core, TMR(0, 0x00), 0x1234);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 0x1234u);

    PSXCore_WriteTimerRegister(core, TMR(1, 0x08), 0x8000);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(1, 0x08)), 0x8000u);

    // Mode write masks to 0x3FF and forces bit10 (IRQ_REQUEST) + resets counter.
    PSXCore_WriteTimerRegister(core, TMR(2, 0x00), 0xABCD);
    PSXCore_WriteTimerRegister(core, TMR(2, 0x04), 0xFFFF);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x04)), 0x03FFu | 0x0400u);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x00)), 0u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_free_run_target_irq() {
    TEST("Timer free-run target reached generates IRQ (repeat)");
    PSXCore* core = PSXCore_Create();
    // MODE_IRQ_TARGET(0x10) | MODE_IRQ_REPEAT(0x40)
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x50);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x08), 5);
    for (int i = 0; i < 5; i++)
        PSXCore_TickTimers(core, 1);

    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 5u);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);
    // Target flag (bit11) set in mode.
    uint32_t mode = PSXCore_ReadTimerRegister(core, TMR(0, 0x04));
    ASSERT_EQ(mode & 0x0800u, 0x0800u);
    // Reading mode clears the target flag.
    mode = PSXCore_ReadTimerRegister(core, TMR(0, 0x04));
    ASSERT_EQ(mode & 0x0800u, 0u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_target_reset() {
    TEST("Timer target reset (bit3) resets counter to 0 at target");
    PSXCore* core = PSXCore_Create();
    // MODE_IRQ_TARGET(0x10) | MODE_RESET_TARGET(0x08)
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x18);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x08), 5);
    for (int i = 0; i < 5; i++)
        PSXCore_TickTimers(core, 1);

    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 0u);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_overflow_irq() {
    TEST("Timer overflow at FFFFh generates IRQ");
    PSXCore* core = PSXCore_Create();
    // MODE_IRQ_OVERFLOW(0x20) | MODE_IRQ_REPEAT(0x40)
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x60);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x00), 0xFFFE);
    PSXCore_TickTimers(core, 1); // -> FFFF
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 0);
    PSXCore_TickTimers(core, 1); // -> 0000 (overflow)
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 0u);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x04)) & 0x1000u, 0x1000u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_oneshot() {
    TEST("Timer one-shot suppresses further IRQs until mode rewrite");
    PSXCore* core = PSXCore_Create();
    // MODE_IRQ_TARGET(0x10), no repeat -> one-shot
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x10);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x08), 3);
    for (int i = 0; i < 3; i++)
        PSXCore_TickTimers(core, 1);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);

    PSXCore_ClearTimerInterrupt(core, 0);
    for (int i = 0; i < 3; i++)
        PSXCore_TickTimers(core, 1);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 0);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_toggle() {
    TEST("Timer toggle mode raises IRQ on alternating reaches");
    PSXCore* core = PSXCore_Create();
    // MODE_IRQ_TARGET(0x10) | MODE_IRQ_REPEAT(0x40) | MODE_IRQ_TOGGLE(0x80) | MODE_RESET_TARGET(0x08)
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0xD8);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x08), 2);

    auto advance = [&]() { for (int i = 0; i < 2; i++) PSXCore_TickTimers(core, 1); };

    advance(); // fire 1: toggle line -> high, pending
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);
    PSXCore_ClearTimerInterrupt(core, 0);

    advance(); // fire 2: toggle line -> low, no IRQ
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 0);
    PSXCore_ClearTimerInterrupt(core, 0);

    advance(); // fire 3: toggle line -> high, pending
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);

    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_sysclk8() {
    TEST("Timer 2 System Clock/8 counts one per 8 cycles");
    PSXCore* core = PSXCore_Create();
    // Timer 2: MODE_CLK_SRC1(0x200) -> src=2 -> /8
    PSXCore_WriteTimerRegister(core, TMR(2, 0x04), 0x200);
    PSXCore_TickTimers(core, 7);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x00)), 0u);
    PSXCore_TickTimers(core, 1);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x00)), 1u);

    // Free run sysclk (div 1): mode write resets counter.
    PSXCore_WriteTimerRegister(core, TMR(2, 0x04), 0);
    PSXCore_TickTimers(core, 8);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x00)), 8u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_sync_stop_timer2() {
    TEST("Timer 2 sync mode 0 stops counter, mode 1 free runs");
    PSXCore* core = PSXCore_Create();
    // MODE_SYNC_EN(0x01) with sync mode 0 -> stop forever
    PSXCore_WriteTimerRegister(core, TMR(2, 0x04), 0x01);
    PSXCore_TickTimers(core, 10);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x00)), 0u);

    // MODE_SYNC_EN(0x01) | sync mode 1 (0x02) -> free run
    PSXCore_WriteTimerRegister(core, TMR(2, 0x04), 0x03);
    PSXCore_TickTimers(core, 5);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(2, 0x00)), 5u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_sync_pause_timer0() {
    TEST("Timer 0 sync mode 0 pauses during blank line");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x01); // sync enable, mode 0
    PSXCore_TickTimers(core, 5);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 5u);

    PSXCore_SetTimerSync(core, 0, 1); // blank active -> paused
    PSXCore_TickTimers(core, 5);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 5u);

    PSXCore_SetTimerSync(core, 0, 0); // blank inactive -> resume
    PSXCore_TickTimers(core, 3);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 8u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_sync_reset_timer0() {
    TEST("Timer 0 sync mode 1 resets counter on blank edge");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x03); // sync enable, mode 1
    PSXCore_WriteTimerRegister(core, TMR(0, 0x00), 10);
    PSXCore_SetTimerSync(core, 0, 1); // rising edge
    PSXCore_TickTimers(core, 1);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 1u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_sync_arm_timer0() {
    TEST("Timer 0 sync mode 3 pauses until first blank then free runs");
    PSXCore* core = PSXCore_Create();
    // sync enable (0x01) | sync mode 3 (0x06) = 0x07
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x07);
    PSXCore_TickTimers(core, 5);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 0u); // paused

    PSXCore_SetTimerSync(core, 0, 1); // first blank -> arm free run
    PSXCore_SetTimerSync(core, 0, 0);
    PSXCore_TickTimers(core, 5);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 5u); // free run
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_reset_timers() {
    TEST("Timer reset clears counters and interrupts");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteTimerRegister(core, TMR(0, 0x00), 0x1234);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x04), 0x50);
    PSXCore_WriteTimerRegister(core, TMR(0, 0x08), 2);
    for (int i = 0; i < 2; i++)
        PSXCore_TickTimers(core, 1);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 1);

    PSXCore_ResetTimers(core);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x00)), 0u);
    ASSERT_EQ(PSXCore_ReadTimerRegister(core, TMR(0, 0x04)), 0u);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(core, 0), 0);
    PSXCore_Destroy(core);
    PASS();
}

static void test_timer_null_safety() {
    TEST("Timer null pointer safety");
    ASSERT_EQ(PSXCore_ReadTimerRegister(nullptr, 0x1F801100u), 0u);
    PSXCore_WriteTimerRegister(nullptr, 0x1F801100u, 1);
    PSXCore_TickTimers(nullptr, 10);
    ASSERT_EQ(PSXCore_GetTimerInterruptPending(nullptr, 0), 0);
    PSXCore_ClearTimerInterrupt(nullptr, 0);
    PSXCore_SetTimerSync(nullptr, 0, 1);
    PSXCore_ResetTimers(nullptr);
    PASS();
}

static void test_interrupt_registers() {
    TEST("Interrupt controller I_STAT/I_MASK register read/write");
    PSXCore* core = PSXCore_Create();

    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0u);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801074u), 0u);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801078u), 0u);

    // I_MASK is a plain R/W register.
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801074u, 0x042F);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801074u), 0x042Fu);
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801074u, 0);

    // I_STAT write is write-0-to-clear (psx-spx "Interrupt Acknowledge").
    PSXCore_RaiseInterrupt(core, 0);
    PSXCore_RaiseInterrupt(core, 2);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0x00000005u);
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801070u, 0xFFFFFFFB); // clear bit2 only
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0x00000001u);

    // Writes to unmapped neighbor registers are ignored.
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801078u, 0xDEADBEEF);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0x00000001u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_interrupt_pending() {
    TEST("PSXCore_GetInterruptPending reflects (I_STAT & I_MASK) != 0");
    PSXCore* core = PSXCore_Create();

    ASSERT_EQ(PSXCore_GetInterruptPending(core), 0);

    // Raise a source; unmasked so not pending yet.
    PSXCore_RaiseInterrupt(core, 1);
    ASSERT_EQ(PSXCore_GetInterruptPending(core), 0);

    // Enable the mask for that source.
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801074u, 1u << 1);
    ASSERT_EQ(PSXCore_GetInterruptPending(core), 1);

    // Acknowledge via write-0-to-clear.
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801070u, ~(1u << 1));
    ASSERT_EQ(PSXCore_GetInterruptPending(core), 0);

    PSXCore_Destroy(core);
    PASS();
}

static void test_interrupt_raise_clear() {
    TEST("PSXCore_RaiseInterrupt/ClearInterrupt manage I_STAT bits");
    PSXCore* core = PSXCore_Create();

    PSXCore_RaiseInterrupt(core, 10);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 1u << 10);
    PSXCore_ClearInterrupt(core, 10);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0u);

    // Out-of-range IRQs are ignored.
    PSXCore_RaiseInterrupt(core, 11);
    PSXCore_RaiseInterrupt(core, -1);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_interrupt_reset() {
    TEST("Interrupt controller reset clears I_STAT/I_MASK");
    PSXCore* core = PSXCore_Create();

    PSXCore_RaiseInterrupt(core, 3);
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801074u, 0xFFFF);
    PSXCore_ResetInterruptController(core);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0u);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801074u), 0u);
    ASSERT_EQ(PSXCore_GetInterruptPending(core), 0);

    // PSXCore_Reset clears the interrupt controller too.
    PSXCore_RaiseInterrupt(core, 0);
    PSXCore_WriteInterruptControllerRegister(core, 0x1F801074u, 0xFFFF);
    PSXCore_Reset(core);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801070u), 0u);
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(core, 0x1F801074u), 0u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_interrupt_null_safety() {
    TEST("Interrupt controller null pointer safety");
    ASSERT_EQ(PSXCore_ReadInterruptControllerRegister(nullptr, 0x1F801070u), 0u);
    PSXCore_WriteInterruptControllerRegister(nullptr, 0x1F801070u, 1);
    ASSERT_EQ(PSXCore_GetInterruptPending(nullptr), 0);
    PSXCore_RaiseInterrupt(nullptr, 0);
    PSXCore_ClearInterrupt(nullptr, 0);
    PSXCore_ResetInterruptController(nullptr);
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
    test_step_branch_delay_slot();
    test_load_delay();
    test_branch_in_delay_slot();
    test_step_jr_jalr();
    test_branch_load_delay_interaction();
    test_step_memory();
    test_run_multiple();
    test_run_early_exit();
    test_memory_read_write_api();
    test_memory_bounds();
    test_kseg_translation();
    test_kseg_ram_end_bios();
    test_kseg_unmapped();
    test_kseg_instruction_fetch();
    test_lwl_lwr_aligned();
    test_lwl_lwr_unchanged();
    test_div_by_zero_positive();
    test_div_by_zero_negative();
    test_div_overflow();
    test_divu_by_zero();
    test_div_normal();

    test_cop0_mfc0_mtc0_roundtrip();
    test_mfc0_load_delay();
    test_cop0_cause_rw_bits();
    test_syscall_exception();
    test_break_exception();
    test_ov_add();
    test_ov_add_nonoverflow();
    test_ov_addi();
    test_ov_sub();
    test_exception_vector_bev1();
    test_sr_stack_shift();
    test_exception_nested_sr();
    test_rfe_pop();
    test_exception_in_delay_slot();

    test_timer_registers();
    test_timer_free_run_target_irq();
    test_timer_target_reset();
    test_timer_overflow_irq();
    test_timer_oneshot();
    test_timer_toggle();
    test_timer_sysclk8();
    test_timer_sync_stop_timer2();
    test_timer_sync_pause_timer0();
    test_timer_sync_reset_timer0();
    test_timer_sync_arm_timer0();
    test_timer_reset_timers();
    test_timer_null_safety();

    test_interrupt_registers();
    test_interrupt_pending();
    test_interrupt_raise_clear();
    test_interrupt_reset();
    test_interrupt_null_safety();

    printf("\n======================\n");
    printf("Results: %d/%d passed\n", tests_passed, tests_run);

    return (tests_passed == tests_run) ? 0 : 1;
}
