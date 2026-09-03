# Batch Orchestrator Phase 0 Design: Agent-agnostic / OS-agnostic Architecture

## Overview

Redesign the Batch Orchestrator from a PowerShell-coupled, CLI-dependent system to an Agent-agnostic, OS-agnostic execution platform that uses the host agent's built-in Sub-agent capabilities as the primary execution path.

**Status:** Superseded (historical record)
**Date:** 2026-08-26
**Scope:** Architecture redesign preserving all existing functionality

> **Superseded by Issue #242.** This document designed a shell + PowerShell
> Batch Orchestrator runtime. That runtime has been removed: batch orchestration
> is now a Markdown-only, agent-agnostic protocol with no scripts, wrapper,
> scheduler, state-machine implementation, or configuration file.
>
> Every file path, module name, script, and configuration key below refers to
> assets that **no longer exist**. The behaviour they implemented was migrated
> into the protocol before deletion — see the Functional Preservation Checklist
> at the end of this document for the feature list, and
> [`../skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md)
> for the current specification.
>
> This document is retained as a historical record of the design reasoning. It
> is not a current reference and must not be used as one.

---

## 1. Existing Module Responsibility Map

### 1.1 Module Inventory

| Module | Lines | Responsibility | Dependencies |
|--------|-------|---------------|-------------|
| BatchStateMachine | 138 | Batch-level state definitions + transition validation | None |
| IssueStateMachine | 172 | Per-Issue state definitions + transition validation | None |
| DependencyGraph | 414 | DAG construction, cycle detection, topo sort, concurrency groups | None |
| BatchScheduler | 290 | Concurrency limit management, parallel dispatch | IssueStateMachine |
| BatchSubAgent | 490 | Sub-agent lifecycle, retry, backoff, failure categorization, **process launch** | System.Diagnostics.Process, Get-Process |
| BatchGitUtilities | 297 | Worktree create/remove, branch management, env init | git CLI |
| BatchMergeQueue | 276 | Serial merge queue, **pwsh -File invocation** | pwsh CLI, Merge Skill |
| BatchPersistence | 185 | JSON state save/load, GitHub sync | ConvertTo-Json, gh CLI |
| Invoke-BatchOrchestrator | 659 | Main orchestration loop (all phases) | All modules |
| Invoke-SubAgentWorker | 261 | Single-issue worker (investigate→implement→test→commit→push→PR) | opencode CLI, git, gh |
| Launch-SubAgent | 23 | Thin wrapper launching worker via pwsh | pwsh CLI |
| batch.ps1 | 122 | CLI entry point (run/resume/status/test) | pwsh CLI |

### 1.2 Data Flow (Current)

```
batch.ps1 (CLI)
  └→ Invoke-BatchOrchestrator.ps1
       ├→ BatchPersistence: Load/create state
       ├→ DependencyGraph: Build DAG, detect cycles, compute waves
       ├→ BatchScheduler: Register issues, manage slots
       ├→ BatchGitUtilities: Create worktrees (missing in current orchestrator!)
       ├→ BatchSubAgent → Launch-SubAgent.ps1
       │    └→ Invoke-SubAgentWorker.ps1
       │         ├→ opencode CLI: AI agent execution
       │         ├→ git: add, commit, push
       │         └→ gh: pr create
       ├→ BatchMergeQueue → pwsh -File merge.ps1
       ├→ BatchPersistence: Save state
       └→ BatchGitUtilities: Cleanup worktrees
```

### 1.3 Identified Coupling Points

| Coupling | Location | Type | Severity |
|----------|----------|------|----------|
| `#Requires -Version 7.0` | All 12 files | PowerShell | Critical |
| `Import-Module` / `Export-ModuleMember` | All modules | PowerShell | Critical |
| `System.Diagnostics.Process.Start("pwsh", ...)` | BatchSubAgent.psm1:373-381 | PowerShell | Critical |
| `& pwsh -File $mergeScript` | BatchMergeQueue.psm1:164 | PowerShell | High |
| `opencode.exe` hardcoded | Invoke-SubAgentWorker.ps1:120-123 | CLI | High |
| `Write-Host -ForegroundColor` | All scripts | PowerShell | Low |
| `$Script:` scope | BatchStateMachine, IssueStateMachine | PowerShell | Low |
| `ConvertTo-Json/FromJson` | BatchPersistence, BatchSubAgent | PowerShell | Medium |
| `Start-Sleep` | BatchSubAgent, BatchMergeQueue | PowerShell | Low |

---

## 2. Agent Runtime Interface Design

### 2.1 Architecture

```
Batch Orchestrator Core (Platform-independent)
    │
    ├── State Machine (JSON state files)
    ├── Dependency DAG (JSON graph)
    ├── Scheduler (concurrency groups)
    ├── Retry Manager (JSON state + backoff calc)
    ├── Persistence (JSON file I/O)
    ├── Approval Gate (gh API or adapter)
    ├── Merge Queue (delegated to Merge Skill)
    └── Cleanup (git operations)
            │
            ▼
    Agent Runtime Interface (ARI)
            │
    ┌───────┼──────────────────────────┐
    │       │                          │
Built-in   CLI Adapter (Optional)    Test Provider
Sub-agent  │                          │
(Primary)  ├── Claude Code Adapter    ├── Mock Result
           ├── OpenCode Adapter       ├── Deterministic
           ├── Codex Adapter          └── CI-ready
           └── Future Adapter
```

### 2.2 ARI Contract (JSON Schema)

The Agent Runtime Interface is defined by a task-to-result contract. Any provider must accept a Task and return a Result.

#### Task (Input)

```json
{
  "$schema": "batch-task-schema.json",
  "type": "object",
  "required": ["task_id", "issue_number", "worktree_path", "branch_name", "prompt"],
  "properties": {
    "task_id": {
      "type": "string",
      "description": "Unique task identifier (issue ID)"
    },
    "issue_number": {
      "type": "integer",
      "description": "GitHub issue number"
    },
    "description": {
      "type": "string",
      "description": "Short issue description"
    },
    "worktree_path": {
      "type": "string",
      "description": "Absolute path to the git worktree"
    },
    "branch_name": {
      "type": "string",
      "description": "Git branch name for this task"
    },
    "prompt": {
      "type": "string",
      "description": "Full task prompt for the sub-agent"
    },
    "timeout_minutes": {
      "type": "integer",
      "default": 30,
      "description": "Maximum execution time"
    },
    "result_file": {
      "type": "string",
      "description": "Path where sub-agent should write result JSON"
    },
    "environment": {
      "type": "object",
      "description": "Additional environment variables"
    }
  }
}
```

#### Result (Output)

```json
{
  "$schema": "batch-result-schema.json",
  "type": "object",
  "required": ["success", "task_id"],
  "properties": {
    "success": {
      "type": "boolean"
    },
    "task_id": {
      "type": "string"
    },
    "pr_number": {
      "type": "integer",
      "description": "GitHub PR number (null if no PR)"
    },
    "commit_sha": {
      "type": "string",
      "description": "Commit SHA (null if no commit)"
    },
    "changed_files": {
      "type": "array",
      "items": { "type": "string" },
      "description": "List of modified files"
    },
    "report": {
      "type": "object",
      "properties": {
        "investigation_summary": { "type": "string" },
        "implementation_summary": { "type": "string" },
        "design_decision": { "type": "string" },
        "test_results": { "type": "string" },
        "test_passed": { "type": "boolean" }
      }
    },
    "error": {
      "type": "string",
      "description": "Error message (null if success)"
    },
    "error_category": {
      "type": "string",
      "enum": ["api_error", "timeout", "connection_failure", "transient", "code_error", "test_failure", "architecture_violation", "dependency_conflict"],
      "description": "Error category for retry classification"
    },
    "started_at": {
      "type": "string",
      "format": "date-time"
    },
    "completed_at": {
      "type": "string",
      "format": "date-time"
    },
    "duration_seconds": {
      "type": "number"
    },
    "provider": {
      "type": "string",
      "description": "Provider that executed this task"
    }
  }
}
```

### 2.3 Provider Interface

Every provider must implement these operations:

```
interface AgentProvider {
  // Identity
  name: string          // "built-in-subagent", "claude-code", "test"
  type: string          // "host", "cli", "test"
  available(): boolean  // Can this provider run in current environment?

  // Execution
  launch(task: Task): ProviderHandle
  // Returns immediately with a handle for monitoring

  poll(handle: ProviderHandle): ProviderStatus
  // Returns current status without blocking

  wait(handle: ProviderHandle, timeout_ms: number): Result
  // Blocks until completion or timeout

  cancel(handle: ProviderHandle): boolean
  // Attempts to cancel a running task

  // Lifecycle
  cleanup(handle: ProviderHandle): void
  // Release resources associated with a handle
}
```

#### ProviderHandle

```json
{
  "provider": "built-in-subagent",
  "handle_id": "task-123",
  "pid": null,
  "started_at": "2026-08-26T10:00:00Z",
  "status": "running"
}
```

#### ProviderStatus

```json
{
  "status": "running | completed | failed | cancelled | timeout",
  "elapsed_ms": 5000,
  "result_file_exists": false
}
```

---

## 3. Built-in Host Sub-agent Execution Model

### 3.1 Primary Execution Path

When the host agent has built-in sub-agent capabilities, this is the standard execution path:

```
Orchestrator Core
    │
    ├── 1. Create worktree (git worktree add)
    ├── 2. Build task prompt
    ├── 3. Invoke ARI with provider="built-in-subagent"
    │       │
    │       ▼
    │   Host Agent Runtime
    │       │
    │       ├── Task tool(prompt=sub_agent_prompt)
    │       │   │
    │       │   ▼
    │       │ Sub-agent executes:
    │       │   1. Reads issue description
    │       │   2. Investigates codebase
    │       │   3. Implements changes
    │       │   4. Runs tests
    │       │   5. git add + commit
    │       │   6. git push
    │       │   7. gh pr create
    │       │   8. Returns Result JSON
    │       │
    │       └── Returns Result to Orchestrator
    │
    ├── 4. Process result
    ├── 5. Handle retry if needed
    └── 6. Continue orchestration
```

### 3.2 Task Prompt Template (for Host Sub-agent)

The orchestrator builds a prompt that the sub-agent executes:

```
You are working on Issue #{issue_number} in a git repository.

ISSUE: #{issue_number}
TITLE: {description}

WORKTREE: {worktree_path}
BRANCH: {branch_name}

TASK:
1. Investigate Issue #{issue_number} and understand the requirements
2. Research the repository structure and relevant code
3. Implement the required changes
4. Run available tests to verify your changes
5. Create a git commit with a descriptive message
6. Push the branch to origin
7. Create a Pull Request targeting main

CONSTRAINTS:
- Work ONLY in {worktree_path}
- Do NOT modify files outside this worktree
- Do NOT use --admin, force push, or bypass approvals
- Use branch name '{branch_name}' for the PR
- After creating the PR, output a JSON result file at {result_file} with format:
  {"success": true, "pr_number": <number>, "commit_sha": "<sha>", "changed_files": [...]}
```

### 3.3 Why Built-in Sub-agent is Primary

| Factor | Built-in Sub-agent | CLI Provider |
|--------|-------------------|-------------|
| Installation required | No | Yes |
| Authentication required | Uses host session | Separate login |
| OS dependency | None | May vary |
| Recursive agent spawning | No | Yes (risky) |
| Tool access | Full (inherits host) | Limited (CLI sandbox) |
| Monitoring | Built-in (poll/return) | Process exit code |
| Cancellation | Host-native | Kill process |

---

## 4. Optional Adapter Structure

### 4.1 Adapter Directory Layout

```
runtime/
├── adapters/
│   ├── README.md                    # Adapter documentation
│   ├── adapter-interface.json       # JSON Schema for adapter contract
│   ├── built-in-subagent/
│   │   ├── README.md                # Built-in sub-agent adapter docs
│   │   └── adapter.sh               # Shell: invokes Task tool via host
│   ├── claude-code/
│   │   ├── README.md                # Claude Code adapter docs
│   │   └── adapter.sh               # Shell: invokes `claude -p`
│   ├── opencode/
│   │   ├── README.md                # OpenCode adapter docs
│   │   └── adapter.sh               # Shell: invokes `opencode run`
│   ├── codex/
│   │   ├── README.md                # Codex adapter docs
│   │   └── adapter.sh               # Shell: invokes `codex exec`
│   └── test/
│       ├── README.md                # Test provider docs
│       └── adapter.sh               # Shell: returns mock result
├── core/
│   ├── state-machine.sh             # Batch + Issue state machines
│   ├── dependency-graph.sh          # DAG operations
│   ├── scheduler.sh                 # Concurrency management
│   ├── retry.sh                     # Backoff calculation
│   ├── persistence.sh               # JSON state I/O
│   └── cleanup.sh                   # Worktree/branch removal
├── orchestrator.sh                  # Main orchestrator (shell)
└── powershell/                      # Existing (backward compat, not required)
```

### 4.2 Adapter Selection Logic

```pseudocode
function select_provider(preferred_provider):
    # 1. Check if preferred provider is available
    if preferred_provider.available():
        return preferred_provider

    # 2. Fallback chain
    fallback_order = ["built-in-subagent", "test"]
    for provider in fallback_order:
        if provider.available():
            log("Preferred provider unavailable, using fallback: " + provider.name)
            return provider

    # 3. No provider available
    error("No agent provider available. Install one of: " + fallback_order.join(", "))
    return null
```

### 4.3 Adapter: built-in-subagent (Primary)

**Execution model:** The host agent uses its built-in Task tool to spawn a sub-agent.

```bash
# adapter.sh for built-in-subagent
# This is a documentation/interface adapter.
# The actual execution happens via the host agent's Task tool.
# This adapter documents the contract for the orchestrator.

invoke() {
    local task_json="$1"
    local task_id=$(echo "$task_json" | jq -r '.task_id')
    local prompt=$(echo "$task_json" | jq -r '.prompt')
    local result_file=$(echo "$task_json" | jq -r '.result_file')

    # In Claude Code: Task tool call
    # In other agents: equivalent sub-agent mechanism
    # This shell script is a DOCUMENTATION adapter, not executable logic.
    # The orchestrator reads this adapter's interface definition
    # and uses whatever sub-agent mechanism the host provides.

    echo "ERROR: This adapter must be invoked through the host agent's sub-agent mechanism, not directly."
    return 1
}
```

**Key insight:** The built-in sub-agent adapter is primarily a **contract definition**, not an executable script. The orchestrator core reads the contract and uses whatever sub-agent mechanism the host agent provides.

### 4.4 Adapter: claude-code (Optional CLI Fallback)

```bash
# adapter.sh for claude-code CLI

AVAILABLE=$(command -v claude >/dev/null 2>&1 && echo "true" || echo "false")

available() {
    [ "$AVAILABLE" = "true" ]
}

invoke() {
    local task_json="$1"
    local worktree_path=$(echo "$task_json" | jq -r '.worktree_path')
    local prompt=$(echo "$task_json" | jq -r '.prompt')
    local timeout=$(echo "$task_json" | jq -r '.timeout_minutes // 30')

    local result_file=$(echo "$task_json" | jq -r '.result_file')
    local stdout_file="/tmp/batch-stdout-$(date +%s).txt"
    local stderr_file="/tmp/batch-stderr-$(date +%s).txt"

    # Write prompt to temp file
    local prompt_file="/tmp/batch-prompt-$(date +%s).txt"
    echo "$prompt" > "$prompt_file"

    # Launch claude CLI
    timeout "${timeout}m" claude -p "$(cat "$prompt_file")" \
        --output-format json \
        > "$stdout_file" 2> "$stderr_file" &
    local pid=$!

    echo "{\"handle_id\": \"$pid\", \"pid\": $pid, \"stdout\": \"$stdout_file\", \"stderr\": \"$stderr_file\"}"

    # Wait for completion
    wait "$pid"
    local exit_code=$?

    # Parse result
    if [ -f "$result_file" ]; then
        cat "$result_file"
    else
        echo "{\"success\": false, \"error\": \"No result file produced\", \"exit_code\": $exit_code}"
    fi
}
```

### 4.5 Adapter: test (Mock Provider for CI)

```bash
# adapter.sh for test/mock provider

available() {
    # Always available
    return 0
}

invoke() {
    local task_json="$1"
    local task_id=$(echo "$task_json" | jq -r '.task_id')
    local result_file=$(echo "$task_json" | jq -r '.result_file')
    local worktree_path=$(echo "$task_json" | jq -r '.worktree_path')

    # Determine action from task description
    local description=$(echo "$task_json" | jq -r '.description // ""')

    # Check if this is a test that expects a no-op
    if echo "$description" | grep -qi "no.op\|noop\|no change"; then
        echo "{\"success\": true, \"task_id\": \"$task_id\", \"pr_number\": null, \"commit_sha\": null, \"changed_files\": [], \"provider\": \"test\", \"report\": {\"implementation_summary\": \"No changes required\"}}"
        return 0
    fi

    # Create a minimal change in the worktree
    if [ -d "$worktree_path" ]; then
        cd "$worktree_path"
        echo "# Test change for task $task_id" > ".batch-test-$(date +%s).txt"
        git add -A
        git commit -m "test: Batch Orchestrator test provider change for task $task_id"
        local commit_sha=$(git rev-parse HEAD)

        # Push (may fail in CI, that's ok)
        git push origin HEAD 2>/dev/null || true

        # Create PR (may fail in CI, that's ok)
        local pr_number=""
        pr_number=$(gh pr create --title "test: Task $task_id" --body "Automated test by Batch Orchestrator Test Provider" --base main 2>/dev/null | grep -o '[0-9]*' || echo "")

        echo "{\"success\": true, \"task_id\": \"$task_id\", \"pr_number\": ${pr_number:-null}, \"commit_sha\": \"$commit_sha\", \"changed_files\": [\".batch-test-*.txt\"], \"provider\": \"test\", \"report\": {\"implementation_summary\": \"Test provider created minimal change\"}}"
    else
        echo "{\"success\": false, \"task_id\": \"$task_id\", \"error\": \"Worktree not found: $worktree_path\", \"provider\": \"test\"}"
        return 1
    fi
}
```

---

## 5. Core/Shell Separation

### 5.1 Separation Principle

```
┌─────────────────────────────────────────────┐
│              Core (Pure Logic)               │
│                                              │
│  State Machine    - State definitions        │
│                    - Transition validation    │
│                    - Pure functions            │
│                                              │
│  Dependency DAG   - Graph operations          │
│                    - Cycle detection           │
│                    - Topological sort          │
│                    - Concurrency groups        │
│                                              │
│  Scheduler        - Slot management           │
│                    - Dispatch decisions        │
│                    - Completion tracking       │
│                                              │
│  Retry Manager    - Backoff calculation       │
│                    - Category classification  │
│                    - Retry decisions           │
│                                              │
│  Contracts        - JSON schemas              │
│                    - Interface definitions     │
│                    - Validation rules          │
└─────────────────────────────────────────────┘
           │
           │ uses
           ▼
┌─────────────────────────────────────────────┐
│           Runtime (Platform-specific)         │
│                                              │
│  Persistence      - JSON file I/O            │
│                    - Shell/Python/Node        │
│                                              │
│  Git Operations   - worktree, branch         │
│                    - commit, push             │
│                    - Shell commands            │
│                                              │
│  GitHub Operations - gh CLI                   │
│                     - PR create/view          │
│                     - Approval check          │
│                                              │
│  Agent Runtime    - ARI implementation       │
│                    - Provider selection       │
│                    - Task dispatch            │
│                                              │
│  Orchestrator     - Main loop                │
│                    - Phase coordination       │
│                    - Shell/Python/Node        │
└─────────────────────────────────────────────┘
```

### 5.2 Core: Pure Functions (No I/O)

The Core layer contains only pure functions with no side effects:

| Module | Pure Functions | I/O Functions |
|--------|---------------|---------------|
| StateMachine | `valid_transition(from, to)`, `is_terminal(state)`, `get_valid_transitions(state)` | None |
| DependencyGraph | `add_node(graph, id)`, `add_edge(graph, from, to)`, `detect_cycle(graph)`, `topo_sort(graph)`, `concurrency_groups(graph)` | None |
| Scheduler | `slot_available(scheduler)`, `get_ready_issues(scheduler, completed)` | None |
| RetryManager | `is_retryable(category)`, `calculate_backoff(state, max)`, `categorize_error(message)` | None |
| Contracts | `validate_task(json)`, `validate_result(json)` | None |

### 5.3 Runtime: I/O and Platform Operations

| Module | I/O Operations | Platform |
|--------|---------------|----------|
| Persistence | `save_state(state, path)`, `load_state(path)`, `sync_with_github(state)` | Shell/Python |
| GitOperations | `create_worktree(...)`, `remove_worktree(...)`, `get_commit_sha(path)` | Shell (git) |
| GitHubOperations | `create_pr(...)`, `check_approval(pr_number)`, `get_pr_state(pr_number)` | Shell (gh) |
| AgentRuntime | `select_provider()`, `launch_task(task)`, `poll_task(handle)`, `wait_task(handle)` | Shell + ARI |
| Orchestrator | `run(batch_id, issues)`, `resume(batch_id)`, `status(batch_id)` | Shell/Python |

### 5.4 Technology Selection for Core vs Runtime

```
Core (Pure Logic):
  - Target: Any POSIX shell (bash, sh, dash, zsh)
  - No external dependencies
  - Functions only, no side effects
  - Testable with simple assertions

Runtime (I/O Layer):
  - Primary: bash (available on all Unix-like systems)
  - Fallback: Python 3 (widely available)
  - Optional: PowerShell 7 (existing implementation)
  - Optional: Node.js (if project uses it)
```

---

## 6. Task/Result/Lifecycle/Error/Retry/Cancel Contracts

### 6.1 Lifecycle States

```
Task Lifecycle:
  CREATED → QUEUED → DISPATCHED → RUNNING → COMPLETED
                                                  ↓
                                            FAILED (terminal)
                                                  ↓
                                         RETRYING → DISPATCHED
                                                  ↓
                                         CANCELLED (terminal)
                                                  ↓
                                         TIMEOUT → RETRYING or FAILED
```

**State definitions:**

| State | Description | Next States |
|-------|-------------|-------------|
| CREATED | Task object created, not yet queued | QUEUED |
| QUEUED | Waiting for dependency/concurrency slot | DISPATCHED |
| DISPATCHED | Submitted to provider, waiting for execution | RUNNING, FAILED |
| RUNNING | Agent actively executing | COMPLETED, FAILED, TIMEOUT, CANCELLED |
| COMPLETED | Agent finished successfully | (terminal) |
| FAILED | Agent failed (non-retryable or retries exhausted) | (terminal) |
| RETRYING | Waiting for backoff before retry | DISPATCHED |
| CANCELLED | Cancelled by orchestrator or user | (terminal) |
| TIMEOUT | Execution exceeded timeout | RETRYING, FAILED |

### 6.2 Error Categories and Retry Rules

```json
{
  "retryable": {
    "api_error": { "description": "API rate limit or transient error", "max_retries": 3 },
    "timeout": { "description": "Execution timeout", "max_retries": 2 },
    "connection_failure": { "description": "Network connectivity issue", "max_retries": 3 },
    "transient": { "description": "Unknown transient failure", "max_retries": 3 }
  },
  "non_retryable": {
    "code_error": { "description": "Compilation or syntax error in implementation" },
    "test_failure": { "description": "Tests failed after implementation" },
    "architecture_violation": { "description": "Change violates project architecture rules" },
    "dependency_conflict": { "description": "Unresolvable dependency conflict" }
  }
}
```

### 6.3 Retry Contract

```
RetryManager:
  state = {
    task_id: string,
    retry_count: int,
    max_retries: int,
    last_error: string,
    last_error_category: string,
    backoff_seconds: int,
    next_retry_at: datetime
  }

  functions:
    should_retry(state, error_category) -> {retryable: bool, reason: string}
    calculate_backoff(state, max_backoff=120) -> int  // seconds
    categorize_error(error_message) -> string
    prepare_retry(state, error_category) -> state
```

### 6.4 Cancel Contract

```
CancelContract:
  - Cancel is cooperative, not force-kill
  - Orchestrator sets task state to CANCELLED
  - Provider receives cancellation signal (if supported)
  - Task completes current operation and exits
  - Cleanup runs regardless of cancellation
  - Cancelled tasks can be retried (state → CREATED)
```

---

## 7. Worktree Responsibility Boundaries

### 7.1 What the Orchestrator Owns

```
Orchestrator:
  ✓ Creates worktree (git worktree add)
  ✓ Creates branch (git checkout -b)
  ✓ Initializes environment (.env files)
  ✓ Records worktree path in task state
  ✓ Passes worktree path to sub-agent
  ✓ Reads result from worktree
  ✓ Removes worktree on completion (git worktree remove)
  ✓ Removes branch on completion (git branch -D)
  ✓ Removes remote branch on completion (git push --delete)
  ✓ Prunes stale references (git worktree prune)
```

### 7.2 What the Sub-agent Owns

```
Sub-agent:
  ✓ Reads files in worktree
  ✓ Modifies files in worktree
  ✓ Runs tests in worktree
  ✓ Creates commits in worktree
  ✓ Pushes branch from worktree
  ✓ Creates PR from worktree
  ✓ Writes result file in worktree
  ✗ Does NOT create/remove worktree
  ✗ Does NOT modify files outside worktree
  ✗ Does NOT push to main
  ✗ Does NOT merge PRs
```

### 7.3 Worktree Isolation Guarantee

```
Repository (main worktree)
  ├── .git/
  ├── src/
  └── ...

worktrees/
  ├── 169-add-timer-register/
  │   ├── .git -> ../../.git  (shared git dir)
  │   ├── src/
  │   └── .subagent/result.json
  ├── 170-fix-dma-alignment/
  │   ├── .git -> ../../.git
  │   ├── src/
  │   └── .subagent/result.json
  └── 171-implement-interrupt/
      ├── .git -> ../../.git
      ├── src/
      └── .subagent/result.json

Each worktree:
  - Shares .git with main (objects, refs)
  - Has independent working tree
  - Has independent index
  - Cannot affect other worktrees
  - Branch is unique per worktree
```

---

## 8. State Machine/Scheduler/Dependency Responsibility Boundaries

### 8.1 State Machine (Pure Logic)

```
Responsibilities:
  ✓ Defines valid states
  ✓ Defines valid transitions
  ✓ Validates transitions
  ✓ Reports terminal states
  ✓ Reports active states
  ✓ State descriptions

NOT responsible for:
  ✗ Persisting state (that's Persistence)
  ✗ Making transition decisions (that's Orchestrator)
  ✗ Reacting to state changes (that's Orchestrator)
  ✗ I/O operations
```

### 8.2 Scheduler (Pure Logic)

```
Responsibilities:
  ✓ Tracks concurrency slots
  ✓ Determines slot availability
  ✓ Registers issues
  ✓ Claims/releases slots
  ✓ Reports scheduler status
  ✓ Determines ready issues (based on dependency completion)

NOT responsible for:
  ✗ Actually launching agents (that's AgentRuntime)
  ✗ Creating worktrees (that's GitOperations)
  ✗ Persisting state (that's Persistence)
  ✗ Making business decisions (that's Orchestrator)
```

### 8.3 Dependency Resolver (Pure Logic)

```
Responsibilities:
  ✓ Builds DAG from issue dependencies
  ✓ Detects cycles
  ✓ Computes topological sort
  ✓ Groups into concurrency waves
  ✓ Determines ready issues

NOT responsible for:
  ✗ Executing in dependency order (that's Scheduler + Orchestrator)
  ✗ Persisting graph (that's Persistence)
  ✗ Handling failures (that's Orchestrator)
  ✗ Creating git branches (that's GitOperations)
```

### 8.4 Orchestration Flow (Separation of Concerns)

```
Orchestrator (coordinates all components):
  1. Persistence.load_state()           → Load or create state
  2. DependencyGraph.build(issues)      → Build DAG
  3. DependencyGraph.detect_cycle()     → Check for cycles
  4. DependencyGraph.concurrency_groups() → Compute waves
  5. Scheduler.register(issues)         → Register with scheduler
  6. For each wave:
     a. DependencyGraph.ready(completed) → Get ready issues
     b. For each ready issue:
        i.  GitOperations.create_worktree() → Create worktree
        ii. AgentRuntime.launch(task)       → Launch sub-agent
        iii.Scheduler.claim_slot()          → Claim concurrency slot
  7. Monitor loop:
     a. AgentRuntime.poll(handle)        → Check status
     b. If completed: process result
     c. If failed: RetryManager.should_retry() → Decide retry
     d. If retry: RetryManager.prepare_retry() → Set retry state
     e. Persistence.save_state()         → Persist progress
  8. Merge phase:
     a. ApprovalGate.check(pr_number)   → Check approval
     b. MergeQueue.enqueue(approved)     → Queue for merge
     c. MergeQueue.process()             → Serial merge
  9. Cleanup:
     a. GitOperations.remove_worktree() → Remove worktree
     b. Persistence.save_state()         → Final state
```

---

## 9. Test Provider Contract

### 9.1 Purpose

The Test Provider enables CI/CD validation of the Batch Orchestrator without requiring any AI agent service.

### 9.2 Test Provider Behavior

| Scenario | Input | Behavior | Output |
|----------|-------|----------|--------|
| Normal task | Any task | Create minimal change in worktree | success=true, commit, PR |
| No-op task | Description contains "no-op" | No changes made | success=true, no commit |
| Failure task | Description contains "fail" | Exit with error | success=false, error |
| Timeout task | Description contains "timeout" | Sleep then timeout | success=false, timeout error |
| Retry task | Description contains "retry-1" | Fail first, succeed second | success=true after retry |

### 9.3 Test Provider Configuration

```json
{
  "test_provider": {
    "enabled": true,
    "behaviors": {
      "default": "success",
      "patterns": [
        { "match": "no.op|noop", "behavior": "noop" },
        { "match": "fail", "behavior": "fail" },
        { "match": "timeout", "behavior": "timeout", "delay_seconds": 35 },
        { "match": "retry-1", "behavior": "fail_first_succeed_second" }
      ]
    }
  }
}
```

### 9.4 CI Integration

```yaml
# Example CI pipeline
batch-orchestrator-test:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - name: Run Batch Orchestrator Tests
      run: |
        # Unit tests (no external dependencies)
        bash runtime/core/test-state-machine.sh
        bash runtime/core/test-dependency-graph.sh
        bash runtime/core/test-scheduler.sh
        bash runtime/core/test-retry.sh

        # Integration test with Test Provider
        bash runtime/orchestrator.sh test --provider test

    - name: Validate State Transitions
      run: bash runtime/core/test-state-transitions.sh

    - name: Validate Dependency Resolution
      run: bash runtime/core/test-dependency-resolution.sh
```

---

## 10. Migration Plan

### 10.1 Migration Principles

1. **Never break existing functionality** - PowerShell implementation stays working
2. **Add new alongside old** - New runtime lives in `runtime/core/` and `runtime/adapters/`
3. **Test both paths** - Ensure new implementation produces identical results
4. **Gradual cutover** - Switch default only after extensive testing
5. **Preserve all data contracts** - JSON state files are identical

### 10.2 Phase Breakdown

#### Phase 1: Core Logic Extraction (No I/O)

Extract pure functions from PowerShell modules into shell functions:

| Source Module | Target | Functions to Extract |
|---------------|--------|---------------------|
| BatchStateMachine.psm1 | core/state-machine.sh | `valid_transition()`, `is_terminal()`, `get_valid_transitions()` |
| IssueStateMachine.psm1 | core/state-machine.sh | `valid_issue_transition()`, `is_issue_terminal()`, `is_issue_active()` |
| DependencyGraph.psm1 | core/dependency-graph.sh | `new_graph()`, `add_node()`, `add_edge()`, `detect_cycle()`, `topo_sort()`, `concurrency_groups()` |
| BatchScheduler.psm1 | core/scheduler.sh | `new_scheduler()`, `slot_available()`, `register_issue()`, `claim_slot()`, `release_slot()` |
| BatchSubAgent.psm1 | core/retry.sh | `is_retryable()`, `calculate_backoff()`, `categorize_error()` |

**Verification:** Run existing PowerShell tests, then run equivalent shell tests. Compare results.

#### Phase 2: Runtime Layer (I/O Operations)

Create shell-based I/O operations:

| Source Module | Target | Operations |
|---------------|--------|------------|
| BatchPersistence.psm1 | runtime/persistence.sh | `save_state()`, `load_state()`, `save_issues()`, `load_issues()` |
| BatchGitUtilities.psm1 | runtime/git-operations.sh | `create_worktree()`, `remove_worktree()`, `init_environment()` |
| BatchSubAgent.psm1 (launch) | runtime/agent-runtime.sh | `select_provider()`, `launch_task()`, `poll_task()`, `wait_task()` |
| BatchMergeQueue.psm1 | runtime/merge-queue.sh | `enqueue()`, `process_queue()`, `serial_merge()` |
| gh CLI calls | runtime/github-operations.sh | `create_pr()`, `check_approval()`, `get_pr_state()` |

**Verification:** Create worktrees, persist state, launch test provider tasks. Compare with PowerShell.

#### Phase 3: Orchestrator Rewriting

Rewrite `Invoke-BatchOrchestrator.ps1` logic in shell:

| Source | Target | Description |
|--------|--------|-------------|
| Invoke-BatchOrchestrator.ps1 | runtime/orchestrator.sh | Main orchestration loop |
| Invoke-SubAgentWorker.ps1 | Runtime sub-agent prompt | Prompt template (not script) |
| batch.ps1 | batch.sh | Shell entry point |

**Verification:** Run full orchestrator with test provider. Compare state files with PowerShell run.

#### Phase 4: Adapter Implementation

Implement optional CLI adapters:

| Adapter | Source | Description |
|---------|--------|-------------|
| built-in-subagent | New | Contract definition for host sub-agent |
| claude-code | New | `claude -p` wrapper |
| opencode | New | `opencode run` wrapper |
| test | New | Mock provider for CI |
| powershell | Existing | Backward-compatible adapter |

**Verification:** Run orchestrator with each adapter. Verify identical behavior.

#### Phase 5: Documentation and Testing

| Task | Description |
|------|-------------|
| SKILL.md rewrite | Agent-agnostic protocol specification |
| Adapter documentation | Per-adapter README files |
| E2E tests | Test provider + real agent E2E |
| Migration guide | How to switch from PowerShell to shell |

### 10.3 File Structure (Target)

```
skills/common/process/batch/
├── SKILL.md                              # Agent-agnostic protocol spec
├── config/
│   ├── batch-config.json                 # Updated (no pwsh required)
│   └── batch-provider-config.json        # Provider configuration
├── wrapper/
│   ├── batch.sh                          # Shell entry point (NEW)
│   └── batch.ps1                         # PowerShell entry point (kept)
├── runtime/
│   ├── README.md                         # Runtime documentation
│   ├── core/                             # Pure logic (NO I/O)
│   │   ├── state-machine.sh              # Batch + Issue state machines
│   │   ├── dependency-graph.sh           # DAG operations
│   │   ├── scheduler.sh                  # Concurrency management
│   │   ├── retry.sh                      # Retry + backoff
│   │   └── contracts.sh                  # JSON schema validation
│   ├── adapters/                         # Agent runtime adapters
│   │   ├── adapter-interface.json        # Adapter contract schema
│   │   ├── built-in-subagent/
│   │   │   └── adapter.sh               # Host sub-agent contract
│   │   ├── claude-code/
│   │   │   └── adapter.sh               # Claude Code CLI adapter
│   │   ├── opencode/
│   │   │   └── adapter.sh               # OpenCode CLI adapter
│   │   ├── codex/
│   │   │   └── adapter.sh               # Codex CLI adapter
│   │   └── test/
│   │       └── adapter.sh               # Test/mock provider
│   ├── runtime/                          # I/O layer
│   │   ├── persistence.sh               # JSON state I/O
│   │   ├── git-operations.sh            # Worktree/branch ops
│   │   ├── github-operations.sh         # gh CLI wrapper
│   │   ├── merge-queue.sh               # Serial merge
│   │   ├── agent-runtime.sh             # Provider dispatch
│   │   └── orchestrator.sh              # Main orchestrator
│   ├── powershell/                       # Existing (backward compat)
│   │   ├── Modules/
│   │   └── Scripts/
│   └── tests/                            # Tests
│       ├── test-state-machine.sh
│       ├── test-dependency-graph.sh
│       ├── test-scheduler.sh
│       ├── test-retry.sh
│       ├── test-persistence.sh
│       └── test-e2e.sh
├── docs/
│   ├── BATCH_PROCESSING.md
│   └── BATCH_ORCHESTRATOR_PHASE0_DESIGN.md  # This document
└── E2E_TEST.md                           # E2E test results
```

### 10.4 Dependency Graph (Migration)

```
Phase 1 (Core Logic)
  ├── state-machine.sh     ← BatchStateMachine + IssueStateMachine
  ├── dependency-graph.sh  ← DependencyGraph
  ├── scheduler.sh         ← BatchScheduler
  ├── retry.sh             ← BatchSubAgent (retry parts)
  └── contracts.sh         ← JSON schemas (new)
         │
Phase 2 (Runtime Layer)
  ├── persistence.sh       ← BatchPersistence
  ├── git-operations.sh    ← BatchGitUtilities
  ├── github-operations.sh ← gh calls from BatchPersistence + SubAgent
  ├── merge-queue.sh       ← BatchMergeQueue
  ├── agent-runtime.sh     ← BatchSubAgent (launch parts)
  └── orchestrator.sh      ← Invoke-BatchOrchestrator
         │
Phase 3 (Adapters)
  ├── built-in-subagent/   ← New (contract only)
  ├── test/                ← New (mock)
  ├── claude-code/         ← New (optional)
  ├── opencode/            ← New (optional)
  └── codex/               ← New (optional)
         │
Phase 4 (Entry Points)
  ├── batch.sh             ← New (replaces batch.ps1 as primary)
  └── batch.ps1            ← Kept (backward compat)
```

---

## 11. Open Questions and Decisions

### Q1: Should the orchestrator itself be a shell script or a SKILL.md-driven agent behavior?

**Current thinking:** The orchestrator should be a **SKILL.md-driven agent behavior**. The SKILL.md defines the protocol, and the host agent follows it. The shell scripts (`orchestrator.sh`, etc.) serve as:
- Reference implementation for environments without a capable host agent
- Test harness for CI/CD
- Fallback when host agent doesn't have sub-agent capabilities

**Decision needed:** Confirm this dual-mode approach.

### Q2: How does the orchestrator handle the case where the host agent has no sub-agent capability?

**Current thinking:** Fall back to CLI adapter. If no CLI adapter is available either, the orchestrator cannot run. This is documented as a prerequisite.

**Decision needed:** Should we provide a "sequential single-task" mode that runs tasks one at a time through the host agent directly (no sub-agent)?

### Q3: Should JSON state files use a specific schema version?

**Current thinking:** Yes. Add `$schema` and `version` fields to all state files. This enables forward-compatible migration.

**Decision needed:** Initial schema version number.

### Q4: How does the shell orchestrator handle JSON parsing without jq dependency?

**Current thinking:** Require `jq` as a dependency alongside `git`. It's widely available and the JSON operations are non-trivial.

**Decision needed:** Confirm jq as a dependency, or implement minimal JSON parser in bash.

---

## 12. Functional Preservation Checklist

The following existing features MUST be preserved in the new architecture:

| Feature | Current Location | Migration Target | Status |
|---------|-----------------|-----------------|--------|
| Batch state machine (9 states) | BatchStateMachine.psm1 | core/state-machine.sh | Preserve |
| Issue state machine (13 states) | IssueStateMachine.psm1 | core/state-machine.sh | Preserve |
| DAG construction | DependencyGraph.psm1 | core/dependency-graph.sh | Preserve |
| Cycle detection (DFS) | DependencyGraph.psm1 | core/dependency-graph.sh | Preserve |
| Topological sort (Kahn's) | DependencyGraph.psm1 | core/dependency-graph.sh | Preserve |
| Concurrency groups | DependencyGraph.psm1 | core/dependency-graph.sh | Preserve |
| Concurrency limit | BatchScheduler.psm1 | core/scheduler.sh | Preserve |
| Slot management | BatchScheduler.psm1 | core/scheduler.sh | Preserve |
| Exponential backoff + jitter | BatchSubAgent.psm1 | core/retry.sh | Preserve |
| Error categorization | BatchSubAgent.psm1 | core/retry.sh | Preserve |
| Retryable/non-retryable classification | BatchSubAgent.psm1 | core/retry.sh | Preserve |
| Worktree creation | BatchGitUtilities.psm1 | runtime/git-operations.sh | Preserve |
| Worktree collision detection | BatchGitUtilities.psm1 | runtime/git-operations.sh | Preserve |
| Worktree removal | BatchGitUtilities.psm1 | runtime/git-operations.sh | Preserve |
| Environment initialization | BatchGitUtilities.psm1 | runtime/git-operations.sh | Preserve |
| JSON state persistence | BatchPersistence.psm1 | runtime/persistence.sh | Preserve |
| GitHub state sync | BatchPersistence.psm1 | runtime/persistence.sh | Preserve |
| Resume from persisted state | Invoke-BatchOrchestrator.ps1 | runtime/orchestrator.sh | Preserve |
| Serial merge queue | BatchMergeQueue.psm1 | runtime/merge-queue.sh | Preserve |
| SHA-bound approval | Invoke-BatchOrchestrator.ps1 | runtime/orchestrator.sh | Preserve |
| PR approval gate | Invoke-BatchOrchestrator.ps1 | runtime/orchestrator.sh | Preserve |
| Failure isolation | Invoke-BatchOrchestrator.ps1 | runtime/orchestrator.sh | Preserve |
| Admin bypass prohibition | config/batch-config.json | config/batch-config.json | Preserve |
| Force push prohibition | config/batch-config.json | config/batch-config.json | Preserve |
| Structured sub-agent report | BatchSubAgent.psm1 | contracts (result schema) | Preserve |

---

*End of Phase 0 Design Document*
