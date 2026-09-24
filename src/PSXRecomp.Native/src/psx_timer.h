#pragma once

#include <cstdint>

/*
 * PS1 timers (Root Counters). Implemented in Rust (`../rust/src/timer.rs`,
 * Issue #486); this header only declares that crate's internal C ABI for use
 * by psx_api.cpp.
 *
 * The state is a plain value owned by PSXCore and passed by value; Rust never
 * allocates or retains it. Every function is infallible and cannot panic.
 * Must match `TimerChannel`/`TimerState`/`TimerReadResult` in timer.rs
 * field-for-field.
 */
struct PSXTimerChannel {
    uint16_t counter;
    uint16_t target;
    uint16_t mode;
    uint8_t  irq_flag;
    uint8_t  irq_armed;
    uint8_t  toggle_line;
    uint8_t  sync_active;
    uint8_t  sync_prev;
    uint8_t  sync_armed;
    uint32_t frac;
};

struct PSXTimerState {
    PSXTimerChannel channels[3];
};

struct PSXTimerReadResult {
    PSXTimerState state;
    uint32_t value;
};

extern "C" {
PSXTimerState psx_timer_reset(void);
PSXTimerReadResult psx_timer_read_register(PSXTimerState state, uint32_t address);
PSXTimerState psx_timer_write_register(PSXTimerState state, uint32_t address, uint32_t value);
PSXTimerState psx_timer_tick(PSXTimerState state, uint32_t cycles);
PSXTimerState psx_timer_set_sync_line(PSXTimerState state, int32_t timer, int32_t active);
uint32_t psx_timer_get_interrupt_pending(PSXTimerState state, int32_t timer);
PSXTimerState psx_timer_clear_interrupt(PSXTimerState state, int32_t timer);
}
