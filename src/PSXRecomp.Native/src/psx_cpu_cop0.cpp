// PSXCpu SYSCALL/BREAK and COP0 moves (MFC0/MTC0/RFE). Moved verbatim out of
// psx_cpu.cpp (Issue #524); owned by the COP0 Rust migration slice (#529).

#include "psx_cpu.h"
#include "psx_cpu_cop0.h"
#include <cstdint>

// System
void PSXCpu::ExecSyscall() {
    RaiseException(0x08); // Sys
}

void PSXCpu::ExecBreak() {
    RaiseException(0x09); // Bp
}

// Coprocessor 0
void PSXCpu::ExecMfc0(uint32_t rt, uint32_t rd) {
    if (rd >= PSX_COP0_COUNT) return;
    // MFC0 writes the GPR through the load-delay slot (R3000A: the destination
    // is not visible to the immediately following instruction).
    WriteRegDelayed(rt, cop0_[rd]);
}

void PSXCpu::ExecMtc0(uint32_t rt, uint32_t rd) {
    if (rd >= PSX_COP0_COUNT) return;
    if (rd == 13) {
        // CAUSE: only IP[1:0] (bits 8-9, software interrupt pending) are R/W
        // (Rust, psx_cpu_cop0.h, Issue #529).
        cop0_[13] = psx_cpu_cop0_write_cause(cop0_[13], gpr_[rt]);
    } else {
        cop0_[rd] = gpr_[rt];
    }
}

void PSXCpu::ExecRfe() {
    // RFE pops the SR 3-level KU/IE stack, leaving KUo/IEo unchanged
    // (docs/cpu/cop0.md; Rust, psx_cpu_cop0.h, Issue #529).
    // PC restore is a software (JR) responsibility and out of scope (ADR-005).
    cop0_[12] = psx_cpu_cop0_rfe(cop0_[12]);
}
