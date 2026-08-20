# R3000A Instruction Set

PSX R3000A で使用可能な全命令の一覧。

## Instruction Categories

### Arithmetic Instructions

| Mnemonic | Format | Opcode/Funct | Description | Overflow |
|----------|--------|--------------|-------------|----------|
| ADD | R | 0x00/0x20 | rd = rs + rt (signed) | Yes |
| ADDU | R | 0x00/0x21 | rd = rs + rt (unsigned) | No |
| ADDI | I | 0x08 | rt = rs + imm (signed) | Yes |
| ADDIU | I | 0x09 | rt = rs + imm (unsigned) | No |
| SUB | R | 0x00/0x22 | rd = rs - rt (signed) | Yes |
| SUBU | R | 0x00/0x23 | rd = rs - rt (unsigned) | No |
| SLT | R | 0x00/0x2A | rd = (rs < rt) ? 1 : 0 (signed) | No |
| SLTU | R | 0x00/0x2B | rd = (rs < rt) ? 1 : 0 (unsigned) | No |
| SLTI | I | 0x0A | rt = (rs < imm) ? 1 : 0 (signed) | No |
| SLTIU | I | 0x0B | rt = (rs < imm) ? 1 : 0 (unsigned) | No |

### Logical Instructions

| Mnemonic | Format | Opcode/Funct | Description |
|----------|--------|--------------|-------------|
| AND | R | 0x00/0x24 | rd = rs & rt |
| OR | R | 0x00/0x25 | rd = rs \| rt |
| XOR | R | 0x00/0x26 | rd = rs ^ rt |
| NOR | R | 0x00/0x27 | rd = ~(rs \| rt) |
| ANDI | I | 0x0C | rt = rs & (0x0000 imm) |
| ORI | I | 0x0D | rt = rs \| (0x0000 imm) |
| XORI | I | 0x0E | rt = rs ^ (0x0000 imm) |
| LUI | I | 0x0F | rt = imm << 16 |

### Shift Instructions

| Mnemonic | Format | Opcode/Funct | Description |
|----------|--------|--------------|-------------|
| SLL | R | 0x00/0x00 | rd = rt << shamt |
| SRL | R | 0x00/0x02 | rd = rt >> shamt (logical) |
| SRA | R | 0x00/0x03 | rd = rt >> shamt (arithmetic) |
| SLLV | R | 0x00/0x04 | rd = rt << rs[4:0] |
| SRLV | R | 0x00/0x06 | rd = rt >> rs[4:0] (logical) |
| SRAV | R | 0x00/0x07 | rd = rt >> rs[4:0] (arithmetic) |

### Multiply / Divide Instructions

| Mnemonic | Format | Opcode/Funct | Description |
|----------|--------|--------------|-------------|
| MULT | R | 0x00/0x18 | HI:LO = rs * rt (signed) |
| MULTU | R | 0x00/0x19 | HI:LO = rs * rt (unsigned) |
| DIV | R | 0x00/0x1A | LO = rs / rt, HI = rs % rt (signed) |
| DIVU | R | 0x00/0x1B | LO = rs / rt, HI = rs % rt (unsigned) |

### Move From/To HI/LO

| Mnemonic | Format | Opcode/Funct | Description |
|----------|--------|--------------|-------------|
| MFHI | R | 0x00/0x10 | rd = HI |
| MTHI | R | 0x00/0x11 | HI = rs |
| MFLO | R | 0x00/0x12 | rd = LO |
| MTLO | R | 0x00/0x13 | LO = rs |

### Load Instructions

| Mnemonic | Format | Opcode | Description |
|----------|--------|--------|-------------|
| LB | I | 0x20 | rt = sign_extend(memory[base+offset]) |
| LBU | I | 0x24 | rt = zero_extend(memory[base+offset]) |
| LH | I | 0x21 | rt = sign_extend(memory[base+offset:hword]) |
| LHU | I | 0x25 | rt = zero_extend(memory[base+offset:hword]) |
| LW | I | 0x23 | rt = memory[base+offset:word] |
| LWL | I | 0x22 | rt = load_word_left(base+offset) |
| LWR | I | 0x26 | rt = load_word_right(base+offset) |

### Store Instructions

| Mnemonic | Format | Opcode | Description |
|----------|--------|--------|-------------|
| SB | I | 0x28 | memory[base+offset] = rt[7:0] |
| SH | I | 0x29 | memory[base+offset:hword] = rt[15:0] |
| SW | I | 0x2B | memory[base+offset:word] = rt |
| SWL | I | 0x2A | store_word_left(base+offset, rt) |
| SWR | I | 0x2E | store_word_right(base+offset, rt) |

### Jump Instructions

| Mnemonic | Format | Opcode/Funct | Description | Delay Slot |
|----------|--------|--------------|-------------|------------|
| J | J | 0x02 | Jump to target | Yes |
| JAL | J | 0x03 | Jump and link (ra = PC+8) | Yes |
| JR | R | 0x00/0x08 | Jump to rs | Yes |
| JALR | R | 0x00/0x09 | Jump and link to rs (rd = PC+8) | Yes |

### Branch Instructions

| Mnemonic | Format | Opcode/Funct | Condition | Delay Slot |
|----------|--------|--------------|-----------|------------|
| BEQ | I | 0x04 | rs == rt | Yes |
| BNE | I | 0x05 | rs != rt | Yes |
| BLEZ | I | 0x06 | rs <= 0 (signed) | Yes |
| BGTZ | I | 0x07 | rs > 0 (signed) | Yes |
| BLTZ | REGIMM | 0x01/rt=0x00 | rs < 0 (signed) | Yes |
| BGEZ | REGIMM | 0x01/rt=0x01 | rs >= 0 (signed) | Yes |
| BLTZAL | REGIMM | 0x01/rt=0x10 | rs < 0, ra = PC+8 | Yes |
| BGEZAL | REGIMM | 0x01/rt=0x11 | rs >= 0, ra = PC+8 | Yes |

### Special Instructions

| Mnemonic | Format | Opcode/Funct | Description |
|----------|--------|--------------|-------------|
| SYSCALL | R | 0x00/0x0C | システムコール例外 |
| BREAK | R | 0x00/0x0D | ブレークポイント例外 |

### Coprocessor Instructions

| Mnemonic | Format | Opcode | Description |
|----------|--------|--------|-------------|
| MFC0 | COP | 0x10/0x00 | rt = COP0[rd] |
| MTC0 | COP | 0x10/0x04 | COP0[rd] = rt |
| RFE | COP | 0x10/0x10 | 例外から復帰 |
| LWC2 | I | 0x32 | CP2レジスタにロード (GTE) |
| SWC2 | I | 0x3A | CP2レジスタからストア (GTE) |

## Instruction Encoding Table

### Primary Opcode (bits 31:26)

```
 0  SPECIAL  REGIMM  J       JAL     BEQ     BNE     BLEZ    BGTZ
 1  ADDI     ADDIU   SLTI    SLTIU   ANDI    ORI     XORI    LUI
 2  COP0     COP1    COP2    COP3    *       *       *       *
 3  *        *       *       *       *       *       *       *
 4  LB       LH      LWL     LW      LBU     LHU     LWR     *
 5  SB       SH      SWL     SW      *       *       SWR     *
 6  *        LWC1    LWC2    *       *       *       *       *
 7  *        SWC1    SWC2    *       *       *       *       *
```

### SPECIAL Function (bits 5:0, when opcode=0x00)

```
 0  SLL      *       SRL     SRA     SLLV    *       SRLV    SRAV
 1  JR       JALR    *       *       SYSCALL BREAK   *       *
 2  MFHI     MTHI    MFLO    MTLO    *       *       *       *
 3  MULT     MULTU   DIV     DIVU    *       *       *       *
 4  ADD      ADDU    SUB     SUBU    AND     OR      XOR     NOR
 5  *        *       SLT     SLTU    *       *       *       *
 6  *        *       *       *       *       *       *       *
 7  *        *       *       *       *       *       *       *
```

### REGIMM rt field (bits 20:16, when opcode=0x01)

```
 0  BLTZ     BGEZ    *       *       *       *       *       *
 2  BLTZAL   BGEZAL  *       *       *       *       *       *
```
