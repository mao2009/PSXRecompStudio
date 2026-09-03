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

## 3. Worker lifecycle states

Beyond the task state model in
[`orchestration.md` §3.2](orchestration.md#32-task-state), a worker carries a
coarse lifecycle state used for recovery:

| Lifecycle state | Meaning |
|---|---|
| `PENDING` | Not yet started (waiting on dependency or on a slot) |
| `RUNNING` | Actively executing |
| `ORPHANED` | The worker is gone and produced no usable result |
| `SUCCESS` | Completed with a validated result |
| `FAILED` | Retry budget exhausted, or a non-retryable failure |

## 4. Dependent task handling

When a task does not reach `SUCCESS`:

| Effect on dependents | Rule |
|---|---|
| Blocked, not skipped | Every dependent becomes `BLOCKED`, with the failed dependency named |
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

1. That task's outcome is recorded with the exact gate and condition.
2. **The remaining queue is not drained automatically.** Processing stops at the
   blocked item rather than skipping past it.
3. A blocked item **returns to pending**; it is not marked `FAILED`. Being
   blocked is a state to resolve, not a terminal defeat.
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

### 6.3 Orphan handling

A worker is `ORPHANED` when it is no longer running and left no usable result —
including when it left a result that is unparseable or incomplete.

```text
ORPHANED detected
      ↓
retry budget remaining?
      ├─ yes → increment retry_count → re-provision → re-dispatch
      └─ no  → FAILED  (and NOT counted as completed)
```

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

## 7. Reporting failures

Every non-`SUCCESS` outcome is reported with:

| Field | Content |
|---|---|
| Task | Which task |
| Classification | `NO_OP` / `BLOCKED` / `FAILED` |
| Category | The failure category, including `unknown` |
| Condition | The exact failing condition, quoted |
| Attempts | Retries used, out of budget |
| Preserved artifacts | Worktree and branch retained for diagnosis |
| Dependents affected | Tasks `BLOCKED` as a consequence |

A batch containing any `FAILED` or `BLOCKED` task is **not** reported as a
successful batch, regardless of how many tasks succeeded.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`orchestration.md`](orchestration.md) — state model and reporting
- [`worker-contract.md`](worker-contract.md) — result validation, semantic conflict
- [`review-and-gates.md`](review-and-gates.md) — gates and merge delegation
- [`git-worktree.md`](git-worktree.md) — preserved artifacts and cleanup
- [`examples.md`](examples.md) — worked failure scenarios
