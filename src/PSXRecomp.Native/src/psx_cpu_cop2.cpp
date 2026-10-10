// PSXCpu COP2 (GTE) instructions (Issue #447). The GTE register file and its
// command arithmetic belong to the attached GTE (the managed GteRegisterBank,
// see PSXCpu::AttachGte); this file owns only what the CPU does around it:
//
// - SR.CU2 (bit 30) gates every COP2/LWC2/SWC2: clear (or no GTE attached)
//   raises CpU with CAUSE.CE = 2 before anything else happens.
// - MFC2/CFC2 write the GPR through the load delay, exactly like LW.
// - MTC2/CTC2 read the GPR as any instruction does (a load still in its delay
//   slot is not visible) and write the GTE register at once.
// - LWC2/SWC2 address like LW/SW: a misaligned address raises AdEL/AdES with
//   BadVAddr, an unmapped load yields 0 and an unmapped store is dropped, and
//   a store is dropped while SR.IsC isolates the cache. LWC2 writes the GTE
//   register at once (it occupies no GPR load-delay slot).
// - A command the GTE does not implement fails closed: Step() returns
//   PSX_STEP_GTE_COMMAND_UNSUPPORTED (kPsxStepGteCommandUnsupported) and
//   nothing retires.

#include "psx_cpu.h"
#include "psx_cpu_memory_access.h"
#include "psx_memory.h"
#include <cstdint>

static constexpr uint32_t kSrCu2 = 1u << 30;
static constexpr uint32_t kGteControlBase = 32;

bool PSXCpu::Cop2Usable() {
    if ((cop0_[12] & kSrCu2) == 0 || gte_command_ == nullptr) {
        RaiseException(0x0B, 2); // CpU, CE = 2
        return false;
    }
    return true;
}

void PSXCpu::ExecMfc2(uint32_t rt, uint32_t rd) {
    if (!Cop2Usable()) return;
    WriteRegDelayed(rt, gte_read_(gte_context_, rd));
}

void PSXCpu::ExecCfc2(uint32_t rt, uint32_t rd) {
    if (!Cop2Usable()) return;
    WriteRegDelayed(rt, gte_read_(gte_context_, kGteControlBase + rd));
}

void PSXCpu::ExecMtc2(uint32_t rt, uint32_t rd) {
    if (!Cop2Usable()) return;
    gte_write_(gte_context_, rd, gpr_[rt]);
}

void PSXCpu::ExecCtc2(uint32_t rt, uint32_t rd) {
    if (!Cop2Usable()) return;
    gte_write_(gte_context_, kGteControlBase + rd, gpr_[rt]);
}

void PSXCpu::ExecCop2Command(uint32_t command) {
    if (!Cop2Usable()) return;
    if (gte_command_(gte_context_, command & 0x01FFFFFFu) != 0) {
        gte_command_unsupported_ = true;
    }
}

void PSXCpu::ExecLwc2(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    if (!Cop2Usable()) return;
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 4);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x04, a.vaddr); // AdEL
        return;
    }
    gte_write_(gte_context_, rt, a.status == PSX_MEM_ACCESS_UNMAPPED ? 0u : memory.Read32(a.phys));
}

void PSXCpu::ExecSwc2(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    if (!Cop2Usable()) return;
    PSXMemAccess a = psx_cpu_mem_classify(gpr_[rs], offset, 4);
    if (a.status == PSX_MEM_ACCESS_MISALIGNED) {
        RaiseAddressError(0x05, a.vaddr); // AdES
        return;
    }
    if (a.status == PSX_MEM_ACCESS_UNMAPPED || StoreIsCacheIsolated(a.vaddr)) {
        return;
    }
    memory.Write32(a.phys, gte_read_(gte_context_, rt));
}
