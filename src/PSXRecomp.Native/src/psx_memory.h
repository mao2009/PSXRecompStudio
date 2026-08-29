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

static constexpr uint32_t PSX_BIOS_BASE = 0x1FC00000u;
static constexpr uint32_t PSX_HW_REG_BASE = 0x1F801000u;

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
    if (address < PSX_RAM_SIZE) {
        return (static_cast<uint32_t>(ram_[address]) << 24) |
               (static_cast<uint32_t>(ram_[address + 1]) << 16) |
               (static_cast<uint32_t>(ram_[address + 2]) << 8) |
               static_cast<uint32_t>(ram_[address + 3]);
    }
    if (address >= PSX_BIOS_BASE && address + 3u < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        uint32_t idx = address - PSX_BIOS_BASE;
        return (static_cast<uint32_t>(bios[idx]) << 24) |
               (static_cast<uint32_t>(bios[idx + 1]) << 16) |
               (static_cast<uint32_t>(bios[idx + 2]) << 8) |
               static_cast<uint32_t>(bios[idx + 3]);
    }
    if (address >= PSX_HW_REG_BASE && address + 3u < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        uint32_t idx = address - PSX_HW_REG_BASE;
        return (static_cast<uint32_t>(hw_regs[idx]) << 24) |
               (static_cast<uint32_t>(hw_regs[idx + 1]) << 16) |
               (static_cast<uint32_t>(hw_regs[idx + 2]) << 8) |
               static_cast<uint32_t>(hw_regs[idx + 3]);
    }
    return 0;
}

inline void PSXMemory::Write32(uint32_t address, uint32_t value) {
    if (address < PSX_RAM_SIZE) {
        ram_[address] = static_cast<uint8_t>(value >> 24);
        ram_[address + 1] = static_cast<uint8_t>(value >> 16);
        ram_[address + 2] = static_cast<uint8_t>(value >> 8);
        ram_[address + 3] = static_cast<uint8_t>(value & 0xFF);
        return;
    }
    if (address >= PSX_BIOS_BASE && address + 3u < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        uint32_t idx = address - PSX_BIOS_BASE;
        bios[idx] = static_cast<uint8_t>(value >> 24);
        bios[idx + 1] = static_cast<uint8_t>(value >> 16);
        bios[idx + 2] = static_cast<uint8_t>(value >> 8);
        bios[idx + 3] = static_cast<uint8_t>(value & 0xFF);
        return;
    }
    if (address >= PSX_HW_REG_BASE && address + 3u < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        uint32_t idx = address - PSX_HW_REG_BASE;
        hw_regs[idx] = static_cast<uint8_t>(value >> 24);
        hw_regs[idx + 1] = static_cast<uint8_t>(value >> 16);
        hw_regs[idx + 2] = static_cast<uint8_t>(value >> 8);
        hw_regs[idx + 3] = static_cast<uint8_t>(value & 0xFF);
        return;
    }
}

inline uint16_t PSXMemory::Read16(uint32_t address) const {
    if (address < PSX_RAM_SIZE) {
        return static_cast<uint16_t>((static_cast<uint16_t>(ram_[address]) << 8) | ram_[address + 1]);
    }
    if (address >= PSX_BIOS_BASE && address + 1u < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        uint32_t idx = address - PSX_BIOS_BASE;
        return static_cast<uint16_t>((static_cast<uint16_t>(bios[idx]) << 8) | bios[idx + 1]);
    }
    if (address >= PSX_HW_REG_BASE && address + 1u < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        uint32_t idx = address - PSX_HW_REG_BASE;
        return static_cast<uint16_t>((static_cast<uint16_t>(hw_regs[idx]) << 8) | hw_regs[idx + 1]);
    }
    return 0;
}

inline void PSXMemory::Write16(uint32_t address, uint16_t value) {
    if (address < PSX_RAM_SIZE) {
        ram_[address] = static_cast<uint8_t>(value >> 8);
        ram_[address + 1] = static_cast<uint8_t>(value & 0xFF);
        return;
    }
    if (address >= PSX_BIOS_BASE && address + 1u < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        uint32_t idx = address - PSX_BIOS_BASE;
        bios[idx] = static_cast<uint8_t>(value >> 8);
        bios[idx + 1] = static_cast<uint8_t>(value & 0xFF);
        return;
    }
    if (address >= PSX_HW_REG_BASE && address + 1u < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        uint32_t idx = address - PSX_HW_REG_BASE;
        hw_regs[idx] = static_cast<uint8_t>(value >> 8);
        hw_regs[idx + 1] = static_cast<uint8_t>(value & 0xFF);
        return;
    }
}

inline uint8_t PSXMemory::Read8(uint32_t address) const {
    if (address < PSX_RAM_SIZE) {
        return ram_[address];
    }
    if (address >= PSX_BIOS_BASE && address < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        return bios[address - PSX_BIOS_BASE];
    }
    if (address >= PSX_HW_REG_BASE && address < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        return hw_regs[address - PSX_HW_REG_BASE];
    }
    return 0;
}

inline void PSXMemory::Write8(uint32_t address, uint8_t value) {
    if (address < PSX_RAM_SIZE) {
        ram_[address] = value;
        return;
    }
    if (address >= PSX_BIOS_BASE && address < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        bios[address - PSX_BIOS_BASE] = value;
        return;
    }
    if (address >= PSX_HW_REG_BASE && address < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        hw_regs[address - PSX_HW_REG_BASE] = value;
        return;
    }
}
