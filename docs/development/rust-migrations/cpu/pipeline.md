# PSXCpu Migration Slice: Pipeline / Load Delay

**Status:** Stable

**Authority:** Reference

**Related Issues:** #531, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_pipeline.cpp`
- Rust: `rust/src/cpu_pipeline.rs`
- Tests: `tests/test_psx_cpu_pipeline_rust.cpp` (runner `run_psx_cpu_pipeline_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_pipeline.h`

## Scope

`Step`, `FetchInstruction`, `FlushPipeline`, `UpdateLoadDelay`, `WriteRegDelayed`, `SetPendingBranch`: the interrupt check, fetch (with AdEL), branch-delay and branch-in-delay-slot handling, and the double-buffered load delay.

## Cross-slice calls

Calls `ExecuteInstruction` (decode), `RaiseException`/`RaiseAddressError` (exception), `TranslateAddress`/`IsMapped` (memory access) and `RecordGprWrite` (`psx_cpu.cpp`). `WriteRegDelayed` and `SetPendingBranch` are called by memory access, unaligned, COP0 and control. This is the second-wave slice: it depends on the call boundaries of the others staying stable.

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_pipeline.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_step_branch_delay_slot`, `test_load_delay`, `test_branch_in_delay_slot`, `test_kseg_instruction_fetch`, `test_adel_misaligned_fetch`, `test_adel_unmapped_fetch`.

The migration PR replaces this section with its exports and decisions.
