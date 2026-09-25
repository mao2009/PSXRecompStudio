# PSXCpu Migration Slice: Aligned Load/Store

**Status:** Stable

**Authority:** Reference

**Related Issues:** #527, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_memory_access.cpp`
- Rust: `rust/src/cpu_memory_access.rs`
- Tests: `tests/test_psx_cpu_memory_access_rust.cpp` (runner `run_psx_cpu_memory_access_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_memory_access.h`

## Scope

`kUnmappedPhysical`, `TranslateAddress`, `IsMapped`, and `ExecLb`, `ExecLbu`, `ExecLh`, `ExecLhu`, `ExecLw`, `ExecSb`, `ExecSh`, `ExecSw`: effective address, alignment check (AdEL/AdES), KUSEG/KSEG0/KSEG1 translation, and the unmapped read-0/ignored-write rule.

## Cross-slice calls

Calls `WriteRegDelayed` (pipeline), `RaiseAddressError` (exception), `SignExtend16` (`psx_cpu.cpp`) and `PSXMemory` reads/writes. `TranslateAddress`/`IsMapped` are also called by unaligned and pipeline.

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_memory_access.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_step_memory`, `test_kseg_translation`, `test_kseg_ram_end_bios`, `test_kseg_unmapped`, `test_adel_misaligned_lh`, `test_adel_misaligned_lhu`, `test_adel_misaligned_lw`, `test_ades_misaligned_sh`, `test_ades_misaligned_sw`, `test_aligned_halfword_word_access_unaffected`.

The migration PR replaces this section with its exports and decisions.
