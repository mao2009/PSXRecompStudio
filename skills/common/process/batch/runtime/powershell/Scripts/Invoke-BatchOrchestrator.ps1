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
Import-Module (Join-Path $modulePath "BatchCheckpoint.psm1") -Force
Import-Module (Join-Path $modulePath "AgentProvider.psm1") -Force

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

function Set-BatchStateTransition {
    param(
        [hashtable]$BatchState,
        [string]$ToState,
        [string]$FilePath
    )

    $fromState = $BatchState.State
    if (-not (Test-ValidBatchTransition -FromState $fromState -ToState $ToState)) {
        throw "Invalid batch state transition: $fromState -> $ToState"
    }
    $BatchState.State = $ToState
}

function Set-IssueStateTransition {
    param(
        [hashtable]$IssueState,
        [string]$ToState,
        [string]$BatchId,
        [string]$Reason = ""
    )

    $fromState = $IssueState.State
    if (-not (Test-ValidIssueTransition -FromState $fromState -ToState $ToState)) {
        throw "Invalid issue state transition: $fromState -> $ToState"
    }
    $IssueState.State = $ToState
    $IssueState.UpdatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    try {
        Write-TransitionLog -BatchId $BatchId -EntityType "worker" -EntityId $IssueState.IssueId -FromState $fromState -ToState $ToState -Reason $Reason -StateDir $StateDir
    } catch { }
}

function Save-AllCheckpoints {
    param(
        [hashtable]$BatchState,
        [hashtable]$IssueStates,
        [string]$Provider = "",
        [int]$MaxRetries = 3
    )

    $batchCp = New-BatchCheckpoint -BatchId $BatchState.BatchId -IssueCount $BatchState.IssueCount
    $batchCp.batchState = $BatchState.State
    $batchCp.completedCount = $BatchState.CompletedCount
    $batchCp.failedCount = $BatchState.FailedCount
    $batchCp.blockedCount = $BatchState.BlockedCount
    $batchCp.failureReason = $BatchState.FailureReason

    foreach ($issueId in $IssueStates.Keys) {
        $workerCp = New-WorkerCheckpointFromIssueState -IssueState $IssueStates[$issueId] -Provider $Provider -MaxRetries $MaxRetries
        $workerCp.batchId = $BatchState.BatchId
        $batchCp.workers[$issueId] = @{
            state = $workerCp.lifecycleState
            updatedAt = $workerCp.updatedAt
        }
        Save-WorkerCheckpoint -Checkpoint $workerCp -StateDir $StateDir
    }

    Save-BatchCheckpoint -Checkpoint $batchCp -StateDir $StateDir
}

function Test-GitBranchExists {
    param(
        [string]$BranchName,
        [string]$Repository = "."
    )
    & git -C $Repository rev-parse --verify --quiet "refs/heads/$BranchName" > $null 2>&1
    return $LASTEXITCODE -eq 0
}

function Test-GitPrExists {
    param(
        [string]$BranchName,
        [string]$Repository = "."
    )
    $remoteUrl = & git -C $Repository remote get-url origin 2>$null
    if (-not $remoteUrl) { return $null }
    $repoId = $remoteUrl -replace '\.git$', '' -replace '^git@[^:]+:', '' -replace '^ssh://git@[^/]+/', '' -replace '^https?://[^/]+/', ''
    if (-not $repoId) { return $null }
    $result = & gh pr list --repo $repoId --head $BranchName --json number --state open 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    try {
        $prs = $result | ConvertFrom-Json
        if ($prs.Count -gt 0) {
            return $prs[0].number
        }
    } catch { }
    return $null
}

function Invoke-BatchOrchestration {
    Write-Host "=== Batch Orchestrator ===" -ForegroundColor Green
    Write-Host "Batch ID: $BatchId" -ForegroundColor Cyan
    Write-Host ""

    $configuredProvider = if ($env:BATCH_AGENT_PROVIDER) { $env:BATCH_AGENT_PROVIDER } else { "" }
    try {
        $providerSelection = Resolve-AgentProvider -ProviderName $configuredProvider -ErrorAction Stop
    } catch {
        $providerSelection = @{ Blocked = $true; SelectionReason = "Provider selection failed: $($_.Exception.Message)"; SelectedProvider = $null; SelectedMechanism = $null; HostAgent = $null; NativeSubagentCapability = "UNKNOWN"; ExplicitProviderConfigured = -not [string]::IsNullOrWhiteSpace($configuredProvider) }
        Write-BatchLog $providerSelection.SelectionReason "ERROR"
    }
    $resolvedProvider = if ($providerSelection.SelectedProvider) { $providerSelection.SelectedProvider } else { "" }
    Write-BatchLog ("Host agent: {0}; native capability: {1}; configured provider: {2}; selected provider: {3}; mechanism: {4}; reason: {5}" -f $providerSelection.HostAgent, $providerSelection.NativeSubagentCapability, $configuredProvider, $providerSelection.SelectedProvider, $providerSelection.SelectedMechanism, $providerSelection.SelectionReason) "INFO"

    $existingState = Get-BatchState -FilePath $batchStateFile
    if ($null -ne $existingState) {
        Write-BatchLog "Resuming from persisted state: $($existingState.State)" "INFO"
        $batchState = $existingState
        $issueStates = Get-IssueStates -FilePath $issueStatesFile
        if ($null -eq $issueStates) {
            $issueStates = @{}
        }
        # A resumed RUNNING batch must use the persisted immutable selection.
        if ($batchState.State -eq "RUNNING") {
            $savedIssue = $issueStates.Values | Where-Object { $_.SelectedProvider } | Select-Object -First 1
            if ($savedIssue) {
                $providerSelection = @{
                    Blocked = $false
                    HostAgent = $savedIssue.HostAgent
                    NativeSubagentCapability = $savedIssue.NativeCapability
                    ExplicitProviderConfigured = $savedIssue.ExplicitProviderConfigured
                    SelectedProvider = $savedIssue.SelectedProvider
                    SelectedMechanism = $savedIssue.SelectedMechanism
                    SelectionReason = $savedIssue.SelectionReason
                }
                $resolvedProvider = $savedIssue.SelectedProvider
                Write-BatchLog "Restored persisted provider selection: $($savedIssue.SelectedProvider) via $($savedIssue.SelectedMechanism)" "INFO"
            }
        }
        $sync = Sync-StateWithGitHub -BatchState $batchState -IssueStates $issueStates
        $batchState = $sync.BatchState
        $issueStates = $sync.IssueStates
        foreach ($change in $sync.Changes) {
            Write-BatchLog "Sync: $change" "WARN"
        }

        if ($batchState.State -eq "RUNNING") {
            Write-BatchLog "Detecting orphaned processes from previous run..." "INFO"
            $orphanCount = 0
            $adoptedWorkers = @()
            foreach ($issueId in $issueStates.Keys) {
                $issue = $issueStates[$issueId]
                if ($issue.State -in @("SUBAGENT_STARTING", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING")) {
                    $worktreePath = $issue.WorktreePath
                    $resultFile = if ($worktreePath) { Join-Path $worktreePath ".subagent" "result.json" } else { $null }

                    if ($resultFile -and (Test-Path $resultFile)) {
                        $resumeResult = Get-SubAgentResult -ResultFile $resultFile
                        if ($null -ne $resumeResult -and $resumeResult.ContainsKey("Success")) {
                            Write-BatchLog "Issue ${issueId}: Valid result file found from previous run, will process" "INFO"
                            continue
                        }
                        Write-BatchLog "Issue ${issueId}: Result file exists but is invalid, treating as orphaned" "WARN"
                    }

                    $processAlive = $false
                    if ($issue.SubAgentProcessId) {
                        $processAlive = Test-SubAgentProcessRunning -ProcessId $issue.SubAgentProcessId
                    }

                    if (-not $processAlive) {
                        $fromState = $issue.State
                        $issue.LastError = "Orphaned: process $(if ($issue.SubAgentProcessId) { "PID $($issue.SubAgentProcessId) dead" } else { "no PID" }) from previous run"
                        $issue.SubAgentProcessId = $null
                        Set-IssueStateTransition -IssueState $issue -ToState "ORPHANED" -BatchId $BatchId -Reason "Resume: process dead"
                        $orphanCount++
                        Write-BatchLog "Issue ${issueId}: ORPHANED ($fromState -> ORPHANED)" "WARN"
                    } else {
                        Write-BatchLog "Issue ${issueId}: Process PID $($issue.SubAgentProcessId) still alive, adopting" "INFO"
                        $adoptedWorkers += @{
                            IssueId = $issueId
                            ProcessId = $issue.SubAgentProcessId
                            RetryCount = $issue.RetryCount
                        }
                    }
                }
            }
            if ($orphanCount -gt 0) {
                Write-BatchLog "$orphanCount orphaned worker(s) detected, recovery will be attempted" "WARN"
            }
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
                $batchState.HostAgent = $providerSelection.HostAgent
                $batchState.NativeCapability = $providerSelection.NativeSubagentCapability
                $batchState.ExplicitProviderConfigured = $providerSelection.ExplicitProviderConfigured
                $batchState.ConfiguredProvider = $configuredProvider
                $batchState.SelectedProvider = $providerSelection.SelectedProvider
                $batchState.SelectedMechanism = $providerSelection.SelectedMechanism
                $batchState.SelectionReason = $providerSelection.SelectionReason
                Set-BatchStateTransition -BatchState $batchState -ToState "PLANNING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed: $_" }
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
                    try { Set-BatchStateTransition -BatchState $batchState -ToState "FAILED" } catch { $batchState.State = "FAILED" }
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

                Set-BatchStateTransition -BatchState $batchState -ToState "SCHEDULING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed: $_" }
            }

            "SCHEDULING" {
                Write-BatchLog "=== Phase: Scheduling ===" "INFO"

                if ($providerSelection.Blocked) {
                    foreach ($issueId in $issueStates.Keys) {
                        if ($issueStates[$issueId].State -notin @("COMPLETED", "FAILED", "BLOCKED")) {
                            Set-IssueStateTransition -IssueState $issueStates[$issueId] -ToState "BLOCKED" -BatchId $BatchId -Reason $providerSelection.SelectionReason
                            $issueStates[$issueId].LastError = $providerSelection.SelectionReason
                            $issueStates[$issueId].LaunchStatus = "BLOCKED"
                            $issueStates[$issueId].ExecutionStatus = "NOT_STARTED"
                            $issueStates[$issueId].FailureClassification = "provider_selection_blocked"
                            $issueStates[$issueId].SelectionReason = $providerSelection.SelectionReason
                            $issueStates[$issueId].ConfiguredProvider = $configuredProvider
                            $issueStates[$issueId].ExplicitProviderConfigured = $providerSelection.ExplicitProviderConfigured
                        }
                    }
                    $batchState.BlockedCount = @($issueStates.Values | Where-Object { $_.State -eq "BLOCKED" }).Count
                    $batchState.FailureReason = $providerSelection.SelectionReason
                    $batchState.State = "FAILED"
                    Save-BatchState -State $batchState -FilePath $batchStateFile
                    Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                    Write-BatchLog "Worker launch: BLOCKED; Issue execution: NOT STARTED" "WARN"
                    return
                }

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

                Set-BatchStateTransition -BatchState $batchState -ToState "RUNNING"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed: $_" }
            }

            "RUNNING" {
                Write-BatchLog "=== Phase: Running ===" "INFO"

                $scheduler = New-BatchScheduler -MaxConcurrency $MaxConcurrency
                foreach ($issueId in $issueStates.Keys) {
                    Register-SchedulerIssue -Scheduler $scheduler -IssueId $issueId
                }

                $completedIssues = @()
                foreach ($issueId in $issueStates.Keys) {
                    if ($issueStates[$issueId].State -in @("COMPLETED", "PR_READY")) {
                        $completedIssues += $issueId
                    }
                }

                $subAgentWorkerScript = Join-Path $scriptPath "Invoke-SubAgentWorker.ps1"
                $activeProcesses = @{}

                foreach ($adopted in $adoptedWorkers) {
                    $adoptedConfig = New-SubAgentConfig -MaxRetries $MaxRetries
                    $adoptedState = New-SubAgentState -IssueId $adopted.IssueId -Config $adoptedConfig
                    $adoptedState.ProcessId = $adopted.ProcessId
                    $adoptedState.RetryCount = $adopted.RetryCount
                    $adoptedState.State = "SUBAGENT_RUNNING"
                    $activeProcesses[$adopted.IssueId] = $adoptedState
                    Write-BatchLog "Restored tracking for adopted worker $($adopted.IssueId) (PID: $($adopted.ProcessId))" "INFO"
                }

                $allDone = $false
                while (-not $allDone) {
                    foreach ($issueId in $issueStates.Keys) {
                        $issue = $issueStates[$issueId]
                        if ($issue.State -eq "ORPHANED") {
                            $tempConfig = New-SubAgentConfig -MaxRetries $MaxRetries
                            $tempState = New-SubAgentState -IssueId $issueId -Config $tempConfig
                            $tempState.RetryCount = $issue.RetryCount
                            $retryCheck = Test-SubAgentRetryable -SubAgentState $tempState -ErrorCategory "transient"
                            if ($retryCheck.Retryable) {
                                $issue.RetryCount++
                                Write-BatchLog "Issue ${issueId}: Recovering from ORPHANED (attempt $($issue.RetryCount)/$($MaxRetries))" "WARN"
                                $issue.LastError = "Recovering from orphaned state"
                                $issue.SubAgentProcessId = $null
                                Set-IssueStateTransition -IssueState $issue -ToState "WAITING_FOR_SUBAGENT" -BatchId $BatchId -Reason "Recovery: retry eligible"
                            } else {
                                Write-BatchLog "Issue ${issueId}: ORPHANED recovery exhausted ($($retryCheck.Reason))" "ERROR"
                                $issue.LastError = "Orphaned: $($retryCheck.Reason)"
                                Set-IssueStateTransition -IssueState $issue -ToState "SUBAGENT_FAILED" -BatchId $BatchId -Reason "Recovery: retry exhausted"
                                Fail-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -ErrorMessage "Recovery exhausted: $($retryCheck.Reason)"
                            }
                        }
                    }

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
                                $issueStates[$issueId].StartedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                                Write-BatchLog "Dispatching Sub-agent for issue $issueId" "INFO"

                                $subAgentConfig = New-SubAgentConfig -MaxRetries $MaxRetries
                                $subAgentState = New-SubAgentState -IssueId $issueId -Config $subAgentConfig
                                $subAgentState.RetryCount = $issueStates[$issueId].RetryCount

                                $worktreePath = $issueStates[$issueId].WorktreePath
                                $branchName = $issueStates[$issueId].BranchName

                                if (-not $worktreePath -or -not $branchName) {
                                    $issueStates[$issueId].State = "BLOCKED"
                                    $issueStates[$issueId].LastError = "Missing worktree or branch"
                                    Write-BatchLog "Issue $issueId BLOCKED: missing worktree/branch" "ERROR"
                                    Fail-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -ErrorMessage "Missing worktree/branch"
                                    continue
                                }

                                $existingPr = Test-GitPrExists -BranchName $branchName -Repository $Repository
                                if ($existingPr) {
                                    Write-BatchLog "Issue ${issueId}: Found existing PR #$existingPr, recovering state" "INFO"
                                    $issueStates[$issueId].PrNumber = $existingPr
                                    Complete-SchedulerIssue -Scheduler $scheduler -IssueId $issueId
                                    $completedIssues += $issueId
                                    Set-IssueStateTransition -IssueState $issueStates[$issueId] -ToState "PR_READY" -BatchId $BatchId -Reason "Idempotency: existing PR #$existingPr"
                                    continue
                                }

                                if (-not (Test-GitBranchExists -BranchName $branchName -Repository $Repository)) {
                                    Write-BatchLog "Issue ${issueId}: Branch '$branchName' not found, launching new worker" "WARN"
                                }

                                try {
                                    if ($providerSelection.SelectedMechanism -eq "native-subagent") {
                                        $requestResult = New-NativeDispatchRequest `
                                            -IssueId $issueId `
                                            -IssueNumber $issueStates[$issueId].IssueNumber `
                                            -WorktreePath $worktreePath `
                                            -BranchName $branchName `
                                            -Prompt "Implement Issue #$($issueStates[$issueId].IssueNumber): $($issueStates[$issueId].Description)" `
                                            -ResultFile (Join-Path $worktreePath ".subagent" "result.json")
                                        $issueStates[$issueId].State = "READY_FOR_NATIVE_DISPATCH"
                                        $issueStates[$issueId].LaunchStatus = "READY_FOR_NATIVE_DISPATCH"
                                        $issueStates[$issueId].ExecutionStatus = "NOT_STARTED"
                                        $issueStates[$issueId].FailureClassification = $null
                                        $issueStates[$issueId].SelectedProvider = $providerSelection.SelectedProvider
                                        $issueStates[$issueId].SelectedMechanism = "native-subagent"
                                        $issueStates[$issueId].SelectionReason = $providerSelection.SelectionReason
                                        $issueStates[$issueId].HostAgent = $providerSelection.HostAgent
                                        $issueStates[$issueId].NativeCapability = $providerSelection.NativeSubagentCapability
                                        $issueStates[$issueId].ConfiguredProvider = $configuredProvider
                                        $issueStates[$issueId].ExplicitProviderConfigured = $providerSelection.ExplicitProviderConfigured
                                        $issueStates[$issueId].DispatchDeadline = (Get-Date).ToUniversalTime().AddMinutes($subAgentConfig.TimeoutMinutes)
                                        $issueStates[$issueId].DispatchRequest = $requestResult.RequestFile
                                        $issueStates[$issueId].SubAgentProcessId = $null
                                        Write-BatchLog "Native dispatch request ready for ${issueId}: $($requestResult.RequestFile)" "SUCCESS"
                                    } else {
                                        $launchResult = Invoke-SubAgentLaunch -IssueId $issueId -IssueNumber $issueStates[$issueId].IssueNumber -Description $issueStates[$issueId].Description -WorktreePath $worktreePath -BranchName $branchName -SubAgentScript $subAgentWorkerScript -TimeoutMinutes $subAgentConfig.TimeoutMinutes

                                        $subAgentState.ProcessId = $launchResult.ProcessId
                                        $subAgentState.State = "SUBAGENT_RUNNING"
                                        $subAgentState.StartedAt = $launchResult.StartedAt
                                        $issueStates[$issueId].State = "SUBAGENT_RUNNING"
                                        $issueStates[$issueId].SubAgentProcessId = $launchResult.ProcessId
                                        $issueStates[$issueId].LaunchStatus = "STARTED"
                                        $issueStates[$issueId].ExecutionStatus = "STARTED"
                                        $issueStates[$issueId].SelectedProvider = $providerSelection.SelectedProvider
                                        $issueStates[$issueId].SelectedMechanism = "provider-adapter"
                                        $issueStates[$issueId].SelectionReason = $providerSelection.SelectionReason
                                        $activeProcesses[$issueId] = $subAgentState

                                        Write-BatchLog "Sub-agent for $issueId started (PID: $($launchResult.ProcessId))" "SUCCESS"
                                    }
                                } catch {
                                    $issueStates[$issueId].State = "FAILED"
                                    $issueStates[$issueId].LaunchStatus = "FAILED"
                                    $issueStates[$issueId].ExecutionStatus = "NOT_STARTED"
                                    $issueStates[$issueId].FailureClassification = "launch_failure"
                                    $issueStates[$issueId].LastError = "Launch failed: $($_.Exception.Message)"
                                    Write-BatchLog "Issue $issueId launch FAILED (execution NOT_STARTED): $($_.Exception.Message)" "ERROR"
                                    Fail-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -ErrorMessage $_.Exception.Message
                                }
                            }
                        }
                    }

                    $activeIssues = @()
                    foreach ($issueId in $issueStates.Keys) {
                        $state = $issueStates[$issueId].State
                        if ($state -in @("READY_FOR_NATIVE_DISPATCH", "DISPATCHED", "SUBAGENT_STARTING", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING")) {
                            $activeIssues += $issueId
                        }
                    }

                    $newlyCompleted = @()
                    foreach ($issueId in $activeIssues) {
                        $issue = $issueStates[$issueId]
                        $worktreePath = $issue.WorktreePath
                        $resultFile = if ($worktreePath) { Join-Path $worktreePath ".subagent" "result.json" } else { $null }

                        # Native dispatch is host-owned. Reflect the lifecycle
                        # written by the host agent without starting a process.
                        if ($issue.DispatchRequest -and (Test-Path $issue.DispatchRequest)) {
                            try {
                                $dispatch = Get-Content $issue.DispatchRequest -Raw | ConvertFrom-Json
                                $stateProgression = Get-NativeDispatchStateProgression -IssueState $issue.State -RequestStatus $dispatch.Status
                                foreach ($nextState in $stateProgression) {
                                    $reason = if ($nextState -eq "DISPATCHED") {
                                        "Host native Task/Subagent accepted dispatch"
                                    } else {
                                        "Host native worker started"
                                    }
                                    Set-IssueStateTransition -IssueState $issue -ToState $nextState -BatchId $BatchId -Reason $reason
                                    if ($nextState -eq "DISPATCHED") {
                                        $issue.LaunchStatus = "DISPATCHED"
                                        $issue.ExecutionStatus = "STARTED"
                                    } elseif ($nextState -eq "SUBAGENT_RUNNING") {
                                        $issue.LaunchStatus = "STARTED"
                                        $issue.ExecutionStatus = "STARTED"
                                    }
                                }
                            } catch {
                                Write-BatchLog "Issue ${issueId}: invalid native dispatch status - $($_.Exception.Message)" "WARN"
                            }
                        }

                        if ($issue.State -in @("READY_FOR_NATIVE_DISPATCH", "DISPATCHED") -and $issue.DispatchDeadline -and (Get-Date).ToUniversalTime() -gt [datetime]$issue.DispatchDeadline) {
                            Set-IssueStateTransition -IssueState $issue -ToState "FAILED" -BatchId $BatchId -Reason "Native dispatch deadline exceeded"
                            $issue.LaunchStatus = "FAILED"
                            $issue.ExecutionStatus = "NOT_STARTED"
                            $issue.FailureClassification = "launch_failure"
                            $issue.LastError = "Native dispatch deadline exceeded"
                            Fail-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -ErrorMessage $issue.LastError
                            Write-BatchLog "Issue ${issueId}: native dispatch timed out" "ERROR"
                            continue
                        }

                        if ($resultFile -and (Test-Path $resultFile)) {
                            $result = Get-SubAgentResult -ResultFile $resultFile
                            if ($null -ne $result -and $result.ContainsKey("Success")) {
                                if ($result.Success -and $result.PrNumber -and $result.CommitSha) {
                                    $issue.State = "PR_READY"
                                    $issue.PrNumber = $result.PrNumber
                                    $issue.CommitSha = $result.CommitSha
                                    $issue.CompletedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                                    $issue.Report = $result.Report
                                    $completedIssues += $issueId
                                    $newlyCompleted += $issueId
                                    Complete-SchedulerIssue -Scheduler $scheduler -IssueId $issueId
                                    if ($activeProcesses.ContainsKey($issueId)) {
                                        $activeProcesses.Remove($issueId)
                                    }
                                    Write-BatchLog "Issue ${issueId}: PR #$($issue.PrNumber) ready (SHA: $($issue.CommitSha))" "SUCCESS"
                                } elseif (-not $result.Success) {
                                    $processId = $issue.SubAgentProcessId
                                    if (($processId -and -not (Test-SubAgentProcessRunning -ProcessId $processId)) -or $issue.SelectedMechanism -eq "native-subagent") {
                                        $errorMsg = if ($result.ContainsKey("Error")) { $result.Error } else { "Sub-agent failed" }
                                        $errorCategory = if ($result.ContainsKey("FailureClassification") -and $result.FailureClassification) { $result.FailureClassification } else { Get-SubAgentFailureCategory -ErrorMessage $errorMsg }
                                        $subAgentState = if ($activeProcesses.ContainsKey($issueId)) { $activeProcesses[$issueId] } else { $null }

                                        if ($null -ne $subAgentState) {
                                            $retryCheck = Test-SubAgentRetryable -SubAgentState $subAgentState -ErrorCategory $errorCategory
                                            if ($retryCheck.Retryable) {
                                                Write-BatchLog "Issue ${issueId}: Retrying (category: $errorCategory, attempt $($subAgentState.RetryCount + 1)/$($subAgentState.MaxRetries))" "WARN"
                                                $subAgentState = Invoke-SubAgentRetry -SubAgentState $subAgentState -ErrorCategory $errorCategory
                                                $activeProcesses[$issueId] = $subAgentState
                                                $issue.RetryCount = $subAgentState.RetryCount
                                                $issue.State = $subAgentState.State
                                                $issue.LastError = $subAgentState.LastError
                                                $issue.SubAgentProcessId = $null
                                                $resultFilePath = Join-Path $worktreePath ".subagent" "result.json"
                                                if (Test-Path $resultFilePath) {
                                                    Remove-Item $resultFilePath -Force
                                                }
                                            } else {
                                                $issue.State = "SUBAGENT_FAILED"
                                                $issue.LastError = $retryCheck.Reason
                                                Fail-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -ErrorMessage $retryCheck.Reason
                                                if ($activeProcesses.ContainsKey($issueId)) {
                                                    $activeProcesses.Remove($issueId)
                                                }
                                                Write-BatchLog "Issue ${issueId}: FAILED ($($retryCheck.Reason))" "ERROR"
                                            }
                                        } else {
                                            if ($issue.SelectedMechanism -eq "native-subagent" -and
                                                $issue.State -in @("READY_FOR_NATIVE_DISPATCH", "DISPATCHED")) {
                                                Set-IssueStateTransition -IssueState $issue -ToState "FAILED" -BatchId $BatchId -Reason $errorMsg
                                                $issue.LaunchStatus = "FAILED"
                                                $issue.ExecutionStatus = "NOT_STARTED"
                                                $issue.FailureClassification = "launch_failure"
                                            } else {
                                                $issue.State = "SUBAGENT_FAILED"
                                            }
                                            $issue.LastError = $errorMsg
                                            Fail-SchedulerIssue -Scheduler $scheduler -IssueId $issueId -ErrorMessage $errorMsg
                                            Write-BatchLog "Issue ${issueId}: FAILED ($errorMsg)" "ERROR"
                                        }
                                    }
                                }
                            } else {
                                $processId = $issue.SubAgentProcessId
                                if ($processId -and -not (Test-SubAgentProcessRunning -ProcessId $processId)) {
                                    Write-BatchLog "Issue ${issueId}: Corrupt result.json, treating as orphaned" "WARN"
                                    $issue.LastError = "Corrupt or incomplete result.json"
                                    $issue.SubAgentProcessId = $null
                                    Remove-Item $resultFile -Force -ErrorAction SilentlyContinue
                                    Set-IssueStateTransition -IssueState $issue -ToState "ORPHANED" -BatchId $BatchId -Reason "Corrupt result.json"
                                }
                            }
                        } elseif ($issue.SubAgentProcessId -and -not (Test-SubAgentProcessRunning -ProcessId $issue.SubAgentProcessId)) {
                            $orphanCheck = Test-OrphanedProcess -ProcessId $issue.SubAgentProcessId -ResultFile $(if ($worktreePath) { Join-Path $worktreePath ".subagent" "result.json" } else { "" })
                            if ($orphanCheck.IsOrphaned) {
                                $fromState = $issue.State
                                $issue.LastError = $orphanCheck.Reason
                                $issue.SubAgentProcessId = $null
                                Set-IssueStateTransition -IssueState $issue -ToState "ORPHANED" -BatchId $BatchId -Reason $orphanCheck.Reason
                                Write-BatchLog "Issue ${issueId}: ORPHANED ($fromState -> ORPHANED: $($orphanCheck.Reason))" "WARN"
                            }
                        }
                    }

                    $allActive = @()
                    foreach ($issueId in $issueStates.Keys) {
                        $state = $issueStates[$issueId].State
                        if ($state -notin @("COMPLETED", "FAILED", "BLOCKED", "SUBAGENT_FAILED", "PR_READY")) {
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
                    try {
                        Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries
                    } catch { Write-Warning "Checkpoint save failed: $_" }
                }

                $batchState.CompletedCount = ($issueStates.Values | Where-Object { $_.State -eq "COMPLETED" }).Count
                $batchState.FailedCount = ($issueStates.Values | Where-Object { $_.State -in @("FAILED", "SUBAGENT_FAILED") }).Count
                $batchState.BlockedCount = ($issueStates.Values | Where-Object { $_.State -eq "BLOCKED" }).Count

                $prReadyIssues = @()
                foreach ($issueId in $issueStates.Keys) {
                    if ($issueStates[$issueId].State -eq "PR_READY") {
                        $prReadyIssues += $issueId
                    }
                }

                if ($prReadyIssues.Count -gt 0 -and $batchState.FailedCount -eq 0 -and $batchState.BlockedCount -eq 0) {
                    Set-BatchStateTransition -BatchState $batchState -ToState "WAITING_FOR_MERGE"
                } elseif ($prReadyIssues.Count -gt 0) {
                    Set-BatchStateTransition -BatchState $batchState -ToState "WAITING_FOR_MERGE"
                } else {
                    try { Set-BatchStateTransition -BatchState $batchState -ToState "FAILED" } catch { $batchState.State = "FAILED" }
                    $batchState.FailureReason = "$($batchState.FailedCount) failed, $($batchState.BlockedCount) blocked"
                }

                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try {
                    Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries
                } catch { Write-Warning "Checkpoint save failed: $_" }
            }

            "WAITING_FOR_MERGE" {
                Write-BatchLog "=== Phase: Waiting for Merge Approval ===" "INFO"

                $prReadyIssues = @()
                foreach ($issueId in $issueStates.Keys) {
                    if ($issueStates[$issueId].State -eq "PR_READY") {
                        $prReadyIssues += $issueId
                    }
                }

                if ($prReadyIssues.Count -eq 0) {
                    Write-BatchLog "No PRs ready for merge" "WARN"
                    try { Set-BatchStateTransition -BatchState $batchState -ToState "FAILED" } catch { $batchState.State = "FAILED" }
                    $batchState.FailureReason = "No PRs ready for merge"
                    Save-BatchState -State $batchState -FilePath $batchStateFile
                    Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                    return
                }

                Write-BatchLog "Checking approval status for $($prReadyIssues.Count) PRs..." "INFO"
                $allApproved = $true
                $approvedIssues = @()
                $unapprovedIssues = @()

                foreach ($issueId in $prReadyIssues) {
                    $issue = $issueStates[$issueId]
                    $prNumber = $issue.PrNumber

                    if (-not $prNumber) {
                        Write-BatchLog "Issue ${issueId}: No PR number" "ERROR"
                        $allApproved = $false
                        $unapprovedIssues += $issueId
                        continue
                    }

                    try {
                        $prJson = & gh pr view $prNumber --json "reviewDecision,reviews,headRefOid" 2>$null
                        if ($LASTEXITCODE -ne 0) {
                            Write-BatchLog "Issue ${issueId}: Failed to query PR #$prNumber" "ERROR"
                            $allApproved = $false
                            $unapprovedIssues += $issueId
                            continue
                        }

                        $pr = $prJson | ConvertFrom-Json
                        $reviewDecision = $pr.reviewDecision
                        $headSha = $pr.headRefOid

                        $isApproved = ($reviewDecision -eq "APPROVED")

                        if (-not $isApproved) {
                            Write-BatchLog "Issue ${issueId}: PR #$prNumber not yet approved (status: $reviewDecision)" "WARN"
                            $allApproved = $false
                            $unapprovedIssues += $issueId
                            continue
                        }

                        if ($issue.CommitSha -and $headSha -ne $issue.CommitSha) {
                            Write-BatchLog "Issue ${issueId}: PR #$prNumber HEAD SHA changed ($($issue.CommitSha) -> $headSha). Approval invalidated." "WARN"
                            $issue.ApprovedCommitSha = $null
                            $allApproved = $false
                            $unapprovedIssues += $issueId
                            continue
                        }

                        $issue.ApprovedCommitSha = $issue.CommitSha
                        $approvedIssues += $issueId
                        Write-BatchLog "Issue ${issueId}: PR #$prNumber approved (SHA: $($issue.CommitSha))" "SUCCESS"
                    } catch {
                        Write-BatchLog "Issue ${issueId}: Error checking approval - $($_.Exception.Message)" "ERROR"
                        $allApproved = $false
                        $unapprovedIssues += $issueId
                    }
                }

                foreach ($issueId in $approvedIssues) {
                    $issueStates[$issueId].State = "READY_FOR_MERGE"
                }
                foreach ($issueId in $unapprovedIssues) {
                    $issueStates[$issueId].State = "WAITING_FOR_APPROVAL"
                }

                if ($allApproved) {
                    Write-BatchLog "All PRs approved. Proceeding to merge." "SUCCESS"
                    Set-BatchStateTransition -BatchState $batchState -ToState "MERGING"
                } else {
                    Write-BatchLog "$($unapprovedIssues.Count) PR(s) not yet approved. Please approve, then re-run." "INFO"
                    Write-BatchLog "Unapproved: $($unapprovedIssues -join ', ')" "INFO"
                }

                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed: $_" }
            }

            "MERGING" {
                Write-BatchLog "=== Phase: Merging ===" "INFO"

                $mergeQueue = New-MergeQueue

                foreach ($issueId in $issueStates.Keys) {
                    $issue = $issueStates[$issueId]
                    if ($issue.State -eq "READY_FOR_MERGE") {
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
                        $issueStates[$item.IssueId].CompletedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                    }

                    foreach ($item in $mergeQueue.Failed) {
                        $issueStates[$item.IssueId].State = "FAILED"
                        $issueStates[$item.IssueId].LastError = "Merge failed"
                    }

                    foreach ($item in $mergeQueue.Conflicted) {
                        $issueStates[$item.IssueId].State = "PR_READY"
                        $issueStates[$item.IssueId].LastError = "Conflict during rebase - requires resolution and re-approval"
                        Write-BatchLog "Issue $($item.IssueId): Conflict detected. PR returned to PR_READY for resolution." "WARN"
                    }
                }

                Set-BatchStateTransition -BatchState $batchState -ToState "CLEANUP"
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed: $_" }
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

                Set-BatchStateTransition -BatchState $batchState -ToState "COMPLETED"
                $batchState.CompletedCount = ($issueStates.Values | Where-Object { $_.State -eq "COMPLETED" }).Count
                Save-BatchState -State $batchState -FilePath $batchStateFile
                Save-IssueStates -Issues $issueStates -FilePath $issueStatesFile
                try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed: $_" }
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
                try { Set-BatchStateTransition -BatchState $batchState -ToState "FAILED" } catch { $batchState.State = "FAILED" }
                $batchState.FailureReason = "Unknown state: $($batchState.State)"
                Save-BatchState -State $batchState -FilePath $batchStateFile
            }
        }
    } catch {
        Write-BatchLog "Error: $($_.Exception.Message)" "ERROR"
        Write-BatchLog $_.ScriptStackTrace "ERROR"
        try { Set-BatchStateTransition -BatchState $batchState -ToState "FAILED" } catch { $batchState.State = "FAILED" }
        $batchState.FailureReason = "Exception: $($_.Exception.Message)"
        Save-BatchState -State $batchState -FilePath $batchStateFile
        try { Save-AllCheckpoints -BatchState $batchState -IssueStates $issueStates -Provider $resolvedProvider -MaxRetries $MaxRetries } catch { Write-Warning "Checkpoint save failed during error recovery: $_" }
    }
}

Invoke-BatchOrchestration
