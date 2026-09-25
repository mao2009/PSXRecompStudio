// Native tests for the PSXCpu SYSCALL/BREAK and COP0 moves (MFC0/MTC0/RFE)
// migration surface (src/psx_cpu_cop0.cpp; Rust migration slice #529). The
// existing tests below were moved verbatim out of test_psx_core.cpp (Issue
// #524). New tests for this slice are added to this file only and called from
// run_psx_cpu_cop0_rust_tests(); test_psx_core.cpp and CMakeLists.txt already
// wire this file in.

#include "psx_core.h"
#include "test_harness.h"

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

void run_psx_cpu_cop0_rust_tests() {
    test_cop0_mfc0_mtc0_roundtrip();
    test_mfc0_load_delay();
    test_cop0_cause_rw_bits();
    test_syscall_exception();
    test_break_exception();
    test_rfe_pop();
}
