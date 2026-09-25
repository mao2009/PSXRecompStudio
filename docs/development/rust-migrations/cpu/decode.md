# PSXCpu Migration Slice: Decode / Dispatch

**Status:** Stable

**Authority:** Reference

**Related Issues:** #525, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_decode.cpp`
- Rust: `rust/src/cpu_decode.rs`
- Tests: `tests/test_psx_cpu_decode_rust.cpp` (runner `run_psx_cpu_decode_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_decode.h`

## Scope

`PSXCpu::ExecuteInstruction`: the opcode/funct/rt/rs switch that calls each `Exec*` handler, and raises RI (0x0A) for reserved encodings and CpU (0x0B, with `CAUSE.CE`) for COP1/COP2/COP3 and LWC/SWC 1-3.

## Cross-slice calls

Calls every `Exec*` handler (all other slices plus the ALU/HI-LO handlers in `psx_cpu.cpp`) and `RaiseException` (exception).

## Current state

Not migrated. The C++ code is the only implementation and
`rust/src/cpu_decode.rs` is an empty, documentation-only module with no exports.

Existing tests for this slice, moved from `test_psx_core.cpp` by #524:
`test_ri_undefined_opcode`, `test_ri_undefined_special_funct`, `test_ri_undefined_regimm`, `test_ri_undefined_cop0_form`, `test_cpu_unusable_cop1`, `test_cpu_unusable_cop2`, `test_cpu_unusable_cop3`, `test_cpu_unusable_lwc2`, `test_cpu_unusable_swc2`.

The migration PR replaces this section with its exports and decisions.
