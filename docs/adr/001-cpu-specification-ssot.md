# ADR-001: CPU Specification SSOT Management

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

The PSX R3000A CPU specification forms the foundation for future CPU implementation, the Recompiler, the Debugger, and MCP integrations. Multiple sources must be consulted, including MIPS ISA manuals, PSX-SPX, PlayStation developer documentation, and test results, but these sources differ in accuracy and level of detail.

## Decision

Manage the CPU specification as the SSOT under `docs/cpu/`, separated using the following structure.

### File Structure

```text
docs/cpu/
├── r3000a.md              # R3000A overview
├── registers.md           # Register set
├── instruction-format.md  # Instruction formats (R/I/J)
├── instruction-set.md     # Instruction set (complete instruction list)
├── exceptions.md          # Exception handling
├── cop0.md                # COP0 registers
├── memory.md              # Memory map
├── pipeline.md            # Pipeline and delay slots
└── test-specification.md  # Test specification
```

### Machine-Readable Instruction Definitions

```text
config/cpu/
└── r3000a-instructions.yaml  # YAML definitions for all instructions
```

### ADRs

```text
docs/adr/
├── 001-cpu-specification-ssot.md        # This ADR
├── 002-instruction-definition-yaml.md   # YAML format for instruction definitions
├── 003-mips-isa-r3000a-psx-layering.md # Specification layering
├── 004-branch-load-delay-modeling.md    # Delay-slot modeling
└── 005-pc-model.md                      # PC update modeling
```

### Separation Principles

1. **MIPS ISA**: General MIPS I ISA specification
2. **R3000A**: R3000A-specific implementation specification
3. **PSX**: Actual behavior on PlayStation hardware, including COP0 behavior, memory map, and exception vectors

### Reference Priority

1. This project's `docs/cpu/` documentation (SSOT)
2. PSX-SPX (`psx-spx.consoledev.net`)
3. MIPS R3000 Hardware Manual
4. IDT R30xx Family Software Reference Manual
5. Other emulator implementations (reference only)

## Consequences

- Changes to the specification must update the SSOT first.
- When external documentation conflicts with observed behavior, verified behavior on real PlayStation hardware takes precedence.
- MCP and AI tooling use the SSOT as their reference source.
