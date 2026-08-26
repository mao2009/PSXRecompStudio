#Requires -Version 7.0

<#
.SYNOPSIS
    State machine for Batch Orchestrator.

.DESCRIPTION
    Defines batch-level states, transitions, and validation for
    multi-Issue parallel execution lifecycle management.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

$Script:BatchStates = @{
    BATCH_INITIALIZING = "BATCH_INITIALIZING"
    PLANNING           = "PLANNING"
    SCHEDULING         = "SCHEDULING"
    RUNNING            = "RUNNING"
    WAITING_FOR_MERGE  = "WAITING_FOR_MERGE"
    MERGING            = "MERGING"
    CLEANUP            = "CLEANUP"
    COMPLETED          = "COMPLETED"
    FAILED             = "FAILED"
}

$Script:BatchTransitions = @{
    BATCH_INITIALIZING = @("PLANNING", "FAILED")
    PLANNING           = @("SCHEDULING", "FAILED")
    SCHEDULING         = @("RUNNING", "FAILED")
    RUNNING            = @("WAITING_FOR_MERGE", "FAILED")
    WAITING_FOR_MERGE  = @("MERGING", "COMPLETED", "FAILED")
    MERGING            = @("CLEANUP", "FAILED")
    CLEANUP            = @("COMPLETED", "FAILED")
    COMPLETED          = @()
    FAILED             = @()
}

$Script:BatchStateDescriptions = @{
    BATCH_INITIALIZING = "Creating Worktrees, Branches, and environment for each Issue"
    PLANNING           = "Parsing dependencies, building DAG, detecting cycles"
    SCHEDULING         = "Determining execution order and concurrency groups"
    RUNNING            = "Sub-agents executing in parallel"
    WAITING_FOR_MERGE  = "All Sub-agents completed, waiting for user approval to merge"
    MERGING            = "Serializing PR merges via Merge Skill"
    CLEANUP            = "Removing Worktrees, Branches, and temporary files"
    COMPLETED          = "All Issues merged and cleaned up"
    FAILED             = "Batch processing failed"
}

function Get-BatchState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    if ($Script:BatchStates.ContainsKey($State)) {
        return $Script:BatchStates[$State]
    }
    throw "Invalid batch state: $State"
}

function Test-ValidBatchTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FromState,

        [Parameter(Mandatory = $true)]
        [string]$ToState
    )

    if (-not $Script:BatchTransitions.ContainsKey($FromState)) {
        return $false
    }

    return $Script:BatchTransitions[$FromState] -contains $ToState
}

function Get-ValidBatchTransitions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    if ($Script:BatchTransitions.ContainsKey($State)) {
        return $Script:BatchTransitions[$State]
    }
    return @()
}

function Get-AllBatchStates {
    [CmdletBinding()]
    param()

    return $Script:BatchStates.Values
}

function Get-BatchStateDefinition {
    [CmdletBinding()]
    param()

    $definition = @{}
    foreach ($state in $Script:BatchStates.Values) {
        $definition[$state] = @{
            State = $state
            Description = $Script:BatchStateDescriptions[$state]
            ValidTransitions = Get-ValidBatchTransitions -State $state
            IsTerminal = $Script:BatchTransitions[$state].Count -eq 0
        }
    }
    return $definition
}

function Test-BatchStateTerminal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    $transitions = Get-ValidBatchTransitions -State $State
    return $transitions.Count -eq 0
}

Export-ModuleMember -Function @(
    'Get-BatchState',
    'Test-ValidBatchTransition',
    'Get-ValidBatchTransitions',
    'Get-AllBatchStates',
    'Get-BatchStateDefinition',
    'Test-BatchStateTerminal'
)
