#pragma once

#include <cstdint>
#include <cstring>

static constexpr uint32_t PSX_INT_STAT = 0x1F801070;
static constexpr uint32_t PSX_INT_MASK = 0x1F801074;
static constexpr int      PSX_INT_IRQ_COUNT = 11;

class PSXInterruptController {
public:
    PSXInterruptController();

    void Reset();

    uint32_t ReadRegister(uint32_t address);
    void WriteRegister(uint32_t address, uint32_t value);

    void Raise(int irq);
    void Clear(int irq);

    bool GetInterruptPending() const;
    uint32_t GetStatus() const;
    uint32_t GetMask() const;

private:
    uint32_t i_stat_;
    uint32_t i_mask_;
};

inline PSXInterruptController::PSXInterruptController() {
    Reset();
}

inline void PSXInterruptController::Reset() {
    i_stat_ = 0;
    i_mask_ = 0;
}

inline uint32_t PSXInterruptController::ReadRegister(uint32_t address) {
    if (address == PSX_INT_STAT)
        return i_stat_;
    if (address == PSX_INT_MASK)
        return i_mask_;
    return 0;
}

inline void PSXInterruptController::WriteRegister(uint32_t address, uint32_t value) {
    if (address == PSX_INT_STAT) {
        // Write-0-to-clear (psx-spx "Interrupt Acknowledge"):
        // writing 0 clears the edge-latched bit, writing 1 leaves it unchanged.
        i_stat_ &= value;
        return;
    }
    if (address == PSX_INT_MASK) {
        i_mask_ = value;
    }
}

inline void PSXInterruptController::Raise(int irq) {
    if (irq < 0 || irq >= PSX_INT_IRQ_COUNT)
        return;
    i_stat_ |= (1u << irq);
}

inline void PSXInterruptController::Clear(int irq) {
    if (irq < 0 || irq >= PSX_INT_IRQ_COUNT)
        return;
    i_stat_ &= ~(1u << irq);
}

inline bool PSXInterruptController::GetInterruptPending() const {
    return (i_stat_ & i_mask_) != 0;
}

inline uint32_t PSXInterruptController::GetStatus() const {
    return i_stat_;
}

inline uint32_t PSXInterruptController::GetMask() const {
    return i_mask_;
}