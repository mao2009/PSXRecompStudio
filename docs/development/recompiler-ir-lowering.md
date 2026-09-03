# MIPS-to-IR Lowering (Phase 2A)

## Overview

`MipsToIrLowerer` translates decoded R3000a instructions into the #206 IR blocks.
It maps the pure-GPR subset and produces machine-readable diagnostics for unsupported opcodes.

## Supported Instruction Subset

| MIPS Opcode | IR Mapping | Notes |
|---|---|---|
| NOP (SLL r0,r0,0) | `Nop` | Detected as SLL with all-zero operands |
| ADDU rd,rs,rt | `ReadGpr`×2 → `Add` → `WriteGpr` | 32-bit wrap semantics |
| SUBU rd,rs,rt | `ReadGpr`×2 → `Subtract` → `WriteGpr` | 32-bit wrap semantics |
| ADDIU rt,rs,imm | `ReadGpr` + `Constant(sign-ext imm)` → `Add` → `WriteGpr` | 16-bit imm sign-extended |
| LUI rt,imm | `Constant(imm << 16)` → `WriteGpr` | Direct constant mapping |
| AND rd,rs,rt | `ReadGpr`×2 → `And` → `WriteGpr` | |
| OR rd,rs,rt | `ReadGpr`×2 → `Or` → `WriteGpr` | |
| XOR rd,rs,rt | `ReadGpr`×2 → `Xor` → `WriteGpr` | |
| NOR rd,rs,rt | `ReadGpr`×2 → `Nor` → `WriteGpr` | |
| SLL rd,rt,shamt | `ReadGpr` → `ShiftLeftLogical` → `WriteGpr` | shamt 0..31 |
| SRL rd,rt,shamt | `ReadGpr` → `ShiftRightLogical` → `WriteGpr` | shamt 0..31 |
| SRA rd,rt,shamt | `ReadGpr` → `ShiftRightArithmetic` → `WriteGpr` | shamt 0..31 |

## Unsupported Instructions

Any opcode outside the above set returns `MipsToIrLoweringResult.Unsupported` with a
`RecompilerIrDiagnosticCode` and descriptive message. No silent fallback, no generic exceptions.

## $zero Handling

- Reads of GPR 0 map to `ReadGpr(0)` (allowed by #206 validator).
- Writes to GPR 0 are suppressed — `WriteGpr` with register 0 is never emitted.

## API Surface

- `MipsToIrLowerer.Lower(R3000aInstruction, uint entryPc)` → `MipsToIrLoweringResult`
- `MipsToIrLowerer.LowerProgram(IReadOnlyList<(R3000aInstruction, uint)>)` → `RecompilerIrProgram`

## #206 Boundary

The lowerer maps MIPS decoder output into #206 IR types without modifying the contract.
RecompilerContract.cs (the #206 SSOT) is never changed by this phase.

## Deferred

- Phase 2B: memory (LW/SW), load delay
- Phase 2C: control flow (BEQ/BNE/J/JAL/JR), delay slots
- Optimization, SSA, constant folding, host codegen
