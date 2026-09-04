# Recompiler IR / CPU Semantic Contract

Status: Stable
Authority: SSOT
Related Issues: #205, #206, #207, #208, #211

## Scope

The first vertical slice executes a validated, backend-agnostic IR block. An IR
value is a fixed-width 32-bit guest value; operation order is architectural
side-effect order. Within each block, input values must refer to a result
defined by an earlier operation; there is no external input-value namespace.
The IR is not SSA and does not encode a host language.

Blocks are ordered by entry PC and end in an explicit exit. A successful exit
provides the next PC (or an explicit control-flow transition); unsupported,
exception, and budget exits do not provide one. PC ownership belongs to the
execution boundary, not a backend.

GPR reads observe `GPR[0] == 0`. A GPR write to register zero is invalid. HI,
LO, PC, load-delay state, exception state, termination reason, and ordered
memory observations are compared through `RecompilerStateSnapshot`.

## Memory operations

Memory load and store are explicit IR operations on 32-bit guest addresses,
never host pointers:
`Load8` / `Load16` / `Load32` produce a result from an address operand;
`Store8` / `Store16` / `Store32` write a value operand to an address operand and
produce no result. In both cases input A is the address and input B, on a store,
is the value. A narrower access occupies the low bits of the 32-bit IR value —
zero-extended on a load, truncated on a store — so signedness stays off the
operation surface: a sign-extending guest load is expressed by the lowering as a
`ShiftLeftLogical` / `ShiftRightArithmetic` pair. Translation (including KSEG),
alignment, little-endian
access, signedness, read/write ordering, MMIO policy, and the resolved memory
image belong to the memory/runtime contract, not the IR or host backend. The
ordered memory observations compared through `RecompilerStateSnapshot` describe
guest-visible access, independent of these operations' lowering.

## Control flow

A block terminates in a `RecompilerIrExit` that may carry a `RecompilerIrFlow`
describing an explicit control-flow transition:

- `Sequential` — falls through to the exit's next PC (the existing success
  relation).
- `Branch` — a conditional branch. Condition is a value produced earlier in the
  block (nonzero = take `Target`); the exit's next PC is the not-taken
  fall-through successor.
- `Jump` — an unconditional jump to `Target`; the exit must not provide a next
  PC.
- `Call` / `Return` — reserved extension points for later stages. They are a
  deliberate part of the contract (so lowering and backends can plan for them)
  but are rejected by the validator until a stage defines their semantics.

Branches and jumps are explicit; a backend must not hide or synthesize
fall-through, delay-slot, or call/return behavior. Delay slots and JAL PC+8
remain lowering/execution responsibilities. `CompareEqual` / `CompareNotEqual`
operations produce the 0/1 condition values consumed by a `Branch` flow.

## Functions and metadata

A `RecompilerIrFunction` groups the basic blocks of a function under an entry
PC and may carry PS1/MIPS-scoped `RecompilerIrMetadataEntry` values. Function
blocks are a grouping view over the blocks of a `RecompilerIrProgram`; the
program remains the SSOT for block ordering and uniqueness. Metadata is a
generic typed key/value mechanism (key plus a single `uint` or `string` value)
so lowering can record PS1/MIPS specifics (for example endianness or
address-space region) without leaking them into the generic IR operation
surface.

## Validation and fail-fast

The validator rejects, with a machine-readable diagnostic, any program that:
uses an undefined operation or flow kind; mis-shapes a memory, compare, shift,
or existing arithmetic operation; writes GPR[0]; leaves a branch condition
undefined or a successor missing; places a flow on a non-success exit; uses a
reserved flow; duplicates a function entry PC; or references a function block
that is not in the program. The same discipline that rejects unsupported
behavior extends to these structures: invalid state or operands fail fast rather
than degrade.

Unsupported behavior is a diagnostic/termination result, never an implicit NOP
or interpreter fallback. Canonical serialization uses fixed property order,
camelCase, LF, UTF-8 without BOM, and no time, path, machine, or object identity.
Enum fields at the contract boundary must contain defined members.

Issue boundaries: #207 lowers decoder output into this contract; #208 consumes
it without changing guest semantics; #211 compares snapshots; #209 composes
the bounded end-to-end pipeline. The memory, control-flow, function, and
metadata surfaces above establish the stable contract that a future MIPS-to-IR
lowering stage builds on.
