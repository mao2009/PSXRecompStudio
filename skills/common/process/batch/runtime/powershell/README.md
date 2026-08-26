# Batch Orchestrator Skill: PowerShell Runtime

PowerShell Core 7.x runtime implementation for the Batch Orchestrator Skill.

## Requirements

- PowerShell Core 7.0 or later
- Git
- GitHub CLI (`gh`)
- PR Merge Skill (for merge delegation)

## Usage

### Wrapper Script

```powershell
# Run batch with issues file
.\wrapper\batch.ps1 run -BatchId my-batch -IssuesFile issues.json

# Run with inline issue IDs
.\wrapper\batch.ps1 run -BatchId my-batch -IssueIds @("140","141","142")

# Resume after crash
.\wrapper\batch.ps1 resume -BatchId my-batch

# Check status
.\wrapper\batch.ps1 status -BatchId my-batch

# Run tests
.\wrapper\batch.ps1 test
```

### Direct Script Invocation

```powershell
.\runtime\powershell\Scripts\Invoke-BatchOrchestrator.ps1 -BatchId my-batch -IssueIds @("140","141")
```

## Modules

### BatchStateMachine.psm1

Batch-level state machine (9 states).

### IssueStateMachine.psm1

Per-Issue state machine (13 states).

### DependencyGraph.psm1

DAG construction, cycle detection, topological sort, concurrency groups.

### BatchScheduler.psm1

Concurrency limit management and parallel dispatch.

### BatchSubAgent.psm1

Sub-agent lifecycle, retry with backoff, failure categorization.

### BatchGitUtilities.psm1

Worktree creation, Branch management, environment initialization.

### BatchMergeQueue.psm1

Serial merge queue via Merge Skill.

### BatchPersistence.psm1

State save/load for crash recovery and resume.

## Configuration

Project-specific configuration in `config/batch-config.json`.

## Testing

Run unit tests:

```powershell
.\wrapper\batch.ps1 test
```

Or directly:

```powershell
.\runtime\powershell\Tests\Test-BatchSkill.ps1
```
