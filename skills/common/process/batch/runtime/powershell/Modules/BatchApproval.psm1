#Requires -Version 7.0

<#
.SYNOPSIS
    Approval state tracking for Batch Skill.

.DESCRIPTION
    Provides functions for managing approval states, including tracking
    approved commit SHAs and invalidation conditions.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-BatchApproval {
    <#
    .SYNOPSIS
        Creates a new approval record.
    .PARAMETER IssueNumber
        The Issue number.
    .PARAMETER CommitSha
        The commit SHA being approved.
    .PARAMETER MainHeadSha
        The main HEAD SHA at time of approval.
    .PARAMETER ApprovedBy
        Who approved the change.
    .PARAMETER Notes
        Optional approval notes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$IssueNumber,

        [Parameter(Mandatory = $true)]
        [string]$CommitSha,

        [Parameter(Mandatory = $true)]
        [string]$MainHeadSha,

        [Parameter(Mandatory = $false)]
        [string]$ApprovedBy = "user",

        [Parameter(Mandatory = $false)]
        [string]$Notes
    )

    $approval = @{
        IssueNumber = $IssueNumber
        CommitSha = $CommitSha
        MainHeadSha = $MainHeadSha
        ApprovedBy = $ApprovedBy
        ApprovedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        Notes = $Notes
        IsValid = $true
    }

    return $approval
}

function Test-BatchApprovalValid {
    <#
    .SYNOPSIS
        Tests if an approval is still valid.
    .PARAMETER Approval
        The approval record.
    .PARAMETER CurrentCommitSha
        The current commit SHA.
    .PARAMETER CurrentMainHeadSha
        The current main HEAD SHA.
    .OUTPUTS
        Hashtable with validation results.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Approval,

        [Parameter(Mandatory = $true)]
        [string]$CurrentCommitSha,

        [Parameter(Mandatory = $true)]
        [string]$CurrentMainHeadSha
    )

    $reasons = @()

    # Check if approval exists
    if (-not $Approval.IsValid) {
        $reasons += "Approval has been invalidated"
    }

    # Check commit SHA match
    if ($Approval.CommitSha -ne $CurrentCommitSha) {
        $reasons += "Commit SHA mismatch: approved=$($Approval.CommitSha), current=$CurrentCommitSha"
    }

    # Check main HEAD change
    if ($Approval.MainHeadSha -ne $CurrentMainHeadSha) {
        $reasons += "Main HEAD has changed: approved=$($Approval.MainHeadSha), current=$CurrentMainHeadSha"
    }

    return @{
        IsValid = $reasons.Count -eq 0
        Reasons = $reasons
        Approval = $Approval
    }
}

function Invalidate-BatchApproval {
    <#
    .SYNOPSIS
        Invalidates an approval record.
    .PARAMETER Approval
        The approval record.
    .PARAMETER Reason
        Reason for invalidation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Approval,

        [Parameter(Mandatory = $true)]
        [string]$Reason
    )

    $Approval.IsValid = $false
    $Approval.InvalidatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $Approval.InvalidationReason = $Reason

    return $Approval
}

function Get-BatchApprovalSummary {
    <#
    .SYNOPSIS
        Gets a summary of an approval record.
    .PARAMETER Approval
        The approval record.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Approval
    )

    $status = if ($Approval.IsValid) { "VALID" } else { "INVALID" }

    return @"
Approval Status: $status
Issue: #$($Approval.IssueNumber)
Approved Commit: $($Approval.CommitSha)
Main HEAD at Approval: $($Approval.MainHeadSha)
Approved By: $($Approval.ApprovedBy)
Approved At: $($Approval.ApprovedAt)
$(if (-not $Approval.IsValid) { "Invalidated At: $($Approval.InvalidatedAt)`nReason: $($Approval.InvalidationReason)" })
$(if ($Approval.Notes) { "Notes: $($Approval.Notes)" })
"@
}

function Save-BatchApproval {
    <#
    .SYNOPSIS
        Saves an approval record to file.
    .PARAMETER Approval
        The approval record.
    .PARAMETER FilePath
        Path to save the approval.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Approval,

        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $Approval | ConvertTo-Json -Depth 10 | Set-Content -Path $FilePath
}

function Get-BatchApproval {
    <#
    .SYNOPSIS
        Loads an approval record from file.
    .PARAMETER FilePath
        Path to load the approval from.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    if (-not (Test-Path $FilePath)) {
        return $null
    }

    return Get-Content $FilePath | ConvertFrom-Json -AsHashtable
}

Export-ModuleMember -Function @(
    'New-BatchApproval',
    'Test-BatchApprovalValid',
    'Invalidate-BatchApproval',
    'Get-BatchApprovalSummary',
    'Save-BatchApproval',
    'Get-BatchApproval'
)
