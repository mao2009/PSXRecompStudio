# Rust FFI Safety Contract

**Status:** Stable

**Authority:** Reference

**Related Issues:** #473, #471

**Related Components:** `src/PSXRecomp.Native/rust/`, `src/PSXRecomp.Native/src/psx_rust_abi.cpp`, `src/PSXRecomp.Native/include/psx_core.h`, `src/PSXRecomp.Core/NativeInterop.cs`

**Dependencies:** [ADR-023](../adr/023-rust-native-coexistence-substrate.md)

## Purpose

The rules every Rust function exported across the native boundary must follow.
This is a **migration contract** for the incremental C++ -> Rust migration
(#471), not an introduction to Rust FFI: it states what this repository
requires, so that a reviewer can decide whether a migration PR is acceptable by
reading the diff against this page.

ADR-023 owns the decision to host Rust inside the existing `PSXRecomp.Native`
shared library; this page owns the resulting per-function rules.

## Where Rust may live

Rust code lives under `src/PSXRecomp.Native/rust/` only. It is compiled as a
`staticlib` and linked into `PSXRecomp.Native`, so the managed side keeps using
the single `[LibraryImport("PSXRecomp.Native")]` boundary it already has.

- No Rust type, crate, or build step may become visible to
  `PSXRecomp.Core`/`.Infrastructure`/`.Cli`/`PSXRecompStudio` beyond the C ABI
  functions declared in `include/psx_core.h`.
- Managed P/Invoke declarations stay in `NativeInterop.cs` (the Domain interop
  boundary enforced by analyzer rule AARC007).
- Third-party crates require justification in the PR: the point of the
  substrate is that the unsafe surface stays small enough to audit.

## The exported-function rules

### 1. Linkage and symbol stability

- Every exported function is `#[no_mangle] pub extern "C"`. The implicit
  calling convention is the platform C convention; never `extern "system"`,
  `extern "Rust"`, or a `-unwind` variant.
- Exported symbol names are part of the ABI. Renaming or changing the meaning
  of an existing export is a breaking change: bump `ABI_VERSION`.
- The re-export thunks in `src/psx_rust_abi.cpp` carry the names managed code
  imports and stay one-to-one with the Rust exports, with no logic of their own.
  They exist because a static archive member is only linked in when referenced,
  and on Windows only a `dllexport`ed *definition* reaches the import library.
- A Rust function that only C++ inside this library calls (not P/Invoked) needs
  no thunk: declare it in an internal `src/*.h` header next to its C++ caller
  and keep it out of `include/psx_core.h`. It still obeys sections 2–9. See
  [Migrated subsystems](#migrated-subsystems).

### 2. ABI-visible types

Only these may cross the boundary:

- Fixed-width integers (`u8`/`i8` … `u64`/`i64`) and `f32`/`f64`. `c_int` and
  friends are allowed only where mirroring an existing C declaration requires
  it; prefer the explicit width.
- Raw pointers (`*const T` / `*mut T`) to ABI-visible types.
- `#[repr(C)]` structs whose fields are themselves ABI-visible.
- `#[repr(<integer>)]` field-less enums (e.g. `#[repr(i32)]`), or a plain
  integer status code.

Never: `String`, `&str`, `Vec<T>`, slices, references, tuples, trait objects,
`Option<T>` (except the documented null-pointer-optimised
`Option<extern "C" fn>` case), `bool` as a struct field, and any `enum` without
an explicit `repr`. `usize`/`isize` are permitted only for lengths that are
already `size_t` on the C side.

`#[repr(C)]` structs must be declared identically in `include/psx_core.h` and,
when managed code sees them, as a blittable `struct` in `PSXRecomp.Core`. A
layout change is an ABI break.

### 3. Ownership

- **The allocator frees.** Memory allocated by Rust is freed by Rust, memory
  allocated by C++/C# is freed by C++/C#. Never free across allocators.
- Every function that hands out Rust-owned memory must have a matching
  `*_Free`/`*_Destroy` export, and its doc comment must name that function.
- Follow the existing `PSXCore_Create` / `PSXCore_Destroy` pattern for handles:
  an opaque pointer whose fields are not part of the ABI, released exactly once.
- Default for everything else: the caller owns its buffers, and the callee
  borrows them **only for the duration of the call**. Retaining a caller-owned
  pointer past return requires an explicit, documented lifetime rule.

### 4. Pointers, null, and buffers

- Every pointer parameter's doc comment states: may it be null, must it be
  aligned, how long must it stay valid, and is it read, written, or both.
- Null is checked explicitly and reported as an error code. Never dereference
  on the strength of "the caller would not do that".
- A buffer is always a pointer **and** a length, in adjacent parameters, with
  the length in elements unless the name says bytes. The callee must not read
  or write outside `[ptr, ptr + len)`.
- A zero length is valid and must be handled without dereferencing the pointer
  (which is then allowed to be null or dangling).
- Pointer validity, alignment, provenance, and non-aliasing are the caller's
  obligation; state them in a `# Safety` section on the `unsafe extern "C"`
  function.

### 5. Error representation

- Fallible functions return `i32` status: `0` for success, negative for
  failure. Results travel through out-parameters.
- Status constants are defined once in Rust, mirrored as `#define` in
  `include/psx_core.h` and as `const int` in `NativeInterop.cs`. The current
  set is `PSX_RUST_OK` (0), `PSX_RUST_ERR_NULL_ARGUMENT` (-1),
  `PSX_RUST_ERR_PANIC` (-2); extend it rather than inventing per-function codes.
- On a failure path, out-parameters are documented as unspecified unless the
  function explicitly guarantees otherwise. Do not signal errors by an in-band
  sentinel inside a value's normal range.
- Infallible functions (no pointer arguments, no panicking operation) may
  return their value directly and must say so in their doc comment.

### 6. Panic containment

A panic must never unwind across the boundary — it is undefined behaviour and
would tear down the managed host.

- Wrap every body that can panic in `std::panic::catch_unwind` and convert
  `Err` into `PSX_RUST_ERR_PANIC`.
- `AssertUnwindSafe` is permitted only when the state the closure touches is
  caller-owned and the function documents that it is unspecified after a panic.
- Prefer not panicking at all: use checked/wrapping arithmetic and explicit
  bounds checks rather than indexing and `unwrap()`. Panic containment is the
  backstop, not the design.
- The release profile keeps `panic = "unwind"` on purpose, so containment
  produces an error code rather than aborting the process.

### 7. Integer width and endianness

- Widths are explicit on both sides; `int`/`long` never appear in a new
  declaration. `PSXCore_*` uses `int` where it predates this contract — mirror
  it exactly when extending those, and use fixed widths for everything new.
- Conversions are explicit and checked (`try_into`, `wrapping_*`, masking).
  Silent truncation is a defect.
- Scalars crossing the boundary use **host** endianness; no byte swapping on
  the boundary itself.
- PS1 guest data is little-endian and is converted at the point it is read from
  or written to guest memory, using explicit `from_le_bytes`/`to_le_bytes` — not
  by transmuting a host struct. This keeps a future big-endian host a
  compilation problem, not a silent wrong answer.

### 8. Unsafe isolation

- `unsafe` is confined to the thinnest possible wrapper at the boundary; the
  logic underneath is safe Rust, so it can be unit-tested directly (see
  `round_trip` in `rust/src/lib.rs`).
- Every `unsafe` block carries a `// SAFETY:` comment naming the invariant that
  makes it sound.
- Crates keep `#![deny(unsafe_op_in_unsafe_fn)]`, so an `unsafe fn` body must
  still mark its unsafe operations.
- No `static mut`, and no `transmute` across the boundary.

### 9. Threading

The current substrate is stateless and therefore trivially thread-safe. Any
future export that touches shared state must document its threading contract:
which handle may be used from which thread, whether concurrent calls on the
same handle are allowed, and what synchronisation the callee performs. Rust's
`Send`/`Sync` do not cross FFI — the documented contract is the only guarantee
a C++ or C# caller has.

## Definition of done for a migration PR

- [ ] Exports obey sections 1–9 and each one's doc comment states ownership,
      null handling, and error semantics.
- [ ] `include/psx_core.h` and `NativeInterop.cs` mirror the Rust signature and
      its documented semantics exactly.
- [ ] `ABI_VERSION` bumped when an existing export changed.
- [ ] Rust unit tests cover the safe logic plus the boundary's null/error paths.
- [ ] A managed test exercises the real staged artifact, not a mock.
- [ ] The C++ implementation being replaced is removed in the same change, or
      the PR states why both remain and how they stay in agreement.

## Current substrate surface

Infrastructure (#473), managed-visible. See `rust/src/lib.rs`.

| Export | Signature | Semantics |
|---|---|---|
| `PSXRecompRust_AbiVersion` | `uint32_t(void)` | Returns `ABI_VERSION` (1). Infallible. |
| `PSXRecompRust_RoundTrip` | `int32_t(uint32_t value, uint32_t* out_result)` | Writes `value ^ 0x5A5A5A5A`. `PSX_RUST_OK` / `PSX_RUST_ERR_NULL_ARGUMENT` / `PSX_RUST_ERR_PANIC`. `out_result` stays caller-owned. |

## Migrated subsystems

### Interrupt controller (#484)

`rust/src/interrupt.rs` implements I_STAT/I_MASK. Its exports are **internal**
to `PSXRecomp.Native`: `src/psx_api.cpp` calls them (declared in
`src/psx_interrupt.h`) to implement the unchanged `PSXCore_*Interrupt*`
functions of `include/psx_core.h`, so neither `psx_core.h`, `NativeInterop.cs`,
nor `ABI_VERSION` changed. The state is a `#[repr(C)]` two-`u32` value
(`InterruptState` / `PSXInterruptState`) stored inline in `PSXCore` and passed
by value: no pointers, no allocation, no `unsafe`, and no operation that can
panic, so every export is infallible (§5) and returns its result directly.

| Export | Signature | Semantics |
|---|---|---|
| `psx_interrupt_reset` | `PSXInterruptState(void)` | Power-on state (both zero). |
| `psx_interrupt_read_register` | `uint32_t(PSXInterruptState, uint32_t address)` | I_STAT, I_MASK, else 0. |
| `psx_interrupt_write_register` | `PSXInterruptState(PSXInterruptState, uint32_t address, uint32_t value)` | I_STAT write-0-to-clear (`&=`); I_MASK replaced; other addresses ignored. |
| `psx_interrupt_raise` | `PSXInterruptState(PSXInterruptState, int32_t irq)` | Sets I_STAT bit `irq`; `irq` outside 0..10 ignored. |
| `psx_interrupt_clear` | `PSXInterruptState(PSXInterruptState, int32_t irq)` | Clears I_STAT bit `irq`; `irq` outside 0..10 ignored. |
| `psx_interrupt_pending` | `uint32_t(PSXInterruptState)` | 1 when `I_STAT & I_MASK != 0`, else 0. |

### Timer controller (#486)

`rust/src/timer.rs` implements the three Root Counter timers. Its exports are
**internal** to `PSXRecomp.Native`: `src/psx_api.cpp` calls them (declared in
`src/psx_timer.h`) to implement the unchanged `PSXCore_*Timer*` functions of
`include/psx_core.h`, so neither `psx_core.h`, `NativeInterop.cs`, nor
`ABI_VERSION` changed. The state (`PSXTimerState`, three `PSXTimerChannel`
values) is `#[repr(C)]`, POD, and passed/returned by value: no pointers, no
allocation, no `unsafe`, and no operation that can panic, so every export is
infallible (§5). A register read can have a side effect (reading MODE clears
its target/overflow flags), so `psx_timer_read_register` returns
`PSXTimerReadResult` — the evolved state bundled with the read value — rather
than taking an out-parameter pointer.

| Export | Signature | Semantics |
|---|---|---|
| `psx_timer_reset` | `PSXTimerState(void)` | Power-on state (all fields zero). |
| `psx_timer_read_register` | `PSXTimerReadResult(PSXTimerState, uint32_t address)` | COUNT/MODE/TARGET, else 0; reading MODE clears its target/overflow flags. |
| `psx_timer_write_register` | `PSXTimerState(PSXTimerState, uint32_t address, uint32_t value)` | COUNT/TARGET replaced; MODE masked, forces IRQ_REQUEST set, and resets the channel's counter/toggle/frac/sync-arm/irq state. |
| `psx_timer_tick` | `PSXTimerState(PSXTimerState, uint32_t cycles)` | Advances all three timers by `cycles`, applying clock divisor, sync gating, and target/overflow IRQ semantics. |
| `psx_timer_set_sync_line` | `PSXTimerState(PSXTimerState, int32_t timer, int32_t active)` | Updates the Hblank/Vblank sync line and its edge side effects; `timer` outside 0–2 ignored. |
| `psx_timer_get_interrupt_pending` | `uint32_t(PSXTimerState, int32_t timer)` | 1 when `timer`'s IRQ is latched, else 0; 0 for `timer` outside 0–2. |
| `psx_timer_clear_interrupt` | `PSXTimerState(PSXTimerState, int32_t timer)` | Clears `timer`'s IRQ latch; `timer` outside 0–2 ignored. |

### DMA controller (#488)

`rust/src/dma.rs` implements the DMA controller registers (per-channel
MADR/BCR/CHCR, DPCR, DICR) and, since Issue #442, a transfer-duration model:
`psx_dma_tick` completes a started channel after a deterministic cycle count
and sets its DICR flag. No data is ever moved.
Its exports are **internal** to `PSXRecomp.Native`: `src/psx_api.cpp` calls
them (declared in `src/psx_dma.h`) to implement the `PSXCore_*Dma*` functions
of `include/psx_core.h`. #442 added one public entry point,
`PSXCore_TickDma(PSXCore*, uint32_t cycles)`, mirrored in `NativeInterop.cs`;
`ABI_VERSION` (which versions the `PSXRecompRust_*` substrate exports) is
unchanged. The state
(`PSXDmaState`, seven `PSXDmaChannelState` values, DPCR, DICR, and a
seven-`u32` `remaining` array of per-channel cycles left, 120 bytes) is
`#[repr(C)]`, POD, and passed/returned by value: no pointers, no allocation,
no `unsafe`, and no operation that can panic (address decoding and channel
indexing are range-checked), so every export is infallible (§5) and returns
its result directly.

| Export | Signature | Semantics |
|---|---|---|
| `psx_dma_reset` | `PSXDmaState(void)` | Power-on state: all 7 channels zero, DPCR `0x07654321`, DICR zero. |
| `psx_dma_read_register` | `uint32_t(PSXDmaState, uint32_t address)` | Per-channel MADR/BCR/CHCR at base + `channel * 0x10` + 0/4/8; DPCR as stored; DICR with derived bit 31; else 0. |
| `psx_dma_write_register` | `PSXDmaState(PSXDmaState, uint32_t address, uint32_t value)` | Per-channel MADR/BCR/CHCR replaced (a CHCR write also resets that channel's `remaining`); DPCR replaced; DICR flags (bits 0–6) write-1-to-clear and force-IRQ/master-enable/enables (bits 15/23/24–30) replaced; other addresses ignored. |
| `psx_dma_get_interrupt_pending` | `uint32_t(PSXDmaState)` | 1 when `master_enable && (flags & enables) != 0` or `force_irq`, i.e. the DICR bit-31 condition; else 0. |
| `psx_dma_tick` | `PSXDmaState(PSXDmaState, uint32_t cycles)` | Each started channel (CHCR bit 24, its DPCR enable bit `3 + 4*ch`, and bit 28 for sync mode 0) counts down one cycle per word (sync 0: BCR[15:0]; sync 1: BCR[15:0] × BCR[31:16]; zero field = 0x10000; sync 2/3: one word). On completion CHCR bits 24/28 clear and DICR flag `ch` is set when DICR enable `24 + ch` is set. Excess cycles are discarded. |

The DICR bit layout (flags 0–6, enables 24–30) is the one migrated from the C++
controller; psx-spx places enables at 16–22 and flags at 24–30. Reconciling it
is a separate change, since it alters guest-visible register semantics.

## Related

- [ADR-023: Rust Native Coexistence Substrate](../adr/023-rust-native-coexistence-substrate.md)
- [Native Library Build and Test Execution](native-library-build.md)
- [Top-level Architecture SSOT](../../ARCHITECTURE.md)
