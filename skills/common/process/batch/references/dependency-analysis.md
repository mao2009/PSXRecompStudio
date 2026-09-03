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
  outcome — `BLOCKED` by aggregation rule 2, absent a rule 1 match — is derived
  at `REPORTING`.

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
A task that fails preflight takes task state `BLOCKED` and task result `BLOCKED`.
An already-present change takes task state `COMPLETED` and task result `NO_OP`.
Both are **excluded from wave construction**, but remain in the inventory and in
the final report.

Those two are the **pre-execution terminal** tasks: a task already holding a
terminal task state *and* a terminal task result before Phase 6 begins — task
state `BLOCKED` with task result `BLOCKED`, or task state `COMPLETED` with task
result `NO_OP`. Section [3.1.1](#311-propagation-to-dependents) adds more of the
first kind, and a resumed batch may carry either kind forward.

The **executable task set** is the task inventory minus every pre-execution
terminal task. It is the set the dependency graph ([§4.3](#43-graph-construction))
and wave construction ([§6](#6-wave-construction)) operate on, and the set Phase 6
dispatches from ([`orchestration.md` §2](orchestration.md#phase-6--execution)).

`BLOCKED` alone is **not** the exclusion predicate, and the distinction is
load-bearing. A preflight `NO_OP` task is terminal but is not `BLOCKED`: treating
"non-`BLOCKED`" as "still to run" would put a finished task into a wave and
dispatch a worker to redo work the preflight already found present. In the other
direction, counting such a task as *remaining* would let a set that holds nothing
executable be reported as an unresolvable ordering — batch-level stop condition S3
([`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions)) — over
tasks that need no ordering at all.

Membership is fixed within Phase 4 — after propagation
([§3.1.1](#311-propagation-to-dependents)) and before graph construction
([§4.3](#43-graph-construction)) — and is **not** recomputed during execution. A
task that is blocked later, at Phase 6's prerequisite re-check
([`orchestration.md` §2](orchestration.md#phase-6--execution)), was a member: it
keeps the wave Phase 5 assigned it and reports that wave with an empty branch
([`orchestration.md` §7.2](orchestration.md#72-per-task-section)), which is
exactly how the report distinguishes it from a task that was never planned at
all.

Pre-execution terminal tasks stay in the inventory and in the report throughout.
Exclusion from the executable task set is not removal from the batch.

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

So: **every dependent that is not already terminal takes task state `BLOCKED`
and task result `BLOCKED`, transitively, naming the prerequisite that prevented
execution**. The triggering prerequisite result may be `FAILED`, `BLOCKED`,
`NO_OP` or any other non-`SUCCESS` result.

**Propagation never reclassifies a dependent that is already terminal.** A
dependent found already implemented at preflight holds task state `COMPLETED`
with task result `NO_OP`, and `COMPLETED` has no outgoing edge
([`orchestration.md` §3.2](orchestration.md#32-task-state)); blocking it would
take an illegal transition and leave it carrying two task results, against
invariant 1 of [`../SKILL.md`](../SKILL.md#invariants). It keeps the result it
has, and the non-`SUCCESS` prerequisite is recorded against it as context rather
than as a reclassification. The same holds for a dependent already `BLOCKED`:
it is already `BLOCKED`, and the additional prerequisite is added to its recorded
reason.

This matters to the batch outcome, not just to bookkeeping. Two already-implemented
Issues, one declaring a dependency on the other, are both task result `NO_OP` and
the batch is outcome `NO_OP` by aggregation rule 7
([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome));
reclassifying the dependent would produce outcome `BLOCKED` by rule 4 and report
a block that never happened. This runs *after* every
dependency source has been resolved (§4.1, §4.2) and *before* graph construction
(§4.3) — the ordering Phase 4 mandates
([`orchestration.md` §2](orchestration.md#phase-4--analysis)). Resolving
dependencies first matters: an undeclared structural or verification dependency
that has not been established yet cannot be propagated along, which would leave a
dependent of a blocked task eligible for dispatch. Those tasks are then excluded
from wave construction along with their prerequisite, and they stay in the
inventory and the report like any other `BLOCKED` task. In the vocabulary of
§3.1 they become pre-execution terminal tasks and leave the executable task set.

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

Build a directed graph over the **executable task set**
([§3.1](#31-executability-preflight-result)) — the inventory minus every
pre-execution terminal task:

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
   The outcome is not written here: it is derived at `REPORTING` by §3.5 —
   rule 2, absent a rule 1 match — which is the protocol's single
   outcome-writing point.
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
remaining := the executable task set        # inventory minus every
                                            # pre-execution terminal task (3.1)
wave_number := 1

while remaining is not empty:
    ready := { t in remaining : every dependency of t is already
                                assigned to an earlier wave }

    if ready is empty:                       # `remaining` holds only
        stop — unresolvable ordering;        # executable tasks, so this
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
- Every task in the executable task set is assigned to exactly one wave, and no
  pre-execution terminal task is assigned to any wave — so no worker is ever
  dispatched for a task that was already terminal at Phase 5
  ([§3.1](#31-executability-preflight-result)).

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

Two different conditions govern a wave, and conflating them is the mistake this
section exists to prevent.

**Settling** is a property of one member. A member has settled when it holds
either a delivered result awaiting validation (task state `RESULT_READY`) or a
terminal task state ([`orchestration.md` §3.2](orchestration.md#32-task-state)).
Settling is what Phase 6's postcondition requires
([`orchestration.md` §2](orchestration.md#phase-6--execution)); terminal task
results are produced by Phases 7–8, not by the wave finishing.

**The wave barrier** is a property of the whole wave, and it is what the *next*
wave waits on. Wave N+1 MUST NOT start until every one of these holds for wave N:

1. **Every member has settled.** None is still executing.
2. **Every delivered result has been validated and classified**
   ([`worker-contract.md` §4](worker-contract.md#4-worker-result-validation),
   [§5](worker-contract.md#5-semantic-conflict-detection)). No member is left in
   task state `RESULT_READY` awaiting a determination.
3. **Every integration-eligible result has been integrated**, through the gates
   and the Merge Skill, and **both** re-verifications each integration carries
   have settled: the re-verification against the refreshed base performed
   *before* the merge ([`orchestration.md` §4](orchestration.md#4-integration-ordering))
   and the post-merge verification the Merge Skill owns *after* it
   ([`review-and-gates.md` §2](review-and-gates.md#2-gate-order),
   [§8](review-and-gates.md#8-merge-ordering)). An integration whose post-merge
   verification has not settled is not yet an integration.
4. **Every member holds a terminal task state** ([`orchestration.md`
   §3.2](orchestration.md#32-task-state)), and therefore exactly one terminal
   task result.
5. **The next wave's prerequisites have been re-checked**, and every dependent of
   a prerequisite that did not reach task result `SUCCESS` has taken task state
   `BLOCKED` and task result `BLOCKED`, naming that prerequisite
   ([`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)).
   The remaining plan is re-checked against the current base whenever an
   integration has moved it ([`git-worktree.md` §2.2](git-worktree.md#22-base-revision)).

Condition 3 is the one most easily lost. A next-wave member is provisioned from
the base **as it stands at provisioning time**, so a prerequisite that was
validated but not yet integrated is not yet in that base: its dependent would be
built, and verified, against a tree that does not contain its own premise. This
is the same guarantee
[`orchestration.md` §3.1](orchestration.md#31-batch-lifecycle-state) enforces on
the `INTEGRATION → EXECUTION` re-entry edge, stated here for the wave.

So the barrier is **not** "no member is in a non-terminal state" on its own.
`RESULT_READY` is a legitimate settled state *within* a wave — Phase 6 stops
there deliberately — but it never crosses the barrier. A result still awaiting
validation, approval, merge or re-verification holds the wave, whether or not
anything depends on it: the barrier is a property of the wave, not of a single
dependency edge.

**The barrier orders waves; it never halts the batch.** These are different
things, and conflating them would contradict the failure isolation this protocol
exists to provide
([`orchestration.md` §3.6](orchestration.md#36-the-terminating-path)):

- A member that returns to `RESULT_READY` for a **fresh determination** — an
  approval invalidated because a peer's merge moved the base, which
  [`review-and-gates.md` §6.3](review-and-gates.md#63-invalidation) calls the
  protocol working correctly — is *progressing*, not stalled. It re-enters the
  gates and terminalizes. The barrier waits for that, exactly as it waits for a
  merge to land.
- A member that ends terminal `BLOCKED` or `FAILED` satisfies condition 4 at
  once. It does not hold the barrier open, and the next wave proceeds with
  whichever of its own members are still executable after condition 5.
- No member's gate stop turns into a batch-level stop. Only the six conditions of
  [`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions) halt a
  batch, and an ordinary gate stop is not among them
  ([`review-and-gates.md` §10](review-and-gates.md#10-gate-reporting)).

The barrier therefore delays a later wave until the current one is fully
resolved, and resolution always arrives: every member reaches a terminal task
state, by integration or by classification. What it must never do is let a later
wave build on a base that its prerequisite has not yet reached.

## 7. Recording the plan

The plan is part of the batch report and MUST contain:

- the task inventory,
- the pairwise dependency determinations, including every `UNKNOWN` and how it
  was resolved,
- the DAG and the cycle-check result,
- the wave assignment,
- for each wave, the parallel/sequential classification of its members and the
  reason for every serialization,
- every task excluded from the executable task set ([§3.1](#31-executability-preflight-result)),
  with the reason: the failing condition for a `BLOCKED` exclusion, or the
  evidence of prior implementation for a `COMPLETED` + `NO_OP` one.

A plan that omits its `UNKNOWN` resolutions is incomplete: those resolutions are
exactly the safety decisions a reviewer needs to check.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`orchestration.md`](orchestration.md) — phase contracts and state model
- [`worker-contract.md`](worker-contract.md) — preflight and result validation
- [`git-worktree.md`](git-worktree.md) — isolation and concurrency policy
- [`examples.md`](examples.md) — worked scenarios
