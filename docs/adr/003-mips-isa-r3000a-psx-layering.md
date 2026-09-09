# ADR-003: MIPS ISA / R3000A / PSX Specification Layering

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

When documenting the PSX CPU, mixing the MIPS ISA, the R3000A implementation, and PSX-specific behavior creates problems for future Recompiler and Debugger work. For example, the architectural MIPS ISA specification may differ from behavior observed on actual PlayStation hardware.

## Decision

Manage the specification as three separate layers.

### Layer 1: MIPS ISA

General MIPS I ISA behavior shared by R2000/R3000/R4000-class implementations.

- 32-bit instructions
- Three instruction formats: R, I, and J
- 32 GPRs
- Branch delay slot
- Load delay slot
- 32-bit address space
- Little-endian operation

### Layer 2: R3000A

R3000A-specific implementation details.

- Five-stage pipeline
- 4 KB instruction cache
- 1 KB data cache
- CP0 (System Control Coprocessor)
- COP2 (GTE)
- Exception vectors selected by the BEV bit
- Debug registers such as DCIC, BPC, and BDA

### Layer 3: PSX

Behavior specific to PlayStation hardware, based on hardware verification.

- Memory map
  - KUSEG: 0x00000000 - 0x7FFFFFFF
  - KSEG0: 0x80000000 - 0x9FFFFFFF (cached)
  - KSEG1: 0xA0000000 - 0xBFFFFFFF (uncached)
  - KSEG2: 0xC0000000 - 0xFFFFFFFF
- COP0 registers and PSX-specific values
- Exception vector at 80000080h
- GPU (COP2) commands
- BIOS calls
- DMA
- Termination conditions

### Why the Separation Matters

```text
MIPS ISA:  "ADD rd, rs, rt performs signed addition and raises an exception on overflow."
R3000A:    "ADD is encoded as an R-type instruction with opcode=0x00 and funct=0x20."
PSX:       "An ADD overflow exception is recorded as Ov (0Ch) in the COP0 CAUSE register."
```

## Consequences

- Documentation for each layer can be updated independently.
- Changes or clarifications to the MIPS ISA layer do not implicitly redefine R3000A- or PSX-specific behavior.
- PSX-specific behavior is explicitly separated from generic MIPS ISA behavior.
- Tests can be designed independently for each layer.
