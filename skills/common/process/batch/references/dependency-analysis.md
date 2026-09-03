# Dependency Analysis

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Covers the planning stage: task discovery, inventory, classification, the
dependency model, DAG construction and cycle detection, wave construction, and
the parallel-safety determination.

All five artifacts produced here are **mandatory before any worker is
dispatched**, including when only one worker will be used.

## 1. Task discovery

Collect the candidate task set from the request. A task is normally one Issue.

| Input form | Discovery action |
|---|---|
| Explicit identifiers ("#101, #102, #103") | Take exactly those; do not expand the set |
| A query or label ("all open `batch-ready` Issues") | Resolve the query, then list the resolved identifiers explicitly in the inventory |
| A described work set with no identifiers | Decompose into named tasks; each must be independently completable and independently verifiable |

Rules:

- The discovered set MUST be written down before analysis begins. An implicit
  set is not a task set.
- The orchestrator MUST NOT silently add tasks that were not requested, and MUST
  NOT silently drop tasks that were.
- If the set cannot be resolved unambiguously, the batch records **stop
  condition S1**
  ([`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions)) and
  finishes through the terminating path
  ([`orchestration.md` §3.6](orchestration.md#36-the-terminating-path)). The
  outcome — `BLOCKED`, by aggregation rule 2 — is derived at `REPORTING`.

## 2. Task inventory

For each discovered task, record:

| Field | Required | Content |
|---|---|---|
| `task_id` | yes | Stable identifier, normally `issue-<number>` |
| `issue_number` | when applicable | The Issue number |
| `goal` | yes | What "done" means for this task, in one sentence |
| `expected_change_surface` | yes | Directories, files, modules, or contracts this task is expected to touch. `UNKNOWN` is a permitted value and has consequences — see [§5](#5-parallel-safety-determination) |
| `declared_dependencies` | yes | Task ids this task explicitly depends on; `[]` if none declared |
| `owning_ssot` | when applicable | The architecture/SSOT document governing the change |
| `verification` | yes | How this task's result will be verified |

An inventory entry with a missing required field **records the field name and
the exact reason it could not be established**, and gives that task **task state
`BLOCKED` and task result `BLOCKED`**. Both are assigned — they are separate
vocabularies ([`orchestration.md` §3](orchestration.md#3-state-models-and-outcomes)),
and the result without the state would leave the task with no legal terminal
state. The value is never fabricated and never silently emptied.

Such an entry is complete in the sense Phase 2 requires
([`orchestration.md` §2](orchestration.md#phase-2--inventory)): the recorded
field-and-reason *is* the entry's content for that field. A `BLOCKED` inventory
entry is therefore representable and reportable, and Phase 2's postcondition is
satisfiable without inventing data.

## 3. Task classification

Classify each task on two axes.

### 3.1 Executability (preflight result)

Determined by the preflight checks in
[`worker-contract.md`](worker-contract.md#1-preflight-validation).
A task that fails preflight takes task result `BLOCKED` and is **excluded from
wave construction**, but it remains in the inventory and in the final report.

#### 3.1.1 Propagation to dependents

Excluding a blocked task is not enough. Its dependents must be resolved **before
the graph is built**, or the exclusion silently corrupts the plan:

- Dropping the edge `A -> B` when A is `BLOCKED` would let B dispatch as though
  its prerequisite had landed — exactly the speculative execution
  [`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)
  forbids.
- Keeping the edge would leave B permanently unready, and an unready task with
  tasks remaining is batch-level stop condition S3
  ([`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions)) —
  halting a whole batch over one blocked prerequisite.

So: **every dependent of a `BLOCKED` task takes task result `BLOCKED` too,
transitively, naming the blocked prerequisite**. This runs *after* every
dependency source has been resolved (§4.1, §4.2) and *before* graph construction
(§4.3) — the ordering Phase 4 mandates
([`orchestration.md` §2](orchestration.md#phase-4--analysis)). Resolving
dependencies first matters: an undeclared structural or verification dependency
that has not been established yet cannot be propagated along, which would leave a
dependent of a blocked task eligible for dispatch. Those tasks are then excluded
from wave construction along with their prerequisite, and they stay in the
inventory and the report like any other `BLOCKED` task.

This is the same rule [`failure-recovery.md`
§4](failure-recovery.md#4-dependent-task-handling) applies when a task fails
during execution, applied one stage earlier. Both halves of failure isolation
still hold: dependents stop, and tasks that do **not** depend on the blocked task
are untouched and proceed normally.

### 3.2 Coupling

| Class | Definition | Consequence |
|---|---|---|
| `INDEPENDENT` | No dependency on, and no established change-surface overlap with, any other task in the set | Eligible to share a wave |
| `DEPENDENT` | Requires another task's result to exist first | Placed in a later wave than its dependencies |
| `OVERLAPPING` | Shares a change surface with another task, with no dependency direction | Must be serialized against that peer; direction chosen by the orchestrator and recorded |
| `UNCERTAIN` | Dependency or overlap could not be established | **Treated as `DEPENDENT` or `OVERLAPPING`**, never as `INDEPENDENT` |

## 4. Dependency model

### 4.1 Dependency sources

A dependency from task B to task A (`A -> B`, "B depends on A") is established
when any of these holds:

| Source | Example |
|---|---|
| Declared | The Issue states "depends on #101" / "blocked by #101" |
| Structural | B modifies or consumes an interface, schema, or contract that A introduces or changes |
| Sequential requirement | A's change must exist in the base before B's change is meaningful or verifiable |
| Verification | B's tests cannot pass until A is integrated |

### 4.2 Unknown dependencies

If it cannot be established whether `A -> B` exists, the pair is recorded as
`UNKNOWN` and **treated as a dependency**. The direction is chosen so that the
task with the broader or less certain change surface runs first, and the choice
is recorded with its reason.

This is a fail-closed rule. An unknown dependency is never resolved as "no
dependency".

### 4.3 Graph construction

Build a directed graph over non-`BLOCKED` tasks:

- one node per task,
- one edge `A -> B` per established or assumed dependency.

### 4.4 Cycle detection

Cycle detection runs **before execution planning**, over the whole graph.

If a cycle is found:

1. Planning stops immediately. No wave is constructed and no worker is
   dispatched.
2. The full cycle path is reported (e.g. `A -> B -> C -> A`).
3. Record **stop condition S2** with the cycle path as its reason
   ([`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions)).
   The outcome is not written here: batch outcome `BLOCKED` is derived at
   `REPORTING` by aggregation rule 2, the protocol's single outcome-writing
   point.
4. The batch then finishes its lifecycle through the terminating path
   ([`orchestration.md` §3.6](orchestration.md#36-the-terminating-path)):
   aggregate verification (`NOT RUN`, since nothing was integrated), cleanup, and
   the full report. Stopping analysis is not stopping the batch.
5. Resolution requires an explicit operator decision. The orchestrator MUST NOT
   break a cycle by dropping an edge on its own judgement.

A graph that cannot be checked for cycles is treated as containing one.

## 5. Parallel-safety determination

Being in the same wave requires **both** conditions:

1. **Dependency-free with respect to each other** — no edge in either
   direction, established or assumed.
2. **Non-overlapping change surfaces** — the two tasks are not expected to
   modify the same files, modules, or contracts.

### 5.1 Overlap determination

| Situation | Determination |
|---|---|
| Change surfaces are disjoint and both are known | Parallel-safe |
| Change surfaces intersect | Not parallel-safe → serialize |
| Either change surface is `UNKNOWN` | **Not parallel-safe** → serialize |
| Overlap is plausible but unconfirmed | **Not parallel-safe** → serialize |

Overlap is assessed at the granularity actually known. Two tasks touching the
same file are overlapping. Two tasks touching the same shared contract,
generated artifact, or configuration key are overlapping even if their file
lists differ.

### 5.2 Serialization of overlapping peers

Overlapping tasks are placed in **different waves**, ordered so that the task
whose result the other most likely needs to observe runs first. The chosen order
and its reason are recorded in the plan.

Serialization is a safety decision, not a failure. It is reported as such.

## 6. Wave construction

Waves are constructed by repeated extraction from the DAG:

```text
remaining := all non-BLOCKED tasks
wave_number := 1

while remaining is not empty:
    ready := { t in remaining : every dependency of t is already
                                assigned to an earlier wave }

    if ready is empty:                       # `remaining` holds only
        stop — unresolvable ordering;        # non-BLOCKED tasks, so this
              batch-level stop condition S3  # is genuine unresolvable ordering

    wave := a maximal subset of `ready` whose members are
            pairwise parallel-safe (see §5)

    tasks in `ready` excluded from `wave` for overlap
        are deferred to a later wave

    assign wave to wave_number
    remaining := remaining minus wave
    wave_number := wave_number + 1
```

Properties this guarantees:

- A task never appears before any of its dependencies.
- Two tasks in the same wave are always both dependency-free and
  non-overlapping with respect to each other.
- A wave may contain exactly one task. That is a normal outcome, not a
  degradation.
- Every non-`BLOCKED` task is assigned to exactly one wave.

### 6.1 Concurrency within a wave

Wave membership expresses **permission** to run concurrently. It does not
promise that the executing agent will do so.

- The number of workers actually running at once is capped by the concurrency
  policy in [`git-worktree.md`](git-worktree.md#4-concurrency-policy)
  (default: 3).
- A wave larger than the cap is dispatched in cap-sized groups. Task-level
  isolation and ordering guarantees are unchanged.
- An agent with no concurrent worker capability executes the wave's members one
  after another, still in isolation, still validated independently. See
  [`../SKILL.md`](../SKILL.md#silent-sequential-fallback-is-forbidden).

### 6.2 Wave advancement

A wave is complete when every member has **settled**: each holds either a
delivered result awaiting validation (task state `RESULT_READY`) or a terminal
task state ([`orchestration.md` §3.2](orchestration.md#32-task-state)). Terminal
task results are produced by Phases 7–8, not by the wave finishing
([`orchestration.md` §2](orchestration.md#phase-6--execution)).

Before the next wave begins:

1. Results from the completed wave are validated
   ([`worker-contract.md`](worker-contract.md#4-worker-result-validation)).
2. Tasks whose dependencies did not reach `SUCCESS` are re-classified as
   `BLOCKED` — see
   [`failure-recovery.md`](failure-recovery.md#4-dependent-task-handling).
3. The remaining plan is re-checked against the current base if any integration
   has occurred in the meantime.

A wave MUST NOT start while an earlier wave has a member in a non-terminal
state.

## 7. Recording the plan

The plan is part of the batch report and MUST contain:

- the task inventory,
- the pairwise dependency determinations, including every `UNKNOWN` and how it
  was resolved,
- the DAG and the cycle-check result,
- the wave assignment,
- for each wave, the parallel/sequential classification of its members and the
  reason for every serialization,
- every task excluded as `BLOCKED`, with the failing condition.

A plan that omits its `UNKNOWN` resolutions is incomplete: those resolutions are
exactly the safety decisions a reviewer needs to check.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`orchestration.md`](orchestration.md) — phase contracts and state model
- [`worker-contract.md`](worker-contract.md) — preflight and result validation
- [`git-worktree.md`](git-worktree.md) — isolation and concurrency policy
- [`examples.md`](examples.md) — worked scenarios
