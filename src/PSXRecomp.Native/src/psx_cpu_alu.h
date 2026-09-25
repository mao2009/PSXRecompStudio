#pragma once

#include <cstdint>

/*
 * PSXCpu overflow-checked arithmetic (ADD/ADDI/SUB). Implemented in Rust
 * (`../rust/src/cpu_alu.rs`, Issue #495); this header only declares that
 * crate's internal C ABI for use by psx_cpu.cpp.
 *
 * Both functions take plain u32 operands and return the result by value;
 * Rust never allocates or retains state. Every function is infallible and
 * cannot panic. Must match `AluResult` in cpu_alu.rs field-for-field.
 */
struct PSXAluResult {
    uint32_t result;
    uint32_t overflow; // 0 or 1
};

extern "C" {
PSXAluResult psx_cpu_alu_add(uint32_t a, uint32_t b);
PSXAluResult psx_cpu_alu_sub(uint32_t a, uint32_t b);
}
