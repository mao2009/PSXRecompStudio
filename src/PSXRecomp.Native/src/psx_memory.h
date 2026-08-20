#pragma once

#include <cstdint>
#include <cstring>
#include "psx_cpu.h"

class PSXMemory {
public:
    PSXMemory();

    void Reset();

    uint8_t* GetRAM();
    uint32_t GetRAMSize() const;

    uint8_t bios[PSX_BIOS_SIZE];
    uint8_t hw_regs[PSX_HW_REG_SIZE];

private:
    uint8_t ram_[PSX_RAM_SIZE];
};

inline PSXMemory::PSXMemory() {
    Reset();
}

inline void PSXMemory::Reset() {
    std::memset(ram_, 0, PSX_RAM_SIZE);
    std::memset(bios, 0, PSX_BIOS_SIZE);
    std::memset(hw_regs, 0, PSX_HW_REG_SIZE);
}

inline uint8_t* PSXMemory::GetRAM() { return ram_; }
inline uint32_t PSXMemory::GetRAMSize() const { return PSX_RAM_SIZE; }
