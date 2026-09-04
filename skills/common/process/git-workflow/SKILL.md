---
name: git-workflow
description: >
  Git workflow skill for AI agents: the single source of truth for Issue-driven
  branch creation, commit/push mechanics, branch freshness, PR handoff, rebase
  safety, and post-merge cleanup. Use whenever an agent performs repository Git
  operations outside merge execution itself.
version: 0.1.0
scope: process
platform: agent-agnostic
related-issues: "#93"
---

# Git Workflow Skill

The single source of truth for **how Git repository work is performed from an
Issue through PR handoff and post-merge cleanup**.

This skill owns branch creation, repository-state checks, commit/push mechanics,
branch freshness, safe history rewriting, and the handoff boundaries between
implementation, Pull Request authoring, review, and merge.

It does **not** own commit-message wording, PR title/body wording, pre-PR code
review, or final merge eligibility. Those concerns remain in their dedicated
skills.

## Core principles

1. **`main` is merge-only.** Never commit or push feature work directly to
   `main`.
2. **One Issue, one working branch by default.** A branch must have a verified
   driving Issue or an explicitly approved non-Issue maintenance purpose.
3. **Ground truth over assumptions.** Inspect actual branch, diff, remote state,
   Issue state, PR state, and CI evidence before acting.
4. **No silent history destruction.** Never use plain `--force`; published
   history rewriting requires a justified rebase and `--force-with-lease`.
5. **No protection bypass.** Never use admin merge, direct `main` push, or any
   branch-protection circumvention.
6. **Preserve unrelated work.** Do not reset, clean, stash, amend, or include
   unrelated dirty changes unless the task explicitly owns them.
7. **Final merge rules belong to Merge Skill.** This skill prepares a clean,
   reviewable PR branch; it does not redefine approval or merge gates.

## Responsibility boundary

| Concern | Owned by |
|---|---|
| Issue-driven branch creation, commit/push mechanics, rebase safety, cleanup | **this skill** |
| Commit message format, type/scope/subject, trailers | [Commit Message Skill](../commit-message/SKILL.md) |
| Pre-PR implementation review | [Self-Review Skill](../self-review/SKILL.md) |
| PR title/body, Issue linkage, verification evidence | [Pull Request Skill](../pull-request/SKILL.md) |
| Final rebase/validation/approval/merge | [Merge Skill](../merge/SKILL.md) |
| Multi-Issue orchestration/worktree lanes | [Batch Skill](../batch/SKILL.md) |
| Final task reporting | [Reporting Skill](../reporting/SKILL.md) |

When two skills appear to overlap, the narrower skill owns the detailed rule and
this workflow only sequences the handoff.

## Standard lifecycle

```text
Issue
  ↓
Repository preflight
  ↓
Branch creation
  ↓
Implementation
  ↓
Commit(s)
  ↓
Push
  ↓
Self Review
  ↓
Pull Request creation
  ↓
CI / external review
  ↓
Merge Skill
  ↓
Post-merge verification
  ↓
Cleanup
```

## 1. Repository preflight

Before changing Git state:

1. Verify the repository and intended base branch.
2. Fetch current remote refs when network access is available.
3. Record the current `main` HEAD.
4. Inspect working-tree state (`tracked`, `modified`, `untracked`).
5. Identify unrelated dirty changes and preserve them untouched.
6. Verify the driving Issue exists and is open unless the task explicitly uses
   a non-Issue workflow allowed by project policy.
7. Check for an existing branch/PR for the same Issue before creating another.

If the working tree contains unrelated changes, the default is **work around
them**, not clean them up. Prefer a dedicated worktree or a branch operation that
leaves those files untouched.

## 2. Branch policy

### Base

Create normal development branches from the latest `main`.

Do not branch from a stale local `main` when the current remote state can be
resolved.

### Naming

Default branch pattern:

```text
<type>/<issue-number>-<short-kebab-summary>
```

Examples:

- `issue/93-git-workflow-skill`
- `fix/247-final-approval-ordering`
- `docs/250-reference-provenance`

The repository Project Profile may define a narrower convention. Never infer an
Issue number without verifying it.

### Existing branch

If a branch for the Issue already exists:

- reuse it when it is clearly the active branch for the same task;
- do not create a parallel branch merely because the local checkout is absent;
- inspect remote/local divergence before making changes;
- if ownership or purpose is ambiguous, fail closed rather than overwriting.

## 3. `main` merge-only policy

The following are forbidden for normal development:

- `git commit` while on `main`,
- `git push origin main` for feature/fix/docs work,
- editing a GitHub file directly on `main`,
- force-updating `main`,
- merging by writing commits directly into `main` history outside the approved PR
  merge path.

Allowed operations on `main` are read/fetch/update operations required to obtain
its latest state, plus post-merge verification and local fast-forward refresh.

Emergency repository-owner procedures, if any, must be separately documented;
they are not implied by this skill.

## 4. Implementation isolation

Keep task changes isolated from unrelated work.

Preferred options:

1. dedicated Git worktree for parallel/dirty environments,
2. clean task branch in the current checkout,
3. temporary patch/stash only when the unrelated work is owned by the same
   operator and the task explicitly permits it.

Do not automatically `git reset --hard`, `git clean -fd`, or stash another
actor's changes.

Generated files, formatting churn, line-ending-only changes, editor metadata,
and build artifacts must not ride along unless they are required by the Issue.

## 5. Commit workflow

Before each commit:

1. inspect `git status`;
2. inspect staged and unstaged diffs;
3. stage only files/hunks belonging to the logical change;
4. run the relevant verification for the changed scope;
5. use the Commit Message Skill to author the message;
6. verify no session URL, credential, private URL, secret, or sensitive locator
   appears in the commit message.

A commit should represent one logical change. If two concerns can be reviewed
independently, prefer separate commits.

Do not commit knowingly failing code merely to make progress visible on a public
branch. Temporary WIP commits may exist locally but must be normalized before
PR review.

## 6. Push policy

Normal push:

```text
git push -u origin <branch>
```

Before pushing:

- verify the current branch name,
- verify the destination remote/branch,
- verify the commit range to be published,
- verify no unrelated commits are included,
- re-check commit messages for sensitive/session data.

Never push directly to `main`.

## 7. History rewrite and rebase safety

### Unpublished history

Local unpublished commits may be amended, reordered, squashed, or rebased when
needed to produce a clean logical history.

### Published PR branch

Rewriting a published PR branch is allowed only when justified by the repository
workflow, such as the mandatory rebase before merge.

Requirements:

1. fetch the current remote branch first;
2. record the expected remote SHA;
3. ensure local and remote history are understood;
4. perform the intended rebase;
5. verify the resulting diff still matches the Issue scope;
6. push with **`--force-with-lease`**, bound to the observed remote state;
7. never use plain `--force`;
8. if the remote moved unexpectedly, stop instead of overwriting it.

A history rewrite invalidates any approval or review evidence that was bound to
the previous HEAD. The Merge Skill owns the exact final-approval handling.

### Divergence

If local and remote branches diverged unexpectedly:

- do not choose a side by force;
- inspect both histories;
- preserve unique commits;
- stop and report when the correct resolution is not mechanically provable.

## 8. Keeping a PR branch current

A PR branch may become behind `main` while review is in progress.

Do not merge a stale branch merely because old CI was green. Before final merge,
the Merge Skill requires its mandatory latest-main rebase and current-head
validation.

For preparatory work before that final gate, this workflow may rebase earlier
when useful, but every published rewrite must obey the force-with-lease rules.

After a rebase:

- verify the PR's changed-file scope,
- rerun affected tests/CI,
- update PR verification text if materially stale,
- treat previous SHA-bound evidence as stale.

## 9. Pull Request handoff

Before creating a PR:

1. branch is pushed and remote HEAD is known;
2. Issue scope is satisfied or clearly marked partial;
3. no unrelated files/commits are present;
4. local verification is current;
5. Self-Review Skill has completed;
6. Pull Request Skill is used to author title/body and Issue linkage.

PR creation itself must not automatically merge the PR.

After creation, re-fetch the PR and verify:

- target is `main`,
- head branch is correct,
- changed files match the intended scope,
- Issue linkage is correct,
- PR is not accidentally draft/non-draft contrary to intent.

## 10. CI and external review

CI and review results are observed against a specific PR HEAD.

Rules:

- never present pending/unknown CI as successful;
- after a new commit/rebase, old HEAD results are not final evidence for the new
  HEAD;
- actionable review findings must be resolved, not hidden by a history rewrite;
- provider-side review unavailability is handled by the Merge Skill's
  [Review Provider Policy](../merge/REVIEW_PROVIDER_POLICY.md), not by inventing
  a Git workaround;
- a generic status check named after a reviewer is not proof that a current-HEAD
  review completed unless the provider evidence establishes that fact.

## 11. Merge handoff

When the user requests merge, hand control to the Merge Skill.

The Git Workflow Skill must not bypass or duplicate:

- latest-main mandatory rebase,
- current-HEAD validation,
- review-provider/fallback policy,
- final SHA-bound explicit human approval,
- final HEAD/main revalidation,
- normal GitHub merge path.

No admin bypass, direct `main` push, or plain force push is permitted as a
substitute for a blocked merge.

## 12. Post-merge verification and cleanup

After GitHub reports the PR merged:

1. verify PR state is merged;
2. verify the driving Issue closed when `Closes`/`Fixes` was expected;
3. verify the merge/rebase result is present on `main`;
4. delete the remote task branch when project policy allows;
5. delete the local task branch when safe;
6. remove the task worktree when used;
7. prune stale refs;
8. refresh local `main` by fast-forward only.

Cleanup must never destroy unrelated dirty work.

If the PR was closed without merge, do not delete unique branch work unless the
user explicitly authorizes discarding it.

## AI-agent safety checks

Before every destructive or history-changing Git operation, verify:

- repository,
- branch,
- remote,
- expected current SHA,
- target SHA/base,
- whether the branch is published,
- whether unrelated work exists,
- whether the operation can discard commits.

The following are always prohibited in normal workflow:

- plain `git push --force`,
- direct push to `main`,
- `gh pr merge --admin`,
- reset/clean that discards unrelated work,
- fabricated Issue/PR/CI/review state,
- commit/PR metadata containing session URLs or credentials.

## Failure behavior

Fail closed when repository state cannot be established.

Examples:

- remote branch SHA cannot be resolved before a rewrite,
- branch unexpectedly diverged,
- Issue/PR mapping is ambiguous,
- current HEAD differs from the expected candidate,
- base branch changed unexpectedly,
- required verification cannot be associated with the current HEAD.

Report the exact blocking condition instead of guessing or silently repairing
history.

## Validation checklist

Before PR creation:

- [ ] Driving Issue verified.
- [ ] Branch is not `main` and follows repository convention.
- [ ] Branch was based on the intended `main` state.
- [ ] Unrelated dirty changes are preserved and excluded.
- [ ] Commits are scoped and commit messages satisfy Commit Message Skill.
- [ ] Remote push target is the task branch, never `main`.
- [ ] No plain force push or protection bypass was used.
- [ ] Required local verification is current.
- [ ] Self-review is complete.
- [ ] PR handoff uses Pull Request Skill.

Before history rewrite:

- [ ] Rewrite is justified.
- [ ] Remote branch was fetched and expected SHA recorded.
- [ ] Unique remote commits will not be discarded.
- [ ] Push uses `--force-with-lease` only.
- [ ] Post-rewrite tests/review evidence will be treated as new-HEAD evidence.

After merge:

- [ ] PR merge state verified.
- [ ] Issue close state verified when applicable.
- [ ] `main` contains the merged result.
- [ ] Task branch/worktree cleanup completed safely.
- [ ] Unrelated work remains untouched.

## Project-profile inputs

This skill is reusable across repositories. A host Project Profile may specify:

1. default branch name,
2. branch naming convention,
3. Issue requirement and linkage convention,
4. allowed merge methods,
5. verification ladder,
6. worktree conventions,
7. whether remote branches are deleted after merge,
8. repository-specific protected/sensitive paths,
9. review-provider policy location.

Project-specific values refine this skill but must not weaken its safety rules
without an explicit repository policy change.

## Completion criteria

This skill is satisfied when an agent can carry an Issue through branch creation,
commit/push, PR handoff, merge handoff, and cleanup while preserving these
invariants:

- `main` remains merge-only,
- task work is isolated and traceable to its Issue,
- unrelated work is preserved,
- history rewriting is lease-protected and auditable,
- commit/PR/review/merge responsibilities remain in their dedicated SSOTs,
- the final merge is performed only through the Merge Skill's validated path.

## Non-goals

- Defining commit-message wording.
- Defining PR title/body wording.
- Performing code self-review.
- Replacing Batch orchestration.
- Defining final merge approval rules.
- Using Git operations to bypass CI, review, or repository protection.
