---
name: batch-orchestrator
description: >
  Parallel Issue execution Orchestrator for Batch Skill.
  Manages concurrent Sub-agents with dependency scheduling,
  retry, failure isolation, and serial merge via Merge Skill.
version: 1.0.0
scope: process
platform: agent-agnostic
related-issues: "#155"
---

# Batch Orchestrator Skill

A parallel Issue execution Orchestrator that safely processes multiple
independent Issues via concurrent Sub-agents, with dependency-aware
scheduling and serial merge through the Merge Skill.

## Core Principles

1. **Safety over speed**: Never compromise existing Merge/Batch safety
2. **Parallel execution, serial merge**: Sub-agents run in parallel, merges are serialized
3. **Sub-agent integrity**: Orchestrator never substitutes implementation
4. **Failure isolation**: One Sub-agent failure does not block unrelated Issues
5. **Dependency respect**: DAG-based scheduling with cycle detection
6. **Resume capable**: Crash recovery through persisted state
7. **Admin bypass forbidden**: Never use `--admin`, force push, or protection circumvention

## Architecture

```text
Orchestrator
    ↓
Scheduler (concurrency limit)
    ↓
Sub-agent (per Issue)
    ↓
Worktree / Branch (independent)
    ↓
PR
    ↓
Merge Skill (serial)
    ↓
main
```

## Batch State Machine

### States

```text
BATCH_INITIALIZING
PLANNING
SCHEDULING
RUNNING
WAITING_FOR_MERGE
MERGING
CLEANUP
COMPLETED
FAILED
```

### Transitions

```text
BATCH_INITIALIZING → PLANNING → SCHEDULING → RUNNING
    ↓                                                    ↓
    └→ FAILED                              WAITING_FOR_MERGE → MERGING → CLEANUP → COMPLETED
                                              ↓                                        ↓
                                           COMPLETED                               FAILED
                                              ↓
                                           FAILED
```

## Issue State Machine

### States

```text
SUBAGENT_STARTING
SUBAGENT_RUNNING
SUBAGENT_RETRYING
SUBAGENT_FAILED
WAITING_FOR_SUBAGENT
WAITING_DEPENDENCY
PR_READY
WAITING_FOR_APPROVAL
READY_FOR_MERGE
MERGING
COMPLETED
BLOCKED
FAILED
```

## Dependency Management

### DAG Construction

Issues are modeled as a Directed Acyclic Graph (DAG):

```text
Issue A (no deps) ─┐
Issue B (depends on A) ─┤→ Parallel waves
Issue C (no deps) ─┘
```

### Cycle Detection

Cycles are detected before execution begins. If a cycle is found:
- Batch stops immediately
- Cycle path is reported
- User must resolve dependency conflict

### Concurrency Groups

Issues are grouped into waves based on dependency completion:

```text
Wave 1: [A, C] (no dependencies)
Wave 2: [B] (depends on A)
Wave 3: [D] (depends on B and C)
```

## Parallel Execution

### Concurrency Limit

```text
max_parallel_subagents: 3 (configurable)
```

### Dispatch Rules

1. Check dependency completion
2. Check concurrency slot availability
3. Create Worktree and Branch
4. Initialize environment
5. Launch Sub-agent
6. Monitor completion

### Failure Isolation

```text
Issue A → SUCCESS → continues
Issue B → BLOCKED → does not affect A or C
Issue C → RUNNING → continues
Issue D → depends on B → BLOCKED
```

## Sub-agent Responsibilities

Each Sub-agent MUST:

1. Investigate the Issue
2. Research repository architecture
3. Check Architecture / SSOT
4. Design implementation
5. Implement changes
6. Run tests
7. Verify results
8. Create structured report
9. Create PR

### Required Report Fields

| Field | Description |
|-------|-------------|
| InvestigationSummary | What was investigated |
| ImplementationSummary | What was implemented |
| DesignDecision | Why this design was chosen |
| ChangedFiles | List of modified files |
| TestResults | Test execution results |
| PrNumber | GitHub PR number |
| CommitSha | Commit SHA |

## Retry Mechanism

### Retryable Errors

- API connection errors
- Sub-agent startup failures
- Timeouts
- Transient failures

### Non-Retryable Errors

- Code compilation errors
- Test failures
- Architecture violations
- Dependency conflicts

### Backoff

```text
backoff = base_seconds * 2^retry_count + random_jitter
```

### Retry Flow

```text
Sub-agent fails
    ↓
Categorize error
    ↓
Retryable?
├─ YES → Wait backoff → Retry
│         ↓
│       Success → Continue
│
└─ NO / Retry limit → BLOCKED → User notification
```

## Merge Control

### Serial Merge

PRs are merged one at a time through the Merge Skill:

```text
PR A → Rebase → Approval → Validation → Merge → main updates
    ↓
PR B → Rebase (onto new main) → Approval → Validation → Merge
    ↓
PR C → Same process
```

### Merge Skill Integration

The Batch Orchestrator NEVER merges directly. It delegates to the Merge Skill which enforces:

- Mandatory rebase onto latest main HEAD
- SHA-bound approval validation
- Standard merge only (no `--admin`)
- Conflict delegation to Sub-agent

## Approval Management

### Per-Issue Independence

Each Issue has independent approval tracked by commit SHA.

### Invalidation Conditions

- Rebase changed content
- Commit changed
- PR content changed
- Force push detected
- HEAD SHA changed

## Resume / Crash Recovery

### Persisted State

| File | Content |
|------|---------|
| `.batch-state-{id}.json` | Batch-level state |
| `.batch-issues-{id}.json` | Per-Issue states |

### Recovery Process

1. Load persisted state
2. Sync with GitHub reality
3. Resume from last known state
4. Continue processing

## Cleanup

After merge confirmation:

1. Delete Worktree
2. Delete local Branch
3. Delete remote Branch
4. Prune stale references
5. Mark Issue as COMPLETED

## Configuration

Project-specific configuration in `config/batch-config.json`.

## Runtime

```text
runtime/
├── README.md
└── powershell/
    ├── Modules/
    │   ├── BatchStateMachine.psm1
    │   ├── IssueStateMachine.psm1
    │   ├── DependencyGraph.psm1
    │   ├── BatchScheduler.psm1
    │   ├── BatchSubAgent.psm1
    │   ├── BatchGitUtilities.psm1
    │   ├── BatchMergeQueue.psm1
    │   └── BatchPersistence.psm1
    ├── Scripts/
    │   └── Invoke-BatchOrchestrator.ps1
    └── Tests/
        └── Test-BatchSkill.ps1
```

## Usage

```powershell
# Run batch with issues file
.\wrapper\batch.ps1 run -BatchId my-batch -IssuesFile issues.json

# Run with inline issue IDs
.\wrapper\batch.ps1 run -BatchId my-batch -IssueIds @("140","141","142")

# Check status
.\wrapper\batch.ps1 status -BatchId my-batch

# Resume after crash
.\wrapper\batch.ps1 resume -BatchId my-batch

# Run tests
.\wrapper\batch.ps1 test
```

## Non-goals

- Replacing human judgment on merge timing
- Automatic merge without user approval
- Orchestrator implementing code for Sub-agents
- Parallel merge to main
- Admin bypass or protection circumvention
- Ignoring dependency ordering
