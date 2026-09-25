#pragma once

#include <cstdint>

/*
 * PSXCpu aligned load/store address semantics and virtual->physical
 * translation. Implemented in Rust (`../rust/src/cpu_memory_access.rs`, Issue
 * #527); this header only declares that crate's internal C ABI for use by
 * psx_cpu_memory_access.cpp.
 *
 * Every function takes plain integers and returns its result by value; Rust
 * never allocates, touches memory, or retains state, and no function can
 * panic. The caller keeps owning GPR reads, PSXMemory access, the load delay,
 * and AdEL/AdES raising. Must match `MemAccess` and the `MEM_ACCESS_*`
 * constants in cpu_memory_access.rs.
 */
constexpr uint32_t PSX_MEM_ACCESS_OK = 0;         // aligned and mapped
constexpr uint32_t PSX_MEM_ACCESS_MISALIGNED = 1; // raise AdEL/AdES; wins over unmapped
constexpr uint32_t PSX_MEM_ACCESS_UNMAPPED = 2;   // load queues 0, store is dropped

struct PSXMemAccess {
    uint32_t vaddr;  // base + sign-extended offset, 32-bit wrapping
    uint32_t phys;   // translated; 0xFFFFFFFF when unmapped
    uint32_t status; // PSX_MEM_ACCESS_*
};

extern "C" {
uint32_t psx_cpu_mem_translate(uint32_t virt);
uint32_t psx_cpu_mem_is_mapped(uint32_t phys); // 0 or 1
PSXMemAccess psx_cpu_mem_classify(uint32_t base, int16_t offset, uint32_t width);
uint32_t psx_cpu_mem_extend_load(uint32_t raw, uint32_t width, uint32_t is_signed);
}
