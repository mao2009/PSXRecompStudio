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

    // Memory read/write helpers for CPU
    uint32_t Read32(uint32_t address) const;
    void Write32(uint32_t address, uint32_t value);
    uint16_t Read16(uint32_t address) const;
    void Write16(uint32_t address, uint16_t value);
    uint8_t Read8(uint32_t address) const;
    void Write8(uint32_t address, uint8_t value);

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

inline uint32_t PSXMemory::Read32(uint32_t address) const {
    const uint8_t* ram = ram_;
    return (ram[address] << 24) | (ram[address + 1] << 16) | (ram[address + 2] << 8) | ram[address + 3];
}

inline void PSXMemory::Write32(uint32_t address, uint32_t value) {
    ram_[address] = static_cast<uint8_t>(value >> 24);
    ram_[address + 1] = static_cast<uint8_t>(value >> 16);
    ram_[address + 2] = static_cast<uint8_t>(value >> 8);
    ram_[address + 3] = static_cast<uint8_t>(value & 0xFF);
}

inline uint16_t PSXMemory::Read16(uint32_t address) const {
    const uint8_t* ram = ram_;
    return (ram[address] << 8) | ram[address + 1];
}

inline void PSXMemory::Write16(uint32_t address, uint16_t value) {
    ram_[address] = static_cast<uint8_t>(value >> 8);
    ram_[address + 1] = static_cast<uint8_t>(value & 0xFF);
}

inline uint8_t PSXMemory::Read8(uint32_t address) const {
    return ram_[address];
}

inline void PSXMemory::Write8(uint32_t address, uint8_t value) {
    ram_[address] = value;
}
