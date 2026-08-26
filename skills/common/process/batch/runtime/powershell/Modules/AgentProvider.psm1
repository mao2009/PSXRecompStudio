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
        [string]$CommitSha = "",

        [Parameter(Mandatory = $false)]
        [int]$PullRequestNumber = 0,

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
        CommitSha           = $CommitSha
        PullRequestNumber   = $PullRequestNumber
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

    $claude_exe = "C:\Users\Hobart\.local\bin\claude.exe"
    if (-not (Test-Path $claude_exe)) {
        $claude_exe = "claude"
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
        [string]$ResultDirectory
    )

    $start_time = Get-Date

    # Prepare result files
    $stdout_file = Join-Path $ResultDirectory "claude-stdout.log"
    $stderr_file = Join-Path $ResultDirectory "claude-stderr.log"

    if (-not (Test-Path $ResultDirectory)) {
        New-Item -ItemType Directory -Path $ResultDirectory -Force | Out-Null
    }

    try {
        # Build arguments
        $args = @()
        $args += $ProviderConfig.Arguments  # -p, --no-ask-approve
        $args += $Prompt

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ProviderConfig.Executable
        $psi.Arguments = $args -join ' '
        $psi.UseShellExecute = $false
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

        # Capture output
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()

        # Wait for process to complete
        $process.WaitForExit()
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
        [string]$ProviderName = "claude-code"
    )

    switch ($ProviderName) {
        "claude-code" {
            return New-ClaudeCodeProvider
        }
        "opencode" {
            throw "OpenCode provider not yet implemented"
        }
        "test" {
            return @{
                Name        = "test"
                Type        = "test"
                Executable  = "echo"
                Arguments   = @()
                IsTestProvider = $true
            }
        }
        default {
            throw "Unknown provider: $ProviderName"
        }
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
        [string]$ResultDirectory
    )

    # Dispatch to provider-specific invoker
    switch ($ProviderConfig.Type) {
        "claude-code" {
            return Invoke-ClaudeCodeProvider `
                -ProviderConfig $ProviderConfig `
                -Prompt $Prompt `
                -WorkingDirectory $WorkingDirectory `
                -ResultDirectory $ResultDirectory
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
    'Invoke-AgentProvider'
)
