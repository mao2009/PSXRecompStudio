// PSXCpu exception resolution (RaiseException/RaiseAddressError). The EPC,
// CAUSE, SR stack push and vector arithmetic is computed in Rust
// (rust/src/cpu_exception.rs, Issue #530); this file applies the result to
// COP0/PC and keeps the exception snapshot and pipeline flags.

#include "psx_cpu.h"
#include "psx_cpu_exception.h"
#include <cstdint>

void PSXCpu::RaiseAddressError(uint32_t excode, uint32_t addr) {
    // BadVaddr (cop0r8) is updated only for AdEL/AdES (docs/cpu/cop0.md).
    cop0_[8] = addr;
    RaiseException(excode);
}

void PSXCpu::RaiseException(uint32_t excode, uint32_t ce) {
    // EPC/BD from the delay-slot state (docs/cpu/pipeline.md), CAUSE Excode/CE/BD
    // with every other bit preserved, the SR KU/IE stack push and the BEV
    // vector (docs/cpu/cop0.md).
    const PSXExceptionResolution r = psx_cpu_exception_resolve(
        executing_in_delay_slot_ ? 1u : 0u, delay_slot_pc_, executing_instr_addr_,
        cop0_[13], cop0_[12], excode, ce);

    cop0_[13] = r.cause;
    cop0_[14] = r.epc;
    cop0_[12] = r.sr;
    pc_ = r.vector;

    // Snapshot the exception resolution (Issue #481): the per-step fields the
    // handler computed above get reused by the next step, so a caller that asks
    // after Step() returns reads these stable values instead.
    last_exception_code_ = excode;
    last_exception_fault_pc_ = r.epc;
    last_exception_in_delay_slot_ = r.in_delay_slot != 0;

    exception_raised_ = true;
    branch_pending_ = false;
    branch_issued_ = false;
}
