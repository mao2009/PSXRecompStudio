---
name: batch-orchestration
description: >
  Agent-agnostic orchestration protocol for executing several Issues or tasks as
  one batch. Defines the evidence bar, task inventory, dependency analysis, the
  DAG and execution waves, parallel-safety rules, worker abstraction and
  isolation, result validation, semantic conflict detection, the review and
  approval gates, serial integration delegated to the Merge Skill, aggregate
  verification, cleanup, and reporting. Markdown-only: no batch-specific
  runtime, wrapper, scheduler or configuration file is required to execute it.
version: 4.0.0
scope: process
platform: agent-agnostic
related-issues: "#145, #155, #159, #160, #161, #162, #167, #242"
---

# Batch Orchestration Skill

## Status of this document

**This document is the single source of truth (SSOT) for batch orchestration in
this repository.** Behaviour is defined here, not in any `.sh`, `.ps1`, `.py`,
`.js` file or JSON configuration, and none may be introduced as a requirement of
this Skill.

The references under `references/` explain detail. They never define a rule that
contradicts or replaces one stated here. If a reference and this document ever
disagree, **this document governs**.

This Skill defines **which facts must be established**, not **which commands to
run**. Any agent that can read Markdown and perform ordinary Git and repository
operations can execute it.

## Purpose

Execute several Issues or tasks safely as one batch by:

1. making the work set and its dependencies **explicit before execution**,
2. executing each unit of work in an **isolated worker**,
3. **validating** every worker result before it is allowed to influence the
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

When it applies, the [mandatory planning artifacts](#mandatory-planning-artifacts)
MUST be produced before any implementation work begins — including when only one
worker will ultimately be used.

Do **not** apply this Skill to a single, indivisible task. That follows
[`../../task/implementation/SKILL.md`](../../task/implementation/SKILL.md)
directly.

## Definitions

| Term | Definition |
|---|---|
| **Batch** | One execution of this protocol over a defined set of tasks. |
| **Task** | One independently completable unit of work, normally one Issue. The unit of the dependency graph, of worker assignment, and of result classification. |
| **Worker** | An independent, delegated execution unit with its own context and its own isolated worktree and branch. A worker is an *abstraction*: how it is realized is left entirely to the executing agent's native capability. |
| **Wave** | A set of tasks whose dependencies are all satisfied and which have been established parallel-safe with respect to each other. |
| **Orchestrator** | The role executing this protocol. It plans, dispatches, validates and integrates. It never implements task work itself. |
| **Integration** | Bringing one worker's validated result into the shared branch, through the review, approval and merge gates. |
| **Gate** | A condition that MUST hold before the protocol may advance. |

## Evidence classification

Every finding this protocol relies on carries exactly one label. This vocabulary
is shared with the Merge Skill.

| Label | Meaning |
|---|---|
| **CONFIRMED** | Actually observed from a trusted source during this run. |
| **INFERRED** | A reasonable conclusion from CONFIRMED facts, not itself observed. |
| **UNVERIFIED** | Not established — including "the check could not be performed" and "the tool was unavailable". |

Rules:

- **Opening a gate requires CONFIRMED.** A gate whose status is INFERRED or
  UNVERIFIED is a closed gate.
- **Permitting parallel execution requires every safety condition CONFIRMED**
  for that specific pair of tasks. INFERRED or UNVERIFIED safety means
  **SERIAL**.
- Where the unknown affects correctness rather than only scheduling, the
  affected task is **BLOCKED**.
- UNVERIFIED findings MUST be reported, never omitted and never skipped because
  a check was unavailable.
- INFERRED may support a decision that fails safe — choosing serial execution is
  such a decision. It may never open a gate or authorise parallelism.

**Absence of evidence of a conflict is not evidence of independence.** Never
promote INFERRED to CONFIRMED because a conflict seems unlikely.

## Normative rules

### MUST

1. Produce the five [mandatory planning artifacts](#mandatory-planning-artifacts)
   before dispatching any worker.
2. Give every worker its **own worktree and own branch**. Workers never share a
   working tree.
3. Classify every task's outcome as exactly one **task result**: `SUCCESS`,
   `NO_OP`, `BLOCKED` or `FAILED`.
4. **Validate** each worker's delivered result before integrating it.
5. Re-establish dependency and semantic integration safety immediately before
   each integration, against the base **as it currently stands**.
6. Integrate **one task at a time**, in dependency order.
7. Delegate all merge execution to the Merge Skill
   ([`../merge/SKILL.md`](../merge/SKILL.md)).
8. Run aggregate verification, cleanup and reporting on **every** path,
   including every early stop.
9. Derive exactly one **batch outcome** by the
   [ordered outcome rules](#batch-outcome), and report the rule that produced it.
10. Report the task result for every task in the inventory, including everything
    not completed.

### MUST NOT

1. **MUST NOT** treat a batch as executed when a single worker processed every
   task sequentially without an inventory, graph and waves having been produced.
   See [Silent sequential fallback is forbidden](#silent-sequential-fallback-is-forbidden).
2. **MUST NOT** implement task work in the orchestrator context. The
   orchestrator never substitutes its own implementation for a worker's, and
   never repairs a worker's result to make it pass.
3. **MUST NOT** merge directly, use administrative bypass, force push, or push
   directly to the default branch.
4. **MUST NOT** integrate an unvalidated, incomplete, or malformed worker
   result.
5. **MUST NOT** integrate anything while a required condition is INFERRED or
   UNVERIFIED.
6. **MUST NOT** close an Issue because a worker reported completion. Issue
   closure is a consequence of the approved merge only.
7. **MUST NOT** treat `NO_OP` as `SUCCESS`, or as evidence that the batch
   passed.
8. **MUST NOT** change worker mechanism or provider as a retry. A mechanism
   switch is not a retry.
9. **MUST NOT** skip aggregate verification, cleanup or reporting because the
   batch stopped early.

### SHOULD

1. Keep concurrency at or below **3** simultaneous workers unless the operator
   raises it deliberately.
2. Retry a transient failure at most **3** times with exponential backoff.
3. Prefer a smaller wave over an uncertain one.

### MAY

1. Run waves with a single member when the graph or the available capability
   allows nothing wider.
2. Record batch progress in any durable form so an interrupted batch can be
   resumed. No particular format is required, and none is required to exist.

## Workflow

The batch performs these steps in this order. This is an ordered procedure, not
a state machine: there is no separate progress vocabulary to track, and no step
carries a transition table.

```text
 1. Retrieve the tasks or Issues from a trusted source.
 2. Confirm each one's live state and requirements.
 3. Build the task inventory.
 4. Resolve dependencies between tasks.
 5. Investigate each task's expected change surface.
 6. Classify every finding CONFIRMED / INFERRED / UNVERIFIED.
 7. Decide parallel safety, pairwise.
 8. Build the dependency graph and the execution waves.
 9. Provision an isolated worker, worktree and branch for each task in the wave.
10. Execute the wave, within the concurrency policy.
11. Validate each delivered worker result.
12. Re-evaluate dependency and semantic integration safety.
13. Pass eligible changes to the Merge Skill, one at a time, in dependency order.
14. Run aggregate verification when anything was integrated.
15. Attempt cleanup.
16. Report every task result and the batch outcome.
```

Steps 1–8 are the **planning stage** and are mandatory in full. Steps 9–13 are
the **execution stage** and repeat once per wave — a later wave is provisioned
from the base as it stands when that wave starts, which every prior integration
has moved. Steps 14–16 run on **every** path.

A batch with **nothing left to execute** — every task already resolved before
dispatch, whether `NO_OP`, `BLOCKED` or a mixture — is not a stop and records no
blocking condition. Steps 9–13 simply have nothing to do, aggregate
verification is `NOT RUN` because nothing was integrated, and the outcome is
derived at step 16 like any other batch's.

A condition that leaves the **batch as a whole** unable to continue safely is a
**batch-level blocking condition**. Most fail-closed stops are not one: they
stop a single task and leave the rest of the batch running
([Fail-closed rules](#fail-closed-rules)).

**A batch that stops on a batch-level blocking condition does not skip steps
14–16.** It records that condition verbatim, stops dispatching further workers,
integrates nothing that was not already established eligible, classifies every
task that has no established task result as `BLOCKED` naming that condition, and
then runs aggregate verification, cleanup and reporting exactly as a batch that
merged every task does. Stopping is a reportable outcome, not an exit.

## Mandatory planning artifacts

Before any worker is dispatched, all five artifacts below MUST exist and MUST
appear in the batch report:

| # | Artifact | Content |
|---|---|---|
| 1 | Task inventory | Every task, its identifier, its goal, its expected change surface |
| 2 | Dependency analysis | For each ordered pair, whether a dependency exists, or that it could not be established |
| 3 | Dependency graph | The DAG, with a completed cycle check |
| 4 | Execution waves | The wave assignment of every task that is still to be executed |
| 5 | Parallel/sequential classification | For every task in a wave, whether it is parallel-safe with its wave peers, with the evidence |

If any of these cannot be produced, the batch **stops**, records the exact
reason, and finishes through steps 14–16. It does not proceed as an ordinary
sequential implementation, and it does not stop silently.

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

## Dependency and parallel safety

### Dependencies

A dependency `A -> B` ("B depends on A") is established from any of:

| Source | Example |
|---|---|
| Declared | The Issue states "depends on #101" / "blocked by #101" |
| Structural | B modifies or consumes an interface, schema or contract that A introduces or changes |
| Sequential | A's change must exist in the base before B's change is meaningful |
| Verification | B's tests cannot pass until A is integrated |

Rules:

- Dependencies come from evidence, never from Issue-number ordering.
- A pair whose relationship cannot be established is **treated as dependent**.
  The direction is chosen so that the task with the broader or less certain
  change surface runs first, and the choice is recorded with its reason.
- The graph is built over the tasks that are still to be executed — the
  inventory minus every task that already holds a task result.
- **A cycle, or a graph that cannot be checked for cycles, blocks the batch.**
  The full cycle path is reported. The orchestrator MUST NOT break a cycle by
  dropping an edge on its own judgement; that is an operator decision.

### Prerequisites

- A task is not dispatched while any prerequisite is still to be resolved.
- Once a prerequisite holds a task result other than `SUCCESS` — `FAILED`,
  `BLOCKED` or `NO_OP` — every dependent that does not already hold a task
  result becomes `BLOCKED`, transitively, naming that prerequisite. A dependent
  that already holds a task result keeps it, and records the prerequisite as
  context.
- A prerequisite that is still being worked on holds **no** task result yet and
  therefore blocks nothing. Dependents wait; they are not pre-emptively blocked.
- A dependent is **blocked, never skipped**: it stays in the inventory and in the
  report, and its work is never integrated on the assumption that the
  prerequisite was optional.

### Parallel safety

Two tasks may share a wave **only when every dimension below is CONFIRMED for
that specific pair**:

- [ ] No dependency between them, in either direction
- [ ] No overlap of change surfaces — files, modules, generated artifacts
- [ ] No shared public contract, interface or schema change
- [ ] No shared mutable configuration
- [ ] No architectural coupling that requires ordering
- [ ] No execution-order requirement

**File non-overlap alone is never sufficient.** Two tasks touching disjoint
files can still conflict through a shared contract, shared configuration,
architectural coupling, or a required order. "Different files" is a starting
observation, not a conclusion.

| Evidence level on any dimension | Execution |
|---|---|
| All dimensions CONFIRMED | **PARALLEL permitted** |
| Any dimension INFERRED | **SERIAL** |
| Any dimension UNVERIFIED | **SERIAL**, or **BLOCKED** where the unknown affects correctness |

Serialization is a safety decision, not a degradation. A wave of one is a normal
outcome. Both are reported as such, with the reason.

### Waves

- A wave holds only tasks whose dependencies are already satisfied by an earlier
  wave, and whose members are pairwise parallel-safe.
- Wave membership expresses **permission** to run concurrently. The number of
  workers actually running at once is bounded by the concurrency policy
  ([`references/worker-and-isolation.md`](references/worker-and-isolation.md#concurrency)).
- **A wave's successor does not start until every member of the current wave
  holds a task result.** Because task result `SUCCESS` requires the integration
  to have completed, this is also what guarantees that every wave-1 change that
  was going to land has landed. A later task is provisioned from the base as it
  stands at that moment, so dispatching it while a prerequisite is validated but
  not yet integrated would build and verify it against a base that does not
  contain its own premise.
- Wave ordering never halts the batch. A member that ends `BLOCKED` or `FAILED`
  resolves the wave for it; the next wave proceeds with whichever of its own
  members are still executable.

## Result vocabulary

Task results and batch outcomes are two separate vocabularies with the same four
spellings. A task result describes one task; a batch outcome describes the
batch. Reporting MUST qualify every use — "task result `BLOCKED`", "batch outcome
`BLOCKED`".

### Task result

Every task in the inventory ends with exactly one:

| Task result | Meaning | Integration | Issue closure |
|---|---|---|---|
| `SUCCESS` | Work was required, the worker produced it, validation passed, and the required integration completed | Completed | Via approved merge only |
| `NO_OP` | No work was required — the change was already present, established by evidence | Not eligible | Never |
| `BLOCKED` | A required precondition, dependency, gate or piece of evidence did not hold, so the work could not safely proceed or conclude | Not eligible | Never |
| `FAILED` | Work was required and was attempted, and the implementation or its verification did not succeed | Not eligible | Never |

Rules:

- **`NO_OP` requires CONFIRMED evidence that the functionality is already
  present.** An absent diff is not that evidence. This holds identically whether
  the finding comes from the pre-dispatch check or from the worker: both produce
  task result `NO_OP`, and neither is `SUCCESS`.
- **Where it cannot be CONFIRMED whether the work was already done, the task is
  `BLOCKED`, not `NO_OP`.** `NO_OP` is a positive finding, never a fallback.
- `NO_OP` is reported as `NO_OP`. It never counts toward a passing batch, and it
  always leaves an explicit operator decision open on the underlying Issue.
- `BLOCKED` is not a milder `FAILED`. `FAILED` means work was attempted and did
  not work. `BLOCKED` means it was not attempted, or cannot be concluded,
  without a decision only the operator can make.
- A worker's own classification is an **input** to validation, never the task
  result. The orchestrator assigns the task result after validating the claim.

### Batch outcome

Derived once, at reporting time, by these rules **in order**. The first match
decides, and the report names the rule number.

| # | Condition | Batch outcome |
|---|---|---|
| 1 | Aggregate verification ran and failed | `FAILED` |
| 2 | Any task result is `FAILED` | `FAILED` |
| 3 | Any task result is `BLOCKED`, or a batch-level blocking condition was recorded ([Workflow](#workflow)) | `BLOCKED` |
| 4 | Aggregate verification is `NOT RUN` while at least one task result is `SUCCESS` | `BLOCKED` |
| 5 | At least one task result is `SUCCESS` **and** at least one is `NO_OP` | `BLOCKED` |
| 6 | The inventory is empty, or every task result is `NO_OP` | `NO_OP` |
| 7 | Every task result is `SUCCESS` **and** aggregate verification is `PASS` | `SUCCESS` |

The rules are exhaustive and deliberately overlap; the ordering is what makes
the outcome unique. Consequences worth stating:

- **Batch outcome `SUCCESS` requires every task in the inventory to be task
  result `SUCCESS`.** Rule 7 is the only route to it.
- **`NO_OP` is never promoted.** Rule 5 exists because a `NO_OP` task leaves an
  operator decision open; a batch that also integrated real work still has that
  decision outstanding.
- **Aggregate verification is load-bearing.** Rule 1 fails a batch whose tasks
  all reported `SUCCESS`, and rule 4 refuses `SUCCESS` to a batch that
  integrated work it never verified as a whole.
- **The reported cause is the condition, not the rule alone.** Where the batch
  itself stopped, the report quotes the stopping condition verbatim; "some task
  was blocked" is not a cause.

## Gates and integration

Integration is **serial**: exactly one task at a time, in dependency order, and
within a wave in the order results were validated.

Per task, in this order, each requiring CONFIRMED evidence to open:

```text
1. Eligibility      result validated, classification SUCCESS,
                    every dependency already integrated
2. Semantic safety  no semantic conflict against the base as it stands now
3. Review           reviewed per ../self-review/SKILL.md and satisfied
4. Approval         explicit, attributed, current, SHA-bound  → owned by the Merge Skill
5. Merge            → executed by the Merge Skill
6. Post-merge       → verified by the Merge Skill
```

Rules:

- **A gate whose status is not CONFIRMED open is closed.** There is no "probably
  approved" and no "conflict probably absent". A closed gate stops that task and
  is reported with the exact condition that could not be established.
- Before **each** integration the base is refreshed, the change re-verified
  against the current base, and semantic conflict detection re-run. Every prior
  integration in the batch has moved the base.
- Review is a distinct pass from authoring: the worker authors, and a separate
  context reviews. Automated reviewers are best-effort inputs; their absence,
  unavailability or pending status neither opens nor closes the gate on its own.
- If integration of one task stops, the remaining queue is **not** drained
  automatically. Dependents become `BLOCKED`, and remaining approvals and
  semantic determinations are re-established before any further integration.
- Nothing already merged is reverted automatically. Reverting is an operator
  decision.
- A gate stop is task-scoped. It stops that task; unrelated tasks keep running
  and integrating. That failure isolation is the reason this protocol exists.

### Merge Skill boundary

[`../merge/SKILL.md`](../merge/SKILL.md) is the **authority** on rebase,
approval validity, exact-SHA approval binding, mergeability, merge execution and
post-merge verification. This Skill MUST NOT perform, reimplement or approximate
any of them.

This Skill owns exactly one thing at the boundary: **which task is handed to the
Merge Skill, and in what order.** It invokes the Merge Skill for one task at a
time and accepts its outcome as authoritative. Where this document states a
merge or approval condition, that statement is a **reference** to the Merge
Skill's rule, never a second definition of it. If the two disagree, the Merge
Skill governs.

**Batch execution is never itself a merge condition.** A change does not become
mergeable because it is part of a batch.

## Aggregate verification

Per-task verification proves each task in isolation. Aggregate verification
proves the *combination*, which no worker observed. It runs when **anything was
integrated**, and confirms that:

1. the integrated base builds and its test suite passes as a whole,
2. each `SUCCESS` task's intended change is actually present in that base, and
3. no integration silently reverted an earlier one.

Its result is `PASS`, `FAIL` or `NOT RUN`, with the meanings
[`../reporting/SKILL.md`](../reporting/SKILL.md) gives those words.

- Nothing integrated → `NOT RUN`. That is a correct, expected report.
- Could not be run → `NOT RUN`, with the reason. It is never reported as
  passing.
- `FAIL` is reported against the integrated base, identifying which tasks are
  implicated. Nothing is reverted automatically.

## Cleanup

Cleanup removes isolation artifacts, and **only for work whose merge is
CONFIRMED present on the default branch**. It is attempted for every such task.

- Artifacts for `NO_OP`, `BLOCKED` and `FAILED` tasks are **deliberately
  preserved** where any were provisioned, so the work stays recoverable and
  diagnosable.
- A cleanup failure is **recorded and reported** with the artifact that could not
  be removed. It never reverts a merge, never changes a task result, and never
  stops reporting.
- Residual artifacts are listed in the report with their paths, so the operator
  knows exactly what remains and why.

Detail: [`references/worker-and-isolation.md`](references/worker-and-isolation.md#cleanup).

## Fail-closed rules

Uncertainty resolves to the safe side. "Probably fine" is not a permitted state.

| Unknown condition | Required resolution |
|---|---|
| Dependency between two tasks | Treat as **dependent**; order them |
| Change-surface overlap | Treat as **overlapping** → serial |
| Parallel safety | **Serial** |
| Whether a precondition holds | Task is **BLOCKED** |
| Whether the work was already done | Task is **BLOCKED**, not `NO_OP` |
| Worker result incomplete or malformed | **Do not integrate** |
| Semantic conflict status | **Do not integrate** |
| Review requirement or performance | **Do not integrate** |
| Approval existence or validity | **Do not merge** |
| Recorded batch progress unreadable | **Stop**; do not re-dispatch and do not integrate |

Every fail-closed stop MUST be reported with the exact condition that could not
be established. Most are **task**-scoped and leave the rest of the batch
running. A condition that leaves the **batch as a whole** unable to continue
safely is a batch-level blocking condition: it stops dispatch, and the batch
still runs steps 14-16 ([Workflow](#workflow)).

## Reporting contract

The batch report follows [`../reporting/SKILL.md`](../reporting/SKILL.md) and
MUST additionally contain:

**Plan** — the task inventory; the dependency determinations, including every
one that could not be established and how it was resolved; the DAG and the
cycle-check result; the wave assignment; the parallel/sequential classification
with the evidence and the reason for every serialization; and whether workers ran
concurrently or serially, and why.

**Per task**, for every task in the inventory without exception — its
identifier, its task result, its wave if one was assigned, its branch and
worktree if any were provisioned, whether it was integrated and via which merge,
and for every non-`SUCCESS` result the exact condition, quoted. Fields that do
not apply to a task may be omitted or marked absent; nothing is fabricated to
fill them.

**Batch** — the batch outcome with the rule number that produced it; the
stopping condition quoted verbatim if the batch stopped; counts per task result;
the aggregate verification result; every fail-closed stop with the condition
that could not be established; residual artifacts; and the remaining work and
operator decisions.

Honesty rules:

- A task absent from the report is a defect in the report.
- `NO_OP` is reported as `NO_OP`, never folded into `SUCCESS`.
- Verification that was not run is reported `NOT RUN`, never as passing.
- Batch outcome `SUCCESS` requires **every** task to be task result `SUCCESS`.
- A batch that integrated nothing is additionally reported as having produced no
  integrated change.
- The parallel-safety decision is reported explicitly, including which
  dimensions were CONFIRMED and which forced serial execution. A batch that ran
  serially because safety could not be proven is a **correct** outcome and is
  reported as such, not as a shortfall.

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
| Final report format and `PASS` / `FAIL` / `NOT RUN` semantics | [`../reporting/SKILL.md`](../reporting/SKILL.md) |

## References

| Reference | Contents |
|---|---|
| [`references/worker-and-isolation.md`](references/worker-and-isolation.md) | Worker abstraction and mechanism selection, pre-dispatch checks, worktree and branch strategy, concurrency, git safety, worker result reporting and validation, semantic conflict detection, cleanup |
| [`references/failure-and-recovery.md`](references/failure-and-recovery.md) | Failure classification, retry policy and budget, non-delivering workers, integration failure, resume and reconciliation |
| [`references/examples.md`](references/examples.md) | Worked conformance scenarios |

## Non-goals

- Replacing human judgement on merge timing.
- Merging without explicit approval.
- Implementing task work in the orchestrator.
- Parallel merges into the default branch.
- Administrative bypass or protection-rule circumvention.
- Providing or requiring a batch-specific executable runtime or configuration
  file.
- Tracking batch or task progress as a normative state machine. Progress is an
  execution detail; the protocol's closed vocabularies are the evidence labels,
  the task result and the batch outcome.
