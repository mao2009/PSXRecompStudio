# Recompiler Host Code Generation (Phase 3A)

Deterministic C source generation from validated #206 GPR IR. Generates
self-contained, compilable C source with fixed-width types and well-defined
arithmetic — no UB, no host-width assumptions.

## Backend Choice

**Plain C** (C11, `<stdint.h>`). Rationale: simplest portable standard; maps
directly to gcc/g++ native toolchain; `uint32_t` guarantees 32-bit guest
values without host-width assumptions; no class hierarchies needed for
Phase 3A scope.

## Generated ABI

### State struct

```c
typedef struct {
  uint32_t gpr[32];
  uint32_t hi;
  uint32_t lo;
  uint32_t pc;
  int32_t termination_reason;
  uint32_t next_pc;
} RecompilerState;
```

- `gpr[0]` is always 0 on entry (caller must ensure).
- `hi`, `lo`, `pc` are present for ABI stability (Phase 3B+); initialized to
  0 in Phase 3A usage.
- `termination_reason`: written by the generated block on exit; 0 = Success;
  nonzero = `RecompilerIrTerminationReason` byte value cast to `int32_t`.
- `next_pc`: set only on Success exit.

### Function signature

```c
static int32_t recompiler_block_0x<entryPc>(RecompilerState* state);
```

- Takes a pointer to `RecompilerState`.
- On every exit, writes `state->termination_reason` (0 on Success, the reason
  code otherwise).
- Returns 0 on Success (with `state->next_pc` set), or the termination reason
  code as a non-zero `int32_t`.

### Dispatch function

```c
int32_t recompiler_dispatch(RecompilerState* state);
```

For Phase 3A (single-block programs), dispatches to the single block function.

### Entry / exit behavior

- Entry: caller initializes `gpr[0] = 0`.
- Exit: returns termination reason and writes it to `state->termination_reason`;
  on Success, also sets `state->next_pc`.
- No execution loop (out of scope for Phase 3A).

### GPR access

- Read: `state->gpr[i]`
- Write: `state->gpr[i] = value`
- `$zero` invariant preserved: generator never emits `WriteGpr` to `gpr[0]`.

## Fixed-Width Policy

- All guest values: `uint32_t` / `int32_t` via `<stdint.h>`.
- Never use host `long`, `int`, or pointer-width arithmetic for guest values.
- Immediate constants 0-9 are rendered as plain decimal literals; larger
  values are rendered as `(valueu)`.

## UB Avoidance Rules

### Unsigned wrapping (Add / Subtract)

```c
uint32_t r = (uint32_t)a + (uint32_t)b;   // well-defined modular wrap
uint32_t r = (uint32_t)a - (uint32_t)b;   // well-defined modular wrap
```

Signed overflow is UB; all guest arithmetic uses unsigned types.

### Shifts (SLL / SRL)

```c
uint32_t r = (uint32_t)a << (s & 31u);   // well-defined for uint32_t
uint32_t r = (uint32_t)a >> (s & 31u);   // well-defined for uint32_t
```

Shift amount masked to 5 bits; `>>` on `uint32_t` is always logical.

### Arithmetic shift right (SRA)

```c
static uint32_t recompiler_sra32(uint32_t a, uint32_t s) {
  uint32_t sh = s & 31u;
  uint32_t result = a >> sh;
  if ((a & 0x80000000u) != 0u && sh != 0u) {
    result |= (0xFFFFFFFFu << (32u - sh));
  }
  return result;
}
```

This is a well-defined, 64-bit-free formulation that does not depend on the
implementation-defined behavior of `>>` on signed values.

### NOR

```c
uint32_t r = ~(a | b);   // well-defined on uint32_t
```

## Fixed Build Recipe

| Parameter     | Value                                    |
|---------------|------------------------------------------|
| Compiler      | `gcc` (primary)                          |
| Standard      | `-std=c11`                               |
| Optimization  | `-O0` (semantic debugging priority)      |
| Warnings      | `-Wall -Wextra`                          |
| Includes      | `<stdint.h>` only (self-contained)       |
| Output        | Generated to temp dir in tests; never committed |

## Deterministic Generation

Same IR + same config → byte-equivalent source.

- Fixed identifier naming: `v0`, `v1`, ... (by `resultValueId`).
- Fixed indentation: 2 spaces.
- Fixed block ordering: by `EntryPc` (enforced by `RecompilerIrProgram`).
- Fixed operation ordering: by position within block.
- No timestamps, GUIDs, random names, paths, or environment-dependent values.
- Helper function emitted in fixed order before block functions.

## Unsupported IR

Generator rejects (returns `Success=false` with machine-readable diagnostic):

- IR that fails `RecompilerIrValidator.Validate()`.
- Undefined `RecompilerIrOperationKind` values.
- Undefined `RecompilerIrTerminationReason` values.
- Empty programs (`UNSUPPORTED_EMPTY_PROGRAM`).
- Multi-block programs (`UNSUPPORTED_MULTI_BLOCK_PROGRAM`; Phase 3A is
  single-block only).

Generator never silently produces partial source for invalid IR.
