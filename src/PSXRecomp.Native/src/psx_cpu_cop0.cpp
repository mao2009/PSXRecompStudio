// PSXCpu SYSCALL/BREAK and COP0 moves (MFC0/MTC0/RFE). Moved verbatim out of
// psx_cpu.cpp (Issue #524); owned by the COP0 Rust migration slice (#529).

#include "psx_cpu.h"
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
        // CAUSE: only IP[1:0] (bits 8-9, software interrupt pending) are R/W.
        uint32_t ip = gpr_[rt] & 0x300;
        cop0_[13] = (cop0_[13] & ~0x300u) | ip;
    } else {
        cop0_[rd] = gpr_[rt];
    }
}

void PSXCpu::ExecRfe() {
    // RFE pops the SR 3-level stack (docs/cpu/cop0.md):
    //   KUc<--KUp, IEc<--IEp; KUp<--KUo, IEp<--IEo
    // KUo/IEo (bits 4-5) are left unchanged by RFE (PSX hardware: psx-spx).
    // PC restore is a software (JR) responsibility and out of scope (ADR-005).
    uint32_t sr = cop0_[12];
    uint32_t kup = (sr >> 2) & 1;
    uint32_t iep = (sr >> 3) & 1;
    uint32_t kuo = (sr >> 4) & 1;
    uint32_t ieo = (sr >> 5) & 1;
    sr &= ~0x0Fu;
    sr |= (kup) | (iep << 1) | (kuo << 2) | (ieo << 3);
    cop0_[12] = sr;
}
