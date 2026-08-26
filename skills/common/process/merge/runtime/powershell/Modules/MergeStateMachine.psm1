#Requires -Version 7.0

<#
.SYNOPSIS
    State machine for PR Merge Skill.

.DESCRIPTION
    Defines states, transitions, and validation for PR merge lifecycle management.
    Supports resumability through persistent state tracking.

.NOTES
    Version: 1.0.0
    Issue: #146
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

# State definitions
$Script:States = @{
    TRIGGER_CHECK       = "TRIGGER_CHECK"
    APPROVAL_VALIDATION = "APPROVAL_VALIDATION"
    MAIN_HEAD_REFRESH   = "MAIN_HEAD_REFRESH"
    REBASE              = "REBASE"
    CONFLICT            = "CONFLICT"
    VALIDATING          = "VALIDATING"
    MERGING             = "MERGING"
    MERGED              = "MERGED"
    CLEANUP             = "CLEANUP"
    COMPLETED           = "COMPLETED"
    FAILED              = "FAILED"
}

# Valid transitions
$Script:Transitions = @{
    TRIGGER_CHECK       = @("APPROVAL_VALIDATION", "FAILED")
    APPROVAL_VALIDATION = @("MAIN_HEAD_REFRESH", "FAILED")
    MAIN_HEAD_REFRESH   = @("REBASE")
    REBASE              = @("VALIDATING", "CONFLICT")
    CONFLICT            = @()
    VALIDATING          = @("MERGING", "FAILED")
    MERGING             = @("MERGED", "FAILED")
    MERGED              = @("CLEANUP")
    CLEANUP             = @("COMPLETED", "FAILED")
    COMPLETED           = @()
    FAILED              = @()
}

function Get-MergeState {
    <#
    .SYNOPSIS
        Gets the current state definition.
    .PARAMETER State
        The state name to retrieve.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    if ($Script:States.ContainsKey($State)) {
        return $Script:States[$State]
    }
    throw "Invalid state: $State"
}

function Test-ValidMergeTransition {
    <#
    .SYNOPSIS
        Tests if a state transition is valid.
    .PARAMETER FromState
        The current state.
    .PARAMETER ToState
        The target state.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FromState,

        [Parameter(Mandatory = $true)]
        [string]$ToState
    )

    if (-not $Script:Transitions.ContainsKey($FromState)) {
        return $false
    }

    return $Script:Transitions[$FromState] -contains $ToState
}

function Get-ValidMergeTransitions {
    <#
    .SYNOPSIS
        Gets all valid transitions from a state.
    .PARAMETER State
        The current state.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    if ($Script:Transitions.ContainsKey($State)) {
        return $Script:Transitions[$State]
    }
    return @()
}

function Get-AllMergeStates {
    <#
    .SYNOPSIS
        Gets all defined states.
    #>
    [CmdletBinding()]
    param()

    return $Script:States.Values
}

function Get-MergeStateDefinition {
    <#
    .SYNOPSIS
        Gets the complete state definition for documentation.
    #>
    [CmdletBinding()]
    param()

    $definition = @{}
    foreach ($state in $Script:States.Values) {
        $definition[$state] = @{
            State = $state
            ValidTransitions = Get-ValidMergeTransitions -State $state
        }
    }
    return $definition
}

Export-ModuleMember -Function @(
    'Get-MergeState',
    'Test-ValidMergeTransition',
    'Get-ValidMergeTransitions',
    'Get-AllMergeStates',
    'Get-MergeStateDefinition'
)
