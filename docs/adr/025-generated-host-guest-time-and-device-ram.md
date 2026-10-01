# ADR-025: Generated-host guest time and device-originated RAM

- Status: Accepted
- Date: 2026-10-01
- Issue: #679 (parent #676; depends on #678; next #680)

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
- Pending IRQs are only *observable* (I_STAT); delivering them as INT exceptions into the artifact's CPU state is #680.
- A report round-trips with the host, so the pipe — not the instruction count — bounds artifact speed; the 1024 threshold and the MMIO-bound reports keep that proportional to device interaction.

## Related

- ADR-014, ADR-016, #442 (`DeviceScheduler`), #587 (CD-ROM DMA3), #678, #680
