# R3000A Instruction Formats

The MIPS I ISA has three instruction formats. All instructions are fixed-length 32-bit words.

## R-Type (Register)

Used for register-to-register operations.

```
 31  26 25  21 20  16 15  11 10   6 5    0
+------+-----+-----+-----+------+------+------+
| opcode|  rs |  rt |  rd | shamt | funct|
| 6 bit |5 bit|5 bit|5 bit|5 bit |6 bit |
+------+-----+-----+-----+------+------+------+
```

| Field | Bits | Description |
|-------|------|-------------|
| opcode | 31:26 | 0x00 (SPECIAL) |
| rs | 25:21 | Source register 1 |
| rt | 20:16 | Source register 2 / target |
| rd | 15:11 | Destination register |
| shamt | 10:6 | Shift amount |
| funct | 5:0 | Function code (identifies the instruction) |

## I-Type (Immediate)

Used for immediate operations, loads/stores, and branches.

```
 31  26 25  21 20  16 15                0
+------+-----+-----+-------------------+
| opcode|  rs |  rt |    immediate      |
| 6 bit |5 bit|5 bit|     16 bit        |
+------+-----+-----+-------------------+
```

| Field | Bits | Description |
|-------|------|-------------|
| opcode | 31:26 | Opcode |
| rs | 25:21 | Base register (source) |
| rt | 20:16 | Target register (destination) |
| immediate | 15:0 | 16-bit immediate |

### Immediate Handling

- **Arithmetic operations** (ADDI, ADDIU, SLTI, SLTIU): sign-extended
- **Logical operations** (ANDI, ORI, XORI): zero-extended
- **Loads/stores**: sign-extended offset
- **Branches**: sign-extended, then shifted left by 2 bits (relative address)

## J-Type (Jump)

Used for jump instructions.

```
 31  26 25                               0
+------+---------------------------------+
| opcode|         instr_index            |
| 6 bit |           26 bit               |
+------+---------------------------------+
```

| Field | Bits | Description |
|-------|------|-------------|
| opcode | 31:26 | Opcode |
| instr_index | 25:0 | 26-bit jump index |

### Target Address Calculation

```
target = ((PC + 4) & 0xF0000000) | (instr_index << 2)
```

- (PC + 4): address of the delay-slot instruction (next PC)
- Upper 4 bits: taken from the upper 4 bits of the delay-slot address
- instr_index: 26-bit index shifted left by 2 bits

## Coprocessor Format

Used for COP instructions (COP0, COP2, etc.).

```
 31  26 25  21 20  16 15               0
+------+-----+-----+-------------------+
| opcode|  rs |  rt |       cofun       |
| 6 bit |5 bit|5 bit|     16 bit        |
+------+-----+-----+-------------------+
```

| Field | Bits | Description |
|-------|------|-------------|
| opcode | 31:26 | COP number (01-11) |
| rs | 25:21 | Function field within COPz |
| rt | 20:16 | Register selector |
| cofun | 15:0 | Coprocessor-specific function |

## Encoding Note

The PSX R3000A is little-endian. Take care when handling byte order.

```
Memory address:  A+0  A+1  A+2  A+3
Value:           LSB  ...  ...  MSB
```
