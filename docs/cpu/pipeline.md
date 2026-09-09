# R3000A Pipeline and Delay Slots

## Pipeline

The PSX R3000A uses a five-stage pipeline.

```
IF (Instruction Fetch)
 ↓
ID (Instruction Decode)
 ↓
EX (Execute)
 ↓
MEM (Memory Access)
 ↓
WB (Write Back)
```

## Branch Delay Slot

The instruction immediately following a branch instruction always executes, regardless of whether the branch is taken.

### Behavior

```
1000: BEQ $1, $2, target
1004: ADD $3, $4, $5    ← Delay Slot (always executes)
1008: ...               ← If branch is not taken
100C: target: ...       ← If branch is taken
```

### Branch Instructions

| Instruction | Condition | Delay Slot |
|-------------|-----------|------------|
| J | Unconditional | Yes |
| JAL | Unconditional | Yes |
| JR | Unconditional | Yes |
| JALR | Unconditional | Yes |
| BEQ | rs == rt | Yes |
| BNE | rs != rt | Yes |
| BLEZ | rs <= 0 | Yes |
| BGTZ | rs > 0 | Yes |
| BLTZ | rs < 0 | Yes |
| BGEZ | rs >= 0 | Yes |
| BLTZAL | rs < 0, ra=PC+8 | Yes |
| BGEZAL | rs >= 0, ra=PC+8 | Yes |

### Branch in a Delay Slot

Behavior when a branch instruction is placed in a delay slot is **UNPREDICTABLE**. On real PSX hardware, the following behavior is observed:

```
BEQ $1, $2, target1
BEQ $3, $4, target2    ← Delay Slot (branch in delay slot)
```

In this case, the inner branch executes first, after which the outer branch is applied.

In this repository's implementation (the ADR-005 PC model), the inner branch executes its own delay slot before the outer branch is applied:

```asm
1000: BEQ $1, $2, target1
1004: BEQ $3, $4, target2    ← Outer delay slot + inner branch
1008: NOP                    ← Inner branch delay slot (always executes)
100C: ...
1010: target1: ...           ← Outer branch target
```

After the inner branch at 1004 and its delay slot at 1008 execute, the outer branch is applied:

- Outer branch taken: PC = target1
- Outer branch not taken: PC = 1008 + 4 = 100C (the instruction immediately after the inner branch's delay slot)

The inner branch target (`target2`) is ignored.

### Exception in Delay Slot

If an exception occurs in a delay slot:

1. CAUSE.BD = 1
2. EPC = address of the branch instruction, not the delay-slot instruction
3. The exception handler checks BD and returns to EPC+8

## Load Delay Slot

The instruction immediately following a load does not observe the loaded value.

### Behavior

```
LW $1, 0($2)      ← Load $1 from memory
ADD $3, $1, $4    ← Delay Slot: $1 still has the old value
NOP                ← The new value becomes visible in $1
ADD $5, $1, $6    ← Uses the new value of $1
```

### Special LWL/LWR Behavior

LWL/LWR can read the preceding load result only when they form a **consecutive LWL/LWR pair**:

```
# Valid pair (LWR → LWL order)
LWR $1, 3($2)     ← In load delay
LWL $1, 0($2)     ← Can read the LWR result in $1

# Valid pair (LWL → LWR order)
LWL $1, 0($2)     ← In load delay
LWR $1, 3($2)     ← Can read the LWL result in $1
```

A normal load delay remains after the second instruction. If the instructions are not a consecutive pair, the normal load-delay rule applies.

### Importance for Testing

PSX BIOS code relies on load-delay behavior. In particular:

```asm
lw   $31, 0($sp)    # Load return address
jal  function        # Delay slot: $31 still has the old value
```

In this case, `jal` stores the correct return address (PC+8) in $31.

## Pipeline Hazards

### Data Hazard

```asm
ADD $1, $2, $3
SUB $4, $1, $5    ← The result in $1 is not yet available
```

The R3000A does not provide hardware hazard resolution. The assembler inserts NOPs.

### Control Hazard

Hazards caused by branch instructions. Resolved through delay slots.

## Cache

### Instruction Cache (4 KB)

- 1 line = 16 bytes
- Physical-address tags
- 80% hit rate

### Data Cache (1 KB)

- 1 line = 4 bytes (1 word)
- Write-through
- Physical-address tags
- 4-deep write buffer

On PSX, the data cache is normally used as scratchpad memory.
