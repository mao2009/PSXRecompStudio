#include "psx_core.h"
#include "psx_core.cpp"

extern "C" {

PSXCore* PSXCore_Create(void) {
    return new PSXCore();
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

}
