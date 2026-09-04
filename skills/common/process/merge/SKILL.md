---
name: pr-merge
description: >
  Safe PR Merge Skill for Batch and Standalone use.
  Enforces mandatory rebase → validation → final SHA-bound approval → normal
  merge flow.
  Prevents admin bypass and protection rule circumvention.
  Cross-platform: POSIX shell (default) and PowerShell implementations.
version: 1.4.0
scope: process
platform: agent-agnostic
related-issues: "#146, #176, #247"
---

# PR Merge Skill

A safe, standalone PR Merge Skill that enforces a strict merge flow:
**Latest Main HEAD Rebase → Validation → Final SHA-bound Human Approval →
Normal Merge**.

The human approval gate runs **after** the mandatory rebase, so the approval
binds to the exact commit that will be merged. Requesting it earlier would bind
it to a SHA the mandatory rebase is already known to discard, forcing a second
approval round (Issue #247).

This Skill prevents admin bypass and protection rule circumvention,
ensuring that merges only happen through the standard GitHub merge path.

## Core Principles

1. **Safety over speed**: Never merge if conditions are not met
2. **No admin bypass**: Never use `--admin`, unconditional force push, or protection circumvention
3. **Mandatory rebase**: Always rebase onto latest main HEAD before merge
4. **Approval as final gate**: User approval is required and tied to the SHA of
   the final merge candidate, i.e. the post-rebase HEAD that will be merged
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
3. PR is not a draft
4. PR head/base branches are the ones under merge
```

If any precondition fails, **stop and report**.

These are technical safety preconditions for touching the branch at all. They
are independent of approval and are **never** relaxed because the approval gate
now runs later.

### 2. Main HEAD Refresh

Fetch the latest main HEAD:

```text
1. git fetch origin main
2. Record the current main HEAD SHA
3. Clear any recorded rebase base: the candidate is only proven rebased once
   the rebase completes against this refreshed main HEAD
```

### 3. Mandatory Rebase

Always rebase onto latest main HEAD, starting from the commit GitHub would
merge:

```text
1. Compare the worktree HEAD with the PR branch head on the remote:
     same         -> proceed
     remote_ahead -> fast-forward onto the remote PR head, then proceed
     local_ahead  -> proceed (local work not pushed yet)
     diverged     -> FAILED (never rewrite either side)
     unknown      -> FAILED (the candidate cannot be verified)
2. git rebase origin/main
3. If conflict  -> stop, delegate to Sub-agent
4. If failure   -> FAILED
5. If clean     -> record the resulting HEAD as the final merge candidate,
                   record the main HEAD it was rebased onto, and proceed to
                   validation
```

Step 1 is a **fast-forward only**: it creates no merge commit, rewrites no
history, pushes nothing, and never discards commits either side does not
contain. It exists so a PR head that moved on the remote becomes the candidate,
rather than the rebase producing a candidate the PR does not contain.

**Never skip rebase, even if branch appears up-to-date.**

If the rebase changes the PR HEAD, any approval already recorded belongs to a
pre-rebase SHA and is **discarded here**. The flow does not return to the
approval gate at this point; it continues to validation, and the gate downstream
requests a fresh approval bound to the rebased HEAD.

### 4. Conflict Handling

If rebase produces conflicts:

```text
1. Record conflict state
2. Abort rebase (git rebase --abort)
3. Return to caller with conflict information
4. Sub-agent resolves conflicts
5. Sub-agent updates PR
6. Merge Skill re-fired (a new mandatory rebase runs first)
```

**Never auto-resolve conflicts.** A conflicting rebase never reaches the
approval gate: no approval is requested for a candidate that does not exist.

### 5. Validation

After successful rebase, validate the **final merge candidate**:

```text
1. Verify PR is still open, not a draft, and mergeable
2. Verify required checks are passing for this HEAD
3. Verify the repository review gate is satisfied
4. Record validation result
```

A failure here stops the flow **before** any human approval is requested, so an
operator is never asked to approve a candidate that cannot merge.

### 6. Final Approval Validation

This is the SHA-bound human approval gate, and it runs on the validated,
post-rebase candidate:

```text
1. Resolve the current merge candidate HEAD and the live main HEAD
2. If the candidate is not proven rebased onto the live main HEAD -- no
   recorded rebase base, or main has advanced since the rebase -- discard any
   approval and return to MAIN_HEAD_REFRESH for another mandatory rebase
2a. If the remote PR head is strictly ahead of the candidate -- a push landed
   while this gate was waiting -- discard any approval and return to
   MAIN_HEAD_REFRESH, so nobody is asked to approve a SHA that GitHub would
   not merge
3. Load the approval record; if absent, hold and report the candidate SHA that
   requires approval
4. Verify the approval source:
     github_review  -> GitHub's required human/third-party approval policy;
                       CodeRabbit is informational and not a repository gate.
     explicit_human -> a formal approval source created by `merge.sh approve`:
                       attribute to the authenticated operator identity and
                       bind to both the PR HEAD SHA and the main HEAD SHA.
5. Verify approved_commit_sha matches the current merge candidate
6. Verify main_head_sha matches current main HEAD
7. Reject unknown approval sources, missing identity/timestamp, and stale
   or malformed records (fail closed)
8. If approval is invalid, require a fresh explicit approval
```

**No valid approval = No merge.**

In the normal case the operator is asked **once**, for the SHA that is merged.

### 7. Explicit Human Approval

For solo/personal development where a GitHub third-party approval is not
available, an **Explicit Human Approval** can be recorded as a first-class,
auditable approval source. It is **not** a fake GitHub APPROVED review and
never uses `--admin`, unconditional force push, or protection bypass.

```sh
# Advance the flow until it reports the candidate awaiting approval
merge.sh merge --pr <number> --worktree <path>

# Record an explicit human approval bound to that candidate and to main HEAD
merge.sh approve --pr <number> --worktree <path> [--main-dir <path>]

# Resume; the recorded approval is validated against the same candidate
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

### 8. Final HEAD Revalidation

Immediately before the irreversible merge, re-verify that nothing moved between
approval and now:

```text
1. An approved commit SHA is recorded, and the approval record binds to it
2. The local PR HEAD equals the approved SHA
3. The PR HEAD on GitHub equals the approved SHA
4. Main HEAD still equals the main HEAD the candidate was rebased onto
5. PR is open, not a draft, mergeable, and its required checks and review gate
   still pass
```

Any divergence stops the merge, **fail closed**:

| Divergence | Result |
|------------|--------|
| Main HEAD advanced | Approval discarded, return to `MAIN_HEAD_REFRESH` for another mandatory rebase |
| Remote PR head is not the candidate (moved, or undeterminable) | Approval discarded, return to `MAIN_HEAD_REFRESH` to **rebuild the candidate** from the remote PR head |
| Anything else (local and remote agree; only the approval record is at fault) | Approval discarded, return to `APPROVAL_VALIDATION` for a fresh approval |

The merge never proceeds on the same invocation as a detected divergence.

The middle row matters for termination, not only safety. A moved remote PR head
means the **candidate itself changed**, so returning to `APPROVAL_VALIDATION`
would offer the unchanged local HEAD again, and this same revalidation would
reject it again — a merge that can never happen and an approval requested for a
SHA that can never merge. Routing through the mandatory rebase re-synchronises
the candidate onto the remote PR head, so the flow converges instead.

### 9. Normal Merge

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

### 10. Post-Merge Verification

Verify the merge on GitHub:

```text
1. Confirm PR state is MERGED
2. Verify merge commit exists on main
3. Record final state
```

### 11. Cleanup

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
MAIN_HEAD_REFRESH
REBASE
CONFLICT
VALIDATING
APPROVAL_VALIDATION
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
MAIN_HEAD_REFRESH  ←───────────────┐
    ↓                              │
REBASE                             │ main advanced:
    ├─ conflict → CONFLICT → (return to caller)
    ├─ failure  → FAILED       │ candidate is stale,
    └─ clean                    │ approval discarded
         ↓                         │
VALIDATING  (CI + review gates on the final candidate)
    ↓
APPROVAL_VALIDATION  (SHA-bound human approval on that candidate)
    ├─ no/invalid approval → hold here
    ├─ candidate not rebased onto live main ───────┘
    └─ valid → MERGING
                 ├─ final HEAD revalidation fails, main moved → MAIN_HEAD_REFRESH
                 ├─ final HEAD revalidation fails otherwise  → APPROVAL_VALIDATION
                 └─ passes → MERGED → CLEANUP → COMPLETED

A clean rebase always continues to `VALIDATING`, whether or not it changed the
HEAD; a changed HEAD additionally discards any pre-rebase approval, and the safe
rebase push must complete before validation is reached. Approval is requested
only at `APPROVAL_VALIDATION`, and only for a candidate proven rebased onto the
live main HEAD, so the normal case needs exactly one approval.
```

### Transition Rules

| From | To | Condition |
|------|----|-----------|
| TRIGGER_CHECK | MAIN_HEAD_REFRESH | Preconditions met |
| TRIGGER_CHECK | FAILED | Preconditions not met |
| MAIN_HEAD_REFRESH | REBASE | Main HEAD fetched |
| MAIN_HEAD_REFRESH | FAILED | Main HEAD could not be resolved |
| REBASE | VALIDATING | Rebase clean (any pre-rebase approval discarded if the HEAD changed) |
| REBASE | CONFLICT | Rebase conflicts |
| REBASE | FAILED | Rebase failed without conflicts, or no recorded rebase base |
| VALIDATING | APPROVAL_VALIDATION | Required checks and review gate pass for the final candidate |
| VALIDATING | FAILED | Validation failed |
| APPROVAL_VALIDATION | MERGING | Approval valid and bound to the final candidate |
| APPROVAL_VALIDATION | MAIN_HEAD_REFRESH | Candidate not proven rebased onto live main; approval discarded |
| APPROVAL_VALIDATION | FAILED | Unrecoverable precondition failure |
| MERGING | MERGED | Final HEAD revalidation passed and merge succeeded |
| MERGING | APPROVAL_VALIDATION | Final HEAD revalidation failed; fresh approval required |
| MERGING | MAIN_HEAD_REFRESH | Main HEAD advanced after approval; re-rebase required |
| MERGING | FAILED | Merge failed |
| MERGED | CLEANUP | Verification passed |
| CLEANUP | COMPLETED | Cleanup succeeded |
| CLEANUP | FAILED | Cleanup failed |

## Approval Model

### Approval Sources

Two approval sources are supported, each validated separately:

| Source | Validation |
|--------|------------|
| `github_review` | Existing GitHub approval required by repository policy. CodeRabbit findings are reviewed when present but do not replace approval. |
| `explicit_human` | Formal solo-dev approval created by `merge.sh approve`. Verified by authenticated operator identity, PR HEAD SHA binding, and main HEAD SHA binding. |

The `Approval` object in state carries `"ApprovalSource"`. An absent source is
treated as the legacy `github_review` default; any present-but-unknown value
fails closed. Validation results from one source are never reused for the
other source.

Whatever the source, the approval is only ever evaluated against the **final
merge candidate**: the post-rebase HEAD, validated against a main HEAD that has
not moved since the rebase. An approval that predates the mandatory rebase is
discarded rather than carried forward.

### Approval Record

| Field | Description |
|-------|-------------|
| `pr_number` | The PR number |
| `issue_number` | The Issue number |
| `commit_sha` / `approved_commit` | The commit SHA being approved (PR HEAD) |
| `main_head_sha` / `approved_main_head` | The main HEAD SHA at approval time |
| `rebased_onto_main_sha` | The main HEAD the candidate was rebased onto (state field; absent means "not proven rebased" and fails closed into another rebase) |
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
- PR content changed, locally or on GitHub (new commit, bot commit, push,
  force push)
- Main HEAD advanced after the rebase the candidate is bound to
- Artifact changes

### Validation

To validate an approval:

```text
1. Check is_valid flag
2. Compare approved_commit_sha with current commit
3. Compare approved_main_head_sha with current main HEAD
4. Confirm the candidate is proven rebased onto the current main HEAD
```

**All must match for approval to be valid.**

The same comparison is repeated at the Final HEAD Revalidation step immediately
before the merge, against the PR HEAD as GitHub reports it, so a push that lands
between approval and merge cannot be merged.

### CodeRabbit review

CodeRabbit is a best-effort automated reviewer. Missing, skipped, pending, unavailable, or rate-limited reviews do not block Merge Skill validation. Confirmed unresolved major findings may be considered during human review; repository-owned CI and explicit human approval remain mandatory.

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
| `git push --force` / `-f` | Unconditional history rewrite; prohibited |
| Plain `--force-with-lease` | Lease is not explicit; prohibited |
| Direct push | Bypasses PR process |
| API merge with bypass | Circumvents protections |

The only force-update exception is the runtime's
`merge_safe_rebase_push` for a mandatory-rebased PR feature branch. It
requires an explicit old remote SHA lease and post-push SHA verification.
Main and protected base branches remain prohibited.

## Batch Invocation

When called from Batch Skill:

```text
Batch Skill
    ↓
Merge Skill (PR #149)
    ↓
Preconditions check
    ↓
Main HEAD refresh
    ↓
Mandatory rebase
    ↓
Validation (CI + review gates)
    ↓
Final SHA-bound human approval
    ↓
Final HEAD revalidation
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
Main HEAD refresh
    ↓
Mandatory rebase
    ↓
Validation (CI + review gates)
    ↓
Final SHA-bound human approval
    ↓
Final HEAD revalidation
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
| `rebased_onto_main_sha` | Main HEAD the current candidate was rebased onto |
| `created_at` | Creation timestamp |
| `updated_at` | Last update timestamp |

### Recovery Process

To resume a stopped process:

```text
1. Load persisted state
2. Add any state fields introduced since the file was written (missing fields
   are inserted as null)
3. Verify PR exists and is open
4. Check current state
5. Resume from last known state
```

A state file written before the ordering fix can be persisted at
`APPROVAL_VALIDATION` *before* any rebase. It carries no `rebased_onto_main_sha`,
which is read as "not proven rebased": the resumed flow returns to
`MAIN_HEAD_REFRESH` and runs the mandatory rebase rather than merging. This
fails closed, and the rebase is idempotent.

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
exactly one step (`TRIGGER_CHECK → MAIN_HEAD_REFRESH → REBASE →
CONFLICT/VALIDATING → APPROVAL_VALIDATION → MERGING → MERGED → CLEANUP →
COMPLETED`) and persists its state, so it is safe to re-run and can be resumed
after an interruption.

```sh
# Advance the merge for PR 149 (resumes from the current persisted state)
runtime/merge.sh merge --pr 149

# Record an explicit human approval for PR 149. Run this once the flow reports
# the final merge candidate awaiting approval, so it binds to the merged SHA.
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

- **1.4.0** — Approval / rebase ordering (Issue #247):
  - The SHA-bound human approval gate now runs **after** the mandatory rebase and
    the CI/review gates, on the final merge candidate. Previously
    `APPROVAL_VALIDATION` preceded `MAIN_HEAD_REFRESH`/`REBASE`, so a PR needing
    a rebase could not reach the rebase without an approval, while any approval
    given was then invalidated by that same rebase — an unavoidable double
    approval. Approval is now requested once, for the SHA that is merged.
  - New ordering: `TRIGGER_CHECK → MAIN_HEAD_REFRESH → REBASE → VALIDATING →
    APPROVAL_VALIDATION → MERGING → MERGED → CLEANUP → COMPLETED`.
  - Added a Final HEAD Revalidation immediately before the merge: the approved
    SHA must still equal the local PR HEAD **and** the PR HEAD GitHub reports,
    main must not have moved since the rebase, and the PR must still be open,
    non-draft, mergeable and passing its gates. Any divergence discards the
    approval and returns to the approval gate, or to another mandatory rebase
    when main moved. The merge never proceeds on that invocation.
  - Added the `rebased_onto_main_sha` state field. An approval is only evaluated
    for a candidate proven rebased onto the live main HEAD; an absent marker is
    read as "not proven rebased" and fails closed into another rebase. State
    files written by earlier versions are migrated on load, so a legacy state
    persisted at `APPROVAL_VALIDATION` re-runs the mandatory rebase instead of
    merging.
  - The mandatory rebase now starts from the commit GitHub would merge: when the
    remote PR head has moved ahead, the worktree is fast-forwarded onto it
    first. Fast-forward only — no merge commit, no history rewrite, no push —
    and a diverged or undeterminable remote head fails closed.
  - A remote PR head that is not the current candidate routes the flow back
    through the mandatory rebase rather than to a fresh approval, so remote-only
    drift converges onto the new candidate instead of repeatedly requesting
    approval for a stale local HEAD that can never merge. The approval gate
    applies the same rule before prompting, so a push that lands while it waits
    never results in an approval request for a superseded SHA.
  - Rebase safety preconditions are unchanged: PR head/base validation, the
    explicit remote SHA lease, and post-push SHA verification all still run
    before the approval gate is reached.
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
