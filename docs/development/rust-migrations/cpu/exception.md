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

Migrated (#530). `rust/src/cpu_exception.rs` computes the new EPC, CAUSE, SR
and exception vector from the current inputs; `src/psx_cpu_exception.cpp`
calls it through `src/psx_cpu_exception.h` and applies the result to
`cop0_[12..14]` and `pc_`. C++ keeps BadVaddr (`RaiseAddressError` stores the
faulting address unchanged, so there is nothing to compute), the
`last_exception_*` snapshot, `exception_raised_`, and clearing
`branch_pending_`/`branch_issued_`. There is no second C++ implementation of
the arithmetic, and no observable behavior changed.

### Exports

Internal to `PSXRecomp.Native` (not P/Invoked; `include/psx_core.h`,
`NativeInterop.cs` and `ABI_VERSION` are unchanged). The export takes plain
`uint32_t` values, never panics and never allocates, so it is infallible per
the [Rust FFI Safety Contract](../../rust-ffi-contract.md) §5 and returns a
`#[repr(C)]` POD by value (`ExceptionResolution` / `PSXExceptionResolution`:
`epc`, `cause`, `sr`, `vector`, `in_delay_slot`, five `u32`).

| Export | Signature | Semantics |
|---|---|---|
| `psx_cpu_exception_resolve` | `PSXExceptionResolution(uint32_t in_delay_slot, uint32_t delay_slot_pc, uint32_t instr_addr, uint32_t cause, uint32_t sr, uint32_t excode, uint32_t ce)` | See below. `in_delay_slot` nonzero = true. |

### Semantics

- **EPC / BD**: in a delay slot, `epc = delay_slot_pc - 4` (the owning branch,
  wrapping) and `in_delay_slot = 1`, CAUSE bit 31 set; otherwise
  `epc = instr_addr`, BD clear.
- **CAUSE**: Excode (bits 6:2), CE (29:28) and BD (31) are replaced by
  `excode << 2`, `(ce & 3) << 28` and BD; every other bit, including the
  software IP[1:0] (bits 9:8), is preserved. `excode` is not masked, exactly
  as before; every caller passes a 5-bit code. Non-CpU callers pass `ce = 0`,
  so a stale CE from an earlier CpU is cleared.
- **SR**: `(sr & ~0x3F) | ((sr & 0x0F) << 2)`, i.e. KUo/IEo <- KUp/IEp,
  KUp/IEp <- KUc/IEc, KUc/IEc <- 0; bits above 5 untouched.
- **Vector**: `0xBFC00180` when SR.BEV (bit 22) is set, else `0x80000080`.
  BEV is read from the input SR; the stack push never touches bit 22.

### Tests

- Rust: `cpu_exception::tests` (EPC/BD in and out of a delay slot, EPC
  wrap, nonzero-as-true at the export, every 5-bit Excode, CE masking and
  stale-CE clear, IP and other CAUSE bits preserved, all 64 SR low-bit
  patterns plus upper-bit preservation, BEV vector selection).
- Native (`tests/test_psx_cpu_exception_rust.cpp`): the nine tests moved here
  by #524, plus software IP[1:0] preserved with a stale Excode/BD cleared,
  CpU setting CE, and AdES in a delay slot setting BadVaddr, BD and EPC.
