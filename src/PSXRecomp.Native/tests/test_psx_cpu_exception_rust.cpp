// Native tests for the PSXCpu exception resolution (EPC/CAUSE/SR stack/vector)
// migration surface (src/psx_cpu_exception.cpp; Rust migration slice #530). The
// existing tests below were moved verbatim out of test_psx_core.cpp (Issue
// #524). New tests for this slice are added to this file only and called from
// run_psx_cpu_exception_rust_tests(); test_psx_core.cpp and CMakeLists.txt
// already wire this file in.

#include "psx_core.h"
#include "test_harness.h"

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

static void test_ri_in_delay_slot() {
    TEST("RI in a delay slot sets BD=1, EPC=branch addr");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x10000001u); // BEQ $0,$0,+1
    PSXCore_WriteMemory32(core, 4, 0x78000000u); // reserved opcode in delay slot
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetPC(core), 4u);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0x0Au);
    ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x80000000u, 0x80000000u); // BD = 1
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u); // EPC = branch addr
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);
    PSXCore_Destroy(core);
    PASS();
}

// Issue #377: PSXCore_Step() returns 0 for a step that faulted, so a caller that
// must not mistake a faulted run for a clean one (the differential reference
// oracle, the interpreter-backed title engine) needs an explicit signal. Before
// this existed, a GTE word made those callers report a clean Success.
static void test_exception_raised_flag() {
    TEST("PSXCore_GetExceptionRaised reports a GTE/CpU fault that Step() returns 0 for");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x00000000u); // NOP
    PSXCore_WriteMemory32(core, 4, 0x4A180001u); // RTPS (GTE command) -> CpU
    PSXCore_SetPC(core, 0);

    ASSERT_EQ(PSXCore_Step(core), 0);               // NOP
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0); // ... did not fault

    ASSERT_EQ(PSXCore_Step(core), 0);               // GTE: Step() still reports success
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 1); // ... but the flag shows the fault
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0x0Bu); // CpU
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);

    // The flag is per-step, not sticky: the next (non-faulting) step clears it.
    PSXCore_WriteMemory32(core, 0x80u, 0x00000000u); // NOP at the vector
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0);

    PSXCore_Destroy(core);
    PASS();
}

// Issue #481: the recompiler needs the faulting instruction's resolution
// (Excode, EPC, delay-slot flag) without re-deriving it. The CPU captures it
// when it raises and the API surfaces it scalarly.
static void test_trap_resolution_break_standalone_getters() {
    TEST("BREAK resolution getters report Excode=0x09, EPC, BD=0 for a standalone trap (Issue #481)");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x14, 0x0000000Du); // BREAK (SPECIAL funct 0x0D)
    PSXCore_SetPC(core, 0x14);

    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 1);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x09u);
    ASSERT_EQ(PSXCore_GetExceptionFaultPc(core), 0x14u); // EPC = the BREAK's own address
    ASSERT_EQ(PSXCore_GetExceptionInDelaySlot(core), 0);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);

    // The captured resolution is per-step like ExceptionRaised: the next clean
    // step clears it, so a stale BREAK can never leak into a later instruction.
    PSXCore_WriteMemory32(core, 0x80, 0x00000000u); // NOP at the vector
    ASSERT_EQ(PSXCore_Step(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0u);
    ASSERT_EQ(PSXCore_GetExceptionFaultPc(core), 0u);
    ASSERT_EQ(PSXCore_GetExceptionInDelaySlot(core), 0);

    PSXCore_Destroy(core);
    PASS();
}

static void test_trap_resolution_break_in_delay_slot_getters() {
    TEST("BREAK in a delay slot captures EPC=owning branch PC, BD=1 (Issue #481)");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0x10, 0x08000008u); // J (region-relative target)
    PSXCore_WriteMemory32(core, 0x14, 0x0000000Du); // BREAK in the delay slot
    PSXCore_SetPC(core, 0x10);

    ASSERT_EQ(PSXCore_Step(core), 0); // J retires, enters the delay slot
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 0);
    ASSERT_EQ(PSXCore_Step(core), 0); // the delay-slot BREAK raises
    ASSERT_EQ(PSXCore_GetExceptionRaised(core), 1);
    ASSERT_EQ(PSXCore_GetExceptionCode(core), 0x09u);
    ASSERT_EQ(PSXCore_GetExceptionFaultPc(core), 0x10u); // EPC points at the J, not the BREAK
    ASSERT_EQ(PSXCore_GetExceptionInDelaySlot(core), 1);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);

    PSXCore_Destroy(core);
    PASS();
}

static void test_cause_ce_cleared_by_non_cpu_exception() {
    TEST("CAUSE.CE is cleared by a following non-CpU exception");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x4C010000u); // MFC3 -> CpU, CE=3
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) >> 28) & 3u, 3u);
    PSXCore_WriteMemory32(core, 0x80u, 0x0000000Cu); // SYSCALL at the vector
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0x08u); // Sys
    ASSERT_EQ((PSXCore_GetCop0(core, 13) >> 28) & 3u, 0u);      // CE reset
    PSXCore_Destroy(core);
    PASS();
}

// Issue #530: the EPC/CAUSE/SR/vector arithmetic moved to Rust
// (rust/src/cpu_exception.rs); these pin the paths it feeds end to end.
static void test_cause_software_ip_preserved() {
    TEST("Exception preserves CAUSE software IP[1:0] and clears a stale Excode/BD");
    PSXCore* core = PSXCore_Create();
    // IP1|IP0 set, stale Excode 0x1F and BD; SR IEc=0 so no interrupt is taken.
    PSXCore_SetCop0(core, 13, 0x80000300u | 0x7Cu);
    PSXCore_WriteMemory32(core, 0, 0x0000000Cu); // SYSCALL
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EQ(PSXCore_GetCop0(core, 13), 0x00000300u | (0x08u << 2));
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_cpu_exception_sets_ce() {
    TEST("CpU sets CAUSE.CE to the coprocessor number");
    PSXCore* core = PSXCore_Create();
    PSXCore_WriteMemory32(core, 0, 0x4C010000u); // MFC3 -> CpU, CE=3
    PSXCore_SetPC(core, 0);
    PSXCore_Step(core);
    ASSERT_EXCEPTION(core, 0x0Bu, 0u);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) >> 28) & 3u, 3u);
    PSXCore_Destroy(core);
    PASS();
}

static void test_ades_in_delay_slot_badvaddr() {
    TEST("AdES in a delay slot sets BadVaddr, BD=1, EPC=branch addr");
    PSXCore* core = PSXCore_Create();
    PSXCore_SetGPR(core, 29, 0x1000u);
    PSXCore_WriteMemory32(core, 0x20, 0x10000001u); // BEQ $0,$0,+1
    PSXCore_WriteMemory32(core, 0x24, 0xAFA20003u); // SW $2, 3($29) -> 0x1003
    PSXCore_SetPC(core, 0x20);
    PSXCore_Step(core);
    PSXCore_Step(core);
    ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, 0x05u);
    ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x80000000u, 0x80000000u);
    ASSERT_EQ(PSXCore_GetCop0(core, 14), 0x20u);
    ASSERT_EQ(PSXCore_GetCop0(core, 8), 0x1003u);
    ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u);
    PSXCore_Destroy(core);
    PASS();
}

void run_psx_cpu_exception_rust_tests() {
    test_cause_software_ip_preserved();
    test_cpu_exception_sets_ce();
    test_ades_in_delay_slot_badvaddr();
    test_exception_vector_bev1();
    test_sr_stack_shift();
    test_exception_nested_sr();
    test_exception_in_delay_slot();
    test_ri_in_delay_slot();
    test_exception_raised_flag();
    test_trap_resolution_break_standalone_getters();
    test_trap_resolution_break_in_delay_slot_getters();
    test_cause_ce_cleared_by_non_cpu_exception();
}
