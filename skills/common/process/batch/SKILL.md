---
name: batch-orchestrator
description: >
  Parallel Issue execution Orchestrator for Batch Skill.
  Manages concurrent Sub-agents with dependency scheduling,
  retry, failure isolation, and serial merge via Merge Skill.
  Cross-platform: POSIX shell (default) and PowerShell implementations.
version: 2.0.0
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

### Primary Path (Shell / POSIX)

```text
batch.sh (entry point)
    ↓
Orchestrator (orchestrator.sh)
    ↓
Host Agent → Built-in Sub-agent / Task tool
    ↓
Task execution (worktree → commit → PR)
```

### Provider Selection Policy

The Agent Runtime Interface dispatches tasks through providers:

| Priority | Provider | Description | Required |
|----------|----------|-------------|----------|
| 1 | host-native | Current host agent's native sub-agent/task capability | Requires host capability |
| 2 | same-provider adapter | Existing supported mechanism for the current provider | Provider-specific |
| 3 | explicit adapter | External provider explicitly configured by the user/config | Explicit only |
| 4 | BLOCKED | No eligible execution mechanism | No worker launch |

The presence of `claude`, `opencode`, `codex`, or another CLI on `PATH` is not
configuration and never selects a provider. A provider switch is not a retry;
retries remain within the selected provider and mechanism.

### Host-Native Dispatch Contract

When the current host exposes a native task/sub-agent capability, the Batch
runtime prepares an isolated task context and writes a dispatch request with
status `READY_FOR_NATIVE_DISPATCH`. The host AI agent running this Skill MUST
consume that request with its own native Task/Subagent tool. The shell and
PowerShell runtimes MUST NOT spawn, emulate, or substitute a provider CLI for
this step.

The request includes `task_id`, `issue_number`, `worktree_path`, `branch_name`,
prompt/context, required Skills, execution scope, validation requirements, and
result path. The host updates the request lifecycle as it accepts and runs the
task:

```text
READY_FOR_NATIVE_DISPATCH → DISPATCHED → SUBAGENT_RUNNING → COMPLETED/FAILED
```

The runtime collects the result and applies the existing Batch state machine.
If native capability is unavailable and no explicit external provider is
configured, the worker remains `BLOCKED` with `Issue execution: NOT_STARTED`.

### Legacy Path (PowerShell)

```text
batch.ps1 (entry point)
    ↓
Orchestrator (Invoke-BatchOrchestrator.ps1)
    ↓
Sub-agent Worker (pwsh child process)
    ↓
Task execution
```

Both implementations are functionally equivalent. The Shell version is the default for cross-platform use.

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
| `.batch-checkpoints-{id}/batch-checkpoint.json` | Batch checkpoint with worker summaries |
| `.batch-checkpoints-{id}/worker-{issueId}.json` | Per-worker runtime checkpoints |
| `.batch-log-{id}.jsonl` | Transition audit log (JSONL) |

### Checkpoint Lifecycle

Checkpoints are saved atomically at key transitions:

```text
BATCH_INITIALIZING → PLANNING → SCHEDULING → RUNNING
    ↓                                                    ↓
    └→ FAILED                              WAITING_FOR_MERGE → MERGING → CLEANUP → COMPLETED
                                               ↓                                        ↓
                                            COMPLETED                               FAILED
                                               ↓
                                            FAILED
```

Worker checkpoints are saved at:
- Phase transitions: `agent_completed` → `commit` → `push` → `pr_created`
- State changes: `PENDING` → `RUNNING` → `SUCCESS`/`ORPHANED`/`FAILED`
- Retry events: increment `retryCount`, update `lastRetryAt`
- Before git operations: capture `changedFiles`, `branch`, `baseCommit`

### Checkpoint Schema (Provider-Neutral)

Core schema (version 1) is independent of any specific agent provider:

**Batch Checkpoint:**
```json
{
  "schemaVersion": 1,
  "batchId": "batch-123",
  "createdAt": "2026-01-01T00:00:00Z",
  "updatedAt": "2026-01-01T00:00:00Z",
  "batchState": "RUNNING",
  "issueCount": 5,
  "completedCount": 2,
  "failedCount": 0,
  "blockedCount": 1,
  "failureReason": null,
  "workers": {
    "issue-1": { "state": "SUCCESS", "updatedAt": "..." }
  }
}
```

**Worker Checkpoint:**
```json
{
  "schemaVersion": 1,
  "issueId": "issue-1",
  "issueNumber": 42,
  "description": "Add feature X",
  "createdAt": "2026-01-01T00:00:00Z",
  "updatedAt": "2026-01-01T00:00:00Z",
  "provider": "claude-code",
  "lifecycleState": "RUNNING",
  "completedPhases": ["agent_completed", "commit"],
  "branch": "issue/42-feature-x",
  "baseCommit": "abc123",
  "currentCommit": "def456",
  "resultCommit": null,
  "prNumber": null,
  "prState": null,
  "worktreePath": "/tmp/worktree/issue-42",
  "testResult": null,
  "testPassed": false,
  "remainingWork": null,
  "failureReason": null,
  "failureCategory": null,
  "retryCount": 0,
  "maxRetries": 3,
  "lastRetryAt": null,
  "processId": 12345,
  "startedAt": "2026-01-01T00:00:00Z",
  "completedAt": null,
  "providerMetadata": { "sessionId": "abc" }
}
```

### Lifecycle States (Worker Checkpoint)

| State | Meaning | Source Issue State |
|-------|---------|-------------------|
| `PENDING` | Not yet started | `WAITING_DEPENDENCY`, `WAITING_FOR_SUBAGENT` |
| `RUNNING` | Active execution | `SUBAGENT_STARTING`, `SUBAGENT_RUNNING`, `SUBAGENT_RETRYING`, `PR_READY` |
| `ORPHANED` | Process died, no result | `ORPHANED` |
| `SUCCESS` | Completed, PR ready | `COMPLETED` |
| `FAILED` | Exhausted retries | `SUBAGENT_FAILED`, `FAILED` |

### ORPHANED Detection and Recovery

1. **Detection**: On resume, `Test-OrphanedProcess` checks:
   - Process liveness via PID
   - Result file existence and JSON validity
   - Corrupt result.json → treat as orphaned

2. **Recovery Flow**:
   ```
   ORPHANED detected
       ↓
   retryCount < maxRetries?
       ├─ YES → retryCount++ → WAITING_FOR_SUBAGENT → redispatch
       └─ NO  → SUBAGENT_FAILED (NOT added to completedIssues)
   ```

3. **Retry Budget Continuation**: `retryCount` and `maxRetries` persist in checkpoint and survive orchestrator restarts.

### Retry Budget

- Per-issue `maxRetries` (default 3, configurable)
- `retryCount` increments on each retry attempt
- Checkpointed at every retry transition
- On resume, `Test-SubAgentRetryable` observes persisted count

### Idempotency

- **PR Existence Check**: Before launching worker, `Test-GitPrExists` runs regardless of branch existence
- **Git Operation Validation**: `$LASTEXITCODE` checked after `git add/commit/push`; commit SHA verified
- **Duplicate Prevention**: Existing PR → transition to `PR_READY`, skip worker launch

### Atomic Persistence

All writes use temp-file + atomic rename:

```powershell
$tmpFile = "$FilePath.tmp.$pid.$(Get-Random)"
$Data | ConvertTo-Json -Depth 20 | Set-Content -Path $tmpFile -Force
Move-Item -Path $tmpFile -Destination $FilePath -Force
```

Transition log uses `Add-Content -ErrorAction Stop` with parent directory auto-creation.

### Corrupt State Handling (Fail-Closed)

| Scenario | Behavior |
|----------|----------|
| Missing state file | Return `$null` (new run) |
| Existing file, corrupt JSON | **Throw error** (fail-closed) |
| Existing file, valid JSON | Return parsed state |

Prevents silent progress loss and duplicate dispatch.

### Safe Filename Generation

**Checkpoint Directory**: `.batch-checkpoints-{BatchId}`

**Worker Filename**: `worker-{safeIssueId}.json`

**Encoding Rules**:
- Safe IssueIds (`^[a-zA-Z0-9_-]+$`): used directly → `worker-issue-1.json`
- Unsafe IssueIds: `~` + uppercase hex of UTF-8 bytes → `worker-~69737375652F31.json` for `issue/1`
- Blank/whitespace IssueIds: **rejected** before filename generation

**Injectivity Guarantee**: Distinct IssueIds always produce distinct filenames. No collision between `issue/1` (`~69737375652F31`) and `issue_2F1` (safe, unchanged).

### BatchId / IssueId Validation

Rejected patterns at all path construction points:
- Empty or whitespace-only
- Path separators: `/` `\`
- Parent traversal: `..`

Applied in: `Get-CheckpointDirectory`, `Get-BatchStateFilePath`, `Get-TransitionLogPath`, `Get-WorkerCheckpointPath`.

### Resume Behavior

1. Load batch checkpoint (`Get-BatchCheckpoint`)
2. Load all worker checkpoints (`Get-AllWorkerCheckpoints`)
3. Load legacy state (`Get-BatchState`, `Get-IssueStates`)
4. Build recovery context (`New-RecoveryContext`)
5. Reconcile: checkpoint data takes precedence when legacy state is missing/stale
6. Restore `retryCount`, `completedPhases`, `currentCommit`, `prNumber`, `providerMetadata`
7. Detect ORPHANED workers from checkpoint + process liveness
8. Resume scheduling from `RUNNING` state

### Provider-Neutral Design

- Core checkpoint schema contains NO provider-specific logic
- `providerMetadata` field isolates provider-specific data (e.g., session ID)
- New providers only add to `providerMetadata`; core fields unchanged
- `Save-AllCheckpoints` records the provider selected by the runtime policy; no
  provider is the default.

### Cross-Platform Considerations

| Platform | Notes |
|----------|-------|
| **Windows** | Native `Move-Item -Force` atomic rename; NTFS supports |
| **Linux/macOS** | `Move-Item` atomic on same filesystem; PowerShell Core 7.x |
| **Linux without pwsh** | Shell implementation (orchestrator.sh) uses same atomic pattern; no pwsh required |

The Shell runtime (`orchestrator.sh`, `persistence.sh`) provides equivalent checkpoint/resume without PowerShell dependency. PowerShell is ONLY needed for the legacy `batch.ps1` entry point.

### Provider Implementation Details Isolation

Provider-specific implementation details MUST NOT leak into core checkpoint schema:
- Session tokens, API keys → `providerMetadata` only
- Provider-specific phase names → map to standard `completedPhases` values
- Provider-specific error categories → map to standard `failureCategory` values

### Configuration

Checkpoint behavior controlled in `config/batch-config.json`:

```json
{
  "checkpoint": {
    "enabled": true,
    "provider_neutral": true,
    "recovery": {
      "orphan_detection": true,
      "idempotency_protection": true
    }
  }
}
```

### Recovery Process (Detailed)

```text
1. Resume invoked (batch.sh resume / batch.ps1 resume)
2. Load batch checkpoint → Get-BatchCheckpoint
3. Load worker checkpoints → Get-AllWorkerCheckpoints
4. Load legacy state → Get-BatchState, Get-IssueStates
5. Sync-StateWithGitHub → verify PR/branch reality
6. For each issue with worker checkpoint:
   a. Build recovery context → New-RecoveryContext
   b. If ORPHANED in checkpoint + process dead → Test-OrphanedProcess
   c. If retry eligible → increment retryCount → WAITING_FOR_SUBAGENT
   d. If retry exhausted → SUBAGENT_FAILED
7. For issues without worker checkpoint:
   a. Use legacy issue state
   b. Apply ORPHANED detection from process liveness
8. Enter RUNNING loop with restored state
9. Continue scheduling and dispatch
```

## Cleanup

After merge confirmation:

1. Delete Worktree
2. Delete local Branch
3. Delete remote Branch
4. Prune stale references
5. Mark Issue as COMPLETED

## Dependencies

### Required

| Tool | Purpose |
|------|---------|
| git | Repository operations, worktree management |

### Optional

| Tool | Purpose | Used By |
|------|---------|---------|
| gh | GitHub PR/issue operations | github-operations.sh, merge-queue.sh |
| claude | Claude Code CLI | adapters/claude-code/ |
| pwsh | PowerShell (for PS runtime only) | powershell/ |

### Not Required

| Tool | Notes |
|------|-------|
| jq | Shell version uses sed-based JSON |
| python | Not needed |
| node | Not needed |
| opencode | Not a required dependency |
| codex | Not a required dependency |

When `gh` is not available, GitHub-dependent operations (PR creation, approval checks, merge) return graceful errors. The orchestrator continues processing and stops safely before any merge that requires approval verification.

## Configuration

Project-specific configuration in `config/batch-config.json`.

## Runtime

### Shell (POSIX sh) — Default

```text
runtime/
├── batch.sh                          # CLI entry point
├── orchestrator.sh                   # Main orchestrator loop
├── persistence.sh                    # JSON state I/O (atomic writes)
├── git-operations.sh                 # Worktree CRUD, branch ops
├── agent-runtime.sh                  # Provider dispatch interface
├── github-operations.sh              # PR management via gh CLI
├── merge-queue.sh                    # Serial merge with approval gate
├── core/                             # Pure logic (zero I/O)
│   ├── state-machine.sh              # Batch/issue state transitions
│   ├── dependency-graph.sh           # DAG, cycle detection
│   ├── scheduler.sh                  # Concurrency-aware scheduling
│   ├── retry.sh                      # Exponential backoff
│   ├── contracts.sh                  # State schema validation
│   └── tests/                        # 227 tests
├── adapters/
│   ├── test/adapter.sh               # Test provider (no AI agent)
│   ├── built-in-subagent/adapter.sh  # Host agent Task tool contract
│   └── claude-code/adapter.sh        # Explicitly selected provider adapter
└── tests/                            # 122 tests
```

### PowerShell — Legacy

```text
runtime/
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

### Design Principles

- **Core Logic**: Pure functions, zero I/O, zero external dependencies
- **Runtime Layer**: POSIX sh compatible, sources Core modules
- **Git**: Only hard dependency
- **Agent Runtime**: Provider-agnostic dispatch via adapter pattern
- **State**: JSON files with atomic writes (temp + mv)

## Usage

### Shell (POSIX sh) — Cross-platform

```sh
# Run batch with issue numbers
batch.sh run batch-100 101 102 103

# Run with test provider (no AI agent needed)
batch.sh run batch-100 101 102 --provider test

# Run with custom concurrency and retries
batch.sh run batch-100 101 102 103 --max-concurrency 5 --max-retries 5

# Resume after interruption (syncs with GitHub)
batch.sh resume batch-100

# Check status
batch.sh status batch-100

# Show help
batch.sh help
```

### PowerShell (Windows / pwsh)

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

### Provider Selection

| Scenario | Recommended Provider |
|----------|---------------------|
| Host agent with native Task tool | host-native mechanism |
| Explicit provider configured | configured provider adapter |
| CI / deterministic testing | test |
| Native unavailable and no provider configured | `BLOCKED` |

## Non-goals

- Replacing human judgment on merge timing
- Automatic merge without user approval
- Orchestrator implementing code for Sub-agents
- Parallel merge to main
- Admin bypass or protection circumvention
- Ignoring dependency ordering
