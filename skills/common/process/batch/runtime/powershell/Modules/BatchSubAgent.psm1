#Requires -Version 7.0

<#
.SYNOPSIS
    Sub-agent lifecycle management for Batch Orchestrator.

.DESCRIPTION
    Handles Sub-agent launching, retry with backoff, failure isolation,
    and structured reporting. Orchestrator never substitutes implementation.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-SubAgentConfig {
    <#
    .SYNOPSIS
        Creates Sub-agent configuration.
    .PARAMETER MaxRetries
        Maximum retry attempts.
    .PARAMETER TimeoutMinutes
        Sub-agent timeout in minutes.
    .PARAMETER BackoffBaseSeconds
        Base backoff duration in seconds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [int]$MaxRetries = 3,

        [Parameter(Mandatory = $false)]
        [int]$TimeoutMinutes = 30,

        [Parameter(Mandatory = $false)]
        [int]$BackoffBaseSeconds = 5
    )

    return @{
        MaxRetries = $MaxRetries
        TimeoutMinutes = $TimeoutMinutes
        BackoffBaseSeconds = $BackoffBaseSeconds
    }
}

function New-SubAgentState {
    <#
    .SYNOPSIS
        Creates initial Sub-agent state for an issue.
    .PARAMETER IssueId
        The issue identifier.
    .PARAMETER Config
        Sub-agent configuration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $true)]
        [hashtable]$Config
    )

    return @{
        IssueId = $IssueId
        State = "SUBAGENT_STARTING"
        RetryCount = 0
        MaxRetries = $Config.MaxRetries
        ProcessId = $null
        StartedAt = $null
        CompletedAt = $null
        LastError = $null
        Report = $null
        PrNumber = $null
        CommitSha = $null
        WorktreePath = $null
        BranchName = $null
        BackoffSeconds = $Config.BackoffBaseSeconds
    }
}

function Test-SubAgentRetryable {
    <#
    .SYNOPSIS
        Tests if a Sub-agent failure is retryable.
    .PARAMETER SubAgentState
        The Sub-agent state.
    .PARAMETER ErrorCategory
        The error category.
    .OUTPUTS
        Hashtable with Retryable (bool) and Reason.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SubAgentState,

        [Parameter(Mandatory = $false)]
        [string]$ErrorCategory = "transient"
    )

    $retryableCategories = @("api_error", "timeout", "connection_failure", "transient")
    $nonRetryableCategories = @("code_error", "test_failure", "architecture_violation", "dependency_conflict")

    if ($ErrorCategory -in $nonRetryableCategories) {
        return @{
            Retryable = $false
            Reason = "Non-retryable error category: $ErrorCategory"
        }
    }

    if ($SubAgentState.RetryCount -ge $SubAgentState.MaxRetries) {
        return @{
            Retryable = $false
            Reason = "Retry limit reached ($($SubAgentState.MaxRetries))"
        }
    }

    if ($ErrorCategory -in $retryableCategories) {
        return @{
            Retryable = $true
            Reason = "Retryable error: $ErrorCategory (attempt $($SubAgentState.RetryCount + 1)/$($SubAgentState.MaxRetries))"
        }
    }

    return @{
        Retryable = $true
        Reason = "Unknown category treated as retryable (attempt $($SubAgentState.RetryCount + 1)/$($SubAgentState.MaxRetries))"
    }
}

function Get-SubAgentBackoffDuration {
    <#
    .SYNOPSIS
        Calculates exponential backoff duration with max cap enforcement.
    .PARAMETER SubAgentState
        The Sub-agent state.
    .PARAMETER MaxBackoffSeconds
        Maximum backoff duration in seconds (default: 120).
    .OUTPUTS
        Duration in seconds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SubAgentState,

        [Parameter(Mandatory = $false)]
        [int]$MaxBackoffSeconds = 120
    )

    $exponential = $SubAgentState.BackoffSeconds * [Math]::Pow(2, $SubAgentState.RetryCount)
    $jitter = Get-Random -Minimum 0 -Maximum ([Math]::Max(1, [int]($exponential * 0.1)))
    $duration = [Math]::Ceiling($exponential + $jitter)
    return [Math]::Min($duration, $MaxBackoffSeconds)
}

function Invoke-SubAgentRetry {
    <#
    .SYNOPSIS
        Prepares Sub-agent state for retry.
    .PARAMETER SubAgentState
        The Sub-agent state.
    .PARAMETER ErrorCategory
        The error category.
    .OUTPUTS
        Updated Sub-agent state.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SubAgentState,

        [Parameter(Mandatory = $false)]
        [string]$ErrorCategory = "transient"
    )

    $retryCheck = Test-SubAgentRetryable -SubAgentState $SubAgentState -ErrorCategory $ErrorCategory
    if (-not $retryCheck.Retryable) {
        $SubAgentState.State = "SUBAGENT_FAILED"
        $SubAgentState.LastError = $retryCheck.Reason
        return $SubAgentState
    }

    $SubAgentState.State = "SUBAGENT_RETRYING"
    $SubAgentState.RetryCount++
    $SubAgentState.ProcessId = $null
    $SubAgentState.LastError = "Retry #$($SubAgentState.RetryCount) after $ErrorCategory"

    $backoff = Get-SubAgentBackoffDuration -SubAgentState $SubAgentState
    Write-Host "Waiting $($backoff)s before retry..." -ForegroundColor Yellow
    Start-Sleep -Seconds $backoff

    $SubAgentState.State = "SUBAGENT_STARTING"
    return $SubAgentState
}

function New-SubAgentReport {
    <#
    .SYNOPSIS
        Creates a structured Sub-agent report template.
    .PARAMETER IssueId
        The issue identifier.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId
    )

    return @{
        IssueId = $IssueId
        InvestigatedAt = $null
        InvestigationSummary = $null
        ImplementedAt = $null
        ImplementationSummary = $null
        DesignDecision = $null
        ChangedFiles = @()
        TestResults = $null
        TestPassed = $false
        RemainingIssues = @()
        PrNumber = $null
        PrUrl = $null
        CommitSha = $null
        ReportedAt = $null
    }
}

function Test-SubAgentReportComplete {
    <#
    .SYNOPSIS
        Validates that a Sub-agent report is complete.
    .PARAMETER Report
        The Sub-agent report.
    .OUTPUTS
        Hashtable with IsComplete (bool) and MissingFields.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Report
    )

    $requiredFields = @(
        "InvestigationSummary",
        "ImplementationSummary",
        "DesignDecision",
        "ChangedFiles",
        "TestResults",
        "PrNumber",
        "CommitSha"
    )

    $missing = @()
    foreach ($field in $requiredFields) {
        if (-not $Report.ContainsKey($field) -or $null -eq $Report[$field] -or $Report[$field] -eq "") {
            $missing += $field
        }
    }

    if ($Report.ContainsKey("ChangedFiles") -and $Report.ChangedFiles.Count -eq 0) {
        $missing += "ChangedFiles (empty)"
    }

    return @{
        IsComplete = $missing.Count -eq 0
        MissingFields = $missing
    }
}

function Get-SubAgentFailureCategory {
    <#
    .SYNOPSIS
        Categorizes a Sub-agent failure.
    .PARAMETER ErrorMessage
        The error message.
    .OUTPUTS
        Error category string.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    $lowerError = $ErrorMessage.ToLower()

    if ($lowerError -match "timeout|timed out|deadline exceeded") {
        return "timeout"
    }
    if ($lowerError -match "connection|network|dns|socket") {
        return "connection_failure"
    }
    if ($lowerError -match "rate limit|429|too many requests") {
        return "api_error"
    }
    if ($lowerError -match "test.*fail|assertion|expected.*but got") {
        return "test_failure"
    }
    if ($lowerError -match "compil|syntax|type.*error|lint") {
        return "code_error"
    }
    if ($lowerError -match "depend|import|module.*not found") {
        return "dependency_conflict"
    }

    return "transient"
}

function Invoke-SubAgentLaunch {
    <#
    .SYNOPSIS
        Launches a Sub-agent as a child process in a specified worktree.
    .PARAMETER IssueId
        The issue identifier.
    .PARAMETER IssueNumber
        The GitHub issue number.
    .PARAMETER Description
        Issue description.
    .PARAMETER WorktreePath
        Path to the worktree.
    .PARAMETER BranchName
        Branch name.
    .PARAMETER SubAgentScript
        Path to the sub-agent worker script.
    .PARAMETER TimeoutMinutes
        Timeout in minutes.
    .OUTPUTS
        Hashtable with ProcessId and StartedAt.
    #>
    [CmdletBinding()]
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
        [string]$SubAgentScript,

        [Parameter(Mandatory = $false)]
        [int]$TimeoutMinutes = 30
    )

    $resultDir = Join-Path $WorktreePath ".subagent"
    if (-not (Test-Path $resultDir)) {
        New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    }
    $resultFile = Join-Path $resultDir "result.json"

    $scriptArgs = @(
        "-IssueId", $IssueId,
        "-IssueNumber", $IssueNumber.ToString(),
        "-Description", $Description,
        "-WorktreePath", $WorktreePath,
        "-BranchName", $BranchName,
        "-ResultFile", $resultFile,
        "-TimeoutMinutes", $TimeoutMinutes.ToString()
    )

    $logFile = Join-Path $resultDir "launch.log"
    $stdoutLogFile = Join-Path $resultDir "launch-stdout.log"
    $stderrLogFile = Join-Path $resultDir "launch-stderr.log"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "pwsh"
    $psi.Arguments = "-File `"$SubAgentScript`" $($scriptArgs -join ' ')"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $subagent_process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $subagent_process) {
        throw "Failed to start Sub-agent process for $IssueId"
    }

    $subagent_pid = $subagent_process.Id

    # Drain child pipes asynchronously to prevent buffer deadlocks.
    $stdout_task = $subagent_process.StandardOutput.ReadToEndAsync()
    $stderr_task = $subagent_process.StandardError.ReadToEndAsync()

    # Write launch info to log
    $started_at_utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
    @"
=== Sub-agent Process Launch Log ===
IssueId: $IssueId
ProcessId: $subagent_pid
StartedAt: $started_at_utc
Script: $SubAgentScript
WorktreePath: $WorktreePath
BranchName: $BranchName
ResultFile: $resultFile

=== Process Running ===
"@ | Set-Content -Path $logFile -Force

    return @{
        ProcessId       = $subagent_pid
        StartedAt       = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        LogFile         = $logFile
        Process         = $subagent_process
        StdoutTask      = $stdout_task
        StderrTask      = $stderr_task
        StdoutPath      = $stdoutLogFile
        StderrPath      = $stderrLogFile
    }
}

function Update-SubAgentLaunchLog {
    <#
    .SYNOPSIS
        Updates the launch log with process completion status and writes stream output to files.
    .PARAMETER LogFile
        Path to the launch log file.
    .PARAMETER ProcessId
        The process ID.
    .PARAMETER ExitCode
        The process exit code.
    .PARAMETER StdoutTask
        Async task draining stdout from the child process.
    .PARAMETER StderrTask
        Async task draining stderr from the child process.
    .PARAMETER StdoutPath
        Path to write stdout log.
    .PARAMETER StderrPath
        Path to write stderr log.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LogFile,

        [Parameter(Mandatory = $true)]
        [int]$ProcessId,

        [Parameter(Mandatory = $false)]
        [int]$ExitCode = -1,

        [Parameter(Mandatory = $false)]
        [System.Threading.Tasks.Task]$StdoutTask,

        [Parameter(Mandatory = $false)]
        [System.Threading.Tasks.Task]$StderrTask,

        [Parameter(Mandatory = $false)]
        [string]$StdoutPath,

        [Parameter(Mandatory = $false)]
        [string]$StderrPath
    )

    # Collect and write stream output
    $stdout = ""
    $stderr = ""
    try {
        if ($null -ne $StdoutTask -and $StdoutTask.Wait(10000)) {
            $stdout = $StdoutTask.Result
        }
    } catch { }
    try {
        if ($null -ne $StderrTask -and $StderrTask.Wait(10000)) {
            $stderr = $StderrTask.Result
        }
    } catch { }

    if ($StdoutPath -and $stdout) {
        $stdout | Set-Content -Path $StdoutPath -Force
    }
    if ($StderrPath -and $stderr) {
        $stderr | Set-Content -Path $StderrPath -Force
    }

    if (-not (Test-Path $LogFile)) { return }

    $completedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
    $status = if ($ExitCode -eq 0) { "COMPLETED" } else { "FAILED (exit code: $ExitCode)" }

    $update = @"

=== Process $status ===
CompletedAt: $completedAt
ExitCode: $ExitCode
"@

    Add-Content -Path $LogFile -Value $update -Force
}

function Get-SubAgentResult {
    <#
    .SYNOPSIS
        Reads Sub-agent completion result from the result file.
    .PARAMETER ResultFile
        Path to the result file.
    .OUTPUTS
        Hashtable with completion status, or $null if not yet complete.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResultFile
    )

    if (-not (Test-Path $ResultFile)) {
        return $null
    }

    try {
        $result = Get-Content $ResultFile -Raw | ConvertFrom-Json -AsHashtable
        return $result
    } catch {
        return $null
    }
}

function Test-SubAgentProcessRunning {
    <#
    .SYNOPSIS
        Tests if a Sub-agent process is still running.
    .PARAMETER ProcessId
        The process ID.
    .OUTPUTS
        Boolean indicating if process is running.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId
    )

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        return $null -ne $process
    } catch {
        return $false
    }
}

function Stop-SubAgentProcess {
    <#
    .SYNOPSIS
        Stops a Sub-agent process.
    .PARAMETER ProcessId
        The process ID.
    .PARAMETER Force
        Force kill the process.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId,

        [Parameter(Mandatory = $false)]
        [bool]$Force = $false
    )

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            if ($Force) {
                $process.Kill()
            } else {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(5000)) {
                    $process.Kill()
                }
            }
        }
    } catch {
        Write-Warning "Failed to stop process $ProcessId`: $_"
    }
}

Export-ModuleMember -Function @(
    'New-SubAgentConfig',
    'New-SubAgentState',
    'Test-SubAgentRetryable',
    'Get-SubAgentBackoffDuration',
    'Invoke-SubAgentRetry',
    'New-SubAgentReport',
    'Test-SubAgentReportComplete',
    'Get-SubAgentFailureCategory',
    'Invoke-SubAgentLaunch',
    'Update-SubAgentLaunchLog',
    'Get-SubAgentResult',
    'Test-SubAgentProcessRunning',
    'Stop-SubAgentProcess'
)
