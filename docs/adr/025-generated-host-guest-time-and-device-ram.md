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

`PsxDeviceGraph` takes an optional `IMemoryBus deviceRam` for traffic a device originates; null keeps the interpreter's native RAM. The generated-host bridge passes `ArtifactDeviceRam`, which reads/writes `artifact_ram` through the protocol's existing byte `R`/`W` requests. The artifact serves them only while it waits for the reply to a report, and `ArtifactDeviceRam` throws outside that window, so an access that cannot reach `artifact_ram` fails closed (`ARTIFACT_DEVICE_RAM_UNROUTABLE`) instead of reaching native RAM. There is no second RAM and no copy/sync. The device→RAM movers today are CD-ROM DMA3 and GPU DMA2/OTC DMA6 (`GpuDmaTransfer`, #732, built over the same seam); the native generic DMA model moves no data. A future mover (GPU DMA2 #440, SPU, MDEC) must route through the same seam; one that cannot is unsupported and must fail closed.

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
- **Boundary.** No guest code is called on the guest's behalf. The artifact has no generated block at the vector, so the host-transfer offer for that pc is answered by the host: since #662 (addendum below) an unpopulated RAM vector is the Runtime's kernel exception handler; a guest-installed vector or the BEV = 1 vector is answered with `UnresolvedIndirectFlow` and `ARTIFACT_EXCEPTION_VECTOR_UNHANDLED` (the message carries EPC/CAUSE/SR).
- **SR bit layout (recorded, not changed).** The generated host and the kernel contract (`BiosKernelSyscallDispatch`, ADR-014) use the R3000A layout (IEc = bit 0, KUc = bit 1). `PSXCpu`'s INT check and `docs/cpu/cop0.md` name bit 1 as IEc. The two agree on every other value (the stack push is layout-neutral), but a guest that enables interrupts with SR bit 0 only (SYS 02h, `mtc0 0x401`) is not interrupted by the interpreter. **Resolved by Issue #684:** `PSXCpu` and `docs/cpu/cop0.md` now use IEc = bit 0, and the parity test compares the two without setting bit 1.

## Addendum (Issue #662): the kernel exception handler at the vector

At the unpopulated RAM vector the host serves the shared `BiosExceptionHandler` (ADR-014 amendment, the same contract the interpreter uses) instead of stopping. The artifact stays the CPU: the host reads its state and writes the result back over the pipe, in the reply phase of the `RHOST_TRANSFER` offer, before its decision.

- **State read.** `E` -> `RHOST_COP0 epc cause sr hi lo intEntry`. The transfer offer carries pc and the GPRs only, so the host asks for the COP0 state and HI/LO only when the pc is the vector and the RAM vector is unpopulated.
- **Provenance.** `intEntry` is 1 only when the artifact's own `artifact_interrupt_boundary` accepted a hardware INT and control is still at the vector that entry set (any other pc, or the transfer itself, clears it). The host enters the kernel handler only with `intEntry = 1`: a vector that is zero with `CAUSE.ExcCode == 0` does not prove an INT entry, so an ordinary jump to `0x80000080` takes the generic `ARTIFACT_EXCEPTION_VECTOR_UNHANDLED` path and mutates nothing.
- **State write.** `G index value` (GPR; only registers that changed), `H hi lo`, `C sr` then `P` (the artifact's own RFE pop, as for the SYSCALL return), `L 0|1`, then the usual `D` with the PC. The host never computes RFE.
- **Interrupt line.** `L` refreshes the artifact's `irq_line` / `CAUSE.IP2` from the controller after the handler ran. Without it the artifact keeps the line from its last guest-time ack; a handler that acknowledged I_STAT retires no instruction, so no new ack would come and the same INT would be taken forever. Whether the next INT is taken stays the artifact's own SR decision.
- **Reachability.** A hook or return target must be a block the artifact compiled; the same rule as a patched jump-table target. The EPC of an INT is a dispatch-boundary pc and always is.
- **Fail closed.** An unmodelled chain element, a non-INT exception or an unusable PCB/TCB stops the run with the handler's diagnostic (`BIOS_EXCEPTION_*`); nothing continues on a guess.

## Amendment (Issue #693): mixed execution - state ownership, RAM copy-sync, time, protocol

When an in-image indirect target has no compiled block and mixed execution is enabled (ADR-012 amendment), the host runs the interpreter on **the host-owned device graph's own native core** and gives control back to the artifact at a clean compiled block entry.

**Ownership during a fallback.**

| State | Owner | How it crosses |
|---|---|---|
| Devices, interrupt controller (I_STAT/I_MASK), timers, DMA, GPU, CD-ROM, scratchpad, other COP0 registers | the host's single `PsxDeviceGraph` / its core | not copied: the interpreter steps that same core |
| `DeviceScheduler` | the host | the same instance; one `CyclesPerInstruction` cycle per instruction the interpreter retires, exactly as for the interpreter engine |
| GPR, HI/LO, PC | the artifact before and after; the interpreter core during | copied in and out explicitly |
| SR, CAUSE, EPC | the artifact before and after; the core during | copied in and out explicitly (the artifact models only these three) |
| BIOS HLE state | guest RAM (no C# state) | follows RAM |
| Guest RAM | `artifact_ram` (SSOT before and after); the core's RAM is a working copy during | **copy-sync** below |
| Pending interrupt line | the controller | `S` carries it back; the artifact still takes the INT only by its own SR decision |

This supersedes "no second RAM and no copy/sync" **for the duration of one fallback segment only**: the graph core's RAM (otherwise unused) is the segment's working copy.

**RAM copy-sync (page-granular, correctness first).** RAM is split into 4096-byte pages. `_synced` / `fallback_shadow` is the image the two sides last agreed on (all zero at first, like a fresh core). *Entry*: the artifact sends the pages that differ from the shadow (`Y`), ascending, 8192 lowercase hex characters each, covered by an FNV-1a hash over `(page index as 4 little-endian bytes, page bytes)`; before applying them the host restores any page of its core RAM that drifted from the agreed image, so the core equals artifact RAM when the segment starts; a count or hash mismatch is a protocol fault and nothing is applied. *Return*: the host stages the pages the segment changed (`B`) and commits them (`K count hash`); the artifact applies them to `artifact_ram` **only** if count and hash verify, so guest RAM is never left half-updated, and otherwise answers `RHOST_FALLBACK_REFUSED checksum` leaving RAM unchanged (the run stops with `ARTIFACT_FALLBACK_SYNC_FAILED`). Every other outcome (a stop, a budget exhaustion) writes nothing back. A device that moves data into RAM during the segment (CD-ROM DMA3) targets the core's working copy through `ArtifactDeviceRam.RedirectTo`, and is back on `artifact_ram` afterwards. The page diff keeps the dirty-page optimisation a property of the protocol: the measured Persona handoff dirtied 1 of 512 pages; a shared backing store (one RAM for both engines) remains a later optimisation behind the same contract.

**Clean return boundary.** The artifact starts blocks only at fused-unit boundaries, where no branch delay slot or load delay is in flight. The interpreter returns at a PC the artifact compiled only when the native CPU says the same (`PSXCore_GetPipelineState`: bit 0 branch delay pending, bit 1 uncommitted load; read-only, never a flush) and no guest interrupt handler is running. A compiled block entry that is the delay slot of an uncompiled branch, or follows an uncompiled load, is therefore not a return point.

**Time.** Guest time stays the host's. The artifact reports its retired instructions before offering a transfer (existing rule), so devices are current at entry; during the segment the host advances the scheduler per interpreter instruction; nothing about the segment needs to be reported to the artifact afterwards.

**Protocol (version 1; everything is opt-in, an unmodified run never sends it).** Host to artifact: `F version` (query; reply `RHOST_FALLBACK version indirect irq hi lo sr cause epc dirtyPages` or `RHOST_FALLBACK_REFUSED reason`; changes no state), `Y` (pull dirty pages: `RHOST_PAGE index hex` lines, then `RHOST_PAGES_END hash`), `B index hex` (stage one page; ascending and distinct), `K count hash` (verified commit; `RHOST_OK` or `RHOST_FALLBACK_REFUSED checksum`), `S hi lo sr cause epc irq gpr1..gpr31` (full CPU write; CAUSE.IP2 is set from the `irq` field), then the existing `D 0 pc 0 0`. `RHOST_TRANSFER` and every existing message are unchanged. A malformed fallback command is fatal to the artifact (exit 101, `ARTIFACT_FALLBACK_PROTOCOL_FAILED` on the host); a host-side malformed reply is `ARTIFACT_HOST_PROTOCOL_FAILED`. `indirect` is 1 only when the offered pc equals the runtime target of the last register-indirect block exit (`state->indirect_target`, written only by such exits).

**Fail-closed diagnostics.** `ARTIFACT_FALLBACK_UNSUPPORTED_STATE` (left the image), `ARTIFACT_FALLBACK_EXCEPTION_UNSUPPORTED` (an exception the interpreter loop does not service), `ARTIFACT_FALLBACK_BUDGET_EXHAUSTED`, `ARTIFACT_FALLBACK_TRANSITION_BUDGET_EXHAUSTED`, `ARTIFACT_FALLBACK_SYNC_FAILED`; a BIOS or kernel boundary inside a segment keeps the Runtime's own diagnostic.

**Evidence.** Counts only, deterministic: transitions, returns, instructions retired by the interpreter, pages copied each way, and per target `(entries, instructions, last return PC)`. Timings are measurement-only and never enter a canonical document.

## Amendment (Issue #744): exact asynchronous deadlines and retirement boundaries

This amendment supersedes the original synchronization batching-equivalence claim,
its permitted late device delivery, fused-MMIO timing allowance, and the #680
statement that every dispatch boundary has a current IRQ line. It retains the
existing modeled guest clock (one CPU cycle per retired instruction), scheduler,
CPU exception rules, device RAM ownership, and AOT-only policy. It does not claim
physical PS1 cycle accuracy.

### Deadline ownership and delivery

`DeviceScheduler.NextEventCycles` is a positive conservative distance from its
current clock to the earliest possible timed effect. Devices own the calculation:
Rust timer/DMA queries inspect their existing state without register-read side
effects; the CD-ROM query includes sectors, response due times and acknowledge
spacing; the scheduler adds VBlank and delivery of pending DMA, CD-ROM, SIO or GPU
edges. A ready CD-ROM DMA burst requires the following scheduler advance because
DMA precedes CD-ROM in the existing stage order. A deadline may precede an effect,
but must never follow it. Unknown CD-ROM implementations conservatively return
one cycle. No duplicate register or event model is introduced.

SIO pending is a delivery latch, consumed by `ClearSio0Interrupt` before raising
IRQ7. The controller's subsequently held I_STAT bit is a different owner and
does not require one-cycle credit. No second SIO edge tracker is needed; the
current empty-port model never produces an /ACK pulse.

The host sends `T <positive instruction credit>` before the retired-report line
ack and before a host-transfer decision, including initialization and fallback
return. Initialization also sends the existing `L` IRQ level, including a line
already pending before the first retirement. The artifact stores the corresponding absolute retired-instruction
deadline. It reports as soon as that deadline is reached, before another guest
instruction executes. The old 1024 limit remains an additional bounded reporting
cap; reducing it is neither the fix nor a correctness prerequisite. MMIO accesses
flush preceding retirements and flush their own retirement, so additions,
rescheduling, cancellation and read side effects refresh the next deadline.

The host rejects a report exceeding its issued credit before advancing devices
(`ARTIFACT_EVENT_DEADLINE_EXCEEDED`). `AdvanceExact` also splits host-driven
blocking-call waits at deadlines; the interpreter uses the same method for those
waits. Ordinary interpreter retirement already advances one cycle. The legacy
`Advance` API retains its explicit single-batch semantics for direct device tests.

A native batch is allowed only while no device deadline occurs inside it. The existing
scheduler stage order is preserved at every effect-producing retirement. IRQ
source assertion at a scheduler advance and CPU acceptance at the following
eligible fetch boundary are distinct events.

### Fused instructions and exception acceptance

Lowering records IR operation offsets for interior retirements. A branch retires
before its delay-slot operations, so MMIO in the slot observes the branch's elapsed
time. Every successful instruction is charged once, including interior
retirements; a faulting instruction is not charged. This also preserves preceding
retirements when a later fused instruction traps.

Hardware INT remains gated by IEc and IM2. It cannot be accepted between a branch
and its delay slot. A pending load is different: the native CPU permits INT before
the observer, commits the pending load in `FlushPipeline`, then records EPC at the
observer PC with BD clear. A fused load records that commit explicitly and applies
it only for an accepted INT; the observer and any fused branch/slot do not execute.
The exception itself retires no instruction. Ordinary observer cancellation and
load-delay semantics remain unchanged when INT is not accepted.

The host-serviced SYSCALL route remains the interpreter's HLE convention: the
serviced instruction costs one cycle. Current lowering never fuses a standalone
SYSCALL/BREAK with a load because it reads no GPR. Fused traps are branch-delay
exceptions and cannot enter HostSyscall (`BD != 0`). Guest-owned traps charge only
the successful fused prefix. The HLE offer performs the same shared exception
entry as guest delivery, recording CAUSE/EPC as well as pushing SR before service;
RFE changes SR but does not erase CAUSE/EPC.

The host-owned scheduler remains the single clock during interpreter fallback.
Before handoff, native retirements are synchronized; after a clean interpreter
return, the host refreshes credit relative to the artifact's native retired total.
Fallback instructions are never charged again by the artifact.

### Protocol, cost, and failure behavior

`T` is an extension to existing reply phases; it does not add a request or a
round trip. Its payload is bounded and positive; malformed credits fail the
existing protocol rather than silently clamping a deadline. Updated host and
artifact must be paired for the exact-time contract: production passes
`--exact-device-time`, requiring a fresh credit in each reply phase; missing
credits fail closed. Legacy scripted peers retain
the earlier reporting behavior; they cannot establish strict parity. An older
artifact rejects the new command, failing closed. Existing RAM requests and IRQ
line acknowledgements retain their meanings.

Review follow-up (#746): every command token is matched in full. Transfer,
initialization and SYSCALL reply phases recognize only `T/F/Y/B/K/S/R/W/C/E/G/H/L/P`
and the explicit terminating `D` or `N`; unknown tokens fail with protocol exit 100,
never implicit decline. Retirement replies recognize `R/W/T` and terminating
`A/I/X`. Credit is a whole unsigned decimal token in `1..UINT32_MAX` (no sign,
fraction, exponent or suffix) and at most one `T` is permitted in either phase.
Exact mode requires that one credit before a decision/ack; valid legacy peers
may omit it, retaining the earlier timing model. Malformed historical peers are
not a compatibility promise. These checks add no protocol round trips.

Artifact checks are local integer comparisons per retirement. IPC occurs at
existing device observations and actual conservative deadlines, without
mandatory instruction-by-instruction transport. The strict dispatch budget still
prevents execution/exception acceptance after exhaustion. Scheduler/protocol
failures retain existing diagnostics and terminate instead of accepting stale time.

### Alternatives

- Smaller periodic reporting alone: rejected; it leaves phase-dependent timing
  errors and increases IPC without defining a correct acceptance point.
- Host credit alone at dispatch-unit boundaries: rejected; a two/three-instruction
  fused unit could overshoot a deadline and its MMIO would still see stale time.
- Splitting all generated blocks or falling back at every deadline: rejected as
  unnecessary here; explicit interior retirement points retain native execution
  and the existing delay semantics without repeated RAM copy handoffs.
- Fixed timer offsets or OpenBIOS PCs: rejected; they cannot represent rescheduled
  events, interrupt masking, or common-runtime semantics.

Regression evidence and reproducible OpenBIOS measurements are recorded in #744
and its stacked PR. These operational measurements are not architecture constants.

## Related

- ADR-014, ADR-016, #442 (`DeviceScheduler`), #587 (CD-ROM DMA3), #678, #680

## Amendment (Issue #732): batched VBlank field parity

Batching several VBlank intervals preserves the GPU interlace field by toggling
for odd interval counts while retaining a single latched IRQ0 in the existing
scheduler stage. Even interval counts leave the field unchanged.

## Amendment (Issue #732): firmware COP0 registers across mixed execution

SR, CAUSE and EPC keep their existing artifact fields and fallback state transfer.
In firmware mode, all other COP0 registers belong to the shared native core:
MFC0/MTC0 use an optional generated-state callback rather than independent
artifact copies. This preserves native reset values (including PRID) and writes
made by interpreter fallback. An absent callback retains the existing standalone
codegen contract; ordinary HLE artifacts do not enable it.

The additive request is `RHOST_COP0_ACCESS register write value`, where register
is 0..31 except 12..14 and write is 0 or 1. The host validates all fields and
serves the native core's existing GetCop0/SetCop0 ABI. The reply is `V value` or
`X`; malformed replies, unknown tags and out-of-range unsigned values fail
closed. The artifact flushes outstanding retired time before the access. The
access itself retires no instruction and introduces no synthetic device address.
Transfer commands, fallback state version, SR/CAUSE/EPC ownership and guest IRQ
acceptance are unchanged. Older peers remain valid for ordinary artifacts;
firmware peers must understand this additive request or fail closed. There is
no runtime compilation or full-register copy protocol.

## Amendment (Issue #749): aligned memory faults and completed prefixes

### Context

Generated memory helpers previously assembled unaligned words as bytes. Guest
firmware therefore missed AdEL/AdES, and correct explicit COP0 ownership alone
could not reproduce BadVAddr or the handler boundary.

### Decision

Attach validated, optional memory-fault-site provenance to aligned CPU memory
operations, retaining the existing primitive and exception contracts. Check
alignment before any RAM/MMIO effect or IsC suppression, preserve virtual
BadVAddr through the existing COP0 service, and use the existing EPC/BD/SR/vector
helper. Commit an owed pending load on a fault even when a successful observer
write would cancel it. Preserve already completed branch/link and load effects.

Credit only the successful source-instruction prefix, not IR operations or the
faulting instruction. `partial_retired` records credits already accounted in the
current unit; both a fault and normal completion add only the uncredited
remainder. Reset it for every dispatch iteration, including host transfers.
Flush credited time through the existing service before publishing BadVAddr.
Native and fallback continue to use the same host-owned scheduler and graph.
No new request/reply tokens or protocol versions are required.

### Consequences and alternatives

LWL/LWR/SWL/SWR remain legal because their internal word accesses are already
aligned and carry no aligned-source fault provenance. Generic raw IR remains
compatible, and empty provenance does not alter legacy serialization. Malformed
source locations or pending SSA commits fail validation before code generation.
Synthetic fixture tests compare full CPU/COP0/RAM/device state and cycles at the
guest vector, plus RFE return and native/fallback transitions. Full OpenBIOS
parity remains a separate gate on the integrated stack.

Rejected: patching byte helpers without a source location (cannot recover EPC,
BD or owed loads), host-only re-execution after a side effect (too late), and a
new CPU engine or exception protocol (duplicates existing ownership/contracts).

Alignment-fault provenance and exact-time interior boundaries compose without
additional credit: a fault prefix must equal the number of instruction-boundary
hooks at or before its IR operation. Validation rejects mismatches before C
generation. Hooks synchronize the successful prefix before alignment checks;
the fault only reports an uncredited remainder. A pending-load IRQ accepted
before its observer suppresses that observer's fault, while a branch-delay-slot
fault keeps branch EPC/BD and defers IRQ acceptance until after the owed slot.

### Integration verification criterion

Do not infer exact device-time parity from native coverage percentages alone.
The acceptance gate compares interpreter and generated-host guest-cycle counts
and CPU/COP0, RAM, scratchpad and device snapshots at every defined OpenBIOS
observation boundary, including the first hardware IRQ and EXE marker.
