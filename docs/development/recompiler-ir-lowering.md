# MIPS-to-IR Lowering

## Overview

`MipsToIrLowerer` translates decoded R3000a instructions into the #206 IR blocks.
It maps the pure-GPR subset (Phase 2A), the base+offset memory subset with the
R3000A load delay (Phase 2B), and the branch/jump/call subset with branch delay
slots (Phase 2C), and produces machine-readable diagnostics for everything else.

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
| JAL target | `Constant(pc + 8)` → `WriteGpr($ra)` → delay slot → exit with `Call` flow | Link value from `R3000aLinkSemantics`; the exit's next PC is the return address |
| JR rs | delay slot → exit with `UnresolvedIndirectFlow` | See [Register-indirect control flow](#register-indirect-control-flow) |
| JALR rd,rs | `ReadGpr(rs)` → `Constant(pc + 8)` → `WriteGpr(rd)` → delay slot → exit with `UnresolvedIndirectFlow` | Target read before the link write, so `JALR rd,rd` keeps the pre-link target |

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

The same ordering carries the link write of a call: `PSXCpu::ExecJal` links
before the transfer applies, so `$ra` is written ahead of the delay slot and a
delay-slot instruction reads the new link value. This is what makes the BIOS
pattern in `docs/cpu/pipeline.md` — `lw $ra, 0($sp)` followed by `jal` — end with
the linked address rather than the loaded one.

Rejected, never approximated:

- a control transfer with no delay-slot instruction (`LowerProgram` throws);
- a delay-slot entry that is not at `pc + 4` (`LowerProgram` throws);
- a control-transfer instruction **inside** a delay slot — UNPREDICTABLE on
  MIPS I (`InvalidFlow`);
- a **load** inside a delay slot: its load-delay shadow lands on a successor
  reached through the transfer, which this stage cannot check
  (`InvalidMemoryAccess`);
- a delay-slot instruction outside the lowered subset (diagnostic propagated);
- a resolved transfer target that is lowered but lands **inside** a fused block
  rather than on its entry — a branch into a delay slot or into a load-delay
  slot. There is no block to enter, and an execution boundary would read that as
  "control left the program", so `LowerProgram` throws instead. A target outside
  the lowered stream is a legitimate exit and is left alone.

Straight-line instructions keep the Phase 2A relation exactly: a success exit
with a next PC and **no** explicit `Sequential` flow, so an existing Phase 2A
program still serializes identically.

## Load delay

The R3000A load delay (ADR-004) is real and the native interpreter implements
it: a load's target register keeps its previous value for exactly one
instruction. `PSXCpu::UpdateLoadDelay` (`src/PSXRecomp.Native/src/psx_cpu.cpp`)
is the SSOT — the pending value commits at the **retirement point of the
following instruction**, and a write to the same register during that
instruction cancels it.

The IR operation surface has no delayed-write representation, and adding one
would push CPU pipeline state into a generic IR. It does not need one: the #206
contract already defines operation order as architectural side-effect order, so
the delay is expressed by **where the commit is placed**, exactly as the branch
delay slot is expressed by fusing the transfer with its delay slot.

When the load-delay slot instruction reads the target register, `LowerProgram`
fuses the pair into one block entered at the load's PC:

```text
block(entryPc = load PC)
  1. the load's effective address, access and sign extension — but no commit
  2. the load-delay-slot instruction's operations (its reads see the old value)
  3. the commit: WriteGpr(target, loaded)
  exit: success, next PC = loadPc + 8
```

When that instruction is itself a control transfer, its own delay slot joins the
block and the commit sits **between** them — the transfer reads the pre-load
value, the branch delay slot (which retires later) reads the loaded one:

```text
block(entryPc = load PC)
  1. the load's access
  2. the transfer's operand reads, condition and link write
  3. the commit
  4. the transfer's delay-slot operations
  exit: the transfer's flow
```

When the load-delay-slot instruction **writes** the load's target register, no
commit is emitted at all: on hardware that write cancels the pending load
(`PSXCpu::SetGPR` / `WriteRegDelayed`), so the instruction's own value is the
one that survives.

Cases where an immediate commit is already equivalent, and no fusion happens:

| Case | Why it is equivalent |
|---|---|
| Delay-slot instruction reads other registers | The pending value is never observed |
| Delay-slot instruction writes the same register without reading it | An immediate write cancels the pending load on hardware too |
| Load targets `$zero` | GPR[0] is immutable, so the access happens and the value is discarded |
| Load is the last instruction of the stream | No in-stream observer |

Rejected, never approximated:

- a **chained** load delay — the fused block's load-delay-slot instruction is
  itself a load whose own value is read by the instruction after the block. Its
  commit would have to land outside the fused block (`InvalidMemoryAccess`);
- a **load in a branch delay slot** — its shadow lands on a successor reached
  through the transfer, which this stage cannot check or represent
  (`InvalidMemoryAccess`).

### Load-delay state at termination

A load that ends the stream has no in-stream observer, so its commit is emitted
in its own block. On hardware the value is still pending at that point and
commits one instruction later. The two states differ only by that one retirement
step, which is why every differential run gives the interpreter one extra step
before the state is compared. This lowering never produces a program whose
`RecompilerStateSnapshot.LoadDelay` is pending; a stage that hands a partially
retired pipeline across a program boundary will need that state.

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
in a register. JR and JALR therefore lower to a block that retires its delay slot
and then terminates with `RecompilerIrTerminationReason.UnresolvedIndirectFlow`.
That is an honest statement of the frontier — control leaves the lowered program
here — rather than a synthesized transfer. JR emits no operation of its own; JALR
emits `ReadGpr(rs)` before its link write, because the link may target the same
register and the interpreter captures the target first (`PSXCpu::ExecJalr`).

`JR $ra` is **not** special-cased. A return-like flow would need
`RecompilerIrFlowKind.Return`, which stays reserved precisely because its target
is register-held; and treating any `JR $ra` as a return would be an analysis
claim, not a semantic one — the same encoding also implements tail calls and
computed jumps. It therefore lowers exactly like any other JR, and a program that
calls and returns is cross-checked against the interpreter up to the return
boundary (see `MipsToIrLoweringDifferentialTests.JalThenJrRa_ReturnsToTheLinkedAddress`).

Resolving a register-held target — and with it `Return` — needs a contract change
this stage deliberately does not make; see [Deferred](#deferred).

## Deferred

| Deferred | Reason |
|---|---|
| Register-held control-flow targets (JR / JALR resolution, `Return` flow) | `RecompilerIrFlow.Target` is a static address and has no value-id form. Expressing a runtime target needs a #206 contract change, which this stage deliberately does not make; the frontier stays explicit as `UnresolvedIndirectFlow`. |
| BLEZ, BGTZ, BLTZ, BGEZ, BLTZAL, BGEZAL | Compare-with-zero branches need signed comparison operations the contract does not have yet. |
| LWL / LWR, SWL / SWR | Unaligned pair access with the special ADR-004 load-delay pairing. |
| ADDI / SUB / ADD, SLT / SLTI / SLTU / SLTIU, ANDI / ORI / XORI, SLLV / SRLV / SRAV, MULT / DIV / HI / LO | Not yet lowered; each returns `InvalidOperationShape`. |
| COP0 / COP2, SYSCALL / BREAK | Coprocessor and exception semantics. |
| Chained load delay, and a load in a branch delay slot | Their commit points fall outside the fused block; both fail fast with `InvalidMemoryAccess`. |
| Pending load delay across a program boundary | `RecompilerStateSnapshot.LoadDelay` can carry it, but no IR operation queues one. |
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
now produce memory operations and explicit flows — including `Call` — the
generator **rejects** them (`UNSUPPORTED_OPERATION_KIND` / `UNSUPPORTED_FLOW_KIND`)
instead of emitting a block that silently drops the access or the transfer.
Extending the backend is a separate stage (#208).
