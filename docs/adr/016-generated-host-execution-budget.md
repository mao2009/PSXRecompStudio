# ADR-016: Generated-host execution budget semantics

- Status: Accepted
- Date: 2026-09-17
- Issue: #375

## Context

The generated-host dispatcher and the interpreter intentionally count different execution units. `RecompilerDifferentialFixture.StepBudget` counts retired generated-host dispatch units (normally generated IR blocks), while `ReferenceStepBudget` counts retired MIPS instructions. Control-transfer lowering can fuse a guest instruction and its delay slot into one generated block, so equal numeric budgets are not generally equivalent execution windows.

Before this decision, `recompiler_dispatch` checked `steps >= budget` only after invoking the current generated block or host-transfer callback. That made the advertised upper bound off by one: budget 0 could mutate guest state once and budget N could mutate guest state through N+1 dispatches before reporting `ExecutionBudgetExceeded`.

## Decision

Generated-host `budget` is a **strict upper bound on retired generated-host dispatch units**.

A dispatch unit is either:

1. one generated block selected for the current guest PC, or
2. one unresolved-PC transfer that is claimed by the optional host-transfer callback.

The dispatcher checks the limit **before** invoking either a generated block or a non-null host-transfer callback. Therefore:

- `budget == 0` retires zero dispatch units;
- `budget == N` retires at most N dispatch units;
- no register, HI/LO, PC, RAM, or host-transfer side effect from dispatch unit N+1 is observable;
- when a known block or callable host transfer would exceed the limit, the dispatcher returns `ExecutionBudgetExceeded` with the guest PC still pointing at that unretired unit;
- a no-hook unknown PC after at least one retired unit remains normal program fall-off and returns Success without consuming another unit;
- an unknown initial PC with no host hook remains `UnsupportedIr`, because there is no dispatchable unit to budget.

A host-transfer callback that returns “unclaimed” does not retire a unit, but the callback itself can have host/guest-visible effects. For that reason it is never invoked after the strict budget is exhausted.

## Backend relationship

This decision does **not** redefine interpreter or IR budgets as host-block budgets. The unit boundary remains explicit:

- generated host: retired generated block / claimed host-transfer unit;
- interpreter: retired MIPS instruction;
- differential fixtures: `StepBudget` and `ReferenceStepBudget` are independent unless the fixture explicitly asserts a shared execution window.

Callers comparing the two backends must continue to use the differential fixture contract rather than assuming numerically equal budgets imply identical work.

## Consequences

- The previous N+1 generated-host behavior is removed.
- Budget cut-offs become fail-closed and cannot perform one extra guest mutation.
- Existing callers that relied on the accidental post-dispatch accounting must adjust their host budget rather than depending on the off-by-one behavior.
- Normal straight-line fall-off without a host hook preserves its existing Success classification at an exact final-block budget.
- Regression coverage must include 0, 1, N, register/PC/RAM state at exhaustion, and a state-mutating host-transfer callback.

## Alternatives considered

### Post-dispatch accounting

Rejected. Keeping N+1 behavior while documenting it would leave `budget` unsuitable as a hard execution safety boundary and would permit state mutation after the nominal limit.

### One common instruction-exact budget for every backend

Rejected for this issue. Generated blocks and guest instructions are not one-to-one because of lowering/fusion semantics. Converting all backends to one unit would be a larger execution-model change and is unnecessary to fix the correctness bug.
