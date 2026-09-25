// PSXCpu address translation and aligned loads/stores (LB/LBU/LH/LHU/LW/
// SB/SH/SW). Effective address, alignment, translation and load extension
// are computed in Rust (rust/src/cpu_memory_access.rs, Issue #527); this file
// keeps GPR reads, PSXMemory access, the load delay, and AdEL/AdES raising.

#include "psx_cpu.h"
#include "psx_cpu_memory_access.h"
#include "psx_memory.h"
#include <cstdint>

uint32_t PSXCpu::TranslateAddress(uint32_t virt) const {
    return psx_cpu_mem_translate(virt);
}

bool PSXCpu::IsMapped(uint32_t phys) const {
    return psx_cpu_mem_is_mapped(phys) != 0;
}

// Memory
void PSXCpu::ExecLb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 1);
    if (a.status != PSX_MEM_ACCESS_OK) {
        WriteRegDelayed(rt, 0); // unmapped (a byte is never misaligned)
        return;
    }
    WriteRegDelayed(rt, psx_cpu_mem_extend_load(memory.Read8(a.phys), 1, 1));
}

void PSXCpu::ExecLbu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 1);
    if (a.status != PSX_MEM_ACCESS_OK) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, psx_cpu_mem_extend_load(memory.Read8(a.phys), 1, 0));
}

void PSXCpu::ExecLh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 2);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x04, a.vaddr); // AdEL
        return;
    }
    if (a.status == PSX_MEM_ACCESS_UNMAPPED) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, psx_cpu_mem_extend_load(memory.Read16(a.phys), 2, 1));
}

void PSXCpu::ExecLhu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 2);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x04, a.vaddr); // AdEL
        return;
    }
    if (a.status == PSX_MEM_ACCESS_UNMAPPED) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, psx_cpu_mem_extend_load(memory.Read16(a.phys), 2, 0));
}

void PSXCpu::ExecLw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 4);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x04, a.vaddr); // AdEL
        return;
    }
    if (a.status == PSX_MEM_ACCESS_UNMAPPED) {
        WriteRegDelayed(rt, 0);
        return;
    }
    WriteRegDelayed(rt, memory.Read32(a.phys));
}

void PSXCpu::ExecSb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 1);
    if (a.status != PSX_MEM_ACCESS_OK) {
        return; // unmapped store is dropped
    }
    memory.Write8(a.phys, static_cast<uint8_t>(gpr_[rt] & 0xFF));
}

void PSXCpu::ExecSh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 2);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x05, a.vaddr); // AdES
        return;
    }
    if (a.status == PSX_MEM_ACCESS_UNMAPPED) {
        return;
    }
    memory.Write16(a.phys, static_cast<uint16_t>(gpr_[rt] & 0xFFFF));
}

void PSXCpu::ExecSw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 4);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x05, a.vaddr); // AdES
        return;
    }
    if (a.status == PSX_MEM_ACCESS_UNMAPPED) {
        return;
    }
    memory.Write32(a.phys, gpr_[rt]);
}
