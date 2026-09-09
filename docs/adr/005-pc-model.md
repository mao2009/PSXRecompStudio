# ADR-005: PC Update Model

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

If PC updates on the PSX R3000A CPU are not modeled correctly, branch instructions, delay slots, and exception handling will behave incorrectly. PC management is especially important for Recompiler design.

## Decision

### PC State Definition

```text
PC state:
- pc: current PC (address of the instruction currently being decoded)
- next_pc: next PC (normally pc + 4)
- delay_slot_pc: address of the instruction in the delay slot (used only for branches)
```

### Normal PC Update

```text
1. Fetch instruction: instruction = memory[pc]
2. Decode and execute the instruction.
3. Update PC: pc = next_pc; next_pc = pc + 4
```

### Branch Instruction PC Update

```text
1. Fetch and decode the branch instruction.
2. Evaluate the branch condition.
3. delay_slot_pc = pc + 4
4. Fetch and execute the next instruction (the delay slot).
5. If the branch condition was satisfied:
   pc = target_address
   next_pc = pc + 4
6. Otherwise:
   pc = delay_slot_pc + 4
   next_pc = pc + 4
```

### Jump Instruction PC Update

```text
J/JAL instructions:
1. Fetch and decode the jump instruction.
2. target = ((pc + 4) & 0xF0000000) | (instr_index << 2)
3. Execute the delay slot.
4. pc = target
5. next_pc = pc + 4
```

### JR/JALR Instruction PC Update

```text
JR rs instruction:
1. Fetch and decode the JR instruction.
2. target = GPR[rs]
3. Execute the delay slot.
4. pc = target
5. next_pc = pc + 4
```

### PC Update on Exception

```text
1. Save the current PC to EPC.
2. Set the BD flag when the exception occurred in a delay slot.
3. Set the exception code in the CAUSE register.
4. pc = exception vector address (80000080h)
5. next_pc = pc + 4
```

### Exception Vectors

```text
BEV=0:
  Reset:         BFC00000h
  UTLB Miss:     80000000h
  COP0 Break:    80000040h
  General:       80000080h

BEV=1:
  Reset:         BFC00000h
  UTLB Miss:     BFC00100h
  COP0 Break:    BFC00140h
  General:       BFC00180h
```

### RFE (Return From Exception)

```text
RFE only pops the SR status stack. It does not restore the PC.

1. KUc←KUp, IEc←IEp (SR[1:0] ← SR[3:2])
2. KUp←KUo, IEp←IEo (SR[3:2] ← SR[5:4])
3. KUo/IEo (SR[5:4]) are not changed by RFE; real hardware continues reusing the oldest level.
4. Software explicitly restores the PC: read EPC into a GPR with MFC0 $k0, EPC, then jump to that address with JR $k0. Because exception entry is not a JAL, $ra cannot be used. The conventional sequence executes RFE in the JR delay slot:
   MFC0 $k0, EPC / JR $k0 / RFE
```

An earlier version described step 2 as clearing `SR[5:4] = 0` (clearing the oldest level), but that was incorrect. Real-hardware behavior documented by psx-spx and the implementation/tests from PR #193 (Issue #141) show that RFE leaves KUo/IEo (SR bits 4-5) unchanged. See `src/PSXRecomp.Native/src/psx_cpu.cpp` (`PSXCpu::ExecRfe()`) and the supporting test `src/PSXRecomp.Native/tests/test_psx_core.cpp` (`test_rfe_pop`, boundary case `0x3C -> 0x3F`, confirming KUo/IEo=1 remains 1 after RFE). For the detailed three-level stack transition, follow `docs/cpu/cop0.md` (SR bit definitions) and `docs/cpu/exceptions.md` (RFE procedure).

Exception return when BD=1:
```text
1. EPC points to the branch instruction.
2. The exception handler must re-execute the branch instruction and its delay slot.
3. Execution then reaches the branch target or continues on the not-taken path.
```

## Consequences

- The Interpreter manages the PC using the two variables `pc` and `next_pc`.
- The Recompiler must track PCs at block boundaries.
- Exception handling must process the BD bit.
- Tests must comprehensively verify PC update ordering.
