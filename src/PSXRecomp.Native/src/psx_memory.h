#pragma once

#include <cstdint>
#include <cstring>
#include "psx_cpu.h"
#include "psx_dma.h"
#include "psx_timer.h"
#include "psx_interrupt.h"

static constexpr uint32_t PSX_BIOS_BASE = 0x1FC00000u;
static constexpr uint32_t PSX_HW_REG_BASE = 0x1F801000u;

// The PS1 main RAM aliases across the low 8 MiB of the physical address space:
// address & (RAM_SIZE - 1) selects the physical byte (Issue #386).
static constexpr uint32_t PSX_RAM_MIRROR_END = 0x00800000u;

static constexpr uint32_t PSX_SCRATCHPAD_BASE = 0x1F800000u;
static constexpr uint32_t PSX_SCRATCHPAD_SIZE = 0x000400u;

// MMIO registers routed to the Rust controllers (Issue #386). Ranges match the
// managed MemoryBus/MmioRoute so both translation paths classify identically.
static constexpr uint32_t PSX_INTR_I_STAT = 0x1F801070u;
static constexpr uint32_t PSX_INTR_I_MASK = 0x1F801074u;
static constexpr uint32_t PSX_DMA_BASE = 0x1F801080u;
static constexpr uint32_t PSX_DMA_REGION_END = 0x1F8010F4u; // DICR
static constexpr uint32_t PSX_TIMER_BASE = 0x1F801100u;
static constexpr uint32_t PSX_TIMER_REGION_END = PSX_TIMER_BASE + 3u * 0x10u - 1u; // 0x1F80112F

class PSXMemory {
public:
    PSXMemory();

    void Reset();

    uint8_t* GetRAM();
    uint32_t GetRAMSize() const;

    // Wires the PSXCore-owned controller states so MMIO reads/writes in the
    // hardware-register window route to them instead of the flat hw_regs
    // array. Pointers stay valid for the lifetime of the PSXCore that owns
    // both the memory and the states (PSXCore_Reset reassigns the states in
    // place; it never moves them). Unattached controllers fall back to the
    // flat hw_regs window, preserving the pre-attach behavior.
    void AttachControllers(PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);

    // Memory read/write helpers for CPU
    uint32_t Read32(uint32_t address);
    void Write32(uint32_t address, uint32_t value);
    uint16_t Read16(uint32_t address);
    void Write16(uint32_t address, uint16_t value);
    uint8_t Read8(uint32_t address);
    void Write8(uint32_t address, uint8_t value);

    uint8_t bios[PSX_BIOS_SIZE];
    uint8_t hw_regs[PSX_HW_REG_SIZE];

private:
    uint8_t ram_[PSX_RAM_SIZE];
    uint8_t scratchpad_[PSX_SCRATCHPAD_SIZE];
    PSXDmaState* dma_ = nullptr;
    PSXTimerState* timers_ = nullptr;
    PSXInterruptState* interrupts_ = nullptr;

    bool ReadController32(uint32_t address, uint32_t& value);
    bool WriteController32(uint32_t address, uint32_t value);

    static uint32_t Load32LE(const uint8_t* p);
    static void Store32LE(uint8_t* p, uint32_t value);
};

inline PSXMemory::PSXMemory() {
    Reset();
}

inline void PSXMemory::Reset() {
    std::memset(ram_, 0, PSX_RAM_SIZE);
    std::memset(scratchpad_, 0, PSX_SCRATCHPAD_SIZE);
    std::memset(bios, 0, PSX_BIOS_SIZE);
    std::memset(hw_regs, 0, PSX_HW_REG_SIZE);
}

inline uint8_t* PSXMemory::GetRAM() { return ram_; }
inline uint32_t PSXMemory::GetRAMSize() const { return PSX_RAM_SIZE; }

inline void PSXMemory::AttachControllers(PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts) {
    dma_ = dma;
    timers_ = timers;
    interrupts_ = interrupts;
}

inline uint32_t PSXMemory::Load32LE(const uint8_t* p) {
    return static_cast<uint32_t>(p[0]) |
           (static_cast<uint32_t>(p[1]) << 8) |
           (static_cast<uint32_t>(p[2]) << 16) |
           (static_cast<uint32_t>(p[3]) << 24);
}

inline void PSXMemory::Store32LE(uint8_t* p, uint32_t value) {
    p[0] = static_cast<uint8_t>(value & 0xFF);
    p[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
    p[2] = static_cast<uint8_t>((value >> 16) & 0xFF);
    p[3] = static_cast<uint8_t>((value >> 24) & 0xFF);
}

inline bool PSXMemory::ReadController32(uint32_t address, uint32_t& value) {
    if (address == PSX_INTR_I_STAT || address == PSX_INTR_I_MASK) {
        if (!interrupts_) return false;
        value = psx_interrupt_read_register(*interrupts_, address);
        return true;
    }
    if (address >= PSX_DMA_BASE && address <= PSX_DMA_REGION_END) {
        if (!dma_) return false;
        value = psx_dma_read_register(*dma_, address);
        return true;
    }
    if (address >= PSX_TIMER_BASE && address <= PSX_TIMER_REGION_END) {
        if (!timers_) return false;
        // Timer count reads can toggle the counter's sync line, so the read
        // returns a new state that must be written back in place.
        PSXTimerReadResult result = psx_timer_read_register(*timers_, address);
        *timers_ = result.state;
        value = result.value;
        return true;
    }
    return false;
}

inline bool PSXMemory::WriteController32(uint32_t address, uint32_t value) {
    if (address == PSX_INTR_I_STAT || address == PSX_INTR_I_MASK) {
        if (!interrupts_) return false;
        *interrupts_ = psx_interrupt_write_register(*interrupts_, address, value);
        return true;
    }
    if (address >= PSX_DMA_BASE && address <= PSX_DMA_REGION_END) {
        if (!dma_) return false;
        *dma_ = psx_dma_write_register(*dma_, address, value);
        return true;
    }
    if (address >= PSX_TIMER_BASE && address <= PSX_TIMER_REGION_END) {
        if (!timers_) return false;
        *timers_ = psx_timer_write_register(*timers_, address, value);
        return true;
    }
    return false;
}

inline uint32_t PSXMemory::Read32(uint32_t address) {
    if (address < PSX_RAM_MIRROR_END) {
        uint32_t idx = address & (PSX_RAM_SIZE - 1u);
        if (idx > PSX_RAM_SIZE - 4u) return 0;
        return Load32LE(ram_ + idx);
    }
    if (address >= PSX_SCRATCHPAD_BASE && address <= PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE - 4u) {
        uint32_t idx = address - PSX_SCRATCHPAD_BASE;
        return Load32LE(scratchpad_ + idx);
    }
    if (address >= PSX_BIOS_BASE && address <= PSX_BIOS_BASE + PSX_BIOS_SIZE - 4u) {
        return Load32LE(bios + (address - PSX_BIOS_BASE));
    }
    if (address >= PSX_HW_REG_BASE && address <= PSX_HW_REG_BASE + PSX_HW_REG_SIZE - 4u) {
        uint32_t value;
        if (ReadController32(address, value)) return value;
        return Load32LE(hw_regs + (address - PSX_HW_REG_BASE));
    }
    return 0;
}

inline void PSXMemory::Write32(uint32_t address, uint32_t value) {
    if (address < PSX_RAM_MIRROR_END) {
        uint32_t idx = address & (PSX_RAM_SIZE - 1u);
        if (idx > PSX_RAM_SIZE - 4u) return;
        Store32LE(ram_ + idx, value);
        return;
    }
    if (address >= PSX_SCRATCHPAD_BASE && address <= PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE - 4u) {
        uint32_t idx = address - PSX_SCRATCHPAD_BASE;
        Store32LE(scratchpad_ + idx, value);
        return;
    }
    if (address >= PSX_BIOS_BASE && address <= PSX_BIOS_BASE + PSX_BIOS_SIZE - 4u) {
        Store32LE(bios + (address - PSX_BIOS_BASE), value);
        return;
    }
    if (address >= PSX_HW_REG_BASE && address <= PSX_HW_REG_BASE + PSX_HW_REG_SIZE - 4u) {
        if (WriteController32(address, value)) return;
        Store32LE(hw_regs + (address - PSX_HW_REG_BASE), value);
        return;
    }
}

inline uint16_t PSXMemory::Read16(uint32_t address) {
    if (address < PSX_RAM_MIRROR_END) {
        uint32_t idx = address & (PSX_RAM_SIZE - 1u);
        if (idx > PSX_RAM_SIZE - 2u) return 0;
        return static_cast<uint16_t>(ram_[idx]) |
               static_cast<uint16_t>(static_cast<uint16_t>(ram_[idx + 1]) << 8);
    }
    if (address >= PSX_SCRATCHPAD_BASE && address <= PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE - 2u) {
        uint32_t idx = address - PSX_SCRATCHPAD_BASE;
        return static_cast<uint16_t>(scratchpad_[idx]) |
               static_cast<uint16_t>(static_cast<uint16_t>(scratchpad_[idx + 1]) << 8);
    }
    if (address >= PSX_BIOS_BASE && address <= PSX_BIOS_BASE + PSX_BIOS_SIZE - 2u) {
        uint32_t idx = address - PSX_BIOS_BASE;
        return static_cast<uint16_t>(bios[idx]) |
               static_cast<uint16_t>(static_cast<uint16_t>(bios[idx + 1]) << 8);
    }
    if (address >= PSX_HW_REG_BASE && address <= PSX_HW_REG_BASE + PSX_HW_REG_SIZE - 2u) {
        uint32_t idx = address - PSX_HW_REG_BASE;
        // A 32-bit controller read covers any halfword inside the register.
        uint32_t value;
        if (ReadController32(address & ~2u, value)) {
            return static_cast<uint16_t>((value >> (8 * (address & 2))) & 0xFFFF);
        }
        return static_cast<uint16_t>(hw_regs[idx]) |
               static_cast<uint16_t>(static_cast<uint16_t>(hw_regs[idx + 1]) << 8);
    }
    return 0;
}

inline void PSXMemory::Write16(uint32_t address, uint16_t value) {
    if (address < PSX_RAM_MIRROR_END) {
        uint32_t idx = address & (PSX_RAM_SIZE - 1u);
        if (idx > PSX_RAM_SIZE - 2u) return;
        ram_[idx] = static_cast<uint8_t>(value & 0xFF);
        ram_[idx + 1] = static_cast<uint8_t>(value >> 8);
        return;
    }
    if (address >= PSX_SCRATCHPAD_BASE && address <= PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE - 2u) {
        uint32_t idx = address - PSX_SCRATCHPAD_BASE;
        scratchpad_[idx] = static_cast<uint8_t>(value & 0xFF);
        scratchpad_[idx + 1] = static_cast<uint8_t>(value >> 8);
        return;
    }
    if (address >= PSX_BIOS_BASE && address <= PSX_BIOS_BASE + PSX_BIOS_SIZE - 2u) {
        uint32_t idx = address - PSX_BIOS_BASE;
        bios[idx] = static_cast<uint8_t>(value & 0xFF);
        bios[idx + 1] = static_cast<uint8_t>(value >> 8);
        return;
    }
    if (address >= PSX_HW_REG_BASE && address <= PSX_HW_REG_BASE + PSX_HW_REG_SIZE - 2u) {
        uint32_t wordAddr = address & ~2u;
        uint32_t value32;
        if (ReadController32(wordAddr, value32)) {
            // DICR bits 0-6 are write-1-to-clear interrupt flags: the read-back
            // echoes any currently-set flag back as a 1, which a full-word write
            // would then clear. Zero them here so a sub-word write to another
            // DICR field leaves flags untouched unless the caller's own byte/
            // halfword targets them.
            if (wordAddr == PSX_DMA_REGION_END) value32 &= ~0x7Fu;
            uint32_t shift = 8u * (address & 2u);
            WriteController32(wordAddr, (value32 & ~(0xFFFFu << shift)) | (static_cast<uint32_t>(value) << shift));
            return;
        }
        uint32_t idx = address - PSX_HW_REG_BASE;
        hw_regs[idx] = static_cast<uint8_t>(value & 0xFF);
        hw_regs[idx + 1] = static_cast<uint8_t>(value >> 8);
        return;
    }
}

inline uint8_t PSXMemory::Read8(uint32_t address) {
    if (address < PSX_RAM_MIRROR_END) {
        return ram_[address & (PSX_RAM_SIZE - 1u)];
    }
    if (address >= PSX_SCRATCHPAD_BASE && address < PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE) {
        return scratchpad_[address - PSX_SCRATCHPAD_BASE];
    }
    if (address >= PSX_BIOS_BASE && address < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        return bios[address - PSX_BIOS_BASE];
    }
    if (address >= PSX_HW_REG_BASE && address < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        // A 32-bit controller read covers any byte inside the register.
        uint32_t value;
        if (ReadController32(address & ~3u, value)) {
            return static_cast<uint8_t>((value >> (8 * (address & 3))) & 0xFF);
        }
        return hw_regs[address - PSX_HW_REG_BASE];
    }
    return 0;
}

inline void PSXMemory::Write8(uint32_t address, uint8_t value) {
    if (address < PSX_RAM_MIRROR_END) {
        ram_[address & (PSX_RAM_SIZE - 1u)] = value;
        return;
    }
    if (address >= PSX_SCRATCHPAD_BASE && address < PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE) {
        scratchpad_[address - PSX_SCRATCHPAD_BASE] = value;
        return;
    }
    if (address >= PSX_BIOS_BASE && address < PSX_BIOS_BASE + PSX_BIOS_SIZE) {
        bios[address - PSX_BIOS_BASE] = value;
        return;
    }
    if (address >= PSX_HW_REG_BASE && address < PSX_HW_REG_BASE + PSX_HW_REG_SIZE) {
        uint32_t wordAddr = address & ~3u;
        uint32_t value32;
        if (ReadController32(wordAddr, value32)) {
            // See the matching comment in Write16: preserve DICR's W1C flag bits.
            if (wordAddr == PSX_DMA_REGION_END) value32 &= ~0x7Fu;
            uint32_t shift = 8u * (address & 3u);
            WriteController32(wordAddr, (value32 & ~(0xFFu << shift)) | (static_cast<uint32_t>(value) << shift));
            return;
        }
        hw_regs[address - PSX_HW_REG_BASE] = value;
        return;
    }
}