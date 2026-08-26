#Requires -Version 7.0

<#
.SYNOPSIS
    Git Worktree and Branch management utilities for Batch Skill.

.DESCRIPTION
    Provides functions for creating, managing, and cleaning up Git Worktrees
    and Branches with meaningful naming conventions.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-BatchWorktree {
    <#
    .SYNOPSIS
        Creates a new Worktree with meaningful naming.
    .PARAMETER IssueNumber
        The Issue number.
    .PARAMETER Description
        Short description of the Issue.
    .PARAMETER BaseRef
        The base reference (branch/tag/commit) to create from.
    .PARAMETER WorktreeRoot
        Root directory for Worktrees.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$IssueNumber,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $false)]
        [string]$BaseRef = "main",

        [Parameter(Mandatory = $false)]
        [string]$WorktreeRoot = "../worktrees"
    )

    $branchName = "issue/$IssueNumber-$Description"
    $worktreePath = Join-Path $WorktreeRoot "$IssueNumber-$Description"

    # Validate naming
    if ($branchName -notmatch '^[a-z0-9\-/]+$') {
        throw "Invalid branch name: $branchName. Must contain only lowercase letters, numbers, hyphens, and slashes."
    }

    # Check if Worktree already exists
    if (Test-Path $worktreePath) {
        throw "Worktree already exists: $worktreePath"
    }

    # Create Worktree directory if it doesn't exist
    $worktreeRootExpanded = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorktreeRoot)
    if (-not (Test-Path $worktreeRootExpanded)) {
        New-Item -ItemType Directory -Path $worktreeRootExpanded -Force | Out-Null
    }

    # Create Worktree
    Write-Host "Creating Worktree: $worktreePath" -ForegroundColor Cyan
    Write-Host "  Branch: $branchName" -ForegroundColor Gray
    Write-Host "  Base: $BaseRef" -ForegroundColor Gray

    git worktree add -b $branchName $worktreePath $BaseRef
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create Worktree: $worktreePath"
    }

    return @{
        BranchName = $branchName
        WorktreePath = $worktreePath
        IssueNumber = $IssueNumber
        Description = $Description
    }
}

function Remove-BatchWorktree {
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

    & git $removeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to remove Worktree: $WorktreePath"
    }

    # Delete local Branch
    Write-Host "Deleting local Branch: $BranchName" -ForegroundColor Yellow
    git branch -D $BranchName
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
    git worktree prune
}

function Get-BatchWorktree {
    <#
    .SYNOPSIS
        Gets information about a Worktree.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    if (-not (Test-Path $WorktreePath)) {
        return $null
    }

    $originalLocation = Get-Location
    try {
        Set-Location $WorktreePath

        $branch = git branch --show-current
        $commit = git rev-parse HEAD
        $status = git status --short

        return @{
            Path = $WorktreePath
            Branch = $branch
            Commit = $commit
            IsDirty = $status.Count -gt 0
            Status = $status
        }
    }
    finally {
        Set-Location $originalLocation
    }
}

function Test-BatchWorktree {
    <#
    .SYNOPSIS
        Tests if a Worktree is in a valid state.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $info = Get-BatchWorktree -WorktreePath $WorktreePath
    if ($null -eq $info) {
        return $false
    }

    return -not $info.IsDirty
}

function Get-BatchMainHead {
    <#
    .SYNOPSIS
        Gets the latest main HEAD commit SHA.
    #>
    [CmdletBinding()]
    param()

    git fetch origin main
    return git rev-parse origin/main
}

function Invoke-BatchRebase {
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
        git fetch origin main

        # Attempt rebase
        Write-Host "Rebasing onto origin/main..." -ForegroundColor Cyan
        git rebase origin/main 2>&1

        if ($LASTEXITCODE -eq 0) {
            return @{
                Success = $true
                HasConflicts = $false
                Message = "Rebase succeeded"
            }
        }

        # Check for conflicts
        $conflictFiles = git diff --name-only --diff-filter=U
        if ($conflictFiles.Count -gt 0) {
            return @{
                Success = $false
                HasConflicts = $true
                ConflictFiles = $conflictFiles
                Message = "Rebase failed with conflicts"
            }
        }

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

function Stop-BatchRebase {
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
        git rebase --abort
    }
    finally {
        Set-Location $originalLocation
    }
}

function Confirm-BatchMerge {
    <#
    .SYNOPSIS
        Merges the current Branch into main.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .PARAMETER BranchName
        Name of the Branch to merge.
    .OUTPUTS
        Hashtable with success status.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $true)]
        [string]$BranchName
    )

    $originalLocation = Get-Location
    try {
        # Checkout main
        Set-Location (Get-Location).Path
        git checkout main

        # Merge Branch
        Write-Host "Merging Branch: $BranchName" -ForegroundColor Cyan
        git merge $BranchName --no-ff -m "Merge $BranchName into main"

        if ($LASTEXITCODE -eq 0) {
            return @{
                Success = $true
                Message = "Merge succeeded"
            }
        }

        return @{
            Success = $false
            Message = "Merge failed"
        }
    }
    finally {
        Set-Location $originalLocation
    }
}

function Test-BatchPrMerged {
    <#
    .SYNOPSIS
        Tests if a PR has been merged.
    .PARAMETER PrNumber
        The PR number.
    .PARAMETER Repository
        The repository (owner/repo).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$PrNumber,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    $prArgs = @("pr", "view", $PrNumber, "--json", "state")
    if ($Repository) {
        $prArgs += "--repo"
        $prArgs += $Repository
    }

    $result = & gh $prArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    $pr = $result | ConvertFrom-Json
    return $pr.state -eq "MERGED"
}

Export-ModuleMember -Function @(
    'New-BatchWorktree',
    'Remove-BatchWorktree',
    'Get-BatchWorktree',
    'Test-BatchWorktree',
    'Get-BatchMainHead',
    'Invoke-BatchRebase',
    'Stop-BatchRebase',
    'Confirm-BatchMerge',
    'Test-BatchPrMerged'
)
