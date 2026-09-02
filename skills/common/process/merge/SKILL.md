---
name: pr-merge
description: >
  Safe PR Merge Skill for Batch and Standalone use.
  Enforces mandatory approval → rebase → validation → normal merge flow.
  Prevents admin bypass and protection rule circumvention.
  Cross-platform: POSIX shell (default) and PowerShell implementations.
version: 1.3.0
scope: process
platform: agent-agnostic
related-issues: "#146, #176"
---

# PR Merge Skill

A safe, standalone PR Merge Skill that enforces a strict merge flow:
**User Approval → Latest Main HEAD Rebase → Validation → Normal Merge**.

This Skill prevents admin bypass and protection rule circumvention,
ensuring that merges only happen through the standard GitHub merge path.

## Core Principles

1. **Safety over speed**: Never merge if conditions are not met
2. **No admin bypass**: Never use `--admin`, force push, or protection circumvention
3. **Mandatory rebase**: Always rebase onto latest main HEAD before merge
4. **Approval as gate**: User approval is required and tied to commit SHA
5. **Conflict delegation**: Conflicts are delegated back to Sub-agent, not auto-resolved
6. **Standalone and Batch compatible**: Same safety conditions regardless of invocation

## When to apply

This Skill is triggered when:

| Condition | Description |
|-----------|-------------|
| PR exists | A GitHub PR is ready for merge |
| User approval | User has explicitly approved the PR |
| All checks pass | Required checks are green |
| No conflicts | Branch is up-to-date with main |

### Invocation Modes

| Mode | Description |
|------|-------------|
| **Standalone** | Direct invocation for single PR merge |
| **Batch** | Called by Batch Skill orchestrator |

## Inputs

1. **PR Number** (required): The GitHub PR number to merge
2. **Repository** (optional): owner/repo format (auto-detected if not provided)
3. **Worktree Path** (optional): Path to the Worktree (for Batch mode)
4. **Branch Name** (optional): Branch name (for cleanup after merge)

## Procedure

### 1. Trigger Check

Verify preconditions before proceeding:

```text
1. PR exists and is open
2. Target branch is main
3. PR is mergeable (not draft, no conflicts)
4. Required checks are passing
```

If any precondition fails, **stop and report**.

### 2. Approval Validation

Check for a valid approval record:

```text
1. Load approval record (if exists)
2. Verify approval exists
3. Verify the approval source:
     github_review  -> the repository-owned CodeRabbit Review Gate is the
                       authoritative CodeRabbit quality result. The GitHub
                       reviewDecision field is not proof of SHA-bound review.
     explicit_human -> a formal approval source created by `merge.sh approve`:
                       attribute to the authenticated operator identity and
                       bind to both the PR HEAD SHA and the main HEAD SHA.
4. Verify approved_commit_sha matches current_commit_sha
5. Verify main_head_sha matches current main HEAD
6. Reject unknown approval sources, missing identity/timestamp, and stale
   or malformed records (fail closed)
7. If approval is invalid, require a fresh explicit approval
```

**No valid approval = No merge.**

### 3. Explicit Human Approval (new)

For solo/personal development where a GitHub third-party approval is not
available, an **Explicit Human Approval** can be recorded as a first-class,
auditable approval source. It is **not** a fake GitHub APPROVED review and
never uses `--admin`, force push, or protection bypass.

```sh
# Record an explicit human approval bound to the current PR HEAD and main HEAD
merge.sh approve --pr <number> --worktree <path> [--main-dir <path>]

# The normal merge flow then resumes and validates the recorded approval
merge.sh merge --pr <number> --worktree <path>
```

Key properties:

- **Authenticated identity**: `approved_by` is taken from the operator's real
  authenticated GitHub identity (`gh api user` login). It is never an arbitrary
  `--approved-by` value, and operator-controlled local git config is never used
  as identity, so an operator cannot impersonate another approver. If no
  authenticated identity is available, explicit approval fails closed.
- **SHA binding**: the approval is bound to the PR HEAD SHA **and** the main
  HEAD SHA at approval time. Any change to either invalidates the approval.
- **Explicit operation**: merely editing the state file is not accepted as an
  approval; only the `merge.sh approve` operation produces a valid record, and
  a hand-crafted record that omits the required identity/timestamp/SHA fields
  is rejected.
- **Resumable**: `approve -> state saved -> (interruption) -> merge resume`
  re-validates the persisted approval before proceeding.

### 3. Main Head Refresh

Fetch the latest main HEAD:

```text
1. git fetch origin main
2. Record current main HEAD SHA
3. Update approval record with new main HEAD (if needed)
```

### 4. Mandatory Rebase

Always rebase onto latest main HEAD:

```text
1. git rebase origin/main
2. If clean → proceed to validation
3. If conflict → stop, delegate to Sub-agent
```

**Never skip rebase, even if branch appears up-to-date.**

### 5. Conflict Handling

If rebase produces conflicts:

```text
1. Record conflict state
2. Abort rebase (git rebase --abort)
3. Return to caller with conflict information
4. Sub-agent resolves conflicts
5. Sub-agent updates PR
6. Approval invalidated
7. User re-approves
8. Merge Skill re-fired
```

**Never auto-resolve conflicts.**

### 6. Validation

After successful rebase:

```text
1. Verify no merge conflicts exist
2. Run required tests (if configured)
3. Verify PR is still mergeable
4. Record validation result
```

### 7. Normal Merge

Execute merge through standard GitHub path:

```text
1. gh pr merge <pr-number> --merge
2. Verify merge succeeded
3. Record merge commit SHA
```

**NEVER use:**
- `gh pr merge --admin`
- Force push
- Direct push to main
- Protection rule bypass

### 8. Post-Merge Verification

Verify the merge on GitHub:

```text
1. Confirm PR state is MERGED
2. Verify merge commit exists on main
3. Record final state
```

### 9. Cleanup

After successful merge verification:

```text
1. Delete Worktree (if provided)
2. Delete local Branch
3. Delete remote Branch
4. Prune stale references
5. Mark COMPLETED
```

**Merge and Cleanup are separate states.**

## State Machine

### States

```text
TRIGGER_CHECK
APPROVAL_VALIDATION
MAIN_HEAD_REFRESH
REBASE
CONFLICT
VALIDATING
MERGING
MERGED
CLEANUP
COMPLETED
FAILED
```

### Transitions

```text
TRIGGER_CHECK
    ↓
APPROVAL_VALIDATION
    ↓
MAIN_HEAD_REFRESH
    ↓
REBASE
    ├─ conflict → CONFLICT → (return to caller)
    └─ clean → VALIDATING
                   ↓
                 MERGING
                   ↓
                 MERGED
                   ↓
                 CLEANUP
                   ↓
                 COMPLETED
```

### Transition Rules

| From | To | Condition |
|------|----|-----------|
| TRIGGER_CHECK | APPROVAL_VALIDATION | Preconditions met |
| TRIGGER_CHECK | FAILED | Preconditions not met |
| APPROVAL_VALIDATION | MAIN_HEAD_REFRESH | Approval valid |
| APPROVAL_VALIDATION | FAILED | No approval or invalid |
| MAIN_HEAD_REFRESH | REBASE | Main HEAD fetched |
| REBASE | VALIDATING | Rebase clean |
| REBASE | CONFLICT | Rebase conflicts |
| VALIDATING | MERGING | Validation passed |
| VALIDATING | FAILED | Validation failed |
| MERGING | MERGED | Merge succeeded |
| MERGING | FAILED | Merge failed |
| MERGED | CLEANUP | Verification passed |
| CLEANUP | COMPLETED | Cleanup succeeded |
| CLEANUP | FAILED | Cleanup failed |

## Approval Model

### Approval Sources

Two approval sources are supported, each validated separately:

| Source | Validation |
|--------|------------|
| `github_review` | Existing GitHub third-party approval. The repository-owned `CodeRabbit Review Gate` is authoritative for CodeRabbit quality; `reviewDecision` alone is not SHA-bound evidence. Required GitHub approvals remain separate merge checks. |
| `explicit_human` | Formal solo-dev approval created by `merge.sh approve`. Verified by authenticated operator identity, PR HEAD SHA binding, and main HEAD SHA binding. |

The `Approval` object in state carries `"ApprovalSource"`. An absent source is
treated as the legacy `github_review` default; any present-but-unknown value
fails closed. Validation results from one source are never reused for the
other source.

### Approval Record

| Field | Description |
|-------|-------------|
| `pr_number` | The PR number |
| `issue_number` | The Issue number |
| `commit_sha` / `approved_commit` | The commit SHA being approved (PR HEAD) |
| `main_head_sha` / `approved_main_head` | The main HEAD SHA at approval time |
| `approved_by` | Authenticated identity of the approver |
| `approved_at` | When approved (ISO 8601) |
| `approval_source` | `explicit_human` or `github_review` |
| `is_valid` | Whether approval is still valid |
| `notes` | Optional notes |

### Approval Invalidation

An approval is invalidated when:

- Rebase changed content
- Conflict resolution changed code
- Tests affected by changes
- PR content changed
- Artifact changes

### Validation

To validate an approval:

```text
1. Check is_valid flag
2. Compare approved_commit_sha with current commit
3. Compare approved_main_head_sha with current main HEAD
```

**All must match for approval to be valid.**

### CodeRabbit quality gate

Merge Skill does not reimplement CodeRabbit interpretation. It requires the
required repository check named `CodeRabbit Review Gate` to succeed on the
current PR head. That gate owns the direct current-head path and the narrowly
scoped content-equivalent-rebase path for `No files to review`. Raw status
success, skipped/missing/pending review, zero threads alone, or stale evidence
never satisfies it.

The `explicit_human` record created by `merge.sh approve` remains the
canonical locally persisted, authenticated SHA-bound human approval. An
untraceable prior-session assertion or inferred approval is not accepted; the
record must bind the current PR HEAD and current `main` HEAD.

## Conflict Handling

### Flow

When conflicts occur during rebase:

```text
1. Detect conflicts
2. Record conflict state
3. Abort rebase
4. Return to caller with conflict information
5. Sub-agent resolves conflicts in same Branch/Worktree
6. Sub-agent re-tests
7. Sub-agent updates PR
8. Approval invalidated
9. User re-approves
10. Merge Skill re-fired
11. New rebase attempt
```

### Conflict Information

Return to caller:

| Field | Description |
|-------|-------------|
| `has_conflicts` | Boolean |
| `conflict_files` | List of conflicting files |
| `worktree_path` | Path to Worktree |
| `branch_name` | Branch name |

## Merge Strategy

### Standard Merge

Use standard GitHub merge:

```text
gh pr merge <pr-number> --merge
```

### What is NOT allowed

| Method | Reason |
|--------|--------|
| `gh pr merge --admin` | Bypasses protection rules |
| `--squash` | Changes commit history |
| `--rebase` | May cause issues |
| Force push | Bypasses all checks |
| Direct push | Bypasses PR process |
| API merge with bypass | Circumvents protections |

## Batch Invocation

When called from Batch Skill:

```text
Batch Skill
    ↓
Merge Skill (PR #149)
    ↓
Preconditions check
    ↓
Approval validation
    ↓
Main HEAD refresh
    ↓
Rebase
    ↓
Validation
    ↓
Normal merge
    ↓
Cleanup
    ↓
Return to Batch Skill
```

**Batch Skill is NOT a merge condition.**

## Standalone Invocation

When invoked directly:

```text
User
    ↓
Merge Skill (PR #149)
    ↓
Preconditions check
    ↓
Approval validation
    ↓
Main HEAD refresh
    ↓
Rebase
    ↓
Validation
    ↓
Normal merge
    ↓
Cleanup
```

## Cleanup Process

After merge confirmation:

```text
1. Confirm PR merged on GitHub
2. Verify merge commit exists on main
3. Delete Worktree (if provided)
4. Delete local Branch
5. Delete remote Branch
6. Verify no remaining references
7. Mark COMPLETED
```

**Important:** Merge and Cleanup are separate states.
Cleanup failure does not revert merge.

## Resumability

### Persisted State

| Field | Description |
|-------|-------------|
| `pr_number` | The PR number |
| `issue_number` | The Issue number |
| `branch_name` | The Branch name |
| `worktree_path` | The Worktree path |
| `current_state` | The current state |
| `current_commit_sha` | Current commit SHA |
| `approved_commit_sha` | Approved commit SHA |
| `main_head_sha` | Main HEAD SHA |
| `created_at` | Creation timestamp |
| `updated_at` | Last update timestamp |

### Recovery Process

To resume a stopped process:

```text
1. Load persisted state
2. Verify PR exists and is open
3. Check current state
4. Resume from last known state
```

## Configuration

Project-specific configuration is externalized in `config/`:

```text
config/
└── merge-config.json    # Project configuration
```

## Runtime

The actual implementation lives in `runtime/`:

```text
runtime/
├── README.md               # Runtime documentation
└── <runtime-name>/         # Specific runtime implementation
    ├── modules/            # Reusable modules
    ├── scripts/            # Entry-point scripts
    └── tests/              # Tests
```

### Current Runtimes

| Runtime | Status | Platform |
|---------|--------|----------|
| Shell (POSIX sh) — Default | Implemented | Linux, macOS, Windows (POSIX) |
| PowerShell Core 7.x | Legacy | Windows, Linux, macOS |

The **Shell (POSIX sh) runtime** is the default for Linux/POSIX environments
and is fully self-contained on standard POSIX tooling (`sh`, `git`, and
optionally `gh`). It does **not** require `pwsh`/PowerShell.

The PowerShell runtime is retained as a legacy/Windows option and is only
needed if an agent explicitly invokes the `wrapper/merge.ps1` entry point.

### Shell Runtime Usage

The Shell runtime is a resumable state machine: each invocation processes
exactly one step (`TRIGGER_CHECK → APPROVAL_VALIDATION → MAIN_HEAD_REFRESH →
REBASE → CONFLICT/VALIDATING → MERGING → MERGED → CLEANUP → COMPLETED`) and
persists its state, so it is safe to re-run and can be resumed after an
interruption.

```sh
# Advance the merge for PR 149 (resumes from the current persisted state)
runtime/merge.sh merge --pr 149

# Record an explicit human approval for PR 149
runtime/merge.sh approve --pr 149 --worktree ../worktrees/149-merge

# Full context for a batch-driven merge
runtime/merge.sh merge --pr 149 --issue 148 \
    --worktree ../worktrees/148-e2e-test --branch issue/148-e2e-test \
    --repo owner/repo

# Show current state only
runtime/merge.sh status --pr 149

# Run the runtime test suite
runtime/merge.sh test
```

Requirements: POSIX `sh`, `git`, and (for GitHub PR operations) `gh`.
`pwsh`/PowerShell is **not** required.

## Porting to Another Project

To use this Skill in another project:

1. Copy the `skills/common/process/merge/` directory
2. Update `config/merge-config.json` with project-specific settings
3. Ensure the required Runtime is available
4. No changes to SKILL.md required

## Non-goals

- Replacing human judgment on merge timing
- Automatic merge without user approval
- Conflict resolution by Merge Skill
- Skipping rebase for "simple" changes
- Compromising main branch history
- Admin bypass or protection circumvention

## Changelog

- **1.3.0** — Explicit Human Approval (Issue #176):
  - Approval is bound to the worktree PR HEAD SHA and the current main HEAD SHA,
    and attributed to the operator's authenticated GitHub identity
    (`gh api user` login). Arbitrary `--approved-by` values and operator-controlled
    local git config are never accepted as identity; the operation fails closed if
    no authenticated identity is available.
  - Added the `approve` subcommand (`merge.sh approve`) which records an
    `explicit_human` Approval record. This is a distinct approval source from the
    existing GitHub third-party review gate (`github_review`).
    Unknown/malformed approval sources fail closed during validation.
  - `ApprovedAt` is validated as an ISO 8601 UTC timestamp (malformed values are
    rejected).
  - Batch `merge-queue.sh` gate now accepts a valid `explicit_human` approval
    recorded by this Skill, in addition to the GitHub review gate. The batch path
    stays fail-closed: it only honors an `explicit_human` record that is valid,
    identity-bearing, and bound to the current commit.
- **1.2.0** — GitHub third-party review gate enforced before merge; approval source
  separation (`github_review` vs `explicit_human`) framework introduced.
