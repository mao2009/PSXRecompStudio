# PSXCpu Migration Slice: Exception Resolution

**Status:** Stable

**Authority:** Reference

**Related Issues:** #530, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_exception.cpp`
- Rust: `rust/src/cpu_exception.rs`
- Tests: `tests/test_psx_cpu_exception_rust.cpp` (runner `run_psx_cpu_exception_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_exception.h`

## Scope

`RaiseException` and `RaiseAddressError`: EPC and `CAUSE.BD` from the delay-slot state, `CAUSE` Excode/CE, BadVaddr, the SR stack push, the BEV vector, and the `last_exception_*` snapshot.

## Cross-slice calls

Called by decode, memory access, COP0 and pipeline. Writes `pc_` and clears `branch_pending_`/`branch_issued_`, which `Step` (pipeline) relies on.

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_exception.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_exception_vector_bev1`, `test_sr_stack_shift`, `test_exception_nested_sr`, `test_exception_in_delay_slot`, `test_ri_in_delay_slot`, `test_exception_raised_flag`, `test_trap_resolution_break_standalone_getters`, `test_trap_resolution_break_in_delay_slot_getters`, `test_cause_ce_cleared_by_non_cpu_exception`.

The migration PR replaces this section with its exports and decisions.
