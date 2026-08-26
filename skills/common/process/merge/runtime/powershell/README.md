# PR Merge Skill: PowerShell Runtime

PowerShell Core 7.x runtime implementation for the PR Merge Skill.

## Requirements

- PowerShell Core 7.0 or later
- Git
- GitHub CLI (`gh`)

## Usage

### Wrapper Script

```powershell
# Merge a PR
.\wrapper\merge.ps1 merge -PrNumber 149

# With optional parameters
.\wrapper\merge.ps1 merge -PrNumber 149 -IssueNumber 148 -WorktreePath "../worktrees/148-e2e-test" -BranchName "issue/148-e2e-test"

# Run tests
.\wrapper\merge.ps1 test
```

### Direct Script Invocation

```powershell
# Run orchestrator directly
.\runtime\powershell\Scripts\Invoke-MergeOrchestrator.ps1 -PrNumber 149
```

## Modules

### MergeStateMachine.psm1

State machine definitions for the merge lifecycle.

### MergeGitUtilities.psm1

Git operations including rebase, merge verification, and branch management.

### MergeApproval.psm1

Approval state tracking and validation.

## Configuration

Project-specific configuration is in `config/merge-config.json`.

## Testing

Run unit tests:

```powershell
.\wrapper\merge.ps1 test
```

Or directly:

```powershell
.\runtime\powershell\Tests\Test-MergeSkill.ps1
```
