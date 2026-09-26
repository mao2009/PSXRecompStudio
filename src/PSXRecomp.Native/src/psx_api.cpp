#include "psx_core.h"
#include "psx_core.cpp"
#include "psx_core_test_hooks.h"
#include <new>

extern "C" {

void* PSXCoreGetCpuForTrace(PSXCore* core) {
    // See psx_core_test_hooks.h: internal harness accessor, not part of the
    // stable public ABI contract. Returns the PSXCpu* as an opaque void* so the
    // header stays valid C; golden_trace.h casts it back in C++.
    if (!core) return nullptr;
    return &core->cpu;
}

PSXCore* PSXCore_Create(void) {
    try {
        PSXCore* core = new PSXCore();
        // Route hardware-register reads/writes to the controller states this
        // core owns (Issue #386); pointers stay valid for the core's lifetime.
        core->memory.AttachControllers(&core->dma, &core->timers, &core->interrupts);
        return core;
    } catch (...) {
        return nullptr;
    }
}

void PSXCore_Destroy(PSXCore* core) {
    delete core;
}

void PSXCore_Reset(PSXCore* core) {
    if (!core) return;
    core->cpu.Reset();
    core->memory.Reset();
    core->dma = psx_dma_reset();
    core->timers = psx_timer_reset();
    core->interrupts = psx_interrupt_reset();
}

uint32_t PSXCore_GetGPR(PSXCore* core, int index) {
    if (!core) return 0;
    return core->cpu.GetGPR(index);
}

void PSXCore_SetGPR(PSXCore* core, int index, uint32_t value) {
    if (!core) return;
    core->cpu.SetGPR(index, value);
}

uint32_t PSXCore_GetPC(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.GetPC();
}

void PSXCore_SetPC(PSXCore* core, uint32_t value) {
    if (!core) return;
    core->cpu.SetPC(value);
}

uint32_t PSXCore_GetHI(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.GetHI();
}

void PSXCore_SetHI(PSXCore* core, uint32_t value) {
    if (!core) return;
    core->cpu.SetHI(value);
}

uint32_t PSXCore_GetLO(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.GetLO();
}

void PSXCore_SetLO(PSXCore* core, uint32_t value) {
    if (!core) return;
    core->cpu.SetLO(value);
}

uint32_t PSXCore_GetCop0(PSXCore* core, int index) {
    if (!core) return 0;
    return core->cpu.GetCop0(index);
}

void PSXCore_SetCop0(PSXCore* core, int index, uint32_t value) {
    if (!core) return;
    core->cpu.SetCop0(index, value);
}

uint8_t* PSXCore_GetRAM(PSXCore* core) {
    if (!core) return nullptr;
    return core->memory.GetRAM();
}

uint32_t PSXCore_GetRAMSize(void) {
    return PSX_RAM_SIZE;
}

void PSXCore_SetGpuMmioCallbacks(
    PSXCore* core,
    void* context,
    PSXGpuMmioRead32 read32,
    PSXGpuMmioWrite32 write32) {
    if (!core) return;
    core->memory.AttachGpuMmio(context, read32, write32);
}

uint32_t PSXCore_ReadDmaRegister(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return psx_dma_read_register(core->dma, address);
}

void PSXCore_WriteDmaRegister(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->dma = psx_dma_write_register(core->dma, address, value);
}

int PSXCore_GetDmaInterruptPending(PSXCore* core) {
    if (!core) return 0;
    return psx_dma_get_interrupt_pending(core->dma) != 0 ? 1 : 0;
}

void PSXCore_TickDma(PSXCore* core, uint32_t cycles) {
    if (!core) return;
    core->dma = psx_dma_tick(core->dma, cycles);
}

uint32_t PSXCore_ReadTimerRegister(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    PSXTimerReadResult result = psx_timer_read_register(core->timers, address);
    core->timers = result.state;
    return result.value;
}

void PSXCore_WriteTimerRegister(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->timers = psx_timer_write_register(core->timers, address, value);
}

void PSXCore_TickTimers(PSXCore* core, uint32_t cycles) {
    if (!core) return;
    core->timers = psx_timer_tick(core->timers, cycles);
}

int PSXCore_GetTimerInterruptPending(PSXCore* core, int timer) {
    if (!core) return 0;
    return psx_timer_get_interrupt_pending(core->timers, timer) != 0 ? 1 : 0;
}

void PSXCore_ClearTimerInterrupt(PSXCore* core, int timer) {
    if (!core) return;
    core->timers = psx_timer_clear_interrupt(core->timers, timer);
}

void PSXCore_SetTimerSync(PSXCore* core, int timer, int active) {
    if (!core) return;
    core->timers = psx_timer_set_sync_line(core->timers, timer, active != 0 ? 1 : 0);
}

void PSXCore_ResetTimers(PSXCore* core) {
    if (!core) return;
    core->timers = psx_timer_reset();
}

uint32_t PSXCore_ReadInterruptControllerRegister(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return psx_interrupt_read_register(core->interrupts, address);
}

void PSXCore_WriteInterruptControllerRegister(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->interrupts = psx_interrupt_write_register(core->interrupts, address, value);
}

int PSXCore_GetInterruptPending(PSXCore* core) {
    if (!core) return 0;
    return psx_interrupt_pending(core->interrupts) != 0 ? 1 : 0;
}

void PSXCore_RaiseInterrupt(PSXCore* core, int irq) {
    if (!core) return;
    core->interrupts = psx_interrupt_raise(core->interrupts, irq);
}

void PSXCore_ClearInterrupt(PSXCore* core, int irq) {
    if (!core) return;
    core->interrupts = psx_interrupt_clear(core->interrupts, irq);
}

void PSXCore_ResetInterruptController(PSXCore* core) {
    if (!core) return;
    core->interrupts = psx_interrupt_reset();
}

int PSXCore_GetSio0InterruptPending(PSXCore* core) {
    if (!core) return 0;
    return core->memory.GetSio0InterruptPending() ? 1 : 0;
}

void PSXCore_ClearSio0Interrupt(PSXCore* core) {
    if (!core) return;
    core->memory.ClearSio0Interrupt();
}

uint32_t PSXCore_GetSio0CommandStatus(PSXCore* core) {
    if (!core) return 0;
    return core->memory.GetSio0CommandStatus();
}

uint32_t PSXCore_GetSio0LastCommandByte(PSXCore* core) {
    if (!core) return 0;
    return core->memory.GetSio0LastCommandByte();
}

int PSXCore_Step(PSXCore* core) {
    if (!core) return -1;
    // Feed the Interrupt Controller's aggregate pending line into the CPU
    // before stepping (Issue #144); PSXCpu itself stays decoupled from
    // the Interrupt Controller.
    core->cpu.SetHardwareInterruptPending(psx_interrupt_pending(core->interrupts) != 0);
    return core->cpu.Step(core->memory);
}

int PSXCore_StepWithoutInterrupts(PSXCore* core) {
    if (!core) return -1;
    core->cpu.SetHardwareInterruptPending(false);
    return core->cpu.Step(core->memory);
}

int PSXCore_GetExceptionRaised(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.ExceptionRaised() ? 1 : 0;
}

uint32_t PSXCore_GetExceptionCode(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.GetLastExceptionCode();
}

uint32_t PSXCore_GetExceptionFaultPc(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.GetLastExceptionFaultPc();
}

int PSXCore_GetExceptionInDelaySlot(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.GetLastExceptionInDelaySlot() ? 1 : 0;
}

int PSXCore_GetRfeExecuted(PSXCore* core) {
    if (!core) return 0;
    return core->cpu.RfeExecuted() ? 1 : 0;
}

int PSXCore_Run(PSXCore* core, uint32_t maxInstructions) {
    if (!core) return -1;
    // Re-sample the Interrupt Controller before every instruction (not just once
    // for the whole batch): the Issue requires each Step() to see a current
    // pending state, and a multi-instruction Run() must behave identically to
    // that many individual PSXCore_Step() calls (Issue #144).
    for (uint32_t i = 0; i < maxInstructions; i++) {
        core->cpu.SetHardwareInterruptPending(psx_interrupt_pending(core->interrupts) != 0);
        int result = core->cpu.Step(core->memory);
        if (result != 0) {
            return result;
        }
    }
    return 0;
}

uint32_t PSXCore_ReadMemory32(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return core->memory.Read32(address);
}

void PSXCore_WriteMemory32(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->memory.Write32(address, value);
}

uint16_t PSXCore_ReadMemory16(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return core->memory.Read16(address);
}

void PSXCore_WriteMemory16(PSXCore* core, uint32_t address, uint16_t value) {
    if (!core) return;
    core->memory.Write16(address, value);
}

uint8_t PSXCore_ReadMemory8(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return core->memory.Read8(address);
}

void PSXCore_WriteMemory8(PSXCore* core, uint32_t address, uint8_t value) {
    if (!core) return;
    core->memory.Write8(address, value);
}

}
