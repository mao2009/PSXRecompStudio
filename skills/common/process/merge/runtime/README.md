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

`TRIGGER_CHECK → APPROVAL_VALIDATION → MAIN_HEAD_REFRESH → REBASE`

From `REBASE`: `HEAD unchanged → VALIDATING`, provided the existing approval
still binds to the exact HEAD; `HEAD changed → safe rebase push → invalidate
stale approval → APPROVAL_VALIDATION → MAIN_HEAD_REFRESH → REBASE`, where a
fresh SHA-bound approval is required. Rebase or safe-push failure is
fail-closed and cannot reach `VALIDATING` or `MERGING`.

Safety guarantees (unchanged from the previous runtime):

- **No admin bypass**: never invokes `gh pr merge --admin`
- No unconditional force push, no direct push to main
- After mandatory rebase, a feature branch may be updated only through the
  runtime's explicit-SHA `--force-with-lease` controlled exception; plain
  `--force-with-lease`, main, and protected base branches are forbidden.
- Mandatory rebase onto latest `origin/main` before merge
- A changed rebased HEAD invalidates persisted approval and returns to
  `APPROVAL_VALIDATION`; unchanged HEAD approval remains SHA-bound
- Approval tied to commit SHA and main HEAD SHA
- Conflicts are delegated back to a Sub-agent (never auto-resolved)
- CodeRabbit is a best-effort automated reviewer outside this runtime. Missing,
  skipped, pending, unavailable, or rate-limited reviews do not block the merge
  flow; findings that are present remain subject to human review.

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
