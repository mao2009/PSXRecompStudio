# Unit Retirement Trace (Issue #774)

**Status:** Diagnostic (opt-in). Off by default; the default generated host is unchanged.

**Related Issues:** #774

**Related Components:** `UnitTraceScope` and `UnitTraceRun` (`src/PSXRecomp.Infrastructure/UnitTraceRun.cs`), `UnitTraceJoiner` (`src/PSXRecomp.Core/Diagnostics/UnitTraceJoiner.cs`), `RecompilerHostCodeGen` (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs`)

Japanese version: [unit-trace.ja.md](unit-trace.ja.md)

## Purpose

The unit trace records, for one run of the generated host, every native unit it executed, every transfer to the host, and every fallback segment the interpreter ran. The parent joins the two halves into one ordered sequence of guest-instruction positions. The sequence lets a later comparison with the pure interpreter find the first stretch where the two runs differ.

The trace is a measurement aid. It does not change what the run computes.

## Enabling

| Setting | Effect |
|---|---|
| `PSX_UNIT_TRACE_FILE=<path>` | Selects a trace build of the generated host and names its native trace file. The parent writes the fallback trace beside it. |
| `PSX_UNIT_TRACE_MAX_BYTES=<bytes>` | Output cap for the native trace. Default 64 MiB. An invalid value disables tracing for that run; the run itself proceeds untraced. |
| `UnitTraceScope.Begin(path)` | The in-process route used by tests. It has the same effect as the environment variable for runs started on that async context. |

With neither set, the generated source is byte-identical to the non-tracing build, and no trace or fallback file is written.

If the trace file cannot be created, the run continues untraced. The parent then writes no fallback trace either, so the joiner cannot produce a complete join for that run.

## Output files

| File | Written by | Contents |
|---|---|---|
| `<path>` | generated host (child process) | header, unit records, handoff markers, end line |
| `<path>.fallback` | parent process | header, fallback and transfer records, end line |

Both are JSON Lines. Each line is one object with `"v":1` and a `"kind"`.

## Identity: session and handoff

- **Session ID.** The parent generates one session ID per run and gives it to the child through `PSX_TRACE_SESSION`. Both headers carry it. The joiner rejects a join whose two sessions differ.
- **Handoff ID.** Every transfer from the native code to the host is one handoff, numbered from 1 in the order they happen. The native handoff marker and the parent's record for the same transfer carry the same number. The joiner checks that the numbers are consecutive and that the two records agree on the transfer PC.

## Record schema (version 1)

Native file (`<path>`):

| `kind` | Fields | Meaning |
|---|---|---|
| `header` | `session`, `cap` | First line. |
| `unit` | `seq`, `entry`, `next`, `start`, `end`, `retired`, `interrupted`, `exception`, `termination`, `state`? | One dispatcher pass. `start` and `end` are cumulative native retirement totals before and after the pass. `retired` is `end - start` and counts only instructions that completed. `entry` is the PC after any interrupt redirect of that pass. `interrupted` and `exception` are 0 or 1. `termination` is the `RecompilerIrTerminationReason` value; 0 is success. `state` (B-6b; trace build only, additive) is the CPU state at the unit's end: `pc` (the successor), `gpr` (32 values), `hi`, `lo`, `sr`, `cause` and `epc`. |
| `handoff` | `seq`, `handoff`, `native_total`, `pc` | The native code transferred to the host. `pc` is the state PC at the transfer. |
| `truncated` | `seq`, `reason` | The output cap was reached. Later records are dropped. |
| `end` | `seq`, `complete`, `reason`? | Last line. `complete:false` with `reason:"output_cap"` after truncation. |

Fallback file (`<path>.fallback`):

| `kind` | Fields | Meaning |
|---|---|---|
| `header` | `session`, `stream`, `cap` | First line. |
| `fallback` | `seq`, `handoff`, `retired`, `status`, `start_pc`, `end_pc`, `stop`?, `entry`?, `exit`? | One interpreter segment. `status` is `returned`, `stopped` or `not_run`. `end_pc` is the PC the segment left the CPU at. `stop` names the diagnostic when the segment stopped. `entry` and `exit` (Issue #774, B-6b; trace build only, additive) are the CPU state the segment started and ended with: `pc`, `gpr` (32 values), `hi`, `lo`, `sr`, `cause`, `epc`. |
| `transfer` | `seq`, `handoff`, `retired`, `status`, `start_pc`, `end_pc` | A transfer the host served without running guest instructions. `retired` is always 0; `status` is `host` or `declined`. |
| `truncated`, `end` | as above | |

Within one file, `seq` increases by one per record. A missing or repeated `seq` is a sequence gap and rejects the join.

## Joining

`UnitTraceJoiner.Join(nativeLines, fallbackLines)` returns one of three statuses:

| Status | Meaning | What may be used |
|---|---|---|
| `Complete` | Both files ended normally and every handoff has its fallback record. | All segments. |
| `Partial` | The traces agree so far, but one of them is cut, truncated or never ended. | Only the segments returned, up to `ComparableUpToRetired`. |
| `Rejected` | The traces contradict each other, or a record is malformed in the middle of a stream. | Nothing. `Errors` says why. |

The joiner checks these invariants:

- The sessions match, and the sequence numbers are gap-free.
- Each native unit starts at the cumulative total where the previous record ended.
- A unit that completes nothing is either a host-transfer iteration (see below) or an interrupt boundary.
- A native stretch enters where the previous stretch ended. Two cases are exempt: an interrupted unit, whose entry is the vector, and a host-transfer iteration.
- After a returned fallback, the next native stretch enters at the segment's `end_pc`. After a host-served transfer, the chain is not checked, because the host decides where execution resumes.
- Nothing follows a stopped fallback segment or a native stop, except the stop record itself.

### Incomplete and truncated

- **Truncated.** The native file reached the cap. The `truncated` record and `end` with `complete:false` make the join `Partial`.
- **No end line.** The run did not exit normally (for example it was killed). The join is `Partial`. A last line cut in the middle is discarded, and the lines before it are kept.
- **Missing fallback record.** If the native side has a handoff with no fallback record, and both files ended normally, the join is `Rejected`. If the native side was cut, the join is `Partial`, because the child may have stopped before the parent answered.

## Reading a host-transfer iteration

After a handoff, the dispatcher's pass that served the transfer is recorded as a `unit` with `retired:0`, `start == end`, and `entry` equal to the handoff PC. This is not a guest instruction. It shows only that the pass ended at `next`, the PC the artifact resumed at. The joiner accepts this record only directly after a handoff, and checks that its `next` matches the returned fallback's `end_pc`.

## What the trace measures

- The order of native stretches, fallback segments and host transfers, in one run.
- The number of instructions each native unit completed, and each fallback segment retired.
- The entry and next PC of each stretch.
- Whether a trace is complete, partial or contradictory.

## What the trace does not measure

- Instruction positions inside a native fused unit. A unit is one record; a partial completion inside it is reported only as its retired count.
- GPU, VRAM, DMA or device state.
- Interrupts inside a native fused block are reproduced (B-6a, `InterruptInsideAFusedBlock_...`); other in-unit positions are not covered.
- Process failures. Child kills and timeouts are checked by cutting the traces at every line, not by killing a real child.

## Known semantic differences

These are recorded, not fixed, in B-5. Consumers must account for them.

1. **Serviced SYSCALL counting (fixed in #790).** A SYSCALL serviced in native code or in the fallback is credited one retirement and resumes at `pc + 4`. The interpreter fetches the instruction and charges it one unit of device time, as the native side does. Before #790 the fallback did not count it; the fallback counter is now aligned. The test `ServicedSyscallInTheFallback_IsRetiredLikeTheNativeOne` checks it.
2. **PC after an interrupt.** Inside a fallback segment the fallback's `end_pc` is the vector, and the instruction the interrupt preempted is not recorded. An in-body native interrupt (reproduced in B-6a) also sets `next` to the vector. A native boundary interrupt keeps `next` at the program-order PC, and the transfer record carries the vector.
3. **Termination 5.** In these runs, `termination` 5 (`UnresolvedIndirectFlow`) appears with a fallback stop, because the fallback reported its stop that way.
4. **SYSCALL in a delay slot.** Both engines refuse to service it and stop with a runtime failure. Both credit the branch once. Under the R3000 restart model the branch would be re-executed, so whether the branch counts is an open convention (pinned by `DelaySlotSyscall_IsRefusedByBothEngines_AndNeitherCompletes`).

## Verification

- `UnitTraceJoinerTests`: invariants, rejection and partial cases, on synthetic lines.
- `UnitTraceEndToEndTests`: the production generated host as a child process, on synthetic programs with three handoffs, a serviced SYSCALL, and timer IRQs. Each stretch is checked against the interpreter's fetches, and the joined sequence is cut at every line.
- `HostCodeGenTests` (`UnitTrace_*`): generated-source behaviour with trace on and off, cap, open failure and stopping units.
