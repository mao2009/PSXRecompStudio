# R3000A Exception Handling

## Exception Sources

| ExcCode | Mnemonic | Description |
|---------|----------|-------------|
| 0x00 | INT | External interrupt |
| 0x01 | MOD | TLB modification (unused on PSX) |
| 0x02 | TLBL | TLB load (unused on PSX) |
| 0x03 | TLBS | TLB store (unused on PSX) |
| 0x04 | AdEL | Address error (load/instruction fetch) |
| 0x05 | AdES | Address error (store) |
| 0x06 | IBE | Instruction fetch bus error |
| 0x07 | DBE | Data load/store bus error |
| 0x08 | Sys | SYSCALL instruction |
| 0x09 | Bp | BREAK instruction |
| 0x0A | RI | Reserved instruction |
| 0x0B | CpU | Coprocessor unusable |
| 0x0C | Ov | Arithmetic overflow |

## Implementation Status

`PSXCpu` (`src/PSXRecomp.Native/src/psx_cpu.cpp`) raises the following through
`RaiseException`; all of them go through the same EPC / CAUSE.BD / SR-stack /
vector-selection machinery described below.

| ExcCode | Raised when |
|---------|-------------|
| 0x00 INT | A pending, unmasked interrupt at an instruction-fetch boundary outside a delay slot (Issue #144) |
| 0x04 AdEL | A misaligned LH/LHU/LW, or an instruction fetch from a misaligned or unmapped PC (Issue #376) |
| 0x05 AdES | A misaligned SH/SW (Issue #376) |
| 0x08 Sys | SYSCALL |
| 0x09 Bp | BREAK |
| 0x0A RI | An undefined opcode, an undefined SPECIAL funct, an undefined REGIMM selector, or a COP0 form other than MFC0/MTC0/RFE (Issue #376) |
| 0x0B CpU | Any COP1/COP2/COP3 access, including LWC1/2/3 and SWC1/2/3, since none of those coprocessors are implemented. CAUSE.CE carries the coprocessor number (Issue #376, Issue #377) |
| 0x0C Ov | Signed overflow in ADD/ADDI/SUB |

Not modelled: MOD/TLBL/TLBS (0x01-0x03; the PSX has no TLB) and IBE/DBE
(0x06-0x07; no bus-error reporting in this memory model).

Notes on the choices above:

- An unrecognised **COP0** form raises RI, not CpU. COP0 itself is usable — CU0
  is implicitly set in kernel mode on the PSX — so the *form* is what is
  reserved: CFC0/CTC0 address control registers the R3000A's COP0 does not have,
  and TLBR/TLBWI/TLBP/TLBWR address a TLB the PSX does not have. CpU is reserved
  for coprocessors that are genuinely absent (1, 2 and 3).
- SR.CU1/CU2/CU3 are not consulted: those coprocessors are unimplemented, so
  access is unusable regardless of what software wrote to SR. Likewise SR.CU0 is
  not checked for a COP0 access from user mode.
- **Address errors apply to instruction fetch and to the aligned load/store
  forms only.** LWL/LWR/SWL/SWR are the architecturally unaligned forms and are
  deliberately exempt. An unmapped *data* access remains a silent read-of-0 /
  ignored write in this model (see `test_kseg_unmapped`); only an unmapped
  *instruction fetch* faults, because continuing past a corrupted control
  transfer is what hides the bug.

### Observing a fault from a host caller (Issue #377)

`PSXCore_Step()` returns **zero for a step that faulted**. That is deliberate: an
architectural exception is a normal, continuable hardware event — `PSXCore_Run()`
must keep executing into the handler, and a title's every SYSCALL would otherwise
look like an emulator error. The step succeeds; it simply lands the PC on the
exception vector.

A host caller that must not mistake a faulted run for a clean one therefore asks
`PSXCore_GetExceptionRaised()` (C#: `PSXCoreWrapper.ExceptionRaised`), which
reports whether the most recent step raised, and is reset by every step.

Two callers depend on this, both of which run a bare program with no exception
handler installed:

- `RecompilerInterpreterExecutor` — the differential-testing reference oracle.
  A fault must surface as `RecompilerIrTerminationReason.Exception`, which
  `RecompilerStateDiff` treats as a behavioral field and therefore a hard
  mismatch. Without it, a CpU fault moved the PC out of the program bounds and
  the run reported `Success`: a faulted reference indistinguishable from a clean
  one, which is the false-match risk the oracle exists to rule out.
- `InterpreterTitleExecutionEngine` — reaching `ExecutionOrchestrator` as
  `RuntimeFailure` / `CPU_EXCEPTION` rather than being handed to the handoff,
  which would classify a faulted title as `Completed`.

### Carrying a fault through the recompiler (Issue #481)

BREAK is the first trap lowered by the recompiler as a first-class architectural
exception (Excode 0x09, Bp). The interpreter oracle populates a snapshot-level
exception resolution for BREAK only; every other modelled trap keeps its default
(unraised) resolution, so existing single-difference guarantees (e.g. the CpU
contract in Issue #377) are preserved:

- A standalone BREAK lowers to an exception-terminated IR block whose
  resolution carries `faultPc = <own PC>` and `inDelaySlot = false`. The host
  block writes `state->exception_raised/exception_code/exception_fault_pc/
  exception_in_delay_slot` before terminating with reason
  `RecompilerIrTerminationReason.Exception` (byte 6).
- A BREAK in a branch/JAL delay slot suppresses the owning transfer: the
  architectural link write (JAL/JALR) is still retired first, then the fault is
  taken with `faultPc = <owning branch PC>` and `inDelaySlot = true` — the EPC
  and CAUSE.BD values the hardware produces, per the EPC rules above.
- The 4-tuple is part of the snapshot protocol: both the production and test
  drivers emit `exception.raised/code/faultPc/inDelaySlot` lines that both
  parsers (strict and lenient) reconstruct.
- Because the raised fault parks the host PC at the faulting block while the
  interpreter moves to the exception vector (0x80000080), `RecompilerStateDiff`
  waives the `pc` difference **only** when both sides terminate `Exception` with
  a raised resolution — the agreement then lives in `exception.faultPc`. An
  unraised exception keeps the strict `pc` compare.

## Exception Vectors

| Exception | BEV=0 | BEV=1 |
|-----------|-------|-------|
| Reset | BFC00000h | BFC00000h |
| UTLB Miss | 80000000h | BFC00100h |
| COP0 Break | 80000040h | BFC00140h |
| General | 80000080h | BFC00180h |

On PSX, execution normally starts with BEV=1 during BIOS startup and later switches to BEV=0 when the BIOS changes it.

## Exception Processing

### On Exception

1. **Save EPC**: EPC = address of the branch instruction when in a delay slot; otherwise the current PC
2. **Set BD**: CAUSE.BD = 1 when the exception occurred in a delay slot
3. **Set CAUSE**: CAUSE.Excode = exception code
4. **Push SR stack**: shift the 3-level stack toward the older levels
   - KUo ← KUp, IEo ← IEp
   - KUp ← KUc, IEp ← IEc
5. **Disable interrupts**: KUc ← 0 (kernel), IEc ← 0 (interrupts disabled)
6. **Transfer PC**: PC = exception vector address

### RFE (Return From Exception)

1. Pop the 3-level SR stack
   - KUc ← KUp, IEc ← IEp
   - KUp ← KUo, IEp ← IEo
   - KUo/IEo (SR bits 4-5) are not modified by RFE
2. RFE itself does not change the PC. Restoring the PC is software's responsibility and is normally done by reading EPC into a GPR with MFC0 and then jumping with JR. If the exception occurred in a delay slot, execution returns from the branch instruction address. See ADR-005 for details.

## Interrupt Handling

### I_STAT (1F801070h)

Interrupt status register. Edge-triggered.

| Bit | Source |
|-----|--------|
| 0 | VBlank |
| 1 | GPU |
| 2 | CD-ROM |
| 3 | DMA |
| 4 | TMR0 |
| 5 | TMR1 |
| 6 | TMR2 |
| 7 | SIO |
| 8 | SPU |
| 9 | PIO |

### I_MASK (1F801074h)

Interrupt mask register. R/W.

## Interrupt Processing

1. If I_STAT & I_MASK is non-zero, an interrupt is pending at the Interrupt Controller aggregate line (`PSXInterruptController::GetInterruptPending()`).
2. On every CPU `Step()`, that aggregate pending state is reflected into COP0 CAUSE.IP2 (bit 10; see IP in `docs/cpu/cop0.md`).
3. If `(CAUSE.IP & SR.IM) != 0 && SR.IEc == 1`, process an INT exception (Excode 0x00). The SR 3-level stack is pushed, and EPC/CAUSE.BD are set according to the same #141 model used for other exceptions. Interrupts are not checked while executing a branch delay slot; they are evaluated only after the branch + delay-slot pair completes (Issue #144, ADR-004/ADR-005).

## Exception Priority

```
highest:  I-Fetch (Instruction Fetch)
          RI (Instruction Decode)
          CpU (Instruction Decode)
          TLBL (I-Fetch)
          AdEL (IVA)
          IBE (end of I-Fetch)
          ...
lowest:   ...
```

## Nested Exceptions

If SR is not saved/restored inside the exception handler, nested exceptions can cause problems.

```asm
# Example exception handler (R3000A 3-level stack)
mfc0 k0, C0_SR     # Save SR
sw   k0, saved_sr
ori  k0, k0, 0x3   # KUc=0, IEc=0 (kernel, interrupts disabled)
mtc0 k0, C0_SR     # Stack save complete
# ... handling ...
lw   k0, saved_sr
mtc0 k0, C0_SR     # Restore SR
mfc0 k1, C0_EPC    # Read return address from EPC
jr   k1            # Jump back to PC
rfe                # Execute in the delay slot and pop the 3-level stack
```
