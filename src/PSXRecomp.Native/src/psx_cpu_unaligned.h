#pragma once

#include <cstdint>

/*
 * PSXCpu LWL/LWR/SWL/SWR aligned-base and little-endian byte merge.
 * Implemented in Rust (`../rust/src/cpu_unaligned.rs`, Issue #528); this
 * header only declares that crate's internal C ABI for use by
 * psx_cpu_unaligned.cpp.
 *
 * Every function takes plain u32 values and returns a u32; Rust never
 * allocates, retains state or performs memory I/O, and cannot panic.
 * `mem` is the aligned word at psx_cpu_unaligned_base(addr); loads return the
 * new register value, stores return the word to write back.
 */
extern "C" {
uint32_t psx_cpu_unaligned_base(uint32_t addr);
uint32_t psx_cpu_unaligned_lwl(uint32_t addr, uint32_t reg, uint32_t mem);
uint32_t psx_cpu_unaligned_lwr(uint32_t addr, uint32_t reg, uint32_t mem);
uint32_t psx_cpu_unaligned_swl(uint32_t addr, uint32_t reg, uint32_t mem);
uint32_t psx_cpu_unaligned_swr(uint32_t addr, uint32_t reg, uint32_t mem);
}
