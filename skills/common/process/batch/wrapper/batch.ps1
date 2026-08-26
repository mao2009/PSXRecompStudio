#Requires -Version 7.0

<#
.SYNOPSIS
    Backward-compatible wrapper for Batch Skill.

.DESCRIPTION
    Provides a simple interface for the Batch Skill, maintaining backward
    compatibility with the previous PowerShell-only implementation.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("orchestrate", "subagent", "test")]
    [string]$Command,

    [Parameter(Mandatory = $false)]
    [int]$IssueNumber,

    [Parameter(Mandatory = $false)]
    [string]$Description,

    [Parameter(Mandatory = $false)]
    [string]$WorktreePath,

    [Parameter(Mandatory = $false)]
    [string]$BranchName,

    [Parameter(Mandatory = $false)]
    [string]$BaseRef = "main",

    [Parameter(Mandatory = $false)]
    [string]$WorktreeRoot = "../worktrees",

    [Parameter(Mandatory = $false)]
    [string]$StateFile
)

# Get the script directory
$wrapperPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimePath = Join-Path $wrapperPath ".." "runtime" "powershell"

switch ($Command) {
    "orchestrate" {
        if (-not $IssueNumber -or -not $Description) {
            Write-Host "Usage: batch.ps1 orchestrate -IssueNumber <number> -Description <description>" -ForegroundColor Red
            exit 1
        }

        $scriptArgs = @{
            IssueNumber = $IssueNumber
            Description = $Description
            BaseRef = $BaseRef
            WorktreeRoot = $WorktreeRoot
        }

        if ($StateFile) {
            $scriptArgs.StateFile = $StateFile
        }

        & (Join-Path $runtimePath "Scripts" "Invoke-BatchOrchestrator.ps1") @scriptArgs
    }

    "subagent" {
        if (-not $IssueNumber -or -not $WorktreePath -or -not $BranchName) {
            Write-Host "Usage: batch.ps1 subagent -IssueNumber <number> -WorktreePath <path> -BranchName <name>" -ForegroundColor Red
            exit 1
        }

        $scriptArgs = @{
            IssueNumber = $IssueNumber
            WorktreePath = $WorktreePath
            BranchName = $BranchName
        }

        if ($StateFile) {
            $scriptArgs.StateFile = $StateFile
        }

        & (Join-Path $runtimePath "Scripts" "Invoke-BatchSubAgent.ps1") @scriptArgs
    }

    "test" {
        & (Join-Path $runtimePath "Tests" "Test-BatchSkill.ps1")
    }

    default {
        Write-Host "Unknown command: $Command" -ForegroundColor Red
        Write-Host "Available commands: orchestrate, subagent, test" -ForegroundColor Yellow
        exit 1
    }
}
