#pragma once

#include <cstdint>

/*
 * PSXCpu non-trapping ALU / logic / compare / shift arithmetic (ADDU, SUBU,
 * AND, OR, XOR, NOR, SLT, SLTU, SLL/SRL/SRA and their variable/immediate
 * forms). Implemented in Rust (`../rust/src/cpu_ops.rs`, Issue #501); this
 * header only declares that crate's internal C ABI for use by psx_cpu.cpp.
 *
 * Every function takes plain u32 operands and returns the u32 result; Rust
 * never allocates or retains state, and no function can panic. Shift
 * functions use only the low 5 bits of `amount`. The caller keeps owning
 * immediate extension, GPR reads, and SetGPR.
 */
extern "C" {
uint32_t psx_cpu_ops_addu(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_subu(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_and(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_or(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_xor(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_nor(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_slt(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_sltu(uint32_t a, uint32_t b);
uint32_t psx_cpu_ops_sll(uint32_t value, uint32_t amount);
uint32_t psx_cpu_ops_srl(uint32_t value, uint32_t amount);
uint32_t psx_cpu_ops_sra(uint32_t value, uint32_t amount);
}
