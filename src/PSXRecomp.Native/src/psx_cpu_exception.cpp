// PSXCpu exception resolution (RaiseException/RaiseAddressError). Moved
// verbatim out of psx_cpu.cpp (Issue #524); owned by the exception-resolution
// Rust migration slice (#530).

#include "psx_cpu.h"
#include <cstdint>

void PSXCpu::RaiseAddressError(uint32_t excode, uint32_t addr) {
    // BadVaddr (cop0r8) is updated only for AdEL/AdES (docs/cpu/cop0.md).
    cop0_[8] = addr;
    RaiseException(excode);
}

void PSXCpu::RaiseException(uint32_t excode, uint32_t ce) {
    // EPC: branch instruction address (delay_slot_pc_ - 4, since delay_slot_pc_
    // is the delay-slot instruction's address = branch addr + 4) if in a delay
    // slot, else the current instruction's address (docs/cpu/pipeline.md).
    uint32_t epc = executing_in_delay_slot_ ? (delay_slot_pc_ - 4u) : executing_instr_addr_;
    bool bd = executing_in_delay_slot_;

    // CAUSE: set Excode[6:2], CE[29:28] and BD (bit 31); preserve IP[1:0]
    // (bits 8-9). CE is cleared for every exception that is not CpU so a stale
    // coprocessor number from an earlier CpU cannot be misread afterwards.
    uint32_t cause = cop0_[13];
    cause &= ~(0x7Cu | 0x30000000u | 0x80000000u);
    cause |= (excode << 2) | ((ce & 3u) << 28);
    if (bd) {
        cause |= 0x80000000u;
    }
    cop0_[13] = cause;

    // EPC = branch instruction addr (delay slot) or current instruction addr.
    cop0_[14] = epc;

    // SR 3-level stack shift (docs/cpu/cop0.md):
    //   KUo<--KUp, IEo<--IEp; KUp<--KUc, IEp<--IEc; KUc<--0, IEc<--0
    uint32_t sr = cop0_[12];
    uint32_t kuc = (sr >> 0) & 1;
    uint32_t iec = (sr >> 1) & 1;
    uint32_t kup = (sr >> 2) & 1;
    uint32_t iep = (sr >> 3) & 1;
    sr &= ~0x3Fu;
    sr |= (kup << 4) | (iep << 5) | (kuc << 2) | (iec << 3);
    cop0_[12] = sr;

    // PC = exception vector (BEV is SR bit 22).
    bool bev = (cop0_[12] >> 22) & 1;
    pc_ = bev ? 0xBFC00180u : 0x80000080u;

    // Snapshot the exception resolution (Issue #481): the per-step fields the
    // handler computed above get reused by the next step, so a caller that asks
    // after Step() returns reads these stable values instead.
    last_exception_code_ = excode;
    last_exception_fault_pc_ = epc;
    last_exception_in_delay_slot_ = bd;

    exception_raised_ = true;
    branch_pending_ = false;
    branch_issued_ = false;
}
