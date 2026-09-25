// PSXCpu branch and jump handlers (BEQ/BNE/BLEZ/BGTZ/BLTZ/BGEZ/BLTZAL/
// BGEZAL, J/JAL/JR/JALR). The taken decision, target and PC+8 link value are
// computed in Rust (psx_cpu_control.h, Issue #526); these handlers keep the
// GPR reads, the link write and SetPendingBranch (branch delay, ADR-004/005).

#include "psx_cpu.h"
#include "psx_cpu_control.h"
#include <cstdint>

// Branch (branch delay slot per ADR-004/005)
void PSXCpu::ExecBeq(uint32_t rs, uint32_t rt, int16_t offset) {
    PSXControlResult r = psx_cpu_control_beq(pc_, gpr_[rs], gpr_[rt], offset);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBne(uint32_t rs, uint32_t rt, int16_t offset) {
    PSXControlResult r = psx_cpu_control_bne(pc_, gpr_[rs], gpr_[rt], offset);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBlez(uint32_t rs, int16_t offset) {
    PSXControlResult r = psx_cpu_control_blez(pc_, gpr_[rs], offset);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBgtz(uint32_t rs, int16_t offset) {
    PSXControlResult r = psx_cpu_control_bgtz(pc_, gpr_[rs], offset);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBltz(uint32_t rs, int16_t offset) {
    PSXControlResult r = psx_cpu_control_bltz(pc_, gpr_[rs], offset);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBgez(uint32_t rs, int16_t offset) {
    PSXControlResult r = psx_cpu_control_bgez(pc_, gpr_[rs], offset);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBltzal(uint32_t rs, int16_t offset) {
    // Return address is always linked; the decision uses rs before linking.
    PSXControlResult r = psx_cpu_control_bltz(pc_, gpr_[rs], offset);
    SetGPR(31, r.link);
    SetPendingBranch(r.target, r.taken != 0);
}

void PSXCpu::ExecBgezal(uint32_t rs, int16_t offset) {
    PSXControlResult r = psx_cpu_control_bgez(pc_, gpr_[rs], offset);
    SetGPR(31, r.link);
    SetPendingBranch(r.target, r.taken != 0);
}

// Jump
void PSXCpu::ExecJ(uint32_t target) {
    PSXControlResult r = psx_cpu_control_j(pc_, target);
    SetPendingBranch(r.target, true);
}

void PSXCpu::ExecJal(uint32_t target) {
    PSXControlResult r = psx_cpu_control_j(pc_, target);
    SetGPR(31, r.link);
    SetPendingBranch(r.target, true);
}

void PSXCpu::ExecJr(uint32_t rs) {
    PSXControlResult r = psx_cpu_control_jr(pc_, gpr_[rs]);
    SetPendingBranch(r.target, true);
}

void PSXCpu::ExecJalr(uint32_t rd, uint32_t rs) {
    // gpr_[rs] is read before linking, so jalr rd, rd uses the old value.
    PSXControlResult r = psx_cpu_control_jr(pc_, gpr_[rs]);
    SetGPR(rd, r.link);
    SetPendingBranch(r.target, true);
}
