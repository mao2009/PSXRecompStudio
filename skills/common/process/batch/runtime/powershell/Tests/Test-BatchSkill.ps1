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
Import-Module (Join-Path $modulePath "BatchCheckpoint.psm1") -Force

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

Invoke-BatchTest -Name "Issue state machine has all 16 states" -Test {
    $states = Get-AllIssueStates
    $expected = @(
        "SUBAGENT_STARTING", "READY_FOR_NATIVE_DISPATCH", "DISPATCHED", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING",
        "SUBAGENT_FAILED", "ORPHANED", "WAITING_FOR_SUBAGENT", "WAITING_DEPENDENCY",
        "PR_READY", "WAITING_FOR_APPROVAL", "READY_FOR_MERGE",
        "MERGING", "COMPLETED", "BLOCKED", "FAILED"
    )
    foreach ($s in $expected) {
        if ($s -notin $states) { throw "Missing issue state: $s" }
    }
    if ($states.Count -ne 16) { throw "Expected 16 states, got $($states.Count)" }
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
    $inactive = @("SUBAGENT_FAILED", "COMPLETED", "BLOCKED", "FAILED", "ORPHANED")

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

Invoke-BatchTest -Name "Get-BatchState throws for corrupt file" -Test {
    $tempFile = Join-Path $env:TEMP "test-corrupt-state-$(Get-Random).json"
    try {
        Set-Content -Path $tempFile -Value "not valid JSON"
        $threw = $false
        try {
            Get-BatchState -FilePath $tempFile | Out-Null
        } catch {
            $threw = $true
        }
        if (-not $threw) { throw "Should throw for corrupt batch state file" }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
    }
}

Invoke-BatchTest -Name "Get-IssueStates throws for corrupt file" -Test {
    $tempFile = Join-Path $env:TEMP "test-corrupt-issues-$(Get-Random).json"
    try {
        Set-Content -Path $tempFile -Value "not valid JSON"
        $threw = $false
        try {
            Get-IssueStates -FilePath $tempFile | Out-Null
        } catch {
            $threw = $true
        }
        if (-not $threw) { throw "Should throw for corrupt issue states file" }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
    }
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
# Agent Provider Tests
# ============================================================
Write-Host "`n=== Agent Provider Tests ===" -ForegroundColor Green

Import-Module (Join-Path (Split-Path $PSScriptRoot) "Modules" "AgentProvider.psm1") -Force

Invoke-BatchTest -Name "New-ClaudeCodeProvider creates config" -Test {
    $config = New-ClaudeCodeProvider
    if ($config.Name -ne "claude-code") { throw "Name should be claude-code" }
    if ($config.Type -ne "claude-code") { throw "Type should be claude-code" }
    if (-not $config.Executable) { throw "Executable should be set" }
    if ($config.Arguments.Count -eq 0) { throw "Arguments should be provided" }
}

Invoke-BatchTest -Name "New-AgentProviderResult creates result" -Test {
    $result = New-AgentProviderResult `
        -ProviderName "claude-code" `
        -Success $true `
        -ExitCode 0

    if ($result.ProviderName -ne "claude-code") { throw "ProviderName mismatch" }
    if (-not $result.Success) { throw "Success should be true" }
}

Invoke-BatchTest -Name "Claude Code executable resolves" -Test {
    $config = New-ClaudeCodeProvider
    if (-not $config.Executable) { throw "Executable should be set" }
    if (-not (Get-Command $config.Executable -ErrorAction SilentlyContinue)) {
        if (-not (Test-Path $config.Executable)) {
            Write-Host ("  SKIP: Claude Code CLI not installed ({0})" -f $config.Executable) -ForegroundColor Yellow
        }
    }
}

Invoke-BatchTest -Name "Provider selection blocks without native capability or explicit provider" -Test {
    $selection = Resolve-AgentProvider -NativeSubagentAvailable:$false
    if (-not $selection.Blocked) { throw "Selection should be blocked" }
    if ($selection.SelectedProvider) { throw "Blocked selection must not choose a provider" }
    if ($selection.SelectionReason -notmatch "no explicit") { throw "Missing blocked reason" }
}

Invoke-BatchTest -Name "Explicit provider is selected without PATH fallback" -Test {
    $selection = Resolve-AgentProvider -ProviderName "test" -NativeSubagentAvailable:$false
    if ($selection.Blocked -or $selection.SelectedProvider -ne "test") { throw "Explicit provider was not selected" }
    if ($selection.SelectedMechanism -ne "provider-adapter") { throw "Adapter mechanism missing" }
}

Invoke-BatchTest -Name "Launch failures and provider switches are never retryable" -Test {
    $state = New-SubAgentState -IssueId "issue-retry" -Config (New-SubAgentConfig -MaxRetries 3)
    foreach ($category in @("launch_failure", "provider_switch")) {
        $retry = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory $category
        if ($retry.Retryable) { throw "$category must not be retryable" }
    }
}

Invoke-BatchTest -Name "Unknown provider selection fails explicitly" -Test {
    $thrown = $false
    try { Resolve-AgentProvider -ProviderName "future-provider" -NativeSubagentAvailable:$false -ErrorAction Stop | Out-Null } catch { $thrown = $true }
    if (-not $thrown) { throw "Unknown provider must be rejected" }
}

Invoke-BatchTest -Name "Native capability wins over explicit external provider" -Test {
    $selection = Resolve-AgentProvider -HostAgent "codex" -NativeSubagentAvailable:$true -ProviderName "claude-code"
    if ($selection.SelectedProvider -ne "codex" -or $selection.SelectedMechanism -ne "native-subagent") { throw "Native capability did not win" }
}

Invoke-BatchTest -Name "Native dispatch request is host-handled and does not spawn a process" -Test {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "native-dispatch-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $request = New-NativeDispatchRequest -IssueId "native-219" -IssueNumber 219 -WorktreePath $tempDir -BranchName "issue/219-native" -Prompt "Implement" -ResultFile (Join-Path $tempDir ".subagent" "result.json")
        if ($request.Status -ne "READY_FOR_NATIVE_DISPATCH") { throw "Request is not ready" }
        if ($request.SpawnedProcess) { throw "Native request must not spawn a process" }
        $payload = Get-Content $request.RequestFile -Raw | ConvertFrom-Json
        if ($payload.status -ne "READY_FOR_NATIVE_DISPATCH") { throw "Payload status mismatch" }
        if ($payload.worktree_path -ne $tempDir -or $payload.branch_name -ne "issue/219-native") { throw "Context mismatch" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Native running status advances READY through DISPATCHED" -Test {
    $progression = Get-NativeDispatchStateProgression -IssueState "READY_FOR_NATIVE_DISPATCH" -RequestStatus "SUBAGENT_RUNNING"
    if (($progression -join ",") -ne "DISPATCHED,SUBAGENT_RUNNING") {
        throw "Skipped native dispatch progression was not preserved"
    }
    if (-not (Test-ValidIssueTransition -FromState "READY_FOR_NATIVE_DISPATCH" -ToState $progression[0])) {
        throw "READY_FOR_NATIVE_DISPATCH -> DISPATCHED should remain valid"
    }
    if (-not (Test-ValidIssueTransition -FromState $progression[0] -ToState $progression[1])) {
        throw "DISPATCHED -> SUBAGENT_RUNNING should remain valid"
    }
}

Invoke-BatchTest -Name "Native running status is not eligible for dispatch deadline failure" -Test {
    $progression = Get-NativeDispatchStateProgression -IssueState "READY_FOR_NATIVE_DISPATCH" -RequestStatus "SUBAGENT_RUNNING"
    $stateAfterPoll = $progression[-1]
    $deadlineExpired = $true
    if ($deadlineExpired -and $stateAfterPoll -in @("READY_FOR_NATIVE_DISPATCH", "DISPATCHED")) {
        throw "A running native worker must not fail on dispatch deadline"
    }
}

Invoke-BatchTest -Name "Unchanged native request remains eligible for dispatch deadline failure" -Test {
    $progression = Get-NativeDispatchStateProgression -IssueState "READY_FOR_NATIVE_DISPATCH" -RequestStatus "READY_FOR_NATIVE_DISPATCH"
    $stateAfterPoll = if ($progression.Count -gt 0) { $progression[-1] } else { "READY_FOR_NATIVE_DISPATCH" }
    if ($stateAfterPoll -notin @("READY_FOR_NATIVE_DISPATCH", "DISPATCHED")) {
        throw "An undispatched native request must remain deadline eligible"
    }
}

Invoke-BatchTest -Name "Native dispatch progression remains valid in normal polling order" -Test {
    $first = Get-NativeDispatchStateProgression -IssueState "READY_FOR_NATIVE_DISPATCH" -RequestStatus "DISPATCHED"
    $second = Get-NativeDispatchStateProgression -IssueState "DISPATCHED" -RequestStatus "SUBAGENT_RUNNING"
    if (($first -join ",") -ne "DISPATCHED" -or ($second -join ",") -ne "SUBAGENT_RUNNING") {
        throw "Normal native dispatch progression changed"
    }
}

Invoke-BatchTest -Name "READY_FOR_NATIVE_DISPATCH can transition to FAILED" -Test {
    if (-not (Test-ValidIssueTransition -FromState "READY_FOR_NATIVE_DISPATCH" -ToState "FAILED")) {
        throw "READY_FOR_NATIVE_DISPATCH -> FAILED should be valid (launch failure before host accept)"
    }
}

Invoke-BatchTest -Name "DISPATCHED can transition to FAILED" -Test {
    if (-not (Test-ValidIssueTransition -FromState "DISPATCHED" -ToState "FAILED")) {
        throw "DISPATCHED -> FAILED should be valid (launch failure after host accept, before worker start)"
    }
}

Invoke-BatchTest -Name "FAILED remains terminal" -Test {
    if (-not (Test-IssueStateTerminal -State "FAILED")) { throw "FAILED should be terminal" }
    if ((Get-ValidIssueTransitions -State "FAILED").Count -ne 0) {
        throw "FAILED must have no outgoing transitions"
    }
}

Invoke-BatchTest -Name "Native dispatch failure preserves launch_failure as non-retryable" -Test {
    $state = New-SubAgentState -IssueId "issue-launch" -Config (New-SubAgentConfig -MaxRetries 3)
    $retry = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory "launch_failure"
    if ($retry.Retryable) { throw "launch_failure must not be retryable" }
}

Invoke-BatchTest -Name "Pre-start native result failure records launch failure semantics" -Test {
    $orchestrator = Join-Path $scriptPath ".." "Scripts" "Invoke-BatchOrchestrator.ps1"
    $source = Get-Content $orchestrator -Raw
    $requiredBranch = @(
        '$issue.SelectedMechanism -eq "native-subagent"',
        '$issue.State -in @("READY_FOR_NATIVE_DISPATCH", "DISPATCHED")',
        'Set-IssueStateTransition -IssueState $issue -ToState "FAILED"',
        '$issue.LaunchStatus = "FAILED"',
        '$issue.ExecutionStatus = "NOT_STARTED"',
        '$issue.FailureClassification = "launch_failure"',
        '$issue.State = "SUBAGENT_FAILED"'
    )
    foreach ($fragment in $requiredBranch) {
        if (-not $source.Contains($fragment)) { throw "Missing pre-start failure contract: $fragment" }
    }
}

Invoke-BatchTest -Name "Normal native dispatch path remains valid" -Test {
    if (-not (Test-ValidIssueTransition -FromState "READY_FOR_NATIVE_DISPATCH" -ToState "DISPATCHED")) {
        throw "READY_FOR_NATIVE_DISPATCH -> DISPATCHED should be valid"
    }
    if (-not (Test-ValidIssueTransition -FromState "DISPATCHED" -ToState "SUBAGENT_RUNNING")) {
        throw "DISPATCHED -> SUBAGENT_RUNNING should be valid"
    }
    $progression = Get-NativeDispatchStateProgression -IssueState "READY_FOR_NATIVE_DISPATCH" -RequestStatus "SUBAGENT_RUNNING"
    if (($progression -join ",") -ne "DISPATCHED,SUBAGENT_RUNNING") {
        throw "Normal native dispatch progression regressed"
    }
}

# ============================================================
# ORPHANED State Tests
# ============================================================
Write-Host "`n=== ORPHANED State Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Issue state machine has ORPHANED state" -Test {
    $states = Get-AllIssueStates
    if ("ORPHANED" -notin $states) { throw "Missing ORPHANED state" }
}

Invoke-BatchTest -Name "ORPHANED is not terminal" -Test {
    if (Test-IssueStateTerminal -State "ORPHANED") { throw "ORPHANED should not be terminal" }
}

Invoke-BatchTest -Name "ORPHANED can transition to SUBAGENT_STARTING" -Test {
    $result = Test-ValidIssueTransition -FromState "ORPHANED" -ToState "SUBAGENT_STARTING"
    if (-not $result) { throw "ORPHANED -> SUBAGENT_STARTING should be valid" }
}

Invoke-BatchTest -Name "ORPHANED can transition to SUBAGENT_FAILED" -Test {
    $result = Test-ValidIssueTransition -FromState "ORPHANED" -ToState "SUBAGENT_FAILED"
    if (-not $result) { throw "ORPHANED -> SUBAGENT_FAILED should be valid" }
}

Invoke-BatchTest -Name "ORPHANED cannot transition to COMPLETED" -Test {
    $result = Test-ValidIssueTransition -FromState "ORPHANED" -ToState "COMPLETED"
    if ($result) { throw "ORPHANED -> COMPLETED should be invalid" }
}

Invoke-BatchTest -Name "SUBAGENT_RUNNING can transition to ORPHANED" -Test {
    $result = Test-ValidIssueTransition -FromState "SUBAGENT_RUNNING" -ToState "ORPHANED"
    if (-not $result) { throw "SUBAGENT_RUNNING -> ORPHANED should be valid" }
}

Invoke-BatchTest -Name "SUBAGENT_STARTING can transition to ORPHANED" -Test {
    $result = Test-ValidIssueTransition -FromState "SUBAGENT_STARTING" -ToState "ORPHANED"
    if (-not $result) { throw "SUBAGENT_STARTING -> ORPHANED should be valid" }
}

Invoke-BatchTest -Name "SUBAGENT_STARTING can transition to PR_READY" -Test {
    $result = Test-ValidIssueTransition -FromState "SUBAGENT_STARTING" -ToState "PR_READY"
    if (-not $result) { throw "SUBAGENT_STARTING -> PR_READY should be valid (idempotency)" }
}

Invoke-BatchTest -Name "SUBAGENT_RETRYING can transition to ORPHANED" -Test {
    $result = Test-ValidIssueTransition -FromState "SUBAGENT_RETRYING" -ToState "ORPHANED"
    if (-not $result) { throw "SUBAGENT_RETRYING -> ORPHANED should be valid" }
}

Invoke-BatchTest -Name "ORPHANED can transition to WAITING_FOR_SUBAGENT" -Test {
    $result = Test-ValidIssueTransition -FromState "ORPHANED" -ToState "WAITING_FOR_SUBAGENT"
    if (-not $result) { throw "ORPHANED -> WAITING_FOR_SUBAGENT should be valid" }
}

Invoke-BatchTest -Name "Test-IssueStateRecoverable identifies ORPHANED" -Test {
    if (-not (Test-IssueStateRecoverable -State "ORPHANED")) { throw "ORPHANED should be recoverable" }
}

Invoke-BatchTest -Name "Test-IssueStateRecoverable does not identify SUBAGENT_FAILED" -Test {
    if (Test-IssueStateRecoverable -State "SUBAGENT_FAILED") { throw "SUBAGENT_FAILED should not be recoverable (terminal state)" }
}

Invoke-BatchTest -Name "Test-IssueStateRecoverable identifies BLOCKED" -Test {
    if (-not (Test-IssueStateRecoverable -State "BLOCKED")) { throw "BLOCKED should be recoverable" }
}

Invoke-BatchTest -Name "Test-IssueStateRecoverable rejects COMPLETED" -Test {
    if (Test-IssueStateRecoverable -State "COMPLETED") { throw "COMPLETED should not be recoverable" }
}

# ============================================================
# BatchCheckpoint Tests
# ============================================================
Write-Host "`n=== BatchCheckpoint Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-BatchCheckpoint creates checkpoint with schema version" -Test {
    $cp = New-BatchCheckpoint -BatchId "test-batch" -IssueCount 3
    if ($cp.schemaVersion -ne 1) { throw "SchemaVersion should be 1" }
    if ($cp.batchId -ne "test-batch") { throw "BatchId mismatch" }
    if ($cp.issueCount -ne 3) { throw "IssueCount mismatch" }
    if ($cp.workers.Count -ne 0) { throw "Workers should be empty" }
}

Invoke-BatchTest -Name "New-WorkerCheckpoint creates checkpoint with provider-neutral fields" -Test {
    $cp = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 42 -Description "test" -Provider "claude-code"
    if ($cp.schemaVersion -ne 1) { throw "SchemaVersion should be 1" }
    if ($cp.issueId -ne "issue-1") { throw "IssueId mismatch" }
    if ($cp.issueNumber -ne 42) { throw "IssueNumber mismatch" }
    if ($cp.provider -ne "claude-code") { throw "Provider mismatch" }
    if ($cp.lifecycleState -ne "PENDING") { throw "Initial lifecycleState should be PENDING" }
    if ($cp.completedPhases.Count -ne 0) { throw "completedPhases should be empty" }
    if ($cp.ContainsKey("providerMetadata") -eq $false) { throw "Should have providerMetadata field" }
}

Invoke-BatchTest -Name "Save and load batch checkpoint" -Test {
    $tempDir = Join-Path $env:TEMP "test-cp-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $cp = New-BatchCheckpoint -BatchId "test-cp" -IssueCount 2
        $cp.batchState = "RUNNING"
        $cp.workers["issue-1"] = @{ state = "RUNNING"; updatedAt = "2025-01-01T00:00:00Z" }
        Save-BatchCheckpoint -Checkpoint $cp -StateDir $tempDir
        $loaded = Get-BatchCheckpoint -BatchId "test-cp" -StateDir $tempDir
        if ($null -eq $loaded) { throw "Should load checkpoint" }
        if ($loaded.batchState -ne "RUNNING") { throw "batchState mismatch" }
        if ($loaded.workers.Count -ne 1) { throw "Should have 1 worker" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Save and load worker checkpoint" -Test {
    $tempDir = Join-Path $env:TEMP "test-wc-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $cp = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 42 -Description "test"
        $cp.batchId = "test-batch"
        $cp.lifecycleState = "RUNNING"
        $cp.branch = "issue/42-test"
        $cp.currentCommit = "abc123"
        $cp.completedPhases = @("agent_completed")
        Save-WorkerCheckpoint -Checkpoint $cp -StateDir $tempDir
        $loaded = Get-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-1" -StateDir $tempDir
        if ($null -eq $loaded) { throw "Should load worker checkpoint" }
        if ($loaded.lifecycleState -ne "RUNNING") { throw "lifecycleState mismatch" }
        if ($loaded.branch -ne "issue/42-test") { throw "branch mismatch" }
        if ($loaded.currentCommit -ne "abc123") { throw "currentCommit mismatch" }
        if ($loaded.completedPhases.Count -ne 1) { throw "completedPhases should have 1 entry" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Get-AllWorkerCheckpoints returns all workers" -Test {
    $tempDir = Join-Path $env:TEMP "test-wc-all-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $cp1 = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 1
        $cp1.batchId = "test-batch"
        Save-WorkerCheckpoint -Checkpoint $cp1 -StateDir $tempDir
        $cp2 = New-WorkerCheckpoint -IssueId "issue-2" -IssueNumber 2
        $cp2.batchId = "test-batch"
        Save-WorkerCheckpoint -Checkpoint $cp2 -StateDir $tempDir
        $all = Get-AllWorkerCheckpoints -BatchId "test-batch" -StateDir $tempDir
        if ($all.Count -ne 2) { throw "Should have 2 checkpoints, got $($all.Count)" }
        if (-not $all.ContainsKey("issue-1")) { throw "Should contain issue-1" }
        if (-not $all.ContainsKey("issue-2")) { throw "Should contain issue-2" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Test-BatchCheckpointExists returns correct values" -Test {
    $tempDir = Join-Path $env:TEMP "test-cp-exists-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        if (Test-BatchCheckpointExists -BatchId "test-exists" -StateDir $tempDir) { throw "Should not exist yet" }
        $cp = New-BatchCheckpoint -BatchId "test-exists"
        Save-BatchCheckpoint -Checkpoint $cp -StateDir $tempDir
        if (-not (Test-BatchCheckpointExists -BatchId "test-exists" -StateDir $tempDir)) { throw "Should exist now" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Get-BatchCheckpoint returns null for missing" -Test {
    $result = Get-BatchCheckpoint -BatchId "nonexistent" -StateDir "/tmp"
    if ($null -ne $result) { throw "Should return null for missing" }
}

Invoke-BatchTest -Name "Remove-WorkerCheckpoint deletes file" -Test {
    $tempDir = Join-Path $env:TEMP "test-wc-remove-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $cp = New-WorkerCheckpoint -IssueId "issue-rm" -IssueNumber 1
        $cp.batchId = "test-batch"
        Save-WorkerCheckpoint -Checkpoint $cp -StateDir $tempDir
        Remove-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-rm" -StateDir $tempDir
        $loaded = Get-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-rm" -StateDir $tempDir
        if ($null -ne $loaded) { throw "Should be null after removal" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Remove-BatchCheckpoint removes entire directory" -Test {
    $tempDir = Join-Path $env:TEMP "test-cp-remove-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $cp = New-BatchCheckpoint -BatchId "test-rm-batch"
        Save-BatchCheckpoint -Checkpoint $cp -StateDir $tempDir
        $wcp = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 1
        $wcp.batchId = "test-rm-batch"
        Save-WorkerCheckpoint -Checkpoint $wcp -StateDir $tempDir
        Remove-BatchCheckpoint -BatchId "test-rm-batch" -StateDir $tempDir
        $loaded = Get-BatchCheckpoint -BatchId "test-rm-batch" -StateDir $tempDir
        if ($null -ne $loaded) { throw "Should be null after removal" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Save-AtomicJson writes atomically" -Test {
    $tempDir = Join-Path $env:TEMP "test-atomic-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $filePath = Join-Path $tempDir "test.json"
        $data = @{ Hello = "world"; Number = 42 }
        Save-AtomicJson -FilePath $filePath -Data $data
        if (-not (Test-Path $filePath)) { throw "File should exist" }
        $loaded = Get-Content $filePath -Raw | ConvertFrom-Json
        if ($loaded.Hello -ne "world") { throw "Data mismatch" }
        if ($loaded.Number -ne 42) { throw "Number mismatch" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Get-WorkerCheckpointSummary returns string" -Test {
    $cp = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 42 -Description "test feature"
    $cp.lifecycleState = "RUNNING"
    $cp.branch = "issue/42-test"
    $cp.currentCommit = "abc123"
    $cp.providerMetadata = @{ sessionId = "test-session" }
    $summary = Get-WorkerCheckpointSummary -Checkpoint $cp
    if (-not $summary) { throw "Summary should not be empty" }
    if (-not $summary.Contains("issue-1")) { throw "Summary should contain issue-1" }
    if (-not $summary.Contains("RUNNING")) { throw "Summary should contain RUNNING" }
    if (-not $summary.Contains("sessionId")) { throw "Summary should contain providerMetadata keys" }
}

Invoke-BatchTest -Name "New-RecoveryContext creates recovery context" -Test {
    $batchCp = New-BatchCheckpoint -BatchId "test-batch" -IssueCount 1
    $batchCp.batchState = "RUNNING"
    $workerCp = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 42 -Description "test"
    $workerCp.branch = "issue/42-test"
    $workerCp.lifecycleState = "ORPHANED"
    $workerCp.failureReason = "Process crashed"
    $ctx = New-RecoveryContext -BatchCheckpoint $batchCp -WorkerCheckpoint $workerCp
    if ($ctx.batchId -ne "test-batch") { throw "batchId mismatch" }
    if ($ctx.batchState -ne "RUNNING") { throw "batchState mismatch" }
    if ($ctx.issueId -ne "issue-1") { throw "issueId mismatch" }
    if ($ctx.branch -ne "issue/42-test") { throw "branch mismatch" }
    if ($ctx.failureReason -ne "Process crashed") { throw "failureReason mismatch" }
    if (-not $ctx.workerSummary) { throw "workerSummary should not be empty" }
}

# ============================================================
# PR_READY Terminal State Tests
# ============================================================
Write-Host "`n=== PR_READY Terminal State Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "PR_READY is not terminal" -Test {
    if (Test-IssueStateTerminal -State "PR_READY") { throw "PR_READY should not be terminal (has transition to WAITING_FOR_APPROVAL)" }
}

Invoke-BatchTest -Name "PR_READY is considered active" -Test {
    if (-not (Test-IssueStateActive -State "PR_READY")) { throw "PR_READY should be active per state machine" }
}

Invoke-BatchTest -Name "PR_READY can transition to WAITING_FOR_APPROVAL" -Test {
    $result = Test-ValidIssueTransition -FromState "PR_READY" -ToState "WAITING_FOR_APPROVAL"
    if (-not $result) { throw "PR_READY -> WAITING_FOR_APPROVAL should be valid" }
}

# ============================================================
# Checkpoint Filename Collision Tests
# ============================================================
Write-Host "`n=== Checkpoint Filename Collision Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Different IssueIds produce different checkpoint filenames" -Test {
    $tempDir = Join-Path $env:TEMP "test-collision-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $path1 = Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "issue/1" -StateDir $tempDir
        $path2 = Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "issue?1" -StateDir $tempDir
        $path3 = Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "issue_1" -StateDir $tempDir
        if ($path1 -eq $path2) { throw "issue/1 and issue?1 should produce different paths" }
        if ($path1 -eq $path3) { throw "issue/1 and issue_1 should produce different paths" }
        if ($path2 -eq $path3) { throw "issue?1 and issue_1 should produce different paths" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Safe IssueIds still produce expected filenames" -Test {
    $tempDir = Join-Path $env:TEMP "test-safe-id-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $path = Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "issue-1" -StateDir $tempDir
        if (-not $path.EndsWith("worker-issue-1.json")) { throw "Expected worker-issue-1.json, got $path" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Checkpoint filenames are injective between unsafe and safe IssueIds" -Test {
    $tempDir = Join-Path $env:TEMP "test-injective-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $unsafe = Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "issue/1" -StateDir $tempDir
        $safe = Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "issue_2F1" -StateDir $tempDir
        if ($unsafe -eq $safe) { throw "Unsafe issue/1 and safe issue_2F1 must not collide" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Get-WorkerCheckpointPath rejects empty IssueId" -Test {
    try {
        Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "" -StateDir "/tmp"
        throw "Should have thrown for empty IssueId"
    } catch {
        if (-not $_.Exception.Message -match "empty") { throw "Wrong error: $($_.Exception.Message)" }
    }
}

Invoke-BatchTest -Name "Get-WorkerCheckpointPath rejects whitespace-only IssueId" -Test {
    try {
        Get-WorkerCheckpointPath -BatchId "test-batch" -IssueId "   " -StateDir "/tmp"
        throw "Should have thrown for whitespace-only IssueId"
    } catch {
        if (-not $_.Exception.Message -match "empty") { throw "Wrong error: $($_.Exception.Message)" }
    }
}

# ============================================================
# Transition Log BatchId Validation Tests
# ============================================================
Write-Host "`n=== Transition Log BatchId Validation Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Get-TransitionLogPath rejects empty BatchId" -Test {
    try {
        Get-TransitionLogPath -BatchId "" -StateDir "/tmp"
        throw "Should have thrown for empty BatchId"
    } catch {
        if (-not $_.Exception.Message -match "empty") { throw "Wrong error: $($_.Exception.Message)" }
    }
}

Invoke-BatchTest -Name "Get-TransitionLogPath rejects path traversal" -Test {
    try {
        Get-TransitionLogPath -BatchId "../etc/passwd" -StateDir "/tmp"
        throw "Should have thrown for path traversal"
    } catch {
        if (-not $_.Exception.Message -match "invalid path") { throw "Wrong error: $($_.Exception.Message)" }
    }
}

Invoke-BatchTest -Name "Get-TransitionLogPath rejects backslash" -Test {
    try {
        Get-TransitionLogPath -BatchId 'batch\test' -StateDir "/tmp"
        throw "Should have thrown for backslash"
    } catch {
        if (-not $_.Exception.Message -match "invalid path") { throw "Wrong error: $($_.Exception.Message)" }
    }
}

# ============================================================
# Orphan Detection Result Validation Tests
# ============================================================
Write-Host "`n=== Orphan Detection Result Validation Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Test-OrphanedProcess detects orphan with corrupt result file" -Test {
    $tempDir = Join-Path $env:TEMP "test-corrupt-result-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $resultFile = Join-Path $tempDir "result.json"
        "not valid json" | Set-Content $resultFile
        $result = Test-OrphanedProcess -ProcessId 99999999 -ResultFile $resultFile
        if (-not $result.IsOrphaned) { throw "Should detect orphan with corrupt result" }
        if (-not $result.Reason.Contains("unreadable") -and -not $result.Reason.Contains("corrupt")) { throw "Reason should mention corrupt/unreadable" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Test-OrphanedProcess does not orphan when result is valid" -Test {
    $tempDir = Join-Path $env:TEMP "test-valid-result-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $resultFile = Join-Path $tempDir "result.json"
        @{ Success = $false; Error = "test error" } | ConvertTo-Json | Set-Content $resultFile
        $result = Test-OrphanedProcess -ProcessId 99999999 -ResultFile $resultFile
        if ($result.IsOrphaned) { throw "Should not orphan when result is parseable" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# Transition Log Error Handling Tests
# ============================================================
Write-Host "`n=== Transition Log Error Handling Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Write-TransitionLog creates parent directory if needed" -Test {
    $tempDir = Join-Path $env:TEMP "test-log-parent-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $nestedStateDir = Join-Path $tempDir "nested" "subdir"
        Write-TransitionLog -BatchId "test-log" -EntityType "worker" -EntityId "issue-1" -FromState "WAITING" -ToState "RUNNING" -StateDir $nestedStateDir
        $entries = Get-TransitionLog -BatchId "test-log" -StateDir $nestedStateDir
        if ($entries.Count -ne 1) { throw "Expected 1 entry" }
        $logPath = Join-Path $nestedStateDir ".batch-log-test-log.jsonl"
        if (-not (Test-Path $logPath)) { throw "Log file should exist at nested path" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# Transition Log Tests
# ============================================================
Write-Host "`n=== Transition Log Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Write-TransitionLog creates log entries" -Test {
    $tempDir = Join-Path $env:TEMP "test-log-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        Write-TransitionLog -BatchId "test-log" -EntityType "worker" -EntityId "issue-1" -FromState "SUBAGENT_RUNNING" -ToState "ORPHANED" -Reason "Process dead" -StateDir $tempDir
        Write-TransitionLog -BatchId "test-log" -EntityType "worker" -EntityId "issue-1" -FromState "ORPHANED" -ToState "SUBAGENT_STARTING" -Reason "Recovery" -StateDir $tempDir
        $entries = Get-TransitionLog -BatchId "test-log" -StateDir $tempDir
        if ($entries.Count -ne 2) { throw "Expected 2 entries, got $($entries.Count)" }
        if ($entries[0].fromState -ne "SUBAGENT_RUNNING") { throw "First entry fromState mismatch" }
        if ($entries[0].toState -ne "ORPHANED") { throw "First entry toState mismatch" }
        if ($entries[1].toState -ne "SUBAGENT_STARTING") { throw "Second entry toState mismatch" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Get-TransitionLog returns empty for missing" -Test {
    $entries = Get-TransitionLog -BatchId "nonexistent" -StateDir "/tmp"
    if ($entries.Count -ne 0) { throw "Should return empty for missing" }
}

Invoke-BatchTest -Name "Get-TransitionLog returns indexable array for single entry" -Test {
    $tempDir = Join-Path $env:TEMP "test-log-single-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        Write-TransitionLog -BatchId "test-log-single" -EntityType "worker" -EntityId "issue-1" -FromState "SUBAGENT_RUNNING" -ToState "ORPHANED" -Reason "Process dead" -StateDir $tempDir
        $entries = Get-TransitionLog -BatchId "test-log-single" -StateDir $tempDir
        if ($entries.Count -ne 1) { throw "Expected 1 entry, got $($entries.Count)" }
        if ($entries[0].toState -ne "ORPHANED") { throw "Single entry should be indexable as array" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# Worker Checkpoint from IssueState Tests
# ============================================================
Write-Host "`n=== Worker Checkpoint from IssueState Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-WorkerCheckpointFromIssueState maps SUBAGENT_RUNNING to RUNNING" -Test {
    $issueState = New-IssueState -IssueId "issue-1" -IssueNumber 42 -Description "test"
    $issueState.State = "SUBAGENT_RUNNING"
    $issueState.BranchName = "issue/42-test"
    $issueState.CommitSha = "abc123"
    $issueState.WorktreePath = "/tmp/test"
    $cp = New-WorkerCheckpointFromIssueState -IssueState $issueState -Provider "claude-code"
    if ($cp.lifecycleState -ne "RUNNING") { throw "Should map to RUNNING, got $($cp.lifecycleState)" }
    if ($cp.branch -ne "issue/42-test") { throw "branch mismatch" }
    if ($cp.currentCommit -ne "abc123") { throw "currentCommit mismatch" }
    if ($cp.provider -ne "claude-code") { throw "provider mismatch" }
}

Invoke-BatchTest -Name "New-WorkerCheckpointFromIssueState maps ORPHANED correctly" -Test {
    $issueState = New-IssueState -IssueId "issue-1" -IssueNumber 42 -Description "test"
    $issueState.State = "ORPHANED"
    $cp = New-WorkerCheckpointFromIssueState -IssueState $issueState
    if ($cp.lifecycleState -ne "ORPHANED") { throw "Should map to ORPHANED, got $($cp.lifecycleState)" }
}

Invoke-BatchTest -Name "New-WorkerCheckpointFromIssueState maps COMPLETED to SUCCESS" -Test {
    $issueState = New-IssueState -IssueId "issue-1" -IssueNumber 42 -Description "test"
    $issueState.State = "COMPLETED"
    $issueState.CommitSha = "abc123"
    $issueState.PrNumber = 99
    $cp = New-WorkerCheckpointFromIssueState -IssueState $issueState
    if ($cp.lifecycleState -ne "SUCCESS") { throw "Should map to SUCCESS, got $($cp.lifecycleState)" }
    if ($cp.completedPhases -notcontains "commit") { throw "Should have commit in completedPhases" }
    if ($cp.completedPhases -notcontains "push") { throw "Should have push in completedPhases" }
}

Invoke-BatchTest -Name "New-WorkerCheckpointFromIssueState maps PR_READY to RUNNING" -Test {
    $issueState = New-IssueState -IssueId "issue-1" -IssueNumber 42 -Description "test"
    $issueState.State = "PR_READY"
    $issueState.CommitSha = "abc123"
    $issueState.PrNumber = 99
    $cp = New-WorkerCheckpointFromIssueState -IssueState $issueState
    if ($cp.lifecycleState -ne "RUNNING") { throw "Should map to RUNNING, got $($cp.lifecycleState)" }
    if ($cp.completedPhases -notcontains "pr_created") { throw "Should have pr_created in completedPhases" }
}

# ============================================================
# Orphan Detection Tests
# ============================================================
Write-Host "`n=== Orphan Detection Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Test-OrphanedProcess detects orphan with dead process and no result" -Test {
    $tempDir = Join-Path $env:TEMP "test-orphan-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $resultFile = Join-Path $tempDir "result.json"
        $result = Test-OrphanedProcess -ProcessId 99999999 -ResultFile $resultFile
        if (-not $result.IsOrphaned) { throw "Should detect orphan" }
        if (-not $result.Reason.Contains("exited")) { throw "Reason should mention exit" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Test-OrphanedProcess does not detect orphan when result exists" -Test {
    $tempDir = Join-Path $env:TEMP "test-orphan-res-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $resultFile = Join-Path $tempDir "result.json"
        @{ Success = $true } | ConvertTo-Json | Set-Content $resultFile
        $result = Test-OrphanedProcess -ProcessId 99999999 -ResultFile $resultFile
        if ($result.IsOrphaned) { throw "Should not detect orphan when result exists" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# Atomic Write Tests
# ============================================================
Write-Host "`n=== Atomic Write Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Atomic write does not leave temp files on success" -Test {
    $tempDir = Join-Path $env:TEMP "test-atomic-clean-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $filePath = Join-Path $tempDir "data.json"
        $data = @{ Key = "Value" }
        Save-AtomicJson -FilePath $filePath -Data $data
        $tmpFiles = Get-ChildItem -Path $tempDir -Filter "*.tmp.*" -ErrorAction SilentlyContinue
        if ($tmpFiles.Count -gt 0) { throw "Temp files should be cleaned up" }
        if (-not (Test-Path $filePath)) { throw "Target file should exist" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

Invoke-BatchTest -Name "Atomic write overwrites existing file" -Test {
    $tempDir = Join-Path $env:TEMP "test-atomic-overwrite-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $filePath = Join-Path $tempDir "data.json"
        @{ Old = $true } | ConvertTo-Json | Set-Content $filePath
        $data = @{ New = $true }
        Save-AtomicJson -FilePath $filePath -Data $data
        $loaded = Get-Content $filePath -Raw | ConvertFrom-Json -AsHashtable
        if ($loaded.New -ne $true) { throw "Should have new data" }
        if ($loaded.ContainsKey("Old")) { throw "Should not have old data" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# Schema Version Tests
# ============================================================
Write-Host "`n=== Schema Version Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Batch state includes SchemaVersion" -Test {
    $state = New-BatchState -BatchId "test-sv" -IssueCount 1
    if ($state.SchemaVersion -ne 1) { throw "SchemaVersion should be 1, got $($state.SchemaVersion)" }
}

Invoke-BatchTest -Name "Issue states wrapper includes SchemaVersion" -Test {
    $tempFile = Join-Path $env:TEMP "test-sv-issues-$(Get-Random).json"
    try {
        $issues = @{ "A" = New-IssueState -IssueId "A" -IssueNumber 1 }
        Save-IssueStates -Issues $issues -FilePath $tempFile
        $raw = Get-Content $tempFile -Raw | ConvertFrom-Json
        if ($raw.SchemaVersion -ne 1) { throw "SchemaVersion should be 1 in wrapper" }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
    }
}

# ============================================================
# BatchCheckpoint Config Tests
# ============================================================
Write-Host "`n=== BatchCheckpoint Config Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Config has checkpoint section" -Test {
    $configPath = Join-Path $scriptPath ".." ".." ".." "config" "batch-config.json"
    $config = Get-Content $configPath | ConvertFrom-Json
    if (-not $config.checkpoint) { throw "Config should have checkpoint section" }
    if (-not $config.checkpoint.enabled) { throw "Checkpoint should be enabled" }
    if (-not $config.checkpoint.provider_neutral) { throw "Checkpoint should be provider-neutral" }
    if (-not $config.checkpoint.recovery.orphan_detection) { throw "Orphan detection should be enabled" }
    if (-not $config.checkpoint.recovery.idempotency_protection) { throw "Idempotency protection should be enabled" }
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
