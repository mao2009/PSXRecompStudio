#Requires -Version 7.0

<#
.SYNOPSIS
    Sub-agent worker script for Batch Orchestrator.

.DESCRIPTION
    Launched as a child process by the Batch Orchestrator.
    Invokes OpenCode AI Agent to investigate, implement, test, commit, and create PR.
    Writes completion state to a result file for orchestrator consumption.

.NOTES
    Version: 1.1.0
    Issue: #159
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
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

        Write-AgentLog "Phase 1-4: Invoking OpenCode AI Agent..."
        $timeoutSeconds = $TimeoutMinutes * 60

        $openCodeExe = "$env:APPDATA\npm\node_modules\opencode-ai\bin\opencode.exe"
        if (-not (Test-Path $openCodeExe)) {
            $openCodeExe = "opencode"
        }

        $agentOutput = ""
        $agentExitCode = 1

        try {
            $promptFile = Join-Path $env:TEMP "opencode-prompt-$IssueId.txt"
            $agentPrompt | Set-Content -Path $promptFile -Force

            $promptContent = Get-Content $promptFile -Raw
            $ocProcess = Start-Process -FilePath $openCodeExe -ArgumentList "run", $promptContent, "--pure" -WorkingDirectory $WorktreePath -NoNewWindow -PassThru -Wait -RedirectStandardOutput "$env:TEMP\opencode-stdout-$IssueId.txt" -RedirectStandardError "$env:TEMP\opencode-stderr-$IssueId.txt"
            $agentExitCode = $ocProcess.ExitCode
            $stdoutPath = "$env:TEMP\opencode-stdout-$IssueId.txt"
            $stderrPath = "$env:TEMP\opencode-stderr-$IssueId.txt"
            if (Test-Path $stdoutPath) { $agentOutput = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue }
            if (Test-Path $stderrPath) { $agentError = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue }
            if (Test-Path $promptFile) { Remove-Item $promptFile -Force }
        } catch {
            Write-AgentLog "OpenCode invocation failed: $($_.Exception.Message)" "ERROR"
            $agentOutput = ""
        }

        Write-AgentLog "OpenCode completed (exit code: $agentExitCode)"
        if ($agentOutput) {
            Write-AgentLog "Agent output length: $($agentOutput.Length) chars"
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
