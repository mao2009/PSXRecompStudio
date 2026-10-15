# Divergence Checkpoints and Search (Issue #774, design)

**Status:** Design only. Nothing in this document is implemented yet; the implementation is Phase B-6 or later.

**Related Issues:** #774

**Related Components:** `UnitTraceJoiner` and the unit trace ([unit-trace.md](unit-trace.md)), `RecompilerHostCodeGen` (native state struct), `InterpreterTitleExecutionEngine` (`RunLoop`, `RunFallbackSegment`), `FrameSnapshot` / `CaptureFrame` (Issue #575)

Japanese version: [divergence-checkpoints.ja.md](divergence-checkpoints.ja.md)

## Purpose

Phase C has to find the first point where the pure interpreter and the generated native host stop agreeing on the standalone display path, and then the instruction that caused it. This document fixes three things before any of that measurement runs:

1. Where the two sides can be compared at all.
2. What a checkpoint records, and how each side produces it.
3. How the search narrows from a whole run to one instruction, and when Phase C may start.

## 1. Comparable boundaries

An architectural state is comparable only at a clean boundary. The candidates are:

| Boundary | Native side | Interpreter side | Why it is clean |
|---|---|---|---|
| Native unit end | the unit's epilogue | `RunLoop` step end | A unit is a fused branch with its delay slot, so no delay-slot state crosses the boundary. |
| Handoff | the host transfer | `RunFallbackSegment` entry | The native code has flushed its state to the artifact before transferring. |
| Fallback return | `returned` segment, `WriteBack` done | `returnPcs` check | The return check requires `IsPipelineClean`: no pending delay slot and no uncommitted load. |
| Interrupt entry | vector redirect | `Step` with `ExceptionRaised` | Defined by the CPU, but the preempted fetch did not retire (see the trace document). |

Two sides are compared at the same **global retirement position** `g` only when both have a boundary there. The joiner's stretch ends give those positions. Inside a fused unit, inside an interrupt redirect, and inside a fallback segment, nothing is compared.

A matching `g` is not sufficient. The checkpoint must also match in boundary kind, PC, delay-slot pending state (always 0 at these boundaries; a non-zero value is itself a mismatch), and interrupt-handler state.

## 2. CPU checkpoint

One JSON Lines record per checkpoint, written by both sides in the same schema:

| Field | Content | Source (native / interpreter) |
|---|---|---|
| `g` | global retired position | joined trace / `retired` counter |
| `kind` | `unit_end`, `handoff`, `fallback_return`, `interrupt_entry` | the boundary above |
| `pc`, `next_pc` | PC at the boundary | `pc`, `next_pc` / `_core.Pc` |
| `gpr[32]`, `hi`, `lo` | general and multiply registers | state struct / `ReadGpr()`, `Hi`, `Lo` |
| `sr`, `cause`, `epc` | COP0 control registers | `cop0_sr`, `cop0_cause`, `cop0_epc` / `GetCop0` |
| `handler` | in-handler flag, RFE-pending flag, handler EPC | state / `_inInterruptHandler`, `_rfePending`, `_handlerEpc` |
| `load_pending` | uncommitted load present | state / `IsPipelineClean` |
| `time` | device scheduler time | `event_deadline` and `retired_total` / `_scheduler` |
| `mem` | hashes of the selected RAM ranges (section 3) | both sides, same ranges |

The native state struct already carries the COP0 fields (`cop0_sr`, `cop0_cause`, `cop0_epc`, `cop0_other[32]`), so no new storage is needed on that side. Whether those values are current at every unit boundary, and not only at handoffs, is to be confirmed in B-6.

**Cap.** Checkpoints use the same cap and truncation rules as the unit trace (default 64 MiB, `truncated` and `complete:false` at the limit), so a run cannot produce an unbounded file.

## 3. Memory

- **Selected ranges.** The comparison covers named RAM ranges given per investigation (for example the kernel data area, the executable's data segment, the stack). The default is not all of RAM.
- **Hashing.** Each range is hashed (64-bit FNV-1a is sufficient for equality within one run) at each checkpoint. On the first mismatch, the bytes of the mismatching range are read at the previous matching checkpoint and at the mismatching one, to locate the changed words. The read is bounded by the range size.
- **Changed-region tracking** (a dirty-page bitmap updated on store paths) is an option, not a requirement. It touches the native store path, so it is a B-6 decision, taken only if full hashing of the selected ranges proves too slow.
- **DMA and device writes** are not instructions. They are recorded as device events with `g` and `time`, in a separate log that both sides produce from the same device code. They are compared as a sequence, not as state.
- **Forbidden:** a full-RAM hash per instruction, and any comparison that depends on the instruction count alone.

## 4. Display

The display state is the end goal (Issue #774 is a standalone display divergence), but it is not a function of the CPU position.

- **VRAM hash.** The whole VRAM (1024 × 512 × 16 bit) is hashed only at a VBlank boundary. The existing probe reads VRAM as 15-bit, so a black frame is not evidence of black VRAM. A VRAM hash is a GPU-state equality, not a rendered-image equality.
- **Selected regions.** The display area and the drawing area are hashed separately, using the same VRAM read path.
- **GPU command progress.** The GPU command queue is not drained by the CPU instruction count. Each checkpoint records the queue depth and the last command index. VRAM is compared only when both queues are empty; otherwise the checkpoint is marked `gpu_busy` and not compared.
- **Frame capture.** `CaptureFrame` (Issue #575) snapshots the current display state. A frame is compared at a VBlank IRQ, on the device scheduler clock, not at a CPU position.
- **Synchronization.** A CPU checkpoint and a display checkpoint are paired only when they carry the same scheduler `time`. The CPU checkpoint is the first clean boundary at or after the VBlank event time. Because the native side advances device time through `host_retired`, a retirement count that differs from the interpreter's shifts every later `time` too. That is why the serviced-SYSCALL divergence (section 6) must be resolved before display comparison is trusted.

## 5. Divergence search

Each level narrows the search, and each level's result is recorded before the next starts.

**Level 1: checkpoint comparison.** Walk the checkpoints in `g` order, comparing only positions where both sides have a checkpoint. Report the last matching checkpoint `k-1` and the first mismatching `k`. Nothing past a `Partial` joined trace (`ComparableUpToRetired`) or a `Rejected` join is compared.

**Level 2: unit-level trace.** The interval `(k-1, k]` maps onto joined stretches. Find the first stretch whose start matched and whose end did not. Report its source (native unit, fallback segment, or host transfer), its entry and next PC, and its retired count. Only verified stretches are used.

**Level 3: instruction-level trace.** For the one stretch found in level 2, enable per-instruction recording (the interpreter's fetch, registers and PC for each step; the native side needs an instruction-level hook, which is B-6 work). The window is bounded by `g` and by a record count. The full run is never traced at instruction level.

**Level 4: root cause.** Starting from the first differing field (a register, a RAM word or a device event), find the instruction in the window that wrote it, and the control transfer or device update that led to it. Report the first observed mismatch and the root cause separately. A mismatch that is observed later than its cause is not the cause.

## 6. Known blockers

These block Phase C. They are recorded here, not fixed in B-5.

1. **Serviced SYSCALL counting differs.** A SYSCALL serviced in native code is credited; the same SYSCALL serviced in the fallback is not. Every position after it, and every `time` in section 4, is shifted by one on one side. The pinned test is `KnownDivergence_ServicedSyscallInTheFallback_IsFetchedButNotRetired`. The fix changes device time, so it needs a runtime decision, not only a trace change.
2. **Interrupt inside a fallback segment.** The fallback's `end_pc` is the vector, and the preempted PC is not recorded. A native boundary interrupt leaves `next` at the program-order PC. The checkpoint must use `kind = interrupt_entry` and the vector for these positions, and must not assume the two sides agree on `next`.
3. **Fused-unit partial completion and in-body native interrupts are not reproduced.** The synthetic programs in B-5 never produced an interrupt inside a native unit. Until one is reproduced, the checkpoint rule for those cases is unverified.

## 7. Phase C gate

Phase C (measuring the first divergence on the real standalone path) may start only when all of the following hold. The status at the end of B-5 is in the right-hand column.

| Condition | Status at B-5 end |
|---|---|
| Serviced SYSCALL counting agrees on both sides, or an explicit mapping is recorded and tested | Not met (blocker 1) |
| The checkpoint format is implemented on both sides for CPU state and selected RAM, and the synthetic programs produce no false mismatch at the same positions | Not met (design only) |
| The interrupt-entry rule (blocker 2) is implemented and tested | Not met (documented, not implemented) |
| The VRAM comparison runs only at VBlank and only with an empty GPU queue | Not met (design only) |
| The fused-unit partial and in-body interrupt cases are reproduced, or shown to be absent from the generated code | Not met (not reproduced) |

Phase C is not ready to start.
