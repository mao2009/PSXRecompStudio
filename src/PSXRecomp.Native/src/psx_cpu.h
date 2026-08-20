#pragma once

#include <cstdint>

static constexpr int PSX_GPR_COUNT = 32;
static constexpr uint32_t PSX_RAM_SIZE = 2 * 1024 * 1024;
static constexpr uint32_t PSX_BIOS_SIZE = 512 * 1024;
static constexpr uint32_t PSX_HW_REG_SIZE = 8 * 1024;

class PSXCpu {
public:
    PSXCpu();

    void Reset();

    uint32_t GetGPR(int index) const;
    void SetGPR(int index, uint32_t value);

    uint32_t GetPC() const;
    void SetPC(uint32_t value);

    uint32_t GetHI() const;
    void SetHI(uint32_t value);

    uint32_t GetLO() const;
    void SetLO(uint32_t value);

private:
    uint32_t gpr_[PSX_GPR_COUNT];
    uint32_t pc_;
    uint32_t hi_;
    uint32_t lo_;
};
