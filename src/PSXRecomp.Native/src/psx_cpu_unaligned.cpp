// PSXCpu unaligned loads/stores (LWL/LWR/SWL/SWR). Moved verbatim out of
// psx_cpu.cpp (Issue #524); owned by the LWL/LWR/SWL/SWR Rust migration
// slice (#528).

#include "psx_cpu.h"
#include "psx_memory.h"
#include <cstdint>

// Load Word Left - unaligned load, left bytes
void PSXCpu::ExecLwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t base = addr - (addr & 3);
    uint32_t phys = TranslateAddress(base);
    uint32_t reg = gpr_[rt];
    uint32_t shift = (addr & 3) * 8;
    uint32_t mem_val = IsMapped(phys) ? memory.Read32(phys) : 0;
    uint32_t mask = 0xFFFFFFFF << shift;
    WriteRegDelayed(rt, (mem_val << shift) | (reg & ~mask));
}

// Load Word Right - unaligned load, right bytes
void PSXCpu::ExecLwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t base = addr - (addr & 3);
    uint32_t phys = TranslateAddress(base);
    uint32_t reg = gpr_[rt];
    uint32_t shift = (3 - (addr & 3)) * 8;
    uint32_t mem_val = IsMapped(phys) ? memory.Read32(phys) : 0;
    uint32_t mask = 0xFFFFFFFF >> shift;
    WriteRegDelayed(rt, (reg & ~mask) | (mem_val >> shift));
}

// Store Word Left - unaligned store, left bytes
void PSXCpu::ExecSwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t base = addr - (addr & 3);
    uint32_t phys = TranslateAddress(base);
    if (!IsMapped(phys)) {
        return;
    }
    uint32_t reg = gpr_[rt];
    uint32_t shift = (addr & 3) * 8;
    uint32_t mem_val = memory.Read32(phys);
    uint32_t mask = 0xFFFFFFFF >> shift;
    uint32_t result = (reg >> shift) | (mem_val & ~mask);
    memory.Write32(phys, result);
}

// Store Word Right - unaligned store, right bytes
void PSXCpu::ExecSwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t base = addr - (addr & 3);
    uint32_t phys = TranslateAddress(base);
    if (!IsMapped(phys)) {
        return;
    }
    uint32_t reg = gpr_[rt];
    uint32_t shift = (3 - (addr & 3)) * 8;
    uint32_t mem_val = memory.Read32(phys);
    uint32_t mask = 0xFFFFFFFF << shift;
    uint32_t result = (mem_val & ~mask) | (reg << shift);
    memory.Write32(phys, result);
}
