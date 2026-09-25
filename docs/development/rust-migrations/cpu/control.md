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

Calls `SetPendingBranch` (pipeline) and `SetGPR` (`psx_cpu.cpp`). Called by `ExecuteInstruction` (decode).

## Current state

Migrated (#526). `rust/src/cpu_control.rs` computes the pure part of every
branch/jump: the taken decision, the target, and the `PC + 8` link value. Its
exports are **internal** to `PSXRecomp.Native`: `src/psx_cpu_control.cpp`
calls them (declared in `src/psx_cpu_control.h`), so `include/psx_core.h`,
`NativeInterop.cs` and `ABI_VERSION` are unchanged. `PSXCpu` keeps owning the
GPR reads, the `$ra`/`rd` link write (`SetGPR`), `SetPendingBranch` (branch
delay, ADR-004/005) and `pc_`. The old C++ arithmetic is removed; there is one
implementation.

`PSXControlResult` / `ControlResult` is `#[repr(C)]`, three `u32` fields
(`target`, `taken` as 0/1, `link`), returned by value. Every export takes only
integer values, performs no allocation, dereferences no pointer, uses no
`unsafe`, retains no state, and cannot panic (all additions are `wrapping_*`),
so every export is infallible (§5 of the FFI contract).

| Export | Signature | Semantics | Used by |
|---|---|---|---|
| `psx_cpu_control_beq` | `PSXControlResult(uint32_t pc, uint32_t rs, uint32_t rt, int16_t offset)` | taken = `rs == rt` | `BEQ` |
| `psx_cpu_control_bne` | same | taken = `rs != rt` | `BNE` |
| `psx_cpu_control_blez` | `PSXControlResult(uint32_t pc, uint32_t rs, int16_t offset)` | taken = `int32_t(rs) <= 0` | `BLEZ` |
| `psx_cpu_control_bgtz` | same | taken = `int32_t(rs) > 0` | `BGTZ` |
| `psx_cpu_control_bltz` | same | taken = `int32_t(rs) < 0` | `BLTZ`, `BLTZAL` |
| `psx_cpu_control_bgez` | same | taken = `int32_t(rs) >= 0` | `BGEZ`, `BGEZAL` |
| `psx_cpu_control_j` | `PSXControlResult(uint32_t pc, uint32_t index)` | taken = 1, target = `((pc + 4) & 0xF0000000) \| (index << 2)` | `J`, `JAL` |
| `psx_cpu_control_jr` | `PSXControlResult(uint32_t pc, uint32_t rs)` | taken = 1, target = `rs` | `JR`, `JALR` |

For the conditional branches, target = `pc + 4 + (int32_t(offset) << 2)`.
Every result has `link = pc + 8`. All arithmetic wraps in 32 bits.

Decisions, each checked against the previous C++ on `main`:

- The linking forms reuse the non-linking export, and the C++ handler writes
  `link`. `BLTZAL`/`BGEZAL` write `$ra` whether or not the branch is taken, as
  before.
- Exports take register values, not register numbers. The handler reads
  `gpr_[rs]` before it writes the link, so `JALR rd, rs` with `rd == rs`
  jumps to the old `rs`, and `BLTZAL`/`BGEZAL` on `$ra` decide on the old
  `$ra`.
- `J`/`JAL` take the region from `pc + 4` (the delay slot), not `pc`. The
  decoder masks `index` to 26 bits; Rust does not mask again, like the C++.
- `JR`/`JALR` do not mask or align the target. A misaligned target still
  faults at fetch in the pipeline slice.

Tests: the Rust unit tests in `cpu_control.rs` cover sign extension, 32-bit
wrap, the jump region, the signed zero compares and the link value. In
`tests/test_psx_cpu_control_rust.cpp`, `test_rust_branch_zero_compares_are_signed`,
`test_rust_branch_negative_offset_and_jump_region` and
`test_rust_link_reads_source_before_linking` run through `PSXCore_Step`. The
tests moved from `test_psx_core.cpp` by #524 (`test_step_branch`,
`test_step_jump`, `test_step_jr_jalr`, `test_branch_load_delay_interaction`)
are unchanged and still pass.
