#Requires -Version 7.0

<#
.SYNOPSIS
    Sub-agent lifecycle management for Batch Orchestrator.

.DESCRIPTION
    Handles Sub-agent launching, retry with backoff, failure isolation,
    and structured reporting. Orchestrator never substitutes implementation.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-SubAgentConfig {
    <#
    .SYNOPSIS
        Creates Sub-agent configuration.
    .PARAMETER MaxRetries
        Maximum retry attempts.
    .PARAMETER TimeoutMinutes
        Sub-agent timeout in minutes.
    .PARAMETER BackoffBaseSeconds
        Base backoff duration in seconds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [int]$MaxRetries = 3,

        [Parameter(Mandatory = $false)]
        [int]$TimeoutMinutes = 30,

        [Parameter(Mandatory = $false)]
        [int]$BackoffBaseSeconds = 5
    )

    return @{
        MaxRetries = $MaxRetries
        TimeoutMinutes = $TimeoutMinutes
        BackoffBaseSeconds = $BackoffBaseSeconds
    }
}

function New-SubAgentState {
    <#
    .SYNOPSIS
        Creates initial Sub-agent state for an issue.
    .PARAMETER IssueId
        The issue identifier.
    .PARAMETER Config
        Sub-agent configuration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $true)]
        [hashtable]$Config
    )

    return @{
        IssueId = $IssueId
        State = "SUBAGENT_STARTING"
        RetryCount = 0
        MaxRetries = $Config.MaxRetries
        ProcessId = $null
        StartedAt = $null
        CompletedAt = $null
        LastError = $null
        Report = $null
        PrNumber = $null
        CommitSha = $null
        WorktreePath = $null
        BranchName = $null
        BackoffSeconds = $Config.BackoffBaseSeconds
    }
}

function Test-SubAgentRetryable {
    <#
    .SYNOPSIS
        Tests if a Sub-agent failure is retryable.
    .PARAMETER SubAgentState
        The Sub-agent state.
    .PARAMETER ErrorCategory
        The error category.
    .OUTPUTS
        Hashtable with Retryable (bool) and Reason.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SubAgentState,

        [Parameter(Mandatory = $false)]
        [string]$ErrorCategory = "transient"
    )

    $retryableCategories = @("api_error", "timeout", "connection_failure", "transient")
    $nonRetryableCategories = @("code_error", "test_failure", "architecture_violation", "dependency_conflict")

    if ($ErrorCategory -in $nonRetryableCategories) {
        return @{
            Retryable = $false
            Reason = "Non-retryable error category: $ErrorCategory"
        }
    }

    if ($SubAgentState.RetryCount -ge $SubAgentState.MaxRetries) {
        return @{
            Retryable = $false
            Reason = "Retry limit reached ($($SubAgentState.MaxRetries))"
        }
    }

    if ($ErrorCategory -in $retryableCategories) {
        return @{
            Retryable = $true
            Reason = "Retryable error: $ErrorCategory (attempt $($SubAgentState.RetryCount + 1)/$($SubAgentState.MaxRetries))"
        }
    }

    return @{
        Retryable = $true
        Reason = "Unknown category treated as retryable (attempt $($SubAgentState.RetryCount + 1)/$($SubAgentState.MaxRetries))"
    }
}

function Get-SubAgentBackoffDuration {
    <#
    .SYNOPSIS
        Calculates exponential backoff duration.
    .PARAMETER SubAgentState
        The Sub-agent state.
    .OUTPUTS
        Duration in seconds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SubAgentState
    )

    $exponential = $SubAgentState.BackoffSeconds * [Math]::Pow(2, $SubAgentState.RetryCount)
    $jitter = Get-Random -Minimum 0 -Maximum ($exponential * 0.1)
    return [Math]::Ceiling($exponential + $jitter)
}

function Invoke-SubAgentRetry {
    <#
    .SYNOPSIS
        Prepares Sub-agent state for retry.
    .PARAMETER SubAgentState
        The Sub-agent state.
    .PARAMETER ErrorCategory
        The error category.
    .OUTPUTS
        Updated Sub-agent state.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SubAgentState,

        [Parameter(Mandatory = $false)]
        [string]$ErrorCategory = "transient"
    )

    $retryCheck = Test-SubAgentRetryable -SubAgentState $SubAgentState -ErrorCategory $ErrorCategory
    if (-not $retryCheck.Retryable) {
        $SubAgentState.State = "SUBAGENT_FAILED"
        $SubAgentState.LastError = $retryCheck.Reason
        return $SubAgentState
    }

    $SubAgentState.State = "SUBAGENT_RETRYING"
    $SubAgentState.RetryCount++
    $SubAgentState.ProcessId = $null
    $SubAgentState.LastError = "Retry #$($SubAgentState.RetryCount) after $ErrorCategory"

    $backoff = Get-SubAgentBackoffDuration -SubAgentState $SubAgentState
    Write-Host "Waiting $($backoff)s before retry..." -ForegroundColor Yellow
    Start-Sleep -Seconds $backoff

    $SubAgentState.State = "SUBAGENT_STARTING"
    return $SubAgentState
}

function New-SubAgentReport {
    <#
    .SYNOPSIS
        Creates a structured Sub-agent report template.
    .PARAMETER IssueId
        The issue identifier.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId
    )

    return @{
        IssueId = $IssueId
        InvestigatedAt = $null
        InvestigationSummary = $null
        ImplementedAt = $null
        ImplementationSummary = $null
        DesignDecision = $null
        ChangedFiles = @()
        TestResults = $null
        TestPassed = $false
        RemainingIssues = @()
        PrNumber = $null
        PrUrl = $null
        CommitSha = $null
        ReportedAt = $null
    }
}

function Test-SubAgentReportComplete {
    <#
    .SYNOPSIS
        Validates that a Sub-agent report is complete.
    .PARAMETER Report
        The Sub-agent report.
    .OUTPUTS
        Hashtable with IsComplete (bool) and MissingFields.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Report
    )

    $requiredFields = @(
        "InvestigationSummary",
        "ImplementationSummary",
        "DesignDecision",
        "ChangedFiles",
        "TestResults",
        "PrNumber",
        "CommitSha"
    )

    $missing = @()
    foreach ($field in $requiredFields) {
        if (-not $Report.ContainsKey($field) -or $null -eq $Report[$field] -or $Report[$field] -eq "") {
            $missing += $field
        }
    }

    if ($Report.ContainsKey("ChangedFiles") -and $Report.ChangedFiles.Count -eq 0) {
        $missing += "ChangedFiles (empty)"
    }

    return @{
        IsComplete = $missing.Count -eq 0
        MissingFields = $missing
    }
}

function Get-SubAgentFailureCategory {
    <#
    .SYNOPSIS
        Categorizes a Sub-agent failure.
    .PARAMETER ErrorMessage
        The error message.
    .OUTPUTS
        Error category string.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    $lowerError = $ErrorMessage.ToLower()

    if ($lowerError -match "timeout|timed out|deadline exceeded") {
        return "timeout"
    }
    if ($lowerError -match "connection|network|dns|socket") {
        return "connection_failure"
    }
    if ($lowerError -match "rate limit|429|too many requests") {
        return "api_error"
    }
    if ($lowerError -match "test.*fail|assertion|expected.*but got") {
        return "test_failure"
    }
    if ($lowerError -match "compil|syntax|type.*error|lint") {
        return "code_error"
    }
    if ($lowerError -match "depend|import|module.*not found") {
        return "dependency_conflict"
    }

    return "transient"
}

Export-ModuleMember -Function @(
    'New-SubAgentConfig',
    'New-SubAgentState',
    'Test-SubAgentRetryable',
    'Get-SubAgentBackoffDuration',
    'Invoke-SubAgentRetry',
    'New-SubAgentReport',
    'Test-SubAgentReportComplete',
    'Get-SubAgentFailureCategory'
)
