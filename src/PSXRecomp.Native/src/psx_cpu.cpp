#include "psx_cpu.h"

PSXCpu::PSXCpu() {
    Reset();
}

void PSXCpu::Reset() {
    for (int i = 0; i < PSX_GPR_COUNT; i++) {
        gpr_[i] = 0;
    }
    pc_ = 0;
    hi_ = 0;
    lo_ = 0;
}

uint32_t PSXCpu::GetGPR(int index) const {
    if (index < 0 || index >= PSX_GPR_COUNT) return 0;
    return gpr_[index];
}

void PSXCpu::SetGPR(int index, uint32_t value) {
    if (index < 0 || index >= PSX_GPR_COUNT) return;
    gpr_[index] = value;
}

uint32_t PSXCpu::GetPC() const { return pc_; }
void PSXCpu::SetPC(uint32_t value) { pc_ = value; }

uint32_t PSXCpu::GetHI() const { return hi_; }
void PSXCpu::SetHI(uint32_t value) { hi_ = value; }

uint32_t PSXCpu::GetLO() const { return lo_; }
void PSXCpu::SetLO(uint32_t value) { lo_ = value; }
