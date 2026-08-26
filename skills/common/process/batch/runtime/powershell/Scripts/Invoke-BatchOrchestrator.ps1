#Requires -Version 7.0

<#
.SYNOPSIS
    Orchestrator for Batch Skill processing.

.DESCRIPTION
    Manages the lifecycle of Issue-driven development, including Worktree
    creation, Sub-agent delegation, approval gates, rebase, and cleanup.

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
    [string]$Description,

    [Parameter(Mandatory = $false)]
    [string]$BaseRef = "main",

    [Parameter(Mandatory = $false)]
    [string]$WorktreeRoot = "../worktrees",

    [Parameter(Mandatory = $false)]
    [string]$StateFile
)

# Import modules using cross-platform path construction
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "BatchStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "BatchGitUtilities.psm1") -Force
Import-Module (Join-Path $modulePath "BatchEnvironment.psm1") -Force
Import-Module (Join-Path $modulePath "BatchApproval.psm1") -Force

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

function Invoke-BatchOrchestration {
    <#
    .SYNOPSIS
        Main orchestration function.
    #>
    [CmdletBinding()]
    param()

    Write-Host "=== Batch Orchestrator ===" -ForegroundColor Green
    Write-Host "Issue: #$IssueNumber" -ForegroundColor Cyan
    Write-Host "Description: $Description" -ForegroundColor Cyan
    Write-Host ""

    # Load or create state
    $state = Get-BatchState
    if ($null -eq $state) {
        $state = @{
            IssueNumber = $IssueNumber
            Description = $Description
            State = "INVESTIGATING"
            BranchName = $null
            WorktreePath = $null
            PrNumber = $null
            Approval = $null
            CurrentCommitSha = $null
            MainHeadSha = $null
            CreatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
            UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        }
    }

    Write-Host "Current State: $($state.State)" -ForegroundColor Yellow
    Write-Host ""

    try {
        switch ($state.State) {
            "INVESTIGATING" {
                # Create Worktree
                Write-Host "Creating Worktree..." -ForegroundColor Cyan
                $worktree = New-BatchWorktree -IssueNumber $IssueNumber -Description $Description -BaseRef $BaseRef -WorktreeRoot $WorktreeRoot

                $state.BranchName = $worktree.BranchName
                $state.WorktreePath = $worktree.WorktreePath
                $state.State = "IMPLEMENTING"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")

                Save-BatchState -State $state

                Write-Host "Worktree created: $($worktree.WorktreePath)" -ForegroundColor Green
                Write-Host "Branch: $($worktree.BranchName)" -ForegroundColor Green
            }

            "IMPLEMENTING" {
                # Initialize environment
                Write-Host "Initializing environment..." -ForegroundColor Cyan
                $envResult = Initialize-BatchEnvironment -WorktreePath $state.WorktreePath

                if (-not $envResult.Success) {
                    Write-Host "Environment initialization failed:" -ForegroundColor Red
                    $envResult.Failed | ForEach-Object { Write-Host "  - $($_.File): $($_.Error)" -ForegroundColor Red }
                    return
                }

                Write-Host "Environment initialized:" -ForegroundColor Green
                Write-Host "  Copied: $($envResult.Copied.Count) files" -ForegroundColor Gray
                Write-Host "  Generated: $($envResult.Generated.Count) files" -ForegroundColor Gray

                # Check for secrets
                $secretsCheck = Test-BatchEnvironmentSecrets -WorktreePath $state.WorktreePath
                if (-not $secretsCheck.Success) {
                    Write-Host "WARNING: Secret files detected in Git:" -ForegroundColor Red
                    $secretsCheck.SecretsFound | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
                }

                $state.State = "REPORTING"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")

                Save-BatchState -State $state

                Write-Host "Ready for Sub-agent to implement" -ForegroundColor Green
            }

            "AWAITING_APPROVAL" {
                Write-Host "Waiting for user approval..." -ForegroundColor Yellow
                Write-Host "Current commit: $($state.CurrentCommitSha)" -ForegroundColor Gray
                Write-Host "Main HEAD: $($state.MainHeadSha)" -ForegroundColor Gray

                # Check if approval exists and is valid
                if ($state.Approval) {
                    $approvalValid = Test-BatchApprovalValid -Approval $state.Approval -CurrentCommitSha $state.CurrentCommitSha -CurrentMainHeadSha $state.MainHeadSha

                    if ($approvalValid.IsValid) {
                        Write-Host "Approval is valid" -ForegroundColor Green
                        $state.State = "REBASE"
                        $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                        Save-BatchState -State $state
                    } else {
                        Write-Host "Approval is invalid:" -ForegroundColor Red
                        $approvalValid.Reasons | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
                    }
                }
            }

            "REBASE" {
                Write-Host "Performing mandatory rebase..." -ForegroundColor Cyan

                $rebaseResult = Invoke-BatchRebase -WorktreePath $state.WorktreePath

                if ($rebaseResult.Success) {
                    Write-Host "Rebase succeeded" -ForegroundColor Green
                    $state.State = "VALIDATING"
                } else {
                    Write-Host "Rebase failed" -ForegroundColor Red

                    if ($rebaseResult.HasConflicts) {
                        Write-Host "Conflicts detected:" -ForegroundColor Yellow
                        $rebaseResult.ConflictFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }

                        $state.State = "CONFLICT_RESOLUTION"
                    } else {
                        Write-Host "Rebase failed without conflicts" -ForegroundColor Red
                        return
                    }
                }

                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                Save-BatchState -State $state
            }

            "CONFLICT_RESOLUTION" {
                Write-Host "Conflict resolution required" -ForegroundColor Yellow
                Write-Host "Sub-agent must resolve conflicts in: $($state.WorktreePath)" -ForegroundColor Yellow
                Write-Host "Branch: $($state.BranchName)" -ForegroundColor Yellow
            }

            "VALIDATING" {
                Write-Host "Validating changes..." -ForegroundColor Cyan

                # Run tests
                Write-Host "Running tests..." -ForegroundColor Gray

                # Get current commit
                $originalLocation = Get-Location
                try {
                    Set-Location $state.WorktreePath
                    $state.CurrentCommitSha = git rev-parse HEAD
                } finally {
                    Set-Location $originalLocation
                }

                # Get main HEAD
                $state.MainHeadSha = Get-BatchMainHead

                $state.State = "MERGING"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                Save-BatchState -State $state

                Write-Host "Validation passed" -ForegroundColor Green
            }

            "MERGING" {
                Write-Host "Merging into main..." -ForegroundColor Cyan

                $mergeResult = Confirm-BatchMerge -WorktreePath $state.WorktreePath -BranchName $state.BranchName

                if ($mergeResult.Success) {
                    Write-Host "Merge succeeded" -ForegroundColor Green
                    $state.State = "CLEANUP"
                } else {
                    Write-Host "Merge failed" -ForegroundColor Red
                    return
                }

                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                Save-BatchState -State $state
            }

            "CLEANUP" {
                Write-Host "Cleaning up..." -ForegroundColor Cyan

                # Verify merge
                if ($state.PrNumber) {
                    $merged = Test-BatchPrMerged -PrNumber $state.PrNumber
                    if (-not $merged) {
                        Write-Host "PR not merged. Skipping cleanup." -ForegroundColor Red
                        return
                    }
                }

                # Remove Worktree
                Remove-BatchWorktree -WorktreePath $state.WorktreePath -BranchName $state.BranchName

                $state.State = "COMPLETED"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                Save-BatchState -State $state

                Write-Host "Cleanup completed" -ForegroundColor Green
            }

            "COMPLETED" {
                Write-Host "Issue #$IssueNumber completed!" -ForegroundColor Green
            }

            default {
                Write-Host "Unknown state: $($state.State)" -ForegroundColor Red
            }
        }
    } catch {
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host $_.ScriptStackTrace -ForegroundColor Gray
    }
}

# Run orchestration
Invoke-BatchOrchestration
