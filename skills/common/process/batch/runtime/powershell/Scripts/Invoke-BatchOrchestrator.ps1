#Requires -Version 7.0

<#
.SYNOPSIS
    Orchestrator for Batch Skill: parallel Issue execution with dependency scheduling.

.DESCRIPTION
    Manages the lifecycle of parallel Issue processing via Sub-agents.
    Enforces safety conditions, dependency ordering, and serial merge via Merge Skill.
    NEVER substitutes implementation when Sub-agents fail.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$BatchId,

    [Parameter(Mandatory = $false)]
    [string]$IssuesFile,

    [Parameter(Mandatory = $false)]
    [string[]]$IssueIds,

    [Parameter(Mandatory = $false)]
    [int]$MaxConcurrency = 3,

    [Parameter(Mandatory = $false)]
    [int]$MaxRetries = 3,

    [Parameter(Mandatory = $false)]
    [string]$Repository = ".",

    [Parameter(Mandatory = $false)]
    [string]$WorktreeRoot = "../worktrees",

    [Parameter(Mandatory = $false)]
    [string]$StateDir = ".",

    [Parameter(Mandatory = $false)]
    [string]$MergeSkillPath
)

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "BatchStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "IssueStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "DependencyGraph.psm1") -Force
Import-Module (Join-Path $modulePath "BatchScheduler.psm1") -Force
Import-Module (Join-Path $modulePath "BatchSubAgent.psm1") -Force
Import-Module (Join-Path $modulePath "BatchGitUtilities.psm1") -Force
Import-Module (Join-Path $modulePath "BatchMergeQueue.psm1") -Force
Import-Module (Join-Path $modulePath "BatchPersistence.psm1") -Force

if (-not $MergeSkillPath) {
    $MergeSkillPath = Join-Path $scriptPath ".." ".." ".." ".." ".." "skills" "common" "process" "merge"
}

$batchStateFile = Get-BatchStateFilePath -BatchId $BatchId -StateDir $StateDir
$issueStatesFile = Join-Path $StateDir ".batch-issues-$BatchId.json"

function Write-BatchLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $color = switch ($Level) {
        "ERROR" { "Red" }
        "WARN" { "Yellow" }
        "SUCCESS" { "Green" }
        "INFO" { "Cyan" }
        default { "Gray" }
    }
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $color
}

function Invoke-BatchOrchestration {
    Write-Host "=== Batch Orchestrator ===" -ForegroundColor Green
    Write-Host "Batch ID: $BatchId" -ForegroundColor Cyan
    Write-Host ""

    $existingState = Get-BatchState -FilePath $batchStateFile
    if ($null -ne $existingState) {
        Write-BatchLog "Resuming from persisted state: $($existingState.State)" "INFO"
        $batchState = $existingState
        $issueStates = Get-IssueStates -FilePath $issueStatesFile
        if ($null -eq $issueStates) {
            $issueStates = @{}
        }
        $sync = Sync-StateWithGitHub -BatchState $batchState -IssueStates $issueStates
        $batchState = $sync.BatchState
        $issueStates = $sync.IssueStates
        foreach ($change in $sync.Changes) {
            Write-BatchLog "Sync: $change" "WARN"
        }
    } else {
        $parsedIssues = @()
        if ($IssuesFile -and (Test-Path $IssuesFile)) {
            $fileContent = Get-Content $IssuesFile | ConvertFrom-Json
            foreach ($item in $fileContent) {
                $parsedIssues += @{
                    Id = $item.Id
                    IssueNumber = $item.IssueNumber
                    Description = $item.Description
                    Dependencies = if ($item.Dependencies) { $item.Dependencies } else { @() }
                }
            }
        } elseif ($IssueIds) {
            foreach ($id in $IssueIds) {
                $parsedIssues += @{
                    Id = $id
                    IssueNumber = if ($id -match '^\d+$') { [int]$id } else { 0 }
                    Description = $id
                    Dependencies = @()
                }
            }
        } else {
            Write-BatchLog "No issues provided" "ERROR"
            return
        }

        $batchState = New-BatchState -BatchId $BatchId -IssueCount $parsedIssues.Count
        $issueStates = @{}

        foreach ($issue in $parsedIssues) {
            $issueStates[$issue.Id] = New-IssueState -IssueId $issue.Id -IssueNumber $issue.IssueNumber -Description $issue.Description
            $issueStates[$issue.Id].Dependencies = $issue.Dependencies
        }

        Write-BatchLog "Loaded $($parsedIssues.Count) issues" "INFO"
    }

    try {
        switch ($batchState.State) {
            "BATCH_INITIALIZING" {
                Write-BatchLog "=== Phase: Initialization ===" "INFO"
                $batchState.State = "PLANNING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "PLANNING" {
                Write-BatchLog "=== Phase: Planning ===" "INFO"

                $graph = New-DependencyGraph
                foreach ($issueId in $issueStates.Keys) {
                    $issueData = @{
                        IssueNumber = $issueStates[$issueId].IssueNumber
                        Description = $issueStates[$issueId].Description
                    }
                    Add-DependencyNode -Graph $graph -IssueId $issueId -IssueData $issueData
                }

                foreach ($issueId in $issueStates.Keys) {
                    $deps = $issueStates[$issueId].Dependencies
                    foreach ($dep in $deps) {
                        if ($dep -in $issueStates.Keys) {
                            Add-DependencyEdge -Graph $graph -FromIssue $issueId -ToIssue $dep
                        } else {
                            Write-BatchLog "Warning: Dependency '$dep' for issue '$issueId' not in issue list, skipping" "WARN"
                        }
                    }
                }

                $cycleCheck = Test-DependencyCycle -Graph $graph
                if ($cycleCheck.HasCycle) {
                    Write-BatchLog "Cycle detected: $($cycleCheck.CyclePath -join ' -> ')" "ERROR"
                    $batchState.State = "FAILED"
                    $batchState.FailureReason = "Dependency cycle detected"
                    Save-BatchState -State $batchState -FilePath $batchStateFile
                    return
                }

                $concurrencyGroups = Get-DependencyConcurrencyGroups -Graph $graph

                $batchState.DependencyGraph = @{
                    Nodes = $graph.Nodes.Keys | ForEach-Object { @{ Id = $_ } }
                    Edges = @{}
                }
                foreach ($nodeId in $graph.Edges.Keys) {
                    $batchState.DependencyGraph.Edges[$nodeId] = $graph.Edges[$nodeId]
                }
                $batchState.ConcurrencyGroups = $concurrencyGroups

                Write-BatchLog "Dependency graph built: $($graph.Nodes.Count) nodes, no cycles" "SUCCESS"
                Write-BatchLog "Concurrency groups: $($concurrencyGroups.Count) waves" "INFO"
                for ($i = 0; $i -lt $concurrencyGroups.Count; $i++) {
                    Write-BatchLog "  Wave $($i + 1): $($concurrencyGroups[$i] -join ', ')" "INFO"
                }

                $batchState.State = "SCHEDULING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "SCHEDULING" {
                Write-BatchLog "=== Phase: Scheduling ===" "INFO"

                $scheduler = New-BatchScheduler -MaxConcurrency $MaxConcurrency

                foreach ($issueId in $issueStates.Keys) {
                    Register-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -IssueData @{
                        IssueNumber = $issueStates[$issueId].IssueNumber
                        Description = $issueStates[$issueId].Description
                    }
                }

                $readyIssues = Get-DependencyReadyIssues -Graph @{
                    Nodes = @{}
                    Edges = $batchState.DependencyGraph.Edges
                } -CompletedIssues @()

                foreach ($issueId in $readyIssues) {
                    $issueStates[$issueId].State = "WAITING_FOR_SUBAGENT"
                }

                $batchState.State = "RUNNING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "RUNNING" {
                Write-BatchLog "=== Phase: Running ===" "INFO"

                $scheduler = New-BatchScheduler -MaxConcurrency $MaxConcurrency
                foreach ($issueId in $issueStates.Keys) {
                    Register-SchedulerIssue -Scheduler $scheduler -IssueId $issueId
                }

                $completedIssues = @()
                foreach ($issueId in $issueStates.Keys) {
                    if ($issueStates[$issueId].State -eq "COMPLETED") {
                        $completedIssues += $issueId
                    }
                }

                $allDone = $false
                while (-not $allDone) {
                    $readyForExecution = @()
                    foreach ($issueId in $issueStates.Keys) {
                        $issue = $issueStates[$issueId]
                        if ($issue.State -eq "WAITING_FOR_SUBAGENT" -or $issue.State -eq "WAITING_DEPENDENCY") {
                            $deps = $issue.Dependencies
                            $allDepsMet = $true
                            foreach ($dep in $deps) {
                                if ($dep -notin $completedIssues) {
                                    $allDepsMet = $false
                                    break
                                }
                            }
                            if ($allDepsMet) {
                                $readyForExecution += $issueId
                            }
                        }
                    }

                    foreach ($issueId in $readyForExecution) {
                        if (Test-SchedulerSlotAvailable -Scheduler $scheduler) {
                            $startResult = Start-SchedulerIssue -Scheduler $scheduler -IssueId $issueId
                            if ($startResult.Success) {
                                $issueStates[$issueId].State = "SUBAGENT_STARTING"
                                $issueStates[$issueId].StartedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                                Write-BatchLog "Dispatching Sub-agent for issue $issueId" "INFO"

                                $subAgentConfig = New-SubAgentConfig -MaxRetries $MaxRetries
                                $subAgentState = New-SubAgentState -IssueId $issueId -Config $subAgentConfig
                                $subAgentState.State = "SUBAGENT_RUNNING"
                                $issueStates[$issueId].State = "SUBAGENT_RUNNING"

                                Write-BatchLog "Sub-agent for $issueId started" "SUCCESS"
                            }
                        }
                    }

                    $activeIssues = @()
                    foreach ($issueId in $issueStates.Keys) {
                        $state = $issueStates[$issueId].State
                        if ($state -in @("SUBAGENT_STARTING", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING")) {
                            $activeIssues += $issueId
                        }
                    }

                    $newlyCompleted = @()
                    foreach ($issueId in $activeIssues) {
                        $issue = $issueStates[$issueId]
                        if ($issue.PrNumber -and $issue.CommitSha) {
                            $issue.State = "PR_READY"
                            $issue.CompletedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                            $completedIssues += $issueId
                            $newlyCompleted += $issueId
                            Complete-SchedulerIssue -Scheduler $scheduler -IssueId $issueId
                            Write-BatchLog "Issue $issueId: PR #$($issue.PrNumber) ready" "SUCCESS"
                        }
                    }

                    $allActive = @()
                    foreach ($issueId in $issueStates.Keys) {
                        $state = $issueStates[$issueId].State
                        if ($state -notin @("COMPLETED", "FAILED", "BLOCKED")) {
                            $allActive += $issueId
                        }
                    }

                    if ($allActive.Count -eq 0) {
                        $allDone = $true
                    } else {
                        Write-BatchLog "Waiting for $($allActive.Count) active issues..." "INFO"
                        Start-Sleep -Seconds 5
                    }

                    Save-BatchState -State $batchState -FilePath $batchStateFile
                    Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                }

                $batchState.CompletedCount = ($issueStates.Values | Where-Object { $_.State -eq "COMPLETED" }).Count
                $batchState.FailedCount = ($issueStates.Values | Where-Object { $_.State -eq "FAILED" }).Count
                $batchState.BlockedCount = ($issueStates.Values | Where-Object { $_.State -eq "BLOCKED" }).Count

                $prReadyIssues = @()
                foreach ($issueId in $issueStates.Keys) {
                    if ($issueStates[$issueId].State -eq "PR_READY") {
                        $prReadyIssues += $issueId
                    }
                }

                if ($prReadyIssues.Count -gt 0) {
                    $batchState.State = "WAITING_FOR_MERGE"
                } elseif ($batchState.FailedCount -gt 0 -or $batchState.BlockedCount -gt 0) {
                    $batchState.State = "FAILED"
                    $batchState.FailureReason = "$($batchState.FailedCount) failed, $($batchState.BlockedCount) blocked"
                } else {
                    $batchState.State = "WAITING_FOR_MERGE"
                }

                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "WAITING_FOR_MERGE" {
                Write-BatchLog "=== Phase: Waiting for Merge Approval ===" "INFO"

                $prReadyIssues = @()
                foreach ($issueId in $issueStates.Keys) {
                    if ($issueStates[$issueId].State -eq "PR_READY") {
                        $prReadyIssues += $issueId
                        Write-BatchLog "  Issue $issueId: PR #$($issueStates[$issueId].PrNumber)" "INFO"
                    }
                }

                Write-BatchLog "All $($prReadyIssues.Count) PRs are ready for merge." "INFO"
                Write-BatchLog "Please approve each PR, then re-run to proceed with merge." "INFO"

                $batchState.State = "MERGING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "MERGING" {
                Write-BatchLog "=== Phase: Merging ===" "INFO"

                $mergeQueue = New-MergeQueue

                foreach ($issueId in $issueStates.Keys) {
                    $issue = $issueStates[$issueId]
                    if ($issue.State -eq "PR_READY" -or $issue.State -eq "READY_FOR_MERGE") {
                        if ($issue.PrNumber -and $issue.WorktreePath -and $issue.BranchName) {
                            Add-MergeQueueItem -Queue $mergeQueue -PrNumber $issue.PrNumber -IssueId $issueId -WorktreePath $issue.WorktreePath -BranchName $issue.BranchName
                            $issue.State = "MERGING"
                        }
                    }
                }

                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile

                if ($mergeQueue.Pending.Count -gt 0) {
                    $mergeResult = Invoke-MergeQueueSerial -Queue $mergeQueue -MergeSkillPath $MergeSkillPath -Repository $Repository

                    foreach ($item in $mergeQueue.Merged) {
                        $issueStates[$item.IssueId].State = "COMPLETED"
                        $issueStates[$item.IssueId].CompletedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    }

                    foreach ($item in $mergeQueue.Failed) {
                        $issueStates[$item.IssueId].State = "FAILED"
                        $issueStates[$item.IssueId].LastError = "Merge failed"
                    }

                    foreach ($item in $mergeQueue.Conflicted) {
                        $issueStates[$item.IssueId].State = "PR_READY"
                        $issueStates[$item.IssueId].LastError = "Conflict during rebase"
                    }
                }

                $batchState.State = "CLEANUP"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "CLEANUP" {
                Write-BatchLog "=== Phase: Cleanup ===" "INFO"

                foreach ($issueId in $issueStates.Keys) {
                    $issue = $issueStates[$issueId]
                    if ($issue.State -eq "COMPLETED") {
                        if ($issue.WorktreePath -and $issue.BranchName) {
                            Write-BatchLog "Cleaning up issue $issueId" "INFO"
                            Remove-BatchWorktree -WorktreePath $issue.WorktreePath -BranchName $issue.BranchName -Force
                        }
                    }
                }

                $batchState.State = "COMPLETED"
                $batchState.CompletedCount = ($issueStates.Values | Where-Object { $_.State -eq "COMPLETED" }).Count
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
            }

            "COMPLETED" {
                Write-BatchLog "=== Batch Completed ===" "SUCCESS"
                Write-BatchLog "Completed: $($batchState.CompletedCount) issues" "SUCCESS"
                Write-BatchLog "Failed: $($batchState.FailedCount) issues" $(if ($batchState.FailedCount -gt 0) { "WARN" } else { "INFO" })
                Write-BatchLog "Blocked: $($batchState.BlockedCount) issues" $(if ($batchState.BlockedCount -gt 0) { "WARN" } else { "INFO" })
            }

            "FAILED" {
                Write-BatchLog "=== Batch Failed ===" "ERROR"
                Write-BatchLog "Reason: $($batchState.FailureReason)" "ERROR"
                Write-BatchLog "Completed: $($batchState.CompletedCount)" "INFO"
                Write-BatchLog "Failed: $($batchState.FailedCount)" "ERROR"
                Write-BatchLog "Blocked: $($batchState.BlockedCount)" "WARN"
                Write-BatchLog "To resume, fix issues and re-run with the same Batch ID." "INFO"
            }

            default {
                Write-BatchLog "Unknown state: $($batchState.State)" "ERROR"
                $batchState.State = "FAILED"
                $batchState.FailureReason = "Unknown state: $($batchState.State)"
                Save-BatchState -State $batchState -FilePath $batchStateFile
            }
        }
    } catch {
        Write-BatchLog "Error: $($_.Exception.Message)" "ERROR"
        Write-BatchLog $_.ScriptStackTrace "ERROR"
        $batchState.State = "FAILED"
        $batchState.FailureReason = "Exception: $($_.Exception.Message)"
        Save-BatchState -State $batchState -FilePath $batchStateFile
    }
}

Invoke-BatchOrchestration
