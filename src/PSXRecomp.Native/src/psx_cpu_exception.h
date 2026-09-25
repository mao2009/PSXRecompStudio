#pragma once

#include <cstdint>

/*
 * PSXCpu exception resolution arithmetic (EPC, CAUSE, SR stack push, vector).
 * Implemented in Rust (`../rust/src/cpu_exception.rs`, Issue #530); this
 * header only declares that crate's internal C ABI for use by
 * psx_cpu_exception.cpp.
 *
 * The function takes plain u32 inputs and returns the resolution by value;
 * Rust never allocates or retains state, and the call is infallible and cannot
 * panic. Must match `ExceptionResolution` in cpu_exception.rs field-for-field.
 */
struct PSXExceptionResolution {
    uint32_t epc;
    uint32_t cause;
    uint32_t sr;
    uint32_t vector;
    uint32_t in_delay_slot; // 0 or 1
};

extern "C" {
PSXExceptionResolution psx_cpu_exception_resolve(uint32_t in_delay_slot, uint32_t delay_slot_pc,
                                                  uint32_t instr_addr, uint32_t cause, uint32_t sr,
                                                  uint32_t excode, uint32_t ce);
}
