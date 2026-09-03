# Failure, Retry and Recovery

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Defines failure classification, retry policy and budget, dependent-task
handling, integration failure, recovery and orphan handling, resume semantics,
and corrupt-state handling.

## 1. Failure classification

Every failure is assigned exactly one category before any decision is made about
it. Categories determine retryability.

### 1.1 Non-retryable categories

| Category | Meaning |
|---|---|
| `code_error` | Compilation, syntax, type, or lint failure |
| `test_failure` | Verification ran and failed |
| `architecture_violation` | The change conflicts with the architecture/SSOT |
| `dependency_conflict` | Missing or incompatible dependency, unresolvable import |
| `mechanism_switch` | A different worker mechanism was proposed as a remedy |
| `launch_failure` | The worker could not be established at all |

These are **never** retried. Retrying them repeats a deterministic failure and
hides the real cause. The task becomes `FAILED` with the category recorded.

`mechanism_switch` is listed as non-retryable to enforce a rule that is easy to
violate accidentally: **switching mechanism is not a retry**. A task that failed
under one worker mechanism is not re-attempted under another to make it pass.

### 1.2 Retryable categories

| Category | Meaning |
|---|---|
| `api_error` | Rate limiting, service error, `429`, too many requests |
| `timeout` | Timed out, deadline exceeded |
| `connection_failure` | Network, DNS, socket failure |
| `transient` | A transient condition not matching a more specific category |

### 1.3 Classification rules

- Classification is by observed evidence, not by assumption.
- **An unclassifiable failure is retried within budget and reported with
  category `unknown`.** A bounded retry of an unknown transient condition is
  safe because retries are budgeted, isolated to the task's own worktree, and
  cannot reach shared state — every result still faces full validation and every
  gate before integration.
- What is *not* permitted is treating an unknown failure as success, or as
  grounds to skip validation. Uncertainty in *failure* classification costs a
  bounded retry; uncertainty at an *integration* gate stops the protocol
  ([`review-and-gates.md` §1](review-and-gates.md#1-the-gate-principle)).
- The category MUST be reported. An unreported category is an incomplete report.

## 2. Retry policy

| Setting | Default |
|---|---|
| Maximum retries per task | **3** |
| Backoff base | **5 seconds** |
| Backoff ceiling | **120 seconds** |

Backoff is exponential with jitter:

```text
delay = min(base × 2^attempt + jitter, ceiling)
jitter = a small random fraction (up to 10%) of the exponential term
```

Jitter exists so that several tasks failing on the same external condition do
not retry in lockstep and reproduce the condition.

### 2.1 Retry rules

- A retry re-runs the task **in its own isolation**, under the **same** worker
  mechanism.
- The retry budget is **per task**. One task exhausting its budget does not
  consume another's.
- `retry_count` and the budget survive interruption and resume (§6). A resumed
  batch does not silently restore a task's retries to zero — that would allow
  unbounded retries across restarts.
- Budget exhausted → the task is `FAILED`, with the last failure category and
  the attempt count recorded.
- A retried task's result faces the full validation and gate sequence. A retry
  never inherits a previous attempt's validation or approval.

## 3. Worker delivery state

Beyond the task state model in
[`orchestration.md` §3.2](orchestration.md#32-task-state), a worker carries a
coarse **delivery state**, used for recovery. It is a vocabulary of its own
([`orchestration.md` §3](orchestration.md#3-state-models-and-outcomes)) and it
describes only *what the worker delivered* — never how the task was classified:

| Delivery state | Meaning |
|---|---|
| `PENDING` | Not yet started (waiting on a dependency or on a slot) |
| `RUNNING` | Actively executing |
| `DELIVERED` | Stopped having delivered a parseable result, whatever that result claims |
| `ORPHANED` | Stopped without a parseable result |

What happens next is decided by the classification order in
[`worker-contract.md`](worker-contract.md#classification-order), not by this
vocabulary. A worker that failed loudly is `DELIVERED`, carrying a result whose
`classification` is `FAILED`; a worker that vanished is `ORPHANED`, and whether
that is retried depends on the failure category, not on the delivery state
(§6.3). The two are never the same case, which is exactly why delivery states
carry no `SUCCESS` or `FAILED` value of their own: those belong to the task
result vocabulary and are assigned only after validation.

## 4. Dependent task handling

When a task does not reach `SUCCESS`:

| Effect on dependents | Rule |
|---|---|
| Blocked, not skipped | Every dependent takes task state `BLOCKED` and task result `BLOCKED`, with the non-`SUCCESS` dependency named |
| Not silently dropped | A `BLOCKED` dependent stays in the inventory and in the report |
| Not integrated speculatively | A dependent's work is never integrated on the assumption the dependency was optional |
| Transitively applied | Dependents of dependents are `BLOCKED` too |

**Failure isolation cuts both ways.** Tasks that do *not* depend on the failed
task continue normally — one failure never blocks unrelated work. Tasks that
*do* depend on it stop — one failure never gets bypassed by declaring the
dependency unimportant.

Both halves are required. Isolation without the second half would integrate work
whose premise never landed.

## 5. Integration failure

If integration of one task fails or is stopped at a gate:

1. That task's result is recorded with the exact gate and condition.
2. **The remaining queue is not drained automatically.** Processing stops at the
   blocked item rather than skipping past it.
3. That task's integration is **held**; it is not marked `FAILED` for having
   been stopped at a gate. Processing stops at the held item rather than
   skipping past it. Which state the task takes is determined by *why* the gate
   closed: a condition that can still change — an approval invalidated by a
   moved base, for example — returns the task to `RESULT_READY` for a fresh
   determination, while a condition requiring an operator decision makes the
   task `BLOCKED`, which is terminal
   ([`orchestration.md` §3.2](orchestration.md#32-task-state)).
4. Before any further integration proceeds, the situation is re-evaluated:
   - Do any remaining tasks depend on the one that failed? Those are `BLOCKED`.
   - Has the base changed such that remaining approvals are now invalid
     ([`review-and-gates.md` §6.3](review-and-gates.md#63-invalidation))?
   - Has the base changed such that remaining semantic conflict determinations
     must be redone
     ([`worker-contract.md` §5](worker-contract.md#5-semantic-conflict-detection))?
5. Only tasks that are still independently eligible after that re-evaluation may
   continue integrating.

**A worker failure never authorizes unconditional integration of the other
workers' results.** The other results were validated against a plan that assumed
the failed task would land. That assumption must be re-checked before their
integration continues.

Nothing already merged is reverted automatically. Reverting is an operator
decision.

## 6. Recovery and resume

A batch may be interrupted. Recovery restores a **known** state or stops.

### 6.1 Progress record

Progress may be recorded so an interrupted batch can resume. Any durable form is
acceptable — a notes file, an issue comment, a scratch document. **No particular
storage format, schema, or file is required**, and none is required to exist for
the protocol to be valid.

What matters is that a resumed batch can re-establish, per task: its
classification, its isolation artifacts, its base revision, its retry count, and
how far through the gates it got.

If no progress record exists, the batch does not guess. It re-derives state from
observable reality (§6.2) or starts over from planning.

### 6.2 Reconciliation against reality

On resume, recorded state is reconciled against what is actually true:

1. Do the recorded branches and worktrees still exist?
2. Do the recorded commits exist on those branches?
3. Do the recorded changes actually exist in the base already (i.e. did an
   integration complete after the record was last written)?
4. Are the recorded approvals still valid against current heads?

**Observable reality wins over the record.** A record claiming a merge that the
base does not contain is stale, not authoritative.

Reconciliation corrects **the record**, never a task's actual state, and so it
never moves a task out of a terminal state — which would be impossible, since no
edge leaves one
([`orchestration.md` §3.2](orchestration.md#32-task-state)). A record showing
task state `COMPLETED` with task result `SUCCESS` for a merge the base does not
contain is evidence that the task never reached `COMPLETED`: the record was
written in anticipation, or written and then invalidated, and the task's real
state is whatever reality supports — typically `RESULT_READY` with its branch
intact, or, if that cannot be established, task state `BLOCKED` and task result
`BLOCKED` under §6.5. The discrepancy is reported either way.

### 6.3 Orphan handling

A worker is `ORPHANED` when it is no longer running and delivered no usable
result — no output at all, or output that cannot be parsed into the required
result form
([`worker-contract.md` §3.2](worker-contract.md#32-worker-output-worker--orchestrator)).

`ORPHANED` detection is the **first** classification step, and it is exclusive of
result validation: there is no parseable result to validate. A worker that did
deliver a parseable result is never `ORPHANED` — a defect in that result is a
validation failure and is terminal, not a retryable orphan. The single
classification order is defined in
[`worker-contract.md` — Classification order](worker-contract.md#classification-order).

Delivery state and failure category are orthogonal: `ORPHANED` says the worker
delivered nothing, the category (§1) says why. **Retry follows the category, not
the delivery state.** An orphan is retried only when its cause classifies as
retryable; an orphan whose cause is non-retryable — in practice `launch_failure`,
where the worker could never be established — is terminal without a retry,
because re-running it repeats a deterministic failure (§1.1).

```text
ORPHANED detected
      ↓
cause classifies as retryable?  (§1.1 / §1.2; unknown counts as retryable, §1.3)
      ├─ no  → task state WORKER_FAILED, task result FAILED
      │         (non-retryable category recorded; no retry)
      └─ yes → retry budget remaining?
                 ├─ yes → increment retry_count → re-provision → re-dispatch
                 └─ no  → task state WORKER_FAILED, task result FAILED
                           (and NOT counted as completed)
```

Re-provisioning uses an **attempt-scoped** worktree and branch
([`git-worktree.md` §3.1](git-worktree.md#31-retry-attempt-naming)), so a retry
never reclaims the previous attempt's artifacts, and a preserved orphan artifact
never by itself blocks the retry.

An orphaned worker's partial output is **never** integrated. Partial output is
by definition unvalidated.

### 6.4 Idempotency on resume

Before re-dispatching any task, verify it was not already completed:

| Check | If already present |
|---|---|
| Does the change already exist in the base? | Do not re-dispatch; reconcile the task's state |
| Does an implementation PR already exist? | Do not re-dispatch; resume from that PR's gate position |
| Does the branch already exist with the expected commits? | Do not re-provision; resume from the existing branch |

This prevents the most damaging resume failure: duplicate dispatch producing two
competing implementations of the same task.

### 6.5 Corrupt or unreadable state — fail closed

| Situation | Required behaviour |
|---|---|
| No progress record | Treat as a new run |
| Progress record exists but is unreadable or inconsistent | **Stop.** Report it. Do not re-dispatch, do not integrate |
| Recorded state contradicts observable reality | Trust reality; re-derive; report the discrepancy |

A record that exists but cannot be read is **not** treated as absent. Treating
it as absent would re-dispatch tasks that may already be in flight or already
merged — exactly the duplicate-work and duplicate-merge hazard the record exists
to prevent.

## 7. Reporting non-`SUCCESS` outcomes

`NO_OP` and the two failure classifications have **different** report shapes.
`NO_OP` is not a failure: it has no failure category and no failing condition,
and requiring those of it would force a fabricated failure onto a task that did
not fail.

### 7.1 `NO_OP`

| Field | Content |
|---|---|
| Task | Which task |
| `task_result` | `NO_OP` |
| Evidence | Why the functionality was already present — substantiated per [`worker-contract.md` §4.2](worker-contract.md#42-substantive-validation), never merely an absent diff |
| Base revision | The base the task was evaluated against |
| Verification | The verification actually run, with real `PASS` / `FAIL` / `NOT RUN` outcomes |
| Preserved artifacts | Worktree and branch retained pending the operator decision, or `—` for a preflight `NO_OP`, which never ran and was never provisioned ([`git-worktree.md` §6.2](git-worktree.md#62-what-is-deliberately-preserved)) |
| Operator decision | The decision still required on the underlying Issue |

A `NO_OP` report carries no `Category` and no `Condition` field. Neither exists
for it.

### 7.2 `BLOCKED` and `FAILED`

| Field | Content | Required for |
|---|---|---|
| Task | Which task | both |
| `task_result` | `BLOCKED` / `FAILED` | both |
| Category | The failure category (§1), including `unknown` | `FAILED`, and any `BLOCKED` that followed an observed failure |
| Condition | The exact condition, quoted | both |
| Attempts | Retries used, out of budget | any task that was dispatched |
| Preserved artifacts | Worktree and branch retained for diagnosis, or `—` where none was ever provisioned ([`git-worktree.md` §6.2](git-worktree.md#62-what-is-deliberately-preserved)) | both |
| Dependents affected | Tasks `BLOCKED` as a consequence | both |

**A `BLOCKED` task does not always have a failure category.** Categories classify
*failures* (§1), and much of what produces task result `BLOCKED` is not a failure
at all: a preflight condition that was not satisfied
([`worker-contract.md` §1.2](worker-contract.md#12-preflight-rules)), a
prerequisite that did not reach `SUCCESS` (§4), an undeterminable semantic
conflict
([`worker-contract.md` §5.3](worker-contract.md#53-outcomes)), a gate that closed
on a condition needing an operator decision
([`review-and-gates.md` §10](review-and-gates.md#10-gate-reporting)), or a
batch-level stop ([`orchestration.md` §3.6](orchestration.md#36-the-terminating-path)).
None of these attempted work and none of them failed, so none has a category, and
inventing one would fabricate a failure exactly as §7.1 forbids for `NO_OP`.

`Condition` is required in every case and carries the meaning: it quotes what was
not satisfied. A `BLOCKED` task reports `Category` only when a real failure
preceded the block — an orphaned worker whose cause was non-retryable, for
example. `FAILED` always reports one.

### 7.3 Batch success rule

**A batch passed only if every task in the inventory is task result `SUCCESS`.**

| The batch contains | Passed? |
|---|---|
| `SUCCESS` only | Passed — provided aggregate verification passed too |
| Any `NO_OP` | **Not passed** — the work was already present, which is an operator decision, not a batch success |
| Any `BLOCKED` | **Not passed** |
| Any `FAILED` | **Not passed** |

"Passed" here means **batch outcome `SUCCESS`**, which is a separate vocabulary
from the task results above and from the batch's lifecycle state. Reaching
lifecycle state `COMPLETED` is not passing: a blocked or failed batch reaches
`COMPLETED` too, carrying outcome `BLOCKED` or `FAILED`
([`orchestration.md` §3.4](orchestration.md#34-batch-outcome)). Which outcome a
given composition produces is decided by the ordered aggregation rule in
[`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome),
of which this table is the `SUCCESS` half.

`NO_OP` is never promoted to `SUCCESS`, and never counted toward a passing batch
in order to make one appear to pass
([`../SKILL.md`](../SKILL.md#task-result-vocabulary)). A batch with any
non-`SUCCESS` task is reported with its exact composition per task result.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`orchestration.md`](orchestration.md) — state model and reporting
- [`worker-contract.md`](worker-contract.md) — result validation, semantic conflict
- [`review-and-gates.md`](review-and-gates.md) — gates and merge delegation
- [`git-worktree.md`](git-worktree.md) — preserved artifacts and cleanup
- [`examples.md`](examples.md) — worked failure scenarios
