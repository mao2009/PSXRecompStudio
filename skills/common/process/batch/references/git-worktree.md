# Isolation, Worktrees and Branches

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Defines the isolation model, worktree and branch strategy, the concurrency
policy, git safety prohibitions, and cleanup.

Everything here uses ordinary `git` operations. No batch-specific tooling is
required.

## 1. Isolation model

**Every task gets its own worktree and its own branch. Without exception.**

Isolation is what makes the rest of the protocol sound:

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

### 1.1 Prohibitions

- Two active tasks MUST NOT share a worktree.
- Two active tasks MUST NOT share a branch.
- A task MUST NOT be executed directly in the repository's primary working tree.
- A worker MUST NOT read from or write to another worker's worktree.

## 2. Provisioning

Provisioning happens before dispatch, per task.

```text
1. Determine the base revision, and record it
2. Compute the branch name and worktree path
3. Check for collision  ─── on collision → BLOCKED
4. Create the worktree with a new branch off the base revision
5. Validate the worktree is present and on the expected branch
6. Initialize the environment the task needs
7. Record: task_id, worktree path, branch, base revision
```

If any step fails, the task is `BLOCKED`. It is **never** run in a shared tree
as a fallback.

### 2.1 Collision detection

Provisioning MUST refuse when either:

- the intended worktree path already exists, or
- the intended branch already exists (locally or on the remote).

A collision means either a stale artifact from an earlier run or a genuine
conflict with existing work. Both require an explicit decision. The orchestrator
MUST NOT reuse, overwrite, or auto-rename its way past a collision.

### 2.2 Base revision

- All worktrees for a wave are created from the same recorded base revision.
- The base revision is recorded per task and reported.
- After any integration, the base has moved. Later waves are provisioned from
  the **updated** base, and this is recorded.

## 3. Naming

Naming is deterministic so that artifacts are identifiable and collisions are
detectable.

| Artifact | Pattern | Example |
|---|---|---|
| Branch | `issue/{issue_number}-{short_description}` | `issue/242-markdown-only-batch-skill` |
| Worktree directory | `{issue_number}-{short_description}` | `242-markdown-only-batch-skill` |
| Worktree location | A dedicated directory outside the primary working tree | `../worktrees/242-markdown-only-batch-skill` |

Permitted branch prefixes: `issue`, `feature`, `bugfix`, `hotfix`. `issue` is
the default for Issue-driven batch work.

`{short_description}` is normalized: lowercase, non-alphanumeric runs collapsed
to a single hyphen, trimmed. Normalization MUST be applied consistently so that
the same task always yields the same names.

Worktrees MUST be created outside the repository working tree so they are never
picked up as untracked content.

## 4. Concurrency policy

| Setting | Default | Meaning |
|---|---|---|
| Maximum simultaneous workers | **3** | Upper bound on workers running at once |

Rules:

- The default is 3. An operator may raise or lower it deliberately; the value
  used MUST be reported.
- A wave larger than the limit is dispatched in limit-sized groups, in order.
  Task isolation and ordering guarantees are unchanged.
- The limit bounds **execution** only. It never widens what may share a wave —
  that is decided solely by
  [`dependency-analysis.md` §5](dependency-analysis.md#5-parallel-safety-determination).
- An agent with no concurrent capability uses an effective limit of 1. This
  changes throughput only, never semantics.

Concurrency is never a reason to relax isolation, skip validation, or reorder
integration.

## 5. Git safety prohibitions

These hold for the orchestrator and for every worker, at all times:

| Prohibited | Reason |
|---|---|
| Administrative merge bypass | Circumvents repository protection rules |
| Force push | Destroys history and invalidates review and approval |
| Direct push to the default branch | Bypasses PR, review, and approval entirely |
| Merging outside the Merge Skill | Bypasses the approval and rebase gates |
| Deleting or rewriting another task's branch | Destroys isolation |
| Committing secrets or credentials | Security |
| Logging credentials | Security |

The default branch is modified **only** by the Merge Skill
([`../../merge/SKILL.md`](../../merge/SKILL.md)), and only after its gates pass.

### 5.1 Working tree hygiene

- A worker commits only its own intended changes.
- Unrelated modifications found in a worktree are reported, not swept into the
  task's commit.
- The primary working tree is not modified by the batch at all.

## 6. Cleanup

Cleanup removes isolation artifacts. It runs **only for work that was
integrated**.

### 6.1 What is cleaned

For each task whose merge is confirmed:

```text
1. Confirm the merge is actually present on the default branch
2. Remove the worktree
3. Delete the local branch
4. Delete the remote branch
5. Prune stale worktree references
```

Step 1 is a precondition, not a formality. Cleanup MUST NOT run on unconfirmed
merges — that would destroy unmerged work.

### 6.2 What is deliberately preserved

| Task state | Worktree and branch |
|---|---|
| `SUCCESS`, merged and confirmed | Removed |
| `NO_OP` | **Preserved** — pending an operator decision |
| `BLOCKED` | **Preserved** — the work may be resumable |
| `FAILED` | **Preserved** — required for diagnosis |
| Merge attempted but not confirmed | **Preserved** |

Preserved artifacts are listed in the final report with their paths, so the
operator knows exactly what remains and why.

### 6.3 Cleanup failure

- Cleanup failure **never** reverts a completed merge. Merge and cleanup are
  independent; a merged change stays merged.
- A cleanup failure is reported, with the artifact that could not be removed.
- A cleanup failure does not make a `SUCCESS` task `FAILED`.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`dependency-analysis.md`](dependency-analysis.md) — wave and parallel-safety rules
- [`worker-contract.md`](worker-contract.md) — preflight and worker obligations
- [`orchestration.md`](orchestration.md) — phase contracts
- [`review-and-gates.md`](review-and-gates.md) — merge delegation
- [`../../merge/SKILL.md`](../../merge/SKILL.md) — merge execution and cleanup ownership
