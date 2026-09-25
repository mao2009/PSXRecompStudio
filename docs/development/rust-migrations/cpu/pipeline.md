# PSXCpu Migration Slice: Pipeline / Load Delay

**Status:** Stable

**Authority:** Reference

**Related Issues:** #531, #524, #471

**Dependencies:** [PSXCpu Rust Migration Slices](README.md), [Rust FFI Safety Contract](../../rust-ffi-contract.md)

## Files owned

Paths are relative to `src/PSXRecomp.Native/`.

- C++: `src/psx_cpu_pipeline.cpp`
- Rust: `rust/src/cpu_pipeline.rs`
- Tests: `tests/test_psx_cpu_pipeline_rust.cpp` (runner `run_psx_cpu_pipeline_rust_tests()`)
- Optional internal header for Rust declarations: `src/psx_cpu_pipeline.h`

## Scope

`Step`, `FetchInstruction`, `FlushPipeline`, `UpdateLoadDelay`, `WriteRegDelayed`, `SetPendingBranch`: the interrupt check, fetch (with AdEL), branch-delay and branch-in-delay-slot handling, and the double-buffered load delay.

## Cross-slice calls

Calls `ExecuteInstruction` (decode), `RaiseException`/`RaiseAddressError` (exception), `TranslateAddress`/`IsMapped` (memory access) and `RecordGprWrite` (`psx_cpu.cpp`). `WriteRegDelayed` and `SetPendingBranch` are called by memory access, unaligned, COP0 and control. This is the second-wave slice: it depends on the call boundaries of the others staying stable.

## Current state

Migrated (#531): the deterministic state transitions. `rust/src/cpu_pipeline.rs`
computes them over by-value `#[repr(C)]` copies of the `PSXCpu` fields and
returns the next state; `src/psx_cpu_pipeline.h` declares the internal C ABI.
Rust holds no pointer and no state, and every export is infallible (all PC
arithmetic wraps like `uint32_t`).

| Export | Replaces |
|---|---|
| `psx_cpu_pipeline_update_load_delay` | `UpdateLoadDelay`: returns the GPR commit (`commit_reg`/`commit_value`, `-1` = none) and the shifted state |
| `psx_cpu_pipeline_queue_load` | `WriteRegDelayed`: `$zero`/out-of-range ignored; a load to the pending register cancels that commit (last load wins) |
| `psx_cpu_pipeline_flush_load_delay`, `psx_cpu_pipeline_flush_branch` | `FlushPipeline` |
| `psx_cpu_pipeline_set_pending_branch` | `SetPendingBranch`: a branch in a delay slot keeps the outer target |
| `psx_cpu_pipeline_begin_step` | the per-step exception anchor (`instr_addr`, `in_delay_slot`) and `branch_issued_` reset |
| `psx_cpu_pipeline_advance` | the post-instruction PC / `next_pc` / delay-slot progression; with `exception_raised` it keeps the vector PC, sets `next_pc = pc + 4` and discards branch state (also used by the interrupt-preempt path) |

Stays in C++ (`psx_cpu_pipeline.cpp`): the `PSXCpu` fields themselves, fetch
and AdEL (`FetchInstruction`), `ExecuteInstruction` dispatch, the CAUSE.IP2
mirror and interrupt check, `RaiseException`, the Golden Trace record of the
pending load at the top of `Step`, the GPR commit write, and zeroing
`exception_raised_`/`last_exception_*` (constants, nothing to compute).
`SetGPR`'s cancellation of a same-register pending load stays in `psx_cpu.cpp`.

Timing is unchanged: a load commits at the end of the step after it issues,
the pending commit is still recorded before the instruction's own write, and
`Step`'s control flow (three exits, each ending in `UpdateLoadDelay`) is the
same as before.

Tests: 16 Rust unit tests in `cpu_pipeline.rs`. Native tests in
`test_psx_cpu_pipeline_rust.cpp`: the six moved by #524
(`test_step_branch_delay_slot`, `test_load_delay`, `test_branch_in_delay_slot`,
`test_kseg_instruction_fetch`, `test_adel_misaligned_fetch`,
`test_adel_unmapped_fetch`) plus four added by #531 (same-register back-to-back
loads, pending + new load ordering, exception in a delay slot discarding the
branch, `SetPC` flush). Golden Trace write ordering is covered by the
`test_e2e_trace_*` tests in `test_psx_core.cpp`.
