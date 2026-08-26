param(
    [string]$IssueId,
    [int]$IssueNumber,
    [string]$Description,
    [string]$WorktreePath,
    [string]$BranchName,
    [string]$ResultFile,
    [int]$TimeoutMinutes = 25
)

$workerScript = Join-Path $PSScriptRoot "Invoke-SubAgentWorker.ps1"

Write-Host "=== Batch Sub-agent Launcher ===" -ForegroundColor Cyan
Write-Host "Issue: #$IssueNumber ($IssueId)" -ForegroundColor White
Write-Host "Worktree: $WorktreePath" -ForegroundColor Gray
Write-Host "Branch: $BranchName" -ForegroundColor Gray
Write-Host "Worker: $workerScript" -ForegroundColor Gray
Write-Host "Result: $ResultFile" -ForegroundColor Gray
Write-Host ""

& pwsh -File $workerScript -IssueId $IssueId -IssueNumber $IssueNumber -Description $Description -WorktreePath $WorktreePath -BranchName $BranchName -ResultFile $ResultFile -TimeoutMinutes $TimeoutMinutes

exit $LASTEXITCODE
