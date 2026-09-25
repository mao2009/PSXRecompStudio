# PSXCpu Migration Slice: COP0

**Status:** Stable

**Authority:** Reference

**Related Issues:** #529, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_cop0.cpp`
- Rust: `rust/src/cpu_cop0.rs`
- Tests: `tests/test_psx_cpu_cop0_rust.cpp` (runner `run_psx_cpu_cop0_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_cop0.h`

## Scope

`ExecSyscall`, `ExecBreak`, `ExecMfc0`, `ExecMtc0`, `ExecRfe`: the Sys/Bp traps, COP0 register moves (including the `CAUSE` IP[1:0] write mask) and the SR stack pop.

## Cross-slice calls

Calls `RaiseException` (exception) and `WriteRegDelayed` (pipeline). Called by `ExecuteInstruction` (decode).

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_cop0.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_cop0_mfc0_mtc0_roundtrip`, `test_mfc0_load_delay`, `test_cop0_cause_rw_bits`, `test_syscall_exception`, `test_break_exception`, `test_rfe_pop`.

The migration PR replaces this section with its exports and decisions.
