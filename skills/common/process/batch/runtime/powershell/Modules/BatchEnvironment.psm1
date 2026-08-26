#Requires -Version 7.0

<#
.SYNOPSIS
    Environment initialization utilities for Batch Skill Worktrees.

.DESCRIPTION
    Provides functions for initializing Worktree environments with required
    non-git files like .env, local.config, etc.

.NOTES
    Version: 2.0.0
    Issue: #145
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function Initialize-BatchEnvironment {
    <#
    .SYNOPSIS
        Initializes the Worktree environment with required non-git files.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .PARAMETER ConfigPath
        Path to the environment configuration file.
    .OUTPUTS
        Hashtable with initialization results.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $false)]
        [string]$ConfigPath
    )

    $results = @{
        Success = $true
        Copied = @()
        Generated = @()
        Failed = @()
        Warnings = @()
    }

    # Default configuration if none provided
    if (-not $ConfigPath) {
        $ConfigPath = Join-Path $WorktreePath ".batch-env-config.json"
    }

    # Load or create default configuration
    $config = Get-BatchEnvironmentConfig -ConfigPath $ConfigPath -WorktreePath $WorktreePath

    if ($null -eq $config) {
        $results.Warnings += "No environment configuration found. Skipping initialization."
        return $results
    }

    # Process each file in configuration
    foreach ($file in $config.Files) {
        $source = $file.Source
        $target = $file.Target
        $required = $file.Required
        $method = $file.Method

        try {
            switch ($method) {
                "copy" {
                    $result = Copy-BatchEnvironmentFile -Source $source -Target $target -WorktreePath $WorktreePath
                    if ($result.Success) {
                        $results.Copied += $target
                    } else {
                        if ($required) {
                            $results.Failed += @{
                                File = $target
                                Error = $result.Error
                            }
                            $results.Success = $false
                        } else {
                            $results.Warnings += "Optional file not copied: $target"
                        }
                    }
                }
                "generate" {
                    $result = New-BatchEnvironmentFile -Template $source -Target $target -WorktreePath $WorktreePath
                    if ($result.Success) {
                        $results.Generated += $target
                    } else {
                        if ($required) {
                            $results.Failed += @{
                                File = $target
                                Error = $result.Error
                            }
                            $results.Success = $false
                        } else {
                            $results.Warnings += "Optional file not generated: $target"
                        }
                    }
                }
                default {
                    $results.Warnings += "Unknown method for file: $target"
                }
            }
        } catch {
            if ($required) {
                $results.Failed += @{
                    File = $target
                    Error = $_.Exception.Message
                }
                $results.Success = $false
            } else {
                $results.Warnings += "Error processing optional file: $target - $($_.Exception.Message)"
            }
        }
    }

    # Verify required files exist
    foreach ($file in $config.Files) {
        if ($file.Required) {
            $targetPath = Join-Path $WorktreePath $file.Target
            if (-not (Test-Path $targetPath)) {
                $results.Failed += @{
                    File = $file.Target
                    Error = "Required file missing after initialization"
                }
                $results.Success = $false
            }
        }
    }

    return $results
}

function Get-BatchEnvironmentConfig {
    <#
    .SYNOPSIS
        Gets the environment configuration.
    .PARAMETER ConfigPath
        Path to the configuration file.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,

        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    # Check for project-specific config
    if (Test-Path $ConfigPath) {
        return Get-Content $ConfigPath | ConvertFrom-Json
    }

    # Check for .env.example
    $envExample = Join-Path $WorktreePath ".env.example"
    if (Test-Path $envExample) {
        return @{
            Files = @(
                @{
                    Source = ".env.example"
                    Target = ".env"
                    Required = $false
                    Method = "copy"
                }
            )
        }
    }

    # Return null if no config found
    return $null
}

function Copy-BatchEnvironmentFile {
    <#
    .SYNOPSIS
        Copies an environment file.
    .PARAMETER Source
        Source file path.
    .PARAMETER Target
        Target file path.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Target,

        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $sourcePath = Join-Path $WorktreePath $Source
    $targetPath = Join-Path $WorktreePath $Target

    if (-not (Test-Path $sourcePath)) {
        return @{
            Success = $false
            Error = "Source file not found: $Source"
        }
    }

    try {
        Copy-Item -Path $sourcePath -Destination $targetPath -Force
        return @{
            Success = $true
        }
    } catch {
        return @{
            Success = $false
            Error = $_.Exception.Message
        }
    }
}

function New-BatchEnvironmentFile {
    <#
    .SYNOPSIS
        Generates an environment file from template.
    .PARAMETER Template
        Template file path.
    .PARAMETER Target
        Target file path.
    .PARAMETER WorktreePath
        Path to the Worktree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Template,

        [Parameter(Mandatory = $true)]
        [string]$Target,

        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $templatePath = Join-Path $WorktreePath $Template
    $targetPath = Join-Path $WorktreePath $Target

    if (-not (Test-Path $templatePath)) {
        return @{
            Success = $false
            Error = "Template file not found: $Template"
        }
    }

    try {
        $content = Get-Content $templatePath -Raw
        # Basic template substitution (can be extended)
        $content = $content -replace '\{\{TIMESTAMP\}\}', (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
        $content = $content -replace '\{\{RANDOM\}\}', (Get-Random -Maximum 999999)

        Set-Content -Path $targetPath -Value $content
        return @{
            Success = $true
        }
    } catch {
        return @{
            Success = $false
            Error = $_.Exception.Message
        }
    }
}

function Test-BatchEnvironmentSecrets {
    <#
    .SYNOPSIS
        Tests if any secret files are being tracked by Git.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .OUTPUTS
        Hashtable with test results.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorktreePath
    )

    $originalLocation = Get-Location
    try {
        Set-Location $WorktreePath

        $secretPatterns = @(
            "\.env$",
            "\.env\.local$",
            "\.env\.production$",
            "\.env\.development$",
            "credentials",
            "secret",
            "\.pem$",
            "\.key$",
            "\.p12$"
        )

        $trackedFiles = git ls-files
        $secrets = @()

        foreach ($file in $trackedFiles) {
            foreach ($pattern in $secretPatterns) {
                if ($file -match $pattern) {
                    $secrets += $file
                    break
                }
            }
        }

        return @{
            Success = $secrets.Count -eq 0
            SecretsFound = $secrets
            Message = if ($secrets.Count -eq 0) { "No secrets found" } else { "Secret files detected" }
        }
    }
    finally {
        Set-Location $originalLocation
    }
}

Export-ModuleMember -Function @(
    'Initialize-BatchEnvironment',
    'Get-BatchEnvironmentConfig',
    'Copy-BatchEnvironmentFile',
    'New-BatchEnvironmentFile',
    'Test-BatchEnvironmentSecrets'
)
