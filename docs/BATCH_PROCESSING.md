# Batch Orchestration

**Status:** Stable

**Authority:** Reference — the specification is
[`skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md)

Processes multiple Issues or tasks as one batch: each task runs in its own
isolated worktree and branch, results are validated independently, and changes
are integrated one at a time through the review and approval gates.

Batch orchestration is defined as a **Markdown-only, agent-agnostic protocol**.
There is no batch runtime, wrapper, scheduler, or configuration file: any agent
that can read Markdown and run ordinary Git operations can execute it (#242).

## Capabilities

- **Mandatory planning stage**: task inventory, dependency analysis, dependency
  graph, execution waves, and parallel/sequential classification are produced
  before any implementation begins — including when only one worker is used
- **Dependency DAG scheduling**: tasks form a DAG; each wave starts once its
  dependencies complete; a detected cycle blocks the batch
- **Worker isolation**: every task gets its own worktree and branch; one
  failure does not block unrelated tasks, and dependents of a failure are
  blocked rather than skipped
- **Fail-closed decisions**: unknown dependency, unknown overlap, or unknown
  parallel safety all resolve to sequential; an unknown gate is a closed gate
- **Result validation**: no worker result is integrated until it is validated
  and checked for semantic conflicts against the current base
- **Approval-gated serial integration**: changes are integrated one at a time,
  rebased onto the latest base, after explicit SHA-bound approval, with merge
  execution delegated to the Merge Skill
- **Retry with backoff**: retryable failures retry within a per-task budget
  (default 3) using exponential backoff with jitter; non-retryable failures do
  not retry

## Lifecycle

```text
    Batch requested
         |
         v
    Inventory + Preflight        (unexecutable tasks -> task result BLOCKED)
         |
         v
    Dependency analysis + DAG    (cycle -> batch outcome BLOCKED)
         |
         v
    Execution waves
         |
         v
      +---+   +---+   +---+
      | A |   | C |   | B |      A & C share wave 1; B waits on A
      +---+   +---+   +---+
         |      |       |
         +------+-------+
        (retry within budget)
         |
         v
    Per-result validation + semantic conflict check
         |
         v
    Serial integration: review gate -> approval gate -> Merge Skill
         |
         v
    Aggregate verification -> cleanup -> report
```

## Result classification

| Task result | Meaning |
|---|---|
| `SUCCESS` | Work was required and was completed and validated |
| `NO_OP` | Work was already present; nothing was required |
| `BLOCKED` | Preconditions were not satisfied |
| `FAILED` | Work was required and attempted but did not succeed |

`NO_OP` is never reported as `SUCCESS`, and no result closes an Issue — Issue
closure follows only from the approved merge.

The batch itself carries a **lifecycle state** (where it is: the phase it is
executing, ending at `COMPLETED`) and, separately, a **batch outcome**
(`SUCCESS` / `NO_OP` / `BLOCKED` / `FAILED`). Reaching `COMPLETED` means the
final phases ran and reporting finished — not that the batch passed, and not
that anything was verified: aggregate verification may legitimately be `NOT RUN`,
as it is for a batch that stopped before integrating anything. A batch that stops
early records its stop condition and still runs those final phases. Both models,
and the rule that derives the outcome from the task results, are defined in
[`references/orchestration.md` §3](../skills/common/process/batch/references/orchestration.md#3-state-models-and-outcomes).

## Configuration

There is no batch configuration file. Policy defaults (concurrency limit 3,
retry budget 3, backoff base 5s capped at 120s, branch and worktree naming) are
documented in the Skill's references and may be overridden by an explicit
operator decision, which is then recorded in the batch report.

## Specification

| Document | Contents |
|---|---|
| [`SKILL.md`](../skills/common/process/batch/SKILL.md) | Normative entrypoint: applicability, MUST/MUST NOT rules, lifecycle, invariants, fail-closed rules |
| [`references/orchestration.md`](../skills/common/process/batch/references/orchestration.md) | Phase contracts, state models, integration ordering, aggregate verification, reporting |
| [`references/dependency-analysis.md`](../skills/common/process/batch/references/dependency-analysis.md) | Inventory, dependency model, DAG, wave construction, parallel safety |
| [`references/worker-contract.md`](../skills/common/process/batch/references/worker-contract.md) | Preflight, worker abstraction, dispatch/output contracts, validation, semantic conflicts |
| [`references/git-worktree.md`](../skills/common/process/batch/references/git-worktree.md) | Isolation, worktree/branch strategy, concurrency, git safety, cleanup |
| [`references/review-and-gates.md`](../skills/common/process/batch/references/review-and-gates.md) | Review gate, approval gate, merge delegation, Issue lifecycle safety |
| [`references/failure-recovery.md`](../skills/common/process/batch/references/failure-recovery.md) | Failure classification, retry, recovery, resume |
| [`references/examples.md`](../skills/common/process/batch/references/examples.md) | Worked conformance scenarios |
