#pragma once

#include <cstdint>

/*
 * PSXCpu HI/LO multiply/divide arithmetic (MULT/MULTU/DIV/DIVU). Implemented
 * in Rust (`../rust/src/cpu_hilo.rs`, Issue #497); this header only declares
 * that crate's internal C ABI for use by psx_cpu.cpp.
 *
 * Every function takes plain u32 operands and returns the new HI/LO pair by
 * value; Rust never allocates or retains state. Every function is infallible
 * and cannot panic. Must match `MulDivResult` in cpu_hilo.rs field-for-field.
 */
struct PSXMulDivResult {
    uint32_t hi;
    uint32_t lo;
};

extern "C" {
PSXMulDivResult psx_cpu_hilo_mult(uint32_t a, uint32_t b);
PSXMulDivResult psx_cpu_hilo_multu(uint32_t a, uint32_t b);
PSXMulDivResult psx_cpu_hilo_div(uint32_t dividend, uint32_t divisor);
PSXMulDivResult psx_cpu_hilo_divu(uint32_t dividend, uint32_t divisor);
}
