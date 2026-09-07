# PR Merge Skill: Shell (POSIX) Runtime

The default, cross-platform implementation of the PR Merge Skill.
Self-contained on standard POSIX tooling — **no `pwsh`/PowerShell required**.

## Requirements

- POSIX `sh`
- Git
- GitHub CLI (`gh`) — optional but required for all GitHub PR operations
- GitHub JSON operations require **either `jq` or Python 3**. `jq` is preferred;
  Python 3 is the structured JSON fallback when `jq` is unavailable. If neither
  parser is available, GitHub JSON validation fails closed.
- Does NOT require: `pwsh`, `powershell`, `node`

## Layout

```text
runtime/
├── merge.sh              # CLI entry point
├── orchestrator.sh       # State-driven merge orchestration flow
├── state-machine.sh      # Merge state machine (pure logic)
├── approval.sh           # Approval state tracking and validation (pure logic)
├── git-operations.sh     # Git + gh operations (rebase, merge, cleanup)
├── persistence.sh        # JSON state I/O (atomic writes, fail-closed)
├── tests/                # POSIX shell test suite
└── powershell/           # Legacy PowerShell runtime
```

## Usage

### Merge a PR (resumable, one step per invocation)

```sh
# Advance the merge for PR 149 (resumable, one step per invocation)
sh ./merge.sh merge --pr 149

# Record an explicit human approval (solo-dev approval source)
sh ./merge.sh approve --pr 149 --worktree ../worktrees/149-merge

# Full context for a batch-driven merge
sh ./merge.sh merge --pr 149 --issue 148 \
    --worktree ../worktrees/148-e2e-test --branch issue/148-e2e-test \
    --repo owner/repo

# Show current state
sh ./merge.sh status --pr 149
```

The state machine is persisted to `.merge-state-<pr>.json` on every step, so
re-running `merge` resumes from the last recorded state.

### Merge method

The standard method is **Squash and merge** (`gh pr merge <pr> --squash`,
Issue #265). It is the configured default (`config/merge-config.json` →
`merge.strategy`); missing or malformed configuration fails closed to
`--squash` rather than reintroducing a merge commit.

A merge commit or a rebase merge is an **exception**, requested explicitly on
the invocation that performs the merge:

```sh
sh ./merge.sh merge --pr 149 --merge-method --merge
sh ./merge.sh merge --pr 149 --merge-method --rebase
```

The override is per-invocation and is never persisted, so a resumed run returns
to Squash and merge. An unrecognized method is rejected up front.

### SHA semantics

Squash and merge creates a **new** commit on `main`. Five SHAs are kept apart
and are never conflated:

| Concept | Where it lives |
|---|---|
| pre-rebase HEAD | discarded once the mandatory rebase runs |
| post-rebase PR HEAD | `CurrentCommitSha` |
| approved PR HEAD | `ApprovedCommitSha`, and the `Approval` record's `CommitSha` |
| final PR HEAD | the candidate revalidated immediately before the merge |
| squash commit SHA | `MainCommitSha`, written only by post-merge verification |

The approval binds to the final PR HEAD, never to the squash commit, which does
not exist until the merge has happened. After the merge,
`PR HEAD SHA != squash commit SHA` is the **expected, passing** result:
`merge_pr_merged_status` reports the two under the separate keys
`pr_head_sha` and `main_commit_sha` precisely so nothing downstream can read one
as the other.

### Explicit Human Approval

`approve` records an `explicit_human` approval as a first-class approval source
separate from the GitHub third-party review gate. It:

- attributes `approved_by` to the operator's authenticated GitHub identity
  (`gh api user` login) — never to an arbitrary command-line string and never
  to operator-controlled local git config; if no authenticated identity is
  available the operation fails closed;
- binds the approval to the current PR HEAD SHA **and** the current main HEAD
  SHA, so any change invalidates it;
- is created only by this explicit operation — hand-editing the state file is
  not accepted; a record missing the required identity/timestamp/binding fields
  is rejected (fail closed);
- is resumable: `approve -> (interruption) -> merge` re-validates the persisted
  approval before proceeding.

It never fakes a GitHub APPROVED review and never uses `--admin` or
unconditional force push,
or protection bypass.

## State Machine

`TRIGGER_CHECK → MAIN_HEAD_REFRESH → REBASE → VALIDATING →
APPROVAL_VALIDATION → MERGING → MERGED → CLEANUP → COMPLETED`

The SHA-bound human approval gate runs on the **final merge candidate**, after
the mandatory rebase and the CI/review gates have produced it (Issue #247).
Approval is never requested for an intermediate SHA the rebase is already known
to discard, so the normal case needs exactly one approval.

From `REBASE`: the mandatory rebase first compares the worktree with the PR
branch head on the remote and fast-forwards onto it when the remote is ahead, so
the candidate is the commit GitHub would merge. That step is fast-forward only,
and a diverged or undeterminable remote head fails closed. A clean rebase then
always continues to `VALIDATING`. A changed HEAD additionally runs the safe
rebase push and discards any pre-rebase approval, so the gate downstream asks for
one bound to the rebased SHA. Rebase or safe-push failure is fail-closed and
cannot reach `VALIDATING` or `MERGING`; a conflict goes to `CONFLICT` and never
reaches the approval gate.

From `APPROVAL_VALIDATION`: a candidate that is not proven rebased onto the live
main HEAD -- no recorded rebase base, or main advanced since the rebase --
discards any approval and returns to `MAIN_HEAD_REFRESH` for another mandatory
rebase. The same applies when the remote PR head is strictly ahead of the
candidate, so a push landing while the gate waits never produces an approval
request for a SHA GitHub would not merge.

From `MERGING`: a final HEAD revalidation runs immediately before the merge. It
requires the approved SHA to still equal the local PR HEAD and the PR HEAD
GitHub reports, main to be unmoved since the rebase, and the PR to still be
open, non-draft, mergeable and passing its gates. Any divergence discards the
approval and the merge does not proceed on that invocation. A moved main HEAD,
and a remote PR head that is not the candidate, both return to
`MAIN_HEAD_REFRESH` so the candidate is rebuilt; only a fault confined to the
approval record returns to `APPROVAL_VALIDATION`. Routing remote drift through
the rebase is what makes it converge: re-prompting would offer the unchanged
local HEAD, which this guard would reject again.

Safety guarantees (unchanged from the previous runtime):

- **No admin bypass**: never invokes `gh pr merge --admin`
- No unconditional force push, no direct push to main
- After mandatory rebase, a feature branch may be updated only through the
  runtime's explicit-SHA `--force-with-lease` controlled exception; plain
  `--force-with-lease`, main, and protected base branches are forbidden.
- Mandatory rebase onto latest `origin/main` before merge
- A changed rebased HEAD invalidates any persisted pre-rebase approval; the
  approval gate downstream requires one bound to the rebased HEAD
- Approval tied to the PR HEAD SHA and main HEAD SHA, and re-checked against the
  GitHub PR HEAD immediately before the merge
- The worktree is only ever fast-forwarded onto the remote PR head; diverged
  histories fail closed rather than being rewritten or force-pushed
- The candidate that is merged is always the PR HEAD the approval record binds
  to. The squash commit the merge then creates on `main` is a separate SHA and
  is never compared against, or substituted for, that approved PR HEAD
- A merge commit or rebase merge is never selected by default; it requires an
  explicit, per-invocation `--merge-method` request
- Conflicts are delegated back to a Sub-agent (never auto-resolved)
- CodeRabbit is a best-effort automated reviewer outside this runtime, so the
  runtime never polls it and never derives a merge decision from it. Whether the
  available review evidence lets a candidate merge is decided by the Skill's
  [Review Provider Policy](../REVIEW_PROVIDER_POLICY.md) — including the
  fail-closed fallback, which requires an independent current-HEAD review — and
  is carried into the runtime as the human approval it validates. A provider
  failure state is never treated here as a review pass.

## Testing

```sh
sh ./merge.sh test
```

Or run a single suite:

```sh
sh tests/test-state-machine.sh
```

## Retirement of `runtime/powershell/`

The PowerShell runtime (`runtime/powershell/`) is retained as a legacy/Windows
option. It is only needed if an agent explicitly invokes `wrapper/merge.ps1`.
No Skill execution path requires `pwsh`.
