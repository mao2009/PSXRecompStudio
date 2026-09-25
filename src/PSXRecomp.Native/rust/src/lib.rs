//! Minimal Rust coexistence substrate for the PSXRecompStudio native runtime.
//!
//! This crate was introduced to prove that Rust can be built, linked, called
//! and tested alongside the existing C++ native core (Issue #473). It hosts
//! individual subsystems migrated from C++ (Issue #471), each in its own
//! module, and must never grow into a runtime abstraction of its own.
//!
//! Migrated subsystems:
//! - [`interrupt`]: the I_STAT/I_MASK interrupt controller (Issue #484).
//! - [`timer`]: the Root Counter (Timer) controller (Issue #486).
//! - [`cpu_ops`]: `PSXCpu` non-trapping ALU/logic/shift arithmetic (Issue #501).
//! - [`cpu_hilo`]: `PSXCpu` HI/LO multiply/divide arithmetic (Issue #497).
//! - [`cpu_alu`]: `PSXCpu` overflow-checked ADD/ADDI/SUB arithmetic (Issue #495).
//! - [`dma`]: the DMA controller registers (Issue #488).
//! - [`memory`]: guest RAM/scratchpad/BIOS/HW-register storage (Issue #492).
- [`spu`]: register-only SPU MMIO state (Issue #445).
//!
//! Reserved, still-empty `PSXCpu` migration slots (Issue #524), registered
//! here once so each slice's PR edits only its own module:
//! [`cpu_decode`] (#525), [`cpu_control`] (#526), [`cpu_memory_access`]
//! (#527), [`cpu_unaligned`] (#528), [`cpu_cop0`] (#529), [`cpu_exception`]
//! (#530), [`cpu_pipeline`] (#531). See
//! `docs/development/rust-migrations/cpu/README.md`.
//!
//! The crate is compiled as a `staticlib` and linked into the existing
//! `PSXRecomp.Native` shared library, whose thin `extern "C"` re-export layer
//! (`../../src/psx_rust_abi.cpp`) publishes the DLL/so/dylib symbols that
//! managed callers P/Invoke. The rules every exported function here follows are
//! the repository FFI contract in `docs/development/rust-ffi-contract.md`.

#![deny(unsafe_op_in_unsafe_fn)]
#![deny(missing_docs)]

use std::panic::{catch_unwind, AssertUnwindSafe};

pub mod dma;
pub mod interrupt;
pub mod memory;
pub mod sio0;
pub mod spu;
pub mod timer;
pub mod cpu_ops;
pub mod cpu_hilo;
pub mod cpu_alu;
pub mod cpu_decode;
pub mod cpu_control;
pub mod cpu_memory_access;
pub mod cpu_unaligned;
pub mod cpu_cop0;
pub mod cpu_exception;
pub mod cpu_pipeline;

/// Version of the Rust substrate's C ABI contract.
///
/// Bumped only when an exported signature or its documented semantics change;
/// a managed caller uses it to detect a stale native artifact.
pub const ABI_VERSION: u32 = 1;

/// Sentinel mixed into [`round_trip`]'s result.
///
/// Chosen so that a zeroed or byte-swapped buffer cannot produce the expected
/// value by accident, which makes the smoke test sensitive to a broken build,
/// a stale artifact, or a mismatched calling convention.
pub const ROUND_TRIP_SENTINEL: u32 = 0x5A5A_5A5A;

/// Status code: the call succeeded and the out-parameter was written.
pub const PSX_RUST_OK: i32 = 0;

/// Status code: a required out-parameter pointer was null; nothing was written.
pub const PSX_RUST_ERR_NULL_ARGUMENT: i32 = -1;

/// Status code: a panic was caught at the FFI boundary; nothing is guaranteed
/// to have been written.
pub const PSX_RUST_ERR_PANIC: i32 = -2;

/// Deterministic, total transform used by the ABI smoke surface.
///
/// Pure Rust with no FFI concerns, so it is directly unit-testable. It is an
/// involution (`round_trip(round_trip(v)) == v`), which lets a caller verify
/// the boundary in both directions without extra exported symbols.
#[must_use]
#[inline]
pub const fn round_trip(value: u32) -> u32 {
    value ^ ROUND_TRIP_SENTINEL
}

/// Returns [`ABI_VERSION`].
///
/// Infallible and side-effect free: it reads no memory the caller owns and
/// contains no operation that can panic, so no unwind can originate here.
#[no_mangle]
pub extern "C" fn psx_rust_abi_version() -> u32 {
    ABI_VERSION
}

/// Writes [`round_trip`] of `value` to `out_result`.
///
/// Returns [`PSX_RUST_OK`] on success, [`PSX_RUST_ERR_NULL_ARGUMENT`] when
/// `out_result` is null, or [`PSX_RUST_ERR_PANIC`] when a panic was contained
/// at the boundary. Ownership of `out_result` stays with the caller; this
/// function borrows it only for the duration of the call and never retains it.
///
/// # Safety
///
/// `out_result` must be either null or a valid, properly aligned, writable
/// pointer to a single `u32` that stays valid for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_rust_round_trip(value: u32, out_result: *mut u32) -> i32 {
    if out_result.is_null() {
        return PSX_RUST_ERR_NULL_ARGUMENT;
    }

    // AssertUnwindSafe is sound here: the only state the closure touches is the
    // caller-owned out-parameter, and on the panic path the caller is told via
    // PSX_RUST_ERR_PANIC that its contents are unspecified.
    match catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: checked non-null above; validity/alignment are the caller's
        // documented obligation.
        unsafe { out_result.write(round_trip(value)) };
    })) {
        Ok(()) => PSX_RUST_OK,
        Err(_) => PSX_RUST_ERR_PANIC,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version_is_the_published_constant() {
        assert_eq!(psx_rust_abi_version(), ABI_VERSION);
        assert_eq!(ABI_VERSION, 1);
    }

    #[test]
    fn round_trip_applies_the_sentinel() {
        assert_eq!(round_trip(0), ROUND_TRIP_SENTINEL);
        assert_eq!(round_trip(ROUND_TRIP_SENTINEL), 0);
    }

    #[test]
    fn round_trip_is_an_involution_at_the_numeric_boundaries() {
        for value in [0u32, 1, 0x7FFF_FFFF, 0x8000_0000, u32::MAX, ROUND_TRIP_SENTINEL] {
            assert_eq!(round_trip(round_trip(value)), value, "value = {value:#010X}");
        }
    }

    #[test]
    fn exported_round_trip_writes_the_out_parameter() {
        let mut result = 0u32;
        // SAFETY: `result` is a live, aligned, writable u32 for this call.
        let status = unsafe { psx_rust_round_trip(0x1234_5678, &raw mut result) };

        assert_eq!(status, PSX_RUST_OK);
        assert_eq!(result, round_trip(0x1234_5678));
    }

    #[test]
    fn exported_round_trip_rejects_a_null_out_parameter() {
        // SAFETY: a null pointer is an explicitly supported argument.
        let status = unsafe { psx_rust_round_trip(0x1234_5678, std::ptr::null_mut()) };

        assert_eq!(status, PSX_RUST_ERR_NULL_ARGUMENT);
    }
}
