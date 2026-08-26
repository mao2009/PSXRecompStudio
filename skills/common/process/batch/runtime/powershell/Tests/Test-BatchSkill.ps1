#Requires -Version 7.0

<#
.SYNOPSIS
    Unit tests for Batch Orchestrator Skill.

.DESCRIPTION
    Comprehensive tests covering state machines, dependency graph,
    scheduler, Sub-agent lifecycle, persistence, and safety conditions.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "BatchStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "IssueStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "DependencyGraph.psm1") -Force
Import-Module (Join-Path $modulePath "BatchScheduler.psm1") -Force
Import-Module (Join-Path $modulePath "BatchSubAgent.psm1") -Force
Import-Module (Join-Path $modulePath "BatchPersistence.psm1") -Force
Import-Module (Join-Path $modulePath "BatchGitUtilities.psm1") -Force
Import-Module (Join-Path $modulePath "BatchMergeQueue.psm1") -Force

$testResults = @{
    Passed = 0
    Failed = 0
    Tests = @()
}

function Invoke-BatchTest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Test
    )

    Write-Host "Testing: $Name" -ForegroundColor Cyan

    try {
        & $Test
        $testResults.Passed++
        $testResults.Tests += @{ Name = $Name; Status = "PASSED" }
        Write-Host "  PASSED" -ForegroundColor Green
    } catch {
        $testResults.Failed++
        $testResults.Tests += @{ Name = $Name; Status = "FAILED"; Error = $_.Exception.Message }
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ============================================================
# Batch State Machine Tests
# ============================================================
Write-Host "`n=== Batch State Machine Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Batch state machine has all 9 states" -Test {
    $states = Get-AllBatchStates
    $expected = @(
        "BATCH_INITIALIZING", "PLANNING", "SCHEDULING", "RUNNING",
        "WAITING_FOR_MERGE", "MERGING", "CLEANUP", "COMPLETED", "FAILED"
    )
    foreach ($s in $expected) {
        if ($s -notin $states) { throw "Missing batch state: $s" }
    }
    if ($states.Count -ne 9) { throw "Expected 9 states, got $($states.Count)" }
}

Invoke-BatchTest -Name "Batch valid transitions are allowed" -Test {
    $valid = @(
        @{ From = "BATCH_INITIALIZING"; To = "PLANNING" },
        @{ From = "BATCH_INITIALIZING"; To = "FAILED" },
        @{ From = "PLANNING"; To = "SCHEDULING" },
        @{ From = "PLANNING"; To = "FAILED" },
        @{ From = "SCHEDULING"; To = "RUNNING" },
        @{ From = "SCHEDULING"; To = "FAILED" },
        @{ From = "RUNNING"; To = "WAITING_FOR_MERGE" },
        @{ From = "RUNNING"; To = "FAILED" },
        @{ From = "WAITING_FOR_MERGE"; To = "MERGING" },
        @{ From = "WAITING_FOR_MERGE"; To = "COMPLETED" },
        @{ From = "WAITING_FOR_MERGE"; To = "FAILED" },
        @{ From = "MERGING"; To = "CLEANUP" },
        @{ From = "MERGING"; To = "FAILED" },
        @{ From = "CLEANUP"; To = "COMPLETED" },
        @{ From = "CLEANUP"; To = "FAILED" }
    )
    foreach ($t in $valid) {
        $result = Test-ValidBatchTransition -FromState $t.From -ToState $t.To
        if (-not $result) { throw "Transition $($t.From) -> $($t.To) should be valid" }
    }
}

Invoke-BatchTest -Name "Batch invalid transitions are rejected" -Test {
    $invalid = @(
        @{ From = "BATCH_INITIALIZING"; To = "RUNNING" },
        @{ From = "COMPLETED"; To = "PLANNING" },
        @{ From = "FAILED"; To = "RUNNING" },
        @{ From = "CLEANUP"; To = "RUNNING" }
    )
    foreach ($t in $invalid) {
        $result = Test-ValidBatchTransition -FromState $t.From -ToState $t.To
        if ($result) { throw "Transition $($t.From) -> $($t.To) should be invalid" }
    }
}

Invoke-BatchTest -Name "Batch terminal states have no transitions" -Test {
    $terminals = @("COMPLETED", "FAILED")
    foreach ($s in $terminals) {
        $transitions = Get-ValidBatchTransitions -State $s
        if ($transitions.Count -ne 0) { throw "Terminal state $s should have no transitions" }
    }
}

Invoke-BatchTest -Name "Get-BatchStateDefinition returns complete definition" -Test {
    $def = Get-BatchStateDefinition
    if ($def.Count -ne 9) { throw "Expected 9 definitions, got $($def.Count)" }
    foreach ($state in $def.Keys) {
        if (-not $def[$state].Description) { throw "Missing description for $state" }
        if ($null -eq $def[$state].IsTerminal) { throw "Missing IsTerminal for $state" }
    }
}

# ============================================================
# Issue State Machine Tests
# ============================================================
Write-Host "`n=== Issue State Machine Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Issue state machine has all 13 states" -Test {
    $states = Get-AllIssueStates
    $expected = @(
        "SUBAGENT_STARTING", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING",
        "SUBAGENT_FAILED", "WAITING_FOR_SUBAGENT", "WAITING_DEPENDENCY",
        "PR_READY", "WAITING_FOR_APPROVAL", "READY_FOR_MERGE",
        "MERGING", "COMPLETED", "BLOCKED", "FAILED"
    )
    foreach ($s in $expected) {
        if ($s -notin $states) { throw "Missing issue state: $s" }
    }
    if ($states.Count -ne 13) { throw "Expected 13 states, got $($states.Count)" }
}

Invoke-BatchTest -Name "Issue terminal states have no transitions" -Test {
    $terminals = @("SUBAGENT_FAILED", "COMPLETED", "FAILED")
    foreach ($s in $terminals) {
        $transitions = Get-ValidIssueTransitions -State $s
        if ($transitions.Count -ne 0) { throw "Terminal state $s should have no transitions" }
    }
}

Invoke-BatchTest -Name "Issue valid transitions are correct" -Test {
    $valid = @(
        @{ From = "SUBAGENT_STARTING"; To = "SUBAGENT_RUNNING" },
        @{ From = "SUBAGENT_STARTING"; To = "SUBAGENT_RETRYING" },
        @{ From = "SUBAGENT_STARTING"; To = "SUBAGENT_FAILED" },
        @{ From = "SUBAGENT_RUNNING"; To = "PR_READY" },
        @{ From = "SUBAGENT_RUNNING"; To = "SUBAGENT_RETRYING" },
        @{ From = "SUBAGENT_RUNNING"; To = "SUBAGENT_FAILED" },
        @{ From = "SUBAGENT_RETRYING"; To = "SUBAGENT_STARTING" },
        @{ From = "SUBAGENT_RETRYING"; To = "SUBAGENT_FAILED" },
        @{ From = "WAITING_FOR_SUBAGENT"; To = "SUBAGENT_STARTING" },
        @{ From = "WAITING_FOR_SUBAGENT"; To = "BLOCKED" },
        @{ From = "WAITING_DEPENDENCY"; To = "SUBAGENT_STARTING" },
        @{ From = "PR_READY"; To = "WAITING_FOR_APPROVAL" },
        @{ From = "WAITING_FOR_APPROVAL"; To = "READY_FOR_MERGE" },
        @{ From = "WAITING_FOR_APPROVAL"; To = "PR_READY" },
        @{ From = "READY_FOR_MERGE"; To = "MERGING" },
        @{ From = "MERGING"; To = "COMPLETED" },
        @{ From = "MERGING"; To = "FAILED" },
        @{ From = "MERGING"; To = "PR_READY" }
    )
    foreach ($t in $valid) {
        $result = Test-ValidIssueTransition -FromState $t.From -ToState $t.To
        if (-not $result) { throw "Transition $($t.From) -> $($t.To) should be valid" }
    }
}

Invoke-BatchTest -Name "Test-IssueStateActive correctly identifies active states" -Test {
    $active = @("SUBAGENT_STARTING", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING",
                 "WAITING_FOR_SUBAGENT", "WAITING_DEPENDENCY", "PR_READY",
                 "WAITING_FOR_APPROVAL", "READY_FOR_MERGE", "MERGING")
    $inactive = @("SUBAGENT_FAILED", "COMPLETED", "BLOCKED", "FAILED")

    foreach ($s in $active) {
        if (-not (Test-IssueStateActive -State $s)) { throw "$s should be active" }
    }
    foreach ($s in $inactive) {
        if (Test-IssueStateActive -State $s) { throw "$s should not be active" }
    }
}

# ============================================================
# Dependency Graph Tests
# ============================================================
Write-Host "`n=== Dependency Graph Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-DependencyGraph creates empty graph" -Test {
    $graph = New-DependencyGraph
    if ($graph.Nodes.Count -ne 0) { throw "New graph should have no nodes" }
    if ($graph.Edges.Count -ne 0) { throw "New graph should have no edges" }
}

Invoke-BatchTest -Name "Add-DependencyNode adds node" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    if ($graph.Nodes.Count -ne 1) { throw "Graph should have 1 node" }
    if (-not $graph.Nodes.ContainsKey("A")) { throw "Node A should exist" }
}

Invoke-BatchTest -Name "Add-DependencyNode rejects duplicate" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    try {
        Add-DependencyNode -Graph $graph -IssueId "A"
        throw "Should have thrown"
    } catch {
        if (-not $_.Exception.Message -match "already exists") { throw "Wrong error: $($_.Exception.Message)" }
    }
}

Invoke-BatchTest -Name "Add-DependencyEdge adds edge" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    if ($graph.Edges["A"].Count -ne 1) { throw "A should have 1 dependency" }
    if ($graph.Edges["A"][0] -ne "B") { throw "A should depend on B" }
}

Invoke-BatchTest -Name "Add-DependencyEdge rejects self-dependency" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    try {
        Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "A"
        throw "Should have thrown"
    } catch {
        if (-not $_.Exception.Message -match "cannot depend on itself") { throw "Wrong error" }
    }
}

Invoke-BatchTest -Name "Add-DependencyEdge rejects missing node" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    try {
        Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
        throw "Should have thrown"
    } catch {
        if (-not $_.Exception.Message -match "not found") { throw "Wrong error" }
    }
}

Invoke-BatchTest -Name "No cycle in simple DAG" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    $result = Test-DependencyCycle -Graph $graph
    if ($result.HasCycle) { throw "Should have no cycle" }
}

Invoke-BatchTest -Name "Cycle detected in circular dependency" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyNode -Graph $graph -IssueId "C"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    Add-DependencyEdge -Graph $graph -FromIssue "B" -ToIssue "C"
    Add-DependencyEdge -Graph $graph -FromIssue "C" -ToIssue "A"
    $result = Test-DependencyCycle -Graph $graph
    if (-not $result.HasCycle) { throw "Should detect cycle" }
    if ($result.CyclePath.Count -eq 0) { throw "Should report cycle path" }
}

Invoke-BatchTest -Name "Topological sort returns correct order" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyNode -Graph $graph -IssueId "C"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    Add-DependencyEdge -Graph $graph -FromIssue "B" -ToIssue "C"
    $sorted = Get-DependencyTopologicalSort -Graph $graph
    if ($sorted.Count -ne 3) { throw "Expected 3 items" }
    $indexOfA = $sorted.IndexOf("A")
    $indexOfB = $sorted.IndexOf("B")
    $indexOfC = $sorted.IndexOf("C")
    if ($indexOfC -ge $indexOfB) { throw "C should come before B" }
    if ($indexOfB -ge $indexOfA) { throw "B should come before A" }
}

Invoke-BatchTest -Name "Topological sort rejects cyclic graph" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    Add-DependencyEdge -Graph $graph -FromIssue "B" -ToIssue "A"
    try {
        Get-DependencyTopologicalSort -Graph $graph
        throw "Should have thrown"
    } catch {
        if (-not $_.Exception.Message -match "cycle") { throw "Wrong error" }
    }
}

Invoke-BatchTest -Name "Concurrency groups are correct for independent issues" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyNode -Graph $graph -IssueId "C"
    $groups = Get-DependencyConcurrencyGroups -Graph $graph
    if ($groups.Count -ne 1) { throw "Expected 1 group for independent issues" }
    if ($groups[0].Count -ne 3) { throw "Group should have 3 issues" }
}

Invoke-BatchTest -Name "Concurrency groups are correct for dependent chain" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyNode -Graph $graph -IssueId "C"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    Add-DependencyEdge -Graph $graph -FromIssue "B" -ToIssue "C"
    $groups = Get-DependencyConcurrencyGroups -Graph $graph
    if ($groups.Count -ne 3) { throw "Expected 3 groups for chain" }
    if ($groups[0][0] -ne "C") { throw "Wave 1 should be C (no deps)" }
    if ($groups[1][0] -ne "B") { throw "Wave 2 should be B" }
    if ($groups[2][0] -ne "A") { throw "Wave 3 should be A" }
}

Invoke-BatchTest -Name "Get-DependencyReadyIssues returns independent issues first" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyNode -Graph $graph -IssueId "C"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    $ready = Get-DependencyReadyIssues -Graph $graph -CompletedIssues @()
    if ("A" -in $ready) { throw "A should not be ready (depends on B)" }
    if ("B" -notin $ready) { throw "B should be ready" }
    if ("C" -notin $ready) { throw "C should be ready" }
}

Invoke-BatchTest -Name "Get-DependencyReadyIssues after completing B" -Test {
    $graph = New-DependencyGraph
    Add-DependencyNode -Graph $graph -IssueId "A"
    Add-DependencyNode -Graph $graph -IssueId "B"
    Add-DependencyEdge -Graph $graph -FromIssue "A" -ToIssue "B"
    $ready = Get-DependencyReadyIssues -Graph $graph -CompletedIssues @("B")
    if ("A" -notin $ready) { throw "A should be ready after B completed" }
}

# ============================================================
# Scheduler Tests
# ============================================================
Write-Host "`n=== Scheduler Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-BatchScheduler creates scheduler" -Test {
    $scheduler = New-BatchScheduler -MaxConcurrency 3
    if ($scheduler.MaxConcurrency -ne 3) { throw "MaxConcurrency should be 3" }
    if ($scheduler.RunningSlots -ne 0) { throw "RunningSlots should be 0" }
}

Invoke-BatchTest -Name "Slot available when empty" -Test {
    $scheduler = New-BatchScheduler -MaxConcurrency 2
    if (-not (Test-SchedulerSlotAvailable -Scheduler $scheduler)) { throw "Should have slot available" }
}

Invoke-BatchTest -Name "Slot not available when full" -Test {
    $scheduler = New-BatchScheduler -MaxConcurrency 1
    Register-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Start-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    if (Test-SchedulerSlotAvailable -Scheduler $scheduler) { throw "Should not have slot available" }
}

Invoke-BatchTest -Name "Slot freed after completion" -Test {
    $scheduler = New-BatchScheduler -MaxConcurrency 1
    Register-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Start-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Complete-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    if (-not (Test-SchedulerSlotAvailable -Scheduler $scheduler)) { throw "Slot should be freed" }
}

Invoke-BatchTest -Name "Slot freed after failure" -Test {
    $scheduler = New-BatchScheduler -MaxConcurrency 1
    Register-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Start-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Fail-SchedulerIssue -Scheduler $scheduler -IssueId "A" -ErrorMessage "test"
    if (-not (Test-SchedulerSlotAvailable -Scheduler $scheduler)) { throw "Slot should be freed" }
}

Invoke-BatchTest -Name "Get-SchedulerStatus returns correct counts" -Test {
    $scheduler = New-BatchScheduler -MaxConcurrency 3
    Register-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Register-SchedulerIssue -Scheduler $scheduler -IssueId "B"
    Start-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    Complete-SchedulerIssue -Scheduler $scheduler -IssueId "A"
    $status = Get-SchedulerStatus -Scheduler $scheduler
    if ($status.CompletedCount -ne 1) { throw "CompletedCount should be 1" }
    if ($status.TotalCount -ne 2) { throw "TotalCount should be 2" }
}

# ============================================================
# Sub-agent Tests
# ============================================================
Write-Host "`n=== Sub-agent Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-SubAgentConfig creates config" -Test {
    $config = New-SubAgentConfig -MaxRetries 3 -TimeoutMinutes 30
    if ($config.MaxRetries -ne 3) { throw "MaxRetries should be 3" }
    if ($config.TimeoutMinutes -ne 30) { throw "TimeoutMinutes should be 30" }
}

Invoke-BatchTest -Name "New-SubAgentState creates state" -Test {
    $config = New-SubAgentConfig
    $state = New-SubAgentState -IssueId "test-1" -Config $config
    if ($state.IssueId -ne "test-1") { throw "IssueId mismatch" }
    if ($state.RetryCount -ne 0) { throw "RetryCount should be 0" }
    if ($state.State -ne "SUBAGENT_STARTING") { throw "Initial state should be SUBAGENT_STARTING" }
}

Invoke-BatchTest -Name "Retryable error is retryable" -Test {
    $config = New-SubAgentConfig -MaxRetries 3
    $state = New-SubAgentState -IssueId "test" -Config $config
    $result = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory "timeout"
    if (-not $result.Retryable) { throw "Timeout should be retryable" }
}

Invoke-BatchTest -Name "Non-retryable error is not retryable" -Test {
    $config = New-SubAgentConfig -MaxRetries 3
    $state = New-SubAgentState -IssueId "test" -Config $config
    $result = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory "code_error"
    if ($result.Retryable) { throw "code_error should not be retryable" }
}

Invoke-BatchTest -Name "Retry limit prevents retry" -Test {
    $config = New-SubAgentConfig -MaxRetries 2
    $state = New-SubAgentState -IssueId "test" -Config $config
    $state.RetryCount = 2
    $result = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory "timeout"
    if ($result.Retryable) { throw "Should not retry after limit" }
}

Invoke-BatchTest -Name "Get-SubAgentFailureCategory categorizes timeout" -Test {
    $cat = Get-SubAgentFailureCategory -ErrorMessage "Connection timed out after 30s"
    if ($cat -ne "timeout") { throw "Should be timeout, got $cat" }
}

Invoke-BatchTest -Name "Get-SubAgentFailureCategory categorizes test failure" -Test {
    $cat = Get-SubAgentFailureCategory -ErrorMessage "Test assertion failed: expected 5 but got 3"
    if ($cat -ne "test_failure") { throw "Should be test_failure, got $cat" }
}

Invoke-BatchTest -Name "Get-SubAgentFailureCategory categorizes API error" -Test {
    $cat = Get-SubAgentFailureCategory -ErrorMessage "Rate limit exceeded (429)"
    if ($cat -ne "api_error") { throw "Should be api_error, got $cat" }
}

Invoke-BatchTest -Name "New-SubAgentReport creates template" -Test {
    $report = New-SubAgentReport -IssueId "test-1"
    if ($report.IssueId -ne "test-1") { throw "IssueId mismatch" }
    if ($report.ChangedFiles.Count -ne 0) { throw "ChangedFiles should be empty" }
}

Invoke-BatchTest -Name "Test-SubAgentReportComplete detects missing fields" -Test {
    $report = New-SubAgentReport -IssueId "test"
    $result = Test-SubAgentReportComplete -Report $report
    if ($result.IsComplete) { throw "Empty report should not be complete" }
    if ($result.MissingFields.Count -eq 0) { throw "Should have missing fields" }
}

Invoke-BatchTest -Name "Test-SubAgentReportComplete validates complete report" -Test {
    $report = @{
        InvestigationSummary = "Investigated issue"
        ImplementationSummary = "Implemented feature"
        DesignDecision = "Chose approach X"
        ChangedFiles = @("file1.cs", "file2.cs")
        TestResults = "All tests passed"
        PrNumber = 123
        CommitSha = "abc123"
    }
    $result = Test-SubAgentReportComplete -Report $report
    if (-not $result.IsComplete) { throw "Complete report should pass" }
    if ($result.MissingFields.Count -ne 0) { throw "Should have no missing fields" }
}

# ============================================================
# Sub-agent Launch Tests
# ============================================================
Write-Host "`n=== Sub-agent Launch Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Invoke-SubAgentLaunch creates result directory and returns PID" -Test {
    $tempDir = Join-Path $env:TEMP "test-launch-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $workerScript = Join-Path $scriptPath ".." "Scripts" "Invoke-SubAgentWorker.ps1"
        $result = Invoke-SubAgentLaunch -IssueId "test-launch" -IssueNumber 999 -Description "test" -WorktreePath $tempDir -BranchName "issue/999-test" -SubAgentScript $workerScript -TimeoutMinutes 1
        if ($null -eq $result.ProcessId) { throw "ProcessId should not be null" }
        if ($null -eq $result.StartedAt) { throw "StartedAt should not be null" }
        if ($result.ProcessId -le 0) { throw "ProcessId should be positive" }
        Stop-SubAgentProcess -ProcessId $result.ProcessId -Force $true
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Invoke-BatchTest -Name "Get-SubAgentResult returns null for missing file" -Test {
    $result = Get-SubAgentResult -ResultFile "/nonexistent/result.json"
    if ($null -ne $result) { throw "Should return null for missing file" }
}

Invoke-BatchTest -Name "Get-SubAgentResult reads valid result file" -Test {
    $tempFile = Join-Path $env:TEMP "test-result-$(Get-Random).json"
    try {
        @{ Success = $true; PrNumber = 42; CommitSha = "abc123" } | ConvertTo-Json | Set-Content $tempFile
        $result = Get-SubAgentResult -ResultFile $tempFile
        if (-not $result.Success) { throw "Should be success" }
        if ($result.PrNumber -ne 42) { throw "PrNumber should be 42" }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
    }
}

Invoke-BatchTest -Name "Test-SubAgentProcessRunning returns false for non-existent process" -Test {
    $result = Test-SubAgentProcessRunning -ProcessId 99999999
    if ($result) { throw "Non-existent process should not be running" }
}

Invoke-BatchTest -Name "Stop-SubAgentProcess handles non-existent process gracefully" -Test {
    Stop-SubAgentProcess -ProcessId 99999999 -Force $true
}

# ============================================================
# Backoff Max Cap Tests
# ============================================================
Write-Host "`n=== Backoff Max Cap Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Get-SubAgentBackoffDuration respects max cap" -Test {
    $config = New-SubAgentConfig -MaxRetries 10 -BackoffBaseSeconds 5
    $state = New-SubAgentState -IssueId "test" -Config $config
    $state.RetryCount = 10
    $duration = Get-SubAgentBackoffDuration -SubAgentState $state -MaxBackoffSeconds 120
    if ($duration -gt 120) { throw "Duration $duration should not exceed 120 seconds" }
}

Invoke-BatchTest -Name "Get-SubAgentBackoffDuration increases with retry count" -Test {
    $config = New-SubAgentConfig -MaxRetries 5 -BackoffBaseSeconds 5
    $state = New-SubAgentState -IssueId "test" -Config $config
    $state.RetryCount = 0
    $d0 = Get-SubAgentBackoffDuration -SubAgentState $state -MaxBackoffSeconds 120
    $state.RetryCount = 1
    $d1 = Get-SubAgentBackoffDuration -SubAgentState $state -MaxBackoffSeconds 120
    $state.RetryCount = 2
    $d2 = Get-SubAgentBackoffDuration -SubAgentState $state -MaxBackoffSeconds 120
    if ($d0 -ge $d1) { throw "Backoff should increase with retry count" }
    if ($d1 -ge $d2) { throw "Backoff should increase with retry count" }
}

# ============================================================
# Persistence Tests
# ============================================================
Write-Host "`n=== Persistence Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-BatchState creates initial state" -Test {
    $state = New-BatchState -BatchId "test-batch" -IssueCount 5
    if ($state.BatchId -ne "test-batch") { throw "BatchId mismatch" }
    if ($state.IssueCount -ne 5) { throw "IssueCount mismatch" }
    if ($state.State -ne "BATCH_INITIALIZING") { throw "Initial state should be BATCH_INITIALIZING" }
}

Invoke-BatchTest -Name "New-IssueState creates initial state" -Test {
    $state = New-IssueState -IssueId "issue-1" -IssueNumber 42 -Description "test feature"
    if ($state.IssueId -ne "issue-1") { throw "IssueId mismatch" }
    if ($state.IssueNumber -ne 42) { throw "IssueNumber mismatch" }
    if ($state.State -ne "WAITING_DEPENDENCY") { throw "Initial state should be WAITING_DEPENDENCY" }
}

Invoke-BatchTest -Name "Save and load batch state" -Test {
    $tempFile = Join-Path $env:TEMP "test-batch-state-$(Get-Random).json"
    try {
        $state = New-BatchState -BatchId "test" -IssueCount 3
        $state.State = "RUNNING"
        Save-BatchState -State $state -FilePath $tempFile
        $loaded = Get-BatchState -FilePath $tempFile
        if ($loaded.BatchId -ne "test") { throw "BatchId mismatch" }
        if ($loaded.State -ne "RUNNING") { throw "State mismatch" }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
    }
}

Invoke-BatchTest -Name "Save and load issue states" -Test {
    $tempFile = Join-Path $env:TEMP "test-issue-states-$(Get-Random).json"
    try {
        $issues = @{
            "A" = New-IssueState -IssueId "A" -IssueNumber 1
            "B" = New-IssueState -IssueId "B" -IssueNumber 2
        }
        $issues["A"].State = "COMPLETED"
        Save-IssueStates -Issues $issues -FilePath $tempFile
        $loaded = Get-IssueStates -FilePath $tempFile
        if ($loaded["A"].State -ne "COMPLETED") { throw "Issue A state mismatch" }
        if ($loaded["B"].State -ne "WAITING_DEPENDENCY") { throw "Issue B state mismatch" }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
    }
}

Invoke-BatchTest -Name "Get-BatchState returns null for missing file" -Test {
    $result = Get-BatchState -FilePath "nonexistent-file.json"
    if ($null -ne $result) { throw "Should return null for missing file" }
}

# ============================================================
# Worktree Tests
# ============================================================
Write-Host "`n=== Worktree Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Test-BatchWorktreeCollision detects existing path" -Test {
    $tempDir = Join-Path $env:TEMP "test-wt-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $result = Test-BatchWorktreeCollision -WorktreePath $tempDir -BranchName "issue/999-test"
        if (-not $result.HasCollision) { throw "Should detect collision" }
    } finally {
        Remove-Item $tempDir -Recurse -Force
    }
}

Invoke-BatchTest -Name "Test-BatchWorktreeCollision passes for non-existing" -Test {
    $result = Test-BatchWorktreeCollision -WorktreePath "/nonexistent/path/12345-test" -BranchName "issue/99999-nonexistent-branch"
    if ($result.HasCollision) { throw "Should not detect collision for non-existing" }
}

# ============================================================
# Safety Tests
# ============================================================
Write-Host "`n=== Safety Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Config forbids admin bypass" -Test {
    $configPath = Join-Path $scriptPath ".." ".." ".." "config" "batch-config.json"
    $config = Get-Content $configPath | ConvertFrom-Json

    if (-not $config.merge.forbid_admin_bypass) { throw "Admin bypass should be forbidden" }
    if (-not $config.merge.forbid_force_push) { throw "Force push should be forbidden" }
    if (-not $config.merge.forbid_direct_push) { throw "Direct push should be forbidden" }
    if (-not $config.merge.serial_merge) { throw "Serial merge should be enforced" }
    if (-not $config.merge.require_rebase_before_merge) { throw "Mandatory rebase should be enforced" }
}

Invoke-BatchTest -Name "Config enforces Sub-agent integrity" -Test {
    $configPath = Join-Path $scriptPath ".." ".." ".." "config" "batch-config.json"
    $config = Get-Content $configPath | ConvertFrom-Json

    if (-not $config.subagent.never_substitute_implementation) { throw "Sub-agent substitution should be forbidden" }
    if (-not $config.subagent.require_structured_report) { throw "Structured report should be required" }
}

Invoke-BatchTest -Name "Config enforces approval SHA binding" -Test {
    $configPath = Join-Path $scriptPath ".." ".." ".." "config" "batch-config.json"
    $config = Get-Content $configPath | ConvertFrom-Json

    if (-not $config.approval.sha_bound) { throw "Approval should be SHA-bound" }
    if (-not $config.approval.invalidate_on_rebase) { throw "Approval should invalidate on rebase" }
    if (-not $config.approval.per_issue_independent) { throw "Approval should be per-issue" }
}

# ============================================================
# BLOCKED Recovery Tests
# ============================================================
Write-Host "`n=== BLOCKED Recovery Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "BLOCKED can transition to WAITING_FOR_SUBAGENT" -Test {
    $result = Test-ValidIssueTransition -FromState "BLOCKED" -ToState "WAITING_FOR_SUBAGENT"
    if (-not $result) { throw "BLOCKED -> WAITING_FOR_SUBAGENT should be valid" }
}

Invoke-BatchTest -Name "BLOCKED cannot transition to SUBAGENT_RUNNING" -Test {
    $result = Test-ValidIssueTransition -FromState "BLOCKED" -ToState "SUBAGENT_RUNNING"
    if ($result) { throw "BLOCKED -> SUBAGENT_RUNNING should be invalid" }
}

Invoke-BatchTest -Name "BLOCKED cannot transition to COMPLETED" -Test {
    $result = Test-ValidIssueTransition -FromState "BLOCKED" -ToState "COMPLETED"
    if ($result) { throw "BLOCKED -> COMPLETED should be invalid" }
}

# ============================================================
# BatchMergeQueue Conflicted Tests
# ============================================================
Write-Host "`n=== BatchMergeQueue Conflicted Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-MergeQueue has empty Conflicted array" -Test {
    $queue = New-MergeQueue
    if ($queue.Conflicted.Count -ne 0) { throw "Conflicted should be empty" }
}

Invoke-BatchTest -Name "Get-MergeQueueStatus includes ConflictedCount" -Test {
    $queue = New-MergeQueue
    $status = Get-MergeQueueStatus -Queue $queue
    if ($status.ConflictedCount -ne 0) { throw "ConflictedCount should be 0" }
}

# ============================================================
# State Transition Validation Tests
# ============================================================
Write-Host "`n=== State Transition Validation Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Test-ValidBatchTransition rejects invalid transitions" -Test {
    $invalid = @(
        @{ From = "RUNNING"; To = "PLANNING" },
        @{ From = "CLEANUP"; To = "RUNNING" },
        @{ From = "COMPLETED"; To = "MERGING" },
        @{ From = "FAILED"; To = "RUNNING" }
    )
    foreach ($t in $invalid) {
        $result = Test-ValidBatchTransition -FromState $t.From -ToState $t.To
        if ($result) { throw "$($t.From) -> $($t.To) should be invalid" }
    }
}

Invoke-BatchTest -Name "Test-ValidBatchTransition allows all defined transitions" -Test {
    $valid = @(
        @{ From = "BATCH_INITIALIZING"; To = "PLANNING" },
        @{ From = "BATCH_INITIALIZING"; To = "FAILED" },
        @{ From = "PLANNING"; To = "SCHEDULING" },
        @{ From = "PLANNING"; To = "FAILED" },
        @{ From = "SCHEDULING"; To = "RUNNING" },
        @{ From = "SCHEDULING"; To = "FAILED" },
        @{ From = "RUNNING"; To = "WAITING_FOR_MERGE" },
        @{ From = "RUNNING"; To = "FAILED" },
        @{ From = "WAITING_FOR_MERGE"; To = "MERGING" },
        @{ From = "WAITING_FOR_MERGE"; To = "COMPLETED" },
        @{ From = "WAITING_FOR_MERGE"; To = "FAILED" },
        @{ From = "MERGING"; To = "CLEANUP" },
        @{ From = "MERGING"; To = "FAILED" },
        @{ From = "CLEANUP"; To = "COMPLETED" },
        @{ From = "CLEANUP"; To = "FAILED" }
    )
    foreach ($t in $valid) {
        $result = Test-ValidBatchTransition -FromState $t.From -ToState $t.To
        if (-not $result) { throw "$($t.From) -> $($t.To) should be valid" }
    }
}

# ============================================================
# Summary
# ============================================================
Write-Host "`n=== Test Summary ===" -ForegroundColor Green
Write-Host "Passed: $($testResults.Passed)" -ForegroundColor Green
Write-Host "Failed: $($testResults.Failed)" -ForegroundColor $(if ($testResults.Failed -gt 0) { "Red" } else { "Green" })
Write-Host "Total: $($testResults.Passed + $testResults.Failed)" -ForegroundColor Cyan

if ($testResults.Failed -gt 0) {
    Write-Host "`nFailed Tests:" -ForegroundColor Red
    $testResults.Tests | Where-Object { $_.Status -eq "FAILED" } | ForEach-Object {
        Write-Host "  - $($_.Name): $($_.Error)" -ForegroundColor Red
    }
    exit 1
}

exit 0
