# Recompiler Host Code Generation (Phase 3A–3C)

Deterministic C source generation from validated #206/#264 IR. Generates
self-contained, compilable C source with fixed-width types and well-defined
arithmetic — no UB, no host-width assumptions. Supports GPR arithmetic (Phase
3A), memory access (Phase 3B), comparisons (Phase 3C), and explicit control
flow (Phase 3C).

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
  void* core;
} RecompilerState;
```

- `gpr[0]` is always 0 on entry (caller must ensure).
- `hi`, `lo`, `pc` are present for ABI stability; initialized to 0 in usage.
- `termination_reason`: written by the generated block on exit; 0 = Success;
  nonzero = `RecompilerIrTerminationReason` byte value cast to `int32_t`.
- `next_pc`: set on Success exit; on Branch, sets the taken or fallthrough
  target; on Jump/Call, sets the target address.
- `core`: opaque pointer passed to memory helper functions. The runtime
  provides the implementation; the codegen never dereferences it.

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
int32_t recompiler_dispatch(RecompilerState* state, uint32_t budget);
```

A budgeted sequential dispatcher. It selects the block function whose entry PC
matches `state->pc`, executes it, stops on a non-Success termination, and
refuses to retire more than `budget` instructions (reporting
`RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED`). A PC that matches no block after
at least one step means the straight-line program fell off the end (normal
completion); a PC that matches no block on the first step is reported as
`RECOMPILER_REASON_UNSUPPORTED_IR`.

### Entry / exit behavior

- Entry: caller initializes `gpr[0] = 0`; the dispatcher sets `state->pc` to
  `state->next_pc` after each retired block via the sequential program counter.
- Exit: returns termination reason and writes it to `state->termination_reason`;
  on Success, also sets `state->next_pc`.

### GPR access

- Read: `state->gpr[i]`
- Write: `state->gpr[i] = value`
- `$zero` invariant preserved: generator never emits `WriteGpr` to `gpr[0]`.

### Memory access (Phase 3B)

Block functions call extern memory helpers for guest memory access. Address
translation, alignment, endianness, and bounds checking are the runtime's
responsibility.

```c
extern uint8_t  recompiler_read_mem8(void* core, uint32_t address);
extern uint16_t recompiler_read_mem16(void* core, uint32_t address);
extern uint32_t recompiler_read_mem32(void* core, uint32_t address);
extern void     recompiler_write_mem8(void* core, uint32_t address, uint8_t value);
extern void     recompiler_write_mem16(void* core, uint32_t address, uint16_t value);
extern void     recompiler_write_mem32(void* core, uint32_t address, uint32_t value);
```

- Narrow loads zero-extend to `uint32_t`.
- Narrow stores write only the specified width (little-endian).
- The `core` pointer is `state->core`.

### Comparisons (Phase 3C)

- `CompareEqual`: `uint32_t v = (a == b) ? 1u : 0u;`
- `CompareNotEqual`: `uint32_t v = (a != b) ? 1u : 0u;`

### Control flow (Phase 3C)

The exit of each block carries an explicit flow transition:

- **Sequential**: `state->next_pc = <nextPc>;` (same as Phase 3A).
- **Branch**: `if (cond != 0u) { state->next_pc = <taken>; } else { state->next_pc = <fallthrough>; }`
- **Jump**: `state->next_pc = <target>;`
- **Call**: `state->next_pc = <callee_target>;` (the return address is an
  architectural GPR write the lowering emits).

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
- Duplicate result value ids (`DUPLICATE_RESULT_VALUE_ID`).
- Operation kinds outside the Phase 3A–3C subset
  (`UNSUPPORTED_OPERATION_KIND`).
- Exit flow kinds other than `Sequential`, `Branch`, `Jump`, and `Call`
  (`UNSUPPORTED_FLOW_KIND`). The `Return` flow kind is additionally rejected
  by the IR validator in `RecompilerIrValidator`, since it cannot carry a
  register-held target as a static address.

Generator never silently produces partial source for invalid IR.
