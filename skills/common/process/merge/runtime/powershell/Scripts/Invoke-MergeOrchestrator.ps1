#Requires -Version 7.0

<#
.SYNOPSIS
    Orchestrator for PR Merge Skill.

.DESCRIPTION
    Manages the lifecycle of PR merging, including approval validation,
    mandatory rebase, validation, normal merge, and cleanup.
    Enforces strict safety conditions and prevents admin bypass.

.NOTES
    Version: 1.0.0
    Issue: #146
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

param(
    [Parameter(Mandatory = $true)]
    [int]$PrNumber,

    [Parameter(Mandatory = $false)]
    [int]$IssueNumber,

    [Parameter(Mandatory = $false)]
    [string]$WorktreePath,

    [Parameter(Mandatory = $false)]
    [string]$BranchName,

    [Parameter(Mandatory = $false)]
    [string]$Repository,

    [Parameter(Mandatory = $false)]
    [string]$StateFile
)

# Import modules using cross-platform path construction
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "MergeStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "MergeGitUtilities.psm1") -Force
Import-Module (Join-Path $modulePath "MergeApproval.psm1") -Force

# Default state file path
if (-not $StateFile) {
    $StateFile = ".merge-state-$PrNumber.json"
}

function Get-MergeStateFile {
    <#
    .SYNOPSIS
        Gets the current state for a PR.
    #>
    [CmdletBinding()]
    param()

    if (Test-Path $StateFile) {
        return Get-Content $StateFile | ConvertFrom-Json -AsHashtable
    }

    return $null
}

function Save-MergeState {
    <#
    .SYNOPSIS
        Saves the current state for a PR.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$State
    )

    $State | ConvertTo-Json -Depth 10 | Set-Content -Path $StateFile
}

function Invoke-MergeOrchestration {
    <#
    .SYNOPSIS
        Main orchestration function.
    #>
    [CmdletBinding()]
    param()

    Write-Host "=== PR Merge Orchestrator ===" -ForegroundColor Green
    Write-Host "PR: #$PrNumber" -ForegroundColor Cyan
    if ($IssueNumber) {
        Write-Host "Issue: #$IssueNumber" -ForegroundColor Cyan
    }
    Write-Host ""

    # Load or create state
    $state = Get-MergeStateFile
    if ($null -eq $state) {
        $state = @{
            PrNumber = $PrNumber
            IssueNumber = $IssueNumber
            BranchName = $BranchName
            WorktreePath = $WorktreePath
            State = "TRIGGER_CHECK"
            CurrentCommitSha = $null
            ApprovedCommitSha = $null
            MainHeadSha = $null
            Approval = $null
            CreatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
            UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        }
    }

    Write-Host "Current State: $($state.State)" -ForegroundColor Yellow
    Write-Host ""

    try {
        switch ($state.State) {
            "TRIGGER_CHECK" {
                Write-Host "=== Trigger Check ===" -ForegroundColor Cyan

                # Check if PR exists
                $prInfo = Get-MergePrInfo -PrNumber $PrNumber -Repository $Repository
                if ($null -eq $prInfo) {
                    Write-Host "PR #$PrNumber not found" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "PR not found"
                    Save-MergeState -State $state
                    return
                }

                Write-Host "PR: $($prInfo.title)" -ForegroundColor Gray
                Write-Host "State: $($prInfo.state)" -ForegroundColor Gray
                Write-Host "Target: $($prInfo.baseRefName)" -ForegroundColor Gray

                # Check if target is main
                if ($prInfo.baseRefName -ne "main") {
                    Write-Host "Target branch is not main (actual: $($prInfo.baseRefName))" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "Target branch is not main"
                    Save-MergeState -State $state
                    return
                }

                # Check if PR is open
                if ($prInfo.state -ne "OPEN") {
                    Write-Host "PR is not open (state: $($prInfo.state))" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "PR is not open"
                    Save-MergeState -State $state
                    return
                }

                # Check if PR is draft
                if ($prInfo.isDraft) {
                    Write-Host "PR is a draft" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "PR is a draft"
                    Save-MergeState -State $state
                    return
                }

                # Update state with PR info
                $state.BranchName = $prInfo.headRefName
                if (-not $state.IssueNumber) {
                    # Try to extract Issue number from branch name
                    if ($prInfo.headRefName -match 'issue/(\d+)') {
                        $state.IssueNumber = [int]$Matches[1]
                    }
                }

                Write-Host "Preconditions met" -ForegroundColor Green
                $state.State = "APPROVAL_VALIDATION"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "APPROVAL_VALIDATION" {
                Write-Host "=== Approval Validation ===" -ForegroundColor Cyan

                # Get current commit SHA
                if ($state.WorktreePath -and (Test-Path $state.WorktreePath)) {
                    $state.CurrentCommitSha = Get-MergeCurrentCommit -WorktreePath $state.WorktreePath
                } else {
                    Write-Host "Worktree path not provided or not found" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "Worktree path not provided or not found"
                    Save-MergeState -State $state
                    return
                }

                Write-Host "Current commit: $($state.CurrentCommitSha)" -ForegroundColor Gray

                # Check if approval exists
                if ($null -eq $state.Approval) {
                    Write-Host "No approval found. User approval required." -ForegroundColor Yellow
                    Write-Host "Please approve the PR before merging." -ForegroundColor Yellow
                    return
                }

                # Get current main HEAD for validation
                $state.MainHeadSha = Get-MergeMainHead
                Write-Host "Main HEAD: $($state.MainHeadSha)" -ForegroundColor Gray

                # Validate approval
                $approvalValid = Test-MergeApprovalValid -Approval $state.Approval -CurrentCommitSha $state.CurrentCommitSha -CurrentMainHeadSha $state.MainHeadSha

                if (-not $approvalValid.IsValid) {
                    Write-Host "Approval is invalid:" -ForegroundColor Red
                    $approvalValid.Reasons | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
                    Write-Host "User re-approval required." -ForegroundColor Yellow
                    return
                }

                Write-Host "Approval is valid" -ForegroundColor Green
                $state.ApprovedCommitSha = $state.CurrentCommitSha
                $state.State = "MAIN_HEAD_REFRESH"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "MAIN_HEAD_REFRESH" {
                Write-Host "=== Main HEAD Refresh ===" -ForegroundColor Cyan

                # Fetch latest main
                $state.MainHeadSha = Get-MergeMainHead
                Write-Host "Latest main HEAD: $($state.MainHeadSha)" -ForegroundColor Gray

                $state.State = "REBASE"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "REBASE" {
                Write-Host "=== Mandatory Rebase ===" -ForegroundColor Cyan

                if (-not $state.WorktreePath -or -not (Test-Path $state.WorktreePath)) {
                    Write-Host "Worktree path not provided or not found" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "Worktree path not provided or not found"
                    Save-MergeState -State $state
                    return
                }

                Write-Host "Performing mandatory rebase onto origin/main..." -ForegroundColor Cyan
                $rebaseResult = Invoke-MergeRebase -WorktreePath $state.WorktreePath

                if ($rebaseResult.Success) {
                    Write-Host "Rebase succeeded" -ForegroundColor Green
                    $state.State = "VALIDATING"
                } else {
                    Write-Host "Rebase failed" -ForegroundColor Red

                    if ($rebaseResult.HasConflicts) {
                        Write-Host "Conflicts detected:" -ForegroundColor Yellow
                        $rebaseResult.ConflictFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
                        Write-Host "Conflict delegated to Sub-agent for resolution." -ForegroundColor Yellow

                        $state.State = "CONFLICT"
                        $state.ConflictFiles = $rebaseResult.ConflictFiles
                    } else {
                        Write-Host "Rebase failed without conflicts" -ForegroundColor Red
                        $state.State = "FAILED"
                        $state.FailureReason = "Rebase failed without conflicts"
                    }
                }

                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "CONFLICT" {
                Write-Host "=== Conflict State ===" -ForegroundColor Yellow
                Write-Host "Conflicts detected during rebase" -ForegroundColor Yellow
                Write-Host "Sub-agent must resolve conflicts in: $($state.WorktreePath)" -ForegroundColor Yellow
                Write-Host "Branch: $($state.BranchName)" -ForegroundColor Yellow
                Write-Host "Conflict files:" -ForegroundColor Yellow
                if ($state.ConflictFiles) {
                    $state.ConflictFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
                }
                Write-Host ""
                Write-Host "After resolution:" -ForegroundColor Cyan
                Write-Host "1. Sub-agent resolves conflicts" -ForegroundColor Gray
                Write-Host "2. Sub-agent updates PR" -ForegroundColor Gray
                Write-Host "3. Approval invalidated" -ForegroundColor Gray
                Write-Host "4. User re-approves" -ForegroundColor Gray
                Write-Host "5. Merge Skill re-fired" -ForegroundColor Gray
            }

            "VALIDATING" {
                Write-Host "=== Validation ===" -ForegroundColor Cyan

                # Update current commit after rebase
                if ($state.WorktreePath -and (Test-Path $state.WorktreePath)) {
                    $state.CurrentCommitSha = Get-MergeCurrentCommit -WorktreePath $state.WorktreePath
                    Write-Host "Current commit after rebase: $($state.CurrentCommitSha)" -ForegroundColor Gray
                }

                # Check if PR is still mergeable
                $mergeable = Test-MergePrMergeable -PrNumber $PrNumber -Repository $Repository
                if (-not $mergeable.IsMergeable) {
                    Write-Host "PR is not mergeable: $($mergeable.Reason)" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "PR is not mergeable: $($mergeable.Reason)"
                    Save-MergeState -State $state
                    return
                }

                Write-Host "PR is mergeable" -ForegroundColor Green
                Write-Host "Review decision: $($mergeable.ReviewDecision)" -ForegroundColor Gray

                $state.State = "MERGING"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "MERGING" {
                Write-Host "=== Standard Merge ===" -ForegroundColor Cyan
                Write-Host "Executing standard merge (no --admin)..." -ForegroundColor Cyan

                $mergeResult = Invoke-NormalMerge -PrNumber $PrNumber -Repository $Repository

                if ($mergeResult.Success) {
                    Write-Host "Standard merge succeeded" -ForegroundColor Green
                    $state.State = "MERGED"
                } else {
                    Write-Host "Standard merge failed: $($mergeResult.Message)" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "Merge failed: $($mergeResult.Message)"
                }

                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "MERGED" {
                Write-Host "=== Merge Verification ===" -ForegroundColor Cyan

                # Verify merge on GitHub
                $mergeStatus = Test-MergePrMerged -PrNumber $PrNumber -Repository $Repository
                if (-not $mergeStatus.IsMerged) {
                    Write-Host "PR is not merged on GitHub" -ForegroundColor Red
                    $state.State = "FAILED"
                    $state.FailureReason = "PR is not merged on GitHub"
                    Save-MergeState -State $state
                    return
                }

                Write-Host "PR is merged on GitHub" -ForegroundColor Green
                Write-Host "Merge commit: $($mergeStatus.MergeCommit)" -ForegroundColor Gray

                $state.State = "CLEANUP"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "CLEANUP" {
                Write-Host "=== Cleanup ===" -ForegroundColor Cyan

                # Delete Worktree if provided
                if ($state.WorktreePath -and $state.BranchName) {
                    Write-Host "Cleaning up Worktree and Branch..." -ForegroundColor Yellow
                    Remove-MergeWorktree -WorktreePath $state.WorktreePath -BranchName $state.BranchName
                } else {
                    Write-Host "No Worktree or Branch to clean up" -ForegroundColor Gray
                }

                $state.State = "COMPLETED"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state

                Write-Host "Cleanup completed" -ForegroundColor Green
            }

            "COMPLETED" {
                Write-Host "PR #$PrNumber merge completed!" -ForegroundColor Green
                Write-Host "Merge is complete. State: COMPLETED" -ForegroundColor Green
            }

            "FAILED" {
                Write-Host "Merge failed" -ForegroundColor Red
                Write-Host "Reason: $($state.FailureReason)" -ForegroundColor Red
                Write-Host ""
                Write-Host "To retry, resolve the issue and re-run the Merge Skill." -ForegroundColor Yellow
            }

            default {
                Write-Host "Unknown state: $($state.State)" -ForegroundColor Red
                $state.State = "FAILED"
                $state.FailureReason = "Unknown state: $($state.State)"
                Save-MergeState -State $state
            }
        }
    } catch {
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host $_.ScriptStackTrace -ForegroundColor Gray
        $state.State = "FAILED"
        $state.FailureReason = "Exception: $($_.Exception.Message)"
        Save-MergeState -State $state
    }
}

# Run orchestration
Invoke-MergeOrchestration
