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

Migrated (#525). `rust/src/cpu_decode.rs` classifies the instruction word and
extracts its operand fields. `src/psx_cpu_decode.cpp` calls it once per
instruction and switches on the result to call the `Exec*` handler, or to raise
RI/CpU. No C++ opcode/funct/REGIMM/COP0 decoding is left.

The export is **internal** to `PSXRecomp.Native`. It is declared in
`src/psx_cpu_decode.h` and called only from `psx_cpu_decode.cpp`, so
`include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are unchanged. It
takes a `u32` and returns a `#[repr(C)]` POD by value. It does no allocation,
dereferences no pointer, uses no `unsafe` and keeps no state. It only uses
constant shifts, masks and integer matches, none of which can panic, so it is
infallible (FFI contract §5) and needs no `catch_unwind`.

| Export | Signature | Semantics |
|---|---|---|
| `psx_cpu_decode` | `PSXDecodedInstruction(uint32_t instruction)` | Returns the handler (`op`) and every operand field of `instruction`. |

`PSXDecodedInstruction` / `DecodedInstruction` is eight `uint32_t`s (32 bytes,
checked by a `static_assert` and a Rust test):

| Field | Bits | Notes |
|---|---|---|
| `op` | — | `PSXDecodeOp` / `DecodeOp`, `#[repr(u32)]`. `Reserved = 0`, `CopUnusable = 1`, then one value per `Exec*` handler (2-62). The values are ABI and are mirrored in the header. |
| `rs`, `rt`, `rd`, `shamt` | 21-25, 16-20, 11-15, 6-10 | |
| `imm` | 0-15 | Zero-extended. The caller narrows it to the handler's `int16_t` or `uint16_t` parameter, which chooses sign or zero extension exactly as before. |
| `target` | 0-25 | J/JAL. |
| `cop` | 26-27 | `CAUSE.CE` for `CopUnusable`. |

Every field is extracted from every word. The caller passes each handler the
fields it took before.

Decisions:

- The classification copies the C++ switch that was on `main`, not a MIPS
  manual. REGIMM compares the full 5-bit `rt` (only 0x00/0x01/0x10/0x11). COP0
  accepts MFC0 (`rs == 0`), MTC0 (`rs == 4`) and RFE (`rs == 0x10 && funct ==
  0x10`); any other COP0 form is RI. COP1-3, LWC1-3 and SWC1-3 are CpU with
  `CE = opcode & 3`. LWC0/SWC0 and every other undefined opcode or funct are RI.
- Rust only classifies. Raising RI/CpU, extending the immediate, and all
  GPR/COP0/memory/PC/pipeline state stay in C++.
- `Reserved` is `0`, so a zeroed result fails closed. The C++ switch also sends
  any value it does not recognise to RI.

Tests:

- Rust (`cpu_decode.rs`): every SPECIAL funct (0-63), every REGIMM `rt`
  (0-31), every primary opcode (0-63) and a COP0 `rs` × `funct` grid, each
  also with all operand bits set. Also the CpU coprocessor number, each field
  at its bit boundaries (all-zero, all-one, one field at a time, immediate
  0x7FFF/0x8000, 26-bit target) and the struct layout.
- Native (`tests/test_psx_cpu_decode_rust.cpp`): the tests moved from
  `test_psx_core.cpp` by #524 (`test_ri_undefined_opcode`,
  `test_ri_undefined_special_funct`, `test_ri_undefined_regimm`,
  `test_ri_undefined_cop0_form`, `test_cpu_unusable_cop1`,
  `test_cpu_unusable_cop2`, `test_cpu_unusable_cop3`,
  `test_cpu_unusable_lwc2`, `test_cpu_unusable_swc2`), plus:
  - `test_rust_decode_classification_and_fields`: calls `psx_cpu_decode`
    through the C++ header, so the two enum/struct copies cannot drift.
  - `test_ri_reserved_encoding_boundaries`: LWC0, SWC0, REGIMM `rt = 0x12`,
    COP0 TLBR and opcode 0x3F raise RI.
  - `test_cpu_unusable_every_form`: all nine COPz/LWCz/SWCz opcodes raise CpU
    with the right `CE`.
  - `test_dispatch_routes_operand_fields`: `shamt`, `rs` as a shift amount,
    and the sign- or zero-extended immediate reach the right handler argument.
