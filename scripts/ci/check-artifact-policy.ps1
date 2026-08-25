#Requires -Version 7.0
<#
.SYNOPSIS
    Repository artifact contamination gate for PSXRecompStudio.

.DESCRIPTION
    Scans every Git-tracked file against the repository artifact policy SSOT
    (config/artifact-policy.json) and fails with a violation report when:

      - a file lives under a forbidden path segment (rom/, bios/, bin/, obj/,
        build/, out/, publish/, artifacts/, dist/, node_modules/)
      - a file has a forbidden extension (ROM/disc images, BIOS dumps, build
        outputs, ...)
      - a file exceeds the maximum allowed size
      - a file's content matches a known ROM / disc-image / PS-X executable
        signature regardless of its name or extension

    Files listed in the policy allowlist ("allowedPaths") are exempt from all
    rules. The check covers the whole tracked tree on every run, which is a
    superset of PR-diff scanning at this repository's scale.

    Exit codes: 0 = clean, 1 = violations found, 2 = setup/internal error.

.EXAMPLE
    pwsh ./scripts/ci/check-artifact-policy.ps1

.LINK
    docs/development/artifact-policy.md
#>

[CmdletBinding()]
param(
    # Repository root to scan. Defaults to the checkout this script lives in.
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath '..' '..')).Path
}

$policyPath = Join-Path -Path $RepoRoot -ChildPath 'config/artifact-policy.json'
if (-not (Test-Path -LiteralPath $policyPath)) {
    Write-Error "Artifact policy SSOT not found: $policyPath"
    exit 2
}

try {
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Error "Failed to parse artifact policy ($policyPath): $_"
    exit 2
}

$trackedRaw = & git -C $RepoRoot ls-files
if ($LASTEXITCODE -ne 0) {
    Write-Error "git ls-files failed with exit code $LASTEXITCODE"
    exit 2
}
$tracked = @($trackedRaw | Where-Object { $_ })

$allowedPaths = @($policy.allowedPaths) | ForEach-Object { $_.ToString().ToLowerInvariant() }
$forbiddenExtensions = @($policy.forbiddenExtensions) | ForEach-Object { $_.ToString().ToLowerInvariant() }
$forbiddenSegments = @($policy.forbiddenPathSegments) | ForEach-Object { $_.ToString().ToLowerInvariant() }
$maxSize = [long]$policy.maxFileSizeBytes
$signatures = @($policy.contentSignatures)

$violations = [System.Collections.Generic.List[string]]::new()
[int]$scanned = 0

function Add-Violation {
    param([string]$Rule, [string]$File, [string]$Reason)
    $violations.Add("[$Rule] $File :: $Reason")
}

foreach ($rel in $tracked) {
    $norm = ($rel -replace '\\', '/')
    $lower = $norm.ToLowerInvariant()

    if ($allowedPaths -contains $lower) {
        continue
    }
    $scanned++

    $full = Join-Path -Path $RepoRoot -ChildPath $norm
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Add-Violation -Rule 'UNREADABLE_FILE' -File $norm -Reason 'tracked file does not exist on disk'
        continue
    }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    if (-not $item) {
        Add-Violation -Rule 'UNREADABLE_FILE' -File $norm -Reason 'tracked file could not be read from disk'
        continue
    }

    # Rule 1: forbidden path segment (directory-level policy).
    $segments = $lower -split '/'
    $hitSegment = $segments | Where-Object { $forbiddenSegments -contains $_ } | Select-Object -First 1
    if ($hitSegment) {
        Add-Violation -Rule 'FORBIDDEN_PATH_SEGMENT' -File $norm -Reason "path contains forbidden segment '$hitSegment/'"
    }

    # Rule 2: forbidden extension.
    $ext = [System.IO.Path]::GetExtension($norm).ToLowerInvariant()
    if ($ext -and ($forbiddenExtensions -contains $ext)) {
        Add-Violation -Rule 'FORBIDDEN_EXTENSION' -File $norm -Reason "extension '$ext' is forbidden"
    }

    # Rule 3: size guard.
    if ($item.Length -gt $maxSize) {
        Add-Violation -Rule 'FILE_TOO_LARGE' -File $norm -Reason "$($item.Length) bytes exceeds limit of $maxSize bytes"
    }

    # Rule 4: content signatures (catches renamed binaries).
    foreach ($sig in $signatures) {
        $offset = [long]$sig.offset
        $expectedHex = ($sig.hex -replace '\s', '').ToUpperInvariant()
        $sigLen = [int]($expectedHex.Length / 2)
        if ($item.Length -lt ($offset + $sigLen)) {
            continue
        }

        $stream = [System.IO.File]::OpenRead($full)
        try {
            [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
            $buffer = [byte[]]::new($sigLen)
            $read = 0
            while ($read -lt $sigLen) {
                $n = $stream.Read($buffer, $read, $sigLen - $read)
                if ($n -le 0) { break }
                $read += $n
            }
            if ($read -eq $sigLen -and ([Convert]::ToHexString($buffer) -eq $expectedHex)) {
                Add-Violation -Rule 'CONTENT_SIGNATURE' -File $norm -Reason "content matches '$($sig.name)' signature at offset $offset"
            }
        } finally {
            $stream.Dispose()
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host ("Artifact policy check FAILED: {0} violation(s) in {1} scanned file(s)." -f $violations.Count, $scanned)
    foreach ($v in $violations) {
        Write-Host "  $v"
    }
    Write-Host 'Policy SSOT: config/artifact-policy.json'
    Write-Host 'Guidance:    docs/development/artifact-policy.md'
    exit 1
}

Write-Host ("Artifact policy check passed: {0} tracked file(s) scanned, no violations." -f $scanned)
exit 0
