# PR Merge Skill: Shell (POSIX) Runtime

The default, cross-platform implementation of the PR Merge Skill.
Self-contained on standard POSIX tooling — **no `pwsh`/PowerShell required**.

## Requirements

- POSIX `sh`
- Git
- GitHub CLI (`gh`) — optional but required for all GitHub PR operations
- Does NOT require: `pwsh`, `powershell`, `jq`, `python`, `node`

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

# Full context for a batch-driven merge
sh ./merge.sh merge --pr 149 --issue 148 \
    --worktree ../worktrees/148-e2e-test --branch issue/148-e2e-test \
    --repo owner/repo

# Show current state
sh ./merge.sh status --pr 149
```

The state machine is persisted to `.merge-state-<pr>.json` on every step, so
re-running `merge` resumes from the last recorded state.

## State Machine

`TRIGGER_CHECK → APPROVAL_VALIDATION → MAIN_HEAD_REFRESH → REBASE →
CONFLICT/VALIDATING → MERGING → MERGED → CLEANUP → COMPLETED`

Safety guarantees (unchanged from the previous runtime):

- **No admin bypass**: never invokes `gh pr merge --admin`
- No force push, no direct push to main
- Mandatory rebase onto latest `origin/main` before merge
- Approval tied to commit SHA and main HEAD SHA
- Conflicts are delegated back to a Sub-agent (never auto-resolved)

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
