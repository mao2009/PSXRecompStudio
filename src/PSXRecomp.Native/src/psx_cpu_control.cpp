// PSXCpu branch and jump handlers (BEQ/BNE/BLEZ/BGTZ/BLTZ/BGEZ/BLTZAL/
// BGEZAL, J/JAL/JR/JALR). Moved verbatim out of psx_cpu.cpp (Issue #524);
// owned by the branch/jump Rust migration slice (#526).

#include "psx_cpu.h"
#include <cstdint>

// Branch (branch delay slot per ADR-004/005)
void PSXCpu::ExecBeq(uint32_t rs, uint32_t rt, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, gpr_[rs] == gpr_[rt]);
}

void PSXCpu::ExecBne(uint32_t rs, uint32_t rt, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, gpr_[rs] != gpr_[rt]);
}

void PSXCpu::ExecBlez(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) <= 0);
}

void PSXCpu::ExecBgtz(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) > 0);
}

void PSXCpu::ExecBltz(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) < 0);
}

void PSXCpu::ExecBgez(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) >= 0);
}

void PSXCpu::ExecBltzal(uint32_t rs, int16_t offset) {
    // Return address is always linked, using the value of rs before linking.
    bool taken = ToSigned(gpr_[rs]) < 0;
    SetGPR(31, pc_ + 8);
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, taken);
}

void PSXCpu::ExecBgezal(uint32_t rs, int16_t offset) {
    bool taken = ToSigned(gpr_[rs]) >= 0;
    SetGPR(31, pc_ + 8);
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, taken);
}

// Jump
void PSXCpu::ExecJ(uint32_t target) {
    uint32_t addr = (pc_ + 4) & 0xF0000000;
    SetPendingBranch(addr | (target << 2), true);
}

void PSXCpu::ExecJal(uint32_t target) {
    SetGPR(31, pc_ + 8);
    uint32_t addr = (pc_ + 4) & 0xF0000000;
    SetPendingBranch(addr | (target << 2), true);
}

void PSXCpu::ExecJr(uint32_t rs) {
    SetPendingBranch(gpr_[rs], true);
}

void PSXCpu::ExecJalr(uint32_t rd, uint32_t rs) {
    // Capture the target before linking so that jalr rd, rd uses the old value.
    uint32_t target = gpr_[rs];
    SetGPR(rd, pc_ + 8);
    SetPendingBranch(target, true);
}
