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

Migrated by #529. `rust/src/cpu_cop0.rs` computes the new register value for
the MTC0 CAUSE write and for RFE. Its exports are **internal** to
`PSXRecomp.Native`: `src/psx_cpu_cop0.cpp` calls them (declared in
`src/psx_cpu_cop0.h`), so `include/psx_core.h`, `NativeInterop.cs`, and
`ABI_VERSION` are unchanged. Both take and return plain `u32` values, allocate
nothing, dereference no pointer, use no `unsafe`, retain no state, and contain
only constant masks and shifts, so neither can panic. Both are infallible
(FFI contract §5) and return their result directly.

| Export | Signature | Semantics | Used by |
|---|---|---|---|
| `psx_cpu_cop0_write_cause` | `uint32_t(uint32_t cause, uint32_t written)` | `(cause & ~0x300) \| (written & 0x300)`: IP[1:0] (bits 8-9) from `written`, every other bit kept from `cause`. | `MTC0 rt, $13` |
| `psx_cpu_cop0_rfe` | `uint32_t(uint32_t sr)` | `(sr & ~0x0F) \| ((sr >> 2) & 0x0F)`: `KUc<-KUp`, `IEc<-IEp`, `KUp<-KUo`, `IEp<-IEo`; KUo/IEo (bits 4-5) and bits 6-31 unchanged. | `RFE` |

Decisions:

- Only these two transformations moved. `PSXCpu` keeps the `cop0_` register
  array, GPR reads, the `rd` range check, the plain full-value MTC0 write to
  every register other than CAUSE, MFC0 and its load delay (`WriteRegDelayed`),
  and the SYSCALL/BREAK `RaiseException` calls. Those are state moves or
  calls into other slices, with no bit transformation for Rust to compute.
- The results are plain `u32` returns, not a `#[repr(C)]` struct: each export
  produces exactly one register value, the same shape as `cpu_ops.rs` (#501).
- Both are bit-exact with the replaced C++. The Rust unit tests compare them
  against a verbatim copy of the old C++ on zero, all-one and mixed patterns,
  and for RFE on all 64 combinations of the six stack bits.

Tests in `tests/test_psx_cpu_cop0_rust.cpp`:

- Moved from `test_psx_core.cpp` by #524: `test_cop0_mfc0_mtc0_roundtrip`,
  `test_mfc0_load_delay`, `test_cop0_cause_rw_bits`, `test_syscall_exception`,
  `test_break_exception`, `test_rfe_pop`.
- Added by #529: `test_rust_cop0_write_cause_mask` and `test_rust_cop0_rfe_pop`
  (the exports directly), `test_mtc0_cause_patterns_end_to_end`,
  `test_mtc0_non_cause_full_value_write` and `test_rfe_patterns_end_to_end`
  (through `PSXCore_Step`).
