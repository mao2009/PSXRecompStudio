// PSXCpu address translation and aligned loads/stores (LB/LBU/LH/LHU/LW/
// SB/SH/SW). Moved verbatim out of psx_cpu.cpp (Issue #524); owned by the
// aligned load/store Rust migration slice (#527).

#include "psx_cpu.h"
#include "psx_memory.h"
#include <cstdint>

// Sentinel physical address used to represent an unmapped virtual address.
static constexpr uint32_t kUnmappedPhysical = 0xFFFFFFFFu;

uint32_t PSXCpu::TranslateAddress(uint32_t virt) const {
    // KUSEG (0x00000000-0x7FFFFFFF): treat as a direct physical address.
    if (virt <= 0x7FFFFFFF) {
        return virt;
    }
    // KSEG0 (0x80000000-0x9FFFFFFF) and KSEG1 (0xA0000000-0xBFFFFFFF):
    // physical = address & 0x1FFFFFFF.
    if (virt <= 0xBFFFFFFF) {
        return virt & 0x1FFFFFFF;
    }
    // KSEG2 and reserved upper ranges are unmapped in this model.
    return kUnmappedPhysical;
}

bool PSXCpu::IsMapped(uint32_t phys) const {
    return phys != kUnmappedPhysical;
}

// Memory
void PSXCpu::ExecLb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        WriteRegDelayed(rt, 0);
        return;
    }
    int8_t value = static_cast<int8_t>(memory.Read8(phys));
    WriteRegDelayed(rt, SignExtend16(value));
}

void PSXCpu::ExecLbu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, memory.Read8(phys));
}

void PSXCpu::ExecLh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    if ((addr & 1u) != 0) {
        RaiseAddressError(0x04, addr); // AdEL
        return;
    }
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        WriteRegDelayed(rt, 0);
        return;
    }
    int16_t value = static_cast<int16_t>(memory.Read16(phys));
    WriteRegDelayed(rt, SignExtend16(value));
}

void PSXCpu::ExecLhu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    if ((addr & 1u) != 0) {
        RaiseAddressError(0x04, addr); // AdEL
        return;
    }
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, memory.Read16(phys));
}

void PSXCpu::ExecLw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    if ((addr & 3u) != 0) {
        RaiseAddressError(0x04, addr); // AdEL
        return;
    }
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, memory.Read32(phys));
}

void PSXCpu::ExecSb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        return;
    }
    memory.Write8(phys, static_cast<uint8_t>(gpr_[rt] & 0xFF));
}

void PSXCpu::ExecSh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    if ((addr & 1u) != 0) {
        RaiseAddressError(0x05, addr); // AdES
        return;
    }
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        return;
    }
    memory.Write16(phys, static_cast<uint16_t>(gpr_[rt] & 0xFFFF));
}

void PSXCpu::ExecSw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    if ((addr & 3u) != 0) {
        RaiseAddressError(0x05, addr); // AdES
        return;
    }
    uint32_t phys = TranslateAddress(addr);
    if (!IsMapped(phys)) {
        return;
    }
    memory.Write32(phys, gpr_[rt]);
}
