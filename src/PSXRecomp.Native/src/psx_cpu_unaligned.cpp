// PSXCpu unaligned loads/stores (LWL/LWR/SWL/SWR). The aligned base and the
// little-endian byte merge are computed in Rust (rust/src/cpu_unaligned.rs,
// Issue #528); this file keeps address translation, memory I/O and the
// load-delay interaction.

#include "psx_cpu.h"
#include "psx_cpu_unaligned.h"
#include "psx_memory.h"
#include <cstdint>

// LWL/LWR merge into the value of a load to the same register that is still
// in its delay slot (the R3000A forwards it), so an LWR/LWL pair works
// without a NOP between them (docs/cpu/pipeline.md). Otherwise they merge
// into the committed register value.

// Load Word Left - unaligned load, high bytes of rt
void PSXCpu::ExecLwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(psx_cpu_unaligned_base(addr));
    uint32_t reg = (load_delay_reg_ == static_cast<int>(rt)) ? load_delay_value_ : gpr_[rt];
    uint32_t mem_val = IsMapped(phys) ? memory.Read32(phys) : 0;
    WriteRegDelayed(rt, psx_cpu_unaligned_lwl(addr, reg, mem_val));
}

// Load Word Right - unaligned load, low bytes of rt
void PSXCpu::ExecLwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(psx_cpu_unaligned_base(addr));
    uint32_t reg = (load_delay_reg_ == static_cast<int>(rt)) ? load_delay_value_ : gpr_[rt];
    uint32_t mem_val = IsMapped(phys) ? memory.Read32(phys) : 0;
    WriteRegDelayed(rt, psx_cpu_unaligned_lwr(addr, reg, mem_val));
}

// Store Word Left - unaligned store, high bytes of rt
void PSXCpu::ExecSwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(psx_cpu_unaligned_base(addr));
    if (!IsMapped(phys)) {
        return;
    }
    memory.Write32(phys, psx_cpu_unaligned_swl(addr, gpr_[rt], memory.Read32(phys)));
}

// Store Word Right - unaligned store, low bytes of rt
void PSXCpu::ExecSwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint32_t phys = TranslateAddress(psx_cpu_unaligned_base(addr));
    if (!IsMapped(phys)) {
        return;
    }
    memory.Write32(phys, psx_cpu_unaligned_swr(addr, gpr_[rt], memory.Read32(phys)));
}
