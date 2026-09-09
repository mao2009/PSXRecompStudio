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
