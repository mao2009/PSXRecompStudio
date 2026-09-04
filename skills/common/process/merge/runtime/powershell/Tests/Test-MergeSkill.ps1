#Requires -Version 7.0

<#
.SYNOPSIS
    Tests for PR Merge Skill state machine and utilities.

.DESCRIPTION
    Unit tests for the PR Merge Skill implementation.

.NOTES
    Version: 1.0.0
    Issue: #146
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

# Import modules using cross-platform path construction
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptPath ".." "Modules"
Import-Module (Join-Path $modulePath "MergeStateMachine.psm1") -Force
Import-Module (Join-Path $modulePath "MergeApproval.psm1") -Force

# Test results
$testResults = @{
    Passed = 0
    Failed = 0
    Tests = @()
}

function Invoke-MergeTest {
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

Invoke-MergeTest -Name "Get-MergeState returns valid states" -Test {
    $states = Get-AllMergeStates
    $expectedStates = @(
        "TRIGGER_CHECK",
        "APPROVAL_VALIDATION",
        "MAIN_HEAD_REFRESH",
        "REBASE",
        "CONFLICT",
        "VALIDATING",
        "MERGING",
        "MERGED",
        "CLEANUP",
        "COMPLETED",
        "FAILED"
    )

    foreach ($state in $expectedStates) {
        if ($state -notin $states) {
            throw "Missing state: $state"
        }
    }
}

Invoke-MergeTest -Name "Valid transitions are allowed" -Test {
    $validTransitions = @(
        @{ From = "TRIGGER_CHECK"; To = "MAIN_HEAD_REFRESH" },
        @{ From = "TRIGGER_CHECK"; To = "FAILED" },
        @{ From = "MAIN_HEAD_REFRESH"; To = "REBASE" },
        @{ From = "MAIN_HEAD_REFRESH"; To = "FAILED" },
        @{ From = "REBASE"; To = "VALIDATING" },
        @{ From = "REBASE"; To = "CONFLICT" },
        @{ From = "VALIDATING"; To = "APPROVAL_VALIDATION" },
        @{ From = "VALIDATING"; To = "FAILED" },
        @{ From = "APPROVAL_VALIDATION"; To = "MERGING" },
        @{ From = "APPROVAL_VALIDATION"; To = "MAIN_HEAD_REFRESH" },
        @{ From = "APPROVAL_VALIDATION"; To = "FAILED" },
        @{ From = "MERGING"; To = "MERGED" },
        @{ From = "MERGING"; To = "APPROVAL_VALIDATION" },
        @{ From = "MERGING"; To = "MAIN_HEAD_REFRESH" },
        @{ From = "MERGING"; To = "FAILED" },
        @{ From = "MERGED"; To = "CLEANUP" },
        @{ From = "CLEANUP"; To = "COMPLETED" },
        @{ From = "CLEANUP"; To = "FAILED" }
    )

    foreach ($transition in $validTransitions) {
        $result = Test-ValidMergeTransition -FromState $transition.From -ToState $transition.To
        if (-not $result) {
            throw "Transition $($transition.From) -> $($transition.To) should be valid"
        }
    }
}

Invoke-MergeTest -Name "Invalid transitions are rejected" -Test {
    $invalidTransitions = @(
        @{ From = "TRIGGER_CHECK"; To = "MERGING" },
        @{ From = "COMPLETED"; To = "TRIGGER_CHECK" },
        @{ From = "CONFLICT"; To = "VALIDATING" },
        @{ From = "FAILED"; To = "REBASE" },
        # The approval gate binds to the final merge candidate (Issue #247), so
        # it is neither reachable before the mandatory rebase nor skippable.
        @{ From = "TRIGGER_CHECK"; To = "APPROVAL_VALIDATION" },
        @{ From = "REBASE"; To = "APPROVAL_VALIDATION" },
        @{ From = "VALIDATING"; To = "MERGING" },
        @{ From = "APPROVAL_VALIDATION"; To = "MERGED" }
    )

    foreach ($transition in $invalidTransitions) {
        $result = Test-ValidMergeTransition -FromState $transition.From -ToState $transition.To
        if ($result) {
            throw "Transition $($transition.From) -> $($transition.To) should be invalid"
        }
    }
}

Invoke-MergeTest -Name "Get-ValidMergeTransitions returns correct transitions" -Test {
    $transitions = Get-ValidMergeTransitions -State "REBASE"
    $expected = @("VALIDATING", "CONFLICT", "FAILED")

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

Invoke-MergeTest -Name "New-MergeApproval creates valid approval" -Test {
    $approval = New-MergeApproval -PrNumber 149 -IssueNumber 148 -CommitSha "abc123" -MainHeadSha "def456"

    if ($approval.PrNumber -ne 149) {
        throw "PrNumber mismatch"
    }
    if ($approval.IssueNumber -ne 148) {
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

Invoke-MergeTest -Name "Test-MergeApprovalValid validates correctly" -Test {
    $approval = New-MergeApproval -PrNumber 149 -IssueNumber 148 -CommitSha "abc123" -MainHeadSha "def456"

    # Test valid case
    $result = Test-MergeApprovalValid -Approval $approval -CurrentCommitSha "abc123" -CurrentMainHeadSha "def456"
    if (-not $result.IsValid) {
        throw "Approval should be valid"
    }

    # Test commit SHA mismatch
    $result = Test-MergeApprovalValid -Approval $approval -CurrentCommitSha "xyz789" -CurrentMainHeadSha "def456"
    if ($result.IsValid) {
        throw "Approval should be invalid due to commit SHA mismatch"
    }

    # Test main HEAD change
    $result = Test-MergeApprovalValid -Approval $approval -CurrentCommitSha "abc123" -CurrentMainHeadSha "ghi012"
    if ($result.IsValid) {
        throw "Approval should be invalid due to main HEAD change"
    }
}

Invoke-MergeTest -Name "Invalidate-MergeApproval invalidates correctly" -Test {
    $approval = New-MergeApproval -PrNumber 149 -IssueNumber 148 -CommitSha "abc123" -MainHeadSha "def456"

    $invalidated = Invalidate-MergeApproval -Approval $approval -Reason "Test invalidation"

    if ($invalidated.IsValid) {
        throw "Approval should be invalid after invalidation"
    }
    if ($invalidated.InvalidationReason -ne "Test invalidation") {
        throw "Invalidation reason mismatch"
    }
}

Invoke-MergeTest -Name "Approval invalidation on rebase" -Test {
    $approval = New-MergeApproval -PrNumber 149 -IssueNumber 148 -CommitSha "abc123" -MainHeadSha "def456"

    # Simulate rebase changing commit
    $result = Test-MergeApprovalValid -Approval $approval -CurrentCommitSha "new_commit" -CurrentMainHeadSha "def456"
    if ($result.IsValid) {
        throw "Approval should be invalid after rebase changes commit"
    }
}

Invoke-MergeTest -Name "Approval invalidation on main HEAD change" -Test {
    $approval = New-MergeApproval -PrNumber 149 -IssueNumber 148 -CommitSha "abc123" -MainHeadSha "def456"

    # Simulate main HEAD change
    $result = Test-MergeApprovalValid -Approval $approval -CurrentCommitSha "abc123" -CurrentMainHeadSha "new_main_head"
    if ($result.IsValid) {
        throw "Approval should be invalid when main HEAD changes"
    }
}

# State Definition Tests
Write-Host "`n=== State Definition Tests ===" -ForegroundColor Green

Invoke-MergeTest -Name "Get-MergeStateDefinition returns complete definition" -Test {
    $definition = Get-MergeStateDefinition

    $expectedStates = @(
        "TRIGGER_CHECK",
        "APPROVAL_VALIDATION",
        "MAIN_HEAD_REFRESH",
        "REBASE",
        "CONFLICT",
        "VALIDATING",
        "MERGING",
        "MERGED",
        "CLEANUP",
        "COMPLETED",
        "FAILED"
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

# Safety Tests
Write-Host "`n=== Safety Tests ===" -ForegroundColor Green

Invoke-MergeTest -Name "Admin bypass is not in merge strategy" -Test {
    $config = Get-Content (Join-Path $scriptPath ".." ".." ".." "config" "merge-config.json") | ConvertFrom-Json

    if (-not $config.merge.forbid_admin_bypass) {
        throw "Admin bypass should be forbidden"
    }
    if (-not $config.merge.forbid_force_push) {
        throw "Force push should be forbidden"
    }
    if (-not $config.merge.forbid_direct_push) {
        throw "Direct push should be forbidden"
    }
    if (-not $config.merge.forbid_protection_bypass) {
        throw "Protection bypass should be forbidden"
    }
}

Invoke-MergeTest -Name "CONFLICT state has no valid transitions" -Test {
    $transitions = Get-ValidMergeTransitions -State "CONFLICT"
    if ($transitions.Count -ne 0) {
        throw "CONFLICT state should have no valid transitions (must return to caller)"
    }
}

Invoke-MergeTest -Name "FAILED state has no valid transitions" -Test {
    $transitions = Get-ValidMergeTransitions -State "FAILED"
    if ($transitions.Count -ne 0) {
        throw "FAILED state should have no valid transitions"
    }
}

Invoke-MergeTest -Name "COMPLETED state has no valid transitions" -Test {
    $transitions = Get-ValidMergeTransitions -State "COMPLETED"
    if ($transitions.Count -ne 0) {
        throw "COMPLETED state should have no valid transitions"
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
