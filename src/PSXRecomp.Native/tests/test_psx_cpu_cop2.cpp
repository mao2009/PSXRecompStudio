// Native tests for the CPU side of COP2/GTE (src/psx_cpu_cop2.cpp, Issue #447):
// SR.CU2 gating, the GTE bridge (PSXCore_SetGteCallbacks), the MFC2/CFC2 load
// delay, LWC2/SWC2 addressing and faults, and the fail-closed status for a
// command the GTE refuses. The GTE itself is managed (GteRegisterBank); here a
// plain register array stands in for it, so only the CPU contract is tested.

#include "psx_core.h"
#include "test_harness.h"

namespace {

struct StubGte {
    uint32_t regs[64] = {};
    uint32_t last_command = 0;
    int commands = 0;
};

uint32_t StubRead(void* context, uint32_t reg) { return static_cast<StubGte*>(context)->regs[reg]; }
void StubWrite(void* context, uint32_t reg, uint32_t value) { static_cast<StubGte*>(context)->regs[reg] = value; }
int32_t StubCommand(void* context, uint32_t command) {
    auto* gte = static_cast<StubGte*>(context);
    gte->last_command = command;
    if ((command & 0x3Fu) != 0x06u) return 1; // only "NCLIP" is implemented
    gte->regs[24] = 42;                       // MAC0
    gte->commands++;
    return 0;
}

constexpr uint32_t kBase = 0x1000u;
constexpr uint32_t kSrCu2 = 1u << 30;

PSXCore* CoreWith(StubGte* gte, uint32_t sr, const uint32_t* words, int count) {
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGteCallbacks(core, gte, StubRead, StubWrite, StubCommand);
    for (int i = 0; i < count; i++) PSXCore_WriteMemory32(core, kBase + 4u * i, words[i]);
    PSXCore_SetCop0(core, 12, sr);
    PSXCore_SetPC(core, kBase);
    return core;
}

}  // namespace

static void test_cop2_cu2_clear_raises_cpu() {
    TEST("COP2 with SR.CU2 clear raises CpU CE=2 and leaves the GTE untouched");
    StubGte gte;
    const uint32_t words[] = {0x48C8E800u}; // CTC2 $t0, $29
    PSXCore* core = CoreWith(&gte, 0, words, 1);
    PSXCore_SetGPR(core, 8, 0x1234u);
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x0Bu);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) >> 28) & 3u, 2u);
    ASSERT_EQ(gte.regs[32 + 29], 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_without_gte_raises_cpu() {
    TEST("COP2 with SR.CU2 set but no GTE attached raises CpU CE=2");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, kBase, 0x4A000006u);
    PSXCore_SetCop0(core, 12, kSrCu2);
    PSXCore_SetPC(core, kBase);
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x0Bu);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_ctc2_cfc2_and_load_delay() {
    TEST("CTC2 writes control reg at once; CFC2/MFC2 commit through the load delay");
    StubGte gte;
    gte.regs[24] = 0xCAFEu;
    const uint32_t words[] = {
        0x48C8E800u, // CTC2 $t0, $29      -> control 29 (reg 61)
        0x4849E800u, // CFC2 $t1, $29      -> $t1 delayed
        0x352A0000u, // ori $t2, $t1, 0    (delay slot: old $t1)
        0x352B0000u, // ori $t3, $t1, 0    (new $t1)
        0x480CC000u, // MFC2 $t4, $24
        0x358D0000u, // ori $t5, $t4, 0    (delay slot: old $t4)
    };
    PSXCore* core = CoreWith(&gte, kSrCu2, words, 6);
    PSXCore_SetGPR(core, 8, 0x0000ABCDu);
    PSXCore_SetGPR(core, 9, 0x5555u);
    PSXCore_SetGPR(core, 12, 7u);
    for (int i = 0; i < 6; i++) {
        ASSERT_EQ(PSXCore_Step(core), 0);
        ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0);
    }
    ASSERT_EQ(gte.regs[32 + 29], 0x0000ABCDu);
    ASSERT_EQ(PSXCore_GetGPR(core, 10), 0x5555u);
    ASSERT_EQ(PSXCore_GetGPR(core, 11), 0x0000ABCDu);
    ASSERT_EQ(PSXCore_GetGPR(core, 13), 7u);
    ASSERT_EQ(PSXCore_GetGPR(core, 12), 0xCAFEu);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_mtc2_reads_gpr_before_pending_load() {
    TEST("MTC2 in a load-delay slot sends the GPR's old value");
    StubGte gte;
    const uint32_t words[] = {
        0x8C082000u, // lw $t0, 0x2000($zero)
        0x48880000u, // MTC2 $t0, $0 (delay slot)
        0x48880800u, // MTC2 $t0, $1
    };
    PSXCore* core = CoreWith(&gte, kSrCu2, words, 3);
    PSXCore_WriteMemory32(core, 0x2000u, 0x99u);
    PSXCore_SetGPR(core, 8, 0x11u);
    for (int i = 0; i < 3; i++) ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(gte.regs[0], 0x11u);
    ASSERT_EQ(gte.regs[1], 0x99u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_lwc2_swc2() {
    TEST("LWC2/SWC2 move a word between RAM and a GTE data register");
    StubGte gte;
    const uint32_t words[] = {
        0xC9100000u, // LWC2 $16, 0($t0)
        0xE9100004u, // SWC2 $16, 4($t0)
    };
    PSXCore* core = CoreWith(&gte, kSrCu2, words, 2);
    PSXCore_SetGPR(core, 8, 0x80002000u);
    PSXCore_WriteMemory32(core, 0x2000u, 0xABCD1234u);
    for (int i = 0; i < 2; i++) ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(gte.regs[16], 0xABCD1234u);
    ASSERT_EQ(PSXCore_ReadMemory32(core, 0x2004u), 0xABCD1234u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_misaligned_lwc2_swc2() {
    TEST("Misaligned LWC2 raises AdEL and SWC2 AdES, with BadVAddr");
    StubGte gte;
    const uint32_t lwc2[] = {0xC9010002u}; // LWC2 $1, 2($t0)
    PSXCore* core = CoreWith(&gte, kSrCu2, lwc2, 1);
    PSXCore_SetGPR(core, 8, 0x80002000u);
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x04u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x80002002u);
    ASSERT_EQ(gte.regs[1], 0u);
    PSXCore_Destroy(core);

    const uint32_t swc2[] = {0xE9010002u}; // SWC2 $1, 2($t0)
    core = CoreWith(&gte, kSrCu2, swc2, 1);
    PSXCore_SetGPR(core, 8, 0x80002000u);
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x05u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x80002002u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_command_dispatch_and_fail_closed() {
    TEST("A GTE command runs through the bridge; a refused one stops with a distinct status");
    StubGte gte;
    const uint32_t words[] = {
        0x4A001406u, // NCLIP
        0x4A280030u, // RTPT: refused by the stub
    };
    PSXCore* core = CoreWith(&gte, kSrCu2, words, 2);
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(gte.last_command, 0x00001406u);
    ASSERT_EQ(gte.regs[24], 42u);
    ASSERT_EQ(PSXCore_Step(core), PSX_STEP_GTE_COMMAND_UNSUPPORTED);
    ASSERT_EQ(gte.last_command, 0x00280030u);
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0);
    ASSERT_EQ(PSXCore_GetPC(core), kBase + 4u);
    // Run() stops on it too.
    ASSERT_EQ(PSXCore_Run(core, 10), PSX_STEP_GTE_COMMAND_UNSUPPORTED);
    ASSERT_EQ(PSXCore_GetPC(core), kBase + 4u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cop2_detach() {
    TEST("PSXCore_SetGteCallbacks with null callbacks detaches the GTE");
    StubGte gte;
    const uint32_t words[] = {0x4A001406u};
    PSXCore* core = CoreWith(&gte, kSrCu2, words, 1);
    PSXCore_SetGteCallbacks(core, nullptr, nullptr, nullptr, nullptr);
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x0Bu);
    ASSERT_EQ(gte.commands, 0);
    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_cop2_tests() {
    test_cop2_cu2_clear_raises_cpu();
    test_cop2_without_gte_raises_cpu();
    test_cop2_ctc2_cfc2_and_load_delay();
    test_cop2_mtc2_reads_gpr_before_pending_load();
    test_cop2_lwc2_swc2();
    test_cop2_misaligned_lwc2_swc2();
    test_cop2_command_dispatch_and_fail_closed();
    test_cop2_detach();
}
