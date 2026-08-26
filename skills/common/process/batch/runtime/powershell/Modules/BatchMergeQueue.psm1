#Requires -Version 7.0

<#
.SYNOPSIS
    Merge queue for serializing PR merges in Batch Orchestrator.

.DESCRIPTION
    Ensures PRs are merged one at a time via Merge Skill.
    After each merge, re-validates against latest main HEAD.
    Prevents Admin Bypass under all circumstances.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-MergeQueue {
    <#
    .SYNOPSIS
        Creates a new merge queue.
    #>
    [CmdletBinding()]
    param()

    return @{
        Pending = [System.Collections.Queue]::new()
        Merged = @()
        Failed = @()
        Conflicted = @()
        CurrentlyMerging = $null
    }
}

function Add-MergeQueueItem {
    <#
    .SYNOPSIS
        Adds a PR to the merge queue.
    .PARAMETER Queue
        The merge queue.
    .PARAMETER PrNumber
        The PR number.
    .PARAMETER IssueId
        The issue identifier.
    .PARAMETER WorktreePath
        Path to the Worktree.
    .PARAMETER BranchName
        Branch name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Queue,

        [Parameter(Mandatory = $true)]
        [int]$PrNumber,

        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $true)]
        [string]$WorktreePath,

        [Parameter(Mandatory = $true)]
        [string]$BranchName
    )

    $Queue.Pending.Enqueue(@{
        PrNumber = $PrNumber
        IssueId = $IssueId
        WorktreePath = $WorktreePath
        BranchName = $BranchName
        AddedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
    })
}

function Get-MergeQueueStatus {
    <#
    .SYNOPSIS
        Gets the current merge queue status.
    .PARAMETER Queue
        The merge queue.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Queue
    )

    return @{
        PendingCount = $Queue.Pending.Count
        MergedCount = $Queue.Merged.Count
        FailedCount = $Queue.Failed.Count
        ConflictedCount = $Queue.Conflicted.Count
        CurrentlyMerging = $Queue.CurrentlyMerging
        IsEmpty = $Queue.Pending.Count -eq 0 -and $null -eq $Queue.CurrentlyMerging
    }
}

function Invoke-MergeQueueProcess {
    <#
    .SYNOPSIS
        Processes the next item in the merge queue via Merge Skill.
    .PARAMETER Queue
        The merge queue.
    .PARAMETER MergeSkillPath
        Path to the Merge Skill wrapper.
    .PARAMETER Repository
        Optional repository.
    .OUTPUTS
        Hashtable with success, conflict, or failure information.
    .DESCRIPTION
        Calls the Merge Skill for a single PR. The Merge Skill handles:
        - Approval validation
        - Mandatory rebase
        - Conflict detection
        - Normal merge
        This function NEVER uses Admin Bypass.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Queue,

        [Parameter(Mandatory = $true)]
        [string]$MergeSkillPath,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    if ($Queue.Pending.Count -eq 0) {
        return @{
            Success = $false
            Reason = "Queue is empty"
        }
    }

    $item = $Queue.Pending.Peek()
    $Queue.CurrentlyMerging = $item

    Write-Host "=== Merge Queue: Processing PR #$($item.PrNumber) ===" -ForegroundColor Cyan
    Write-Host "Issue: $($item.IssueId)" -ForegroundColor Gray
    Write-Host "Branch: $($item.BranchName)" -ForegroundColor Gray
    Write-Host ""

    $mergeScript = Join-Path $MergeSkillPath "wrapper" "merge.ps1"

    $scriptArgs = @(
        "merge"
        "-PrNumber", $item.PrNumber.ToString()
        "-IssueNumber", ($item.IssueId -replace '[^0-9]', '')
        "-WorktreePath", $item.WorktreePath
        "-BranchName", $item.BranchName
    )

    if ($Repository) {
        $scriptArgs += "-Repository"
        $scriptArgs += $Repository
    }

    try {
        & pwsh -File $mergeScript @scriptArgs 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0) {
            $Queue.Pending.Dequeue() | Out-Null
            $Queue.Merged += $item
            $Queue.CurrentlyMerging = $null
            Write-Host "PR #$($item.PrNumber) merged successfully" -ForegroundColor Green
            return @{
                Success = $true
                Item = $item
            }
        } else {
            $Queue.Pending.Dequeue() | Out-Null
            $Queue.Failed += $item
            $Queue.CurrentlyMerging = $null
            Write-Host "PR #$($item.PrNumber) merge failed" -ForegroundColor Red
            return @{
                Success = $false
                Reason = "Merge Skill returned exit code $exitCode"
                Item = $item
            }
        }
    } catch {
        $Queue.CurrentlyMerging = $null
        Write-Host "Error invoking Merge Skill: $($_.Exception.Message)" -ForegroundColor Red
        return @{
            Success = $false
            Reason = "Exception: $($_.Exception.Message)"
            Item = $item
        }
    }
}

function Invoke-MergeQueueSerial {
    <#
    .SYNOPSIS
        Processes the entire merge queue serially.
    .PARAMETER Queue
        The merge queue.
    .PARAMETER MergeSkillPath
        Path to the Merge Skill wrapper.
    .PARAMETER Repository
        Optional repository.
    .OUTPUTS
        Hashtable with merge results.
    .DESCRIPTION
        Processes PRs one at a time. After each merge,
        the next PR will be rebased onto the updated main.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Queue,

        [Parameter(Mandatory = $true)]
        [string]$MergeSkillPath,

        [Parameter(Mandatory = $false)]
        [string]$Repository
    )

    $results = @()
    $totalItems = $Queue.Pending.Count

    Write-Host "=== Merge Queue: Processing $totalItems PRs serially ===" -ForegroundColor Green
    Write-Host ""

    $processedCount = 0
    while ($Queue.Pending.Count -gt 0) {
        $processedCount++
        Write-Host "--- Processing $processedCount of $totalItems ---" -ForegroundColor Cyan

        $result = Invoke-MergeQueueProcess -Queue $Queue -MergeSkillPath $MergeSkillPath -Repository $Repository
        $results += $result

        if (-not $result.Success -and $result.Reason -match "conflict") {
            Write-Host "Conflict detected. Stopping merge queue." -ForegroundColor Red
            Write-Host "Remaining items stay in queue." -ForegroundColor Yellow
            break
        }

        if ($Queue.Pending.Count -gt 0) {
            Write-Host "Waiting 2s before next merge (main HEAD update)..." -ForegroundColor Gray
            Start-Sleep -Seconds 2
        }
    }

    return @{
        ProcessedCount = $processedCount
        TotalCount = $totalItems
        MergedCount = $Queue.Merged.Count
        FailedCount = $Queue.Failed.Count
        Results = $results
    }
}

Export-ModuleMember -Function @(
    'New-MergeQueue',
    'Add-MergeQueueItem',
    'Get-MergeQueueStatus',
    'Invoke-MergeQueueProcess',
    'Invoke-MergeQueueSerial'
)
