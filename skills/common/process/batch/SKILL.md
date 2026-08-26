---
name: batch-orchestration
description: >
  Orchestrator/Sub-agent based batch processing for Issue-driven development.
  Manages parallel execution of multiple Issues with dedicated Worktrees,
  mandatory rebase before merge, and strict approval gates.
version: 2.0.0
scope: process
platform: agent-agnostic
related-issues: "#145"
---

# Batch Skill: Orchestrator/Sub-agent Orchestration

A standardized workflow for parallel Issue-driven development using multiple
Sub-agents with dedicated Git Worktrees. The Orchestrator manages lifecycle,
while Sub-agents handle investigation, implementation, testing, and PR creation.

This document defines the **specification only**. The actual implementation
lives in `runtime/` (see [Runtime](#runtime) section).

## Core Principles

1. **1 Issue = 1 Sub-agent = 1 Branch = 1 Worktree**
2. **Mandatory rebase before every merge** (not just conflict resolution)
3. **User approval as required merge gate**
4. **Conflict resolution delegated to Sub-agent**
5. **Explicit state machine with resumability**
6. **Strict merge conditions and mandatory cleanup**

## Responsibilities

### Orchestrator

The Orchestrator manages the lifecycle of Issue-driven development:

| Responsibility | Description |
|---------------|-------------|
| Issue Assignment | Assign Issues to Sub-agents |
| Branch/Worktree Management | Create and delete Branches and Worktrees |
| Environment Initialization | Initialize Worktree environments with required files |
| PR Monitoring | Track Pull Request status |
| Approval Gate | Enforce user approval before merge |
| Rebase Execution | Perform mandatory rebase onto latest main HEAD |
| Final Validation | Validate changes before merge |
| Merge Execution | Merge approved changes into main |
| Cleanup | Remove Worktrees and Branches after merge |
| Reporting | Present Sub-agent results to users |

### Sub-agent

Sub-agents handle the actual implementation work:

| Responsibility | Description |
|---------------|-------------|
| Investigation | Analyze the assigned Issue |
| Implementation | Make changes in dedicated Worktree |
| Testing | Run tests and validation |
| Reporting | Create comprehensive implementation reports |
| PR Creation | Create Pull Request with detailed description |
| Conflict Resolution | Resolve merge conflicts when assigned |
| Re-verification | Re-test after conflict resolution |
| PR Update | Update PR with resolution details |

## State Machine

### States

The Batch Skill uses the following states:

```text
INVESTIGATING
IMPLEMENTING
REPORTING
PR_OPEN
AWAITING_APPROVAL
REBASE
CONFLICT_RESOLUTION
PR_UPDATED
VALIDATING
MERGING
CLEANUP
COMPLETED
```

### Transitions

```text
INVESTIGATING
    ↓
IMPLEMENTING
    ↓
REPORTING
    ↓
PR_OPEN
    ↓
AWAITING_APPROVAL
    ↓
REBASE
    ├─ conflict → CONFLICT_RESOLUTION
    │                 ↓
    │              REPORTING
    │                 ↓
    │              PR_UPDATED
    │                 ↓
    │              AWAITING_APPROVAL
    │
    └─ clean → VALIDATING
                   ↓
                 MERGING
                   ↓
               CLEANUP
                   ↓
                COMPLETED
```

### Transition Rules

| From | To | Condition |
|------|----|-----------|
| INVESTIGATING | IMPLEMENTING | Worktree created |
| IMPLEMENTING | REPORTING | Implementation complete |
| REPORTING | PR_OPEN | Report created |
| PR_OPEN | AWAITING_APPROVAL | PR created |
| AWAITING_APPROVAL | REBASE | User approved |
| REBASE | VALIDATING | Rebase successful, no conflicts |
| REBASE | CONFLICT_RESOLUTION | Rebase failed with conflicts |
| CONFLICT_RESOLUTION | REPORTING | Conflicts resolved |
| VALIDATING | MERGING | Validation passed |
| MERGING | CLEANUP | Merge successful |
| CLEANUP | COMPLETED | Cleanup complete |

## Git Operations

### Branch Naming

Branch names must follow the pattern:

```text
<type>/<issue-number>-<short-description>
```

Example:
```text
issue/145-batch-orchestration
```

### Worktree Naming

Worktree paths must follow the pattern:

```text
<worktree-root>/<issue-number>-<short-description>
```

Example:
```text
../worktrees/145-batch-orchestration
```

### Required Git Operations

The Runtime must support these Git operations:

| Operation | Description |
|-----------|-------------|
| `worktree add` | Create a new Worktree with a new Branch |
| `worktree remove` | Remove a Worktree |
| `worktree prune` | Clean up stale Worktree references |
| `branch create` | Create a new Branch |
| `branch delete` | Delete a local Branch |
| `fetch` | Fetch latest remote state |
| `rebase` | Rebase onto a target ref |
| `rebase --abort` | Abort an in-progress rebase |
| `status` | Check working tree status |
| `diff` | Show changes |
| `diff --diff-filter=U` | List conflicting files |
| `merge` | Merge a Branch |
| `push` | Push to remote |
| `rev-parse` | Get commit SHA |

### Rebase Flow

The rebase flow is mandatory before every merge:

```text
User Approval
    ↓
Fetch Latest Main HEAD
    ↓
Rebase onto Main
    ↓
├─ Success → Validation → Merge
    │
    └─ Conflict → Delegate to Sub-agent
                    ↓
                 Resolve Conflicts
                    ↓
                 Re-test
                    ↓
                 Update PR
                    ↓
                 Re-approval Required
                    ↓
                 Rebase again (loop)
```

**Important:** Rebase is always performed, even without conflicts.

## Approval System

### Approval Record

An approval record must contain:

| Field | Description |
|-------|-------------|
| `issue_number` | The Issue number |
| `commit_sha` | The commit SHA being approved |
| `main_head_sha` | The main HEAD SHA at approval time |
| `approved_by` | Who approved |
| `approved_at` | When approved (ISO 8601) |
| `is_valid` | Whether approval is still valid |
| `notes` | Optional notes |

### Approval Invalidation

An approval is invalidated when:

- Rebase changed content
- Conflict resolution changed code
- Tests affected by changes
- Substantial artifact changes

### Validation

To validate an approval:

```text
1. Check is_valid flag
2. Compare approved commit_sha with current commit
3. Compare approved main_head_sha with current main HEAD
```

All must match for approval to be valid.

## Merge Conditions

All of the following must be satisfied before merge:

| Condition | Description |
|-----------|-------------|
| User approval | Latest artifact has user approval |
| Based on main HEAD | Branch is based on latest main HEAD |
| Rebase successful | Rebase completed without errors |
| No conflicts | No merge conflicts exist |
| Tests passed | Required tests/verification succeeded |
| PR mergeable | PR is in mergeable state |
| Report exists | Sub-agent explanation/verification report exists |

## Conflict Resolution

### Flow

When conflicts occur during rebase:

```text
1. Detect conflicts
2. Delegate to Sub-agent (same Branch/Worktree)
3. Sub-agent investigates cause
4. Sub-agent resolves conflicts
5. Sub-agent re-tests
6. Sub-agent creates Conflict Resolution Report
7. Sub-agent updates PR
8. User re-approves
9. Rebase onto latest main HEAD
10. Repeat until clean
```

### Conflict Resolution Report

Sub-agents must create a report containing:

| Section | Description |
|---------|-------------|
| Conflict Resolution | What conflicts were found |
| Cause | Why conflicts occurred |
| Resolution | How conflicts were resolved |
| Verification | What was verified after resolution |

## Cleanup Process

After merge confirmation:

```text
1. Confirm PR merged
2. Verify merge status
3. Delete Worktree
4. Delete local Branch
5. Delete remote Branch
6. Verify no remaining references
7. Mark COMPLETED
```

**Important:** Never perform destructive cleanup if merge status is uncertain.

## Resumability

### Persisted State

The Runtime must persist:

| Field | Description |
|-------|-------------|
| `issue_number` | The Issue number |
| `pr_number` | The PR number |
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
2. Verify Worktree exists
3. Verify Branch exists
4. Check PR status
5. Resume from last known state
```

## Environment Initialization

After Worktree creation and before Sub-agent startup:

### Required Operations

1. Copy `.env.example` to `.env` if present
2. Copy other required non-git files
3. Verify file permissions
4. Confirm initialization success

### Restrictions

- Never copy files containing secrets indiscriminately
- Never add `.env` or similar to Git
- Use `.env.example` as source when available
- Track copy source, target, and generation method explicitly

### Failure Handling

- If initialization fails, do not start Sub-agent
- Report failure to Orchestrator
- Keep Worktree for investigation

## Sub-agent Reports

### Implementation Report

Sub-agents must create a report with:

| Section | Description |
|---------|-------------|
| Summary | What was implemented |
| Investigation | What was investigated |
| Design Decision | Why this design/implementation was chosen |
| Changes | What was specifically changed |
| Tests | What was executed and results |
| Risks / Limitations | Remaining issues and constraints |
| Related Issues | Related Issues / PRs |
| Verification | Evidence that Issue requirements are met |

### Conflict Resolution Report

When resolving conflicts:

| Section | Description |
|---------|-------------|
| Conflict Resolution | What conflicts were found |
| Cause | Why conflicts occurred |
| Resolution | How conflicts were resolved |
| Verification | What was verified after resolution |

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
| PowerShell Core 7.x | Implemented | Windows, Linux, macOS |

### Runtime Requirements

Any Runtime must:

1. Support all Git operations listed in [Required Git Operations](#required-git-operations)
2. Implement the [State Machine](#state-machine) transitions
3. Persist state for [Resumability](#resumability)
4. Handle [Environment Initialization](#environment-initialization)
5. Create [Sub-agent Reports](#sub-agent-reports)
6. Run on target platforms (see Runtime documentation)

## Configuration

Project-specific configuration is externalized in `config/`:

```text
config/
└── batch-config.json       # Project configuration
```

See `config/batch-config.json` for the configuration schema.

## Porting to Another Project

To use this Skill in another project:

1. Copy the `skills/common/process/batch/` directory
2. Update `config/batch-config.json` with project-specific settings
3. Ensure the required Runtime is available (see Runtime documentation)
4. No changes to SKILL.md required

## Non-goals

- Replacing human judgment on merge timing
- Automatic merge without user approval
- Conflict resolution by Orchestrator
- Skipping rebase for "simple" changes
- Compromising main branch history
