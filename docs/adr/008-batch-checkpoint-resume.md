# ADR-008: Batch Orchestrator Checkpoint and Resume Design

- **Status**: Superseded by Issue #242
- **Date**: 2026-08-27
- **Issue**: #170

> **Superseded.** This ADR recorded the checkpoint/resume design of the Batch
> Orchestrator *runtime* (checkpoint files, JSON schemas, PowerShell modules,
> atomic-write mechanics). That runtime has been removed; batch orchestration is
> now a Markdown-only, agent-agnostic protocol.
>
> The *decision* this ADR captured — that an interrupted batch must be resumable
> without duplicate work or duplicate dispatch, that orphaned workers are
> detected and either retried within budget or failed, and that unreadable state
> fails closed rather than being treated as absent — is preserved in
> [`../../skills/common/process/batch/references/failure-recovery.md`](../../skills/common/process/batch/references/failure-recovery.md).
> What no longer applies is the prescribed file layout and schema: the protocol
> requires that resume state be re-establishable, not that it be stored in any
> particular format.
>
> Retained as a historical record. The file paths and module names below refer
> to assets that no longer exist.

## Context

The Batch Orchestrator executes multiple issues in parallel with dependency scheduling. Runs can be interrupted by:
- Token limit exceeded (LLM provider)
- Process timeout
- Process failure (crash, OOM, signal)
- Orchestrator interruption (SIGTERM, host failure, CI cancellation)

Without checkpoint/resume, interrupted runs require full re-execution, causing:
- Duplicate work and API costs
- Inconsistent state (some issues completed, others not)
- Lost progress on long-running issues

## Decision

### 1. Persist Checkpoints
Save checkpoints at key lifecycle transitions:
- Batch checkpoint: overall batch state, counts, worker summaries
- Worker checkpoint: per-issue runtime state (phases, commits, PR, retries, provider metadata)
- Transition log: audit trail of all state changes (JSONL)

### 2. Provider-Neutral Checkpoint Schema
Core checkpoint fields are independent of any specific agent provider:
- `schemaVersion`: for forward/backward compatibility
- `issueId`, `issueNumber`, `description`: issue identity
- `lifecycleState`: `PENDING` → `RUNNING` → `SUCCESS`/`FAILED`/etc.
- `completedPhases`: `agent_completed`, `commit`, `push`, `pr_created`
- `branch`, `baseCommit`, `currentCommit`, `resultCommit`: git state
- `prNumber`, `prState`: PR tracking
- `retryCount`, `maxRetries`, `lastRetryAt`: retry budget
- `providerMetadata`: extensible provider-specific data (isolated)

### 3. Lifecycle State Persistence
Worker checkpoint `lifecycleState` maps from IssueStateMachine:
- `SUBAGENT_STARTING`/`SUBAGENT_RUNNING` → `RUNNING`
- `ORPHANED` → `ORPHANED`
- `PR_READY` → `RUNNING` (needs approval/merge)
- `COMPLETED` → `SUCCESS`
- `SUBAGENT_FAILED` → `FAILED`

### 4. ORPHANED Detection and Recovery
- On resume, detect processes that died without writing result.json
- `Test-OrphanedProcess` checks process liveness and result file validity
- ORPHANED issues transition to `WAITING_FOR_SUBAGENT` with incremented `retryCount`
- Retry budget (`maxRetries`) persists in checkpoint and continues across restarts

### 5. Retry Budget Continuation
- `retryCount` and `maxRetries` saved in worker checkpoint
- On resume, `Test-SubAgentRetryable` observes persisted count
- Exhausted retries → `SUBAGENT_FAILED` (NOT added to `completedIssues`)

### 6. Idempotency Protection
- On dispatch, check for existing PR via `Test-GitPrExists` (runs regardless of branch existence)
- If open PR found → transition to `PR_READY`, skip worker launch
- On commit/push, validate `$LASTEXITCODE` and resolved commit SHA

### 7. Atomic Persistence
- All writes use temp-file + `Move-Item -Force` pattern
- `Save-AtomicJson` for JSON files
- `Add-Content -ErrorAction Stop` for transition log
- Parent directories created automatically

### 8. Corrupt Persistence = Fail-Closed
- `Get-BatchState`/`Get-IssueStates` throw on JSON parse failure for existing files
- Missing files return `$null` (new run)
- Prevents silent progress loss and duplicate dispatch

### 9. Safe Filename Generation (Injective Encoding)
- Safe IssueIds (`^[a-zA-Z0-9_-]+$`) used directly
- Unsafe IssueIds encoded as `~` + uppercase hex of UTF-8 bytes
- Guarantees injective mapping: distinct IssueIds → distinct filenames
- Rejects blank/whitespace IssueIds before filename generation

### 10. BatchId / IssueId Validation
- Reject empty, whitespace-only, path separators (`/` `\`), and `..` traversal
- Applied at all path construction points (checkpoint dir, state file, transition log)

## Consequences

### Positive
- Resume capability after any interruption type
- Provider-agnostic: new providers reuse core schema
- Safety against persistence corruption and filename collision
- Audit trail enables debugging and compliance
- Retry budget survives orchestrator restarts

### Negative
- Additional disk I/O for checkpoint writes
- Checkpoint directory grows with batch size (cleanup on completion)
- Must maintain schema version compatibility

## Alternatives Considered

### No Checkpoint (Full Re-execution)
- Simpler implementation
- Rejected: unacceptable cost/duplicate work for long batches

### Provider-Specific Checkpoints
- Each provider defines own schema
- Rejected: blocks provider-neutral resume, duplicates core logic

### Legacy State Only (batch-state.json + issue-states.json)
- No worker-level detail (phases, commits, retries)
- Rejected: insufficient for accurate resume; loses retry budget, phase progress

### Database Backend
- SQLite/PostgreSQL for state
- Rejected: adds deployment complexity; file-based is sufficient for CI/CD scale

### Lossy Filename Sanitization (char replacement)
- Replace unsafe chars with `_`
- Rejected: collision between `a/b` and `a_2Fb`; not injective

### Skip PR Existence Check When Branch Exists
- Only check PR when branch missing
- Rejected: misses case where worker created PR then crashed before result persisted

## Related ADRs
- ADR-007: Repository Artifact Policy (atomic writes pattern)
- Batch Skill documentation: `skills/common/process/batch/SKILL.md`