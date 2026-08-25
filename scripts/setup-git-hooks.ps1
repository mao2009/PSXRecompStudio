#!/usr/bin/env pwsh
# PSXRecompStudio - Git Hooks Setup Script (PowerShell)
# Enables local Git hooks from .githooks/ directory
# Run from repository root: ./scripts/setup-git-hooks.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

try {
    $repoRoot = git rev-parse --show-toplevel 2>$null
    if (-not $repoRoot) {
        Write-Error "❌ Not inside a Git repository"
        exit 1
    }
} catch {
    Write-Error "❌ Not inside a Git repository"
    exit 1
}

$hooksDir = Join-Path $repoRoot ".githooks"
if (-not (Test-Path -Path $hooksDir -PathType Container)) {
    Write-Error "❌ Hooks directory not found: $hooksDir"
    Write-Error "   Expected .githooks/ with pre-commit and pre-push"
    exit 1
}

$requiredHooks = @("pre-commit", "pre-push")
$missingHooks = @()
foreach ($hook in $requiredHooks) {
    if (-not (Test-Path -Path (Join-Path $hooksDir $hook) -PathType Leaf)) {
        $missingHooks += $hook
    }
}
if ($missingHooks.Count -gt 0) {
    Write-Error "❌ Required hooks not found in $hooksDir"
    Write-Error "   Missing: $($missingHooks -join ', ')"
    exit 1
}

$currentHooksPath = git config --get core.hooksPath 2>$null
$expectedHooksPath = ".githooks"

if ($currentHooksPath -eq $expectedHooksPath) {
    Write-Host "✅ Git hooks already configured: core.hooksPath = $expectedHooksPath"
    Write-Host "   Hooks directory: $hooksDir"
    exit 0
}

Write-Host "🔧 Configuring Git hooks..."
git config core.hooksPath $expectedHooksPath

$newHooksPath = git config --get core.hooksPath 2>$null
if ($newHooksPath -eq $expectedHooksPath) {
    Write-Host "✅ Git hooks configured successfully"
    Write-Host "   core.hooksPath = $newHooksPath"
    Write-Host "   Hooks directory: $hooksDir"
    Write-Host ""
    Write-Host "Installed hooks:"
    Get-ChildItem -Path $hooksDir -Filter "pre-*" | ForEach-Object {
        Write-Host "   $($_.Name) - $($_.Length) bytes"
    }
    Write-Host ""
    Write-Host "To verify: git config --get core.hooksPath"
} else {
    Write-Error "❌ Failed to configure hooks"
    exit 1
}