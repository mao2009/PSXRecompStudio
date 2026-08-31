#Requires -Version 7.0
<#
.SYNOPSIS
    Heuristic docstring coverage measurement for PSXRecompStudio's C# and C++
    public / interop API surface.

.DESCRIPTION
    Implements the measurement mechanism required by the API documentation
    and docstring policy (docs/development/documentation-policy.md, ADR-011,
    Issue #183). It is a line-based heuristic scanner, not a Roslyn/Clang
    syntax-tree analyzer: it reports an approximate, reviewable percentage
    intended to track policy compliance over time, not to gate merges by
    itself (CodeRabbit diff-scoped Docstring Coverage pre-merge check
    remains the enforcement point; see .coderabbit.yaml).

    Two categories are measured by default:

      - CSharp: public/internal type and member declarations under
                src/PSXRecomp.Core/**/*.cs (the managed interop/wrapper
                surface named by the policy).
      - Cpp:    PSX_API-attributed function declarations in the public C ABI
                header src/PSXRecomp.Native/include/*.h.

    A symbol counts as documented when the nearest non-blank, non-attribute
    line immediately above its declaration is an XML doc-comment line (three
    slashes) or the closing line of a block comment.

.PARAMETER RepoRoot
    Repository root to scan. Defaults to the checkout this script lives in.

.PARAMETER CSharpPath
    One or more root paths (relative to RepoRoot) to scan for C# coverage.
    Defaults to src/PSXRecomp.Core.

.PARAMETER CppPath
    One or more root paths (relative to RepoRoot) to scan for C++ coverage.
    Defaults to src/PSXRecomp.Native/include.

.PARAMETER FailUnder
    Optional minimum coverage percentage (0-100). When set, the script exits
    1 if either measured category falls below it. Omit for report-only mode.

.EXAMPLE
    pwsh ./scripts/docs/measure-docstring-coverage.ps1

.LINK
    docs/development/documentation-policy.md

.LINK
    docs/adr/011-api-documentation-docstring-policy.md
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string[]]$CSharpPath = @("src/PSXRecomp.Core"),
    [string[]]$CppPath = @("src/PSXRecomp.Native/include"),
    [double]$FailUnder = -1
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath ".." "..")).Path
}

function Test-DocumentedAbove {
    param([string[]]$Lines, [int]$Index)

    for ($i = $Index - 1; $i -ge 0; $i--) {
        $line = $Lines[$i].Trim()
        if ($line -eq "") { continue }
        if ($line -match "^\[.*\]$") { continue }
        if ($line -match "^///") { return $true }
        if ($line -match "\*/$") { return $true }
        return $false
    }
    return $false
}

function Measure-CSharpCoverage {
    param([string]$Root)

    $files = Get-ChildItem -LiteralPath $Root -Filter "*.cs" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "[\/](bin|obj)[\/]" }

    $typePattern = "^\s*(public|internal)\s+(static\s+)?(sealed\s+)?(abstract\s+)?(partial\s+)?(class|struct|interface|record|enum)\s+\w+"
    $memberPattern = "^\s*(public|internal)\s+(static\s+)?(readonly\s+)?(const\s+)?(sealed\s+)?(virtual\s+)?(override\s+)?(async\s+)?(unsafe\s+)?(extern\s+)?(partial\s+)?[\w<>\[\],\.\?]+[\?\*]?\s+\w+\s*(\(|\{|=>|;)"
    $ctorPattern = "^\s*(public|internal)\s+([A-Z]\w*)\s*\("
    $dtorPattern = "^\s*~\w+\s*\("

    $total = 0
    $documented = 0
    $details = [System.Collections.Generic.List[string]]::new()

    foreach ($file in $files) {
        $lines = Get-Content -LiteralPath $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            $isSymbol = ($line -match $typePattern) -or ($line -match $memberPattern) -or
                        ($line -match $ctorPattern) -or ($line -match $dtorPattern)
            if (-not $isSymbol) { continue }

            $total++
            $doc = Test-DocumentedAbove -Lines $lines -Index $i
            if ($doc) {
                $documented++
            } else {
                $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $file.FullName) -replace "\\", "/"
                $details.Add(("{0}:{1}: {2}" -f $rel, ($i + 1), $line.Trim()))
            }
        }
    }

    [PSCustomObject]@{
        Total        = $total
        Documented   = $documented
        Undocumented = ($details.ToArray())
    }
}

function Measure-CppCoverage {
    param([string]$Root)

    $files = Get-ChildItem -LiteralPath $Root -Filter "*.h" -Recurse -File -ErrorAction SilentlyContinue

    $declPattern = "^\s*PSX_API\b"

    $total = 0
    $documented = 0
    $details = [System.Collections.Generic.List[string]]::new()

    foreach ($file in $files) {
        $lines = Get-Content -LiteralPath $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -notmatch $declPattern) { continue }

            $total++
            $doc = Test-DocumentedAbove -Lines $lines -Index $i
            if ($doc) {
                $documented++
            } else {
                $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $file.FullName) -replace "\\", "/"
                $details.Add(("{0}:{1}: {2}" -f $rel, ($i + 1), $line.Trim()))
            }
        }
    }

    [PSCustomObject]@{
        Total        = $total
        Documented   = $documented
        Undocumented = ($details.ToArray())
    }
}

function Format-Percent {
    param([int]$Documented, [int]$Total)
    if ($Total -eq 0) { return "n/a (0 symbols found)" }
    return ("{0:N2}%" -f (100.0 * $Documented / $Total))
}

$csharpResults = foreach ($p in $CSharpPath) {
    $full = Join-Path -Path $RepoRoot -ChildPath $p
    if (-not (Test-Path -LiteralPath $full)) {
        Write-Warning "C# scan path not found, skipping: $full"
        continue
    }
    Measure-CSharpCoverage -Root $full
}

$cppResults = foreach ($p in $CppPath) {
    $full = Join-Path -Path $RepoRoot -ChildPath $p
    if (-not (Test-Path -LiteralPath $full)) {
        Write-Warning "C++ scan path not found, skipping: $full"
        continue
    }
    Measure-CppCoverage -Root $full
}

$csTotal = [int](($csharpResults | Measure-Object -Property Total -Sum).Sum)
$csDoc = [int](($csharpResults | Measure-Object -Property Documented -Sum).Sum)
$csUndoc = @($csharpResults | ForEach-Object { $_.Undocumented })

$cppTotal = [int](($cppResults | Measure-Object -Property Total -Sum).Sum)
$cppDoc = [int](($cppResults | Measure-Object -Property Documented -Sum).Sum)
$cppUndoc = @($cppResults | ForEach-Object { $_.Undocumented })

Write-Host "PSXRecompStudio docstring coverage (heuristic, see docs/development/documentation-policy.md)"
Write-Host "=================================================================================="
Write-Host ("C#  ({0}): {1}/{2} documented ({3})" -f ($CSharpPath -join ", "), $csDoc, $csTotal, (Format-Percent -Documented $csDoc -Total $csTotal))
Write-Host ("C++ ({0}): {1}/{2} documented ({3})" -f ($CppPath -join ", "), $cppDoc, $cppTotal, (Format-Percent -Documented $cppDoc -Total $cppTotal))

if ($PSBoundParameters.ContainsKey("Verbose")) {
    if ($csUndoc.Count -gt 0) {
        Write-Host ""
        Write-Host "Undocumented C# symbols:"
        $csUndoc | ForEach-Object { Write-Host ("  {0}" -f $_) }
    }
    if ($cppUndoc.Count -gt 0) {
        Write-Host ""
        Write-Host "Undocumented C++ symbols:"
        $cppUndoc | ForEach-Object { Write-Host ("  {0}" -f $_) }
    }
}

if ($FailUnder -ge 0) {
    $csPct = if ($csTotal -eq 0) { 100.0 } else { 100.0 * $csDoc / $csTotal }
    $cppPct = if ($cppTotal -eq 0) { 100.0 } else { 100.0 * $cppDoc / $cppTotal }
    if ($csPct -lt $FailUnder -or $cppPct -lt $FailUnder) {
        Write-Host ("FAIL: coverage below threshold {0}%" -f $FailUnder)
        exit 1
    }
}

exit 0
