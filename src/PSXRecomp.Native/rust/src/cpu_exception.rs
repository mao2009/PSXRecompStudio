//! Reserved slot for the `PSXCpu` exception resolution Rust migration (Issue
//! #530).
//!
//! Intentionally empty: no exports and no implementation. The C++ code in
//! `src/psx_cpu_exception.cpp` is the only implementation.
//!
//! Issue #524 registered this module once in `lib.rs` and in the CMake cargo
//! `DEPENDS` list, so the migration PR edits this file rather than either of
//! those. Exports added here follow `docs/development/rust-ffi-contract.md`;
//! the slice's scope and file surface are in
//! `docs/development/rust-migrations/cpu/exception.md`.
