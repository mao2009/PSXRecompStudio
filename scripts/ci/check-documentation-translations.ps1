param(
    [string]$RegistryPath = "config/docs/translations.json"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $RegistryPath)) {
    throw "Translation registry not found: $RegistryPath"
}

$registry = Get-Content $RegistryPath -Raw | ConvertFrom-Json
if ($registry.version -ne 1) {
    throw "Unsupported translation registry version: $($registry.version)"
}

$hasStructuralError = $false

foreach ($pair in $registry.pairs) {
    $canonical = [string]$pair.canonical
    $translation = [string]$pair.translation
    $marker = [string]$pair.canonicalMarker

    if (-not (Test-Path $canonical)) {
        Write-Error "Canonical document is missing: $canonical"
        $hasStructuralError = $true
        continue
    }

    if (-not (Test-Path $translation)) {
        Write-Error "Registered translation is missing: $translation"
        $hasStructuralError = $true
        continue
    }

    $translationContent = Get-Content $translation -Raw
    if (-not $translationContent.Contains($marker)) {
        Write-Error "Translation '$translation' does not contain its required Canonical marker: $marker"
        $hasStructuralError = $true
    }

    $canonicalCommit = git log -1 --format=%H -- $canonical
    $translationCommit = git log -1 --format=%H -- $translation

    if ([string]::IsNullOrWhiteSpace($canonicalCommit) -or [string]::IsNullOrWhiteSpace($translationCommit)) {
        Write-Warning "Unable to determine freshness for '$translation' -> '$canonical'."
        continue
    }

    $canonicalTime = [DateTimeOffset]::FromUnixTimeSeconds([int64](git log -1 --format=%ct -- $canonical))
    $translationTime = [DateTimeOffset]::FromUnixTimeSeconds([int64](git log -1 --format=%ct -- $translation))

    if ($canonicalTime -gt $translationTime) {
        Write-Warning "Translation may be stale: '$translation' was last changed before Canonical '$canonical'. Canonical commit: $canonicalCommit; translation commit: $translationCommit"
    } else {
        Write-Host "Translation freshness OK: $translation -> $canonical"
    }
}

if ($hasStructuralError) {
    throw "Documentation translation relationship check failed."
}

Write-Host "Documentation translation relationship check completed. Freshness warnings require human review; they do not prove semantic divergence."
