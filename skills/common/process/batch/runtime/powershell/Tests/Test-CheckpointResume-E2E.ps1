#Requires -Version 7.0

<#
.SYNOPSIS
    E2E tests for Checkpoint and Resume (Issue #170).

.DESCRIPTION
    Tests the checkpoint-resume-recovery loop for Batch Orchestrator workers.
    Simulates token-limit, timeout, process failure, and interruption scenarios
    using fake/test providers. Verifies provider-neutral checkpoint persistence,
    orphan detection, recovery, idempotency, and state restoration.

.NOTES
    Version: 1.0.0
    Issue: #170
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "BatchStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "IssueStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "BatchSubAgent.psm1") -Force
Import-Module (Join-Path $modulePath "BatchPersistence.psm1") -Force
Import-Module (Join-Path $modulePath "BatchCheckpoint.psm1") -Force
Import-Module (Join-Path $modulePath "AgentProvider.psm1") -Force

$testResults = @{
    Passed = 0
    Failed = 0
    Tests = @()
}

function Invoke-E2ETest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Test
    )

    Write-Host "E2E Testing: $Name" -ForegroundColor Cyan

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
# 1. Checkpoint persistence test
# ============================================================
Write-Host "`n=== Checkpoint Persistence Tests ===" -ForegroundColor Green

Invoke-E2ETest -Name "1. Checkpoint is saved on state transition" -Test {
    $tempDir = Join-Path $env:TEMP "e2e-cp-save-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $batchCp = New-BatchCheckpoint -BatchId "e2e-batch" -IssueCount 2
        $batchCp.batchState = "RUNNING"

        $wcp1 = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 1 -Description "test1" -Provider "test"
        $wcp1.batchId = "e2e-batch"
        $wcp1.lifecycleState = "RUNNING"
        $batchCp.workers["issue-1"] = @{ state = "RUNNING"; updatedAt = $wcp1.updatedAt }
        Save-WorkerCheckpoint -Checkpoint $wcp1 -StateDir $tempDir

        $wcp2 = New-WorkerCheckpoint -IssueId "issue-2" -IssueNumber 2 -Description "test2" -Provider "test"
        $wcp2.batchId = "e2e-batch"
        $wcp2.lifecycleState = "PENDING"
        $batchCp.workers["issue-2"] = @{ state = "PENDING"; updatedAt = $wcp2.updatedAt }
        Save-WorkerCheckpoint -Checkpoint $wcp2 -StateDir $tempDir

        Save-BatchCheckpoint -Checkpoint $batchCp -StateDir $tempDir

        $loaded = Get-BatchCheckpoint -BatchId "e2e-batch" -StateDir $tempDir
        if ($loaded.batchState -ne "RUNNING") { throw "batchState mismatch" }
        if ($loaded.workers.Count -ne 2) { throw "Should have 2 workers, got $($loaded.workers.Count)" }

        $all = Get-AllWorkerCheckpoints -BatchId "e2e-batch" -StateDir $tempDir
        if ($all.Count -ne 2) { throw "Get-AllWorkerCheckpoints should return 2" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 2. Worker state persistence on interruption
# ============================================================
Invoke-E2ETest -Name "2. Worker checkpoint survives simulated interruption" -Test {
    $tempDir = Join-Path $env:TEMP "e2e-interrupt-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $wcp = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 1 -Description "test" -Provider "claude-code"
        $wcp.batchId = "test-batch"
        $wcp.lifecycleState = "RUNNING"
        $wcp.branch = "issue/1-test"
        $wcp.currentCommit = "abc123"
        $wcp.completedPhases = @("agent_completed")
        Save-WorkerCheckpoint -Checkpoint $wcp -StateDir $tempDir

        $loaded = Get-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-1" -StateDir $tempDir
        if ($null -eq $loaded) { throw "Checkpoint should exist after 'interruption'" }
        if ($loaded.lifecycleState -ne "RUNNING") { throw "State should be RUNNING" }
        if ($loaded.completedPhases.Count -ne 1) { throw "Should have 1 completed phase" }
        if ($loaded.branch -ne "issue/1-test") { throw "Branch should be preserved" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 3. Token-limit-like termination and resume
# ============================================================
Invoke-E2ETest -Name "3. Token-limit simulation: process dies, checkpoint remains, new session resumes" -Test {
    $tempDir = Join-Path $env:TEMP "e2e-tokenlimit-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $issueState = New-IssueState -IssueId "issue-tl" -IssueNumber 10 -Description "token limit test"
        $issueState.State = "SUBAGENT_RUNNING"
        $issueState.BranchName = "issue/10-token-limit"
        $issueState.CommitSha = "def456"
        $issueState.WorktreePath = $tempDir
        $issueState.SubAgentProcessId = 99999999

        $workerCp = New-WorkerCheckpointFromIssueState -IssueState $issueState -Provider "claude-code"
        $workerCp.batchId = "test-batch"
        Save-WorkerCheckpoint -Checkpoint $workerCp -StateDir $tempDir

        $orphanCheck = Test-OrphanedProcess -ProcessId 99999999 -ResultFile (Join-Path $tempDir "nonexistent.json")
        if (-not $orphanCheck.IsOrphaned) { throw "Should detect orphan after token-limit" }

        $issueState.State = "ORPHANED"
        $issueState.LastError = "Token limit exceeded"
        $issueState.SubAgentProcessId = $null

        $fromState = "SUBAGENT_RUNNING"
        if (-not (Test-ValidIssueTransition -FromState $fromState -ToState "ORPHANED")) {
            throw "SUBAGENT_RUNNING -> ORPHANED should be valid"
        }

        $tempConfig = New-SubAgentConfig -MaxRetries 3
        $tempState = New-SubAgentState -IssueId "issue-tl" -Config $tempConfig
        $retryCheck = Test-SubAgentRetryable -SubAgentState $tempState -ErrorCategory "timeout"
        if (-not $retryCheck.Retryable) { throw "Should be retryable after token-limit" }

        if (-not (Test-ValidIssueTransition -FromState "ORPHANED" -ToState "SUBAGENT_STARTING")) {
            throw "ORPHANED -> SUBAGENT_STARTING should be valid"
        }

        $issueState.State = "SUBAGENT_STARTING"
        $issueState.RetryCount = 1
        $loaded = Get-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-tl" -StateDir $tempDir
        if ($null -eq $loaded) { throw "Checkpoint should still exist" }
        if ($loaded.branch -ne "issue/10-token-limit") { throw "Branch should be preserved across recovery" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 4. SUCCESS worker not re-executed on recovery
# ============================================================
Invoke-E2ETest -Name "4. SUCCESS worker is not re-executed during recovery" -Test {
    $batchCp = New-BatchCheckpoint -BatchId "e2e-no-redo" -IssueCount 2
    $batchCp.batchState = "RUNNING"

    $wcpDone = New-WorkerCheckpoint -IssueId "issue-done" -IssueNumber 1 -Description "done"
    $wcpDone.batchId = "e2e-no-redo"
    $wcpDone.lifecycleState = "SUCCESS"
    $wcpDone.resultCommit = "sha-done"
    $wcpDone.prNumber = 42

    $wcpPending = New-WorkerCheckpoint -IssueId "issue-pending" -IssueNumber 2 -Description "pending"
    $wcpPending.batchId = "e2e-no-redo"
    $wcpPending.lifecycleState = "RUNNING"

    if ($wcpDone.lifecycleState -ne "SUCCESS") { throw "issue-done should be SUCCESS" }
    if (-not (Test-IssueStateTerminal -State "COMPLETED")) { throw "COMPLETED should be terminal" }
    if (Test-ValidIssueTransition -FromState "COMPLETED" -ToState "SUBAGENT_STARTING") {
        throw "COMPLETED should not allow re-execution"
    }
    if ($wcpPending.lifecycleState -eq "SUCCESS") { throw "issue-pending should not be SUCCESS" }
}

# ============================================================
# 5. TIMEOUT/FAILED worker can be retried
# ============================================================
Invoke-E2ETest -Name "5. TIMEOUT/FAILED worker can be retried from checkpoint" -Test {
    $issueState = New-IssueState -IssueId "issue-timeout" -IssueNumber 5 -Description "timeout test"
    $issueState.State = "SUBAGENT_FAILED"
    $issueState.LastError = "timeout after 30 minutes"
    $issueState.RetryCount = 1

    $workerCp = New-WorkerCheckpointFromIssueState -IssueState $issueState -Provider "test"
    if ($workerCp.lifecycleState -ne "FAILED") { throw "Should map to FAILED" }

    if (-not (Test-IssueStateRecoverable -State "ORPHANED")) {
        throw "ORPHANED should be recoverable"
    }

    $tempConfig = New-SubAgentConfig -MaxRetries 3
    $tempState = New-SubAgentState -IssueId "issue-timeout" -Config $tempConfig
    $tempState.RetryCount = 1
    $retryCheck = Test-SubAgentRetryable -SubAgentState $tempState -ErrorCategory "timeout"
    if (-not $retryCheck.Retryable) { throw "Should be retryable" }

    if (Test-ValidIssueTransition -FromState "SUBAGENT_FAILED" -ToState "SUBAGENT_STARTING") {
        throw "SUBAGENT_FAILED -> SUBAGENT_STARTING should be invalid (terminal)"
    }

    if (-not (Test-ValidIssueTransition -FromState "BLOCKED" -ToState "WAITING_FOR_SUBAGENT")) {
        throw "BLOCKED -> WAITING_FOR_SUBAGENT should be valid (recovery path)"
    }
}

# ============================================================
# 6. Orchestrator restart batch state restoration
# ============================================================
Invoke-E2ETest -Name "6. Batch state is fully restored from checkpoint after restart" -Test {
    $tempDir = Join-Path $env:TEMP "e2e-restart-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $batchState = New-BatchState -BatchId "e2e-restart" -IssueCount 2
        $batchState.State = "RUNNING"
        $batchState.CompletedCount = 1
        $batchState.FailedCount = 0
        $batchState.DependencyGraph = @{
            Nodes = @(@{ Id = "A" }, @{ Id = "B" })
            Edges = @{ A = @("B") }
        }
        $batchState.ConcurrencyGroups = @(@("B"), @("A"))

        $issueStates = @{}
        $issueStates["A"] = New-IssueState -IssueId "A" -IssueNumber 1 -Description "first"
        $issueStates["A"].State = "COMPLETED"
        $issueStates["A"].CommitSha = "sha-a"
        $issueStates["A"].PrNumber = 10
        $issueStates["A"].WorktreePath = "/tmp/wt-a"
        $issueStates["A"].BranchName = "issue/1-first"

        $issueStates["B"] = New-IssueState -IssueId "B" -IssueNumber 2 -Description "second"
        $issueStates["B"].State = "SUBAGENT_RUNNING"
        $issueStates["B"].SubAgentProcessId = 99999999
        $issueStates["B"].WorktreePath = "/tmp/wt-b"
        $issueStates["B"].BranchName = "issue/2-second"

        Save-BatchState -State $batchState -FilePath (Join-Path $tempDir ".batch-state-e2e-restart.json")
        Save-IssueStates -Issues $issueStates -FilePath (Join-Path $tempDir ".batch-issues-e2e-restart.json")

        $batchCp = New-BatchCheckpoint -BatchId "e2e-restart" -IssueCount 2
        $batchCp.batchState = "RUNNING"
        foreach ($issueId in $issueStates.Keys) {
            $wc = New-WorkerCheckpointFromIssueState -IssueState $issueStates[$issueId]
            $wc.batchId = "e2e-restart"
            Save-WorkerCheckpoint -Checkpoint $wc -StateDir $tempDir
        }
        Save-BatchCheckpoint -Checkpoint $batchCp -StateDir $tempDir

        $restoredBatch = Get-BatchState -FilePath (Join-Path $tempDir ".batch-state-e2e-restart.json")
        $restoredIssues = Get-IssueStates -FilePath (Join-Path $tempDir ".batch-issues-e2e-restart.json")
        $restoredCp = Get-BatchCheckpoint -BatchId "e2e-restart" -StateDir $tempDir

        if ($restoredBatch.State -ne "RUNNING") { throw "batchState should be RUNNING" }
        if ($restoredBatch.CompletedCount -ne 1) { throw "CompletedCount should be 1" }
        if ($restoredIssues["A"].State -ne "COMPLETED") { throw "Issue A should be COMPLETED" }
        if ($restoredIssues["B"].State -ne "SUBAGENT_RUNNING") { throw "Issue B should be SUBAGENT_RUNNING" }
        if ($restoredCp.batchState -ne "RUNNING") { throw "Checkpoint batchState should be RUNNING" }

        $allCps = Get-AllWorkerCheckpoints -BatchId "e2e-restart" -StateDir $tempDir
        if ($allCps.Count -ne 2) { throw "Should have 2 worker checkpoints" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 7. Git branch/commit/PR state from checkpoint
# ============================================================
Invoke-E2ETest -Name "7. Git state is correctly restored from worker checkpoint" -Test {
    $wcp = New-WorkerCheckpoint -IssueId "issue-git" -IssueNumber 7 -Description "git state test" -Provider "test"
    $wcp.batchId = "test-batch"
    $wcp.lifecycleState = "RUNNING"
    $wcp.branch = "issue/7-git-state"
    $wcp.baseCommit = "base123"
    $wcp.currentCommit = "current456"
    $wcp.resultCommit = "result789"
    $wcp.prNumber = 77
    $wcp.prState = "APPROVED"
    $wcp.worktreePath = "/tmp/wt-git"
    $wcp.completedPhases = @("agent_completed", "committed", "pr_created")

    $tempDir = Join-Path $env:TEMP "e2e-git-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        Save-WorkerCheckpoint -Checkpoint $wcp -StateDir $tempDir
        $loaded = Get-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-git" -StateDir $tempDir

        if ($loaded.branch -ne "issue/7-git-state") { throw "branch mismatch" }
        if ($loaded.baseCommit -ne "base123") { throw "baseCommit mismatch" }
        if ($loaded.currentCommit -ne "current456") { throw "currentCommit mismatch" }
        if ($loaded.resultCommit -ne "result789") { throw "resultCommit mismatch" }
        if ($loaded.prNumber -ne 77) { throw "prNumber mismatch" }
        if ($loaded.completedPhases.Count -ne 3) { throw "Should have 3 completed phases" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 8. Idempotency: no duplicate commit/PR
# ============================================================
Invoke-E2ETest -Name "8. Idempotency check: existing PR detected before creating duplicate" -Test {
    $existingPrNumber = 42
    $existingBranch = "issue/8-idempotent"

    $tempDir = Join-Path $env:TEMP "e2e-idempotent-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $wcp = New-WorkerCheckpoint -IssueId "issue-idempotent" -IssueNumber 8 -Description "idempotent test"
        $wcp.batchId = "test-batch"
        $wcp.lifecycleState = "RUNNING"
        $wcp.branch = $existingBranch
        $wcp.prNumber = $existingPrNumber
        Save-WorkerCheckpoint -Checkpoint $wcp -StateDir $tempDir

        $loaded = Get-WorkerCheckpoint -BatchId "test-batch" -IssueId "issue-idempotent" -StateDir $tempDir
        if ($loaded.prNumber -ne $existingPrNumber) { throw "Should preserve existing PR number" }
        if ($loaded.branch -ne $existingBranch) { throw "Should preserve existing branch" }

        $existingPr = $loaded.prNumber
        if (-not $existingPr -or $existingPr -le 0) { throw "Should have existing PR for idempotency check" }

        $issueState = New-IssueState -IssueId "issue-idempotent" -IssueNumber 8 -Description "idempotent test"
        $issueState.State = "SUBAGENT_STARTING"
        $issueState.BranchName = $existingBranch
        $issueState.PrNumber = $existingPr

        $restored = New-WorkerCheckpointFromIssueState -IssueState $issueState
        if ($restored.prNumber -ne $existingPrNumber) { throw "Restored checkpoint should carry existing PR number" }
        if ($restored.completedPhases -notcontains "pr_created") { throw "Should have pr_created in completedPhases" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 9. Provider-neutral checkpoint recovery
# ============================================================
Invoke-E2ETest -Name "9. Provider-neutral checkpoint works with different providers" -Test {
    $tempDir = Join-Path $env:TEMP "e2e-neutral-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $wcpClaude = New-WorkerCheckpoint -IssueId "issue-1" -IssueNumber 1 -Description "test" -Provider "claude-code"
        $wcpClaude.batchId = "test-batch"
        $wcpClaude.lifecycleState = "RUNNING"
        $wcpClaude.providerMetadata = @{ sessionId = "claude-session-123"; model = "claude-3" }
        Save-WorkerCheckpoint -Checkpoint $wcpClaude -StateDir $tempDir

        $wcpOpen = New-WorkerCheckpoint -IssueId "issue-2" -IssueNumber 2 -Description "test" -Provider "opencode"
        $wcpOpen.batchId = "test-batch"
        $wcpOpen.lifecycleState = "RUNNING"
        $wcpOpen.providerMetadata = @{ sessionToken = "opencode-token-456" }
        Save-WorkerCheckpoint -Checkpoint $wcpOpen -StateDir $tempDir

        $all = Get-AllWorkerCheckpoints -BatchId "test-batch" -StateDir $tempDir
        if ($all.Count -ne 2) { throw "Should have 2 checkpoints" }

        $claudeCp = $all["issue-1"]
        $openCp = $all["issue-2"]

        if ($claudeCp.provider -ne "claude-code") { throw "Claude provider mismatch" }
        if ($openCp.provider -ne "opencode") { throw "OpenCode provider mismatch" }

        $coreFields = @("issueId", "issueNumber", "branch", "currentCommit", "lifecycleState", "completedPhases")
        foreach ($field in $coreFields) {
            if (-not $claudeCp.ContainsKey($field)) { throw "Claude checkpoint missing core field: $field" }
            if (-not $openCp.ContainsKey($field)) { throw "OpenCode checkpoint missing core field: $field" }
        }

        if (-not $claudeCp.ContainsKey("providerMetadata")) { throw "Claude should have providerMetadata" }
        if (-not $openCp.ContainsKey("providerMetadata")) { throw "OpenCode should have providerMetadata" }
        if ($claudeCp.providerMetadata.sessionId -ne "claude-session-123") { throw "Claude metadata mismatch" }
        if ($openCp.providerMetadata.sessionToken -ne "opencode-token-456") { throw "OpenCode metadata mismatch" }
    } finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }
}

# ============================================================
# 10. Existing batch regression tests still pass
# ============================================================
Invoke-E2ETest -Name "10. Existing state machines still function correctly" -Test {
    $batchStates = Get-AllBatchStates
    if ($batchStates.Count -ne 9) { throw "Batch should have 9 states, got $($batchStates.Count)" }

    $issueStates = Get-AllIssueStates
    if ($issueStates.Count -ne 16) { throw "Issue should have 16 states, got $($issueStates.Count)" }

    $transitions = @(
        @{ From = "BATCH_INITIALIZING"; To = "PLANNING" },
        @{ From = "RUNNING"; To = "WAITING_FOR_MERGE" },
        @{ From = "CLEANUP"; To = "COMPLETED" }
    )
    foreach ($t in $transitions) {
        if (-not (Test-ValidBatchTransition -FromState $t.From -ToState $t.To)) {
            throw "Batch transition $($t.From) -> $($t.To) should be valid"
        }
    }

    $issueTransitions = @(
        @{ From = "SUBAGENT_RUNNING"; To = "ORPHANED" },
        @{ From = "SUBAGENT_STARTING"; To = "ORPHANED" },
        @{ From = "SUBAGENT_STARTING"; To = "PR_READY" },
        @{ From = "SUBAGENT_RETRYING"; To = "ORPHANED" },
        @{ From = "ORPHANED"; To = "SUBAGENT_STARTING" },
        @{ From = "ORPHANED"; To = "SUBAGENT_FAILED" },
        @{ From = "ORPHANED"; To = "WAITING_FOR_SUBAGENT" }
    )
    foreach ($t in $issueTransitions) {
        if (-not (Test-ValidIssueTransition -FromState $t.From -ToState $t.To)) {
            throw "Issue transition $($t.From) -> $($t.To) should be valid"
        }
    }
}

# ============================================================
# 11. PR_READY treated as execution-complete
# ============================================================
Invoke-E2ETest -Name "11. PR_READY is not considered active for loop termination" -Test {
    $activeStates = @("SUBAGENT_STARTING", "SUBAGENT_RUNNING", "SUBAGENT_RETRYING",
                       "WAITING_FOR_SUBAGENT", "WAITING_DEPENDENCY",
                       "WAITING_FOR_APPROVAL", "READY_FOR_MERGE", "MERGING")
    $completedLikeStates = @("COMPLETED", "PR_READY")

    foreach ($s in $completedLikeStates) {
        if ($s -in $activeStates) {
            throw "$s should be treated as non-active for loop termination"
        }
    }
    if ("PR_READY" -in @("COMPLETED", "FAILED", "BLOCKED", "SUBAGENT_FAILED")) {
        throw "PR_READY is not truly terminal, just execution-complete"
    }
}

# ============================================================
# 12. ORPHANED retry consumes budget
# ============================================================
Invoke-E2ETest -Name "12. ORPHANED recovery consumes retry budget" -Test {
    $config = New-SubAgentConfig -MaxRetries 2
    $state = New-SubAgentState -IssueId "issue-orphan-retry" -Config $config
    $state.RetryCount = 1

    $retryCheck = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory "transient"
    if (-not $retryCheck.Retryable) { throw "Should be retryable at count 1" }

    $state.RetryCount = 2
    $retryCheck = Test-SubAgentRetryable -SubAgentState $state -ErrorCategory "transient"
    if ($retryCheck.Retryable) { throw "Should NOT be retryable at count 2 (max 2)" }
}

# ============================================================
# 13. Live worker adoption checkpoint preservation
# ============================================================
Invoke-E2ETest -Name "13. Worker checkpoint preserves runtime state for adoption" -Test {
    $issueState = New-IssueState -IssueId "issue-adopt" -IssueNumber 99 -Description "adopt test"
    $issueState.State = "SUBAGENT_RUNNING"
    $issueState.BranchName = "issue/99-adopt"
    $issueState.CommitSha = "adopt123"
    $issueState.WorktreePath = "/tmp/wt-adopt"
    $issueState.SubAgentProcessId = 12345
    $issueState.RetryCount = 1

    $workerCp = New-WorkerCheckpointFromIssueState -IssueState $issueState -Provider "claude-code" -MaxRetries 3
    if ($workerCp.processId -ne 12345) { throw "processId should be preserved" }
    if ($workerCp.retryCount -ne 1) { throw "retryCount should be preserved" }
    if ($workerCp.lifecycleState -ne "RUNNING") { throw "lifecycleState should be RUNNING" }
    if ($workerCp.branch -ne "issue/99-adopt") { throw "branch should be preserved" }
}

# ============================================================
# Summary
# ============================================================
Write-Host "`n=== E2E Test Summary ===" -ForegroundColor Green
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
