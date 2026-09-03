# Orchestration

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Defines the phase-by-phase contract of a batch, the batch and task state models,
the batch outcome model, integration ordering, aggregate verification, cleanup,
and the final report.

## 1. The orchestrator role

The orchestrator is whatever context is executing this protocol. Its
responsibilities are exactly:

| Does | Does not |
|---|---|
| Plan (inventory, analysis, graph, waves) | Implement task work |
| Dispatch and supervise workers | Write a worker's code for it |
| Validate worker results | Repair a worker's result to make it pass |
| Order and drive integration | Merge directly |
| Verify the aggregate and report | Close Issues |

**The orchestrator never substitutes its own implementation for a worker's.**
If a worker cannot complete a task, the task is `FAILED` or `BLOCKED`. It does
not become orchestrator work. This preserves the property that every integrated
change was produced and validated as an isolated, attributable unit.

## 2. Phase contract

Each phase has a precondition, an obligation, and a postcondition. A phase MUST
NOT begin before its predecessor's postcondition holds.

Each phase is also a **batch lifecycle state** of the same name (§3.1): the batch
is, at any moment, in the phase it is executing. Phase aborts below set the
**batch-level stop condition** (§3.4), which is not itself the batch outcome —
the outcome is derived from all such conditions at `REPORTING` (§3.5). A stop
never ends the lifecycle early: the batch still runs Phases 9–11 by way of the
terminating path (§3.6).

### Phase 1 — DISCOVERY

- **Precondition:** A batch has been requested and this Skill applies.
- **Obligation:** Resolve the requested work into an explicit, enumerated
  candidate task set.
- **Postcondition:** A written list of task identifiers exists.
- **Abort:** The set cannot be resolved unambiguously → batch-level stop
  condition S1 (§3.4); the batch takes the terminating path (§3.6).

### Phase 2 — INVENTORY

- **Precondition:** Phase 1 postcondition.
- **Obligation:** Record each task per
  [`dependency-analysis.md` §2](dependency-analysis.md#2-task-inventory).
- **Postcondition:** Every task has a complete inventory entry.
- **Abort:** A required inventory field cannot be established → **record the
  field name and the exact reason it could not be established**, and give that
  task **task state `BLOCKED` and task result `BLOCKED`** (the batch continues
  with the remainder). Both are assigned: they are separate vocabularies (§3),
  and a task carrying the result without the state would have no legal terminal
  state. That
  recorded field-and-reason *is* the entry's content for that field, so the
  postcondition above is satisfied without fabricating or silently emptying a
  value, and Phase 4 and the report have a defined artifact to read
  ([`dependency-analysis.md` §2](dependency-analysis.md#2-task-inventory)).

### Phase 3 — PREFLIGHT

- **Precondition:** Phase 2 postcondition.
- **Obligation:** Run every preflight check per
  [`worker-contract.md` §1](worker-contract.md#1-preflight-validation).
- **Postcondition:** Every task is either preflight-clean, holds task state
  `BLOCKED` and task result `BLOCKED` with a recorded failing condition, or was
  found already implemented and holds task state `COMPLETED` and task result
  `NO_OP`.
- **Abort:** A preflight condition cannot be evaluated → that task takes task
  state `BLOCKED` and task result `BLOCKED`. An unevaluable condition is never
  treated as passing.

### Phase 4 — ANALYSIS

- **Precondition:** Phase 3 postcondition.
- **Obligation:** In this order, per
  [`dependency-analysis.md` §4](dependency-analysis.md#4-dependency-model):
  1. **Resolve every dependency source** — declared, structural, sequential and
     verification — including pairs that resolve to `UNKNOWN`
     ([§4.1](dependency-analysis.md#41-dependency-sources),
     [§4.2](dependency-analysis.md#42-unknown-dependencies)). A dependency that
     has not been established yet cannot be propagated along.
  2. **Propagate task state `BLOCKED` and task result `BLOCKED`** transitively to
     the dependents of every task whose resolved prerequisite is non-`SUCCESS` —
     `FAILED`, `BLOCKED`, `NO_OP` or any other non-`SUCCESS` result — over those
     resolved relationships, **skipping any dependent that is already terminal**,
     which keeps the result it holds
     ([§3.1.1](dependency-analysis.md#311-propagation-to-dependents)).
  3. **Build the DAG and run cycle detection** over the **executable task set**
     as it stands after step 2 — the inventory minus every pre-execution terminal
     task, which is not the same as "the tasks that are not `BLOCKED`"
     ([§3.1](dependency-analysis.md#31-executability-preflight-result),
     [§4.3](dependency-analysis.md#43-graph-construction),
     [§4.4](dependency-analysis.md#44-cycle-detection)).

  The order matters: propagating before step 1 would miss undeclared dependents,
  leaving a task whose prerequisite is blocked still eligible for dispatch, and
  building the graph before step 2 would leave edges pointing at excluded tasks.
- **Postcondition:** A cycle-free graph over the executable task set exists.
- **Abort:** Either of the following records batch-level stop condition S2
  (§3.4); an operator decision is required and the batch takes the terminating
  path (§3.6):
  - **A cycle is found** → report the full cycle path.
  - **The graph cannot be checked** → report the cycle path as `UNAVAILABLE` and
    quote the exact reason the check could not be performed. An unevaluable graph
    is treated as containing a cycle, but it has no path to report, and one is
    never invented.

### Phase 5 — PLANNING

- **Precondition:** Phase 4 postcondition.
- **Obligation:** Build execution waves and classify parallel safety per
  [`dependency-analysis.md` §6](dependency-analysis.md#6-wave-construction).
- **Postcondition:** Every task in the **executable task set** is assigned to
  exactly one wave, no pre-execution terminal task is assigned to any wave
  ([`dependency-analysis.md` §3.1](dependency-analysis.md#31-executability-preflight-result)),
  and the plan is recorded.
- **Abort:** No task is ready while **unassigned members of the executable task
  set remain** → batch-level stop condition S3 (§3.4); the batch takes the
  terminating path (§3.6). An executable task set that is empty from the start is
  not S3, however the batch got there — every task already `BLOCKED`, every task
  already `COMPLETED` with task result `NO_OP`, or any mixture. There is no
  executable work left to order, and task-scoped terminal results never halt a
  batch (§3.6).

> Phases 1–5 constitute the mandatory planning stage. Dispatching a worker
> before Phase 5's postcondition holds violates
> [`../SKILL.md`](../SKILL.md#mandatory-preconditions).

### Phase 6 — EXECUTION

- **Precondition:** Phase 5 postcondition.
- **Obligation:** For each wave, in order — a wave contains only executable-task-set
  members, so a pre-execution terminal task is never dispatched here
  ([`dependency-analysis.md` §3.1](dependency-analysis.md#31-executability-preflight-result)):
  1. **Re-check prerequisites.** Before a wave starts, examine the task result of
     every prerequisite of its members. Transitively assign task state and task
     result `BLOCKED` to the **non-terminal** dependents of any prerequisite that
     did not reach `SUCCESS`, naming that prerequisite. A dependent that is
     already terminal keeps the result it holds and records the prerequisite as
     context only — the same guard Phase 4 step 2 applies, for the same reason:
     no edge leaves a terminal state (§3.2)
     ([`dependency-analysis.md` §6.2](dependency-analysis.md#62-wave-advancement),
     [`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)).
     Phase 4's propagation covered only the tasks already terminal *before*
     execution; a prerequisite can also end `FAILED`, `NO_OP` or `BLOCKED` during
     Phases 6–8,
     and `WAITING_DEPENDENCY → WAITING_FOR_WORKER → WORKER_STARTING` would
     otherwise let its dependent run anyway. This re-check is what keeps §4's
     ordering rule true and Phase 8's dependency precondition satisfiable.
  2. **Provision isolation** for each remaining member
     ([`git-worktree.md`](git-worktree.md)).
  3. **Dispatch** each member to a worker within the concurrency policy, and
     supervise until every member has **settled** — task state `RESULT_READY`,
     or a terminal task state (§3.2). Not "until every member reaches a terminal
     result": a successful worker settles at `RESULT_READY`, and its task result
     is produced by Phases 7–8.
- **Postcondition:** Every member of the wave has **settled** — none is still
  executing. A member holds either a delivered result awaiting validation (task
  state `RESULT_READY`) or a terminal task state (§3.2). The terminal *task
  result* is not produced here: a successful worker reaches `RESULT_READY`, and
  task result `SUCCESS` requires an integrated change (§3.3), so Phases 7–8
  produce it. Settling is this phase's postcondition for one wave; it is **not**
  sufficient to start the next one, which waits on the full wave barrier
  ([`dependency-analysis.md` §6.2](dependency-analysis.md#62-wave-advancement)).
- **Abort:** Isolation cannot be provisioned for a task → that task takes task
  state `BLOCKED` and task result `BLOCKED`; it is not run in a shared tree.

### Phase 7 — VALIDATION

- **Precondition:** A task has settled — its worker delivered a result (task
  state `RESULT_READY`), or it reached a terminal task state without one.
- **Obligation:** Validate the result against the output contract
  ([`worker-contract.md` §4](worker-contract.md#4-worker-result-validation)) and
  run semantic conflict detection
  ([`worker-contract.md` §5](worker-contract.md#5-semantic-conflict-detection)).
- **Postcondition:** The result is either integration-eligible or explicitly
  ineligible with a recorded reason.
- **Abort:** Validation is inconclusive → not integration-eligible, and
  classified by the row that matches which step was inconclusive
  ([`worker-contract.md` §4.3](worker-contract.md#43-validation-outcomes)):
  inconclusive **structural or substantive** validation is terminal `FAILED`, an
  **undeterminable semantic conflict** is terminal `BLOCKED`. The two are never
  merged into one "inconclusive" case.

### Phase 8 — INTEGRATION

- **Precondition:** Phase 7 marked the task integration-eligible, and every
  dependency of the task is already integrated.
- **Obligation:** Pass the review gate, then the approval gate, then delegate
  merge execution to the Merge Skill
  ([`review-and-gates.md`](review-and-gates.md)).
- **Postcondition:** The task is merged, or integration stopped with a recorded
  reason.
- **Abort:** Any gate is closed or unknown → integration stops for that task;
  the batch continues with tasks that do not depend on it.

### Phase 9 — VERIFICATION

- **Precondition:** All integration attempts have concluded, **or** the batch is
  terminating early (§3.6).
- **Obligation:** Aggregate verification (§5).
- **Postcondition:** The integrated result is verified as a whole, or the
  failure is recorded, or the verification is recorded as `NOT RUN`.

### Phase 10 — CLEANUP

- **Precondition:** Phase 9 concluded.
- **Obligation:** Remove isolation artifacts for integrated work only
  ([`git-worktree.md` §6](git-worktree.md#6-cleanup)).
- **Postcondition:** No stale worktree or branch remains for integrated tasks.

### Phase 11 — REPORTING

- **Precondition:** Phase 10 concluded.
- **Obligation:** Produce the batch report (§7).
- **Postcondition:** Every task in the inventory appears in the report with a
  terminal task result, and exactly one batch outcome (§3.4) is recorded.

## 3. State models and outcomes

This protocol tracks progress and results in **six distinct vocabularies**. Each
is a closed set, and none is interchangeable with another. Where the same
spelling appears in more than one vocabulary it denotes a different thing in
each; the mapping between them is always stated, never assumed.

| Vocabulary | Scope | Values | Defined in |
|---|---|---|---|
| **Batch lifecycle state** | The batch — where it is in the protocol | `DISCOVERY`, `INVENTORY`, `PREFLIGHT`, `ANALYSIS`, `PLANNING`, `EXECUTION`, `VALIDATION`, `INTEGRATION`, `VERIFICATION`, `CLEANUP`, `REPORTING`, `COMPLETED` | §3.1 |
| **Task state** | One task — where it is in its own progression | `WAITING_DEPENDENCY`, `WAITING_FOR_WORKER`, `WORKER_STARTING`, `READY_FOR_DISPATCH`, `DISPATCHED`, `WORKER_RUNNING`, `WORKER_RETRYING`, `RESULT_READY`, `WAITING_FOR_APPROVAL`, `READY_FOR_MERGE`, `MERGING`, `BLOCKED`, `WORKER_FAILED`, `COMPLETED`, `FAILED` | §3.2 |
| **Task result** | One task — how it ended | `SUCCESS`, `NO_OP`, `BLOCKED`, `FAILED` | §3.3 |
| **Batch outcome** | The batch — how it ended | `SUCCESS`, `NO_OP`, `BLOCKED`, `FAILED` | §3.4 |
| **Worker delivery state** | One worker — what it delivered | `PENDING`, `RUNNING`, `DELIVERED`, `ORPHANED` | [`failure-recovery.md` §3](failure-recovery.md#3-worker-delivery-state) |
| **Failure category** | One failure — why it failed | The non-retryable and retryable categories, and `unknown` | [`failure-recovery.md` §1](failure-recovery.md#1-failure-classification) |

Rules of use:

- Reporting MUST use these exact strings, and MUST qualify every one of them with
  its vocabulary — "batch outcome `BLOCKED`", "task result `BLOCKED`", "task
  state `BLOCKED`". A bare symbol is not a report.
- **A shared spelling is never an equality.** Batch lifecycle state `COMPLETED`
  is not task state `COMPLETED`, and neither of them is batch outcome `SUCCESS`.
- A batch has exactly one lifecycle state at any moment and — once it reaches
  `COMPLETED` — exactly one outcome. A terminal task has exactly one state and
  exactly one result.

Four further enumerations exist in this protocol and are **not** state, result or
outcome vocabularies. They are closed sets too, defined where they are used, and
they never substitute for the six above:

| Enumeration | Values | Defined in |
|---|---|---|
| Gate outcome | `OPEN` / `CLOSED` / `UNKNOWN` | [`review-and-gates.md` §1](review-and-gates.md#1-the-gate-principle) |
| Verification result | `PASS` / `FAIL` / `NOT RUN` | §5 |
| Coupling determination | `INDEPENDENT` / `DEPENDENT` / `OVERLAPPING` / `UNCERTAIN` | [`dependency-analysis.md` §3.2](dependency-analysis.md#32-coupling) |
| Dependency determination | established / `UNKNOWN`, the latter also being a permitted `expected_change_surface` value | [`dependency-analysis.md` §4.2](dependency-analysis.md#42-unknown-dependencies) |

`UNKNOWN` therefore appears in three of these four, and lowercase `unknown` is a
failure category besides. They are unrelated: a gate whose status could not be
established, a dependency or change surface that could not be established, and a
failure that could not be classified. As everywhere else here, each occurrence is
qualified by what it belongs to — "gate `UNKNOWN`", "dependency `UNKNOWN`",
"category `unknown`" — and none of them is ever a state, a task result or a batch
outcome. What they share is only the fail-closed treatment: every one of them
resolves to the safe side ([`../SKILL.md`](../SKILL.md#fail-closed-rules)).

The Merge Skill ([`../../merge/SKILL.md`](../../merge/SKILL.md)) has a state
machine of its own, which this protocol neither extends nor redefines. Its states
are that Skill's, even where a name coincides with one here — a task in this
protocol's task state `MERGING` is a task the Merge Skill has been asked to
merge, and how that Skill represents the merge internally is its own concern
([`review-and-gates.md` §7](review-and-gates.md#7-merge-execution--delegated)).

### 3.1 Batch lifecycle state

A batch's lifecycle state is **the phase it is executing** (§2), plus the
terminal state `COMPLETED` that follows Phase 11. Phase and lifecycle state are
deliberately one vocabulary, so there is no second naming to keep in sync and
"where is this batch" always has exactly one answer.

```text
Normal path
  DISCOVERY → INVENTORY → PREFLIGHT → ANALYSIS → PLANNING
      → EXECUTION → VALIDATION → INTEGRATION      (re-entered while waves remain)
      → VERIFICATION → CLEANUP → REPORTING → COMPLETED

Terminating path (§3.6), available from every state up to INTEGRATION
  <current state> → VERIFICATION → CLEANUP → REPORTING → COMPLETED
```

Allowed transitions:

| From | To |
|---|---|
| `DISCOVERY` | `INVENTORY`, `VERIFICATION` † |
| `INVENTORY` | `PREFLIGHT`, `VERIFICATION` † |
| `PREFLIGHT` | `ANALYSIS`, `VERIFICATION` † |
| `ANALYSIS` | `PLANNING`, `VERIFICATION` † |
| `PLANNING` | `EXECUTION`, `VERIFICATION` † |
| `EXECUTION` | `VALIDATION`, `VERIFICATION` † |
| `VALIDATION` | `INTEGRATION`, `EXECUTION` ‡, `VERIFICATION` † |
| `INTEGRATION` | `EXECUTION` ‡, `VERIFICATION` (the normal exit, and also the terminating one †) |
| `VERIFICATION` | `CLEANUP` |
| `CLEANUP` | `REPORTING` |
| `REPORTING` | `COMPLETED` |
| `COMPLETED` | — (terminal) |

† Terminating transition — taken when the batch stops early (§3.6).
‡ Wave re-entry — taken while the plan still holds unexecuted waves **and the
current wave has crossed the wave barrier**
([`dependency-analysis.md` §6.2](dependency-analysis.md#62-wave-advancement)),
which is the single definition of what a wave must satisfy before the next one
starts; it is not restated here. The `VALIDATION → EXECUTION` edge is the
**direct** re-entry, available only when the barrier was crossed without any
integration being needed — every result of the current wave terminal and
non-eligible, all `NO_OP`, `BLOCKED` or `FAILED`. Whenever an integration-eligible
result exists, the batch passes through `INTEGRATION` and re-enters from there
instead: a later wave whose prerequisite was validated but not integrated would
otherwise execute against a base that does not contain it.

Notes:

- **Phases 9–11 are on every path.** There is no edge into `CLEANUP` except from
  `VERIFICATION`, and no edge into `COMPLETED` except from `REPORTING`. A batch
  that stops in Phase 1 therefore still runs aggregate verification (§5), cleanup
  (§6) and reporting (§7), exactly as one that merged every task does. Aggregate
  verification that could not be run is reported `NOT RUN` (§5); it is never
  skipped silently.
- **`COMPLETED` MUST NOT be entered unless exactly one batch outcome (§3.4) has
  been recorded.** A `COMPLETED` batch carrying no outcome is an invalid state,
  not a passing one.
- `COMPLETED` is the only terminal lifecycle state and has no outgoing edge. A
  completed batch is never resumed; a re-run is a new batch with a fresh plan.
- Phases 6–8 operate per task and repeat per wave, so `EXECUTION`, `VALIDATION`
  and `INTEGRATION` may be re-entered while unexecuted waves remain. Re-entry
  advances the *batch* through a plan fixed at `PLANNING`; it never re-dispatches
  a task that already reached a terminal task state (§3.2). This does not weaken
  the phase-ordering rule of §2: re-entering `EXECUTION` requires Phase 5's
  postcondition, which held when the wave plan was recorded and continues to
  hold, and each wave still satisfies Phase 6's postcondition for its own members
  before the next wave starts
  ([`dependency-analysis.md` §6.2](dependency-analysis.md#62-wave-advancement)).
- **There is no batch-level waiting state.** Waiting is a task property. While
  tasks sit in `WAITING_DEPENDENCY`, `WAITING_FOR_WORKER` or
  `WAITING_FOR_APPROVAL`, the batch is in `EXECUTION`, `VALIDATION` or
  `INTEGRATION` — whichever phase it is executing.
- Any transition not listed is illegal. A batch observed in an **unrecognized
  lifecycle state**, or observed taking a transition this table does not permit,
  is outside this model, so the model cannot govern its exit.
  The observation is **recorded as batch-level stop condition S6** (§3.4), and
  the batch then takes the terminating procedure of §3.6 in full — stop dispatching, stop unsafe
  integration, and classify the pending work once no worker is still running —
  before moving to `VERIFICATION` to finish Phases 9–11. Those steps are not
  optional here: corruption observed during `EXECUTION` or `INTEGRATION` would
  otherwise let verification and cleanup begin while workers were still active,
  and the report could lack exactly one terminal result per task. This is the
  only transition into `VERIFICATION` not listed
  in the table above, and it exists so that a corrupted batch is still verified,
  cleaned up and reported rather than abandoned. The outcome is **not** assigned
  here: §3.5 rule 1 derives `FAILED` from the recorded observation at
  `REPORTING`, like every other outcome. A batch in an unrecognized state is
  never assumed healthy.

### 3.2 Task state

| From | To |
|---|---|
| `WAITING_DEPENDENCY` | `WAITING_FOR_WORKER`, `BLOCKED` |
| `WAITING_FOR_WORKER` | `WORKER_STARTING`, `BLOCKED` |
| `WORKER_STARTING` | `READY_FOR_DISPATCH`, `WORKER_RETRYING`, `WORKER_FAILED`, `FAILED`, `BLOCKED` ¶ |
| `READY_FOR_DISPATCH` | `DISPATCHED`, `WORKER_FAILED`, `FAILED`, `BLOCKED` ¶ |
| `DISPATCHED` | `WORKER_RUNNING`, `WORKER_RETRYING`, `WORKER_FAILED`, `FAILED`, `BLOCKED` ◊ |
| `WORKER_RUNNING` | `RESULT_READY`, `WORKER_RETRYING`, `WORKER_FAILED`, `BLOCKED` ◊ |
| `WORKER_RETRYING` | `WORKER_STARTING`, `WORKER_FAILED`, `BLOCKED` ◊ |
| `RESULT_READY` | `WAITING_FOR_APPROVAL`, `COMPLETED` §, `FAILED`, `BLOCKED` |
| `WAITING_FOR_APPROVAL` | `READY_FOR_MERGE`, `RESULT_READY`, `BLOCKED` |
| `READY_FOR_MERGE` | `MERGING`, `BLOCKED` ◊ |
| `MERGING` | `COMPLETED`, `FAILED`, `RESULT_READY`, `BLOCKED` ◊ |
| `BLOCKED` | — (terminal) |
| `WORKER_FAILED` | — (terminal) |
| `COMPLETED` | — (terminal) |
| `FAILED` | — (terminal) |

◊ **Termination-only transitions.** These exist solely so that the terminating
procedure (§3.6, step 4) can give every unfinished task a legal terminal state to
carry task state `BLOCKED` and task result `BLOCKED`. Each is **guarded**: it may
be taken only after
the task's worker has settled and any authorized in-flight merge has settled, and
never while work is in flight. Outside §3.6 they are not available — ordinary
execution never blocks a running task. Without them the terminating procedure
could assign a result with no valid state behind it, since §3.3 maps task result
`BLOCKED` only from task state `BLOCKED`.

A worker only ever begins executing through
`READY_FOR_DISPATCH → DISPATCHED → WORKER_RUNNING`. There is deliberately no
shortcut from `WORKER_STARTING` to `WORKER_RUNNING`: Phase 6 requires isolation
to be provisioned and the task to be dispatched before any execution
([§2, Phase 6](#phase-6--execution)), and a retry re-provisions and re-dispatches
on the same path
([`failure-recovery.md` §6.3](failure-recovery.md#63-orphan-handling)). The same
sequence therefore governs a first attempt and every retry, so no attempt can run
in an unprovisioned or undispatched state.

¶ These are the **isolation-provisioning** blocks. Phase 6 provisions a task's
worktree and branch before dispatching it, and a task whose isolation cannot be
provisioned takes task state `BLOCKED` and task result `BLOCKED` there
([§2, Phase 6](#phase-6--execution)). Without these two edges that assignment
would have no legal transition. **No worker is dispatched after the block**: the
task never reaches `DISPATCHED`, and it is never run in a shared tree.

These two edges also carry the terminating procedure's classification (§3.6,
step 4) for a task stopped in `WORKER_STARTING` or `READY_FOR_DISPATCH`. They are
deliberately **not** marked ◊: unlike the termination-only edges they are
reachable in ordinary execution as well, so every non-terminal state in this
table has a legal path to `BLOCKED` when the batch stops, and §3.6 step 4 never
needs a transition the model does not define.

Terminal states: `BLOCKED`, `WORKER_FAILED`, `COMPLETED`, `FAILED`. No edge
leaves any of them.

Waiting states that may still be re-dispatched: `WAITING_DEPENDENCY`,
`WAITING_FOR_WORKER`, `WORKER_RETRYING`. A terminal state is never re-dispatched
within the batch.

**Entry into the model.** Every task that is dispatched enters at exactly one of
two **initial** states, chosen by whether its prerequisites are satisfied:

| Entry state | Condition on the task at Phase 6 |
|---|---|
| `WAITING_DEPENDENCY` | At least one prerequisite has not yet reached task result `SUCCESS` |
| `WAITING_FOR_WORKER` | Every prerequisite is satisfied; the task is waiting only for a worker slot ([`git-worktree.md` §4](git-worktree.md#4-concurrency-policy)) |

`WAITING_FOR_WORKER` is **not** conditional on the slot being busy. It is the
"ready to run" state, and a task passes through it instantly when a slot is
already free; making it conditional would leave a dependency-free task in a wave
under the concurrency cap with no legal entry at all.

A task that entered at `WAITING_DEPENDENCY` therefore leaves it only for
`WAITING_FOR_WORKER` (or `BLOCKED`). There is deliberately **no** direct
`WAITING_DEPENDENCY → WORKER_STARTING` edge: every task, however it entered,
passes through `WAITING_FOR_WORKER` before provisioning begins, so the
concurrency cap applies uniformly
([`git-worktree.md` §4](git-worktree.md#4-concurrency-policy)) and a dependent
whose prerequisite has just landed cannot skip the slot it is still owed.

`WAITING_DEPENDENCY` is the only state with no incoming edge. Every other state,
`WAITING_FOR_WORKER` included, is reached by an edge listed above.

A **pre-execution terminal** task never enters that progression at all. Phases 2
and 3 assign it a terminal state together with a terminal task result directly —
task state `BLOCKED` with task result `BLOCKED`, or task state `COMPLETED` with
task result `NO_OP` — and it is excluded from the executable task set
([`dependency-analysis.md` §3.1](dependency-analysis.md#31-executability-preflight-result)).
A direct assignment at classification time is not a transition and needs no edge;
what it does need is a legal terminal state carrying exactly one result, which
§3.3 supplies.

Any transition not listed in the table is illegal. Observing one means the
task's tracking no longer matches this model, so it is recorded as batch-level
stop condition **S6** (§3.4) exactly as an unrecognized *batch* state is, and the
batch takes the terminating procedure of §3.6. It is never repaired by inventing
an edge, and the affected task is classified by §3.6 step 4 like any other whose
classification was never established.

Notes on the two return edges, both of which are safety features:

- `WAITING_FOR_APPROVAL → RESULT_READY` — the approval became invalid (for
  example the content changed). The task returns for fresh approval; it does not
  proceed on a stale one.
- `MERGING → RESULT_READY` — the merge attempt required the result to change
  (typically a rebase that moved the head). The approval is re-established
  against the new head before merging is retried.

Notes on result rejection — a result reaching `RESULT_READY` is **not**
guaranteed to advance:

- `RESULT_READY → FAILED` — covers **two** validated conclusions, and in both
  the orchestrator has finished classifying the result
  ([`worker-contract.md` §4.3](worker-contract.md#43-validation-outcomes)):
  - the result failed structural or substantive validation, or that validation
    was inconclusive, so it is invalid. The defect is in the result itself and
    the orchestrator MUST NOT repair it.
  - the worker honestly delivered `FAILED` and that report *passed* structural
    and substantive validation. Here the task result is `FAILED` because
    validation **confirmed** the failure, not because the report was defective.

  Either way the task now holds a **validated terminal `FAILED`** — which is
  exactly the classification §3.6 step 4 preserves when a batch stops, and what
  distinguishes it from a delivery the orchestrator never validated.
- `RESULT_READY → BLOCKED` — the result is valid in itself but cannot proceed.
  Two validated conclusions reach this edge:
  - a semantic conflict with a peer result, or a conflict determination that
    could not be made
    ([`worker-contract.md` §5.3](worker-contract.md#53-outcomes));
  - the worker honestly delivered `BLOCKED` — as
    [`worker-contract.md` §2.3](worker-contract.md#23-worker-obligations)
    requires of a worker that meets a blocking condition — and that report passed
    structural and substantive validation.

  Both need an operator decision, so the task takes task state `BLOCKED` and task
  result `BLOCKED`; it is terminal. As with `RESULT_READY → FAILED`, what makes
  the result terminal is that validation **concluded**, not that the worker said
  so.
- `WAITING_FOR_APPROVAL → BLOCKED` — a gate closed on a condition that requires
  an operator decision rather than a fresh determination
  ([`review-and-gates.md`](review-and-gates.md)).

§ `RESULT_READY → COMPLETED` is the terminal path for a **validated `NO_OP`**,
and the only edge that reaches `COMPLETED` without passing through approval and
merge. It exists because a `NO_OP` is not integration-eligible
([`review-and-gates.md` §3](review-and-gates.md#3-integration-eligibility-gate)):
there is nothing to approve and nothing to merge, so routing it through those
gates would either stall it forever or invite an approval for a change that does
not exist. The task takes task state `COMPLETED` with task result `NO_OP`
(§3.3). This bypasses **only** approval and merge — never validation: the `NO_OP`
claim is substantiated first
([`worker-contract.md` §4.2](worker-contract.md#42-substantive-validation)), an
unsubstantiated one is a validation failure and terminal `FAILED`. No Issue is
closed, and the operator decision the `NO_OP` carries stays open.

Which classification applies is decided by the single classification order in
[`worker-contract.md`](worker-contract.md#classification-order): a defective
result is `FAILED`, a result blocked by something outside itself is `BLOCKED`.
No result is ever both.

The `MERGING → COMPLETED` edge is guarded: it is legal only after the Merge Skill
confirms the authorized merge succeeded and its post-merge checks passed. It is
not a shortcut around integration, aggregate verification, cleanup, or reporting;
those batch phases still run before the batch lifecycle reaches `COMPLETED`.

Notes on `BLOCKED`:

- `WAITING_DEPENDENCY → BLOCKED` — a prerequisite did not reach task result
  `SUCCESS`, so the task's premise will not land (§4, and
  [`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)). A
  dependent never waits indefinitely on a prerequisite that already failed.
- `BLOCKED` is **terminal within the batch**. There is no redispatch edge out of
  it. Clearing a block requires an explicit operator decision
  ([`worker-contract.md` §1.2](worker-contract.md#12-preflight-rules)), and the
  re-run that follows is a new batch with a fresh plan — not a resumed state in
  this one. This is what separates a retryable waiting state from a terminal
  blocked state.
- A task in `BLOCKED` does **not** by itself decide the batch outcome. The two
  levels meet in exactly one place: the aggregation rule of §3.5.

### 3.3 Task result classification

The four terminal task results and their meanings are normative and defined in
[`../SKILL.md`](../SKILL.md#task-result-vocabulary).

Mapping from task state to task result:

| Task state | Task result |
|---|---|
| `COMPLETED` with changes integrated | `SUCCESS` |
| `COMPLETED` with no change required | `NO_OP` |
| `BLOCKED` | `BLOCKED` |
| `WORKER_FAILED`, `FAILED` | `FAILED` |

Task state `COMPLETED` is therefore not itself a result: it maps to `SUCCESS` or
to `NO_OP` depending on whether a change was required, and the two are never
merged ([`../SKILL.md`](../SKILL.md#task-result-vocabulary)).

### 3.4 Batch outcome

A batch outcome answers a different question from a lifecycle state. The
lifecycle state says *how far the batch got*. The outcome says *what it
produced*. `COMPLETED` means the batch ran its post-processing and reporting to
the end — it does **not** mean the batch succeeded. All four combinations below
are valid and MUST be expressible:

```text
lifecycle COMPLETED + outcome SUCCESS   the batch did what it set out to do
lifecycle COMPLETED + outcome NO_OP     the batch was well-formed; nothing was required
lifecycle COMPLETED + outcome BLOCKED   it stopped on a condition needing an operator decision
lifecycle COMPLETED + outcome FAILED    work was attempted and did not succeed
```

| Outcome | Meaning |
|---|---|
| `SUCCESS` | Every task in the inventory reached task result `SUCCESS`, and aggregate verification passed |
| `NO_OP` | The batch was well-formed, required no change, and integrated nothing |
| `BLOCKED` | The batch stopped, or ended with outstanding work, on a condition that requires an operator decision |
| `FAILED` | Work was required and attempted and did not succeed — at task level, or on the integrated whole — or the batch's own execution violated this model (§3.1) |

#### Batch-level stop conditions

A **batch-level stop condition** stops the batch itself. It is the only kind of
condition that triggers the terminating path (§3.6), and there are exactly six:

| # | Stop condition | Where it is detected |
|---|---|---|
| S1 | The requested task set cannot be resolved unambiguously | Phase 1 ([`dependency-analysis.md` §1](dependency-analysis.md#1-task-discovery)) |
| S2 | A dependency cycle exists, or the graph cannot be checked for cycles | Phase 4 ([`dependency-analysis.md` §4.4](dependency-analysis.md#44-cycle-detection)) |
| S3 | No task is ready while unassigned members of the **executable task set** remain — the ordering is unresolvable. An empty executable task set is not S3, whether its tasks ended `BLOCKED` or `COMPLETED` with task result `NO_OP` ([`dependency-analysis.md` §3.1](dependency-analysis.md#31-executability-preflight-result)) | Phase 5 ([`dependency-analysis.md` §6](dependency-analysis.md#6-wave-construction)) |
| S4 | A mandatory planning artifact cannot be produced **for a reason no other stop condition already names** — S1 covers an unresolvable task set, S2 an uncheckable graph or a cycle, S3 an unresolvable ordering, and each of those is recorded as itself | Phases 1–5 ([`../SKILL.md`](../SKILL.md#mandatory-preconditions)) |
| S5 | A fail-closed stop leaves **the batch as a whole** unable to continue safely — unreadable persisted state, for example | Phases 1–8 ([`../SKILL.md`](../SKILL.md#fail-closed-rules)) |
| S6 | The batch's own execution violated this model (§3.1) — it was observed in an **unrecognized lifecycle state**, or it took a **transition the model does not permit**, at batch or task level | Any phase |

S6 is the one stop condition whose outcome is **not** `BLOCKED`: §3.5 rule 1
matches it and ranks above rule 2, so the batch is `FAILED`. It is listed here
anyway because §3.6 and §7.3 require every terminating batch to name the stop
condition that caused it, and a corrupted batch must not be the one case that
reports none. It is unbounded by phase, unlike S5, because corruption can be
observed anywhere.

S5 is deliberately narrow. Most fail-closed stops are *task*-scoped: an approval
whose validity cannot be established, or an undeterminable semantic conflict,
stops that task and gives it task state `BLOCKED` and task result `BLOCKED`, while
unrelated tasks continue — that is failure isolation
([`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)). S5
applies only when the condition makes any further work unsafe, whichever task it
belongs to.

S5 is bounded to Phases 1–8 because Phases 9–11 *are* the terminating tail: there
is no earlier phase left to stop, and no terminating transition out of
`VERIFICATION`, `CLEANUP` or `REPORTING` exists (§3.1). A failure inside those
phases is recorded and reported in place — a failed aggregate verification is
reported `FAIL` (§5), a failed cleanup is reported and never reverts a merge
(§6) — and the batch still reaches `COMPLETED`.

#### Other inputs to a `BLOCKED` outcome

These are **not** stop conditions. They never halt dispatch and never trigger the
terminating path; they are simply facts the aggregation reads at `REPORTING`:

| Input | Where it arises |
|---|---|
| At least one task ended with task result `BLOCKED` — a failed preflight, isolation that could not be provisioned, an unavailable or rejected approval, a dependent of a non-`SUCCESS` task | Phases 3, 6–8 |
| The batch ended with an outstanding operator decision — a `NO_OP` task alongside integrated work, or aggregate verification that could not be run over work that *was* integrated | Phases 9–11 |

Every condition and input on this page is a **candidate**. None of them sets the
outcome by itself: §3.5's ordered rules are the sole authority, and they decide
which candidate wins when several hold at once. The report names both the winning
rule and the exact condition (§7.3).

`BLOCKED` is not a milder `FAILED`. `FAILED` means work was attempted and did not
work. `BLOCKED` means it was not attempted, or cannot be concluded, without a
decision only the operator can make.

### 3.5 Aggregation — from task results to the batch outcome

The batch outcome is **derived once, at `REPORTING`**, by the rules below. They
are evaluated **in order**, and the first match decides. They are exhaustive, and
they deliberately overlap — several may hold at once, which is precisely what the
ordering resolves. Every batch therefore has exactly one outcome, and that
outcome is derived rather than chosen.

| # | Condition | Batch outcome |
|---|---|---|
| 1 | Aggregate verification ran and failed (§5), **or** the batch's own execution violated this model — an unrecognized lifecycle state or an impermissible transition (§3.1, §3.2), recorded as S6 | `FAILED` |
| 2 | A batch-level stop condition was recorded (§3.4, S1–S5; **not** S6, which rule 1 has already matched) | `BLOCKED` |
| 3 | Any task result is `FAILED` | `FAILED` |
| 4 | Any task result is `BLOCKED` | `BLOCKED` |
| 5 | Aggregate verification is `NOT RUN` while at least one task result is `SUCCESS` | `BLOCKED` |
| 6 | At least one task result is `SUCCESS` **and** at least one is `NO_OP` | `BLOCKED` |
| 7 | The inventory is empty, or every task result is `NO_OP` | `NO_OP` |
| 8 | Every task result is `SUCCESS` **and** aggregate verification is `PASS` | `SUCCESS` |

Consequences worth stating explicitly, because each restates a rule this protocol
has carried from the start:

- **Batch outcome `SUCCESS` requires every task in the inventory to be task
  result `SUCCESS`.** Rule 8 is the only path to it, and rules 3, 4, 6 and 7
  remove from contention every batch containing a `FAILED`, `BLOCKED` or `NO_OP`
  task. This is the batch success rule of
  [`failure-recovery.md` §7.3](failure-recovery.md#73-batch-success-rule),
  restated as an outcome.
- **A stopped batch is diagnosed as stopped.** Rule 2 sits above the task-result
  rules on purpose. When the batch itself was stopped, §3.6 gives its pending
  tasks task state `BLOCKED` and task result `BLOCKED`, which would otherwise let
  rule 4 fire and report a cause — "some task was blocked" — that says nothing
  about why the batch
  stopped. Rule 2 keeps the stop condition itself as the reported cause.
- **A task result `BLOCKED` does not automatically make the batch outcome
  `BLOCKED`.** Rules 1 and 3 take precedence, so a batch that ran to completion
  holding both a `FAILED` and a `BLOCKED` task is `FAILED`. What is guaranteed is
  the weaker, correct statement: a batch containing a `BLOCKED` task is never
  `SUCCESS` and never `NO_OP`.
- **`NO_OP` is never promoted.** Rule 6 exists because a `NO_OP` task always
  leaves an operator decision open on its Issue
  ([`../SKILL.md`](../SKILL.md#task-result-vocabulary)); a batch that also
  integrated real work still has that decision outstanding, so it is `BLOCKED`
  and not `SUCCESS`.
- **Batch outcome `NO_OP` is a real outcome, not the absence of one.** It means
  the batch was well-formed, ran, integrated nothing and closed no Issue. It
  still carries the operator decision that every `NO_OP` task carries.
- **Aggregate verification is load-bearing.** Rule 1 makes a batch `FAILED` even
  when every task reported `SUCCESS`, and rule 5 refuses `SUCCESS` to a batch
  that integrated work it never verified as a whole (§5).

### 3.6 The terminating path

A **batch-level stop condition** (§3.4, S1–S6) **stops the work, not the
lifecycle.** The batch does not exit at that point. It takes the terminating
transition into `VERIFICATION` and runs Phases 9–11 to the end.

Only a batch-level stop condition does this. A task that ends `FAILED` or
`BLOCKED` does **not** trigger the terminating path: unrelated tasks keep running
and keep integrating, and the batch reaches Phase 9 by the normal route
([`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)). One
blocked task must never halt a batch — that would destroy the failure isolation
this protocol exists to provide.

On recording a batch-level stop condition, in this order:

1. **Record the exact condition**, verbatim, as it will appear in the report
   (§7.3), together with which of S1–S6 it is. The *outcome* is not recorded
   here: it is derived once, at `REPORTING`, by §3.5, which is the single
   authoritative value. Recording an outcome at stop time would create a second
   one that could disagree with it.
2. **Stop dispatching.** No further worker is dispatched and no unexecuted wave
   is started. Workers already running are allowed to finish, or are stopped; in
   either case their results are classified normally and never discarded
   silently.
3. **Stop unsafe integration.** No task is integrated from this point unless it
   was *already* established as integration-eligible through every gate
   ([`review-and-gates.md` §2](review-and-gates.md#2-gate-order)). Eligibility is
   never re-derived, relaxed or assumed in order to drain the queue
   ([`failure-recovery.md` §5](failure-recovery.md#5-integration-failure)).
4. **Classify the pending work**, once no worker is still running **and every
   authorized in-flight merge has settled** (see the merge rule below). Both
   conditions must hold first: a task in `MERGING` can satisfy "no worker
   running" while its merge is still landing, and `BLOCKED` is terminal, so
   classifying it early would strand a merge that then succeeds with no way back
   to `COMPLETED`. The rule is
   exhaustive over unclassified work: **every task for which no terminal
   classification has been established takes task state `BLOCKED` and task result
   `BLOCKED`**, naming the stop condition as its reason. "Established" means
   Phases 7–8 reached a conclusion for it, or Phase 2, 3 or 4 already made it
   pre-execution terminal. A task whose classification *was* established is not
   reclassified here; this step records the conclusion the protocol already
   reached, and only fills the gap where none exists. That covers
   the never-dispatched task, the task validated but not integrated, the
   dispatched task whose worker was stopped without delivering anything, and the
   dispatched task whose delivered result was never validated. A delivered result does not
   escape this rule. A worker that delivered while the task sat in
   `RESULT_READY`, `READY_FOR_MERGE` or `MERGING` produced work that termination
   then stopped from integrating, and task result `SUCCESS` is reachable only
   from task state `COMPLETED` **with changes integrated** (§3.3) — so an
   unintegrated delivered result is task state `BLOCKED` and task result
   `BLOCKED`, carrying the stop condition, with its branch and worktree preserved
   (§6). The one delivered
   result that may still complete is a **validated `NO_OP`**, which never needed
   integration and takes `RESULT_READY → COMPLETED` (§3.2). That is not an
   exception to the rule above but an instance of it: validation established the
   `NO_OP` classification at Phase 7, so this step records it rather than
   overriding it, and the task was never among those for which no classification
   exists.

   **Delivered is not validated, and only validated results keep their own
   classification.** A worker's `classification` field is an *input* to
   validation, never its conclusion
   ([`worker-contract.md` §4](worker-contract.md#4-worker-result-validation)), so
   a worker that delivered `FAILED` has not thereby produced task result
   `FAILED`. A task already in terminal task state `FAILED` or `WORKER_FAILED`
   keeps `FAILED`, because a conclusion was already established for it: `FAILED`
   by validation ([`worker-contract.md` §4.3](worker-contract.md#43-validation-outcomes)),
   `WORKER_FAILED` by the orphan path, where the worker delivered nothing to
   validate and the failure category itself decided the outcome
   ([`failure-recovery.md` §6.3](failure-recovery.md#63-orphan-handling)).
   Neither is reached by validating a delivered result into `WORKER_FAILED` —
   that route does not exist. The stop does not turn an established failure into
   a block. A result that is still
   **unvalidated** when the batch stops holds no terminal task result: it has
   settled in task state `RESULT_READY` and Phase 7 never ran on it, so it is
   task state `BLOCKED` and task result `BLOCKED` like every other unclassified
   delivery — a self-reported `FAILED` included. Recording `FAILED` for it would
   report a determination this protocol never made, and `BLOCKED` is exactly the
   value reserved for work that cannot be classified without further information
   (§3.4).

   The two cases are therefore distinguished by task state alone, which is what
   makes the rule mechanical rather than a judgement call: `FAILED` or
   `WORKER_FAILED` means validation already concluded and the result stands;
   `RESULT_READY` means it never did. A result that *was* validated but not
   integrated — in `WAITING_FOR_APPROVAL`, `READY_FOR_MERGE` or `MERGING` — is
   the case the preceding paragraph already settles: valid, unintegrated, and
   therefore `BLOCKED` because task result `SUCCESS` needs an integrated change
   (§3.3).

   An already-authorized merge that is in flight may be allowed to finish rather
   than abandoned, but it MUST settle **before this step classifies anything, and
   therefore before the batch enters `VERIFICATION`**,
   and only under an approval still valid at that moment
   ([`review-and-gates.md` §6.3](review-and-gates.md#63-invalidation)). A merge
   that cannot settle by then is task state `BLOCKED` and task result `BLOCKED`, and it is **not**
   completed afterwards. Letting it land after Phase 9 would move the integrated
   base out from under the aggregate verification that had just been run over it,
   leaving a change reportable as `SUCCESS` that nothing verified (§5). Anything
   short of settling in time is `BLOCKED`.

   The step waits for workers to settle for a reason. A task whose worker is
   still in flight is not classified while it runs: step 2 already decided
   whether that worker finishes or is stopped, and assigning `BLOCKED` to it
   would either produce a second terminal result or discard the one the worker
   returns — and every task has exactly one
   ([`../SKILL.md`](../SKILL.md#invariants)). Between them, the exhaustive rule
   and the settle-first requirement guarantee the property Phase 11 depends on:
   at `REPORTING`, every task in the inventory holds exactly one terminal task
   result. No task is left stateless, and none is dropped from the inventory.

   These classifications are a consequence of the stop, which is why §3.5 rule 2
   ranks above rule 4 — otherwise they would mask the cause.
5. **`VERIFICATION`** — run aggregate verification (§5) over whatever was
   actually integrated. If nothing was integrated, or it could not be run, it is
   reported `NOT RUN`. It is never reported as passing.
6. **`CLEANUP`** — apply the cleanup policy (§6). Isolation artifacts for
   integrated work are removed; artifacts for `FAILED`, `BLOCKED` and `NO_OP`
   tasks are preserved for diagnosis **where any were provisioned**, and reported
   as absent where none ever were
   ([`git-worktree.md` §6.2](git-worktree.md#62-what-is-deliberately-preserved)).
7. **`REPORTING`** — derive the batch outcome by §3.5, then produce the full
   batch report (§7): the plan section, every task in the inventory, the outcome,
   the rule that produced it, and the stop condition.
8. **`COMPLETED`** — the terminal lifecycle state, now carrying exactly one
   derived outcome.

A batch that stops during Phases 1–5 has integrated nothing and provisioned no
isolation, so steps 5 and 6 are cheap — but they are **not** optional. Their
obligation there is to report `NOT RUN` and "nothing to clean up" as established
facts, so a reader can tell a clean stop from an unfinished run.

**Skipping Phases 9–11 because the batch is blocked is a protocol violation**,
and so is reporting a blocked batch as though it had never started.

## 4. Integration ordering

Integration is **serial**. Exactly one task is integrated at a time.

Ordering rules, applied in this priority:

1. A task is never integrated before any task it depends on.
2. Within a wave, tasks are integrated in the order their results were validated.
3. A task whose dependency did not reach `SUCCESS` is not integrated; it becomes
   `BLOCKED`.

Before each integration:

- The base is refreshed and the task is re-verified against the *current* base,
  not the base it was developed on. Every prior integration in this batch has
  moved the base.
- Semantic conflict detection is re-run against the updated base
  ([`worker-contract.md` §5](worker-contract.md#5-semantic-conflict-detection)).

If integration of one task fails, the remaining queue is **not** drained
automatically. See
[`failure-recovery.md` §5](failure-recovery.md#5-integration-failure).

Merge execution itself is never performed here — it is delegated in full to the
Merge Skill. See [`review-and-gates.md`](review-and-gates.md).

## 5. Aggregate verification

Per-task verification proves each task in isolation. Aggregate verification
proves the *combination*, which no worker observed.

After all integrations conclude:

1. Verify the integrated base builds and its test suite passes as a whole.
2. Confirm that each `SUCCESS` task's intended change is actually present in the
   integrated base.
3. Confirm no integration silently reverted an earlier one.

If aggregate verification fails:

- The verification result `FAIL` is recorded here. It makes the batch outcome
  `FAILED` by §3.5 rule 1 when the outcome is derived at `REPORTING` — even when
  every individual task reported `SUCCESS`.
- The failure is reported against the integrated base, identifying which tasks
  are implicated.
- Nothing is reverted automatically; reverting is an operator decision.

A batch whose aggregate verification was not run MUST report aggregate
verification as `NOT RUN`. It MUST NOT be reported as passing. A batch that
integrated work it could not verify as a whole is `BLOCKED` (§3.5, rule 5).

## 6. Cleanup

Cleanup applies to isolation artifacts only, and only for work that was
integrated. See [`git-worktree.md` §6](git-worktree.md#6-cleanup).

Artifacts for `FAILED`, `BLOCKED` and `NO_OP` tasks are **deliberately
preserved** so the work is recoverable and diagnosable — but only where they
exist. A task blocked before Phase 6 provisioned anything, or found already
implemented at preflight, has no worktree and no branch, and is reported with
`—` rather than as preserved
([`git-worktree.md` §6.2](git-worktree.md#62-what-is-deliberately-preserved)).
Cleanup failure never reverts a completed merge; the two are independent.

## 7. Final reporting

The batch report follows [`../../reporting/SKILL.md`](../../reporting/SKILL.md) and
MUST additionally contain:

### 7.1 Plan section

- The task inventory.
- The dependency determinations, including every `UNKNOWN` and how it was
  resolved.
- The DAG and cycle-check result.
- The wave assignment and the parallel/sequential classification, with the
  reason for every serialization.
- Whether workers ran concurrently or serially, and why
  ([`worker-contract.md` §2](worker-contract.md#2-worker-abstraction)).

### 7.2 Per-task section

For every task in the inventory, without exception:

| Field | Content |
|---|---|
| `task_id` | Identifier |
| `task_result` | `SUCCESS` / `NO_OP` / `BLOCKED` / `FAILED` |
| `wave` | Assigned wave, or `—` if no wave was ever assigned |
| `branch` | The isolation branch, or `—` if no branch was created |
| `worktree` | The isolation worktree path, or `—` if no worktree was created |
| `integrated` | Whether it reached the base, and via which merge |
| `reason` | For non-`SUCCESS`: the exact condition, quoted |

A task keeps its assigned wave once Phase 5 has assigned one; the wave is `—`
only when no wave was ever assigned. Together, `wave`, `branch` and `worktree`
make the kinds of `BLOCKED` distinguishable, so a task blocked before planning is
never confused with one blocked after it:

| Where the task was blocked | `wave` | `branch` | `worktree` |
|---|---|---|---|
| Inventory or preflight (Phases 2–3) — never planned | `—` | `—` | `—` |
| Prerequisite re-check (Phase 6, step 1) — planned, blocked before provisioning | The assigned wave | `—` | `—` |
| Isolation provisioning (Phase 6, step 2) — planned, provisioning attempted and failed | The assigned wave | Whatever the failed attempt created, else `—` | Whatever the failed attempt created, else `—` |
| Validation, gate or integration — provisioned | The assigned wave | The isolation branch | The isolation worktree |

In every case `reason` still quotes the exact failing condition, so the two
`BLOCKED` kinds are distinguishable by both structure and stated cause.

A task found already implemented at preflight carries `—` in `wave`, `branch`
and `worktree` alike, for the same reason: as a pre-execution terminal task it
was never assigned a wave and never provisioned
([`dependency-analysis.md` §3.1](dependency-analysis.md#31-executability-preflight-result),
[`git-worktree.md` §6.2](git-worktree.md#62-what-is-deliberately-preserved)). It
is told apart from the `BLOCKED` rows by its task result, which is `NO_OP`, and
its `reason` states the evidence that the work was already present rather than a
failing condition
([`failure-recovery.md` §7.1](failure-recovery.md#71-no_op)). A `NO_OP` whose
worker *did* run is the other shape: it holds its assigned wave and its isolation
branch, both of which are reported.

### 7.3 Batch section

- Batch lifecycle state — `COMPLETED` for every batch that ran this protocol to
  the end (§3.1).
- Batch outcome — `SUCCESS` / `NO_OP` / `BLOCKED` / `FAILED`, with the
  aggregation rule number that produced it (§3.5). Every outcome has one; there
  is no outcome produced outside those rules.
- Any batch-level stop condition that was recorded, by its identifier (§3.4,
  S1–S6) and with its exact condition quoted. The rule number and the stop
  condition are two different facts and never substitute for one another: the
  rule says how the outcome was derived, the condition says what happened. A
  batch with no stop condition reports none.
- Counts per task result.
- Aggregate verification result: `PASS` / `FAIL` / `NOT RUN`.
- Every fail-closed stop that occurred, with the condition that could not be
  established.
- Remaining work and required operator decisions.

### 7.4 Reporting honesty rules

- A task absent from the report is a defect in the report.
- `NO_OP` is reported as `NO_OP`, never folded into `SUCCESS`.
- Unrun verification is reported as `NOT RUN`, never as passing.
- Batch lifecycle state `COMPLETED` is never reported as, or in place of, batch
  outcome `SUCCESS`. A report that gives a lifecycle state where an outcome is
  required is incomplete (§3.4).
- Batch outcome `SUCCESS` requires **every** task to be task result `SUCCESS`.
  Any `NO_OP`, `BLOCKED` or `FAILED` task means the batch did not pass
  ([`failure-recovery.md` §7.3](failure-recovery.md#73-batch-success-rule)).
- A batch in which every task was `NO_OP` or `BLOCKED` is additionally reported
  as having produced no integrated change.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`dependency-analysis.md`](dependency-analysis.md) — planning stage detail
- [`worker-contract.md`](worker-contract.md) — worker and result contracts
- [`git-worktree.md`](git-worktree.md) — isolation and cleanup
- [`review-and-gates.md`](review-and-gates.md) — gates and merge delegation
- [`failure-recovery.md`](failure-recovery.md) — failure, retry, recovery
- [`examples.md`](examples.md) — worked scenarios
