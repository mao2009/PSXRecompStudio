# Orchestration

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Defines the phase-by-phase contract of a batch, the batch and task state models,
integration ordering, aggregate verification, cleanup, and the final report.

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

### Phase 1 — DISCOVERY

- **Precondition:** A batch has been requested and this Skill applies.
- **Obligation:** Resolve the requested work into an explicit, enumerated
  candidate task set.
- **Postcondition:** A written list of task identifiers exists.
- **Abort:** The set cannot be resolved unambiguously → batch `BLOCKED`.

### Phase 2 — INVENTORY

- **Precondition:** Phase 1 postcondition.
- **Obligation:** Record each task per
  [`dependency-analysis.md` §2](dependency-analysis.md#2-task-inventory).
- **Postcondition:** Every task has a complete inventory entry.
- **Abort:** A required inventory field cannot be established → that task is
  `BLOCKED` (the batch continues with the remainder).

### Phase 3 — PREFLIGHT

- **Precondition:** Phase 2 postcondition.
- **Obligation:** Run every preflight check per
  [`worker-contract.md` §1](worker-contract.md#1-preflight-validation).
- **Postcondition:** Every task is either preflight-clean or `BLOCKED` with a
  recorded failing condition.
- **Abort:** A preflight condition cannot be evaluated → that task is `BLOCKED`.
  An unevaluable condition is never treated as passing.

### Phase 4 — ANALYSIS

- **Precondition:** Phase 3 postcondition.
- **Obligation:** Dependency analysis, DAG construction, cycle detection per
  [`dependency-analysis.md` §4](dependency-analysis.md#4-dependency-model).
- **Postcondition:** A cycle-free graph over all non-`BLOCKED` tasks exists.
- **Abort:** A cycle is found, or the graph cannot be checked → batch `BLOCKED`,
  cycle path reported, operator decision required.

### Phase 5 — PLANNING

- **Precondition:** Phase 4 postcondition.
- **Obligation:** Build execution waves and classify parallel safety per
  [`dependency-analysis.md` §6](dependency-analysis.md#6-wave-construction).
- **Postcondition:** Every non-`BLOCKED` task is assigned to exactly one wave;
  the plan is recorded.
- **Abort:** No task is ready and tasks remain → batch `BLOCKED`.

> Phases 1–5 constitute the mandatory planning stage. Dispatching a worker
> before Phase 5's postcondition holds violates
> [`../SKILL.md`](../SKILL.md#mandatory-preconditions).

### Phase 6 — EXECUTION

- **Precondition:** Phase 5 postcondition.
- **Obligation:** For each wave, in order: provision isolation
  ([`git-worktree.md`](git-worktree.md)), dispatch each member to a worker
  within the concurrency policy, and supervise until every member reaches a
  terminal result.
- **Postcondition:** Every member of the wave holds exactly one of `SUCCESS`,
  `NO_OP`, `BLOCKED`, `FAILED`.
- **Abort:** Isolation cannot be provisioned for a task → that task is
  `BLOCKED`; it is not run in a shared tree.

### Phase 7 — VALIDATION

- **Precondition:** A task has reached a terminal result.
- **Obligation:** Validate the result against the output contract
  ([`worker-contract.md` §4](worker-contract.md#4-worker-result-validation)) and
  run semantic conflict detection
  ([`worker-contract.md` §5](worker-contract.md#5-semantic-conflict-detection)).
- **Postcondition:** The result is either integration-eligible or explicitly
  ineligible with a recorded reason.
- **Abort:** Validation is inconclusive → not integration-eligible.

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

- **Precondition:** All integration attempts have concluded.
- **Obligation:** Aggregate verification (§5).
- **Postcondition:** The integrated result is verified as a whole, or the
  failure is recorded.

### Phase 10 — CLEANUP

- **Precondition:** Phase 9 concluded.
- **Obligation:** Remove isolation artifacts for integrated work only
  ([`git-worktree.md` §6](git-worktree.md#6-cleanup)).
- **Postcondition:** No stale worktree or branch remains for integrated tasks.

### Phase 11 — REPORTING

- **Precondition:** Phase 10 concluded.
- **Obligation:** Produce the batch report (§7).
- **Postcondition:** Every task in the inventory appears in the report with a
  terminal classification.

## 3. State models

State names are normative vocabulary. Reporting MUST use these exact strings.

### 3.1 Batch state

```text
BATCH_INITIALIZING → PLANNING → SCHEDULING → RUNNING → WAITING_FOR_MERGE
                                                              ↓
                                            MERGING → CLEANUP → COMPLETED
```

Allowed transitions:

| From | To |
|---|---|
| `BATCH_INITIALIZING` | `PLANNING`, `FAILED` |
| `PLANNING` | `SCHEDULING`, `FAILED` |
| `SCHEDULING` | `RUNNING`, `FAILED` |
| `RUNNING` | `WAITING_FOR_MERGE`, `FAILED` |
| `WAITING_FOR_MERGE` | `MERGING`, `COMPLETED`, `FAILED` |
| `MERGING` | `CLEANUP`, `FAILED` |
| `CLEANUP` | `COMPLETED`, `FAILED` |
| `COMPLETED` | — (terminal) |
| `FAILED` | — (terminal) |

Any transition not listed is illegal. A batch observed in an unrecognized state
transitions to `FAILED`; it is never assumed to be healthy.

### 3.2 Task state

| From | To |
|---|---|
| `WAITING_DEPENDENCY` | `WORKER_STARTING` |
| `WAITING_FOR_WORKER` | `WORKER_STARTING`, `BLOCKED` |
| `WORKER_STARTING` | `READY_FOR_DISPATCH`, `WORKER_RUNNING`, `WORKER_RETRYING`, `WORKER_FAILED`, `FAILED` |
| `READY_FOR_DISPATCH` | `DISPATCHED`, `WORKER_FAILED`, `FAILED` |
| `DISPATCHED` | `WORKER_RUNNING`, `WORKER_FAILED`, `FAILED` |
| `WORKER_RUNNING` | `RESULT_READY`, `WORKER_RETRYING`, `WORKER_FAILED` |
| `WORKER_RETRYING` | `WORKER_STARTING`, `WORKER_FAILED` |
| `RESULT_READY` | `WAITING_FOR_APPROVAL` |
| `WAITING_FOR_APPROVAL` | `READY_FOR_MERGE`, `RESULT_READY` |
| `READY_FOR_MERGE` | `MERGING` |
| `MERGING` | `COMPLETED`, `FAILED`, `RESULT_READY` |
| `BLOCKED` | `WAITING_FOR_WORKER` |
| `WORKER_FAILED` | — (terminal) |
| `COMPLETED` | — (terminal) |
| `FAILED` | — (terminal) |

Terminal states: `WORKER_FAILED`, `COMPLETED`, `FAILED`.

Notes on the two return edges, both of which are safety features:

- `WAITING_FOR_APPROVAL → RESULT_READY` — the approval became invalid (for
  example the content changed). The task returns for fresh approval; it does not
  proceed on a stale one.
- `MERGING → RESULT_READY` — the merge attempt required the result to change
  (typically a rebase that moved the head). The approval is re-established
  against the new head before merging is retried.

`BLOCKED → WAITING_FOR_WORKER` is the only path out of `BLOCKED`, and it
requires the blocking condition to have been resolved explicitly.

### 3.3 Result classification

The four terminal classifications and their meanings are normative and defined
in [`../SKILL.md`](../SKILL.md#result-vocabulary).

Mapping from task state to classification:

| Task state | Classification |
|---|---|
| `COMPLETED` with changes integrated | `SUCCESS` |
| `COMPLETED` with no change required | `NO_OP` |
| `BLOCKED` | `BLOCKED` |
| `WORKER_FAILED`, `FAILED` | `FAILED` |

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

- The batch is `FAILED` even when every individual task reported `SUCCESS`.
- The failure is reported against the integrated base, identifying which tasks
  are implicated.
- Nothing is reverted automatically; reverting is an operator decision.

A batch whose aggregate verification was not run MUST report aggregate
verification as `NOT RUN`. It MUST NOT be reported as passing.

## 6. Cleanup

Cleanup applies to isolation artifacts only, and only for work that was
integrated. See [`git-worktree.md` §6](git-worktree.md#6-cleanup).

Artifacts for `FAILED` and `BLOCKED` tasks are **deliberately preserved** so the
work is recoverable and diagnosable. Cleanup failure never reverts a completed
merge; the two are independent.

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
| `classification` | `SUCCESS` / `NO_OP` / `BLOCKED` / `FAILED` |
| `wave` | Assigned wave, or `—` if `BLOCKED` at preflight |
| `branch` | Isolation branch |
| `integrated` | Whether it reached the base, and via which merge |
| `reason` | For non-`SUCCESS`: the exact condition, quoted |

### 7.3 Batch section

- Batch terminal state.
- Counts per classification.
- Aggregate verification result: `PASS` / `FAIL` / `NOT RUN`.
- Every fail-closed stop that occurred, with the condition that could not be
  established.
- Remaining work and required operator decisions.

### 7.4 Reporting honesty rules

- A task absent from the report is a defect in the report.
- `NO_OP` is reported as `NO_OP`, never folded into `SUCCESS`.
- Unrun verification is reported as `NOT RUN`, never as passing.
- A batch in which every task was `NO_OP` or `BLOCKED` did not succeed; it is
  reported as having produced no integrated change.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`dependency-analysis.md`](dependency-analysis.md) — planning stage detail
- [`worker-contract.md`](worker-contract.md) — worker and result contracts
- [`git-worktree.md`](git-worktree.md) — isolation and cleanup
- [`review-and-gates.md`](review-and-gates.md) — gates and merge delegation
- [`failure-recovery.md`](failure-recovery.md) — failure, retry, recovery
- [`examples.md`](examples.md) — worked scenarios
