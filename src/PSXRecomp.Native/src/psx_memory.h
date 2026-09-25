#pragma once

#include <cstdint>
#include <new>
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

/*
 * Guest RAM/scratchpad/BIOS/HW-register storage and access semantics.
 * Implemented in Rust (`../rust/src/memory.rs`, Issue #492); this header
 * declares that crate's internal C ABI for use by PSXMemory below.
 *
 * The backing storage (~2.6 MiB) is too large to pass by value the way the
 * DMA/Timer/Interrupt PODs are, so it is owned by an opaque handle following
 * the PSXCore_Create/PSXCore_Destroy pattern (rust-ffi-contract.md §3):
 * PSXMemory creates one handle in its constructor and releases it exactly
 * once in its destructor. The handle's fields are not part of the ABI.
 *
 * DMA/Timer/Interrupt register semantics are not reimplemented here or in
 * memory.rs: the Read/Write functions below take the same three
 * independently-nullable controller-state pointers PSXMemory::AttachControllers
 * has always taken, and memory.rs calls straight into the existing
 * psx_dma_, psx_timer_, and psx_interrupt_ functions to service the
 * HW-register window's MMIO ranges. An unattached (null) controller pointer
 * falls back to the flat HW-register store, matching the pre-Rust-memory
 * behavior exactly.
 */
struct PsxMemoryHandle;

extern "C" {
PsxMemoryHandle* psx_memory_create(void);
void psx_memory_destroy(PsxMemoryHandle* mem);
void psx_memory_reset(PsxMemoryHandle* mem);
uint8_t* psx_memory_ram_ptr(PsxMemoryHandle* mem);

uint32_t psx_memory_read32(PsxMemoryHandle* mem, uint32_t address,
                            PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);
void psx_memory_write32(PsxMemoryHandle* mem, uint32_t address, uint32_t value,
                         PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);
uint16_t psx_memory_read16(PsxMemoryHandle* mem, uint32_t address,
                            PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);
void psx_memory_write16(PsxMemoryHandle* mem, uint32_t address, uint16_t value,
                         PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);
uint8_t psx_memory_read8(PsxMemoryHandle* mem, uint32_t address,
                          PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);
void psx_memory_write8(PsxMemoryHandle* mem, uint32_t address, uint8_t value,
                        PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts);

// SIO0's minimal controller serial protocol (Issue #543). Unlike
// DMA/Timer/Interrupt, this state is owned inline by PsxMemoryHandle (see
// sio0.rs's module documentation), so there is no separate PSXSio0State to
// pass in here: these two functions poll/clear the "byte received" (IRQ7)
// latch straight off the handle, the same polling boundary
// psx_dma_get_interrupt_pending/psx_timer_get_interrupt_pending use.
uint8_t psx_memory_get_sio0_interrupt_pending(const PsxMemoryHandle* mem);
void psx_memory_clear_sio0_interrupt(PsxMemoryHandle* mem);
}

class PSXMemory {
public:
    PSXMemory();
    ~PSXMemory();

    // The Rust-owned handle has single-ownership, non-copyable semantics;
    // PSXMemory is only ever heap-allocated directly (native tests) or
    // embedded by value in the never-copied, never-moved PSXCore, so no
    // caller needs copy or move.
    PSXMemory(const PSXMemory&) = delete;
    PSXMemory& operator=(const PSXMemory&) = delete;
    PSXMemory(PSXMemory&&) = delete;
    PSXMemory& operator=(PSXMemory&&) = delete;

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

    // SIO0 controller serial protocol (Issue #543): whether a "byte
    // received" (IRQ7) is pending, and clearing it. Polled the same way
    // Timer/DMA interrupts are (see DeviceScheduler.Advance).
    bool GetSio0InterruptPending() const;
    void ClearSio0Interrupt();

private:
    PsxMemoryHandle* handle_;
    PSXDmaState* dma_ = nullptr;
    PSXTimerState* timers_ = nullptr;
    PSXInterruptState* interrupts_ = nullptr;
};

inline PSXMemory::PSXMemory() : handle_(psx_memory_create()) {
    if (!handle_) {
        throw std::bad_alloc();
    }
}

inline PSXMemory::~PSXMemory() {
    psx_memory_destroy(handle_);
}

inline void PSXMemory::Reset() {
    psx_memory_reset(handle_);
}

inline uint8_t* PSXMemory::GetRAM() { return psx_memory_ram_ptr(handle_); }
inline uint32_t PSXMemory::GetRAMSize() const { return PSX_RAM_SIZE; }

inline void PSXMemory::AttachControllers(PSXDmaState* dma, PSXTimerState* timers, PSXInterruptState* interrupts) {
    dma_ = dma;
    timers_ = timers;
    interrupts_ = interrupts;
}

inline uint32_t PSXMemory::Read32(uint32_t address) {
    return psx_memory_read32(handle_, address, dma_, timers_, interrupts_);
}

inline void PSXMemory::Write32(uint32_t address, uint32_t value) {
    psx_memory_write32(handle_, address, value, dma_, timers_, interrupts_);
}

inline uint16_t PSXMemory::Read16(uint32_t address) {
    return psx_memory_read16(handle_, address, dma_, timers_, interrupts_);
}

inline void PSXMemory::Write16(uint32_t address, uint16_t value) {
    psx_memory_write16(handle_, address, value, dma_, timers_, interrupts_);
}

inline uint8_t PSXMemory::Read8(uint32_t address) {
    return psx_memory_read8(handle_, address, dma_, timers_, interrupts_);
}

inline void PSXMemory::Write8(uint32_t address, uint8_t value) {
    psx_memory_write8(handle_, address, value, dma_, timers_, interrupts_);
}

inline bool PSXMemory::GetSio0InterruptPending() const {
    return psx_memory_get_sio0_interrupt_pending(handle_) != 0;
}

inline void PSXMemory::ClearSio0Interrupt() {
    psx_memory_clear_sio0_interrupt(handle_);
}
