# PSXCpu Migration Slice: LWL/LWR/SWL/SWR

**Status:** Stable

**Authority:** Reference

**Related Issues:** #528, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_unaligned.cpp`
- Rust: `rust/src/cpu_unaligned.rs`
- Tests: `tests/test_psx_cpu_unaligned_rust.cpp` (runner `run_psx_cpu_unaligned_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_unaligned.h`

## Scope

`ExecLwl`, `ExecLwr`, `ExecSwl`, `ExecSwr`: the byte-merge of an unaligned word with the register or memory word.

## Cross-slice calls

Calls `TranslateAddress`/`IsMapped` (memory access), `WriteRegDelayed` (pipeline), `SignExtend16` (`psx_cpu.cpp`) and `PSXMemory` reads/writes.

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_unaligned.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_lwl_lwr_aligned`, `test_lwl_lwr_unchanged`.

The migration PR replaces this section with its exports and decisions.
