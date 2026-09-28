# RTPS MAC1-3 width and IR saturation

**Status:** Stable

**Authority:** Reference

**Related Issues:** #595, #582, #447

**Related Components:** `src/PSXRecomp.Core/Runtime/Gte/GteRtpsKernel.cs`

## Decision

For RTPS/RTPT, PSXRecompStudio models MAC1-3 with two distinct stages:

1. the matrix/translation accumulation is a signed **44-bit** value;
2. `sf` is applied to that wide value (`0` or arithmetic right shift by 12);
3. the result is stored in the visible **32-bit MAC1-3 register**;
4. IR1/IR2, and the stored IR3 value, saturate from that visible 32-bit MAC value.

This means an `sf=0` internal value may be outside signed 32-bit while still
inside the valid 44-bit accumulator range. In that case the visible MAC wraps to
its low 32 bits before IR saturation. IR saturation must not use the full-width
pre-truncation value.

For `sf=1`, a valid signed 44-bit accumulator shifted by 12 is necessarily
within signed 32-bit range, so the 32-bit storage step cannot lose information.

## RTPS IR3 exception

RTPS/RTPT have a separate FLAG.22 rule. The **stored IR3** still uses the
lm-dependent saturation of visible MAC3, but the IR3 saturation flag is tested
from the wide Z accumulator shifted by 12, with the `lm=0` range
`-0x8000..0x7FFF`.

This is why `sf=0` can produce a saturated stored IR3 without FLAG.22 when
`wideZ >> 12` itself remains in range.

## MAC overflow timing

MAC1-3 overflow flags describe the 44-bit accumulator, not 32-bit MAC-register
truncation. The current implementation checks each accumulation step and wraps
back to signed 44-bit before the next term. This ordering matches the two mature
reference implementations inspected for #595.

A value that exceeds signed 32-bit but remains inside signed 44-bit therefore
does **not** set a MAC overflow flag.

## Evidence

Primary hardware documentation:

- psx-spx, GTE saturation and RTPS/RTPT formulas:
  https://psx-spx.consoledev.net/geometrytransformationenginegte/

Independent implementations inspected at fixed revisions:

- DuckStation `src/core/gte.cpp`,
  revision `d78fd23db934cabc38d66e9a3dd8106f27a00772`:
  https://github.com/stenzek/duckstation/blob/d78fd23db934cabc38d66e9a3dd8106f27a00772/src/core/gte.cpp
  - `SignExtendMACResult` checks and sign-extends the 44-bit accumulator.
  - RTPS stores `x/y/z` with `TruncateAndSetMAC`, then saturates IR1/IR2
    from `REGS.MAC1/2`.
  - RTPS tests FLAG.22 from `z >> 12` separately from stored IR3.

- Mednafen `src/psx/gte.cpp`,
  revision `f0ee9d595db68ad5247ba5ac6a8367fdced9c3fc`:
  https://github.com/libretro-mirrors/mednafen-git/blob/f0ee9d595db68ad5247ba5ac6a8367fdced9c3fc/src/psx/gte.cpp
  - `A_MV` checks/wraps every 44-bit partial accumulation.
  - `MultiplyMatrixByVector_PT` stores the shifted result into 32-bit
    `MAC[1..3]` and then feeds those values to `Lm_B`.
  - `Lm_B_PTZ` receives both visible MAC3 and the wide `tmp[2] >> 12`,
    preserving the RTPS FLAG.22 exception.

The two implementations independently agree with the ordering above.

## Regression vectors

`GteRtpsKernelTests` includes values where `|TRX << 12| = 0x1FFFFF000`.
They are larger than signed 32-bit but comfortably inside signed 44-bit.

The tests cover:

- positive and negative wide values;
- `sf=0`, proving visible-32-bit truncation precedes IR1 saturation;
- `sf=1`, proving the wide value is shifted before visible MAC storage;
- absence of MAC overflow flags for >32-bit-but-valid-44-bit values.

Existing RTPS tests separately cover 44-bit positive/negative overflow and the
special IR3 FLAG.22 behavior.
