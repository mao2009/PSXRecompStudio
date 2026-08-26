#Requires -Version 7.0

<#
.SYNOPSIS
    Sub-agent handler for Batch Skill processing.

.DESCRIPTION
    Handles individual Issue implementation, testing, and PR creation
    within a dedicated Worktree.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

param(
    [Parameter(Mandatory = $true)]
    [int]$IssueNumber,

    [Parameter(Mandatory = $true)]
    [string]$WorktreePath,

    [Parameter(Mandatory = $true)]
    [string]$BranchName,

    [Parameter(Mandatory = $false)]
    [string]$StateFile
)

# Import modules using cross-platform path construction
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "BatchStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "BatchGitUtilities.psm1") -Force

# Default state file path
if (-not $StateFile) {
    $StateFile = ".batch-state-$IssueNumber.json"
}

function Get-BatchState {
    <#
    .SYNOPSIS
        Gets the current state for an Issue.
    #>
    [CmdletBinding()]
    param()

    if (Test-Path $StateFile) {
        return Get-Content $StateFile | ConvertFrom-Json -AsHashtable
    }

    return $null
}

function Save-BatchState {
    <#
    .SYNOPSIS
        Saves the current state for an Issue.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$State
    )

    $State | ConvertTo-Json -Depth 10 | Set-Content -Path $StateFile
}

function Invoke-BatchSubAgent {
    <#
    .SYNOPSIS
        Main Sub-agent function.
    #>
    [CmdletBinding()]
    param()

    Write-Host "=== Batch Sub-agent ===" -ForegroundColor Green
    Write-Host "Issue: #$IssueNumber" -ForegroundColor Cyan
    Write-Host "Worktree: $WorktreePath" -ForegroundColor Cyan
    Write-Host "Branch: $BranchName" -ForegroundColor Cyan
    Write-Host ""

    # Verify Worktree
    $worktreeInfo = Get-BatchWorktree -WorktreePath $WorktreePath
    if ($null -eq $worktreeInfo) {
        Write-Host "Worktree not found: $WorktreePath" -ForegroundColor Red
        return
    }

    Write-Host "Worktree info:" -ForegroundColor Gray
    Write-Host "  Branch: $($worktreeInfo.Branch)" -ForegroundColor Gray
    Write-Host "  Commit: $($worktreeInfo.Commit)" -ForegroundColor Gray
    Write-Host "  Dirty: $($worktreeInfo.IsDirty)" -ForegroundColor Gray
    Write-Host ""

    # Load state
    $state = Get-BatchState
    if ($null -eq $state) {
        Write-Host "State file not found: $StateFile" -ForegroundColor Red
        return
    }

    Write-Host "Current State: $($state.State)" -ForegroundColor Yellow
    Write-Host ""

    try {
        switch ($state.State) {
            "REPORTING" {
                Write-Host "Creating implementation report..." -ForegroundColor Cyan

                # Generate report template
                $report = @"
## Summary
[What was implemented]

## Investigation
[What was investigated]

## Design Decision
[Why this design/implementation was chosen]

## Changes
[What was specifically changed]

## Tests
[What was executed and results]

## Risks / Limitations
[Remaining issues and constraints]

## Related Issues
[Related Issues / PRs]

## Verification
[Evidence that Issue requirements are met]
"@

                $reportPath = Join-Path $WorktreePath "IMPLEMENTATION_REPORT.md"
                Set-Content -Path $reportPath -Value $report

                Write-Host "Report template created: $reportPath" -ForegroundColor Green
                Write-Host "Please fill in the report before creating PR" -ForegroundColor Yellow

                $state.State = "PR_OPEN"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                Save-BatchState -State $state
            }

            "PR_OPEN" {
                Write-Host "Creating Pull Request..." -ForegroundColor Cyan

                # Check for report
                $reportPath = Join-Path $WorktreePath "IMPLEMENTATION_REPORT.md"
                if (-not (Test-Path $reportPath)) {
                    Write-Host "Implementation report not found. Please create report first." -ForegroundColor Red
                    return
                }

                $report = Get-Content $reportPath -Raw

                # Create PR using gh
                $prArgs = @(
                    "pr", "create",
                    "--title", "Issue #$IssueNumber: $Description",
                    "--body", $report,
                    "--base", "main",
                    "--head", $BranchName
                )

                & gh @prArgs

                if ($LASTEXITCODE -eq 0) {
                    Write-Host "PR created successfully" -ForegroundColor Green

                    # Get PR number
                    $prInfo = gh pr view --json number 2>$null | ConvertFrom-Json
                    if ($prInfo) {
                        $state.PrNumber = $prInfo.number
                    }

                    $state.State = "AWAITING_APPROVAL"
                    $state.CurrentCommitSha = $worktreeInfo.Commit
                    $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                    Save-BatchState -State $state
                } else {
                    Write-Host "Failed to create PR" -ForegroundColor Red
                }
            }

            "CONFLICT_RESOLUTION" {
                Write-Host "Resolving conflicts..." -ForegroundColor Cyan

                # Check for conflicts
                $originalLocation = Get-Location
                try {
                    Set-Location $WorktreePath

                    $conflictFiles = git diff --name-only --diff-filter=U
                    if ($conflictFiles.Count -eq 0) {
                        Write-Host "No conflicts found" -ForegroundColor Green
                        $state.State = "REPORTING"
                        $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                        Save-BatchState -State $state
                        return
                    }

                    Write-Host "Conflict files:" -ForegroundColor Yellow
                    $conflictFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }

                    # Generate conflict resolution report
                    $report = @"
## Conflict Resolution

### Conflicts Detected
$($conflictFiles -join "`n")

### Cause
[Why conflicts occurred]

### Resolution
[How conflicts were resolved]

### Verification
[What was verified after resolution]
"@

                    $reportPath = Join-Path $WorktreePath "CONFLICT_RESOLUTION_REPORT.md"
                    Set-Content -Path $reportPath -Value $report

                    Write-Host "Conflict resolution report created: $reportPath" -ForegroundColor Green
                    Write-Host "Please resolve conflicts and update report" -ForegroundColor Yellow

                } finally {
                    Set-Location $originalLocation
                }
            }

            default {
                Write-Host "Sub-agent cannot handle state: $($state.State)" -ForegroundColor Red
            }
        }
    } catch {
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host $_.ScriptStackTrace -ForegroundColor Gray
    }
}

# Run Sub-agent
Invoke-BatchSubAgent
