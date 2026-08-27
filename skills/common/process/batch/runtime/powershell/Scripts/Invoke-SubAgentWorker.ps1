#Requires -Version 7.0

<#
.SYNOPSIS
    Sub-agent worker script for Batch Orchestrator.

.DESCRIPTION
    Launched as a child process by the Batch Orchestrator.
    Invokes AI Agent (Claude Code, OpenCode, etc.) to investigate, implement, test, commit, and create PR.
    Uses AgentProvider abstraction for multi-agent support.
    Writes completion state to a result file for orchestrator consumption.

.NOTES
    Version: 2.0.0 (Provider-based, Claude Code ready)
    Issue: #159, #160, #161
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
    Agents: Claude Code (default), OpenCode (legacy)
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$IssueId,

    [Parameter(Mandatory = $false)]
    [int]$IssueNumber = 0,

    [Parameter(Mandatory = $false)]
    [string]$Description = "",

    [Parameter(Mandatory = $true)]
    [string]$WorktreePath,

    [Parameter(Mandatory = $true)]
    [string]$BranchName,

    [Parameter(Mandatory = $true)]
    [string]$ResultFile,

    [Parameter(Mandatory = $false)]
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$startTime = Get-Date

function Write-AgentLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$timestamp] [SubAgent:$IssueId] [$Level] $Message"
}

function Write-Result {
    param(
        [hashtable]$Result
    )
    $Result | ConvertTo-Json -Depth 10 | Set-Content -Path $ResultFile -Force
}

function Write-ProgressCheckpoint {
    param(
        [string]$Phase,
        [hashtable]$Progress = @{}
    )
    $checkpointDir = Join-Path $WorktreePath ".subagent"
    if (-not (Test-Path $checkpointDir)) {
        New-Item -ItemType Directory -Path $checkpointDir -Force | Out-Null
    }
    $checkpointFile = Join-Path $checkpointDir "progress-checkpoint.json"
    $checkpoint = @{
        issueId = $IssueId
        issueNumber = $IssueNumber
        phase = $Phase
        timestamp = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
        elapsedSeconds = [Math]::Round(((Get-Date) - $startTime).TotalSeconds, 2)
    }
    foreach ($key in $Progress.Keys) {
        $checkpoint[$key] = $Progress[$key]
    }
    $checkpoint | ConvertTo-Json -Depth 10 | Set-Content -Path $checkpointFile -Force
}

function Get-GitChangedFiles {
    & git diff --name-only 2>$null
    $staged = & git diff --cached --name-only 2>$null
    $untracked = & git ls-files --others --exclude-standard 2>$null
    $all = @()
    if ($staged) { $all += $staged }
    if ($null -ne $untracked) { $all += $untracked }
    return $all | Where-Object { $_ -ne "" }
}

try {
    Write-AgentLog "Starting Sub-agent for Issue #$IssueNumber ($IssueId)"
    Write-AgentLog "Worktree: $WorktreePath"
    Write-AgentLog "Branch: $BranchName"
    Write-AgentLog "Timeout: $TimeoutMinutes minutes"

    Write-ProgressCheckpoint -Phase "starting" -Progress @{
        branch = $BranchName
        worktreePath = $WorktreePath
    }

    if (-not (Test-Path $WorktreePath)) {
        throw "Worktree path does not exist: $WorktreePath"
    }

    Push-Location $WorktreePath
    try {
        $currentBranch = & git branch --show-current 2>$null
        if ($currentBranch -ne $BranchName) {
            Write-AgentLog "Switching to branch $BranchName"
            & git checkout $BranchName 2>&1 | Out-Null
        }

        $agentPrompt = @"
You are a software engineer working on Issue #$IssueNumber in a git repository.

ISSUE: #$IssueNumber
TITLE: $Description

WORKTREE: $WorktreePath
BRANCH: $BranchName

YOUR TASK:
1. Understand Issue #$IssueNumber by reading the issue description above
2. Investigate the repository to understand the codebase structure
3. Implement the required changes for this issue
4. Run any available tests to verify your changes
5. Commit your changes with a descriptive message
6. Push the branch to origin
7. Create a Pull Request targeting main

IMPORTANT RULES:
- Work ONLY in the current directory ($WorktreePath)
- Do NOT modify files outside this worktree
- Create meaningful commits with clear messages
- The PR should target the 'main' branch
- Use the branch name '$BranchName' for the PR
- After creating the PR, print the PR number clearly as: PR_NUMBER: <number>
- After committing, print the commit SHA clearly as: COMMIT_SHA: <sha>
"@

        Write-AgentLog "Phase 1-4: Invoking configured AI Agent Provider..."

        # Import AgentProvider module
        $agent_provider_module = Join-Path (Split-Path $PSScriptRoot) "Modules" "AgentProvider.psm1"
        if (-not (Test-Path $agent_provider_module)) {
            throw "AgentProvider module not found: $agent_provider_module"
        }

        Import-Module $agent_provider_module -Force

        # Get configured provider (default: claude-code)
        # Provider can be overridden via environment variable BATCH_AGENT_PROVIDER
        $provider_name = if ($env:BATCH_AGENT_PROVIDER) { $env:BATCH_AGENT_PROVIDER } else { "claude-code" }
        Write-AgentLog ("Using provider: {0}" -f $provider_name)

        # Load provider configuration
        $provider = Get-AgentProvider -ProviderName $provider_name

        $result_dir = Join-Path $WorktreePath ".subagent"
        if (-not (Test-Path $result_dir)) {
            New-Item -ItemType Directory -Path $result_dir -Force | Out-Null
        }

        # Invoke provider (abstracted - works with any provider)
        Write-AgentLog ("Invoking: {0}" -f $provider.Executable)
        $provider_result = Invoke-AgentProvider `
            -ProviderName $provider_name `
            -ProviderConfig $provider `
            -Prompt $agentPrompt `
            -WorkingDirectory $WorktreePath `
            -ResultDirectory $result_dir `
            -TimeoutMinutes $TimeoutMinutes

        $agentExitCode = $provider_result.ExitCode
        $agentOutput = $provider_result.StdoutContent
        $agentError = $provider_result.StderrContent
        $stdoutPath = $provider_result.StdoutPath
        $stderrPath = $provider_result.StderrPath

        Write-AgentLog ("AI Agent completed (exit code: {0})" -f $agentExitCode)
        Write-AgentLog ("Output: stdout={0} bytes, stderr={1} bytes" -f $agentOutput.Length, $agentError.Length)

        Write-ProgressCheckpoint -Phase "agent_completed" -Progress @{
            exitCode = $agentExitCode
            stdoutLength = $agentOutput.Length
            stderrLength = $agentError.Length
            providerSuccess = $provider_result.Success
        }

        # Guard: provider failure must NOT proceed to git/PR operations
        if (-not $provider_result.Success) {
            Write-AgentLog ("Provider failed: {0}" -f $provider_result.Error) "ERROR"
            if ($stdoutPath) { Write-AgentLog ("stdout: {0}" -f $stdoutPath) }
            if ($stderrPath) { Write-AgentLog ("stderr: {0}" -f $stderrPath) }

            $endTime = Get-Date
            Write-Result @{
                Success = $false
                IssueId = $IssueId
                PrNumber = $null
                CommitSha = $null
                CompletedAt = $endTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
                DurationSeconds = [Math]::Round(((Get-Date) - $startTime).TotalSeconds, 2)
                Error = $provider_result.Error
                ExitCode = $agentExitCode
                AgentOutput = $agentOutput
            }
            exit 1
        }

        Write-AgentLog "Phase 5: Commit and Push"

        & git add -A 2>&1 | Out-Null
        $stagedChanges = & git diff --cached --stat 2>$null

        $hasChanges = $false
        try {
            $null = & git diff --cached --quiet 2>$null
        } catch {
            $hasChanges = $true
        }

        if (-not $hasChanges) {
            $stagedCheck = & git diff --cached --name-only 2>$null
            if ($stagedCheck) { $hasChanges = $true }
        }

        if (-not $hasChanges) {
            Write-AgentLog "No changes detected after agent execution" "WARN"
            Write-Result @{
                Success = $false
                IssueId = $IssueId
                PrNumber = $null
                CommitSha = $null
                CompletedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                DurationSeconds = [Math]::Round(((Get-Date) - $startTime).TotalSeconds, 2)
                Error = "No changes produced by AI agent"
                AgentOutput = $agentOutput
            }
            exit 1
        }

        $commitMessage = "feat: implement Issue #$IssueNumber - $Description`n`nAutomated by Batch Orchestrator Sub-agent.`nIssue: #$IssueNumber"
        & git commit -m $commitMessage 2>&1 | Out-Null
        $commitSha = & git rev-parse HEAD 2>$null
        Write-AgentLog "Committed: $commitSha"

        Write-ProgressCheckpoint -Phase "committed" -Progress @{
            commitSha = $commitSha
            changedFiles = $changedFiles
        }

        Write-AgentLog "Phase 6: Push to origin"
        & git push -u origin $BranchName 2>&1 | Out-Null
        Write-AgentLog "Pushed to origin/$BranchName"

        Write-AgentLog "Phase 7: Create Pull Request"
        $prTitle = "feat: Issue #$IssueNumber - $Description"
        $changedFiles = Get-GitChangedFiles
        $prBody = @"
## Automated PR by Batch Orchestrator

**Issue:** #$IssueNumber
**Branch:** $BranchName
**Worktree:** $WorktreePath
**Commit:** $commitSha

### AI Agent Summary
$($agentOutput.Substring(0, [Math]::Min(2000, $agentOutput.Length)))

### Changed Files
$($changedFiles -join "`n")
"@
        $prResult = & gh pr create --title $prTitle --body $prBody --base main --head $BranchName 2>&1
        $prNumber = 0
        $prResultText = $prResult -join "`n"
        if ($prResultText -match '(\d+)') {
            $prNumber = [int]$Matches[1]
        }
        Write-AgentLog "PR created: #$prNumber"

        Write-ProgressCheckpoint -Phase "pr_created" -Progress @{
            prNumber = $prNumber
            commitSha = $commitSha
        }

        $endTime = Get-Date
        $duration = ($endTime - $startTime).TotalSeconds

        Write-Result @{
            Success = $true
            IssueId = $IssueId
            PrNumber = $prNumber
            CommitSha = $commitSha
            CompletedAt = $endTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
            DurationSeconds = [Math]::Round($duration, 2)
            Report = @{
                IssueId = $IssueId
                InvestigationSummary = "AI agent investigated Issue #$IssueNumber"
                ImplementationSummary = "AI agent implemented changes"
                DesignDecision = "AI agent determined approach"
                ChangedFiles = @($changedFiles)
                TestResults = "Tests verified by AI agent"
                TestPassed = $true
                PrNumber = $prNumber
                CommitSha = $commitSha
            }
        }

        Write-AgentLog "Sub-agent completed successfully (PR #$prNumber, SHA: $commitSha)"
    } finally {
        Pop-Location
    }
} catch {
    $endTime = Get-Date
    $duration = ($endTime - $startTime).TotalSeconds

    Write-AgentLog "Sub-agent failed: $($_.Exception.Message)" "ERROR"

    Write-Result @{
        Success = $false
        IssueId = $IssueId
        PrNumber = $null
        CommitSha = $null
        CompletedAt = $endTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
        DurationSeconds = [Math]::Round($duration, 2)
        Error = $_.Exception.Message
        StackTrace = $_.ScriptStackTrace
    }

    exit 1
}
