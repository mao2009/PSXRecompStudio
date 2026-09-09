# CPU Test Specification

Test design for the PSX R3000A CPU. Intended for use by future Interpreter/Recompiler implementations.

## Test Structure

### Layer 1: Decoder Tests

Verify instruction decoding accuracy.

### Layer 2: Instruction Tests

Verify the basic behavior of each instruction.

### Layer 3: Pipeline Tests

Verify delay slots, load delays, and exception handling.

### Layer 4: Integration Tests

Verify combinations of multiple instructions.

---

## Layer 1: Decoder Tests

### R-Type Decode

| Test ID | Input | Expected |
|---------|-------|----------|
| DEC-R-001 | opcode=0x00, funct=0x20 | ADD |
| DEC-R-002 | opcode=0x00, funct=0x21 | ADDU |
| DEC-R-003 | opcode=0x00, funct=0x24 | AND |
| DEC-R-004 | opcode=0x00, funct=0x00 | SLL |
| DEC-R-005 | opcode=0x00, funct=0x08 | JR |
| DEC-R-006 | opcode=0x00, funct=0x0C | SYSCALL |

### I-Type Decode

| Test ID | Input | Expected |
|---------|-------|----------|
| DEC-I-001 | opcode=0x08 | ADDI |
| DEC-I-002 | opcode=0x09 | ADDIU |
| DEC-I-003 | opcode=0x23 | LW |
| DEC-I-004 | opcode=0x2B | SW |
| DEC-I-005 | opcode=0x04 | BEQ |

### J-Type Decode

| Test ID | Input | Expected |
|---------|-------|----------|
| DEC-J-001 | opcode=0x02 | J |
| DEC-J-002 | opcode=0x03 | JAL |

---

## Layer 2: Instruction Tests

### Arithmetic Instructions

#### ADD

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADD-001 | Normal addition | rs=5, rt=3 | rd=8 |
| ADD-002 | Positive overflow | rs=0x7FFFFFFF, rt=1 | trap(Ov) |
| ADD-003 | Negative overflow | rs=0x80000000, rt=-1 | trap(Ov) |
| ADD-004 | Add zero | rs=0, rt=0 | rd=0 |
| ADD-005 | Negative addition | rs=-1, rt=-1 | rd=-2 |

#### ADDU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADDU-001 | Normal addition | rs=5, rt=3 | rd=8 |
| ADDU-002 | Overflow wraparound | rs=0xFFFFFFFF, rt=1 | rd=0 |
| ADDU-003 | Add zero | rs=0, rt=0 | rd=0 |

#### ADDI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADDI-001 | Normal addition | rs=5, imm=3 | rt=8 |
| ADDI-002 | Negative immediate | rs=10, imm=-5 | rt=5 |
| ADDI-003 | Overflow | rs=0x7FFFFFFF, imm=1 | trap(Ov) |

#### ADDIU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADDIU-001 | Normal addition | rs=5, imm=3 | rt=8 |
| ADDIU-002 | Overflow wraparound | rs=0xFFFFFFFF, imm=1 | rt=0 |

#### SUB

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SUB-001 | Normal subtraction | rs=10, rt=3 | rd=7 |
| SUB-002 | Negative result | rs=3, rt=10 | rd=-7 |
| SUB-003 | Overflow | rs=0x7FFFFFFF, rt=-1 | trap(Ov) |
| SUB-004 | Equal values | rs=5, rt=5 | rd=0 |

#### SUBU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SUBU-001 | Normal subtraction | rs=10, rt=3 | rd=7 |
| SUBU-002 | Overflow wraparound | rs=0, rt=1 | rd=0xFFFFFFFF |

#### SLT

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLT-001 | Less than | rs=3, rt=5 | rd=1 |
| SLT-002 | Greater than | rs=5, rt=3 | rd=0 |
| SLT-003 | Equal values | rs=5, rt=5 | rd=0 |
| SLT-004 | Negative value | rs=-1, rt=0 | rd=1 |
| SLT-005 | Signed comparison | rs=0xFFFFFFFF(-1), rt=1 | rd=1 |

#### SLTU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLTU-001 | Less than | rs=3, rt=5 | rd=1 |
| SLTU-002 | Greater than | rs=5, rt=3 | rd=0 |
| SLTU-003 | Unsigned comparison | rs=0xFFFFFFFF(very large), rt=1 | rd=0 |

### Logical Instructions

#### AND

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| AND-001 | Bitwise operation | rs=0xFF, rt=0x0F | rd=0x0F |
| AND-002 | Zero | rs=0xFF, rt=0x00 | rd=0x00 |
| AND-003 | All bits | rs=0xFF, rt=0xFF | rd=0xFF |

#### OR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| OR-001 | Bitwise operation | rs=0xF0, rt=0x0F | rd=0xFF |
| OR-002 | Zero | rs=0xFF, rt=0x00 | rd=0xFF |

#### XOR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| XOR-001 | Bitwise operation | rs=0xFF, rt=0x0F | rd=0xF0 |
| XOR-002 | Equal values | rs=0xFF, rt=0xFF | rd=0x00 |

#### NOR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| NOR-001 | Bitwise operation | rs=0xF0, rt=0x0F | rd=0x00 |
| NOR-002 | Zero | rs=0x00, rt=0x00 | rd=0xFFFFFFFF |

#### ANDI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ANDI-001 | Zero extension | rs=0xFF00, imm=0x0F | rt=0x0000 |
| ANDI-002 | Upper bits | rs=0xFFFF, imm=0xFF | rt=0x00FF |

#### ORI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ORI-001 | Zero extension | rs=0xF000, imm=0x0F | rt=0xF00F |

#### LUI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LUI-001 | Basic | imm=0x1234 | rt=0x12340000 |
| LUI-002 | Zero | imm=0x0000 | rt=0x00000000 |

### Shift Instructions

#### SLL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLL-001 | Shift left by 1 bit | rt=1, shamt=1 | rd=2 |
| SLL-002 | 31 bits | rt=1, shamt=31 | rd=0x80000000 |
| SLL-003 | shamt=0 | rt=0x1234, shamt=0 | rd=0x1234 |

#### SRL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRL-001 | Shift right by 1 bit | rt=2, shamt=1 | rd=1 |
| SRL-002 | No sign extension | rt=0x80000000, shamt=1 | rd=0x40000000 |

#### SRA

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRA-001 | Positive value | rt=4, shamt=1 | rd=2 |
| SRA-002 | Negative value (sign extension) | rt=0x80000000, shamt=1 | rd=0xC0000000 |

#### SLLV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLLV-001 | Register-specified shift | rt=1, rs=3 | rd=8 |

#### SRLV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRLV-001 | Register-specified shift | rt=8, rs=3 | rd=1 |

#### SRAV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRAV-001 | Negative value | rt=0x80000000, rs=1 | rd=0xC0000000 |

### Multiply / Divide Instructions

#### MULT

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MULT-001 | Positive × positive | rs=3, rt=5 | HI:LO=0x00000000:0x0000000F |
| MULT-002 | Negative × positive | rs=-1, rt=5 | HI:LO=0xFFFFFFFF:0xFFFFFFFB |
| MULT-003 | Zero | rs=0, rt=5 | HI:LO=0:0 |

#### MULTU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MULTU-001 | Normal | rs=3, rt=5 | HI:LO=0:15 |
| MULTU-002 | Large value | rs=0xFFFFFFFF, rt=2 | HI:LO=0x00000001:0xFFFFFFFE |

#### DIV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| DIV-001 | Normal | rs=10, rt=3 | LO=3, HI=1 |
| DIV-002 | Negative value | rs=-10, rt=3 | LO=-3, HI=-1 |
| DIV-003 | Division by zero (positive) | rs=5, rt=0 | LO=0xFFFFFFFF, HI=5 |
| DIV-004 | Division by zero (negative) | rs=-5, rt=0 | LO=0x00000001, HI=-5 |

#### DIVU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| DIVU-001 | Normal | rs=10, rt=3 | LO=3, HI=1 |
| DIVU-002 | Division by zero | rs=5, rt=0 | LO=0xFFFFFFFF, HI=5 |

#### MFHI / MTHI / MFLO / MTLO

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MFHI-001 | Read HI | HI=0x1234 | rd=0x1234 |
| MFLO-001 | Read LO | LO=0x5678 | rd=0x5678 |
| MTHI-001 | Write HI | rs=0xABCD | HI=0xABCD |
| MTLO-001 | Write LO | rs=0xEF01 | LO=0xEF01 |

### Load Instructions

#### LB / LBU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LB-001 | Positive byte | mem[addr]=0x7F | rt=0x0000007F |
| LB-002 | Negative byte | mem[addr]=0xFF | rt=0xFFFFFFFF |
| LBU-001 | Zero extension | mem[addr]=0xFF | rt=0x000000FF |

#### LH / LHU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LH-001 | Positive halfword | mem[addr:16]=0x7FFF | rt=0x00007FFF |
| LH-002 | Negative halfword | mem[addr:16]=0xFFFF | rt=0xFFFFFFFF |
| LHU-001 | Zero extension | mem[addr:16]=0xFFFF | rt=0x0000FFFF |

#### LW

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LW-001 | Normal load | mem[addr:32]=0x12345678 | rt=0x12345678 |

#### LWL / LWR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LWL-001 | byte_offset=0 | mem[addr]=0xAA | rt[31:24]=0xAA |
| LWL-002 | byte_offset=3 | mem[addr:3]=0xAA BB CC DD | rt=0xAABBCCDD |
| LWR-001 | byte_offset=0 | mem[addr]=0xDD | rt[7:0]=0xDD |
| LWR-002 | byte_offset=3 | mem[addr:3]=0xAA BB CC DD | rt=0xAABBCCDD |
| LWL-LWR-001 | Combination | mem[addr:3]=0xAA BB CC DD | rt=0xAABBCCDD |

### Store Instructions

#### SB

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SB-001 | Normal store | rt=0x12345678 | mem[addr]=0x78 |

#### SH

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SH-001 | Normal store | rt=0x12345678 | mem[addr:16]=0x5678 |

#### SW

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SW-001 | Normal store | rt=0x12345678 | mem[addr:32]=0x12345678 |

#### SWL / SWR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SWL-001 | byte_offset=0 | rt=0xAABBCCDD | mem[addr]=0xAA |
| SWL-002 | byte_offset=3 | rt=0xAABBCCDD | mem[addr:3]=0xAABBCCDD |
| SWR-001 | byte_offset=0 | rt=0xAABBCCDD | mem[addr]=0xDD |
| SWR-002 | byte_offset=3 | rt=0xAABBCCDD | mem[addr:3]=0xAABBCCDD |

### Jump Instructions

#### J

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| J-001 | Basic jump | instr_index=0x100 | PC=0x00000400 |
| J-002 | Preserve upper 4 bits | PC=0x80001000, instr_index=0x200 | PC=0x80000800 |

#### JAL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| JAL-001 | Basic jump and link | PC=0x1000, instr_index=0x100 | PC=0x00000400, $31=0x1008 |

#### JR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| JR-001 | Basic | rs=0x80001234 | PC=0x80001234 |

#### JALR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| JALR-001 | Basic | PC=0x1000, rs=0x80001234 | PC=0x80001234, $31=0x1008 |

### Branch Instructions

#### BEQ

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BEQ-001 | Branch taken | rs=5, rt=5 | PC=branch_target |
| BEQ-002 | Branch not taken | rs=5, rt=3 | PC=PC+8 |

#### BNE

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BNE-001 | Branch taken | rs=5, rt=3 | PC=branch_target |
| BNE-002 | Branch not taken | rs=5, rt=5 | PC=PC+8 |

#### BLEZ

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BLEZ-001 | Zero or less | rs=0 | PC=branch_target |
| BLEZ-002 | Negative value | rs=-1 | PC=branch_target |
| BLEZ-003 | Positive value | rs=1 | PC=PC+8 |

#### BGTZ

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BGTZ-001 | Positive value | rs=1 | PC=branch_target |
| BGTZ-002 | Zero | rs=0 | PC=PC+8 |

### Special Instructions

#### SYSCALL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SYSCALL-001 | Basic | - | trap(SyscallException) |

#### BREAK

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BREAK-001 | Basic | - | trap(BreakpointException) |

### Coprocessor Instructions

#### MFC0 / MTC0

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MFC0-001 | COP0 read | COP0[rd]=0x1234 | rt=0x1234 |
| MTC0-001 | COP0 write | rt=0x5678 | COP0[rd]=0x5678 |

---

## Layer 3: Pipeline Tests

### Branch Delay Slot

| Test ID | Description | Expected |
|---------|-------------|----------|
| DELAY-B-001 | Basic branch delay slot | Delay-slot instruction executes |
| DELAY-B-002 | Delay slot when branch is taken | Execute delay slot, then jump to target |
| DELAY-B-003 | Delay slot when branch is not taken | Execute delay slot, then continue to next instruction |
| DELAY-B-004 | Branch in a delay slot | UNPREDICTABLE |
| DELAY-B-005 | Exception in a delay slot | EPC=branch instruction address, BD=1 |

### Load Delay Slot

| Test ID | Description | Expected |
|---------|-------------|----------|
| DELAY-L-001 | Basic load delay | Instruction immediately after load sees the old value |
| DELAY-L-002 | Instruction after load delay | Second instruction sees the new value |
| DELAY-L-003 | Special consecutive LWL/LWR-pair circuitry | Pair can read the preceding load value |
| DELAY-L-004 | Load delay with JAL | Compatible with BIOS code |

### Exception in Delay Slot

| Test ID | Description | Expected |
|---------|-------------|----------|
| DELAY-E-001 | SYSCALL in delay slot | EPC=branch instruction, BD=1 |
| DELAY-E-002 | BREAK in delay slot | EPC=branch instruction, BD=1 |

---

## Layer 4: Integration Tests

### Common Patterns

| Test ID | Description | Expected |
|---------|-------------|----------|
| INT-001 | Function call (JAL + JR) | Correct return address |
| INT-002 | Conditional branch (BEQ + delay) | Correct branch target |
| INT-003 | Loop (BNE + delay) | Correct loop behavior |
| INT-004 | LUI + ORI (32-bit immediate) | Correct 32-bit value |
| INT-005 | Memory copy (LW/SW) | Correct data transfer |

### BIOS Compatibility

| Test ID | Description | Expected |
|---------|-------------|----------|
| BIOS-001 | BIOS startup sequence | Starts successfully |
| BIOS-002 | BIOS call | Correct call result |

---

## Test Execution Strategy

### Native C++ Tests

1. Decode tests for each instruction
2. Basic execution tests for each instruction
3. Delay-slot tests
4. Exception-handling tests

### C# Integration Tests

1. Instruction execution tests through P/Invoke
2. Register-state verification
3. Memory-access tests

### External Test Programs

- Amidog's psxtest_cpu.exe (future use)
