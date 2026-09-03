---
name: batch-orchestration
description: >
  Agent-agnostic orchestration protocol for executing multiple Issues or tasks
  as one batch. Defines task inventory, dependency analysis, DAG and execution
  waves, worker abstraction and isolation, parallel-safety rules, the batch
  lifecycle state and batch outcome models, failure classification, retry and
  recovery, result validation, semantic conflict detection, review and approval
  gates, serial integration via the Merge Skill, cleanup, and final reporting.
  Markdown-only: no batch-specific runtime,
  wrapper, scheduler, state-machine implementation or configuration file is
  required to execute it.
version: 3.0.0
scope: process
platform: agent-agnostic
related-issues: "#145, #155, #159, #160, #161, #162, #167, #242"
---

# Batch Orchestration Skill

This Skill is a **process specification**, not a program. It defines *what must
happen, in what order, and under which safety conditions* when several Issues or
tasks are executed as one batch.

Any agent that can read Markdown and perform ordinary Git and repository
operations can execute this protocol. There is no batch-specific runtime to
install, no wrapper to invoke, and no configuration file to load.

## Purpose

Execute multiple Issues or tasks safely as a batch by:

1. making the work set and its dependencies **explicit before execution**,
2. executing units of work in **isolated workers**,
3. **validating** each worker's result before it is allowed to influence the
   repository, and
4. **integrating** results one at a time through the existing review, approval
   and merge gates.

## Applicability

Apply this Skill when **any** of the following holds:

| Trigger | Example |
|---|---|
| A batch is explicitly requested | "Use the Batch Skill", "バッチで処理して" |
| Two or more Issues/tasks are handed over as one unit of work | "Implement #101, #102 and #103" |
| Parallel implementation is requested | "Do these in parallel" |
| Multi-issue or multi-task execution is implied | "Work through this backlog" |

If this Skill applies, the [Mandatory Preconditions](#mandatory-preconditions)
MUST be satisfied before any implementation work begins — including when only
one worker will ultimately be used.

Do **not** apply this Skill to a single, indivisible task. A single task follows
`../../task/implementation/SKILL.md` directly.

## Definitions

| Term | Definition |
|---|---|
| **Batch** | One execution of this protocol over a defined set of tasks. |
| **Task** | One independently completable unit of work, normally one Issue. The unit of the dependency graph, of worker assignment, and of result classification. |
| **Worker** | An independent, delegated execution unit with its own context and its own isolated worktree and branch. A worker is an *abstraction*: how it is realized is left entirely to the executing agent's native capability. |
| **Wave** | A set of tasks whose dependencies are all satisfied and which have been determined parallel-safe with respect to each other. |
| **Orchestrator** | The role executing this protocol. It plans, dispatches, validates and integrates. It never implements task work itself. |
| **Integration** | Bringing one worker's validated result into the shared branch, through the review, approval and merge gates. |
| **Gate** | A condition that MUST hold before the protocol may advance. A gate whose status is unknown is a closed gate. |

## Normative rules

### MUST

1. Produce a **task inventory**, **dependency analysis**, **dependency graph**,
   **execution waves**, and a **parallel/sequential classification** before
   dispatching any worker.
2. Run **preflight validation** per task and give failures task result `BLOCKED`
   before any worker is dispatched.
3. Give every worker its **own worktree and own branch**. Workers never share a
   working tree.
4. Classify every task result as exactly one of `SUCCESS`, `NO_OP`, `BLOCKED`,
   or `FAILED`.
5. **Validate** each worker's result against the worker output contract before
   integrating it.
6. Integrate **one task at a time**, in dependency order, re-verifying against
   the updated base before each integration.
7. Delegate all merge execution to the Merge Skill
   (`../merge/SKILL.md`).
8. Run Phases 9–11 — aggregate verification, cleanup and reporting — on **every**
   path, including every early stop.
9. Derive exactly one **batch outcome** at `REPORTING`, by the ordered
   aggregation rule
   ([`references/orchestration.md` §3.5](references/orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome)),
   before the batch reaches lifecycle state `COMPLETED`.
10. Report the task result for every task and the batch outcome for the batch,
    including everything not completed.

### MUST NOT

1. **MUST NOT** treat a batch as executed when a single worker processed every
   task sequentially without an inventory, graph and waves having been produced.
   See [Silent sequential fallback is forbidden](#silent-sequential-fallback-is-forbidden).
2. **MUST NOT** implement task work in the orchestrator context. The
   orchestrator never substitutes its own implementation for a worker's.
3. **MUST NOT** merge directly, use administrative bypass, force push, or push
   directly to the default branch.
4. **MUST NOT** integrate an unvalidated, incomplete, or malformed worker result.
5. **MUST NOT** integrate any result while a gate's status is unknown.
6. **MUST NOT** close an Issue because a worker reported completion. Issue
   closure is a consequence of the approved merge only.
7. **MUST NOT** treat `NO_OP` as `SUCCESS`, or as evidence that the batch passed.
8. **MUST NOT** retry a task under a different worker mechanism or provider. A
   mechanism switch is not a retry.
9. **MUST NOT** skip aggregate verification, cleanup or reporting because the
   batch stopped early. A blocked or failed batch finishes its lifecycle
   ([`references/orchestration.md` §3.6](references/orchestration.md#36-the-terminating-path)).
10. **MUST NOT** report a lifecycle state in place of an outcome. Reaching
    `COMPLETED` is not passing.

### SHOULD

1. Keep concurrency at or below **3** simultaneous workers unless the operator
   raises it deliberately.
2. Retry a transient failure at most **3** times with exponential backoff.
3. Prefer a smaller wave over an uncertain one.

### MAY

1. Run waves with a single member when the graph or the available capability
   allows nothing wider.
2. Persist batch progress notes so an interrupted batch can be resumed.

## Lifecycle

The batch advances through these phases in order. A phase MUST NOT begin before
its predecessor's postcondition holds.

```text
1. DISCOVERY    → collect candidate tasks
2. INVENTORY    → record each task, its scope, and its expected change surface
3. PREFLIGHT    → per-task executability check; failures take task result BLOCKED
4. ANALYSIS     → dependency + overlap analysis, DAG, cycle check
5. PLANNING     → execution waves, parallel/sequential classification
6. EXECUTION    → dispatch workers wave by wave, in isolation
7. VALIDATION   → per-result contract + semantic conflict check
8. INTEGRATION  → review gate → approval gate → Merge Skill, one at a time
9. VERIFICATION → aggregate verification against the integrated base
10. CLEANUP     → remove worktrees and branches for integrated work
11. REPORTING   → per-task result + batch outcome
```

Phases 1–5 are the **planning stage** and are mandatory in full. Phase 6 onwards
is the **execution stage**.

Each phase is also the batch's **lifecycle state** while it runs, with one
terminal state `COMPLETED` after Phase 11
([`references/orchestration.md` §3.1](references/orchestration.md#31-batch-lifecycle-state)).
A batch that stops early does not leave the lifecycle: it records its stop
condition and enters Phase 9, so Phases 9–11 run on every path — and its outcome,
like every outcome, is derived at `REPORTING`
([`references/orchestration.md` §3.6](references/orchestration.md#36-the-terminating-path)).

Detailed phase preconditions and postconditions:
[`references/orchestration.md`](references/orchestration.md).

## Mandatory preconditions

Before any worker is dispatched, all five artifacts below MUST exist and MUST be
recorded in the batch report:

| # | Artifact | Content |
|---|---|---|
| 1 | Task inventory | Every task, its identifier, its goal, its expected change surface |
| 2 | Dependency analysis | For each ordered pair, whether a dependency exists, or that it is unknown |
| 3 | Dependency graph | The DAG, with a completed cycle check |
| 4 | Execution waves | The ordered wave assignment of every non-`BLOCKED` task |
| 5 | Parallel/sequential classification | For every task in a wave, whether it is parallel-safe with its wave peers |

If any of these cannot be produced, the batch records **stop condition S4**
([`references/orchestration.md` §3.4](references/orchestration.md#batch-level-stop-conditions)).
It does not proceed as an ordinary sequential implementation, and it does not
stop silently: it finishes its lifecycle through Phases 9–11
([`references/orchestration.md` §3.6](references/orchestration.md#36-the-terminating-path)),
and the outcome — `BLOCKED`, by aggregation rule 2 — is derived at `REPORTING`
like every other outcome, never assigned here.

## Silent sequential fallback is forbidden

Whether workers run **concurrently** is a capability question. Whether the batch
**semantics** apply is not.

An agent that lacks a native parallel worker capability MUST NOT collapse the
batch into ordinary one-by-one implementation. It MUST still:

- produce all five mandatory planning artifacts,
- give each task its own worktree and branch,
- execute tasks in explicit, dependency-aware wave order,
- validate each result independently before integration,
- integrate one at a time through the gates, and
- report per-task results.

It MUST additionally state in the report that execution was serialized, and why.
Serialized execution of a correctly planned batch is a valid outcome. Skipping
the planning and calling the result a batch is not.

## Invariants

These hold at every point in the batch:

1. Every task is in exactly one state, and every terminal task carries exactly
   one task result: `SUCCESS` / `NO_OP` / `BLOCKED` / `FAILED`.
2. The batch is in exactly one lifecycle state, and it enters `COMPLETED` only
   with exactly one batch outcome recorded.
3. No two active workers share a worktree or a branch.
4. Nothing is integrated that has not passed validation, review and approval.
5. The default branch is only ever changed by the Merge Skill.
6. One task's failure never causes another task's unvalidated result to be
   integrated, and never silently removes that task's dependents from the batch.
7. Dependents of a non-`SUCCESS` task are `BLOCKED`, not skipped.

## Fail-closed rules

Uncertainty resolves to the safe side. "Probably fine" is not a permitted state.

| Unknown condition | Required resolution |
|---|---|
| Dependency between two tasks unknown | Treat as **dependent**; order them |
| Change-surface overlap unknown | Treat as **overlapping** → sequential |
| Parallel safety unknown | **Sequential** |
| Preflight condition unverifiable | Task is **BLOCKED** |
| Worker output incomplete or malformed | **Do not integrate** |
| Semantic conflict status unknown | **Stop integration** |
| Review requirement unknown | **Stop integration** |
| Approval requirement or validity unknown | **Do not merge** |
| Persisted progress record unreadable or corrupt | **Stop**; do not re-dispatch |
| Whether a task was already completed is unknown | Task is **BLOCKED**, not `NO_OP` |

Every fail-closed stop MUST be reported with the exact condition that could not
be established. A stop is a reportable outcome, not a failure to be worked around.

Most fail-closed stops are **task**-scoped: the task takes task result `BLOCKED`
and unrelated tasks continue. A stop that leaves the **batch as a whole** unable
to continue safely is a batch-level stop condition: it halts dispatch, and the
batch still finishes Phases 9–11
([`references/orchestration.md` §3.6](references/orchestration.md#36-the-terminating-path)).
One blocked task never halts a batch.

## Result vocabulary

Task results and batch outcomes are **two separate vocabularies**. They share
their spellings and answer different questions: a task result describes one
task, a batch outcome describes the batch. Neither is a state — where the batch
*is* is its lifecycle state, and reaching lifecycle state `COMPLETED` says
nothing about whether it succeeded
([`references/orchestration.md` §3](references/orchestration.md#3-state-models-and-outcomes)).

### Task result vocabulary

| Task result | Meaning | Integration | Issue closure |
|---|---|---|---|
| `SUCCESS` | Work was required and was completed and validated | Eligible | Via approved merge only |
| `NO_OP` | Work was already present; nothing was required | Not eligible | Never |
| `BLOCKED` | Preconditions were not satisfied; work did not start or could not continue | Not eligible | Never |
| `FAILED` | Work was required and was attempted but did not succeed | Not eligible | Never |

`NO_OP` MUST be reported as `NO_OP`. It never counts toward a passing batch, and
it always requires an explicit operator decision about the underlying Issue.

### Batch outcome vocabulary

| Batch outcome | Meaning |
|---|---|
| `SUCCESS` | Every task in the inventory is task result `SUCCESS`, and aggregate verification passed |
| `NO_OP` | The batch was well-formed, required no change, and integrated nothing |
| `BLOCKED` | The batch stopped, or ended with outstanding work, on a condition that requires an operator decision |
| `FAILED` | Work was required and attempted and did not succeed — at task level, or on the integrated whole — or the batch's own execution violated the lifecycle model ([`references/orchestration.md` §3.1](references/orchestration.md#31-batch-lifecycle-state)) |

The batch outcome is **derived, not chosen**: it follows the ordered aggregation
rule in
[`references/orchestration.md` §3.5](references/orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome).
A single task result `BLOCKED` therefore does not by itself make the batch
outcome `BLOCKED`, but it does prevent batch outcome `SUCCESS`. The five
batch-level stop conditions — the only ones that halt a batch — are enumerated in
[`references/orchestration.md` §3.4](references/orchestration.md#batch-level-stop-conditions).

Full state models, per-state transitions and worker delivery states:
[`references/orchestration.md`](references/orchestration.md) and
[`references/failure-recovery.md`](references/failure-recovery.md).

## Responsibility boundaries

This Skill owns orchestration only. It MUST NOT reimplement what another Skill
owns:

| Concern | Owner |
|---|---|
| Dependency analysis, waves, worker assignment, integration order | **This Skill** |
| Approval validation, main-HEAD refresh, mandatory rebase, conflict handling, merge execution, post-merge verification | [`../merge/SKILL.md`](../merge/SKILL.md) |
| Pre-PR review semantics and the review checklist | [`../self-review/SKILL.md`](../self-review/SKILL.md) |
| How an individual task is implemented | [`../../task/implementation/SKILL.md`](../../task/implementation/SKILL.md) |
| Documentation synchronization | [`../doc-sync/SKILL.md`](../doc-sync/SKILL.md) |
| Commit message format | [`../commit-message/SKILL.md`](../commit-message/SKILL.md) |
| Final report format | [`../reporting/SKILL.md`](../reporting/SKILL.md) |

When this Skill states a merge or approval condition, that statement is a
**reference** to the Merge Skill's rule, never a second definition of it. If the
two ever disagree, the Merge Skill governs merge and approval.

## References

| Reference | Contents |
|---|---|
| [`references/orchestration.md`](references/orchestration.md) | Phase-by-phase contract, batch lifecycle state, task state, task result and batch outcome models, outcome aggregation, the terminating path, integration ordering, aggregate verification, cleanup, reporting |
| [`references/dependency-analysis.md`](references/dependency-analysis.md) | Task discovery, inventory, classification, dependency model, DAG and cycle detection, wave construction, parallel-safety determination |
| [`references/worker-contract.md`](references/worker-contract.md) | Worker abstraction, preflight validation, dispatch input, required output fields, result validation, semantic conflict detection |
| [`references/git-worktree.md`](references/git-worktree.md) | Isolation model, worktree and branch strategy and naming, concurrency policy, git safety prohibitions, cleanup |
| [`references/review-and-gates.md`](references/review-and-gates.md) | Review gate, approval gate, delegation to the Merge Skill, integration and merge ordering, Issue lifecycle safety |
| [`references/failure-recovery.md`](references/failure-recovery.md) | Failure classification, retry policy and budget, recovery and orphan handling, resume, corrupt-state handling |
| [`references/examples.md`](references/examples.md) | Worked scenarios, including the six normative decision cases |

## Non-goals

- Replacing human judgement on merge timing.
- Merging without explicit approval.
- Implementing task work in the orchestrator.
- Parallel merges into the default branch.
- Administrative bypass or protection-rule circumvention.
- Providing or requiring a batch-specific executable runtime.
