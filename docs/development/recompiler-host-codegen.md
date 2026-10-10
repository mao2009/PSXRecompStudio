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
struct RecompilerState;
typedef int32_t (*recompiler_host_transfer_fn)(struct RecompilerState*);

typedef struct RecompilerState {
  uint32_t gpr[32];
  uint32_t hi;
  uint32_t lo;
  uint32_t pc;
  int32_t termination_reason;
  uint32_t next_pc;
  uint32_t exception_raised;
  uint32_t exception_code;
  uint32_t exception_fault_pc;
  uint32_t exception_in_delay_slot;
  void* core;
  recompiler_host_transfer_fn host_transfer;
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
- `host_transfer`: optional host-owned control-transfer hook. When the
  dispatcher reaches a PC that no generated block owns, it offers the current
  state to this callback before classifying the PC as unsupported. A return
  value of 0 means the host claimed the transfer and has set
  `termination_reason`/`next_pc`; nonzero means it was not claimed. A null hook
  preserves the normal unsupported-PC behavior. This is used by the runtime
  boundary for transfers such as BIOS A0/B0/C0 trampoline vectors without
  embedding BIOS-specific knowledge in generated code.

### Function signature

```c
static int32_t recompiler_block_0x<entryPc>(RecompilerState* state);
```

- Takes a pointer to `RecompilerState`.
- On every exit, writes `state->termination_reason` (0 on Success, the reason
  code otherwise).
- Returns 0 on Success (with `state->next_pc` set), or the termination reason
  code as a non-zero `int32_t`.

### Exception exits (Issue #481)

A block whose exit carries a statically-baked
`RecompilerExceptionState` (BREAK) also writes the four `exception_*` fields
*before* writing `termination_reason` and returning:

```c
state->exception_raised = 1u;
state->exception_code = 9;                 /* Bp, Excode 0x09 */
state->exception_fault_pc = <faultPc>;     /* EPC: faulting PC, or owning branch PC in a delay slot */
state->exception_in_delay_slot = <0|1>;    /* CAUSE.BD */
state->termination_reason = 6;             /* RecompilerIrTerminationReason.Exception */
return (int32_t)6;
```

Exits without an exception resolution (e.g. the runtime AddSigned overflow exit)
leave the `exception_*` fields untouched — the dispatcher and runners must not
read them unless `termination_reason == 6` and `exception_raised == 1`.

### Firmware mode and COP0 (Issue #732)

`state->guest_exceptions != 0` (the artifact's `--guest-exceptions` flag) runs a
firmware such as OpenBIOS: the dispatcher delivers a SYSCALL/BREAK exit to the
guest's own exception vector through `recompiler_exception_entry` — EPC, CAUSE
Excode/BD (CE cleared), the SR KU/IE push, and `0xBFC00180`/`0x80000080` by
SR.BEV — clears the `exception_*` fields and continues there; the trap retires
nothing. The hardware INT entry of the artifact driver uses the same helper. With
the flag clear (every zero-initialised state) the pre-existing HLE behavior
(`host_syscall`, or an `Exception` stop) is unchanged.

MFC0/MTC0 read and write SR/CAUSE/EPC through the existing `cop0_sr`/`cop0_cause`/
`cop0_epc` fields and every other COP0 register through `cop0_other[32]`
(`recompiler_cop0_slot`, `recompiler_cop0_write`); every store is guarded by
`recompiler_store_isolated` (SR.IsC). All constants come from `RecompilerCop0`.

A firmware ROM is built with `ReachableProgramBuilder.BuildFirmwareImage(loadAddress,
words, entry, roots)`, which reports `NativeInstructionCount` and the static
`FallbackTargets` outside the image. Code without a block — RAM code the firmware
copies at run time, a KSEG0 ROM alias, any indirect target not given as a root —
reaches `host_transfer` and, with mixed execution, the interpreter fallback
(Issue #693), which counts it as fallback, never as native.

Running a firmware (`RecompiledHostExecutionEngine(..., guestFirmware: true)`):

- An image whose load address translates into the BIOS ROM window
  (physical `0x1FC00000`, 512 KiB) is loaded into the artifact's own
  read-only `artifact_rom`. Loads through KUSEG/KSEG0/KSEG1 are served there,
  stores are dropped (mask ROM; the native interpreter's memory still accepts
  them — a known divergence that OpenBIOS never exercises). Without a ROM image
  the window is relayed and refused exactly as before (Issue #678).
- No BIOS HLE Runtime may be attached; the Runtime's BIOS vector and kernel
  exception-handler routes are skipped. Every PC without a block (direct or
  indirect, in or out of the image) goes to the fallback interpreter, which is
  attached with the ROM written into its core and permits RAM/ROM execution.
  It returns at any block entry with a clean pipeline, also from inside a guest
  exception handler (a firmware SYSCALL handler returns to `EPC + 4`).
- `NativeRetiredInstructions` (the sum of `RHOST_RETIRED` reports) and
  `MixedFallbackEvidence.FallbackInstructions` are the native/fallback split.

`recompiler_dispatch` selects the block with one `switch (state->pc)`; a
default case is the unknown-PC boundary. A chain of comparisons cost time
proportional to the block count per dispatch (7449 blocks for OpenBIOS).

### Dispatch function

```c
int32_t recompiler_dispatch(RecompilerState* state, uint32_t budget);
```

A budgeted sequential dispatcher. It selects the block function whose entry PC
matches `state->pc` (a `switch`), executes it, stops on a non-Success termination, and
refuses to retire more than `budget` instructions (reporting
`RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED`). When a PC matches no generated
block, the dispatcher first calls the optional `state->host_transfer` hook. If
the host claims the PC, execution follows the termination/continuation state set
by that hook. If no host claims it, a PC reached after at least one step means
the straight-line program fell off the end (normal completion); an unclaimed PC
on the first step is reported as `RECOMPILER_REASON_UNSUPPORTED_IR`.

The generated `RECOMPILER_REASON_*` constants used by the dispatcher are emitted
from the live `RecompilerIrTerminationReason` enum values rather than duplicated
numeric literals, keeping dispatcher and per-block termination reporting on the
same contract.

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
- Every IR memory operation emits exactly one helper call, in IR order,
  whatever its `MemoryEffect` (`Unknown`/`Ordinary`/`Device`). The block has
  no RAM fast path. RAM/MMIO routing belongs to the host helper (`MemoryBus`),
  so an `Unknown` or `Device` access can never fall back to a plain RAM access
  (ADR-020). An undefined `MemoryEffect` is rejected via
  `IR_VALIDATION_FAILED`.

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

### Firmware COP0 service

Firmware artifacts keep SR/CAUSE/EPC in their existing state fields. Other COP0
registers use the optional `host_cop0` callback and the additive
`RHOST_COP0_ACCESS register write value` service (reply `V value` or `X`). The
shared native core owns these registers across native/fallback transitions;
standalone generated code with no callback retains its local register storage.
Outstanding retired time is flushed before access. See ADR-025 for validation,
compatibility and ownership; this service does not change transfer or fallback
protocol versions.

### Generated aligned address traps (Issue #749)

Aligned CPU memory primitives with fault-site provenance check alignment before
RAM/MMIO access and before SR.IsC store suppression. Failed LH/LHU/LW raise AdEL;
failed SH/SW raise AdES. Virtual BadVAddr uses the existing COP0 owner callback;
EPC/BD, CAUSE, SR stack and vector selection use the existing exception entry
helper. Firmware continues into its guest handler and RFE/JR; standalone code
stops with the existing raised exception snapshot.

A memory fault may complete a pending load from the preceding instruction,
including an observer whose successful write would otherwise cancel it. Only
the completed prefix is credited, using `partial_retired` so a downstream
instruction-boundary reporter cannot double-charge it. This field resets at
every dispatch iteration, including interpreter fallback transfers. No new host
protocol, runtime compilation, MMIO route, or device-time correction is added.
