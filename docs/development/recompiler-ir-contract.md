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
provides the next PC; unsupported, exception, and budget exits do not provide
one. PC ownership belongs to the execution boundary, not a backend.

GPR reads observe `GPR[0] == 0`. A GPR write to register zero is invalid. HI,
LO, PC, load-delay state, exception state, termination reason, and ordered
memory observations are compared through `RecompilerStateSnapshot`.

Memory is a future phase boundary: addresses are 32-bit guest values, never
host pointers. Translation (including KSEG), alignment, little-endian access,
signedness, read/write ordering, and MMIO policy belong to the memory/runtime
contract, not the IR or host backend.

Control flow is likewise explicit. Branches, jumps, delay slots, and JAL
PC+8 are lowering/execution responsibilities; a backend must not hide delay
slots as an optimization. Unresolved indirect flow is an explicit exit.

Unsupported behavior is a diagnostic/termination result, never an implicit NOP
or interpreter fallback. Canonical serialization uses fixed property order,
camelCase, LF, UTF-8 without BOM, and no time, path, machine, or object identity.
Enum fields at the contract boundary must contain defined members.

Issue boundaries: #207 lowers decoder output into this contract; #208 consumes
it without changing guest semantics; #211 compares snapshots; #209 composes
the bounded end-to-end pipeline.
