#pragma once

#include <cstdint>
#include <cstring>

static constexpr int PSX_DMA_CHANNEL_COUNT = 7;
static constexpr uint32_t PSX_DMA_BASE = 0x1F801080;
static constexpr uint32_t PSX_DMA_CHANNEL_STRIDE = 0x10;
static constexpr uint32_t PSX_DMA_DPCR = 0x1F8010F0;
static constexpr uint32_t PSX_DMA_DICR = 0x1F8010F4;

static constexpr uint32_t DICR_FLAGS_MASK   = 0x007F;
static constexpr uint32_t DICR_MASTER_EN    = 1u << 15;
static constexpr uint32_t DICR_ENABLES_MASK = 0x7F0000;
static constexpr uint32_t DICR_FORCE_IRQ    = 1u << 23;
static constexpr uint32_t DICR_IRQ_STATUS   = 1u << 31;

struct PSXDmaChannelState {
    uint32_t madr = 0;
    uint32_t bcr = 0;
    uint32_t chcr = 0;
};

class PSXDmaController {
public:
    PSXDmaController();

    void Reset();

    uint32_t ReadRegister(uint32_t address);
    void WriteRegister(uint32_t address, uint32_t value);

    bool GetInterruptPending() const;

    const PSXDmaChannelState& GetChannel(int index) const;
    uint32_t GetDpcr() const;
    uint32_t GetDicr() const;

private:
    int GetChannelIndex(uint32_t address) const;
    int GetRegisterOffset(uint32_t address) const;
    uint32_t ComputeDicrRead() const;

    PSXDmaChannelState channels_[PSX_DMA_CHANNEL_COUNT];
    uint32_t dpcr_;
    uint32_t dicr_;
};

inline PSXDmaController::PSXDmaController() {
    Reset();
}

inline void PSXDmaController::Reset() {
    std::memset(channels_, 0, sizeof(channels_));
    dpcr_ = 0x07654321;
    dicr_ = 0;
}

inline int PSXDmaController::GetChannelIndex(uint32_t address) const {
    if (address < PSX_DMA_BASE || address >= PSX_DMA_BASE + PSX_DMA_CHANNEL_COUNT * PSX_DMA_CHANNEL_STRIDE)
        return -1;
    return static_cast<int>((address - PSX_DMA_BASE) / PSX_DMA_CHANNEL_STRIDE);
}

inline int PSXDmaController::GetRegisterOffset(uint32_t address) const {
    return static_cast<int>((address - PSX_DMA_BASE) % PSX_DMA_CHANNEL_STRIDE);
}

inline uint32_t PSXDmaController::ComputeDicrRead() const {
    uint32_t flags = dicr_ & DICR_FLAGS_MASK;
    uint32_t enables = (dicr_ & DICR_ENABLES_MASK) >> 16;
    bool master = (dicr_ & DICR_MASTER_EN) != 0;
    bool any_active = ((flags & enables) != 0) && master;
    bool irq = any_active || ((dicr_ & DICR_FORCE_IRQ) != 0);
    return flags | (dicr_ & (DICR_MASTER_EN | DICR_ENABLES_MASK | DICR_FORCE_IRQ))
           | (flags << 24) | (irq ? DICR_IRQ_STATUS : 0);
}

inline uint32_t PSXDmaController::ReadRegister(uint32_t address) {
    if (address == PSX_DMA_DPCR)
        return dpcr_;
    if (address == PSX_DMA_DICR)
        return ComputeDicrRead();

    int ch = GetChannelIndex(address);
    if (ch < 0)
        return 0;

    int offset = GetRegisterOffset(address);
    switch (offset) {
        case 0: return channels_[ch].madr;
        case 4: return channels_[ch].bcr;
        case 8: return channels_[ch].chcr;
        default: return 0;
    }
}

inline void PSXDmaController::WriteRegister(uint32_t address, uint32_t value) {
    if (address == PSX_DMA_DPCR) {
        dpcr_ = value;
        return;
    }
    if (address == PSX_DMA_DICR) {
        uint32_t w1c = value & DICR_FLAGS_MASK;
        uint32_t ctrl = value & (DICR_MASTER_EN | DICR_ENABLES_MASK | DICR_FORCE_IRQ);
        dicr_ = (dicr_ & ~(DICR_FLAGS_MASK | DICR_MASTER_EN | DICR_ENABLES_MASK | DICR_FORCE_IRQ))
                | ((dicr_ & DICR_FLAGS_MASK) & ~w1c)
                | ctrl;
        return;
    }

    int ch = GetChannelIndex(address);
    if (ch < 0)
        return;

    int offset = GetRegisterOffset(address);
    switch (offset) {
        case 0: channels_[ch].madr = value; break;
        case 4: channels_[ch].bcr = value; break;
        case 8: channels_[ch].chcr = value; break;
    }
}

inline bool PSXDmaController::GetInterruptPending() const {
    uint32_t flags = dicr_ & DICR_FLAGS_MASK;
    uint32_t enables = (dicr_ & DICR_ENABLES_MASK) >> 16;
    bool master = (dicr_ & DICR_MASTER_EN) != 0;
    return ((flags & enables) != 0 && master) || ((dicr_ & DICR_FORCE_IRQ) != 0);
}

inline const PSXDmaChannelState& PSXDmaController::GetChannel(int index) const {
    return channels_[index];
}

inline uint32_t PSXDmaController::GetDpcr() const {
    return dpcr_;
}

inline uint32_t PSXDmaController::GetDicr() const {
    return ComputeDicrRead();
}
