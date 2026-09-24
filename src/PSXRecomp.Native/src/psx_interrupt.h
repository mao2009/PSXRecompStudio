#pragma once

#include <cstdint>

/*
 * Interrupt controller (I_STAT/I_MASK). Implemented in Rust
 * (`../rust/src/interrupt.rs`, Issue #484); this header only declares that
 * crate's internal C ABI for use by psx_api.cpp.
 *
 * The state is a plain value owned by PSXCore and passed by value; Rust never
 * allocates or retains it. Every function is infallible and cannot panic.
 * Must match `InterruptState` in interrupt.rs field-for-field.
 */
struct PSXInterruptState {
    uint32_t i_stat;
    uint32_t i_mask;
};

extern "C" {
PSXInterruptState psx_interrupt_reset(void);
uint32_t psx_interrupt_read_register(PSXInterruptState state, uint32_t address);
PSXInterruptState psx_interrupt_write_register(PSXInterruptState state, uint32_t address, uint32_t value);
PSXInterruptState psx_interrupt_raise(PSXInterruptState state, int32_t irq);
PSXInterruptState psx_interrupt_clear(PSXInterruptState state, int32_t irq);
uint32_t psx_interrupt_pending(PSXInterruptState state);
}
