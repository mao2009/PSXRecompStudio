#Requires -Version 7.0

<#
.SYNOPSIS
    Scheduler for parallel Issue execution in Batch Orchestrator.

.DESCRIPTION
    Manages concurrency limits, parallel dispatch, and execution
    coordination for independent Issues.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-BatchScheduler {
    <#
    .SYNOPSIS
        Creates a new scheduler instance.
    .PARAMETER MaxConcurrency
        Maximum number of parallel Sub-agents.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [int]$MaxConcurrency = 3
    )

    return @{
        MaxConcurrency = $MaxConcurrency
        RunningSlots = 0
        ActiveIssues = @{}
        QueuedIssues = [System.Collections.Queue]::new()
        CompletedIssues = @()
        FailedIssues = @()
        BlockedIssues = @()
    }
}

function Test-SchedulerSlotAvailable {
    <#
    .SYNOPSIS
        Checks if a concurrency slot is available.
    .PARAMETER Scheduler
        The scheduler instance.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler
    )

    return $Scheduler.RunningSlots -lt $Scheduler.MaxConcurrency
}

function Register-SchedulerIssue {
    <#
    .SYNOPSIS
        Registers an issue with the scheduler.
    .PARAMETER Scheduler
        The scheduler instance.
    .PARAMETER IssueId
        The issue identifier.
    .PARAMETER IssueData
        Issue metadata.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler,

        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $false)]
        [hashtable]$IssueData = @{}
    )

    $Scheduler.ActiveIssues[$IssueId] = @{
        Id = $IssueId
        State = "WAITING_DEPENDENCY"
        Data = $IssueData
        StartedAt = $null
        CompletedAt = $null
        RetryCount = 0
        LastError = $null
    }
}

function Start-SchedulerIssue {
    <#
    .SYNOPSIS
        Claims a concurrency slot for an issue.
    .PARAMETER Scheduler
        The scheduler instance.
    .PARAMETER IssueId
        The issue to start.
    .OUTPUTS
        Hashtable with success status.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler,

        [Parameter(Mandatory = $true)]
        [string]$IssueId
    )

    if (-not (Test-SchedulerSlotAvailable -Scheduler $Scheduler)) {
        return @{
            Success = $false
            Reason = "No concurrency slot available"
        }
    }

    if (-not $Scheduler.ActiveIssues.ContainsKey($IssueId)) {
        return @{
            Success = $false
            Reason = "Issue $IssueId not registered"
        }
    }

    $Scheduler.RunningSlots++
    $Scheduler.ActiveIssues[$IssueId].State = "SUBAGENT_STARTING"
    $Scheduler.ActiveIssues[$IssueId].StartedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")

    return @{ Success = $true }
}

function Complete-SchedulerIssue {
    <#
    .SYNOPSIS
        Marks an issue as completed and releases its slot.
    .PARAMETER Scheduler
        The scheduler instance.
    .PARAMETER IssueId
        The completed issue.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler,

        [Parameter(Mandatory = $true)]
        [string]$IssueId
    )

    if ($Scheduler.ActiveIssues.ContainsKey($IssueId)) {
        $Scheduler.ActiveIssues[$IssueId].State = "COMPLETED"
        $Scheduler.ActiveIssues[$IssueId].CompletedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        $Scheduler.CompletedIssues += $IssueId
        $Scheduler.RunningSlots--
    }
}

function Fail-SchedulerIssue {
    <#
    .SYNOPSIS
        Marks an issue as failed and releases its slot.
    .PARAMETER Scheduler
        The scheduler instance.
    .PARAMETER IssueId
        The failed issue.
    .PARAMETER Error
        The error message.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler,

        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $false)]
        [string]$ErrorMessage = "Unknown error"
    )

    if ($Scheduler.ActiveIssues.ContainsKey($IssueId)) {
        $Scheduler.ActiveIssues[$IssueId].State = "SUBAGENT_FAILED"
        $Scheduler.ActiveIssues[$IssueId].LastError = $ErrorMessage
        $Scheduler.FailedIssues += $IssueId
        $Scheduler.RunningSlots--
    }
}

function Block-SchedulerIssue {
    <#
    .SYNOPSIS
        Marks an issue as blocked.
    .PARAMETER Scheduler
        The scheduler instance.
    .PARAMETER IssueId
        The blocked issue.
    .PARAMETER Reason
        The blocking reason.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler,

        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $false)]
        [string]$Reason = "Unknown"
    )

    if ($Scheduler.ActiveIssues.ContainsKey($IssueId)) {
        $Scheduler.ActiveIssues[$IssueId].State = "BLOCKED"
        $Scheduler.ActiveIssues[$IssueId].LastError = $Reason
        $Scheduler.BlockedIssues += $IssueId
        if ($Scheduler.ActiveIssues[$IssueId].State -eq "SUBAGENT_RUNNING") {
            $Scheduler.RunningSlots--
        }
    }
}

function Get-SchedulerStatus {
    <#
    .SYNOPSIS
        Gets the current scheduler status.
    .PARAMETER Scheduler
        The scheduler instance.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler
    )

    $activeCount = 0
    foreach ($issue in $Scheduler.ActiveIssues.Values) {
        if (Test-IssueStateActive -State $issue.State) {
            $activeCount++
        }
    }

    return @{
        MaxConcurrency = $Scheduler.MaxConcurrency
        RunningSlots = $Scheduler.RunningSlots
        ActiveCount = $activeCount
        CompletedCount = $Scheduler.CompletedIssues.Count
        FailedCount = $Scheduler.FailedIssues.Count
        BlockedCount = $Scheduler.BlockedIssues.Count
        TotalCount = $Scheduler.ActiveIssues.Count
        AllCompleted = ($Scheduler.CompletedIssues.Count + $Scheduler.FailedIssues.Count + $Scheduler.BlockedIssues.Count) -ge $Scheduler.ActiveIssues.Count -and $Scheduler.ActiveIssues.Count -gt 0
    }
}

function Get-SchedulerReadyIssues {
    <#
    .SYNOPSIS
        Gets issues ready to start based on dependency completion.
    .PARAMETER Scheduler
        The scheduler instance.
    .OUTPUTS
        Array of issue IDs ready to start.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Scheduler
    )

    $ready = @()
    foreach ($issueId in $Scheduler.ActiveIssues.Keys) {
        $issue = $Scheduler.ActiveIssues[$issueId]
        if ($issue.State -eq "WAITING_DEPENDENCY") {
            $ready += $issueId
        }
    }
    return $ready
}

Export-ModuleMember -Function @(
    'New-BatchScheduler',
    'Test-SchedulerSlotAvailable',
    'Register-SchedulerIssue',
    'Start-SchedulerIssue',
    'Complete-SchedulerIssue',
    'Fail-SchedulerIssue',
    'Block-SchedulerIssue',
    'Get-SchedulerStatus',
    'Get-SchedulerReadyIssues'
)
