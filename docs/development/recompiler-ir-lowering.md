# MIPS-to-IR Lowering

## Overview

`MipsToIrLowerer` translates decoded R3000a instructions into the #206 IR blocks.
It maps the pure-GPR subset (Phase 2A), the base+offset memory subset, and the
direct control-flow subset, and produces machine-readable diagnostics for
everything else.

The lowerer maps MIPS decoder output into #206 IR types without changing the
contract's shape. `RecompilerContract.cs` (the #206 SSOT) keeps its operation,
flow, exit, and validator surface; this stage only documents the semantics it
relies on (see [Load width and signedness](#load-width-and-signedness)).

## Supported Instruction Subset

### GPR arithmetic (Phase 2A)

| MIPS Opcode | IR Mapping | Notes |
|---|---|---|
| NOP (SLL r0,r0,0) | `Nop` | Detected as SLL with all-zero operands |
| ADDU rd,rs,rt | `ReadGpr`×2 → `Add` → `WriteGpr` | 32-bit wrap semantics |
| SUBU rd,rs,rt | `ReadGpr`×2 → `Subtract` → `WriteGpr` | 32-bit wrap semantics |
| ADDIU rt,rs,imm | `ReadGpr` + `Constant(sign-ext imm)` → `Add` → `WriteGpr` | 16-bit imm sign-extended |
| LUI rt,imm | `Constant(imm << 16)` → `WriteGpr` | Direct constant mapping |
| AND / OR / XOR / NOR rd,rs,rt | `ReadGpr`×2 → op → `WriteGpr` | |
| SLL / SRL / SRA rd,rt,shamt | `ReadGpr` → shift → `WriteGpr` | shamt 0..31 |

### Memory

Every load and store first materialises the guest 32-bit effective address as
`ReadGpr(base)` + `Constant(sign-extended offset)` → `Add`.

| MIPS Opcode | IR Mapping | Notes |
|---|---|---|
| LW rt,off(base) | address → `Load32` → `WriteGpr` | |
| LBU rt,off(base) | address → `Load8` → `WriteGpr` | The load's zero extension is the result |
| LHU rt,off(base) | address → `Load16` → `WriteGpr` | |
| LB rt,off(base) | address → `Load8` → `ShiftLeftLogical 24` → `ShiftRightArithmetic 24` → `WriteGpr` | |
| LH rt,off(base) | address → `Load16` → `ShiftLeftLogical 16` → `ShiftRightArithmetic 16` → `WriteGpr` | |
| SB / SH / SW rt,off(base) | address, `ReadGpr(rt)` → `Store8` / `Store16` / `Store32` | Address is input A, value is input B |

### Control flow

A control-transfer instruction and the instruction in its branch delay slot lower
to a **single** block — see [Delay slots](#delay-slots).

| MIPS Opcode | IR Mapping | Notes |
|---|---|---|
| BEQ rs,rt,off | `ReadGpr`×2 → `CompareEqual` → delay slot → exit with `Branch` flow | Taken target from `R3000aBranchSemantics`; not-taken successor is the exit's next PC (`pc + 8`) |
| BNE rs,rt,off | as BEQ with `CompareNotEqual` | |
| J target | delay slot → exit with `Jump` flow | Target from `R3000aJumpSemantics`; a jump exit carries no next PC |
| JR rs | delay slot → exit with `UnresolvedIndirectFlow` | See [Register-indirect control flow](#register-indirect-control-flow) |

## API Surface

- `MipsToIrLowerer.Lower(R3000aInstruction, uint entryPc)` → `MipsToIrLoweringResult`
  — one straight-line instruction, one block. An instruction that owns a delay
  slot is **rejected** here.
- `MipsToIrLowerer.LowerControlTransfer(R3000aInstruction control, uint entryPc, R3000aInstruction delaySlot)`
  → `MipsToIrLoweringResult` — the control instruction fused with its delay slot.
- `MipsToIrLowerer.LowerProgram(IReadOnlyList<(R3000aInstruction, uint)>)` →
  `RecompilerIrProgram` — a linear stream; a control transfer consumes the next
  entry as its delay slot, so that pair yields one block.

## Delay slots

ADR-004 and `docs/cpu/pipeline.md` are the SSOT: the instruction after a branch
or jump **always** retires, whether or not the transfer is taken. An IR block
ends in a transfer, so the delay slot cannot be a separate block reached *after*
that transfer — it would then be skipped on the taken path. The lowering
therefore fuses the pair:

```text
block(entryPc = branch PC)
  1. the transfer's operand reads and its condition
  2. the delay-slot instruction's operations
  3. exit: flow (Branch/Jump) or an unresolved termination
```

Ordering the condition **before** the delay slot is what makes a delay slot that
overwrites a condition register harmless, exactly as on hardware. The not-taken
successor is `branchPc + 8` — the instruction after the delay slot, never the
delay slot itself.

Rejected, never approximated:

- a control transfer with no delay-slot instruction (`LowerProgram` throws);
- a delay-slot entry that is not at `pc + 4` (`LowerProgram` throws);
- a control-transfer instruction **inside** a delay slot — UNPREDICTABLE on
  MIPS I (`InvalidFlow`);
- a **load** inside a delay slot: its load-delay shadow lands on a successor
  reached through the transfer, which this stage cannot check
  (`InvalidMemoryAccess`);
- a delay-slot instruction outside the lowered subset (diagnostic propagated).

Straight-line instructions keep the Phase 2A relation exactly: a success exit
with a next PC and **no** explicit `Sequential` flow, so an existing Phase 2A
program still serializes identically.

## Load delay

The R3000A load delay (ADR-004) is real and the native interpreter implements
it: a load's target register keeps its previous value for exactly one
instruction. The IR operation surface has no delayed-write representation, and
adding one would push CPU pipeline state into a generic IR, so this stage
instead:

- commits a load with an immediate `WriteGpr`, which is **equivalent** whenever
  the load-delay-slot instruction does not read the target register; and
- **fails fast** in `LowerProgram` when it does, with a diagnostic naming both
  PCs (`InvalidMemoryAccess`).

Cases that stay equivalent and are accepted:

| Case | Why it is equivalent |
|---|---|
| Delay-slot instruction reads other registers | The pending value is never observed |
| Delay-slot instruction writes the same register | An immediate write cancels the pending load on hardware too |
| Delay-slot instruction is another load to the same register | The later load wins on both sides |
| Load targets `$zero` | GPR[0] is immutable |
| Load is the last instruction of the stream | No in-stream observer |

Representing the observable case is future work; see
[Deferred](#deferred).

## Load width and signedness

The #206 contract keeps signedness out of the IR: `Load8` / `Load16` produce the
accessed value zero-extended into the 32-bit IR value, and MIPS sign extension is
expressed with the generic shift operations (`ShiftLeftLogical` then
`ShiftRightArithmetic` by 24 or 16). LBU/LHU therefore use the load result
directly and LB/LH add the shift pair. No new operation kind, flag, or width
field was needed — the contract change for this stage is documentation only.

Stores mirror this: `Store8` / `Store16` write the low bits of the value operand,
so no truncation operation is emitted.

## Register-indirect control flow

`RecompilerIrFlow.Target` is a static address, so it cannot carry a target held
in a register. JR therefore lowers to a block that retires its delay slot and
then terminates with `RecompilerIrTerminationReason.UnresolvedIndirectFlow`. That
is an honest statement of the frontier — control leaves the lowered program here
— rather than a synthesized transfer. The block does not record which register
held the target; a stage that resolves indirect flow will.

## Deferred

| Deferred | Reason |
|---|---|
| JAL, JALR | Calls. `RecompilerIrFlowKind.Call` is a reserved extension point; lowering a call to a plain `Jump` would erase the return relation and mis-model reachability. Reported as `ReservedFlow`. |
| BLEZ, BGTZ, BLTZ, BGEZ, BLTZAL, BGEZAL | Compare-with-zero branches need signed comparison operations the contract does not have yet. |
| LWL / LWR, SWL / SWR | Unaligned pair access with the special ADR-004 load-delay pairing. |
| ADDI / SUB / ADD, SLT / SLTI / SLTU / SLTIU, ANDI / ORI / XORI, SLLV / SRLV / SRAV, MULT / DIV / HI / LO | Not yet lowered; each returns `InvalidOperationShape`. |
| COP0 / COP2, SYSCALL / BREAK | Coprocessor and exception semantics. |
| Observable load delay | Needs either a delayed-write representation or load/consumer fusion. |
| Misalignment and address exceptions | Alignment and translation belong to the memory/runtime contract, not the IR. |
| SSA, optimization, constant folding | Out of scope for lowering. |

## $zero Handling

- Reads of GPR 0 map to `ReadGpr(0)` (allowed by the #206 validator), including
  as a load/store base register and as a branch operand.
- Writes to GPR 0 are suppressed — `WriteGpr` with register 0 is never emitted,
  so a load into `$zero` still performs the access and discards the value.

## Unsupported Instructions

Any opcode outside the sets above returns `MipsToIrLoweringResult.Unsupported`
with a `RecompilerIrDiagnosticCode` and descriptive message; `LowerProgram`
turns that into an `InvalidOperationException` naming the PC, opcode, and
diagnostic. No silent fallback, no generic exceptions.

## Host code generation boundary

`RecompilerHostCodeGen` emits the Phase 3A GPR subset only. Because lowering can
now produce memory operations and explicit flows, the generator **rejects** them
(`UNSUPPORTED_OPERATION_KIND` / `UNSUPPORTED_FLOW_KIND`) instead of emitting a
block that silently drops the access or the transfer. Extending the backend is a
separate stage.
