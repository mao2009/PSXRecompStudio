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

Migrated (#528). `rust/src/cpu_unaligned.rs` computes the aligned base address
and the little-endian byte merge; `src/psx_cpu_unaligned.cpp` calls it through
`src/psx_cpu_unaligned.h` and keeps address translation, the mapped check,
memory I/O, the load-delay forwarding and the delayed register write. There is
no second C++ implementation of the merge.

### Exports

Internal to `PSXRecomp.Native` (not P/Invoked; `include/psx_core.h`,
`NativeInterop.cs` and `ABI_VERSION` are unchanged). Every export takes and
returns plain `uint32_t`, never panics and never allocates, so it is infallible
per the [Rust FFI Safety Contract](../../rust-ffi-contract.md) §5. `mem` is the
aligned word at `psx_cpu_unaligned_base(addr)`.

| Export | Signature | Semantics |
|---|---|---|
| `psx_cpu_unaligned_base` | `uint32_t(uint32_t addr)` | `addr & ~3` |
| `psx_cpu_unaligned_lwl` | `uint32_t(uint32_t addr, uint32_t reg, uint32_t mem)` | New `rt` for LWL |
| `psx_cpu_unaligned_lwr` | `uint32_t(uint32_t addr, uint32_t reg, uint32_t mem)` | New `rt` for LWR |
| `psx_cpu_unaligned_swl` | `uint32_t(uint32_t addr, uint32_t reg, uint32_t mem)` | Word to write back for SWL |
| `psx_cpu_unaligned_swr` | `uint32_t(uint32_t addr, uint32_t reg, uint32_t mem)` | Word to write back for SWR |

Each result is a single word, so no `#[repr(C)]` struct is needed.

### Semantics (little-endian, `n = addr & 3`)

| n | LWL | LWR | SWL (stored word) | SWR (stored word) |
|---|---|---|---|---|
| 0 | `reg & 0x00FFFFFF \| mem << 24` | `mem` | `mem & 0xFFFFFF00 \| reg >> 24` | `reg` |
| 1 | `reg & 0x0000FFFF \| mem << 16` | `reg & 0xFF000000 \| mem >> 8` | `mem & 0xFFFF0000 \| reg >> 16` | `mem & 0x000000FF \| reg << 8` |
| 2 | `reg & 0x000000FF \| mem << 8` | `reg & 0xFFFF0000 \| mem >> 16` | `mem & 0xFF000000 \| reg >> 8` | `mem & 0x0000FFFF \| reg << 16` |
| 3 | `mem` | `reg & 0xFFFFFF00 \| mem >> 24` | `reg` | `mem & 0x00FFFFFF \| reg << 24` |

`LWR addr` + `LWL addr+3` (either order) loads the unaligned word at `addr`;
`SWR addr` + `SWL addr+3` stores it. This matches
[`docs/cpu/test-specification.md`](../../../cpu/test-specification.md)
(LWL-001, LWR-001, SWL-001, SWR-001).

### Behavior changes fixed by this migration

1. **Byte merge was mirrored (big-endian).** The pre-#528 C++ used
   `shift = n * 8` for LWL/SWL and `(3 - n) * 8` for LWR/SWR, which is the
   big-endian formula: every offset produced the result that belongs to
   `3 - n` (for example LWL at `n = 0` loaded the whole word instead of only
   the low byte into bits 31..24). An `LWR addr` / `LWL addr+3` pair therefore
   did not reconstruct the word. The new tests failed 16/16 per-offset cases
   against the old code and pass now.
2. **LWL/LWR ignored a pending load to the same register.** They merged into
   the committed `gpr_[rt]`, and the second write then cancelled the first
   load, so a consecutive LWR/LWL pair without a NOP lost the first half.
   They now merge into `load_delay_value_` when `load_delay_reg_ == rt`
   ([pipeline.md](../../../cpu/pipeline.md), "Special LWL/LWR Behavior").

Open question: the forwarding applies to any load still in its delay slot for
the same `rt` (as DuckStation/Mednafen do), not only to a preceding LWL/LWR.
`docs/cpu/pipeline.md` describes only the LWL/LWR pair case; telling the two
apart would need new `PSXCpu` state in the shared `psx_cpu.h`. Only the pair
case is covered by tests.

The two tests moved here by #524 as `test_lwl_lwr_aligned` /
`test_lwl_lwr_unchanged` executed a plain `LW` (opcode `0x23`) and never
reached this code. They are renamed `test_lw_aligned_load_delay` /
`test_lw_replaces_existing_value` and kept as the LW baseline.

### Tests

- Rust: `cpu_unaligned::tests` (every offset of each op, aligned base, and
  byte-level pair round-trips with neighbour preservation).
- Native (`tests/test_psx_cpu_unaligned_rust.cpp`): all 16 op x offset cases
  with the real opcodes, a KSEG0 unaligned address, consecutive LWR/LWL and
  LWL/LWR pairs with no NOP, a pending load to another register (not merged),
  and an SWR/SWL pair that preserves the neighbouring bytes.
