#Requires -Version 7.0

<#
.SYNOPSIS
    Provider-neutral checkpoint persistence for Batch Orchestrator workers.

.DESCRIPTION
    Handles saving and loading of batch/worker checkpoints for crash recovery
    and resume. Checkpoint schema is provider-neutral: core state is independent
    of any specific agent provider. Provider-specific metadata is isolated in
    the providerMetadata field.

.NOTES
    Version: 1.0.0
    Issue: #170
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

$Script:CheckpointSchemaVersion = 1

function Get-CheckpointDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = ".",
        [Parameter(Mandatory = $false)]
        [switch]$Create
    )
    $dir = Join-Path $StateDir ".batch-checkpoints-$BatchId"
    if ($Create -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Get-BatchCheckpointPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = ".",
        [Parameter(Mandatory = $false)]
        [switch]$Create
    )
    $dir = Get-CheckpointDirectory -BatchId $BatchId -StateDir $StateDir -Create:$Create
    return Join-Path $dir "batch-checkpoint.json"
}

function Get-WorkerCheckpointPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $true)]
        [string]$IssueId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = ".",
        [Parameter(Mandatory = $false)]
        [switch]$Create
    )
    $dir = Get-CheckpointDirectory -BatchId $BatchId -StateDir $StateDir -Create:$Create
    $safeId = $IssueId -replace '[^a-zA-Z0-9_-]', '_'
    return Join-Path $dir "worker-$safeId.json"
}

function New-BatchCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [int]$IssueCount = 0
    )
    return @{
        schemaVersion = $Script:CheckpointSchemaVersion
        batchId = $BatchId
        createdAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        batchState = "BATCH_INITIALIZING"
        issueCount = $IssueCount
        completedCount = 0
        failedCount = 0
        blockedCount = 0
        failureReason = $null
        workers = @{}
    }
}

function New-WorkerCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IssueId,
        [Parameter(Mandatory = $false)]
        [int]$IssueNumber = 0,
        [Parameter(Mandatory = $false)]
        [string]$Description = "",
        [Parameter(Mandatory = $false)]
        [string]$Provider = "",
        [Parameter(Mandatory = $false)]
        [int]$MaxRetries = 3
    )
    return @{
        schemaVersion = $Script:CheckpointSchemaVersion
        issueId = $IssueId
        issueNumber = $IssueNumber
        description = $Description
        createdAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        provider = $Provider
        lifecycleState = "PENDING"
        completedPhases = @()
        branch = $null
        baseCommit = $null
        currentCommit = $null
        resultCommit = $null
        prNumber = $null
        prState = $null
        worktreePath = $null
        testResult = $null
        testPassed = $false
        remainingWork = $null
        failureReason = $null
        failureCategory = $null
        retryCount = 0
        maxRetries = $MaxRetries
        lastRetryAt = $null
        processId = $null
        startedAt = $null
        completedAt = $null
        providerMetadata = @{}
    }
}

function Save-BatchCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Checkpoint,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $Checkpoint.updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $filePath = Get-BatchCheckpointPath -BatchId $Checkpoint.batchId -StateDir $StateDir -Create
    Save-AtomicJson -FilePath $filePath -Data $Checkpoint
}

function Get-BatchCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $filePath = Get-BatchCheckpointPath -BatchId $BatchId -StateDir $StateDir
    if (-not (Test-Path $filePath)) {
        return $null
    }
    try {
        $data = Get-Content $filePath -Raw | ConvertFrom-Json -AsHashtable
        return $data
    } catch {
        Write-Warning "Failed to read batch checkpoint: $_"
        return $null
    }
}

function Save-WorkerCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Checkpoint,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $Checkpoint.updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    if (-not $Checkpoint.ContainsKey("batchId") -or [string]::IsNullOrWhiteSpace($Checkpoint.batchId)) {
        throw "Worker checkpoint for '$($Checkpoint.issueId)' has no batchId"
    }
    $batchId = $Checkpoint.batchId
    $filePath = Get-WorkerCheckpointPath -BatchId $batchId -IssueId $Checkpoint.issueId -StateDir $StateDir -Create
    Save-AtomicJson -FilePath $filePath -Data $Checkpoint
}

function Get-WorkerCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $true)]
        [string]$IssueId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $filePath = Get-WorkerCheckpointPath -BatchId $BatchId -IssueId $IssueId -StateDir $StateDir
    if (-not (Test-Path $filePath)) {
        return $null
    }
    try {
        $data = Get-Content $filePath -Raw | ConvertFrom-Json -AsHashtable
        return $data
    } catch {
        Write-Warning "Failed to read worker checkpoint for $IssueId`: $_"
        return $null
    }
}

function Get-AllWorkerCheckpoints {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $dir = Get-CheckpointDirectory -BatchId $BatchId -StateDir $StateDir
    $files = Get-ChildItem -Path $dir -Filter "worker-*.json" -ErrorAction SilentlyContinue
    $checkpoints = @{}
    foreach ($file in $files) {
        try {
            $data = Get-Content $file.FullName -Raw | ConvertFrom-Json -AsHashtable
            if ($data.ContainsKey("issueId")) {
                $checkpoints[$data.issueId] = $data
            }
        } catch {
            Write-Warning "Failed to read checkpoint file $($file.Name): $_"
        }
    }
    return $checkpoints
}

function Remove-WorkerCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $true)]
        [string]$IssueId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $filePath = Get-WorkerCheckpointPath -BatchId $BatchId -IssueId $IssueId -StateDir $StateDir
    if (Test-Path $filePath) {
        Remove-Item $filePath -Force
    }
}

function Remove-BatchCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $dir = Get-CheckpointDirectory -BatchId $BatchId -StateDir $StateDir
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }
}

function Test-BatchCheckpointExists {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchId,
        [Parameter(Mandatory = $false)]
        [string]$StateDir = "."
    )
    $filePath = Get-BatchCheckpointPath -BatchId $BatchId -StateDir $StateDir
    return (Test-Path $filePath)
}

function Save-AtomicJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [hashtable]$Data
    )
    $dir = Split-Path -Parent $FilePath
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $tmpFile = "$FilePath.tmp.$pid.$(Get-Random)"
    try {
        $Data | ConvertTo-Json -Depth 20 | Set-Content -Path $tmpFile -Force
        Move-Item -Path $tmpFile -Destination $FilePath -Force
    } catch {
        if (Test-Path $tmpFile) {
            Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Get-WorkerCheckpointSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Checkpoint
    )
    $summary = @"
Worker: $($Checkpoint.issueId) (Issue #$($Checkpoint.issueNumber))
Provider: $($Checkpoint.provider)
State: $($Checkpoint.lifecycleState)
Branch: $($Checkpoint.branch)
Current Commit: $($Checkpoint.currentCommit)
Result Commit: $($Checkpoint.resultCommit)
PR: $($Checkpoint.prNumber)
Completed Phases: $($Checkpoint.completedPhases -join ', ')
Test Passed: $($Checkpoint.testPassed)
Remaining Work: $($Checkpoint.remainingWork)
Failure: $($Checkpoint.failureReason)
Retry Count: $($Checkpoint.retryCount)/$($Checkpoint.maxRetries)
Provider Metadata Keys: $($Checkpoint.providerMetadata.Keys -join ', ')
"@
    return $summary
}

function New-RecoveryContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$BatchCheckpoint,
        [Parameter(Mandatory = $true)]
        [hashtable]$WorkerCheckpoint
    )
    return @{
        batchId = $BatchCheckpoint.batchId
        batchState = $BatchCheckpoint.batchState
        issueId = $WorkerCheckpoint.issueId
        issueNumber = $WorkerCheckpoint.issueNumber
        description = $WorkerCheckpoint.description
        provider = $WorkerCheckpoint.provider
        branch = $WorkerCheckpoint.branch
        baseCommit = $WorkerCheckpoint.baseCommit
        currentCommit = $WorkerCheckpoint.currentCommit
        resultCommit = $WorkerCheckpoint.resultCommit
        prNumber = $WorkerCheckpoint.prNumber
        prState = $WorkerCheckpoint.prState
        worktreePath = $WorkerCheckpoint.worktreePath
        completedPhases = $WorkerCheckpoint.completedPhases
        testResult = $WorkerCheckpoint.testResult
        testPassed = $WorkerCheckpoint.testPassed
        remainingWork = $WorkerCheckpoint.remainingWork
        failureReason = $WorkerCheckpoint.failureReason
        failureCategory = $WorkerCheckpoint.failureCategory
        retryCount = $WorkerCheckpoint.retryCount
        maxRetries = $WorkerCheckpoint.maxRetries
        workerSummary = Get-WorkerCheckpointSummary -Checkpoint $WorkerCheckpoint
    }
}

Export-ModuleMember -Function @(
    'Get-CheckpointDirectory',
    'Get-BatchCheckpointPath',
    'Get-WorkerCheckpointPath',
    'New-BatchCheckpoint',
    'New-WorkerCheckpoint',
    'Save-BatchCheckpoint',
    'Get-BatchCheckpoint',
    'Save-WorkerCheckpoint',
    'Get-WorkerCheckpoint',
    'Get-AllWorkerCheckpoints',
    'Remove-WorkerCheckpoint',
    'Remove-BatchCheckpoint',
    'Test-BatchCheckpointExists',
    'Save-AtomicJson',
    'Get-WorkerCheckpointSummary',
    'New-RecoveryContext'
)
