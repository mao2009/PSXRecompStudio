# ADR-002: Machine-Readable Instruction Definition (YAML)

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

PSX R3000A CPU instructions must eventually be consumed by the Decoder, Interpreter, Recompiler, test generator, Debugger, and MCP integrations. Human-readable specifications and machine-readable definitions therefore need to be separated.

## Decision

Manage instruction definitions in YAML format at `config/cpu/r3000a-instructions.yaml`.

### YAML Structure

```yaml
# Metadata
meta:
  version: "1.0"
  description: "PSX R3000A CPU Instruction Set Definitions"
  references:
    - "MIPS R3000 Hardware Manual"
    - "PSX-SPX (psx-spx.consoledev.net)"
    - "IDT R30xx Family Software Reference Manual"

# Instruction definitions
instructions:
  - name: ADD
    opcode: 0x00
    funct: 0x20
    format: R
    category: arithmetic
    operands:
      - type: register
        name: rd
        bits: [15, 11]
      - type: register
        name: rs
        bits: [25, 21]
      - type: register
        name: rt
        bits: [20, 16]
    semantics: |
      temp = GPR[rs] + GPR[rt]
      if overflow(temp) then
        trap(OV)
      else
        GPR[rd] = temp[31:0]
    flags:
      overflow: true
      signed: true
    delay_slot: false
    exceptions:
      - Ov
    references:
      - "MIPS R3000A, ADD instruction"
```

### Design Policy

1. **Decoder**: Identify instructions from opcode + funct + format.
2. **Interpreter**: Generate execution logic from semantics.
3. **Recompiler**: Generate IR from operands + semantics.
4. **Test generator**: Generate test code from test_cases.
5. **Debugger**: Disassemble from name + operands.
6. **MCP**: Make all fields available for reference.

### Extensibility

- Support future instruction additions.
- Support PSX-specific instructions such as COP2/GTE.
- Support title-specific instruction differences if required.

## Consequences

- The YAML schema can later be validated with JSON Schema.
- Tests can be generated automatically from YAML.
- The MCP server can consume the YAML directly.
