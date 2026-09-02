#Requires -Version 7.0

<#
.SYNOPSIS
    State persistence for Batch Orchestrator.
.DESCRIPTION
    Handles saving and loading batch and issue state to/from JSON files
    for crash recovery and resume capability. Includes atomic writes,
    schema versioning, and transition audit logging.
.NOTES
    Version: 1.1.0
    Issue: #155, #170
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

$Script:PersistenceSchemaVersion = 1

function Get-BatchStateFilePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    if ([string]::IsNullOrWhiteSpace($BatchId)) {
        throw "BatchId must not be empty or whitespace-only"
    }
    if ($BatchId -match '[/\\]|(\.\.)') {
        throw "BatchId contains invalid path characters: $BatchId"
    }
    return Join-Path $StateDir ".batch-state-$BatchId.json"
}

function Save-BatchState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$State,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    $State.UpdatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    if (-not $State.ContainsKey("SchemaVersion")) {
        $State.SchemaVersion = $Script:PersistenceSchemaVersion
    }
    $dir = Split-Path -Parent $FilePath
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $tmpFile = "$FilePath.tmp.$pid.$(Get-Random)"
    try {
        $State | ConvertTo-Json -Depth 20 | Set-Content -Path $tmpFile -Force
        Move-Item -Path $tmpFile -Destination $FilePath -Force
    } catch {
        if (Test-Path $tmpFile) {
            Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Get-BatchState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    if (-not (Test-Path $FilePath)) {
        return $null
    }
    try {
        return Get-Content $FilePath -Raw | ConvertFrom-Json -AsHashtable
    } catch {
        throw "Failed to parse batch state from ${FilePath} (corrupt or invalid JSON): $($_.Exception.Message)"
    }
}

function Save-IssueStates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Issues,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    $data = @{
        SchemaVersion = $Script:PersistenceSchemaVersion
        UpdatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        Issues = $Issues
    }
    $dir = Split-Path -Parent $FilePath
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $tmpFile = "$FilePath.tmp.$pid.$(Get-Random)"
    try {
        $data | ConvertTo-Json -Depth 20 | Set-Content -Path $tmpFile -Force
        Move-Item -Path $tmpFile -Destination $FilePath -Force
    } catch {
        if (Test-Path $tmpFile) {
            Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Get-IssueStates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )
    if (-not (Test-Path $FilePath)) {
        return $null
    }
    try {
        $data = Get-Content $FilePath -Raw | ConvertFrom-Json -AsHashtable
        return $data.Issues
    } catch {
        throw "Failed to parse issue states from ${FilePath} (corrupt or invalid JSON): $($_.Exception.Message)"
    }
}

function New-BatchState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [int]$IssueCount = 0
    )
    return @{
        SchemaVersion = $Script:PersistenceSchemaVersion
        BatchId = $BatchId
        State = "BATCH_INITIALIZING"
        IssueCount = $IssueCount
        CompletedCount = 0
        FailedCount = 0
        BlockedCount = 0
        CreatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        UpdatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        FailureReason = $null
        DependencyGraph = $null
        ConcurrencyGroups = $null
        MergeQueueStatus = $null
    }
}

function New-IssueState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId,
        [Parameter(Mandatory = $false)]
        [int]$IssueNumber = 0,
        [Parameter(Mandatory = $false)]
        [string]$Description = ""
    )
    return @{
        IssueId = $IssueId
        IssueNumber = $IssueNumber
        Description = $Description
        State = "WAITING_DEPENDENCY"
        Dependencies = @()
        WorktreePath = $null
        BranchName = $null
        PrNumber = $null
        PrUrl = $null
        CommitSha = $null
        ApprovedCommitSha = $null
        RetryCount = 0
        LastError = $null
        LaunchStatus = $null
        ExecutionStatus = "NOT_STARTED"
        FailureClassification = $null
        SelectionReason = $null
        SelectedProvider = $null
        SelectedMechanism = $null
        Report = $null
        SubAgentProcessId = $null
        StartedAt = $null
        CompletedAt = $null
        CreatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        UpdatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
}

function Sync-StateWithGitHub {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$BatchState,
        [Parameter(Mandatory = $true)]
        [hashtable]$IssueStates
    )
    $changes = @()
    foreach ($issueId in $IssueStates.Keys) {
        $issue = $IssueStates[$issueId]
        if ($issue.PrNumber) {
            $prArgs = @("pr", "view", $issue.PrNumber, "--json", "state,mergeCommit,headRefName")
            $prResult = & gh @prArgs 2>$null
            if ($LASTEXITCODE -eq 0) {
                $pr = $prResult | ConvertFrom-Json
                if ($pr.state -eq "MERGED" -and $issue.State -ne "COMPLETED") {
                    $issue.State = "COMPLETED"
                    $issue.CompletedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                    $changes += "Issue $issueId already merged on GitHub"
                }
                if ($pr.state -eq "CLOSED" -and $issue.State -notin @("COMPLETED", "FAILED")) {
                    $issue.State = "FAILED"
                    $issue.LastError = "PR closed without merge"
                    $changes += "Issue $issueId PR was closed"
                }
            }
        }
        if ($issue.WorktreePath -and -not (Test-Path $issue.WorktreePath)) {
            if ($issue.State -notin @("COMPLETED", "FAILED", "BLOCKED")) {
                $changes += "Issue $issueId worktree no longer exists"
            }
        }
        $issue.UpdatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    return @{
        Changes = $changes
        BatchState = $BatchState
        IssueStates = $IssueStates
    }
}

function Get-TransitionLogPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    if ([string]::IsNullOrWhiteSpace($BatchId)) {
        throw "BatchId must not be empty or whitespace-only"
    }
    if ($BatchId -match '[/\\]|(\.\.)') {
        throw "BatchId contains invalid path characters: $BatchId"
    }
    return Join-Path $StateDir ".batch-log-$BatchId.jsonl"
}

function Write-TransitionLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $true)]
        [string]$EntityType,
        [Parameter(Mandatory = $true)]
        [string]$EntityId,
        [Parameter(Mandatory = $true)]
        [string]$FromState,
        [Parameter(Mandatory = $true)]
        [string]$ToState,
        [Parameter(Mandatory = $false)]
        [string]$Reason = "",
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $logPath = Get-TransitionLogPath -BatchId $BatchId -StateDir $StateDir
    $entry = @{
        timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        entityType = $EntityType
        entityId = $EntityId
        fromState = $FromState
        toState = $ToState
        reason = $Reason
    }
    $line = $entry | ConvertTo-Json -Depth 5 -Compress
    $logDir = Split-Path -Parent $logPath
    if ($logDir -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    Add-Content -Path $logPath -Value $line -Force -ErrorAction Stop
}

function Get-TransitionLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $logPath = Get-TransitionLogPath -BatchId $BatchId -StateDir $StateDir
    if (-not (Test-Path $logPath)) {
        return @()
    }
    $entries = [System.Collections.ArrayList]::new()
    $lines = Get-Content $logPath -ErrorAction SilentlyContinue
    foreach ($line in $lines) {
        if ($line.Trim()) {
            try {
                $entry = $line | ConvertFrom-Json -AsHashtable
                if ($null -ne $entry) {
                    [void]$entries.Add($entry)
                }
            } catch { }
        }
    }
    return ,($entries.ToArray())
}

Export-ModuleMember -Function @(
    'Get-BatchStateFilePath',
    'Save-BatchState',
    'Get-BatchState',
    'Save-IssueStates',
    'Get-IssueStates',
    'New-BatchState',
    'New-IssueState',
    'Sync-StateWithGitHub',
    'Get-TransitionLogPath',
    'Write-TransitionLog',
    'Get-TransitionLog'
)
