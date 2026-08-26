#Requires -Version 7.0

<#
.SYNOPSIS
    Tests for Batch Skill state machine and utilities.

.DESCRIPTION
    Unit tests for the Batch Skill implementation.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

# Import modules using cross-platform path construction
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "BatchStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "BatchApproval.psm1") -Force

# Test results
$testResults = @{
    Passed = 0
    Failed = 0
    Tests = @()
}

function Invoke-BatchTest {
    <#
    .SYNOPSIS
        Invokes a test function.
    #>
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

# State Machine Tests
Write-Host "`n=== State Machine Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Get-BatchState returns valid states" -Test {
    $states = Get-AllStates
    $expectedStates = @(
        "INVESTIGATING",
        "IMPLEMENTING",
        "REPORTING",
        "PR_OPEN",
        "AWAITING_APPROVAL",
        "REBASE",
        "CONFLICT_RESOLUTION",
        "PR_UPDATED",
        "VALIDATING",
        "MERGING",
        "CLEANUP",
        "COMPLETED"
    )

    foreach ($state in $expectedStates) {
        if ($state -notin $states) {
            throw "Missing state: $state"
        }
    }
}

Invoke-BatchTest -Name "Valid transitions are allowed" -Test {
    $validTransitions = @(
        @{ From = "INVESTIGATING"; To = "IMPLEMENTING" },
        @{ From = "IMPLEMENTING"; To = "REPORTING" },
        @{ From = "REPORTING"; To = "PR_OPEN" },
        @{ From = "PR_OPEN"; To = "AWAITING_APPROVAL" },
        @{ From = "AWAITING_APPROVAL"; To = "REBASE" },
        @{ From = "REBASE"; To = "VALIDATING" },
        @{ From = "REBASE"; To = "CONFLICT_RESOLUTION" },
        @{ From = "CONFLICT_RESOLUTION"; To = "REPORTING" },
        @{ From = "VALIDATING"; To = "MERGING" },
        @{ From = "MERGING"; To = "CLEANUP" },
        @{ From = "CLEANUP"; To = "COMPLETED" }
    )

    foreach ($transition in $validTransitions) {
        $result = Test-ValidTransition -FromState $transition.From -ToState $transition.To
        if (-not $result) {
            throw "Transition $($transition.From) -> $($transition.To) should be valid"
        }
    }
}

Invoke-BatchTest -Name "Invalid transitions are rejected" -Test {
    $invalidTransitions = @(
        @{ From = "INVESTIGATING"; To = "MERGING" },
        @{ From = "COMPLETED"; To = "INVESTIGATING" },
        @{ From = "AWAITING_APPROVAL"; To = "IMPLEMENTING" }
    )

    foreach ($transition in $invalidTransitions) {
        $result = Test-ValidTransition -FromState $transition.From -ToState $transition.To
        if ($result) {
            throw "Transition $($transition.From) -> $($transition.To) should be invalid"
        }
    }
}

Invoke-BatchTest -Name "Get-ValidTransitions returns correct transitions" -Test {
    $transitions = Get-ValidTransitions -State "REBASE"
    $expected = @("VALIDATING", "CONFLICT_RESOLUTION")

    if ($transitions.Count -ne $expected.Count) {
        throw "Expected $($expected.Count) transitions, got $($transitions.Count)"
    }

    foreach ($state in $expected) {
        if ($state -notin $transitions) {
            throw "Missing transition: $state"
        }
    }
}

# Approval Tests
Write-Host "`n=== Approval Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "New-BatchApproval creates valid approval" -Test {
    $approval = New-BatchApproval -IssueNumber 145 -CommitSha "abc123" -MainHeadSha "def456"

    if ($approval.IssueNumber -ne 145) {
        throw "IssueNumber mismatch"
    }
    if ($approval.CommitSha -ne "abc123") {
        throw "CommitSha mismatch"
    }
    if ($approval.MainHeadSha -ne "def456") {
        throw "MainHeadSha mismatch"
    }
    if (-not $approval.IsValid) {
        throw "Approval should be valid"
    }
}

Invoke-BatchTest -Name "Test-BatchApprovalValid validates correctly" -Test {
    $approval = New-BatchApproval -IssueNumber 145 -CommitSha "abc123" -MainHeadSha "def456"

    # Test valid case
    $result = Test-BatchApprovalValid -Approval $approval -CurrentCommitSha "abc123" -CurrentMainHeadSha "def456"
    if (-not $result.IsValid) {
        throw "Approval should be valid"
    }

    # Test commit SHA mismatch
    $result = Test-BatchApprovalValid -Approval $approval -CurrentCommitSha "xyz789" -CurrentMainHeadSha "def456"
    if ($result.IsValid) {
        throw "Approval should be invalid due to commit SHA mismatch"
    }

    # Test main HEAD change
    $result = Test-BatchApprovalValid -Approval $approval -CurrentCommitSha "abc123" -CurrentMainHeadSha "ghi012"
    if ($result.IsValid) {
        throw "Approval should be invalid due to main HEAD change"
    }
}

Invoke-BatchTest -Name "Invalidate-BatchApproval invalidates correctly" -Test {
    $approval = New-BatchApproval -IssueNumber 145 -CommitSha "abc123" -MainHeadSha "def456"

    $invalidated = Invalidate-BatchApproval -Approval $approval -Reason "Test invalidation"

    if ($invalidated.IsValid) {
        throw "Approval should be invalid after invalidation"
    }
    if ($invalidated.InvalidationReason -ne "Test invalidation") {
        throw "Invalidation reason mismatch"
    }
}

# State Definition Tests
Write-Host "`n=== State Definition Tests ===" -ForegroundColor Green

Invoke-BatchTest -Name "Get-StateDefinition returns complete definition" -Test {
    $definition = Get-StateDefinition

    $expectedStates = @(
        "INVESTIGATING",
        "IMPLEMENTING",
        "REPORTING",
        "PR_OPEN",
        "AWAITING_APPROVAL",
        "REBASE",
        "CONFLICT_RESOLUTION",
        "PR_UPDATED",
        "VALIDATING",
        "MERGING",
        "CLEANUP",
        "COMPLETED"
    )

    foreach ($state in $expectedStates) {
        if ($state -notin $definition.Keys) {
            throw "Missing state in definition: $state"
        }
        if ($definition[$state].State -ne $state) {
            throw "State mismatch in definition: $state"
        }
    }
}

# Summary
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
