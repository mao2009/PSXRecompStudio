#Requires -Version 7.0

<#
.SYNOPSIS
    Wrapper for Batch Orchestrator Skill.

.DESCRIPTION
    Provides a simple interface for the Batch Orchestrator,
    supporting parallel Issue execution with dependency scheduling.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("run", "test", "status", "resume")]
    [string]$Command,

    [Parameter(Mandatory = $false)]
    [string]$BatchId,

    [Parameter(Mandatory = $false)]
    [string]$IssuesFile,

    [Parameter(Mandatory = $false)]
    [string[]]$IssueIds,

    [Parameter(Mandatory = $false)]
    [int]$MaxConcurrency = 3,

    [Parameter(Mandatory = $false)]
    [int]$MaxRetries = 3,

    [Parameter(Mandatory = $false)]
    [string]$Repository,

    [Parameter(Mandatory = $false)]
    [string]$WorktreeRoot,

    [Parameter(Mandatory = $false)]
    [string]$StateDir
)

$wrapperPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimePath = Join-Path $wrapperPath ".." "runtime" "powershell"

switch ($Command) {
    "run" {
        if (-not $BatchId) {
            $BatchId = "batch-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        }

        $scriptArgs = @{
            BatchId = $BatchId
            MaxConcurrency = $MaxConcurrency
            MaxRetries = $MaxRetries
        }

        if ($IssuesFile) { $scriptArgs.IssuesFile = $IssuesFile }
        if ($IssueIds) { $scriptArgs.IssueIds = $IssueIds }
        if ($Repository) { $scriptArgs.Repository = $Repository }
        if ($WorktreeRoot) { $scriptArgs.WorktreeRoot = $WorktreeRoot }
        if ($StateDir) { $scriptArgs.StateDir = $StateDir }

        & (Join-Path $runtimePath "Scripts" "Invoke-BatchOrchestrator.ps1") @scriptArgs
    }

    "resume" {
        if (-not $BatchId) {
            Write-Host "Usage: batch.ps1 resume -BatchId <id>" -ForegroundColor Red
            exit 1
        }

        $scriptArgs = @{
            BatchId = $BatchId
            MaxConcurrency = $MaxConcurrency
            MaxRetries = $MaxRetries
        }

        if ($Repository) { $scriptArgs.Repository = $Repository }
        if ($WorktreeRoot) { $scriptArgs.WorktreeRoot = $WorktreeRoot }
        if ($StateDir) { $scriptArgs.StateDir = $StateDir }

        & (Join-Path $runtimePath "Scripts" "Invoke-BatchOrchestrator.ps1") @scriptArgs
    }

    "status" {
        if (-not $BatchId) {
            Write-Host "Usage: batch.ps1 status -BatchId <id>" -ForegroundColor Red
            exit 1
        }

        $stateFile = ".batch-state-$BatchId.json"
        if (Test-Path $stateFile) {
            $state = Get-Content $stateFile | ConvertFrom-Json
            Write-Host "Batch: $($state.BatchId)" -ForegroundColor Cyan
            Write-Host "State: $($state.State)" -ForegroundColor Yellow
            Write-Host "Issues: $($state.IssueCount)" -ForegroundColor Gray
            Write-Host "Completed: $($state.CompletedCount)" -ForegroundColor Green
            Write-Host "Failed: $($state.FailedCount)" -ForegroundColor Red
            Write-Host "Blocked: $($state.BlockedCount)" -ForegroundColor Yellow
            Write-Host "Created: $($state.CreatedAt)" -ForegroundColor Gray
            Write-Host "Updated: $($state.UpdatedAt)" -ForegroundColor Gray
        } else {
            Write-Host "No state found for batch: $BatchId" -ForegroundColor Red
        }
    }

    "test" {
        & (Join-Path $runtimePath "Tests" "Test-BatchSkill.ps1")
    }

    default {
        Write-Host "Unknown command: $Command" -ForegroundColor Red
        Write-Host "Available commands: run, resume, status, test" -ForegroundColor Yellow
        exit 1
    }
}
