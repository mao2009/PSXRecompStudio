#include "psx_core.h"
#include "psx_core.cpp"
#include "psx_core_test_hooks.h"
#include <new>

extern "C" {

PSXCpu* PSXCoreGetCpuForTrace(PSXCore* core) {
    // See psx_core_test_hooks.h: internal harness accessor, not part of the
    // stable public ABI contract.
    if (!core) return nullptr;
    return &core->cpu;
}

PSXCore* PSXCore_Create(void) {
    try {
        return new PSXCore();
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
    core->dma.Reset();
    core->timers.Reset();
    core->interrupts.Reset();
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

uint32_t PSXCore_ReadDmaRegister(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return core->dma.ReadRegister(address);
}

void PSXCore_WriteDmaRegister(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->dma.WriteRegister(address, value);
}

int PSXCore_GetDmaInterruptPending(PSXCore* core) {
    if (!core) return 0;
    return core->dma.GetInterruptPending() ? 1 : 0;
}

uint32_t PSXCore_ReadTimerRegister(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return core->timers.ReadRegister(address);
}

void PSXCore_WriteTimerRegister(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->timers.WriteRegister(address, value);
}

void PSXCore_TickTimers(PSXCore* core, uint32_t cycles) {
    if (!core) return;
    core->timers.Tick(cycles);
}

int PSXCore_GetTimerInterruptPending(PSXCore* core, int timer) {
    if (!core) return 0;
    return core->timers.GetInterruptPending(timer) ? 1 : 0;
}

void PSXCore_ClearTimerInterrupt(PSXCore* core, int timer) {
    if (!core) return;
    core->timers.ClearInterrupt(timer);
}

void PSXCore_SetTimerSync(PSXCore* core, int timer, int active) {
    if (!core) return;
    core->timers.SetSyncLine(timer, active != 0);
}

void PSXCore_ResetTimers(PSXCore* core) {
    if (!core) return;
    core->timers.Reset();
}

uint32_t PSXCore_ReadInterruptControllerRegister(PSXCore* core, uint32_t address) {
    if (!core) return 0;
    return core->interrupts.ReadRegister(address);
}

void PSXCore_WriteInterruptControllerRegister(PSXCore* core, uint32_t address, uint32_t value) {
    if (!core) return;
    core->interrupts.WriteRegister(address, value);
}

int PSXCore_GetInterruptPending(PSXCore* core) {
    if (!core) return 0;
    return core->interrupts.GetInterruptPending() ? 1 : 0;
}

void PSXCore_RaiseInterrupt(PSXCore* core, int irq) {
    if (!core) return;
    core->interrupts.Raise(irq);
}

void PSXCore_ClearInterrupt(PSXCore* core, int irq) {
    if (!core) return;
    core->interrupts.Clear(irq);
}

void PSXCore_ResetInterruptController(PSXCore* core) {
    if (!core) return;
    core->interrupts.Reset();
}

int PSXCore_Step(PSXCore* core) {
    if (!core) return -1;
    // Feed the Interrupt Controller's aggregate pending line into the CPU
    // before stepping (Issue #144); PSXCpu itself stays decoupled from
    // PSXInterruptController.
    core->cpu.SetHardwareInterruptPending(core->interrupts.GetInterruptPending());
    return core->cpu.Step(core->memory);
}

int PSXCore_Run(PSXCore* core, uint32_t maxInstructions) {
    if (!core) return -1;
    // Re-sample the Interrupt Controller before every instruction (not just once
    // for the whole batch): the Issue requires each Step() to see a current
    // pending state, and a multi-instruction Run() must behave identically to
    // that many individual PSXCore_Step() calls (Issue #144).
    for (uint32_t i = 0; i < maxInstructions; i++) {
        core->cpu.SetHardwareInterruptPending(core->interrupts.GetInterruptPending());
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
