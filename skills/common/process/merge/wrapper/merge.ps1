#Requires -Version 7.0

<#
.SYNOPSIS
    Wrapper for PR Merge Skill.

.DESCRIPTION
    Provides a simple interface for the PR Merge Skill, maintaining
    backward compatibility with the previous implementation.

.NOTES
    Version: 1.0.0
    Issue: #146
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("merge", "test")]
    [string]$Command,

    [Parameter(Mandatory = $false)]
    [int]$PrNumber,

    [Parameter(Mandatory = $false)]
    [int]$IssueNumber,

    [Parameter(Mandatory = $false)]
    [string]$WorktreePath,

    [Parameter(Mandatory = $false)]
    [string]$BranchName,

    [Parameter(Mandatory = $false)]
    [string]$Repository,

    [Parameter(Mandatory = $false)]
    [string]$StateFile
)

# Get the script directory
$wrapperPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimePath = Join-Path $wrapperPath ".." "runtime" "powershell"

switch ($Command) {
    "merge" {
        if (-not $PrNumber) {
            Write-Host "Usage: merge.ps1 merge -PrNumber <number>" -ForegroundColor Red
            Write-Host "Optional: -IssueNumber <number> -WorktreePath <path> -BranchName <name> -Repository <owner/repo>" -ForegroundColor Yellow
            exit 1
        }

        $scriptArgs = @{
            PrNumber = $PrNumber
        }

        if ($IssueNumber) {
            $scriptArgs.IssueNumber = $IssueNumber
        }
        if ($WorktreePath) {
            $scriptArgs.WorktreePath = $WorktreePath
        }
        if ($BranchName) {
            $scriptArgs.BranchName = $BranchName
        }
        if ($Repository) {
            $scriptArgs.Repository = $Repository
        }
        if ($StateFile) {
            $scriptArgs.StateFile = $StateFile
        }

        & (Join-Path $runtimePath "Scripts" "Invoke-MergeOrchestrator.ps1") @scriptArgs
    }

    "test" {
        & (Join-Path $runtimePath "Tests" "Test-MergeSkill.ps1")
    }

    default {
        Write-Host "Unknown command: $Command" -ForegroundColor Red
        Write-Host "Available commands: merge, test" -ForegroundColor Yellow
        exit 1
    }
}
