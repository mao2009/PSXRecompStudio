#Requires -Version 7.0

<#
.SYNOPSIS
    Agent Provider abstraction for Batch Orchestrator.

.DESCRIPTION
    Defines interfaces and implementations for various AI Agents
    (Claude Code, OpenCode, etc.)

    Allows Batch Orchestrator to work with multiple agents without
    hard-coding agent-specific logic.
#>

# ============================================================
# Agent Provider Configuration Types
# ============================================================

function New-AgentProviderConfig {
    <#
    .SYNOPSIS
        Creates a new Agent Provider configuration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Type,  # "claude-code", "opencode", etc.

        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $false)]
        [string[]]$Arguments = @(),

        [Parameter(Mandatory = $false)]
        [hashtable]$Environment = @{}
    )

    return @{
        Name         = $Name
        Type         = $Type
        Executable   = $Executable
        Arguments    = $Arguments
        Environment  = $Environment
        CreatedAt    = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
}

function New-AgentProviderResult {
    <#
    .SYNOPSIS
        Creates a standardized Agent Provider execution result.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProviderName,

        [Parameter(Mandatory = $true)]
        [bool]$Success,

        [Parameter(Mandatory = $true)]
        [int]$ExitCode,

        [Parameter(Mandatory = $false)]
        [int]$ProcessId = 0,

        [Parameter(Mandatory = $false)]
        [string]$StartedAt = "",

        [Parameter(Mandatory = $false)]
        [string]$FinishedAt = "",

        [Parameter(Mandatory = $false)]
        [string]$StdoutPath = "",

        [Parameter(Mandatory = $false)]
        [string]$StderrPath = "",

        [Parameter(Mandatory = $false)]
        [string]$StdoutContent = "",

        [Parameter(Mandatory = $false)]
        [string]$StderrContent = "",

        [Parameter(Mandatory = $false)]
        [string]$Error = ""
    )

    return @{
        ProviderName        = $ProviderName
        Success             = $Success
        ExitCode            = $ExitCode
        ProcessId           = $ProcessId
        StartedAt           = $StartedAt
        FinishedAt          = $FinishedAt
        StdoutPath          = $StdoutPath
        StderrPath          = $StderrPath
        StdoutContent       = $StdoutContent
        StderrContent       = $StderrContent
        Error               = $Error
    }
}

# ============================================================
# Provider: Claude Code
# ============================================================

function New-ClaudeCodeProvider {
    <#
    .SYNOPSIS
        Creates a Claude Code provider configuration.

    .DESCRIPTION
        Uses Claude Code in non-interactive mode (-p/--print) for batch processing.
        Disables session persistence to prevent prompt dialogs.
    #>
    [CmdletBinding()]
    param()

    $claude_exe = if ($env:BATCH_CLAUDE_EXECUTABLE) {
        $env:BATCH_CLAUDE_EXECUTABLE
    } elseif ($cmd = Get-Command "claude" -ErrorAction SilentlyContinue) {
        $cmd.Source
    } else {
        "claude"
    }

    return New-AgentProviderConfig `
        -Name "claude-code" `
        -Type "claude-code" `
        -Executable $claude_exe `
        -Arguments @("-p", "--permission-mode", "auto", "--no-session-persistence")
}

function Invoke-ClaudeCodeProvider {
    <#
    .SYNOPSIS
        Executes Claude Code with the given prompt.
    .PARAMETER ProviderConfig
        Provider configuration from New-ClaudeCodeProvider
    .PARAMETER Prompt
        The prompt to send to Claude Code
    .PARAMETER WorkingDirectory
        Working directory for Claude Code
    .PARAMETER ResultDirectory
        Directory to save stdout/stderr logs
    .PARAMETER TimeoutMinutes
        Timeout in minutes (default: 30)
    .OUTPUTS
        Hashtable with execution result
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$ProviderConfig,

        [Parameter(Mandatory = $true)]
        [string]$Prompt,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ResultDirectory,

        [Parameter(Mandatory = $false)]
        [int]$TimeoutMinutes = 30
    )

    $start_time = Get-Date

    # Prepare result files
    $stdout_file = Join-Path $ResultDirectory "claude-stdout.log"
    $stderr_file = Join-Path $ResultDirectory "claude-stderr.log"

    if (-not (Test-Path $ResultDirectory)) {
        New-Item -ItemType Directory -Path $ResultDirectory -Force | Out-Null
    }

    try {
        # Build arguments (without prompt - will be passed via stdin)
        $provider_args = @()
        $provider_args += $ProviderConfig.Arguments  # -p, --permission-mode, auto, --no-session-persistence
        $provider_args += "--input-format", "text"   # Explicitly set input format
        $provider_args += "--tools", "Edit,Bash,Read"  # Restrict built-in tools (Git uses Bash)
        $provider_args += "--allowedTools", "Edit,Bash,Read"  # Auto-approve these tools

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ProviderConfig.Executable
        foreach ($a in $provider_args) { $psi.ArgumentList.Add($a) }
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true   # CRITICAL: Accept stdin
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.WorkingDirectory = $WorkingDirectory

        # Add environment variables from provider config
        foreach ($key in $ProviderConfig.Environment.Keys) {
            $psi.Environment[$key] = $ProviderConfig.Environment[$key]
        }

        Write-Host "[AgentProvider] Starting Claude Code process..."
        Write-Host ("  Executable: {0}" -f $psi.FileName)
        Write-Host ("  WorkingDir: {0}" -f $WorkingDirectory)

        $process = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $process) {
            throw "Failed to start Claude Code process"
        }

        $process_id = $process.Id
        Write-Host ("  Process ID: {0}" -f $process_id)

        # Start async reads BEFORE sending prompt to prevent pipe buffer deadlock.
        # Background threads consume stdout/stderr while we write to stdin.
        $stdout_task = $process.StandardOutput.ReadToEndAsync()
        $stderr_task = $process.StandardError.ReadToEndAsync()

        # Send prompt via stdin
        Write-Host "  Sending prompt to Claude Code..."
        $process.StandardInput.WriteLine($Prompt)
        $process.StandardInput.Close()

        # Wait for process to complete with timeout enforcement
        $timeout_ms = $TimeoutMinutes * 60 * 1000
        if (-not $process.WaitForExit($timeout_ms)) {
            Write-Host ("  TIMEOUT: Killing process after {0} minutes" -f $TimeoutMinutes)
            try { $process.Kill($true) } catch { $process.Kill() }
            $process.WaitForExit(10000) | Out-Null

            $end_time = Get-Date
            # Collect whatever output we can, bounded to 5s per stream
            $stdout = ""
            $stderr = ""
            try {
                if ($stdout_task.Wait(5000)) { $stdout = $stdout_task.Result }
            } catch { }
            try {
                if ($stderr_task.Wait(5000)) { $stderr = $stderr_task.Result }
            } catch { }

            $stdout | Set-Content -Path $stdout_file -Force
            $stderr | Set-Content -Path $stderr_file -Force

            return @{
                ProviderName    = $ProviderConfig.Name
                Success         = $false
                ExitCode        = -2
                ProcessId       = $process_id
                StartedAt       = $start_time.ToString("yyyy-MM-ddTHH:mm:ssZ")
                FinishedAt      = $end_time.ToString("yyyy-MM-ddTHH:mm:ssZ")
                StdoutPath      = $stdout_file
                StderrPath      = $stderr_file
                StdoutContent   = $stdout
                StderrContent   = $stderr
                Error           = "Claude Code timed out after $TimeoutMinutes minutes"
            }
        }

        # Process exited - collect async output
        $stdout = $stdout_task.GetAwaiter().GetResult()
        $stderr = $stderr_task.GetAwaiter().GetResult()
        $exit_code = $process.ExitCode

        $end_time = Get-Date

        # Save output to files
        $stdout | Set-Content -Path $stdout_file -Force
        $stderr | Set-Content -Path $stderr_file -Force

        Write-Host ("  Exit Code: {0}" -f $exit_code)
        Write-Host ("  Duration: {0}ms" -f ($end_time - $start_time).TotalMilliseconds)

        return @{
            ProviderName    = $ProviderConfig.Name
            Success         = ($exit_code -eq 0)
            ExitCode        = $exit_code
            ProcessId       = $process_id
            StartedAt       = $start_time.ToString("yyyy-MM-ddTHH:mm:ssZ")
            FinishedAt      = $end_time.ToString("yyyy-MM-ddTHH:mm:ssZ")
            StdoutPath      = $stdout_file
            StderrPath      = $stderr_file
            StdoutContent   = $stdout
            StderrContent   = $stderr
            Error           = if ($exit_code -ne 0) { "Process exited with code $exit_code" } else { "" }
        }
    }
    catch {
        $end_time = Get-Date
        Write-Host ("  ERROR: {0}" -f $_.Exception.Message)

        return @{
            ProviderName    = $ProviderConfig.Name
            Success         = $false
            ExitCode        = -1
            ProcessId       = 0
            StartedAt       = $start_time.ToString("yyyy-MM-ddTHH:mm:ssZ")
            FinishedAt      = $end_time.ToString("yyyy-MM-ddTHH:mm:ssZ")
            StdoutPath      = ""
            StderrPath      = ""
            StdoutContent   = ""
            StderrContent   = ""
            Error           = $_.Exception.Message
        }
    }
}

# ============================================================
# Exports
# ============================================================

# ============================================================
# Provider Loader (Configuration-based)
# ============================================================

function Get-AgentProvider {
    <#
    .SYNOPSIS
        Gets the configured Agent Provider.

    .PARAMETER ProviderName
        Name of the provider (e.g., "claude-code", "opencode")

    .OUTPUTS
        Hashtable with provider configuration
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$ProviderName = ""
    )

    if ([string]::IsNullOrWhiteSpace($ProviderName)) {
        throw "No provider configured. Resolve-AgentProvider must be called before loading a provider."
    }

    switch ($ProviderName) {
        "claude-code" {
            return New-ClaudeCodeProvider
        }
        "opencode" {
            throw "OpenCode provider not yet implemented"
        }
        "test" {
            return New-TestProvider
        }
        "test-provider" {
            return New-TestProvider
        }
        default {
            throw "Unknown provider: $ProviderName"
        }
    }
}

function Resolve-AgentProvider {
    <#
    .SYNOPSIS
        Resolves execution using host-native capability first.

    .DESCRIPTION
        PATH discovery is intentionally not a selection mechanism. An external
        provider is eligible only when ProviderName is explicitly supplied.
        Native capability is reported by the host runtime through deterministic
        environment values because this child process cannot introspect a host
        agent API.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$ProviderName = "",

        [Parameter(Mandatory = $false)]
        [string]$HostAgent = $(if ($env:BATCH_HOST_AGENT) { $env:BATCH_HOST_AGENT } else { "" }),

        [Parameter(Mandatory = $false)]
        [bool]$NativeSubagentAvailable = $(if ($env:BATCH_NATIVE_SUBAGENT_AVAILABLE) { $env:BATCH_NATIVE_SUBAGENT_AVAILABLE -match '^(1|true|yes)$' } else { $false })
    )

    if ($NativeSubagentAvailable) {
        $selected = if ($HostAgent) { $HostAgent } else { "host" }
        return @{
            HostAgent = $HostAgent
            NativeSubagentCapability = "AVAILABLE"
            ExplicitProviderConfigured = $false
            SelectedProvider = $selected
            SelectedMechanism = "native-subagent"
            SelectionReason = "Current host native sub-agent/task capability is available"
            Blocked = $false
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ProviderName)) {
        # Loading is the adapter validation step. It may inspect the executable,
        # but only after explicit selection has been made.
        $null = Get-AgentProvider -ProviderName $ProviderName
        return @{
            HostAgent = $HostAgent
            NativeSubagentCapability = "UNAVAILABLE"
            ExplicitProviderConfigured = $true
            SelectedProvider = $ProviderName
            SelectedMechanism = "provider-adapter"
            SelectionReason = "Explicit provider configuration selected"
            Blocked = $false
        }
    }

    return @{
        HostAgent = $HostAgent
        NativeSubagentCapability = "UNAVAILABLE"
        ExplicitProviderConfigured = $false
        SelectedProvider = $null
        SelectedMechanism = $null
        SelectionReason = "No native sub-agent capability and no explicit execution provider configured"
        Blocked = $true
    }
}

function New-NativeDispatchRequest {
    <#
    .SYNOPSIS
        Materializes the host-native dispatch contract without spawning a process.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$IssueNumber,
        [Parameter(Mandatory = $true)][string]$IssueId,
        [Parameter(Mandatory = $true)][string]$WorktreePath,
        [Parameter(Mandatory = $true)][string]$BranchName,
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][string]$ResultFile,
        [Parameter(Mandatory = $false)][string[]]$RequiredSkills = @(
            "skills/common/task/implementation/SKILL.md",
            "skills/common/process/batch/SKILL.md"
        ),
        [Parameter(Mandatory = $false)][string]$ExecutionScope = "Implement only the requested Issue in the isolated worktree",
        [Parameter(Mandatory = $false)][string]$ValidationRequirements = "Run targeted tests, related Batch tests, build/analyzer, and report results"
    )

    $requestDirectory = Join-Path $WorktreePath ".subagent"
    if (-not (Test-Path $requestDirectory)) {
        New-Item -ItemType Directory -Path $requestDirectory -Force | Out-Null
    }
    $requestFile = Join-Path $requestDirectory "dispatch-request.json"
    $request = [ordered]@{
        Status = "READY_FOR_NATIVE_DISPATCH"
        TaskId = $IssueId
        IssueId = $IssueId
        IssueNumber = $IssueNumber
        WorktreePath = $WorktreePath
        BranchName = $BranchName
        Prompt = $Prompt
        RequiredSkills = @($RequiredSkills)
        ExecutionScope = $ExecutionScope
        ValidationRequirements = $ValidationRequirements
        ResultFile = $ResultFile
        CreatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    $request | ConvertTo-Json -Depth 10 | Set-Content -Path $requestFile -Force
    return @{
        RequestFile = $requestFile
        Status = "READY_FOR_NATIVE_DISPATCH"
        ProcessId = $null
        SpawnedProcess = $false
    }
}

function New-TestProvider {
    return @{
        Name        = "test-provider"
        Type        = "test"
        Executable  = "echo"
        Arguments   = @()
    }
}

function Invoke-AgentProvider {
    <#
    .SYNOPSIS
        Generic Agent Provider invoker.

    .DESCRIPTION
        Invokes the configured agent provider without provider-specific logic.
        Supports multiple providers through abstraction.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProviderName,

        [Parameter(Mandatory = $true)]
        [hashtable]$ProviderConfig,

        [Parameter(Mandatory = $true)]
        [string]$Prompt,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ResultDirectory,

        [Parameter(Mandatory = $false)]
        [int]$TimeoutMinutes = 30
    )

    # Dispatch to provider-specific invoker
    switch ($ProviderConfig.Type) {
        "claude-code" {
            return Invoke-ClaudeCodeProvider `
                -ProviderConfig $ProviderConfig `
                -Prompt $Prompt `
                -WorkingDirectory $WorkingDirectory `
                -ResultDirectory $ResultDirectory `
                -TimeoutMinutes $TimeoutMinutes
        }
        "test" {
            # Test provider: return immediate success
            return @{
                ProviderName    = "test"
                Success         = $true
                ExitCode        = 0
                ProcessId       = 0
                StartedAt       = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                FinishedAt      = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
                StdoutPath      = ""
                StderrPath      = ""
                StdoutContent   = "Test provider executed"
                StderrContent   = ""
                Error           = ""
            }
        }
        default {
            throw "Unknown provider type: $($ProviderConfig.Type)"
        }
    }
}

Export-ModuleMember -Function @(
    'New-AgentProviderConfig',
    'New-AgentProviderResult',
    'New-ClaudeCodeProvider',
    'Invoke-ClaudeCodeProvider',
    'Get-AgentProvider',
    'Resolve-AgentProvider',
    'New-NativeDispatchRequest',
    'Invoke-AgentProvider'
)
