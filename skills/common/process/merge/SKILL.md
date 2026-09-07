---
name: pr-merge
description: >
  Safe PR Merge Skill for Batch and Standalone use.
  Enforces mandatory rebase → validation → final SHA-bound approval → squash
  merge → post-merge verification flow.
  Standard merge method is Squash and merge; merge commit / rebase merge only
  on an explicit exception.
  Prevents admin bypass and protection rule circumvention.
  Cross-platform: POSIX shell (default) and PowerShell implementations.
version: 1.6.0
scope: process
platform: agent-agnostic
related-issues: "#146, #176, #247, #260, #265, #270"
---

# PR Merge Skill

A safe, standalone PR Merge Skill that enforces a strict merge flow:
**Latest Main HEAD Rebase → Validation → Final SHA-bound Human Approval →
Squash and Merge → Post-Merge Verification**.

The human approval gate runs **after** the mandatory rebase, so the approval
binds to the exact commit that will be merged. Requesting it earlier would bind
it to a SHA the mandatory rebase is already known to discard, forcing a second
approval round (Issue #247).

The merge itself uses **Squash and merge** (Issue #265). Squashing creates a
**new** commit on `main`; its SHA is not the PR HEAD SHA, and the two are kept
strictly apart throughout this Skill — see [SHA semantics](#sha-semantics).

This Skill prevents admin bypass and protection rule circumvention,
ensuring that merges only happen through the standard GitHub merge path.

## Core Principles

1. **Safety over speed**: Never merge if conditions are not met
2. **No admin bypass**: Never use `--admin`, unconditional force push, or protection circumvention
3. **Mandatory rebase**: Always rebase onto latest main HEAD before merge
4. **Approval as final gate**: User approval is required and tied to the SHA of
   the final merge candidate, i.e. the post-rebase PR HEAD that will be merged
5. **Squash and merge by default**: The standard merge method is
   `gh pr merge <pr-number> --squash`. A merge commit or a rebase merge is
   performed **only** on an explicit exception (see
   [Merge Strategy](#merge-strategy))
6. **Never conflate PR HEAD with the squash commit**: Approval binds to the
   final PR HEAD; the commit squashing creates on `main` is a different,
   later-existing SHA
7. **Conflict delegation**: Conflicts are delegated back to Sub-agent, not auto-resolved
8. **Standalone and Batch compatible**: Same safety conditions regardless of invocation

## SHA semantics

Five distinct SHAs appear in this flow. They are **never** treated as
interchangeable, and each is reported under its own name:

| Concept | What it is |
|---|---|
| **pre-rebase HEAD** | The PR HEAD before the mandatory rebase runs. Discarded as a merge candidate. |
| **post-rebase PR HEAD** | The candidate the mandatory rebase produced, rebased onto the current main HEAD. |
| **approved PR HEAD** | The SHA a human approval is bound to (`approved_commit_sha`). |
| **final PR HEAD** | The candidate actually merged. Normally equal to the approved PR HEAD; it differs only when a divergence forced a fresh approval (see [Final HEAD Revalidation](#8-final-head-revalidation)). |
| **squash commit SHA** | The **new** commit Squash and merge creates on `main` (`main_commit_sha`). It does not exist until after the merge and is never equal to any PR HEAD above. |

Consequences that the flow depends on:

- Approval binds to the **final PR HEAD** — never to the squash commit SHA,
  which cannot exist at approval time.
- After a squash merge, `PR HEAD SHA != squash commit SHA` is the **expected,
  normal, passing** result. It is never reported as an anomaly or a failure.
- Any check, record, or report that would assert
  `PR HEAD SHA == merged commit SHA` is invalid under this Skill.

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
4. Verify the automated-review evidence for this HEAD satisfies
   REVIEW_PROVIDER_POLICY.md (established provider state, findings resolved,
   and — on the fallback path — a recorded independent current-HEAD review)
5. Record validation result
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
                       CodeRabbit is not an approval source. What its review
                       evidence means for this candidate is decided earlier, in
                       VALIDATING, per REVIEW_PROVIDER_POLICY.md.
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

### 9. Squash Merge

Execute the merge through the standard GitHub path, using Squash and merge:

```text
1. gh pr merge <pr-number> --squash
2. Verify the merge command succeeded
3. The merged candidate is the final PR HEAD (the approved SHA). The commit
   this creates on main is a NEW squash commit whose SHA is read back in the
   next step -- it does not exist yet and is never assumed here.
```

A merge commit (`--merge`) or a rebase merge (`--rebase`) is used here only
when explicitly requested as an exception; see
[Merge Strategy](#merge-strategy).

**NEVER use:**
- `gh pr merge --admin`
- Force push
- Direct push to main
- Protection rule bypass

### 10. Post-Merge Verification

Verify the merge on GitHub:

```text
1. Confirm PR state is MERGED
2. Confirm main HEAD advanced past the main HEAD the candidate was rebased onto
3. Read the commit the merge created on main and record it as the squash commit
   SHA (main_commit_sha) -- a field distinct from every PR HEAD field
4. Record the final PR HEAD alongside it
5. Confirm the driving Issue closed when a closing keyword was expected
6. Record final state
```

Expected results:

| Observation | Verdict |
|---|---|
| Squash commit SHA differs from the final PR HEAD SHA | **PASS** — this is the normal result of a squash merge |
| Squash commit SHA equals a PR HEAD SHA | Not required, not expected; never asserted as a precondition |
| Squash commit SHA cannot yet be read, or main has not advanced | Hold in `MERGED` and retry — never treated as merged-and-verified |
| PR state is not `MERGED` | `FAILED` |

GitHub's closing keywords (`Closes #n` / `Fixes #n` in the PR body) work
identically for a squash merge, so Issue closure is verified exactly as before.

Post-merge verification **holds** rather than failing terminally when its
evidence is incomplete: the merge is already irreversible, and a terminal
failure would strand the worktree and branch before [Cleanup](#11-cleanup).

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
merge candidate**: the post-rebase PR HEAD, validated against a main HEAD that
has not moved since the rebase. An approval that predates the mandatory rebase
is discarded rather than carried forward.

An approval is **never** bound to the squash commit SHA. That commit is created
by the merge, so it does not exist while approval is being granted or
validated; binding to it would be impossible, and treating the two as one SHA
would break the approval gate. See [SHA semantics](#sha-semantics).

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

Every comparison above is between **PR HEAD SHAs** (and main HEAD SHAs). None of
them involves the squash commit SHA, which does not exist yet.

The same comparison is repeated at the Final HEAD Revalidation step immediately
before the merge, against the PR HEAD as GitHub reports it, so a push that lands
between approval and merge cannot be merged.

### Automated review provider

[`REVIEW_PROVIDER_POLICY.md`](REVIEW_PROVIDER_POLICY.md) is the **single source
of truth** for automated-review provider states, fallback eligibility, and the
review evidence the merge gate requires. This Skill consumes it; it does not
restate or relax it, and no other skill defines provider semantics of its own.

In summary — the policy is authoritative on the detail:

- CodeRabbit is the **preferred** automated reviewer, not a single-provider
  mandatory gate; provider capacity never becomes the only path to merge.
- A provider failure or non-completion state (`CODERABBIT_RATE_LIMITED`,
  `CODERABBIT_UNAVAILABLE`, `CODERABBIT_SKIPPED`, `CODERABBIT_PENDING`,
  `CODERABBIT_UNKNOWN`) is **never** a review pass and is never reported as one.
- When a provider failure state is established from provider-side evidence, the
  fallback path supplies review evidence instead of waiting on the provider. It
  is fail-closed: green current-HEAD CI, zero unresolved actionable findings, a
  recorded `FALLBACK_REVIEW_PASS` from an **independent** current-HEAD review
  (a reviewing context separate from the author; the pre-PR self review does not
  qualify), an audit note, and the final SHA-bound human approval.
- Actual CodeRabbit findings are never waived by provider availability: any
  unresolved actionable finding blocks the merge.

Repository-owned CI and explicit human approval remain mandatory in every path.

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

### Standard: Squash and merge

The standard merge method is **Squash and merge**:

```text
gh pr merge <pr-number> --squash
```

It is the configured default (`config/merge-config.json` → `merge.strategy`)
and what the runtime executes when no exception is requested. Missing or
malformed configuration fails closed to `--squash` rather than reintroducing a
merge commit.

Squashing produces a **new** commit on `main`. Its SHA is recorded separately
from the PR HEAD; see [SHA semantics](#sha-semantics).

### Exception: merge commit or rebase merge

`--merge` (merge commit) and `--rebase` (rebase merge) are **not** used on the
standard path. They are performed only when the operator names the method
explicitly on the invocation that performs the merge:

```sh
# Explicit exception - merge commit instead of a squash merge
runtime/merge.sh merge --pr <number> --merge-method --merge

# Explicit exception - rebase merge
runtime/merge.sh merge --pr <number> --merge-method --rebase
```

Properties of the exception:

- **Explicit per invocation**: it is never persisted to the merge state, so a
  resumed or re-run merge returns to Squash and merge rather than silently
  inheriting the exception.
- **Recorded**: the merge step names the method it used in its output, so the
  exception is visible in the run record.
- **Fail closed**: an unrecognized method is rejected; it never falls through
  to `gh` and never silently becomes something else.
- Every other gate is unchanged: the mandatory rebase, current-HEAD validation,
  SHA-bound approval, and final HEAD revalidation all still apply.

### What is NOT allowed

| Method | Reason |
|--------|--------|
| `gh pr merge --admin` | Bypasses protection rules |
| `git push --force` / `-f` | Unconditional history rewrite; prohibited |
| Plain `--force-with-lease` | Lease is not explicit; prohibited |
| Direct push | Bypasses PR process |
| API merge with bypass | Circumvents protections |
| Merge commit / rebase merge **without** an explicit exception | The standard path is Squash and merge |

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
Squash merge
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
Squash merge
    ↓
Cleanup
```

## Cleanup Process

After merge confirmation:

```text
1. Confirm PR merged on GitHub
2. Verify the squash commit the merge created exists on main, recorded as
   main_commit_sha (a value distinct from the final PR HEAD)
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
| `main_commit_sha` | The commit the merge created on `main` (the squash commit). Written only during post-merge verification, and never equal to any PR HEAD field above |
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
# the final merge candidate awaiting approval, so it binds to the PR HEAD that
# is merged (never to the squash commit, which does not exist yet).
runtime/merge.sh approve --pr 149 --worktree ../worktrees/149-merge

# Full context for a batch-driven merge
runtime/merge.sh merge --pr 149 --issue 148 \
    --worktree ../worktrees/148-e2e-test --branch issue/148-e2e-test \
    --repo owner/repo

# Show current state only
runtime/merge.sh status --pr 149

# EXCEPTION ONLY: merge with a merge commit instead of Squash and merge.
# Not persisted, so a later resumed run returns to the standard squash method.
runtime/merge.sh merge --pr 149 --merge-method --merge

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

- **1.5.0** — Squash and merge standardization (Issue #265):
  - The standard merge method is now **Squash and merge**
    (`gh pr merge <pr-number> --squash`). Previously the Skill, the config
    (`merge.strategy`) and both runtimes hardcoded `--merge`, and `--squash`
    was listed as a forbidden method.
  - A merge commit (`--merge`) and a rebase merge (`--rebase`) are now
    exceptions, reachable only by naming the method explicitly on the
    invocation that performs the merge (`merge.sh merge --merge-method ...`).
    The override is not persisted, so a resumed run cannot inherit it, and an
    unrecognized method fails closed to the standard squash method.
  - Added an explicit [SHA semantics](#sha-semantics) contract separating five
    concepts that a squash merge makes genuinely distinct: pre-rebase HEAD,
    post-rebase PR HEAD, approved PR HEAD, final PR HEAD, and the squash commit
    SHA created on `main`. `PR HEAD SHA == merged commit SHA` is no longer a
    valid assumption anywhere in this Skill.
  - Post-merge verification now confirms the PR is `MERGED`, that `main`
    advanced past the rebase base, and reads back the commit the merge created
    on `main`, recording it as the new `main_commit_sha` state field. A PR HEAD
    that differs from that SHA is the expected, passing result — never an
    anomaly. Incomplete post-merge evidence holds in `MERGED` and retries
    instead of failing terminally, so cleanup is never stranded after an
    already-irreversible merge.
  - Reporting vocabulary de-conflated: the runtime's ambiguous `merge_commit`
    result key became `main_commit_sha`, which is emitted alongside a separate
    `pr_head_sha`; `merge_normal_merge` became `merge_execute_merge` (and
    `Invoke-NormalMerge` became `Invoke-MergePr`), since "normal merge" is
    GitHub's name for the merge-commit method this Skill no longer uses by
    default.
  - Approval safety is unchanged: mandatory rebase, current-HEAD validation
    after the rebase, SHA-bound human approval on the final PR HEAD, approval
    invalidation when the PR HEAD moves, and the admin-bypass / direct-push /
    unsafe-force-push prohibitions all still apply exactly as before.
- **1.6.0** — Fail-closed fallback review evidence (Issue #270):
  - [`REVIEW_PROVIDER_POLICY.md`](REVIEW_PROVIDER_POLICY.md) is named the single
    source of truth for provider states and fallback eligibility, and lists the
    skills that reference it as adapters. This Skill's former "CodeRabbit
    review" paragraph stated a looser rule than that policy (missing / skipped /
    pending / unavailable / rate-limited reviews "do not block validation"),
    which was a second, contradicting definition of provider semantics; it is
    replaced by a summary that defers to the policy.
  - The policy gained a parseable provider state vocabulary
    (`CODERABBIT_REVIEWED` / `CODERABBIT_RATE_LIMITED` /
    `CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED` / `CODERABBIT_PENDING` /
    `CODERABBIT_UNKNOWN`), each with the evidence required to establish it, and
    `CODERABBIT_UNKNOWN` as the fail-closed default. A provider failure state is
    never a review pass.
  - The fallback now additionally requires a recorded `FALLBACK_REVIEW_PASS`
    from an **independent** current-HEAD review — a reviewing context separate
    from the author, applying the Review Skill's viewpoints, bound to the merge
    candidate SHA. The author's pre-PR self review does not qualify.
    `FALLBACK_REVIEW_FAIL` and `FALLBACK_REVIEW_ABSENT` block.
  - `CODERABBIT_SKIPPED` became fallback-eligible, but only when the skip's
    cause is positively established (the provider reported the skip and its
    reason, or the PR matches a configured repository-side exclusion). An
    unexplained absent review stays `CODERABBIT_UNKNOWN` and remains blocked.
  - `VALIDATING` now explicitly checks the review-provider evidence for the
    final candidate. Required CI, the mandatory rebase, the admin-bypass /
    direct-push / unsafe-force-push prohibitions, the Squash and merge default
    (1.5.0), and the final SHA-bound human approval are unchanged.
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
