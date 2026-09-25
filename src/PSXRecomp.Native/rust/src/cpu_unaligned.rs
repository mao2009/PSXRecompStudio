//! Reserved slot for the `PSXCpu` LWL/LWR/SWL/SWR Rust migration (Issue #528).
//!
//! Intentionally empty: no exports and no implementation. The C++ code in
//! `src/psx_cpu_unaligned.cpp` is the only implementation.
//!
//! Issue #524 registered this module once in `lib.rs` and in the CMake cargo
//! `DEPENDS` list, so the migration PR edits this file rather than either of
//! those. Exports added here follow `docs/development/rust-ffi-contract.md`;
//! the slice's scope and file surface are in
//! `docs/development/rust-migrations/cpu/unaligned.md`.
