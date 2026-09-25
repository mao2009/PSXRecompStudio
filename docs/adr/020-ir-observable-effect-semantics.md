# ADR-020: IR Observable Side-Effect Semantics for MMIO, Runtime Transfers, and Indirect Control Flow

- Status: Accepted (amended 2026-09-24 by Issue #411 — see below)
- Date: 2026-09-17
- Issue: #411

## Context

The recompiler IR (`RecompilerContract.cs`, `MipsToIrLowerer.cs`,
`RecompilerHostCodeGen.cs`) already lowers PS1 MIPS into a small block-based IR
with an explicit termination/flow model. Issue #411 asked whether that IR
machine-readably distinguishes six concepts, or conflates them with ordinary
RAM access / direct control flow:

1. ordinary guest-memory read/write
2. MMIO / volatile device read/write
3. runtime / BIOS transfer
4. indirect / unresolved control-flow transfer
5. an architectural-exception-producing operation
6. unsupported / explicit lowering failure

An audit of the current implementation (not the issue text alone) found:

- **Already explicit**: indirect control flow. `MipsToIrLowerer` lowers JR/JALR
  to `RecompilerIrTerminationReason.UnresolvedIndirectFlow` — never a resolved
  `Jump`/`Call`/`Branch` flow — because the target lives in a register and is
  only known at execution time. The PS1 BIOS A0/B0/C0 call convention is
  reached exclusively through JALR/JR (`BiosJumpTables`,
  `RecompilerInterpreterExecutor.TryDispatchBiosVector`, and the generated
  host's `host_transfer` hook in `RecompilerHostCodeGen`), so a BIOS/runtime
  transfer is, at lowering time, indistinguishable from any other indirect
  call — and the IR already refuses to guess at one. Resolving *which* vector
  family a live PC hit is a host/runtime-layer responsibility (ADR-014), not
  an IR-lowering one, and stays there.
- **Already explicit (fail-closed)**: unsupported / exception-producing
  operations. `MipsToIrLowerer.TryEmitInstruction` only lowers a fixed opcode
  subset; `ADD`/`SUB`, `SYSCALL`, `BREAK`, `MFC0`/`MTC0`/`RFE`, and the
  COP1/COP2/COP3 opcodes still fall through to the `default` arm and return
  `MipsToIrLoweringResult.Unsupported(...)` — an explicit lowering failure,
  never a silent approximation. `ADDI` is the supported exception-producing
  exception: it lowers to the explicit `AddSigned` operation, which terminates
  with `RecompilerIrTerminationReason.Exception` before the destination write
  when signed overflow is detected.
- **Implicit / missing**: ordinary RAM vs. MMIO/device memory. `Load8/16/32`
  and `Store8/16/32` carried no address-space classification at all. The
  generated C backend calls the same `recompiler_read_mem*`/
  `recompiler_write_mem*` host hooks for every access; real MMIO routing
  already exists, but one layer down, in `MemoryBus`/`Ps1MemoryMap` and the
  DMA/timer/interrupt adapters, entirely outside the IR's own representation.
- **Currently impossible to know statically, for real lowered code**: a real
  PS1 load/store effective address is always `base register + offset`
  (`MipsToIrLowerer.EmitEffectiveAddress`) — a runtime value. The lowering
  stage cannot prove an address is RAM or a device register at
  IR-construction time for the overwhelming majority of guest code. Any effect
  model must not force a guess here.

## Decision

Add a minimal effect-classification field to the existing memory operations,
rather than new IR node kinds, and document the semantics the rest of the IR
already carries.

1. **`RecompilerIrMemoryEffectKind`** (`Unknown = 0`, `Ordinary`, `Device`) is
   added to `RecompilerContract.cs`. `Unknown` is the zero/default value —
   fail-closed by construction: any caller that does not explicitly classify
   an address gets `Unknown`, never `Ordinary`.
2. **`RecompilerIrOperation.MemoryEffect`** is a new optional trailing
   constructor parameter (default `Unknown`), preserving every existing call
   site byte-for-byte. It is meaningful only for `Load8/16/32`/
   `Store8/16/32`; `RecompilerIrValidator` rejects a defined-but-wrong value
   (`InvalidMemoryAccess`) on a memory operation and rejects any non-`Unknown`
   value on a non-memory operation, so the field cannot be attached to the
   wrong operation kind or left in a garbage state.
3. **`RecompilerIrMemoryEffectClassifier.Classify(uint guestVirtualAddress)`**
   reuses the existing `Ps1AddressTranslation.TryTranslate` (KUSEG/KSEG0/
   KSEG1) and `Ps1MemoryMap.ClassifyRegion` (RAM / BIOS / hardware-register
   window / unmapped) rather than re-deriving PS1 memory-map knowledge. RAM
   and BIOS ROM classify as `Ordinary` (no device-visible effect); the
   hardware-register window classifies as `Device`; an untranslatable or
   unmapped address classifies as `Unknown` — never guessed to be `Ordinary`.
4. **`MipsToIrLowerer` is unchanged.** Because a real base+offset address is a
   runtime value, its lowering leaves every `Load`/`Store` at the `Unknown`
   default, which is the architecturally correct answer, not an omission.
   `RecompilerIrMemoryEffectClassifier` exists for a caller that *does* know
   the address ahead of time (tests building IR directly over a synthetic,
   statically-known address; a future lowering stage that can prove a
   constant effective address).
5. **`RecompilerHostCodeGen` is unchanged.** It does not need
   `MemoryEffect` to emit correct code: real RAM/MMIO routing already happens
   one layer below the generated block, in the host's
   `recompiler_read_mem*`/`recompiler_write_mem*` implementation
   (`MemoryBus`/`Ps1MemoryMap`), and that boundary is untouched by this
   change.
6. **One explicit trapping arithmetic operation, no optimizer.** `AddSigned`
   is added to the existing operation enum rather than aliasing `ADDI` to the
   wrapping `Add` used by `ADDU`/`ADDIU`. Its consumer contract is to stop the
   current block with `Exception` before later operations when the signed
   overflow predicate is true. No optimizer exists in this codebase today, so
   there is nothing to make effect-aware; the contract is fixed in the IR and
   locked by tests so a future optimizer cannot ignore it.

## Consequences

### Positive

- A `Load`/`Store` operation can now carry a machine-readable answer to "is
  this ordinary memory or a device register" wherever that answer is known,
  without inventing a parallel operation-kind hierarchy.
- The fail-closed default (`Unknown`) means adding this field changes no
  existing program's meaning: every program lowered before this change, and
  every program `MipsToIrLowerer` lowers after it, still round-trips through
  serialization and codegen identically once the (compatible, trailing,
  defaulted) new field is accounted for.
- A future optimizer that wants to reorder, CSE, or eliminate a dead store
  now has a real signal to consult (`Device` means "never"; `Ordinary` means
  only "no device-visible effect" — normal memory dependency, alias, and
  liveness proof are still required before touching it), and the validator
  prevents that signal from being attached to the wrong operation.
- Indirect flow, BIOS/runtime transfer, and exception-producing/unsupported
  operations are formalized in tests and this ADR without touching working
  code, keeping the change minimal.

### Costs / constraints

- `MipsToIrLowerer` still cannot classify a real load/store's address (this
  is an architectural fact, not a gap this change closes); a future lowering
  stage that wants `Ordinary`/`Device` classification for real code needs
  either constant-address proof (e.g. an unindexed `LUI`/`ORI`-built pointer)
  or a points-to-style analysis — out of scope here.
- No optimizer exists to consume `MemoryEffect` yet; its value today is
  representational and test-locked, not yet load-bearing for a real
  optimization pass.

## Alternatives Considered

### A dedicated `MmioLoad`/`MmioStore` operation kind

Rejected: doubles the operation surface for every width (6 new kinds for 6
existing ones) and requires `RecompilerHostCodeGen` and every consumer to
handle two operation families that mean almost the same thing. A
classification field on the existing operation is the smaller, more
reusable change (also the issue's own explicit preference: reuse what
already represents the concept).

### Model runtime/BIOS transfer as a new `RecompilerIrFlowKind`

Rejected: at lowering time a BIOS call is not distinguishable from any other
register-indirect call — the vector address is a runtime register value. A
new flow kind would either have to guess (wrong) or degrade to exactly what
`UnresolvedIndirectFlow` already means. The real distinction is made later,
by the host/runtime layer that owns BIOS semantics under ADR-014, which is
where it already lives.

### Have codegen consult `MemoryEffect` to call a separate MMIO helper

Rejected for this change: real MMIO routing is already correctly handled one
layer down (`MemoryBus`), so branching in generated C on `MemoryEffect` would
duplicate that routing without adding correctness, and would touch the
generated-C contract that #411 asks to preserve. Left for a future change if
and when a concrete reason (e.g. an optimizer, or a codegen fast path for
proven-ordinary access) needs it.

### Add alignment/exception-effect encoding to `Load`/`Store`

Rejected for this change: alignment faults and their EPC/BD details remain
outside the current lowered IR operation surface. `ADDI` is handled by the
separate `AddSigned` operation because it is a reachable arithmetic blocker and
its minimum required contract is an explicit `Exception` termination with no
destination write; broader exception metadata for every IR operation remains a
separate design concern.

## Amendment (2026-09-24, Issue #411): audit refresh and closing coverage

This amendment makes no new decision. It fixes audit facts in the Context
section that later changes made stale, and it records the coverage that closes
Issue #411.

- **BREAK is no longer an unsupported opcode.** Since Issue #481 it lowers to an
  `Exception` exit carrying `RecompilerExceptionState` (Excode `0x09`, EPC, BD).
  That includes the delay-slot case, where the owning transfer's flow is
  suppressed. `ADD`/`SUB`, `SYSCALL`, `MFC0`/`MTC0`/`RFE`, and COP1–3 still fail
  lowering explicitly. `ADDI` still lowers to the trapping `AddSigned`.
- **Scratchpad counts as `Ordinary`.** `RecompilerIrMemoryEffectClassifier`
  maps RAM, scratchpad, and BIOS ROM to `Ordinary`. Decision point 3 names only
  RAM and BIOS ROM. Scratchpad is plain data memory with no device-visible
  effect, so the code is correct and this amendment aligns the text with it.
- **Stop categories are identified by existing machine-readable fields.** No
  new diagnostic code was needed. The category → termination reason → title
  diagnostic mapping is tabulated in
  `docs/development/recompiler-ir-contract.md` ("Observable effect categories").
  A differential mismatch in stop category is reported by `RecompilerStateDiff`
  as the stable `termination` field path.
- **Closing regression coverage** (`RecompilerIrMemoryEffectTests`):
  - an MMIO read / write / read of the same register keeps three separate host
    hook calls in IR order after codegen, so there is no read CSE and no
    reordering;
  - codegen rejects an undefined `MemoryEffect` (`IR_VALIDATION_FAILED`, no
    source emitted);
  - every defined effect kind is emitted through the runtime memory hook, never
    as a direct RAM access;
  - the differential diff localizes a stop-category mismatch to `termination`,
    deterministically;
  - `memoryEffect` is part of the deterministic IR serialization.

## Related ADRs

- ADR-003 (MIPS ISA / R3000A / PSX Layering) — the layering this change stays
  inside: Domain-only, no title-specific behavior.
- ADR-004 (Branch / Load-Delay Modeling) — the delay-slot fusion this change
  does not touch.
- ADR-014 (BIOS HLE Calls Cross a Shared Runtime Contract) — owns the
  BIOS/runtime transfer semantics this ADR defers to rather than duplicates.
- ADR-016 (Generated-Host Execution Budget Semantics) — the generated-host
  dispatch contract `RecompilerHostCodeGen` implements, left unmodified here.
