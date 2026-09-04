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
            RebasedOntoMainSha = $null
            Approval = $null
            CreatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
            UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        }
    }

    # A state file written before Issue #247 lacks the rebase marker. Absence is
    # treated as "not proven rebased", which fails closed into another mandatory
    # rebase rather than into a merge.
    if (-not $state.ContainsKey("RebasedOntoMainSha")) {
        $state.RebasedOntoMainSha = $null
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
                $state.State = "MAIN_HEAD_REFRESH"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "APPROVAL_VALIDATION" {
                Write-Host "=== Final Approval Validation ===" -ForegroundColor Cyan

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

                Write-Host "Final merge candidate HEAD: $($state.CurrentCommitSha)" -ForegroundColor Gray

                $mainHead = Get-MergeMainHead
                Write-Host "Main HEAD: $mainHead" -ForegroundColor Gray

                # This gate binds a human approval to the commit that will
                # actually be merged, so it only runs on a candidate proven
                # rebased onto the current main HEAD. Otherwise the mandatory
                # rebase is owed first, and any approval bound to the superseded
                # candidate is discarded rather than carried forward.
                if ((-not $state.RebasedOntoMainSha) -or ($state.RebasedOntoMainSha -ne $mainHead)) {
                    if (-not $state.RebasedOntoMainSha) {
                        Write-Host "Merge candidate is not proven rebased onto the current main HEAD." -ForegroundColor Yellow
                    } else {
                        Write-Host "Main HEAD advanced since the mandatory rebase (rebased onto $($state.RebasedOntoMainSha), latest $mainHead)." -ForegroundColor Yellow
                    }
                    Write-Host "Re-running the mandatory rebase; approval will be requested for the resulting candidate." -ForegroundColor Yellow
                    $state.Approval = $null
                    $state.ApprovedCommitSha = $null
                    $state.State = "MAIN_HEAD_REFRESH"
                    $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    Save-MergeState -State $state
                    return
                }
                # A push landing while this gate waits for a human makes the
                # candidate stale. Never ask anyone to approve a SHA GitHub
                # would not merge: rebuild the candidate first. Only a remote
                # strictly ahead counts as drift, so local work that has not
                # been pushed yet is unaffected.
                if ($state.BranchName) {
                    $gateRemote = Get-MergeRemoteHeadState -WorktreePath $state.WorktreePath -BranchName $state.BranchName
                    if ($gateRemote.Relation -eq "remote_ahead") {
                        Write-Host "Remote PR head moved ahead of this candidate (remote $($gateRemote.RemoteSha))." -ForegroundColor Yellow
                        Write-Host "Rebuilding the merge candidate; approval will be requested for the result." -ForegroundColor Yellow
                        $state.Approval = $null
                        $state.ApprovedCommitSha = $null
                        $state.State = "MAIN_HEAD_REFRESH"
                        $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                        Save-MergeState -State $state
                        return
                    }
                }

                $state.MainHeadSha = $mainHead

                # Check if approval exists
                if ($null -eq $state.Approval) {
                    Write-Host "No approval found for the final merge candidate." -ForegroundColor Yellow
                    Write-Host "READY FOR HUMAN APPROVAL: $($state.CurrentCommitSha)" -ForegroundColor Yellow
                    Save-MergeState -State $state
                    return
                }

                # Validate approval
                $approvalValid = Test-MergeApprovalValid -Approval $state.Approval -CurrentCommitSha $state.CurrentCommitSha -CurrentMainHeadSha $state.MainHeadSha

                if (-not $approvalValid.IsValid) {
                    Write-Host "Approval is invalid:" -ForegroundColor Red
                    $approvalValid.Reasons | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
                    Write-Host "User re-approval required." -ForegroundColor Yellow
                    Save-MergeState -State $state
                    return
                }

                Write-Host "Approval is valid for the final merge candidate" -ForegroundColor Green
                $state.ApprovedCommitSha = $state.CurrentCommitSha
                $state.State = "MERGING"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "MAIN_HEAD_REFRESH" {
                Write-Host "=== Main HEAD Refresh ===" -ForegroundColor Cyan

                # Fetch latest main
                $state.MainHeadSha = Get-MergeMainHead
                Write-Host "Latest main HEAD: $($state.MainHeadSha)" -ForegroundColor Gray

                # The candidate is only proven rebased once REBASE completes
                # against this refreshed main HEAD.
                $state.RebasedOntoMainSha = $null
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

                # The mandatory rebase must start from the commit GitHub would
                # merge. When the PR head moved on the remote, fast-forward onto
                # it first so the rebase produces a candidate that matches the
                # PR. Nothing is rewritten or pushed here, and a history that
                # cannot be fast-forwarded fails closed.
                if ($state.BranchName) {
                    $remoteState = Get-MergeRemoteHeadState -WorktreePath $state.WorktreePath -BranchName $state.BranchName
                    switch ($remoteState.Relation) {
                        "remote_ahead" {
                            Write-Host "Remote PR head moved ahead of the worktree; fast-forwarding onto $($remoteState.RemoteSha)" -ForegroundColor Yellow
                            if (-not (Invoke-MergeFastForwardToRemote -WorktreePath $state.WorktreePath -BranchName $state.BranchName)) {
                                Write-Host "Unable to fast-forward the worktree onto the remote PR head" -ForegroundColor Red
                                $state.State = "FAILED"
                                $state.FailureReason = "Unable to fast-forward the worktree onto the remote PR head"
                                Save-MergeState -State $state
                                return
                            }
                        }
                        "diverged" {
                            Write-Host "Worktree and remote PR head have diverged; refusing to rewrite either" -ForegroundColor Red
                            $state.State = "FAILED"
                            $state.FailureReason = "Worktree and remote PR head have diverged"
                            Save-MergeState -State $state
                            return
                        }
                        "unknown" {
                            Write-Host "Unable to establish the remote PR head before rebase" -ForegroundColor Red
                            $state.State = "FAILED"
                            $state.FailureReason = "Unable to establish the remote PR head before rebase"
                            Save-MergeState -State $state
                            return
                        }
                    }
                }

                $preRebaseCommit = Get-MergeCurrentCommit -WorktreePath $state.WorktreePath

                Write-Host "Performing mandatory rebase onto origin/main..." -ForegroundColor Cyan
                $rebaseResult = Invoke-MergeRebase -WorktreePath $state.WorktreePath

                if ($rebaseResult.Success) {
                    Write-Host "Rebase succeeded" -ForegroundColor Green

                    $postRebaseCommit = Get-MergeCurrentCommit -WorktreePath $state.WorktreePath
                    # Any approval recorded earlier belongs to a pre-rebase SHA.
                    # Discard it; the approval gate downstream asks for a fresh
                    # one bound to this candidate.
                    if ($preRebaseCommit -ne $postRebaseCommit) {
                        $state.Approval = $null
                        $state.ApprovedCommitSha = $null
                        Write-Host "Rebased HEAD changed; any pre-rebase approval discarded" -ForegroundColor Yellow
                    }
                    if (-not $state.MainHeadSha) {
                        Write-Host "No recorded main HEAD for the mandatory rebase" -ForegroundColor Red
                        $state.State = "FAILED"
                        $state.FailureReason = "No recorded main HEAD for the mandatory rebase"
                        Save-MergeState -State $state
                        return
                    }
                    $state.CurrentCommitSha = $postRebaseCommit
                    $state.RebasedOntoMainSha = $state.MainHeadSha
                    Write-Host "Final merge candidate: $postRebaseCommit (rebased onto $($state.MainHeadSha))" -ForegroundColor Gray
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
                Write-Host "=== CI / Review Gate Validation ===" -ForegroundColor Cyan

                # Update current commit after rebase
                if ($state.WorktreePath -and (Test-Path $state.WorktreePath)) {
                    $state.CurrentCommitSha = Get-MergeCurrentCommit -WorktreePath $state.WorktreePath
                    Write-Host "Final merge candidate after rebase: $($state.CurrentCommitSha)" -ForegroundColor Gray
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
                Write-Host "CI and review gates passed for the final merge candidate." -ForegroundColor Green

                # Gates pass on the post-rebase candidate, so an approval
                # requested from here is for an already merge-eligible commit.
                $state.State = "APPROVAL_VALIDATION"
                $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                Save-MergeState -State $state
            }

            "MERGING" {
                Write-Host "=== Final HEAD Revalidation ===" -ForegroundColor Cyan

                # Close the window between approval and merge: anything that
                # moved in it must stop the merge (fail closed, never forward).
                $blockReasons = @()
                if (-not $state.ApprovedCommitSha) {
                    $blockReasons += "No approved commit SHA is recorded in state"
                }
                if ($null -eq $state.Approval) {
                    $blockReasons += "Approval record is missing"
                }
                $liveHead = $null
                if ($state.WorktreePath -and (Test-Path $state.WorktreePath)) {
                    $liveHead = Get-MergeCurrentCommit -WorktreePath $state.WorktreePath
                }
                if (-not $liveHead) {
                    $blockReasons += "Current PR HEAD could not be determined"
                } elseif ($state.ApprovedCommitSha -and ($liveHead -ne $state.ApprovedCommitSha)) {
                    $blockReasons += "PR HEAD changed after approval: approved=$($state.ApprovedCommitSha), current=$liveHead"
                }
                $liveMain = Get-MergeMainHead
                $mainMoved = $false
                if (-not $liveMain) {
                    $blockReasons += "Live main HEAD could not be determined"
                } elseif (-not $state.RebasedOntoMainSha) {
                    $blockReasons += "No recorded mandatory-rebase base: the candidate is not proven rebased"
                } elseif ($state.RebasedOntoMainSha -ne $liveMain) {
                    $blockReasons += "Main HEAD advanced after approval: rebased onto $($state.RebasedOntoMainSha), latest $liveMain"
                    $mainMoved = $true
                }
                # The commit GitHub would merge, which is not necessarily the
                # local worktree HEAD: a push landing after approval must not be
                # merged under the old approval.
                $remoteHead = $null
                $prInfo = Get-MergePrInfo -PrNumber $PrNumber -Repository $Repository
                if ($prInfo) { $remoteHead = $prInfo.headRefOid }
                $remoteDrift = $false
                if (-not $remoteHead) {
                    $blockReasons += "Remote PR HEAD could not be determined"
                    $remoteDrift = $true
                } elseif ($state.ApprovedCommitSha -and ($remoteHead -ne $state.ApprovedCommitSha)) {
                    $blockReasons += "Remote PR HEAD does not match the approved SHA: approved=$($state.ApprovedCommitSha), remote=$remoteHead"
                    $remoteDrift = $true
                } elseif ($liveHead -and ($remoteHead -ne $liveHead)) {
                    $blockReasons += "Remote PR HEAD does not match the merge candidate: candidate=$liveHead, remote=$remoteHead"
                    $remoteDrift = $true
                }

                $stillMergeable = Test-MergePrMergeable -PrNumber $PrNumber -Repository $Repository
                if (-not $stillMergeable.IsMergeable) {
                    $blockReasons += $stillMergeable.Reason
                }

                if ($blockReasons.Count -gt 0) {
                    Write-Host "Refusing to merge: the approved state no longer holds." -ForegroundColor Red
                    $blockReasons | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
                    $state.Approval = $null
                    $state.ApprovedCommitSha = $null
                    # Route by what actually diverged. Both branches are
                    # fail-closed: no merge happens on this invocation.
                    #   main moved           -> rebase again
                    #   remote PR head moved -> the CANDIDATE ITSELF changed, so
                    #                           rebuild it from the remote PR
                    #                           head. Re-prompting here would
                    #                           offer the stale local HEAD, which
                    #                           this same guard rejects again.
                    #   approval record only -> only a fresh approval is owed.
                    if ($mainMoved) {
                        Write-Host "Returning to the mandatory rebase for the new main HEAD." -ForegroundColor Yellow
                        $state.State = "MAIN_HEAD_REFRESH"
                    } elseif ($remoteDrift) {
                        Write-Host "Rebuilding the merge candidate from the remote PR head." -ForegroundColor Yellow
                        $state.State = "MAIN_HEAD_REFRESH"
                    } else {
                        Write-Host "Fresh approval required for the current merge candidate." -ForegroundColor Yellow
                        $state.State = "APPROVAL_VALIDATION"
                    }
                    $state.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    Save-MergeState -State $state
                    return
                }

                Write-Host "Final HEAD revalidation passed: $($state.ApprovedCommitSha)" -ForegroundColor Green
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
