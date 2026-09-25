# PSXCpu Migration Slice: Branch / Jump

**Status:** Stable

**Authority:** Reference

**Related Issues:** #526, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_control.cpp`
- Rust: `rust/src/cpu_control.rs`
- Tests: `tests/test_psx_cpu_control_rust.cpp` (runner `run_psx_cpu_control_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_control.h`

## Scope

`ExecBeq`, `ExecBne`, `ExecBlez`, `ExecBgtz`, `ExecBltz`, `ExecBgez`, `ExecBltzal`, `ExecBgezal`, `ExecJ`, `ExecJal`, `ExecJr`, `ExecJalr`: target calculation, the taken condition, and the `$ra`/`rd` link write.

## Cross-slice calls

Calls `SetPendingBranch` (pipeline), `SetGPR` and `ToSigned` (`psx_cpu.cpp`). Called by `ExecuteInstruction` (decode).

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_control.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_step_branch`, `test_step_jump`, `test_step_jr_jalr`, `test_branch_load_delay_interaction`.

The migration PR replaces this section with its exports and decisions.
