#pragma once

#include <cstdint>
#include <cstring>

// PS1 timers (aka Root Counters), per psx-spx "Timers".
//
// Per-timer registers (base = 0x1F801100 + timer*0x10):
//   +0x00: Counter value (16-bit, R/W)
//   +0x04: Counter mode   (16-bit, R/W, writes reset counter to 0000h)
//   +0x08: Counter target (16-bit, R/W)
//
// Clock sources (mode bits 8-9):
//   Timer 0: 0/2 = System Clock, 1/3 = Dotclock
//   Timer 1: 0/2 = System Clock, 1/3 = Hblank
//   Timer 2: 0/1 = System Clock, 2/3 = System Clock/8

static constexpr uint32_t PSX_TIMER_BASE       = 0x1F801100;
static constexpr uint32_t PSX_TIMER_STRIDE     = 0x10;
static constexpr uint32_t PSX_TIMER_COUNT_OFF  = 0x00;
static constexpr uint32_t PSX_TIMER_MODE_OFF   = 0x04;
static constexpr uint32_t PSX_TIMER_TARGET_OFF = 0x08;
static constexpr int      PSX_TIMER_COUNT      = 3;

// Mode register bit definitions (psx-spx). Scoped names avoid collisions with
// other subsystems (CodeRabbit finding).
namespace PSXTimerMode {
constexpr uint16_t SyncEnable       = 1u << 0;
constexpr uint16_t SyncMask         = 3u << 1;
constexpr uint16_t ResetTarget      = 1u << 3;
constexpr uint16_t IrqTarget        = 1u << 4;
constexpr uint16_t IrqOverflow      = 1u << 5;
constexpr uint16_t IrqRepeat        = 1u << 6;
constexpr uint16_t IrqToggle        = 1u << 7;
constexpr uint16_t ClkSrc0          = 1u << 8;
constexpr uint16_t ClkSrc1          = 1u << 9;
constexpr uint16_t IrqRequest       = 1u << 10;
constexpr uint16_t TargetFlag       = 1u << 11;
constexpr uint16_t OverflowFlag     = 1u << 12;
constexpr uint16_t WriteMask        = 0x03FFU; // bits 0-9 writable
}

struct PSXTimerChannel {
    uint16_t counter = 0;
    uint16_t target = 0;
    uint16_t mode = 0;          // bits 0-12
    uint8_t  irq_flag = 0;      // unacknowledged external IRQ latch (edge to interrupt controller)
    uint8_t  irq_armed = 1;     // one-shot: suppress IRQs after first fire until mode rewrite
    uint8_t  toggle_line = 0;   // toggle output line state
    uint8_t  sync_active = 0;   // current HBlank/VBlank line state
    uint8_t  sync_prev = 0;     // previous line state (for edge detect)
    uint8_t  sync_armed = 0;    // sync mode 3: pause until first blank then free run
    uint32_t frac = 0;          // clock fractional accumulator
};

class PSXTimerController {
public:
    PSXTimerController();

    void Reset();

    uint32_t ReadRegister(uint32_t address);
    void WriteRegister(uint32_t address, uint32_t value);

    void Tick(uint32_t cycles);

    void SetSyncLine(int timer, bool active);
    bool GetInterruptPending(int timer) const;
    void ClearInterrupt(int timer);

    const PSXTimerChannel& GetTimer(int index) const;

private:
    int GetTimerIndex(uint32_t address) const;
    int GetRegOffset(uint32_t address) const;
    uint32_t ClockDivisor(int timer) const;
    bool SyncAllowsCount(int timer) const;
    void AdvanceOne(int timer);
    void FireIRQ(int timer);

    PSXTimerChannel timers_[PSX_TIMER_COUNT];
};

inline PSXTimerController::PSXTimerController() {
    Reset();
}

inline void PSXTimerController::Reset() {
    std::memset(timers_, 0, sizeof(timers_));
    // mode = 0: free run, system clock, reset after FFFFh, IRQs disabled.
}

inline int PSXTimerController::GetTimerIndex(uint32_t address) const {
    if (address < PSX_TIMER_BASE ||
        address >= PSX_TIMER_BASE + PSX_TIMER_COUNT * PSX_TIMER_STRIDE)
        return -1;
    return static_cast<int>((address - PSX_TIMER_BASE) / PSX_TIMER_STRIDE);
}

inline int PSXTimerController::GetRegOffset(uint32_t address) const {
    return static_cast<int>((address - PSX_TIMER_BASE) % PSX_TIMER_STRIDE);
}

// Clock source -> number of CPU cycles per single counter increment.
// Dotclock/Hblank are GPU-generated signals (out of #125 scope); they default
// to counting every CPU cycle as a deterministic approximation until a GPU
// clock source is wired in.
inline uint32_t PSXTimerController::ClockDivisor(int timer) const {
    const PSXTimerChannel& tm = timers_[timer];
    uint16_t src = (tm.mode >> 8) & 0x3;
    if (timer == 2) {
        return (src == 2 || src == 3) ? 8u : 1u; // System Clock/8
    }
    return 1u; // Timer 0/1: system clock (dotclock/hblank default to sysclk)
}

// Whether the current sync state permits the counter to increment this cycle.
inline bool PSXTimerController::SyncAllowsCount(int timer) const {
    const PSXTimerChannel& tm = timers_[timer];
    if ((tm.mode & PSXTimerMode::SyncEnable) == 0)
        return true; // free run

    int sm = (tm.mode & PSXTimerMode::SyncMask) >> 1;
    if (timer == 2) {
        // Modes 0 or 3 = stop counter forever; 1 or 2 = free run (no h/v-blank).
        return (sm == 1 || sm == 2);
    }

    // Timer 0/1 use an externally-driven line (Hblank/Vblank).
    bool active = tm.sync_active != 0;
    bool rose = active && (tm.sync_prev == 0);
    switch (sm) {
        case 0: return !active;                        // pause during blank
        case 1: return true;                           // reset at blank (counts)
        case 2: return active;                         // reset at blank & pause outside
        case 3: return tm.sync_armed != 0;             // pause until first blank then free run
        default: return true;
    }
}

inline void PSXTimerController::AdvanceOne(int timer) {
    PSXTimerChannel& tm = timers_[timer];

    if (!SyncAllowsCount(timer))
        return;

    uint16_t old = tm.counter;
    tm.counter = static_cast<uint16_t>(tm.counter + 1u);

    // Target reached (equality on the 16-bit counter, per psx-spx).
    if (tm.counter == tm.target) {
        tm.mode |= PSXTimerMode::TargetFlag;
        if ((tm.mode & PSXTimerMode::IrqTarget) && tm.irq_armed)
            FireIRQ(timer);
        if (tm.mode & PSXTimerMode::ResetTarget)
            tm.counter = 0;
    }

    // Overflow (wrapped from FFFFh to 0000h).
    if (old == 0xFFFF) {
        tm.mode |= PSXTimerMode::OverflowFlag;
        if ((tm.mode & PSXTimerMode::IrqOverflow) && tm.irq_armed)
            FireIRQ(timer);
    }
}

inline void PSXTimerController::FireIRQ(int timer) {
    PSXTimerChannel& tm = timers_[timer];

    if (tm.mode & PSXTimerMode::IrqToggle) {
        tm.toggle_line ^= 1;
        if (tm.toggle_line) {
            tm.mode &= ~PSXTimerMode::IrqRequest; // bit10 = 0 => IRQ pending
            tm.irq_flag = 1;
        } else {
            tm.mode |= PSXTimerMode::IrqRequest;
        }
    } else {
        // Pulse mode: brief low pulse on bit10, raise external IRQ.
        tm.mode &= ~PSXTimerMode::IrqRequest;
        tm.irq_flag = 1;
        tm.mode |= PSXTimerMode::IrqRequest;
    }

    // One-shot: suppress further IRQs until the next mode write re-arms.
    // Interrupt-enable bits are preserved for ReadRegister and read-modify-write.
    if (!(tm.mode & PSXTimerMode::IrqRepeat))
        tm.irq_armed = 0;
}

inline void PSXTimerController::Tick(uint32_t cycles) {
    if (cycles == 0)
        return;

    for (int t = 0; t < PSX_TIMER_COUNT; t++) {
        uint32_t div = ClockDivisor(t);
        timers_[t].frac += cycles;
        uint32_t ticks = timers_[t].frac / div;
        timers_[t].frac %= div;
        for (uint32_t c = 0; c < ticks; c++)
            AdvanceOne(t);
    }
}

inline void PSXTimerController::SetSyncLine(int timer, bool active) {
    if (timer < 0 || timer >= PSX_TIMER_COUNT)
        return;
    PSXTimerChannel& tm = timers_[timer];
    tm.sync_prev = tm.sync_active;
    tm.sync_active = active ? 1u : 0u;

    // Edge side effects on a rising sync line (Hblank/Vblank) for Timer 0/1.
    bool rose = active && (tm.sync_prev == 0);
    if ((tm.mode & PSXTimerMode::SyncEnable) != 0 && timer != 2 && rose) {
        int sm = (tm.mode & PSXTimerMode::SyncMask) >> 1;
        if (sm == 1 || sm == 2)
            tm.counter = 0;      // reset counter at blank edge
        if (sm == 3)
            tm.sync_armed = 1;   // pause-until-first-blank -> free run
    }
}

inline bool PSXTimerController::GetInterruptPending(int timer) const {
    if (timer < 0 || timer >= PSX_TIMER_COUNT)
        return false;
    return timers_[timer].irq_flag != 0;
}

inline void PSXTimerController::ClearInterrupt(int timer) {
    if (timer < 0 || timer >= PSX_TIMER_COUNT)
        return;
    timers_[timer].irq_flag = 0;
}

inline uint32_t PSXTimerController::ReadRegister(uint32_t address) {
    int t = GetTimerIndex(address);
    if (t < 0)
        return 0;

    int offset = GetRegOffset(address);
    switch (offset) {
        case PSX_TIMER_COUNT_OFF:
            return timers_[t].counter;
        case PSX_TIMER_MODE_OFF: {
            uint32_t v = timers_[t].mode;
            // Reading MODE clears the reached-target and reached-FFFF flags.
            timers_[t].mode &= ~(PSXTimerMode::TargetFlag | PSXTimerMode::OverflowFlag);
            return v;
        }
        case PSX_TIMER_TARGET_OFF:
            return timers_[t].target;
        default:
            return 0;
    }
}

inline void PSXTimerController::WriteRegister(uint32_t address, uint32_t value) {
    int t = GetTimerIndex(address);
    if (t < 0)
        return;

    int offset = GetRegOffset(address);
    switch (offset) {
        case PSX_TIMER_COUNT_OFF:
            timers_[t].counter = static_cast<uint16_t>(value & 0xFFFF);
            break;
        case PSX_TIMER_MODE_OFF:
            // Writing MODE forces counter reset and re-arms IRQ request.
            timers_[t].mode = static_cast<uint16_t>((value & PSXTimerMode::WriteMask) | PSXTimerMode::IrqRequest);
            timers_[t].counter = 0;
            timers_[t].toggle_line = 0;
            timers_[t].frac = 0;
            timers_[t].sync_armed = 0;
            timers_[t].irq_flag = 0;
            timers_[t].irq_armed = 1; // re-arm one-shot / repeat IRQs
            break;
        case PSX_TIMER_TARGET_OFF:
            timers_[t].target = static_cast<uint16_t>(value & 0xFFFF);
            break;
        default:
            break;
    }
}

inline const PSXTimerChannel& PSXTimerController::GetTimer(int index) const {
    return timers_[index];
}
