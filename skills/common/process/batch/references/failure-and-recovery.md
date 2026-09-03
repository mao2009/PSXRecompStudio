# Failure, Retry and Recovery

**Status:** Stable

**Authority:** Reference — explanatory detail for
[`../SKILL.md`](../SKILL.md), which is the SSOT. Nothing here overrides a rule
stated there.

Covers failure classification, retry policy and budget, workers that deliver
nothing, dependent handling, integration failure, and resume.

## Failure classification

Every failure is classified before any decision is made about it. The category
determines whether a retry is permitted, and it MUST be reported.

**Non-retryable** — retrying repeats a deterministic failure and hides the real
cause. The task is `FAILED`, with the category recorded.

| Category | Meaning |
|---|---|
| `code_error` | Compilation, syntax, type or lint failure |
| `test_failure` | Verification ran and failed |
| `architecture_violation` | The change conflicts with the architecture/SSOT |
| `dependency_conflict` | Missing or incompatible dependency, unresolvable import |
| `mechanism_switch` | A different worker mechanism was proposed as a remedy |
| `launch_failure` | The worker could not be established at all |

**Retryable** — an external or transient condition that a further attempt may
not encounter.

| Category | Meaning |
|---|---|
| `api_error` | Rate limiting, service error, `429`, too many requests |
| `timeout` | Timed out, deadline exceeded |
| `connection_failure` | Network, DNS or socket failure |
| `transient` | A transient condition not matching a more specific category |

`mechanism_switch` is listed as non-retryable to enforce a rule that is easy to
violate accidentally: **switching mechanism is not a retry**. A task that failed
under one worker mechanism is not re-attempted under another to make it pass.

Rules:

- Classification is by observed evidence, not by assumption.
- **An unclassifiable failure is retried within budget and reported with
  category `unknown`.** A bounded retry of an unknown transient condition is
  safe: retries are budgeted, isolated to the task's own worktree, cannot reach
  shared state, and every result still faces full validation and every gate
  before integration.
- What is never permitted is treating an unknown failure as success, or as
  grounds to skip validation. Uncertainty in *failure* classification costs a
  bounded retry; uncertainty at an *integration* gate stops that integration
  ([`../SKILL.md`](../SKILL.md#gates-and-integration)).
- An unreported category is an incomplete report.

## Retry policy

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
not retry in lockstep and reproduce that condition.

Rules:

- A retry re-runs the task **in its own new isolation**, under the **same**
  worker mechanism. It never reclaims the previous attempt's worktree or branch:
  those may be preserved for diagnosis, and reclaiming them would destroy
  exactly the evidence that was kept
  ([`worker-and-isolation.md`](worker-and-isolation.md#naming)).
- Collision detection applies unchanged to the attempt-scoped name. A collision
  on that name is still `BLOCKED`. A preserved artifact from an **earlier
  attempt of the same task** is not a collision and never by itself blocks the
  retry.
- The retry budget is **per task**. One task exhausting its budget does not
  consume another's.
- `retry_count` and the budget survive interruption and resume. A resumed batch
  does not silently restore a task's retries to zero — that would allow
  unbounded retries across restarts.
- Budget exhausted → task result `FAILED`, with the last failure category and
  the attempt count recorded.
- A retried task's result faces the full validation and gate sequence. A retry
  never inherits a previous attempt's validation or approval.
- Every attempt's branch, worktree and base revision are recorded and reported.

## A worker that delivers nothing

A worker that stops without delivering anything readable as a result has
produced nothing to validate. This is distinct from a worker that failed
loudly: a delivered, readable report claiming `FAILED` is a *validated* failure
([`worker-and-isolation.md`](worker-and-isolation.md#validating-a-worker-result)),
not a non-delivery, and it is never re-dispatched as one.

```text
worker delivered nothing usable
      ↓
is the cause retryable?  (unknown counts as retryable)
      ├─ no  → task result FAILED, non-retryable category recorded, no retry
      └─ yes → retry budget remaining?
                 ├─ yes → increment the attempt count, re-provision, re-dispatch
                 └─ no  → task result FAILED, attempt count recorded
```

The commonest non-retryable cause is `launch_failure`, where the worker could
never be established at all.

**An orphaned worker's partial output is never integrated.** Partial output is
by definition unvalidated.

## Dependent tasks

When a task ends with a task result other than `SUCCESS`:

| Effect on dependents | Rule |
|---|---|
| Blocked, not skipped | Every dependent that does not already hold a task result becomes `BLOCKED`, naming the non-`SUCCESS` prerequisite. A dependent that already holds one keeps it, and records the prerequisite as context |
| Not silently dropped | A `BLOCKED` dependent stays in the inventory and in the report |
| Not integrated speculatively | A dependent's work is never integrated on the assumption the prerequisite was optional |
| Transitively applied | Dependents of dependents are `BLOCKED` too |

**A prerequisite blocks its dependents only once it holds a task result.** A
task still being worked on — awaiting validation, or returned for a fresh
approval after a peer's merge moved the base — has no task result yet, and
blocking its dependents on a transient condition would strand work whose premise
is still on its way.

**Failure isolation cuts both ways.** Tasks that do *not* depend on the failed
task continue normally — one failure never blocks unrelated work. Tasks that
*do* depend on it stop — one failure never gets bypassed by declaring the
dependency unimportant. Both halves are required: isolation without the second
would integrate work whose premise never landed.

## Integration failure

If integration of one task fails or stops at a gate:

1. That task's result is recorded with the exact gate and condition.
2. **The remaining queue is not drained automatically.** Processing stops at the
   blocked item rather than skipping past it.
3. That task is not `FAILED` merely for having been stopped at a gate. Where the
   gate closed on a condition that can still change — an approval invalidated by
   a moved base, for example — the task returns for a fresh determination and is
   not yet terminal. Where it closed on a condition needing an operator
   decision, the task is `BLOCKED`.
4. Before any further integration, the situation is re-established:
   - Do any remaining tasks depend on the one that stopped? Those are `BLOCKED`.
   - Has the base moved such that remaining approvals are now invalid?
   - Has the base moved such that remaining semantic conflict determinations
     must be redone?
5. Only tasks still independently eligible after that re-evaluation may continue
   integrating.

**A worker failure never authorizes unconditional integration of the other
workers' results.** The other results were validated against a plan that assumed
the failed task would land. That assumption must be re-checked, not inherited.

Nothing already merged is reverted automatically. Reverting is an operator
decision.

## Recovery and resume

A batch may be interrupted. Recovery restores a **known** situation or stops.

### Progress record

Progress may be recorded so an interrupted batch can resume. Any durable form is
acceptable — a notes file, an Issue comment, a scratch document. **No particular
storage format, schema or file is required**, and none is required to exist for
the protocol to be valid.

What matters is that a resumed batch can re-establish, per task: its
classification, its isolation artifacts, its base revision, its attempt count,
and how far through the gates it got.

If no progress record exists, the batch does not guess. It re-establishes the
situation from observable reality, or starts over from planning.

### Reconciliation against reality

On resume, any record is reconciled against what is actually true:

1. Do the recorded branches and worktrees still exist?
2. Do the recorded commits exist on those branches?
3. Do the recorded changes already exist in the base — did an integration
   complete after the record was last written?
4. Are the recorded approvals still valid against current heads?

**Observable reality wins over the record.** A record claiming a merge the base
does not contain is stale, not authoritative: the record was written in
anticipation, or written and then invalidated, and the task's real situation is
whatever reality supports. Reconciliation corrects **the record**, never a
task's actual situation, and the discrepancy is reported either way. Where
reality cannot be established, the task is `BLOCKED`.

### Idempotency on resume

Before re-dispatching any task, confirm it was not already completed:

| Check | If already present |
|---|---|
| Does the change already exist in the base? | Do not re-dispatch; reconcile |
| Does an implementation PR already exist? | Do not re-dispatch; resume from that PR's gate position |
| Does the branch already exist with the expected commits? | Do not re-provision; resume from the existing branch |

This prevents the most damaging resume failure: duplicate dispatch producing two
competing implementations of the same task.

### Unreadable state fails closed

| Situation | Required behaviour |
|---|---|
| No progress record | Treat as a new run |
| A record exists but is unreadable or inconsistent | **Stop.** Report it. Do not re-dispatch, do not integrate |
| The record contradicts observable reality | Trust reality; re-establish; report the discrepancy |

A record that exists but cannot be read is **not** treated as absent. Treating
it as absent would re-dispatch tasks that may already be in flight or already
merged — exactly the duplicate-work and duplicate-merge hazard the record exists
to prevent.

## Reporting a non-`SUCCESS` task

`NO_OP` is not a failure. It has no failure category and no failing condition,
and requiring those of it would force a fabricated failure onto a task that did
not fail.

**`NO_OP`** reports: the task; task result `NO_OP`; the CONFIRMED evidence that
the functionality was already present; the base revision it was evaluated
against; the verification actually run with real `PASS` / `FAIL` / `NOT RUN`
outcomes; any preserved artifacts; and the operator decision still required on
the underlying Issue.

**`BLOCKED` and `FAILED`** report: the task; the task result; the exact
condition, quoted; the attempts used out of budget where the task was
dispatched; any preserved artifacts; and the dependents blocked as a
consequence. `FAILED` additionally reports its failure category.

**A `BLOCKED` task does not always have a failure category.** Categories
classify *failures*, and much of what produces `BLOCKED` is not a failure at
all: a pre-dispatch condition that was not satisfied, a prerequisite that did
not reach `SUCCESS`, an undeterminable semantic conflict, a gate that closed on
a condition needing an operator decision, or a batch-level stop. None of these
attempted work and none of them failed, so none has a category, and inventing
one would fabricate a failure. A `BLOCKED` task reports a category only when a
real failure preceded the block.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint and SSOT
- [`worker-and-isolation.md`](worker-and-isolation.md) — isolation, validation, cleanup
- [`examples.md`](examples.md) — worked failure scenarios
