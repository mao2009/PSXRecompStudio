# Batch Skill Runtime: PowerShell Core 7.x

This directory contains the PowerShell Core 7.x implementation of the Batch Skill runtime.

## Requirements

- PowerShell Core 7.0 or later
- Git CLI
- GitHub CLI (`gh`)

## Installation

### Windows

```powershell
winget install Microsoft.PowerShell
```

### Linux

```bash
# Ubuntu/Debian
sudo apt-get install -y powershell

# Fedora/RHEL
sudo dnf install -y powershell
```

### macOS

```bash
brew install powershell
```

## Usage

### Using the Wrapper (Recommended)

The wrapper script provides a simple interface:

```powershell
# Start orchestration for an Issue
.\wrapper\batch.ps1 orchestrate -IssueNumber 145 -Description "batch-orchestration"

# Run Sub-agent in Worktree
.\wrapper\batch.ps1 subagent -IssueNumber 145 -WorktreePath "../worktrees/145-batch-orchestration" -BranchName "issue/145-batch-orchestration"
```

### Using the Scripts Directly

```powershell
# Import modules
Import-Module .\runtime\powershell\Modules\BatchStateMachine.psm1
Import-Module .\runtime\powershell\Modules\BatchGitUtilities.psm1
Import-Module .\runtime\powershell\Modules\BatchEnvironment.psm1
Import-Module .\runtime\powershell\Modules\BatchApproval.psm1

# Run Orchestrator
.\runtime\powershell\Scripts\Invoke-BatchOrchestrator.ps1 -IssueNumber 145 -Description "batch-orchestration"

# Run Sub-agent
.\runtime\powershell\Scripts\Invoke-BatchSubAgent.ps1 -IssueNumber 145 -WorktreePath "../worktrees/145-batch-orchestration" -BranchName "issue/145-batch-orchestration"
```

## Modules

### BatchStateMachine.psm1

State machine definitions and transitions.

```powershell
# Get all states
Get-AllStates

# Test if a transition is valid
Test-ValidTransition -FromState "INVESTIGATING" -ToState "IMPLEMENTING"

# Get valid transitions from a state
Get-ValidTransitions -State "REBASE"
```

### BatchGitUtilities.psm1

Git Worktree and Branch management utilities.

```powershell
# Create a new Worktree
$worktree = New-BatchWorktree -IssueNumber 145 -Description "batch-orchestration"

# Remove a Worktree
Remove-BatchWorktree -WorktreePath $worktree.WorktreePath -BranchName $worktree.BranchName

# Get Worktree info
$info = Get-BatchWorktree -WorktreePath $worktree.WorktreePath

# Perform rebase
$result = Invoke-BatchRebase -WorktreePath $worktree.WorktreePath
```

### BatchEnvironment.psm1

Environment initialization utilities.

```powershell
# Initialize environment
$result = Initialize-BatchEnvironment -WorktreePath $worktree.WorktreePath

# Check for secrets
$secrets = Test-BatchEnvironmentSecrets -WorktreePath $worktree.WorktreePath
```

### BatchApproval.psm1

Approval state tracking.

```powershell
# Create approval
$approval = New-BatchApproval -IssueNumber 145 -CommitSha "abc123" -MainHeadSha "def456"

# Validate approval
$result = Test-BatchApprovalValid -Approval $approval -CurrentCommitSha "abc123" -CurrentMainHeadSha "def456"

# Invalidate approval
$approval = Invalidate-BatchApproval -Approval $approval -Reason "Rebase changed content"
```

## Tests

Run the tests:

```powershell
pwsh -File .\runtime\powershell\Tests\Test-BatchSkill.ps1
```

## Configuration

The PowerShell runtime reads configuration from `config/batch-config.json`.

## Cross-Platform Notes

This runtime is designed to work on:

- Windows (PowerShell Core)
- Linux (PowerShell Core)
- macOS (PowerShell Core)

All path operations use `Join-Path` and `Test-Path` for cross-platform compatibility.

## Backward Compatibility

The `wrapper/batch.ps1` script provides backward compatibility for users who were using the previous PowerShell-only implementation.
