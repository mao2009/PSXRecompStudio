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

Calls `WriteRegDelayed` (pipeline), `RaiseAddressError` (exception) and `PSXMemory` reads/writes. (Before #527 it also called `SignExtend16` from `psx_cpu.cpp`; the offset is now sign-extended in Rust.) `TranslateAddress`/`IsMapped` are also called by unaligned and pipeline.

## Current state

Migrated (#527). `rust/src/cpu_memory_access.rs` computes the effective
address, the alignment classification, the KUSEG/KSEG0/KSEG1 translation and
the loaded-value extension. Its exports are **internal** to
`PSXRecomp.Native`: `src/psx_cpu_memory_access.cpp` calls them (declared in
`src/psx_cpu_memory_access.h`), so `include/psx_core.h`, `NativeInterop.cs`
and `ABI_VERSION` are unchanged. `PSXCpu::TranslateAddress`/`IsMapped` keep
their `psx_cpu.h` signatures and now delegate to Rust, so the unaligned and
pipeline callers get the same translation without edits. The C++
`kUnmappedPhysical` constant and the inline address/alignment/extension logic
are removed; Rust is the only implementation.

`PSXCpu` keeps owning GPR reads, `PSXMemory::Read*`/`Write*` (and so MMIO),
`WriteRegDelayed`, the store-value truncation (`gpr_[rt] & 0xFF`/`0xFFFF`),
and `RaiseAddressError`. Every export takes and returns plain integers or a
`#[repr(C)]` POD by value, performs no allocation, dereferences no pointer,
uses no `unsafe`, retains no state, and cannot panic (`wrapping_add`, masks
and `as` casts only), so every export is infallible (FFI contract §5) and
returns its result directly.

`PSXMemAccess` / `MemAccess` is three `uint32_t`: `vaddr` (effective
address), `phys` (`translate(vaddr)`, `0xFFFFFFFF` when unmapped), `status`
(`0` OK, `1` misaligned, `2` unmapped).

| Export | Signature | Semantics |
|---|---|---|
| `psx_cpu_mem_translate` | `uint32_t(uint32_t virt)` | `virt <= 0x7FFFFFFF`: `virt`. `virt <= 0xBFFFFFFF`: `virt & 0x1FFFFFFF`. Else `0xFFFFFFFF` (unmapped). |
| `psx_cpu_mem_is_mapped` | `uint32_t(uint32_t phys)` | `1` unless `phys == 0xFFFFFFFF`. |
| `psx_cpu_mem_classify` | `PSXMemAccess(uint32_t base, int16_t offset, uint32_t width)` | `vaddr = base + (int32_t)offset`, wrapping. `status` is misaligned when `vaddr & (width - 1) != 0`, else unmapped when `phys == 0xFFFFFFFF`, else OK. `width` is 1, 2 or 4. |
| `psx_cpu_mem_extend_load` | `uint32_t(uint32_t raw, uint32_t width, uint32_t is_signed)` | Width 1/2: sign-extend (`is_signed != 0`) or zero-extend the low byte/halfword. Width 4: `raw`. |

Decisions, each checked against the previous C++ on `main`:

- Misaligned wins over unmapped: the C++ checked alignment before
  translating, so a misaligned KSEG2 `LH` raises AdEL with BadVaddr rather
  than loading zero.
- `LB`/`LBU`/`SB` have no alignment check (width 1 gives mask 0).
- Unmapped `LB`..`LW` still call `WriteRegDelayed(rt, 0)`: the zero goes
  through the load delay like a real load. Unmapped `SB`/`SH`/`SW` return
  without writing and without an exception.
- KSEG2 `0xFFFE0000` (cache control) stays unmapped, as before; this
  migration does not add it.
- `LWL`/`LWR`/`SWL`/`SWR` (#528) are not covered; they only reach this slice
  through `TranslateAddress`/`IsMapped`.

Tests: Rust unit tests in `cpu_memory_access.rs` (translation per segment,
sentinel, sign-extended/wrapping address, alignment per width, precedence,
extension per opcode). Native tests in `test_psx_cpu_memory_access_rust.cpp`
call the exports directly and run the instructions end to end: negative
offset and 32-bit wrap, unmapped loads queue zero through the delay slot,
unmapped stores leave memory untouched, misaligned KSEG2 raises AdEL, and
KSEG1 stores hit the translated address. The tests #524 moved here from
`test_psx_core.cpp` (`test_step_memory`, `test_kseg_translation`,
`test_kseg_ram_end_bios`, `test_kseg_unmapped`, `test_adel_misaligned_lh`,
`test_adel_misaligned_lhu`, `test_adel_misaligned_lw`,
`test_ades_misaligned_sh`, `test_ades_misaligned_sw`,
`test_aligned_halfword_word_access_unaffected`) pass unchanged.
