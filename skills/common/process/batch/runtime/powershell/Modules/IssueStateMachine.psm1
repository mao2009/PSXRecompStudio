#Requires -Version 7.0

<#
.SYNOPSIS
    State machine for individual Issue lifecycle within Batch Orchestrator.

.DESCRIPTION
    Defines per-Issue states, transitions, and validation for
    Sub-agent execution, retry, approval, merge, and cleanup.

.NOTES
    Version: 1.1.0
    Issue: #155, #170
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

$Script:IssueStates = @{
    SUBAGENT_STARTING      = "SUBAGENT_STARTING"
    SUBAGENT_RUNNING       = "SUBAGENT_RUNNING"
    SUBAGENT_RETRYING      = "SUBAGENT_RETRYING"
    SUBAGENT_FAILED        = "SUBAGENT_FAILED"
    ORPHANED               = "ORPHANED"
    WAITING_FOR_SUBAGENT   = "WAITING_FOR_SUBAGENT"
    WAITING_DEPENDENCY     = "WAITING_DEPENDENCY"
    PR_READY               = "PR_READY"
    WAITING_FOR_APPROVAL   = "WAITING_FOR_APPROVAL"
    READY_FOR_MERGE        = "READY_FOR_MERGE"
    MERGING                = "MERGING"
    COMPLETED              = "COMPLETED"
    BLOCKED                = "BLOCKED"
    FAILED                 = "FAILED"
}

$Script:IssueTransitions = @{
    SUBAGENT_STARTING    = @("SUBAGENT_RUNNING", "SUBAGENT_RETRYING", "SUBAGENT_FAILED", "ORPHANED", "PR_READY")
    SUBAGENT_RUNNING     = @("PR_READY", "SUBAGENT_RETRYING", "SUBAGENT_FAILED", "ORPHANED")
    SUBAGENT_RETRYING    = @("SUBAGENT_STARTING", "SUBAGENT_FAILED", "ORPHANED")
    SUBAGENT_FAILED      = @()
    ORPHANED             = @("SUBAGENT_STARTING", "SUBAGENT_FAILED", "WAITING_FOR_SUBAGENT")
    WAITING_FOR_SUBAGENT = @("SUBAGENT_STARTING", "BLOCKED")
    WAITING_DEPENDENCY   = @("SUBAGENT_STARTING")
    PR_READY             = @("WAITING_FOR_APPROVAL")
    WAITING_FOR_APPROVAL = @("READY_FOR_MERGE", "PR_READY")
    READY_FOR_MERGE      = @("MERGING")
    MERGING              = @("COMPLETED", "FAILED", "PR_READY")
    COMPLETED            = @()
    BLOCKED              = @("WAITING_FOR_SUBAGENT")
    FAILED               = @()
}

$Script:IssueStateDescriptions = @{
    SUBAGENT_STARTING    = "Sub-agent process being launched"
    SUBAGENT_RUNNING     = "Sub-agent actively investigating, implementing, testing"
    SUBAGENT_RETRYING    = "Sub-agent failed, attempting retry"
    SUBAGENT_FAILED      = "Sub-agent failed after retry limit exhausted"
    ORPHANED             = "Sub-agent process lost (crash/timeout), recovery pending"
    WAITING_FOR_SUBAGENT = "Waiting for Sub-agent to start (concurrency slot available)"
    WAITING_DEPENDENCY   = "Waiting for dependent Issue to complete"
    PR_READY             = "PR created, awaiting user approval"
    WAITING_FOR_APPROVAL = "Waiting for user approval on PR"
    READY_FOR_MERGE      = "Approved, ready to be merged via Merge Skill"
    MERGING              = "Merge Skill processing PR"
    COMPLETED            = "Issue fully merged and cleaned up"
    BLOCKED              = "Issue blocked, requires user intervention"
    FAILED               = "Issue processing failed"
}

function Get-IssueState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    if ($Script:IssueStates.ContainsKey($State)) {
        return $Script:IssueStates[$State]
    }
    throw "Invalid issue state: $State"
}

function Test-ValidIssueTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FromState,

        [Parameter(Mandatory = $true)]
        [string]$ToState
    )

    if (-not $Script:IssueTransitions.ContainsKey($FromState)) {
        return $false
    }

    return $Script:IssueTransitions[$FromState] -contains $ToState
}

function Get-ValidIssueTransitions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    if ($Script:IssueTransitions.ContainsKey($State)) {
        return $Script:IssueTransitions[$State]
    }
    return @()
}

function Get-AllIssueStates {
    [CmdletBinding()]
    param()

    return $Script:IssueStates.Values
}

function Get-IssueStateDefinition {
    [CmdletBinding()]
    param()

    $definition = @{}
    foreach ($state in $Script:IssueStates.Values) {
        $definition[$state] = @{
            State = $state
            Description = $Script:IssueStateDescriptions[$state]
            ValidTransitions = Get-ValidIssueTransitions -State $state
            IsTerminal = $Script:IssueTransitions[$state].Count -eq 0
        }
    }
    return $definition
}

function Test-IssueStateTerminal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    $transitions = Get-ValidIssueTransitions -State $State
    return $transitions.Count -eq 0
}

function Test-IssueStateActive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    $activeStates = @(
        "SUBAGENT_STARTING",
        "SUBAGENT_RUNNING",
        "SUBAGENT_RETRYING",
        "WAITING_FOR_SUBAGENT",
        "WAITING_DEPENDENCY",
        "PR_READY",
        "WAITING_FOR_APPROVAL",
        "READY_FOR_MERGE",
        "MERGING"
    )
    return $State -in $activeStates
}

function Test-IssueStateRecoverable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    $recoverableStates = @("ORPHANED", "SUBAGENT_FAILED", "BLOCKED")
    return $State -in $recoverableStates
}

Export-ModuleMember -Function @(
    'Get-IssueState',
    'Test-ValidIssueTransition',
    'Get-ValidIssueTransitions',
    'Get-AllIssueStates',
    'Get-IssueStateDefinition',
    'Test-IssueStateTerminal',
    'Test-IssueStateActive',
    'Test-IssueStateRecoverable'
)
