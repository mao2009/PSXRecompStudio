# Workers, Isolation and Result Validation

**Status:** Stable

**Authority:** Reference — explanatory detail for
[`../SKILL.md`](../SKILL.md), which is the SSOT. Nothing here overrides a rule
stated there.

Covers the worker abstraction, the checks that run before a task is dispatched,
worktree and branch strategy, concurrency, git safety, what a worker reports,
how that report is validated, semantic conflict detection, and cleanup.

Everything here uses ordinary Git and repository operations. No batch-specific
tooling is required.

## Worker abstraction

> **Worker** = an independent, delegated execution unit with its own context and
> its own isolated worktree and branch.

This definition is deliberately mechanism-free. How a worker is realized is
decided entirely by the executing agent's native capability, and **no particular
mechanism is normative**. Valid realizations include, without preference:

- a native delegated task or sub-agent facility,
- a separate agent session or context,
- a separate process,
- the same context executing tasks one at a time against separate isolated
  worktrees.

### Prohibited normative dependencies

This Skill MUST NOT require, and MUST NOT be written against:

- any specific agent's sub-agent or task API,
- any specific CLI being installed or present on `PATH`,
- any specific provider, vendor or product,
- any specific operating system, shell or language runtime.

The presence of a tool on `PATH` is not configuration and never selects a worker
mechanism. Mechanism selection is an explicit operator or agent decision.

### Mechanism selection

| Priority | Mechanism | Condition |
|---|---|---|
| 1 | The executing agent's native delegated-execution capability | Available |
| 2 | An explicitly configured external mechanism | Configured explicitly by the operator |
| 3 | Same-context serialized execution with full isolation | No delegated capability available |

- **A mechanism switch is not a retry.** Retries stay within the selected
  mechanism; a task that failed under one mechanism is not re-attempted under
  another to make it pass.
- The selected mechanism MUST be recorded in the report.
- Priority 3 is a legitimate outcome and preserves all batch semantics. It is
  **not** permission to abandon them
  ([`../SKILL.md`](../SKILL.md#silent-sequential-fallback-is-forbidden)).

### Worker obligations

Every worker, however realized, MUST:

1. Work only within its assigned worktree and branch.
2. Investigate the task and the relevant architecture/SSOT before changing code.
3. Implement only the assigned task — no scope expansion into other tasks.
4. Run the task's verification.
5. Report its result honestly, including failure, in the form below.
6. Stop and report rather than work around a blocking condition.

Every worker MUST NOT:

1. Touch another worker's worktree or branch.
2. Modify the shared default branch.
3. Merge, force push, or push directly to the default branch.
4. Close the Issue it is working on.
5. Report unperformed verification as performed.
6. Report a partial or failed implementation as `SUCCESS`.

## Task inventory

For each discovered task, record:

| Field | Content |
|---|---|
| Task identifier | Stable, normally `issue-<number>` |
| Issue number | Where the task has one |
| Goal | What "done" means for this task, in one sentence |
| Expected change surface | Directories, files, modules or contracts the task is expected to touch |
| Declared dependencies | Task ids this task explicitly depends on |
| Owning SSOT | The architecture/SSOT document governing the change, where one applies |
| Verification | How this task's result will be verified |

Rules:

- The discovered set is written down before analysis begins. An implicit set is
  not a task set.
- The orchestrator MUST NOT silently add tasks that were not requested, and MUST
  NOT silently drop tasks that were.
- A field that cannot be established is recorded as **unestablished, with the
  exact reason** — never fabricated, never silently emptied. A task whose goal,
  change surface or verification could not be established is task result
  `BLOCKED`.
- A task set that cannot be resolved unambiguously stops the batch
  ([`../SKILL.md`](../SKILL.md#mandatory-planning-artifacts)).

## Pre-dispatch checks

These run **per task, before any worker is dispatched**, so that a dispatched
task is genuinely executable and its result means something.

| # | Condition | When it does not hold |
|---|---|---|
| 1 | The task's Issue exists | `BLOCKED` |
| 2 | The Issue is open | `BLOCKED` |
| 3 | No existing implementation PR for the Issue | `BLOCKED` |
| 4 | No conflicting active PR touching the same change surface | `BLOCKED` |
| 5 | The target functionality is not already present in the base | `NO_OP` — see below |
| 6 | The intended branch does not already exist | `BLOCKED` |
| 7 | The intended worktree path is free | `BLOCKED` |
| 8 | The task has not already been processed in this batch | `BLOCKED` |
| 9 | The working base is the expected base revision, and that revision is recorded | `BLOCKED` |

Rules:

- **A condition that cannot be evaluated is a failed condition.** These checks
  never pass on UNVERIFIED evidence.
- The exact failing condition is recorded and reported verbatim.
- A `BLOCKED` task requires an explicit operator decision before it can be
  retried. The orchestrator MUST NOT clear a block on its own judgement.
- The base revision used for the batch is recorded, so results can be
  interpreted against a known starting point.

### Already implemented is not success

Condition 5 exists to prevent the most damaging false positive: a batch that
"passes" because the work was already done before it started.

Whether it is found here, before dispatch, or by the worker during execution,
the task result is **`NO_OP`** — and in both cases it requires **CONFIRMED**
evidence that the functionality is already present, not merely an absent diff.
Where that cannot be CONFIRMED, the task is `BLOCKED`
([`../SKILL.md`](../SKILL.md#task-result)).

Neither path is `SUCCESS`. Neither closes an Issue. Neither counts toward a
passing batch.

## Isolation

**Every dispatched task gets its own worktree and its own branch. Without
exception.** A task that is never dispatched — resolved before dispatch as
`NO_OP` or `BLOCKED` — is never provisioned, and has no artifact to create,
preserve or clean up.

| Property | What isolation guarantees |
|---|---|
| Attribution | Every change is traceable to exactly one task |
| Independence | One task's failure cannot corrupt another's work |
| Validation | A result can be inspected on its own before it reaches shared state |
| Recoverability | A failed task's work survives for diagnosis |
| Ordering | Integration order is a decision, not an accident of execution order |

Isolation is required **regardless of concurrency**. An agent executing tasks
one at a time still gives each task its own worktree and branch. Serialized
execution is a scheduling property; shared mutable state is a safety defect.

### Prohibitions

- Two active tasks MUST NOT share a worktree.
- Two active tasks MUST NOT share a branch.
- A task MUST NOT be executed directly in the repository's primary working tree.
- A worker MUST NOT read from or write to another worker's worktree.

### Provisioning

```text
1. Determine the base revision, and record it
2. Compute the branch name and worktree path
3. Check for collision  ─── on collision → BLOCKED
4. Create the worktree with a new branch off the base revision
5. Confirm the worktree is present and on the expected branch
6. Initialize the environment the task needs
7. Record: task identifier, worktree path, branch, base revision
```

If any step fails, the task is task result `BLOCKED`, with the failing step
recorded. It is **never** run in a shared tree as a fallback, and no worker is
dispatched for it.

Provisioning MUST refuse when the intended worktree path already exists, or the
intended branch already exists locally or on the remote. A collision means
either a stale artifact from an earlier run or a genuine conflict with existing
work; both require an explicit decision. The orchestrator MUST NOT reuse,
overwrite, or auto-rename its way past a collision.

### Base revision

- All worktrees for a wave are created from the same recorded base revision.
- The base revision is recorded per task and reported.
- After any integration the base has moved. Later waves are provisioned from the
  **updated** base, and this is recorded.

### Naming

Naming is deterministic so that artifacts are identifiable and collisions are
detectable.

| Artifact | Pattern | Example |
|---|---|---|
| Branch | `issue/{issue_number}-{short_description}` | `issue/242-markdown-only-batch-skill` |
| Worktree directory | `{issue_number}-{short_description}` | `242-markdown-only-batch-skill` |
| Worktree location | A dedicated directory outside the primary working tree | `../worktrees/242-markdown-only-batch-skill` |

Permitted branch prefixes: `issue`, `feature`, `bugfix`, `hotfix`. `issue` is
the default for Issue-driven batch work.

`{short_description}` is normalized — lowercase, non-alphanumeric runs collapsed
to a single hyphen, trimmed — and normalization is applied consistently so the
same task always yields the same names.

Worktrees MUST be created outside the repository working tree so they are never
picked up as untracked content.

A retry re-runs the task in **new** isolation, named `…-attempt-{n}` for
attempt *n* > 1, so it never reclaims the previous attempt's worktree or branch
([`failure-and-recovery.md`](failure-and-recovery.md#retry-policy)).

### Concurrency

| Setting | Default | Meaning |
|---|---|---|
| Maximum simultaneous workers | **3** | Upper bound on workers running at once |

- An operator may raise or lower it deliberately; the value used MUST be
  reported.
- A wave larger than the limit is dispatched in limit-sized groups, in order.
  Isolation and ordering guarantees are unchanged.
- The limit bounds **execution** only. It never widens what may share a wave —
  that is decided solely by the parallel-safety rules
  ([`../SKILL.md`](../SKILL.md#parallel-safety)).
- An agent with no concurrent capability uses an effective limit of 1. That
  changes throughput only, never semantics.

Concurrency is never a reason to relax isolation, skip validation, or reorder
integration.

### Git safety prohibitions

These hold for the orchestrator and for every worker, at all times:

| Prohibited | Reason |
|---|---|
| Administrative merge bypass | Circumvents repository protection rules |
| Force push | Destroys history and invalidates review and approval |
| Direct push to the default branch | Bypasses PR, review and approval entirely |
| Merging outside the Merge Skill | Bypasses the approval and rebase gates |
| Deleting or rewriting another task's branch | Destroys isolation |
| Committing secrets or credentials | Security |
| Logging credentials | Security |

The default branch is modified **only** by the Merge Skill
([`../../merge/SKILL.md`](../../merge/SKILL.md)), and only after its gates pass.

A worker commits only its own intended changes. Unrelated modifications found in
a worktree are reported, not swept into the task's commit. The primary working
tree is not modified by the batch at all.

## What a worker reports

A worker's delivery is a **Markdown report**, not a serialization format. There
is no schema, no required key ordering and no required encoding.

A delivery MUST carry, for the aspects that apply to it:

| Reported | When it applies |
|---|---|
| Task and Issue identity | Always |
| Its own classification — `SUCCESS`, `NO_OP`, `BLOCKED` or `FAILED` | Always |
| What was investigated, including the SSOT consulted | Always |
| What was changed, or for `NO_OP` the evidence that it was already present | Always |
| Why this approach, and which alternatives were rejected | Always |
| Branch | Always |
| Build and verification results, with real `PASS` / `FAIL` / `NOT RUN` outcomes | Always |
| Changed files | When changes were made |
| Commit SHA | When a commit was produced |
| PR identity and state | When a PR exists |
| The exact failure or blocking condition | For `FAILED` and `BLOCKED` |
| Remaining work | When any remains |

**Applicable aspects MUST be present; aspects that do not apply MAY be omitted.**
There is no placeholder value to supply and no representation to get right — a
`NO_OP` delivery simply carries no commit SHA, and a `SUCCESS` delivery carries
no failure condition. What is forbidden is omitting something that *does* apply,
or reporting it falsely.

## Validating a worker result

Validation runs on **every** delivery, including one reporting `SUCCESS`. A
worker's self-assessment is an input to validation, never its conclusion.

Exactly one of these applies to any worker outcome, and they are checked in
order — the first that applies decides.

| # | Check | Outcome when it applies |
|---|---|---|
| 1 | **The worker delivered nothing usable** — no report, or one that cannot be read as a result at all | Nothing to validate. Retried within budget when the cause is retryable, otherwise task result `FAILED` ([`failure-and-recovery.md`](failure-and-recovery.md#a-worker-that-delivers-nothing)) |
| 2 | **The delivery is incomplete** — an aspect that applies to its classification is missing, or the task identity does not match the dispatch | Task result `FAILED`, recording what was missing |
| 3 | **The delivery's claims do not match observable reality**, or cannot be established against it | Task result `FAILED`, recording exactly what could not be established |
| 4 | **The result conflicts semantically with a peer result, or that cannot be determined** | Task result `BLOCKED`, recording the conflict or what could not be determined |
| 5 | Otherwise the delivery is valid, and its classification stands as the task result — subject, for `SUCCESS`, to the integration gates | See below |

Checks 2 and 3 ask whether the result **itself** is sound. "No" and "cannot be
established" are the same answer: a refusal to certify it. The defect is
internal to a result the orchestrator MUST NOT repair, and no operator decision
would change what the result contains. Both are `FAILED`.

Check 4 asks whether the result can coexist with something **outside** itself.
An unresolved or undeterminable semantic conflict says nothing against the
result — it may be perfectly valid — and resolving it needs a decision only an
operator can make. That is `BLOCKED`.

Substantive checks under check 3 include:

1. The claimed changed files actually differ on the worker's branch.
2. The changes lie within the dispatched scope. Out-of-scope changes fail.
3. The branch contains the claimed commit, built on the dispatched base
   revision.
4. The reported verification is consistent with what is observable — a `SUCCESS`
   claiming passing tests that were never run fails.
5. A `NO_OP` is substantiated by CONFIRMED evidence that the functionality was
   already present, not merely by an absent diff.

A valid `SUCCESS` becomes the task result `SUCCESS` only once its required
integration completes. A valid `NO_OP` is terminal as it stands: it is not
integration-eligible, there is nothing to approve and nothing to merge, and the
operator decision it carries stays open.

**The orchestrator MUST NOT repair a result to make it valid.** Repairing a
worker's output would make the orchestrator the implementer and destroy the
attribution that makes per-task validation meaningful.

## Semantic conflict detection

Textual merge cleanliness is not correctness. Two results can merge without any
git conflict and still be mutually incoherent. This is a separate, required
check.

It runs after validation and again **before each integration**, against the base
as it stands at that moment — every prior integration has moved the base.

| Check | Conflict indicator |
|---|---|
| Shared contract | Two results change the same interface, schema or contract in incompatible ways |
| Shared consumer | One result changes a contract another result's code still consumes in its old form |
| Duplicate implementation | Two results independently introduce the same capability |
| Behavioural override | One result's change silently negates another's |
| Shared configuration or generated artifact | Two results change the same key or regenerate the same artifact differently |
| Verification interference | One result's change causes another's verification to no longer hold on the integrated base |

| Determination | Action |
|---|---|
| No semantic conflict, CONFIRMED | Integration may proceed |
| Semantic conflict found | **Do not integrate** the affected results; report both tasks and the conflict |
| Cannot be determined | **Do not integrate** |

A detected semantic conflict is **not** resolved by the orchestrator editing the
results. It is reported and returned to the affected tasks, or escalated for an
operator decision.

## Issue lifecycle safety

| Rule | Requirement |
|---|---|
| Workers do not close Issues | A worker has no authority to close the Issue it implements |
| The orchestrator does not close Issues | A completion report is not grounds for closure |
| Closure follows the merge | An Issue closes only as a consequence of its approved change being merged |
| `NO_OP` never closes an Issue | Already-implemented requires an explicit operator decision |
| `BLOCKED` and `FAILED` never close an Issue | Obviously |

A worker's belief that it finished is exactly the claim the gates exist to
check. Letting that belief close the Issue would let a batch mark its own
homework.

## Cleanup

Cleanup removes isolation artifacts, and **only for work whose merge is
CONFIRMED present on the default branch**.

```text
1. Confirm the merge is actually present on the default branch
2. Remove the worktree
3. Delete the local branch
4. Delete the remote branch
5. Prune stale worktree references
```

Step 1 is a precondition, not a formality. Cleanup MUST NOT run on an
unconfirmed merge — that would destroy unmerged work.

### What is preserved

| Case | Worktree and branch |
|---|---|
| `SUCCESS`, merged and confirmed | Removed |
| `NO_OP` found by a worker that ran | **Preserved** — pending an operator decision |
| `NO_OP` found before dispatch | Nothing was ever provisioned |
| `BLOCKED` after provisioning | **Preserved** — the work may be resumable |
| `BLOCKED` before provisioning | Nothing was ever provisioned |
| `BLOCKED` by a failed provisioning attempt | Whatever the attempt left is **preserved**, and the failing step reported |
| `FAILED` | **Preserved** — required for diagnosis |
| Merge attempted but not confirmed | **Preserved** |
| A superseded retry attempt | **Preserved** — the attempt history is diagnostic |

Preserved artifacts are listed in the final report with their paths. A task that
was never provisioned has no artifact to preserve, and the report says so rather
than naming one that was never created.

### Cleanup failure

- Cleanup is **attempted** for every confirmed merge. An attempt that fails is
  recorded with the artifact that could not be removed, and the batch proceeds
  to reporting.
- Cleanup failure **never** reverts a completed merge. Merge and cleanup are
  independent; a merged change stays merged.
- Cleanup failure never turns a `SUCCESS` task into `FAILED`, and never changes
  any task result.
- Residual artifacts are reported so the operator knows what remains.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint and SSOT
- [`failure-and-recovery.md`](failure-and-recovery.md) — failure, retry, resume
- [`examples.md`](examples.md) — worked scenarios
- [`../../merge/SKILL.md`](../../merge/SKILL.md) — merge execution and its own cleanup
