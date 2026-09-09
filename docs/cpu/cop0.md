# R3000A COP0 Registers

## Register Summary

| Reg | Name | R/W | Description |
|-----|------|-----|-------------|
| r0 | * | - | Unused |
| r1 | * | - | Unused |
| r2 | * | - | Unused |
| r3 | BPC | R/W | Breakpoint Program Counter |
| r4 | * | - | Unused |
| r5 | BDA | R/W | Breakpoint Data Address |
| r6 | TAR | R | Jump Target Address |
| r7 | DCIC | R/W | Debug and Cache Invalidate Control |
| r8 | BadVaddr | R | Bad Virtual Address |
| r9 | BDAM | R/W | Breakpoint Data Address Mask |
| r10 | * | - | Unused |
| r11 | BPCM | R/W | Breakpoint Program Counter Mask |
| r12 | SR | R/W | System Status Register |
| r13 | CAUSE | R/W* | Exception Cause (* bits 8-9 R/W) |
| r14 | EPC | R | Exception Program Counter |
| r15 | PRID | R | Processor Revision Identifier |

## SR (System Status Register) - cop0r12

The R3000A uses a three-level status stack.

```text
Bit  Name    Description
0    KUc     Current Kernel/User mode (0=Kernel, 1=User)
1    IEc     Current Interrupt Enable
2    KUp     Previous Kernel/User mode
3    IEp     Previous Interrupt Enable
4    KUo     Oldest Kernel/User mode
5    IEo     Oldest Interrupt Enable
6    CU0     Coprocessor 0 Usability (unused, always 1 on PSX)
7    CU1     Coprocessor 1 Usability (FPU, unused on PSX)
8-15 IM[7:0] Interrupt Mask (hardware)
16-17 SW     Software Interrupt (R/W)
18-25 IM[9:8] Interrupt Mask (software)
26-27 *      Unused
28    CU2     Coprocessor 2 Usability (GTE)
29    CU3     Coprocessor 3 Usability
30-31 *      Unused
```

### KUc (Kernel/User Current)

- 0: Kernel mode
- 1: User mode

### IEc (Interrupt Enable Current)

- 0: Interrupts disabled
- 1: Interrupts enabled

### 3-Level Stack

On exception entry:
```text
KUo ← KUp, IEo ← IEp
KUp ← KUc, IEp ← IEc
KUc ← 0 (kernel), IEc ← 0 (interrupts disabled)
```

On RFE:
```text
KUc ← KUp, IEc ← IEp
KUp ← KUo, IEp ← IEo
```

KUo/IEo (bits 4-5) are not modified by RFE (real-hardware behavior; see ADR-005).

### BEV (Bootstrap Exception Vector)

- 0: exception vector 80000080h
- 1: exception vector BFC00180h

## CAUSE (Exception Cause) - cop0r13

```
Bit  Name    Description
0-1  *       Unused (zero)
2-6  Excode  Exception code
7    *       Unused (zero)
8-9  IP[1:0] Interrupt pending (R/W)
10-15 IP[7:2] Interrupt pending (hardware, R only)
16-27 *      Unused (zero)
28-29 CE     Coprocessor Error (opcode bits 26-27)
30    *      Unused (PSX-specific: branch condition when BD=1)
31    BD     Branch Delay
```

### IP (Interrupt Pending)

| Bit | Source |
|-----|--------|
| IP[0] | Software interrupt 0 (R/W via MTC0) |
| IP[1] | Software interrupt 1 (R/W via MTC0) |
| IP[2] | Hardware: Interrupt Controller aggregate line (R only) |
| IP[3]-IP[7] | Hardware: unused (unconnected, always 0) in this emulator's model |

On real R3000A/PSX hardware, all peripheral interrupts such as VBlank/GPU/CD-ROM/DMA/TMR0-2 are aggregated by the Interrupt Controller (I_STAT/I_MASK; see `docs/cpu/exceptions.md`) and delivered to the CPU through a single hardware interrupt line (CPU IRQ2 = CAUSE.IP2, bit 10). There is no dedicated CAUSE.IP bit for each individual peripheral. The Interrupt Controller's `GetInterruptPending()` result (`I_STAT & I_MASK != 0`) is this aggregate pending state and is reflected into CAUSE.IP2 on every CPU `Step()` (Issue #144). Software enables this aggregate interrupt line by enabling SR.IM2 (bit 10).

## EPC (Exception Program Counter) - cop0r14

- Stores the PC at which an exception occurred
- If the exception occurred in a delay slot, stores the address of the branch instruction
- RFE itself does not restore the PC. Software reads EPC into a GPR with MFC0 and returns to the PC with JR or equivalent (see ADR-005 and `docs/cpu/exceptions.md`)

## BadVaddr (Bad Virtual Address) - cop0r8

- Stores the address that caused an address error
- Updated only for AdEL (ExcCode 0x04) and AdES (ExcCode 0x05)
- Not updated for other exceptions

## DCIC (Debug and Cache Invalidate Control) - cop0r7

```
Bit  Name    Description
0    DB      Debug breakpoint occurred (R/W)
1    PC      Program Counter break match (R/W)
2-11 *       Unused
12   BCA     Breakpoint Context Active (R/W)
13   BCO     Breakpoint Condition met (R/W)
14-29 *      Unused
30    UD      User Debug Enable (R/W)
31    TR      Trap Enable (R/W)
```

## COP0 Instructions

| Instruction | Opcode | Description |
|-------------|--------|-------------|
| MFC0 rt, rd | 0x10, rs=0x00 | rt = COP0[rd] |
| MTC0 rt, rd | 0x10, rs=0x04 | COP0[rd] = rt |
| RFE | 0x10, rs=0x10 | Return from exception |

## PSX-Specific Notes

- TLB-related instructions (TLBR, TLBWI, TLBWR, TLBP) are generally unused on PSX
- PSX uses fixed mappings
- GTE instructions execute as COP2, not COP0
- Access to COP2 is controlled by the SR.CU2 bit
