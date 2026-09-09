# ADR-004: Branch Delay Slot / Load Delay Slot Modeling

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

The PSX R3000A CPU is based on the MIPS I ISA and has both branch delay slots and load delay slots. These directly affect future Recompiler design. If they are not modeled correctly, compatibility with real PlayStation software will be lost.

## Decision

### Branch Delay Slot

**Specification**:
- The instruction immediately following a branch instruction (J, JAL, BEQ, BNE, BLEZ, BGTZ, BLTZ, BGEZ, BLTZAL, BGEZAL) is always executed, regardless of whether the branch is taken.
- JR/JALR delay slots behave the same way.
- If the instruction in the delay slot is itself a branch instruction, behavior is UNPREDICTABLE.

**Model**:

```text
Current PC: address of the branch instruction
Next PC: address of the branch instruction + 4 (delay-slot address)
Target PC: set when the branch condition is satisfied

Execution order:
1. Decode the branch instruction.
2. Evaluate the branch condition.
3. Execute the delay-slot instruction.
4. If the branch condition was satisfied, jump to the target PC.
5. Otherwise continue with PC = delay_slot_pc + 4.
```

**Implementation Policy**:

```cpp
// When executing a branch instruction
void execute_branch(uint32_t target, bool condition) {
    // Execute the delay slot.
    uint32_t delay_slot_pc = current_pc + 4;
    execute_instruction(delay_slot_pc);

    // Update the PC according to the branch condition.
    if (condition) {
        pc = target;
    } else {
        pc = delay_slot_pc + 4;
    }
}
```

### Load Delay Slot

**Specification**:
- The instruction immediately following a load instruction (LB, LBU, LH, LHU, LW, LWL, LWR) does not observe the load result.
- The load result has not yet been committed to the destination register at that point.
- Later MIPS revisions added hardware interlocks, but the R3000A (MIPS I) exposes this load delay.

**Model**:

```text
Current state:
- load_pending: bool
- load_target_reg: int
- load_value: uint32_t

When executing a load instruction:
1. load_pending = true
2. load_target_reg = rt
3. load_value = memory[base + offset]

When executing the next instruction:
1. If a pending load exists, its register write remains deferred.
2. A consecutive LWL/LWR pair may read the pending value (special hardware path).
3. Other instructions do not observe the pending value.
4. Clear load_pending.
```

**Special LWL/LWR Behavior**:
- LWL/LWR may read the immediately preceding load result only when they form a consecutive LWL/LWR pair.
- This applies only when LWL→LWR or LWR→LWL target the same register.
- The behavior is provided by a special hardware path.
- A normal load delay still remains after the second instruction.
- Tests must verify this behavior.

### BD Bit on Exceptions

**Specification**:
- If an exception occurs in a delay slot, BD (CAUSE bit 31) is set.
- EPC points to the branch instruction, not the delay-slot instruction.
- The exception handler checks BD:
  - BD=0: normal return. Return to EPC+4 with RFE + JR.
  - BD=1: the branch instruction and delay-slot instruction must be re-executed. After RFE, resume at the taken branch target or at EPC+8 when not taken.

## Consequences

- The Interpreter must model delay slots explicitly.
- The Recompiler must analyze blocks with delay slots included.
- The Debugger must support stepping through delay slots.
- Tests must comprehensively verify delay-slot behavior.
