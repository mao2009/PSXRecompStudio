#Requires -Version 7.0

<#
.SYNOPSIS
    Git utilities for Batch Orchestrator.

.DESCRIPTION
    Provides Worktree creation, Branch management, environment initialization,
    and cleanup operations for parallel Issue execution.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-BatchWorktree {
    <#
    .SYNOPSIS
        Creates a new Worktree and Branch for an Issue.
    .PARAMETER IssueNumber
        The Issue number.
    .PARAMETER Description
        Short human-readable description.
    .PARAMETER WorktreeRoot
        Root directory for Worktrees.
    .PARAMETER Repository
        Optional repository path.
    .OUTPUTS
        Hashtable with WorktreePath, BranchName, Success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$IssueNumber,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $false)]
        [string]$WorktreeRoot = "../worktrees",

        [Parameter(Mandatory = $false)]
        [string]$Repository = "."
    )

    $normalizedDesc = $Description.ToLower() -replace '[^a-z0-9]+', '-' -replace '^-|-$', ''
    $branchName = "issue/$IssueNumber-$normalizedDesc"
    $worktreeDir = Join-Path $WorktreeRoot "$IssueNumber-$normalizedDesc"
    $fullWorktreePath = Join-Path $Repository $worktreeDir

    if (Test-Path $fullWorktreePath) {
        Write-Warning "Worktree already exists: $fullWorktreePath"
        return @{
            Success = $false
            Reason = "Worktree already exists"
            WorktreePath = $fullWorktreePath
            BranchName = $branchName
        }
    }

    $parentDir = Split-Path -Parent $fullWorktreePath
    if (-not (Test-Path $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }

    git worktree add -b $branchName $fullWorktreePath 2>$null
    if ($LASTEXITCODE -ne 0) {
        return @{
            Success = $false
            Reason = "Failed to create worktree"
            WorktreePath = $fullWorktreePath
            BranchName = $branchName
        }
    }

    return @{
        Success = $true
        WorktreePath = $fullWorktreePath
        BranchName = $branchName
        IssueNumber = $IssueNumber
        Description = $Description
    }
}

function Test-BatchWorktreeCollision {
    <#
    .SYNOPSIS
        Checks for Worktree/Branch name collisions.
    .PARAMETER WorktreePath
        The Worktree path to check.
    .PARAMETER BranchName
        The Branch name to check.
    .OUTPUTS
        Hashtable with HasCollision (bool) and Conflicts.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $true)]
        [string]$BranchName
    )

    $conflicts = @()

    if (Test-Path $WorktreePath) {
        $conflicts += "Worktree path already exists: $WorktreePath"
    }

    $existingBranches = git branch --list $BranchName 2>$null
    if ($existingBranches) {
        $conflicts += "Branch already exists: $BranchName"
    }

    $existingWorktrees = git worktree list 2>$null | Select-String $BranchName
    if ($existingWorktrees) {
        $conflicts += "Branch used in existing worktree: $BranchName"
    }

    return @{
        HasCollision = $conflicts.Count -gt 0
        Conflicts = $conflicts
    }
}

function Initialize-BatchWorktreeEnvironment {
    <#
    .SYNOPSIS
        Initializes environment files in a Worktree.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .PARAMETER EnvFiles
        Array of .env file names to create.
    .PARAMETER TemplateDir
        Optional directory to copy templates from.
    .DESCRIPTION
        Safely initializes only specified files. Never copies secrets.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $false)]
        [string[]]$EnvFiles = @(".env"),

        [Parameter(Mandatory = $false)]
        [string]$TemplateDir
    )

    if (-not (Test-Path $WorktreePath)) {
        throw "Worktree path does not exist: $WorktreePath"
    }

    foreach ($envFile in $EnvFiles) {
        $targetPath = Join-Path $WorktreePath $envFile

        if (Test-Path $targetPath) {
            continue
        }

        if ($TemplateDir) {
            $templatePath = Join-Path $TemplateDir $envFile
            if (Test-Path $templatePath) {
                Copy-Item -Path $templatePath -Destination $targetPath -Force
                continue
            }
        }

        if ($envFile -eq ".env" -or $envFile -eq ".env.local") {
            "# Environment variables for this worktree`n# Do not commit secrets`n" | Set-Content -Path $targetPath
        }
    }
}

function Remove-BatchWorktree {
    <#
    .SYNOPSIS
        Removes a Worktree and its associated Branch.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .PARAMETER BranchName
        Name of the Branch.
    .PARAMETER Force
        Force removal even if dirty.
    .PARAMETER DeleteRemote
        Also delete remote Branch.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $true)]
        [string]$BranchName,

        [Parameter(Mandatory = $false)]
        [switch]$Force,

        [Parameter(Mandatory = $false)]
        [switch]$DeleteRemote = $true
    )

    Write-Host "Removing Worktree: $WorktreePath" -ForegroundColor Yellow

    if (-not (Test-Path $WorktreePath)) {
        Write-Warning "Worktree does not exist: $WorktreePath"
    } else {
        $removeArgs = @("worktree", "remove", $WorktreePath)
        if ($Force) {
            $removeArgs += "--force"
        }
        & git @removeArgs 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to remove Worktree: $WorktreePath"
        }
    }

    Write-Host "Deleting local Branch: $BranchName" -ForegroundColor Yellow
    git branch -D $BranchName 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to delete local Branch: $BranchName"
    }

    if ($DeleteRemote) {
        Write-Host "Deleting remote Branch: $BranchName" -ForegroundColor Yellow
        git push origin --delete $BranchName 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to delete remote Branch: $BranchName (may not exist)"
        }
    }

    git worktree prune 2>$null
}

function Get-BatchWorktreeCommit {
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

function Test-BatchWorktreeExists {
    <#
    .SYNOPSIS
        Tests if a Worktree exists and is valid.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    if (-not (Test-Path $WorktreePath)) {
        return $false
    }

    $originalLocation = Get-Location
    try {
        Set-Location $WorktreePath
        git rev-parse --git-dir 2>$null
        return $LASTEXITCODE -eq 0
    }
    finally {
        Set-Location $originalLocation
    }
}

Export-ModuleMember -Function @(
    'New-BatchWorktree',
    'Test-BatchWorktreeCollision',
    'Initialize-BatchWorktreeEnvironment',
    'Remove-BatchWorktree',
    'Get-BatchWorktreeCommit',
    'Test-BatchWorktreeExists'
)
