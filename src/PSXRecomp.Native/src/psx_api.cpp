#include "psx_core.h"
#include "psx_core.cpp"
#include <new>

extern "C" {

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

uint8_t* PSXCore_GetRAM(PSXCore* core) {
    if (!core) return nullptr;
    return core->memory.GetRAM();
}

uint32_t PSXCore_GetRAMSize(void) {
    return PSX_RAM_SIZE;
}

int PSXCore_Step(PSXCore* core) {
    if (!core) return -1;
    return core->cpu.Step(core->memory);
}

int PSXCore_Run(PSXCore* core, uint32_t maxInstructions) {
    if (!core) return -1;
    return core->cpu.Run(core->memory, maxInstructions);
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
