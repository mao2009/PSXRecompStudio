#Requires -Version 7.0

<#
.SYNOPSIS
    State persistence for Batch Orchestrator.
.DESCRIPTION
    Handles saving and loading batch and issue state to/from JSON files
    for crash recovery and resume capability.
.NOTES
    Version: 1.0.0
    Issue: #155
#>

function Get-BatchStateFilePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    return Join-Path $StateDir ".batch-state-$BatchId.json"
}

function Save-BatchState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$State,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    $State.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
    $State | ConvertTo-Json -Depth 20 | Set-Content -Path $FilePath
}

function Get-BatchState {
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

function Save-IssueStates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Issues,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    $data = @{
        UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        Issues = $Issues
    }
    $data | ConvertTo-Json -Depth 20 | Set-Content -Path $FilePath
}

function Get-IssueStates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    if (-not (Test-Path $FilePath)) {
        return $null
    }
    $data = Get-Content $FilePath | ConvertFrom-Json -AsHashtable
    return $data.Issues
}

function New-BatchState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [int]$IssueCount = 0
    )
    return @{
        BatchId = $BatchId
        State = "BATCH_INITIALIZING"
        IssueCount = $IssueCount
        CompletedCount = 0
        FailedCount = 0
        BlockedCount = 0
        CreatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        FailureReason = $null
        DependencyGraph = $null
        ConcurrencyGroups = $null
        MergeQueueStatus = $null
    }
}

function New-IssueState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId,
        [Parameter(Mandatory = $false)]
        [int]$IssueNumber = 0,
        [Parameter(Mandatory = $false)]
        [string]$Description = ""
    )
    return @{
        IssueId = $IssueId
        IssueNumber = $IssueNumber
        Description = $Description
        State = "WAITING_DEPENDENCY"
        Dependencies = @()
        WorktreePath = $null
        BranchName = $null
        PrNumber = $null
        PrUrl = $null
        CommitSha = $null
        ApprovedCommitSha = $null
        RetryCount = 0
        LastError = $null
        Report = $null
        SubAgentProcessId = $null
        StartedAt = $null
        CompletedAt = $null
        CreatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
}

function Sync-StateWithGitHub {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$BatchState,
        [Parameter(Mandatory = $true)]
        [hashtable]$IssueStates
    )
    $changes = @()
    foreach ($issueId in $IssueStates.Keys) {
        $issue = $IssueStates[$issueId]
        if ($issue.PrNumber) {
            $prArgs = @("pr", "view", $issue.PrNumber, "--json", "state,mergeCommit,headRefName")
            $prResult = & gh @prArgs 2>$null
            if ($LASTEXITCODE -eq 0) {
                $pr = $prResult | ConvertFrom-Json
                if ($pr.state -eq "MERGED" -and $issue.State -ne "COMPLETED") {
                    $issue.State = "COMPLETED"
                    $issue.CompletedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    $changes += "Issue $issueId already merged on GitHub"
                }
                if ($pr.state -eq "CLOSED" -and $issue.State -notin @("COMPLETED", "FAILED")) {
                    $issue.State = "FAILED"
                    $issue.LastError = "PR closed without merge"
                    $changes += "Issue $issueId PR was closed"
                }
            }
        }
        if ($issue.WorktreePath -and -not (Test-Path $issue.WorktreePath)) {
            if ($issue.State -notin @("COMPLETED", "FAILED", "BLOCKED")) {
                $changes += "Issue $issueId worktree no longer exists"
            }
        }
        $issue.UpdatedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    return @{
        Changes = $changes
        BatchState = $BatchState
        IssueStates = $IssueStates
    }
}

Export-ModuleMember -Function @(
    'Get-BatchStateFilePath',
    'Save-BatchState',
    'Get-BatchState',
    'Save-IssueStates',
    'Get-IssueStates',
    'New-BatchState',
    'New-IssueState',
    'Sync-StateWithGitHub'
)
