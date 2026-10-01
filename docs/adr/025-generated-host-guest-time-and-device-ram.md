# ADR-025: Generated-host guest time and device-originated RAM

- Status: Accepted
- Date: 2026-10-01
- Issue: #679, #680 (parent #676; depends on #678; #680 is the INT-delivery addendum below)

## Context

#678 routed the generated-host artifact's MMIO accesses to the Runtime's `PsxDeviceGraph`, but nothing told that graph that guest time was passing, so Timer, DMA, CD-ROM, SIO0, GPU IRQ1 and VBlank never advanced on the recompiled path. The interpreter advances the existing `DeviceScheduler` by `InterpreterTitleExecutionEngine.CyclesPerInstruction` after every retired instruction.

Advancing the scheduler also lets *devices* touch guest RAM. `DeviceScheduler` calls `CdRomDmaTransfer.TryTransfer()`, whose `IMemoryBus` was the graph's `MemoryBus`: the `PSXCoreWrapper`'s own native RAM. On the generated-host path guest RAM is the artifact's `artifact_ram` (ADR-014, #678), so that write would land in a second, private RAM and the guest would never see it. A budget is also not a clock (ADR-016): it counts dispatch units, which are blocks, not instructions.

## Decision

### Cycle accounting contract

- Guest time is **retired guest instructions**. Each `RecompilerIrBlock` carries `RetiredInstructionCount`, set by `MipsToIrLowerer`: 1 for a straight-line instruction (including SYSCALL/BREAK), 2 for a control transfer fused with its delay slot or a load fused with its load-delay observer, 3 for a load, the control transfer observing it, and that transfer's delay slot. Architectural load-delay semantics are untouched; they change which instructions fuse, never how many retire.
- The dispatch loop adds a unit's count to `retired_total` only when the unit completes (after the SYSCALL hook and the termination check). Therefore: a host-claimed BIOS A0/B0/C0 transfer costs 0 (the JAL/JR and delay slot that reached it retired as their own block); a SYSCALL serviced by the Runtime costs its one instruction (interpreter parity: `Advance(1)` after the kernel service) and no invented kernel time; an exception exit the Runtime does not service, and a budget stop, cost nothing; exception delivery adds no timing (#680 owns it).
- The artifact knows no cycle costs. The host converts: `cycles = retired × InterpreterTitleExecutionEngine.CyclesPerInstruction`, so both backends use one constant. Cycle-exact timing is out of scope.

### Synchronization

The artifact accumulates and reports `RHOST_RETIRED <count>` (uint64 on the wire). It reports:

1. before every MMIO access, host transfer and SYSCALL (devices are current when the guest or the BIOS HLE observes them),
2. after the unit that retired an MMIO **write** (what a store sets going — a DMA kick, a CD-ROM command — happens before the next instruction, as in the interpreter; this is also what makes a resulting device→RAM write visible to the next plain RAM load),
3. when 1024 (`RecompiledArtifactCodeGen.RetiredReportThreshold`) unreported instructions accumulate at a unit boundary,
4. at the end of the run.

Between two reports the guest executes no MMIO and so cannot observe a device; one batch is equivalent to the same instructions advanced one at a time, and `DeviceScheduler`'s contract (VBlank collapsing, latched interrupts) already treats chunking that way. The host advances the existing scheduler in `int.MaxValue`-bounded chunks. Remaining difference from the interpreter: an MMIO inside a fused block sees device time as of the block's start (at most two instructions earlier), and a device effect that needs only time (a DMA waiting for data) may be applied up to 1023 instructions late if the guest does not touch MMIO — it is always applied before the guest next touches MMIO.

A report is a request/reply pair: the host answers `A` (accepted) or `X` (refused) after serving any RAM request the advance caused. A count of 0, a non-integer, or more than `3 × uint.MaxValue` (a 32-bit budget of the largest fused block) is a protocol failure, never a wrapped or silently clamped time.

### Device-originated RAM

`PsxDeviceGraph` takes an optional `IMemoryBus deviceRam` for traffic a device originates; null keeps the interpreter's native RAM. The generated-host bridge passes `ArtifactDeviceRam`, which reads/writes `artifact_ram` through the protocol's existing byte `R`/`W` requests. The artifact serves them only while it waits for the reply to a report, and `ArtifactDeviceRam` throws outside that window, so an access that cannot reach `artifact_ram` fails closed (`ARTIFACT_DEVICE_RAM_UNROUTABLE`) instead of reaching native RAM. There is no second RAM and no copy/sync. The only device→RAM mover today is CD-ROM DMA3; the native generic DMA model moves no data. A future mover (GPU DMA2 #440, SPU, MDEC) must route through the same seam; one that cannot is unsupported and must fail closed.

### Failure classification

Scheduler or device failure while consuming a report: `ARTIFACT_SCHEDULER_FAILED` (artifact exit 99). Report overflow: `ARTIFACT_CYCLES_OVERFLOW`. A malformed or missing message: `ARTIFACT_HOST_PROTOCOL_FAILED`. The artifact exits 99 on a refusal and 100 on a malformed reply, with no snapshot.

## Consequences

- The artifact and the interpreter advance devices by the same time for the same retired instructions; Timer, VBlank and DMA3 effects appear in I_STAT / DMA state / `artifact_ram`.
- Pending IRQs are observable in I_STAT and, since #680 (addendum below), reach the artifact CPU as a hardware INT exception.
- A report round-trips with the host, so the pipe — not the instruction count — bounds artifact speed; the 1024 threshold and the MMIO-bound reports keep that proportional to device interaction.

## Addendum (Issue #680): hardware INT delivery

The pending state is the existing Interrupt Controller's (`I_STAT & I_MASK`, `PSXCore_GetInterruptPending()`, the line the interpreter's CPU reflects into CAUSE.IP2); no second interrupt model exists.

- **Line transport.** The host's reply to `RHOST_RETIRED` becomes `A` (line deasserted) or `I` (asserted), read after the `DeviceScheduler` advance. The artifact mirrors it in `irq_line` / `CAUSE.IP2`. Everything that can change I_STAT, I_MASK or device time passes through a report (an MMIO write flushes at the end of its unit), so the line is current at every dispatch boundary. A device event is noticed at the next report: at most 1024 retired instructions after it (bounded and deterministic; the interpreter notices after one instruction).
- **Acceptance point.** `recompiler_dispatch` calls `host_interrupt` at the top of each iteration, i.e. between dispatch units. A unit fuses a branch with its delay slot, so INT is never taken inside a branch + delay-slot pair (the native CPU does not check while `branch_pending_` either), and EPC is never a delay-slot address (`CAUSE.BD` stays 0). The hook is gated by the budget guard (`steps < budget`): a stop at the budget leaves the state unmutated.
- **Acceptance condition.** `irq_line && SR.IEc (bit 0) && SR.IM2 (bit 10)`. Pending in I_STAT alone never raises an exception.
- **Entry (R3000A, docs/cpu/exceptions.md).** `EPC = pc`; `CAUSE = (CAUSE & ~(Excode|CE|BD)) | IP2` (Excode INT = 0); SR KU/IE stack pushed (`(SR & ~0x3F) | ((SR << 2) & 0x3C)`, the SYSCALL entry's push); `pc = SR.BEV ? 0xBFC00180 : 0x80000080`. No instruction retires, so no device time. A test compares these values with the native `PSXCpu`'s for the same SR and line.
- **Boundary.** No guest code is called on the guest's behalf. The artifact has no generated block at the vector, so the host-transfer offer for that pc is answered with `UnresolvedIndirectFlow` and `ARTIFACT_EXCEPTION_VECTOR_UNHANDLED` (the message carries EPC/CAUSE/SR): the kernel exception path (C0:06 ExceptionHandler, timer/VBlank priority chain, Pad/Card IRQ, B0:17/B0:18) is #662/#658/#661.
- **SR bit layout (recorded, not changed).** The generated host and the kernel contract (`BiosKernelSyscallDispatch`, ADR-014) use the R3000A layout (IEc = bit 0, KUc = bit 1). `PSXCpu`'s INT check and `docs/cpu/cop0.md` name bit 1 as IEc. The two agree on every other value (the stack push is layout-neutral), but a guest that enables interrupts with SR bit 0 only (SYS 02h, `mtc0 0x401`) is not interrupted by the interpreter.

## Related

- ADR-014, ADR-016, #442 (`DeviceScheduler`), #587 (CD-ROM DMA3), #678, #680
