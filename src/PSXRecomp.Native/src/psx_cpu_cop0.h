#pragma once

#include <cstdint>

/*
 * PSXCpu COP0 register bit transformations (MTC0 CAUSE write mask, RFE SR
 * stack pop). Implemented in Rust (`../rust/src/cpu_cop0.rs`, Issue #529);
 * this header only declares that crate's internal C ABI for use by
 * psx_cpu_cop0.cpp.
 *
 * Each function takes the current register value (plus the written value for
 * CAUSE) and returns the new one; Rust never allocates or retains state, and
 * no function can panic. The caller keeps owning the COP0 register array.
 */
extern "C" {
uint32_t psx_cpu_cop0_write_cause(uint32_t cause, uint32_t written);
uint32_t psx_cpu_cop0_rfe(uint32_t sr);
}
