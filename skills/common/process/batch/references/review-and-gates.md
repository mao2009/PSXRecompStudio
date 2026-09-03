# Review, Approval and Integration Gates

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Defines the review gate, the approval gate, delegation of merge execution to the
Merge Skill, merge ordering, and Issue lifecycle safety.

> **Ownership.** The Merge Skill
> ([`../../merge/SKILL.md`](../../merge/SKILL.md)) owns approval validation,
> main-HEAD refresh, mandatory rebase, conflict handling, merge execution and
> post-merge verification. This document states **when** the Batch protocol may
> invoke those, and what it must establish first. It does not redefine them. If
> this document and the Merge Skill ever disagree, **the Merge Skill governs**.

## 1. The gate principle

A **gate** is a condition that must hold before the protocol may advance.

**A gate whose status is unknown is a closed gate.**

There is no "probably approved", no "review presumably not needed", no
"conflict probably absent". Each gate has exactly three outcomes:

| Outcome | Effect |
|---|---|
| Open — condition established as satisfied | Advance |
| Closed — condition established as unsatisfied | Stop, report the condition |
| **Unknown** — condition could not be established | **Stop, report what could not be established** |

Stopping at a gate is a correct, reportable outcome. It is not a failure to be
worked around.

## 2. Gate order

Gates are evaluated in this order, per task. A later gate is never evaluated
before an earlier one has opened.

```text
Validated result
      ↓
1. Integration-eligibility gate   (result valid, dependencies integrated)
      ↓
2. Semantic conflict gate         (no conflict against the current base)
      ↓
3. Review gate                    (review performed and satisfied)
      ↓
4. Approval gate                  (explicit, current, SHA-bound approval)
      ↓
5. Merge execution                → delegated to the Merge Skill
      ↓
6. Post-merge verification        → owned by the Merge Skill
      ↓
7. Cleanup                        (confirmed merges only)
```

## 3. Integration-eligibility gate

Opens only when all hold:

1. The result passed validation
   ([`worker-contract.md` §4](worker-contract.md#4-worker-result-validation)).
2. The result's classification is `SUCCESS`. `NO_OP`, `BLOCKED` and `FAILED`
   are never integration-eligible.
3. Every task this task depends on has been integrated.
4. No dependency of this task ended in a non-`SUCCESS` state.

If a dependency did not reach `SUCCESS`, this task becomes `BLOCKED` — it is not
integrated "anyway" on the assumption that the dependency was optional.

## 4. Semantic conflict gate

Opens only when semantic conflict detection
([`worker-contract.md` §5](worker-contract.md#5-semantic-conflict-detection))
finds no conflict **against the base as it currently stands**.

Because integration is serial, the base moves after every merge. This gate is
therefore re-evaluated before each integration — a result that was conflict-free
an hour ago may not be conflict-free against the base now.

An undeterminable conflict status closes the gate.

## 5. Review gate

Opens only when the change has been reviewed against the review criteria and the
review is satisfied.

- Review semantics, viewpoints and checklist are owned by
  [`../../self-review/SKILL.md`](../../self-review/SKILL.md). This protocol does
  not restate them.
- The review is performed **per task**, on that task's isolated change, before
  integration.
- Review is a distinct pass from authoring. The context that produced a change
  does not self-approve it as reviewer. Batch execution makes this natural: the
  worker authors, the orchestrator (or a separate reviewer) reviews.
- If it cannot be established whether review is required, or whether it was
  performed, the gate is **closed**.

Automated reviewers are best-effort inputs. Their absence, unavailability, rate
limiting, or pending status does not open the gate and does not close it on its
own — repository-owned checks and human approval remain the actual conditions.

## 6. Approval gate

Opens only on an **explicit, current, SHA-bound** approval.

### 6.1 Required properties

| Property | Requirement |
|---|---|
| Explicit | A recorded approval exists. Absence of objection is not approval |
| Attributed | The approver's authenticated identity is recorded |
| Timestamped | A valid, well-formed approval time is recorded |
| Bound to the change | The approved commit SHA equals the current head of the change |
| Bound to the base | The approved base/main head SHA equals the current base head |
| Valid | The approval has not been invalidated |

**Every one of these must hold.** A single mismatch closes the gate.

### 6.2 Independence

- Approval is tracked **per task**. Approving one task's change never approves
  another's.
- Approving a batch as a whole is not a substitute for per-task approval.

### 6.3 Invalidation

An approval is invalidated by any of:

- a rebase that changed the content,
- a changed commit,
- changed content in the change under review,
- a force push,
- a moved head on either the change or the base.

An invalidated approval returns the task to `RESULT_READY` for **fresh**
approval. It is never repaired, reused, or carried forward.

This is why serial integration matters: each merge moves the base, which
invalidates approvals bound to the previous base. Re-approval against the new
base is the protocol working correctly, not friction to be optimized away.

### 6.4 Unknown approval status

If it cannot be established that a valid, current, SHA-bound approval exists,
the gate is **closed** and **no merge occurs**. This is absolute: it is the last
protection between an unvalidated change and the shared branch.

## 7. Merge execution — delegated

Once the approval gate opens, merge execution is handed to the Merge Skill in
full.

**This protocol MUST NOT perform, reimplement, or approximate any of:**

- approval validation,
- base/main HEAD refresh,
- the mandatory rebase onto the latest base,
- conflict detection and conflict handling during merge,
- pre-merge validation,
- the merge itself,
- post-merge verification.

The Batch protocol's obligations around the delegation are only:

1. Establish gates 1–4 before invoking.
2. Invoke the Merge Skill for exactly one task at a time.
3. Accept its outcome as authoritative.
4. Record the outcome and continue integration ordering.

**Batch execution is never itself a merge condition.** A change does not become
mergeable because it is part of a batch. Every safety condition that applies to
a standalone merge applies identically here.

### 7.1 Conflicts during merge

Conflicts are handled by the Merge Skill, which returns them to the originating
task. The orchestrator MUST NOT resolve a merge conflict on the worker's behalf
— doing so would make the orchestrator the author of an unreviewed change.

A task returned with a conflict is re-worked in its own isolated worktree and
re-enters the gates from the start, including fresh review and fresh approval.

## 8. Merge ordering

- Merges are **serial**: exactly one at a time. Never parallel.
- Order follows dependency order; within a wave, validation order.
- Each merge is rebased onto the **current** base, not the base the work started
  from. The Merge Skill enforces this.
- After each merge, the base has moved: remaining approvals bound to the old
  base are invalidated (§6.3), and remaining semantic conflict determinations
  are re-evaluated (§4).

Merge failure mid-queue does **not** cause the rest of the queue to be drained
automatically. See
[`failure-recovery.md` §5](failure-recovery.md#5-integration-failure).

## 9. Issue lifecycle safety

| Rule | Requirement |
|---|---|
| Workers do not close Issues | A worker has no authority to close the Issue it implements |
| The orchestrator does not close Issues | A completion report is not grounds for closure |
| Closure follows the merge | An Issue closes only as a consequence of its approved change being merged |
| `NO_OP` never closes an Issue | Already-implemented requires an explicit operator decision |
| `BLOCKED` and `FAILED` never close an Issue | Obviously |

Rationale: a worker's belief that it finished is exactly the claim the gates
exist to check. Allowing that belief to close the Issue would let a batch mark
its own homework.

## 10. Gate reporting

Every gate evaluation is reported per task:

| Field | Content |
|---|---|
| Gate | Which gate |
| Outcome | `OPEN` / `CLOSED` / `UNKNOWN` |
| Evidence | What established the outcome |
| Effect | What the protocol did next |

A gate reported as `UNKNOWN` MUST name the condition that could not be
established. "Unknown" without a named condition is an incomplete report.

A gate outcome is not a batch outcome, and it is not automatically a terminal
task state either. What a gate stop does to the affected *task* depends on why
the gate closed
([`orchestration.md` §3.2](orchestration.md#32-task-state),
[`failure-recovery.md` §5](failure-recovery.md#5-integration-failure)):

| Why the gate closed | Effect on the task |
|---|---|
| A condition that can still change — an approval invalidated by a moved base, for example | Returns to `RESULT_READY` for a fresh determination. **Not** terminal |
| A condition requiring an operator decision | Terminal task result `BLOCKED` |

Only once a task is terminal does its result reach the batch, and even then the
batch outcome is decided by the aggregation rule
([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome)),
never by the gate.

A gate stop halts the batch itself only in the rare case where it leaves the
batch as a whole unable to continue safely — batch-level stop condition S5
([`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions)). An
ordinary gate stop does not: unrelated tasks keep running and integrating.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`../../merge/SKILL.md`](../../merge/SKILL.md) — **owner** of approval, rebase, merge, post-merge verification
- [`../../self-review/SKILL.md`](../../self-review/SKILL.md) — **owner** of review semantics
- [`worker-contract.md`](worker-contract.md) — validation and semantic conflict detection
- [`orchestration.md`](orchestration.md) — integration ordering, phases, state and outcome models
- [`failure-recovery.md`](failure-recovery.md) — failure handling at and after the gates
