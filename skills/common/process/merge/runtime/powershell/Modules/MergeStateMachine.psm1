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
#
# Ordering invariant (Issue #247): the SHA-bound human approval gate runs on the
# FINAL merge candidate, so it is entered only after the mandatory rebase and
# the CI/review gates have produced that HEAD:
#
#   TRIGGER_CHECK -> MAIN_HEAD_REFRESH -> REBASE -> VALIDATING
#                 -> APPROVAL_VALIDATION -> MERGING -> MERGED -> CLEANUP
#
# Approval is never requested for an intermediate SHA the mandatory rebase is
# already known to discard. Any HEAD or main-HEAD movement seen after approval
# returns the flow to APPROVAL_VALIDATION (fresh approval) or MAIN_HEAD_REFRESH
# (re-rebase), never forward to MERGED.
$Script:Transitions = @{
    TRIGGER_CHECK       = @("MAIN_HEAD_REFRESH", "FAILED")
    MAIN_HEAD_REFRESH   = @("REBASE", "FAILED")
    REBASE              = @("VALIDATING", "CONFLICT", "FAILED")
    CONFLICT            = @()
    VALIDATING          = @("APPROVAL_VALIDATION", "FAILED")
    APPROVAL_VALIDATION = @("MERGING", "MAIN_HEAD_REFRESH", "FAILED")
    MERGING             = @("MERGED", "APPROVAL_VALIDATION", "MAIN_HEAD_REFRESH", "FAILED")
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
