# R3000A Register Set

## General Purpose Registers (GPR)

32 32-bit general-purpose registers ($0 - $31).

| Register | ABI Name | Usage | Initial Value |
|----------|----------|-------|---------------|
| $0 | $zero | Constant 0 (always 0 in hardware) | 0 |
| $1 | $at | Assembler temporary | 0 |
| $2-$3 | $v0-$v1 | Function return values | 0 |
| $4-$7 | $a0-$a3 | Function arguments | 0 |
| $8-$15 | $t0-$t7 | Temporaries (not preserved by calling convention) | 0 |
| $16-$23 | $s0-$s7 | Saved temporaries | 0 |
| $24-$25 | $t8-$t9 | Temporaries (not preserved by calling convention) | 0 |
| $26-$27 | $k0-$k1 | Reserved for kernel use (exception handlers only) | 0 |
| $28 | $gp | Global pointer | 0 |
| $29 | $sp | Stack pointer | 0 |
| $30 | $fp | Frame pointer | 0 |
| $31 | $ra | Return address | 0 |

### Special behavior of $zero ($0)

- Always returns 0 in hardware
- Writes are ignored
- Required by the MIPS ISA

### Special behavior of $31 ($ra)

- JAL, JALR, BLTZAL, and BGEZAL automatically store the return address
- Can otherwise be read and written as a normal register

## Program Counter (PC)

32-bit program counter.

- Address of the currently executing instruction
- Instructions must be 4-byte aligned (the low 2 bits are always 0)
- Initial value: 0x00000000

## HI / LO Registers

Special registers that store multiplication and division results.

| Register | Usage | Initial Value |
|----------|-------|---------------|
| HI | Upper 32 bits of multiplication result; division remainder | 0 |
| LO | Lower 32 bits of multiplication result; division quotient | 0 |

### Multiply Instructions (MULT, MULTU)

```
HI:LO = GPR[rs] * GPR[rt]  (64-bit result)
```

- MULT: signed multiplication
- MULTU: unsigned multiplication
- The result is stored in HI (upper 32 bits) and LO (lower 32 bits)

### Divide Instructions (DIV, DIVU)

```
LO = GPR[rs] / GPR[rt]  (quotient)
HI = GPR[rs] % GPR[rt]  (remainder)
```

- DIV: signed division
- DIVU: unsigned division

### Division by Zero Behavior (PSX-specific)

| Instruction | Quotient when Divisor=0 | Remainder when Divisor=0 |
|-------------|--------------------------|---------------------------|
| DIV (dividend >= 0) | 0xFFFFFFFF (-1) | dividend |
| DIV (dividend < 0) | 0x00000001 (+1) | dividend |
| DIVU | 0xFFFFFFFF (-1) | dividend |

## Coprocessor Registers

### CP0 (System Control Coprocessor)

16 32-bit coprocessor registers. See [cop0.md](cop0.md) for details.

### CP2 (GTE - Geometry Transformation Engine)

Details will be covered in a future document.
