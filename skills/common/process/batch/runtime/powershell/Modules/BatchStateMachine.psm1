#Requires -Version 7.0

<#
.SYNOPSIS
    State machine for Batch Skill orchestration.

.DESCRIPTION
    Defines states, transitions, and validation for Issue lifecycle management.
    Supports resumability through persistent state tracking.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

# State definitions
$Script:States = @{
    INVESTIGATING       = "INVESTIGATING"
    IMPLEMENTING        = "IMPLEMENTING"
    REPORTING           = "REPORTING"
    PR_OPEN             = "PR_OPEN"
    AWAITING_APPROVAL   = "AWAITING_APPROVAL"
    REBASE              = "REBASE"
    CONFLICT_RESOLUTION = "CONFLICT_RESOLUTION"
    PR_UPDATED          = "PR_UPDATED"
    VALIDATING          = "VALIDATING"
    MERGING             = "MERGING"
    CLEANUP             = "CLEANUP"
    COMPLETED           = "COMPLETED"
}

# Valid transitions
$Script:Transitions = @{
    INVESTIGATING       = @("IMPLEMENTING")
    IMPLEMENTING        = @("REPORTING")
    REPORTING           = @("PR_OPEN", "CONFLICT_RESOLUTION")
    PR_OPEN             = @("AWAITING_APPROVAL")
    AWAITING_APPROVAL   = @("REBASE")
    REBASE              = @("VALIDATING", "CONFLICT_RESOLUTION")
    CONFLICT_RESOLUTION = @("REPORTING")
    PR_UPDATED          = @("AWAITING_APPROVAL")
    VALIDATING          = @("MERGING")
    MERGING             = @("CLEANUP")
    CLEANUP             = @("COMPLETED")
    COMPLETED           = @()
}

function Get-BatchState {
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

function Test-ValidTransition {
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

function Get-ValidTransitions {
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

function Get-AllStates {
    <#
    .SYNOPSIS
        Gets all defined states.
    #>
    [CmdletBinding()]
    param()

    return $Script:States.Values
}

function Get-StateDefinition {
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
            ValidTransitions = Get-ValidTransitions -State $state
        }
    }
    return $definition
}

Export-ModuleMember -Function @(
    'Get-BatchState',
    'Test-ValidTransition',
    'Get-ValidTransitions',
    'Get-AllStates',
    'Get-StateDefinition'
)
