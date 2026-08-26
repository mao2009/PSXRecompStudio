#Requires -Version 7.0

<#
.SYNOPSIS
    Git utilities for PR Merge Skill.

.DESCRIPTION
    Provides functions for Git operations required for safe PR merging,
    including rebase, merge verification, and branch management.
    Explicitly forbids admin bypass and protection circumvention.

.NOTES
    Version: 1.0.0
    Issue: #146
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function Get-MergeMainHead {
    <#
    .SYNOPSIS
        Gets the latest main HEAD commit SHA.
    #>
    [CmdletBinding()]
    param()

    git fetch origin main 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch origin main"
    }
    return git rev-parse origin/main
}

function Invoke-MergeRebase {
    <#
    .SYNOPSIS
        Rebases the current Branch onto main.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .OUTPUTS
        Hashtable with success status and conflict information.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $originalLocation = Get-Location
    try {
        Set-Location $WorktreePath

        # Fetch latest main
        Write-Host "Fetching latest main..." -ForegroundColor Cyan
        git fetch origin main 2>$null

        # Attempt rebase
        Write-Host "Rebasing onto origin/main..." -ForegroundColor Cyan
        $rebaseOutput = git rebase origin/main 2>&1

        if ($LASTEXITCODE -eq 0) {
            return @{
                Success = $true
                HasConflicts = $false
                Message = "Rebase succeeded"
            }
        }

        # Check for conflicts
        $conflictFiles = git diff --name-only --diff-filter=U 2>$null
        if ($conflictFiles.Count -gt 0) {
            # Abort rebase to leave clean state
            git rebase --abort 2>$null
            return @{
                Success = $false
                HasConflicts = $true
                ConflictFiles = @($conflictFiles)
                Message = "Rebase failed with conflicts"
            }
        }

        # Abort rebase on other failures
        git rebase --abort 2>$null
        return @{
            Success = $false
            HasConflicts = $false
            Message = "Rebase failed without conflicts"
        }
    }
    finally {
        Set-Location $originalLocation
    }
}

function Stop-MergeRebase {
    <#
    .SYNOPSIS
        Aborts an in-progress rebase.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $originalLocation = Get-Location
    try {
        Set-Location $WorktreePath
        git rebase --abort 2>$null
    }
    finally {
        Set-Location $originalLocation
    }
}

function Invoke-NormalMerge {
    <#
    .SYNOPSIS
        Merges a PR using standard GitHub CLI.
    .PARAMETER PrNumber
        The PR number to merge.
    .PARAMETER Repository
        Optional repository (owner/repo).
    .OUTPUTS
        Hashtable with success status.
    .NOTES
        NEVER use --admin flag. This function enforces standard merge only.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$PrNumber,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    # Build merge command - STANDARD MERGE ONLY
    $mergeArgs = @("pr", "merge", $PrNumber, "--merge")
    if ($Repository) {
        $mergeArgs += "--repo"
        $mergeArgs += $Repository
    }

    Write-Host "Merging PR #$PrNumber using standard merge..." -ForegroundColor Cyan
    Write-Host "Command: gh $($mergeArgs -join ' ')" -ForegroundColor Gray
    Write-Host "IMPORTANT: Using standard merge path (no --admin)" -ForegroundColor Yellow

    & gh @mergeArgs 2>&1

    if ($LASTEXITCODE -eq 0) {
        return @{
            Success = $true
            Message = "Standard merge succeeded"
        }
    }

    return @{
        Success = $false
        Message = "Standard merge failed"
    }
}

function Test-MergePrMerged {
    <#
    .SYNOPSIS
        Tests if a PR has been merged.
    .PARAMETER PrNumber
        The PR number.
    .PARAMETER Repository
        Optional repository (owner/repo).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$PrNumber,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    $prArgs = @("pr", "view", $PrNumber, "--json", "state,mergeCommit")
    if ($Repository) {
        $prArgs += "--repo"
        $prArgs += $Repository
    }

    $result = & gh @prArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        return @{
            IsMerged = $false
            Error = "Failed to get PR status"
        }
    }

    $pr = $result | ConvertFrom-Json
    return @{
        IsMerged = $pr.state -eq "MERGED"
        MergeCommit = $pr.mergeCommit.oid
        State = $pr.state
    }
}

function Test-MergePrMergeable {
    <#
    .SYNOPSIS
        Tests if a PR is in a mergeable state.
    .PARAMETER PrNumber
        The PR number.
    .PARAMETER Repository
        Optional repository (owner/repo).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$PrNumber,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    $prArgs = @("pr", "view", $PrNumber, "--json", "state,isDraft,mergeable,reviewDecision,statusCheckRollup")
    if ($Repository) {
        $prArgs += "--repo"
        $prArgs += $Repository
    }

    $result = & gh @prArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        return @{
            IsMergeable = $false
            Error = "Failed to get PR status"
        }
    }

    $pr = $result | ConvertFrom-Json

    # Check basic conditions
    if ($pr.state -ne "OPEN") {
        return @{
            IsMergeable = $false
            Reason = "PR is not open (state: $($pr.state))"
        }
    }

    if ($pr.isDraft) {
        return @{
            IsMergeable = $false
            Reason = "PR is a draft"
        }
    }

    # Check mergeable status
    if ($pr.mergeable -ne "MERGEABLE") {
        return @{
            IsMergeable = $false
            Reason = "PR is not mergeable (status: $($pr.mergeable))"
        }
    }

    # Check review decision
    if ($pr.reviewDecision -eq "REVIEW_REQUIRED") {
        return @{
            IsMergeable = $false
            Reason = "Review required"
        }
    }

    # Check status checks
    $failedChecks = $pr.statusCheckRollup | Where-Object { $_.conclusion -eq "FAILURE" }
    if ($failedChecks) {
        return @{
            IsMergeable = $false
            Reason = "Required checks failed: $($failedChecks.name -join ', ')"
        }
    }

    return @{
        IsMergeable = $true
        ReviewDecision = $pr.reviewDecision
    }
}

function Get-MergePrInfo {
    <#
    .SYNOPSIS
        Gets detailed PR information.
    .PARAMETER PrNumber
        The PR number.
    .PARAMETER Repository
        Optional repository (owner/repo).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$PrNumber,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    $prArgs = @("pr", "view", $PrNumber, "--json", "number,title,body,headRefName,baseRefName,state,isDraft,mergeable,reviewDecision,commits")
    if ($Repository) {
        $prArgs += "--repo"
        $prArgs += $Repository
    }

    $result = & gh @prArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return $result | ConvertFrom-Json
}

function Get-MergeCurrentCommit {
    <#
    .SYNOPSIS
        Gets the current commit SHA in a Worktree.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $originalLocation = Get-Location
    try {
        Set-Location $WorktreePath
        return git rev-parse HEAD
    }
    finally {
        Set-Location $originalLocation
    }
}

function Remove-MergeWorktree {
    <#
    .SYNOPSIS
        Removes a Worktree and its associated Branch.
    .PARAMETER WorktreePath
        Path to the Worktree to remove.
    .PARAMETER BranchName
        Name of the Branch to delete.
    .PARAMETER Force
        Force removal even if dirty.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $true)]
        [string]$BranchName,

        [Parameter(Mandatory = $false)]
        [switch]$Force
    )

    Write-Host "Removing Worktree: $WorktreePath" -ForegroundColor Yellow

    # Check if Worktree exists
    if (-not (Test-Path $WorktreePath)) {
        Write-Warning "Worktree does not exist: $WorktreePath"
        return
    }

    # Remove Worktree
    $removeArgs = @("worktree", "remove", $WorktreePath)
    if ($Force) {
        $removeArgs += "--force"
    }

    & git @removeArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to remove Worktree: $WorktreePath"
    }

    # Delete local Branch
    Write-Host "Deleting local Branch: $BranchName" -ForegroundColor Yellow
    git branch -D $BranchName 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to delete local Branch: $BranchName"
    }

    # Delete remote Branch
    Write-Host "Deleting remote Branch: $BranchName" -ForegroundColor Yellow
    git push origin --delete $BranchName 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to delete remote Branch: $BranchName (may not exist)"
    }

    # Prune stale Worktree references
    git worktree prune 2>$null
}

Export-ModuleMember -Function @(
    'Get-MergeMainHead',
    'Invoke-MergeRebase',
    'Stop-MergeRebase',
    'Invoke-NormalMerge',
    'Test-MergePrMerged',
    'Test-MergePrMergeable',
    'Get-MergePrInfo',
    'Get-MergeCurrentCommit',
    'Remove-MergeWorktree'
)
