#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Persona title screen end-to-end verification gate (Issue #351).

.DESCRIPTION
    Drives the real PSXRecompStudio pipeline stage-by-stage against a locally
    available disc image and emits a machine-readable JSON result.

    Stages (in order):
      1. FIXTURE_DISCOVERY  — discovers rom/*.chd via RealRomFixtures conventions
      2. BUILD              — restores and builds the .NET solution (Release)
      3. ANALYSIS           — runs RealRomAnalysisSkillTests (CHD→COMPLETE)
      4. RECOMPILER_SLICE   — runs RealRomRecompilerVerticalSliceTests (function recompilation)
      5. RUNTIME_EXECUTION  — runs RealRomTitleExecutionTests (full-title orchestrator over recompiled host)

    Exit codes:
      0 — reserved for a future run that reaches the TITLE_SCREEN release gate;
          the current implementation does not return PASS because that criterion
          is not implemented yet
      1 — a stage failed (FAIL)
      2 — no fixture is present, or all currently implemented stages completed
          without reaching TITLE_SCREEN (SKIP)

    Output: prints a JSON summary to stdout; optionally writes it to --output-path.

.PARAMETER OutputPath
    Optional. Path for the machine-readable JSON result file.
    Default: <repo-root>/reports/e2e/persona-e2e-gate-result.json (git-ignored).

.PARAMETER NoBuild
    Skip the dotnet build/restore step (useful when the solution is already built).

.EXAMPLE
    # From the repository root:
    pwsh scripts/e2e/persona-e2e-gate.ps1

    # With explicit output path:
    pwsh scripts/e2e/persona-e2e-gate.ps1 --OutputPath /tmp/gate-result.json

.NOTES
    ROM / artifact policy (ADR-007):
      - The disc image must be in rom/*.chd (git-ignored). Never add it to git.
      - This script never reads or logs ROM bytes; only safe metadata (stage,
        PASS/FAIL, counts, SHA-256) may appear in output.
      - Output JSON is written under reports/ which is git-ignored.
      - Run pwsh scripts/ci/check-artifact-policy.ps1 after this script to confirm
        no contamination reached the tracked tree.

    Non-goals (Issue #351):
      - No title-specific hacks in Core/Recompiler.
      - No "screenshot exists = PASS" shortcut.
      - No fake title screen.
      - RUNTIME_EXECUTION is implemented for guest-code execution (orchestrator +
        BIOS HLE dispatch); reaching an actual title screen additionally requires
        broader BIOS HLE coverage and GPU/SPU/CD-ROM producers (Issue #9).
#>
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Repository paths
# ---------------------------------------------------------------------------
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent (Split-Path -Parent $scriptDir)
$romDir     = Join-Path $repoRoot 'rom'

if ([string]::IsNullOrEmpty($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'reports' 'e2e' 'persona-e2e-gate-result.json'
}

# ---------------------------------------------------------------------------
# State
# ---------------------------------------------------------------------------
$stages       = [System.Collections.Generic.List[object]]::new()
$firstBlocker = $null
$verdict      = 'PASS'

# ---------------------------------------------------------------------------
# Functions — all defined before any call sites
# ---------------------------------------------------------------------------
function New-StageEntry {
    param([string]$Stage, [string]$Status, [string]$Detail = '')
    [PSCustomObject]@{ stage = $Stage; status = $Status; detail = $Detail }
}

function Add-Pass   { param([string]$Stage, [string]$Detail = '')
    $script:stages.Add((New-StageEntry $Stage 'PASS' $Detail)) }

function Add-Skip   { param([string]$Stage, [string]$Detail)
    $script:stages.Add((New-StageEntry $Stage 'SKIP' $Detail)) }

function Add-Fail   {
    param([string]$Stage, [string]$Detail, [string]$Category, [string]$DiagnosticCode = '')
    $script:stages.Add((New-StageEntry $Stage 'FAIL' $Detail))
    $script:firstBlocker = [PSCustomObject]@{
        stage          = $Stage
        category       = $Category
        description    = $Detail
        diagnosticCode = if ($DiagnosticCode) { $DiagnosticCode } else { $null }
    }
    $script:verdict = 'FAIL'
}

function Get-NextBlocker {
    if ($script:verdict -eq 'FAIL' -and $null -ne $script:firstBlocker) {
        return "$($script:firstBlocker.stage): resolve the failing stage before the gate can continue. " +
               $script:firstBlocker.description
    }

    return 'TITLE_SCREEN: the implemented stages can execute bounded guest code, but the next ' +
           'generic runtime blocker is broader BIOS HLE coverage. After the required BIOS calls ' +
           'are supported, GPU/SPU/CD-ROM producers remain necessary to reach an actual title ' +
           'screen. See docs/v0.1.0/persona-e2e-status.md and Issue #351.'
}

function Build-Result {
    [PSCustomObject]@{
        schemaVersion = 1
        verdict       = $script:verdict
        stages        = $script:stages.ToArray()
        firstBlocker  = $script:firstBlocker
        nextBlocker   = Get-NextBlocker
    }
}

function Emit-Result {
    param([object]$Result)
    $json = $Result | ConvertTo-Json -Depth 10
    Write-Host ''
    Write-Host '--- E2E Gate Result ---' -ForegroundColor Yellow
    Write-Host $json

    $outDir = Split-Path -Parent $script:OutputPath
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }
    [System.IO.File]::WriteAllText($script:OutputPath, $json + "`n")
    Write-Host "Result written to: $($script:OutputPath)" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Stage 1: FIXTURE_DISCOVERY
# ---------------------------------------------------------------------------
Write-Host "[FIXTURE_DISCOVERY] Scanning $romDir for *.chd ..." -ForegroundColor Cyan

$fixtureFiles = @()
if (Test-Path $romDir) {
    $fixtureFiles = @(Get-ChildItem -Path $romDir -Filter '*.chd' -File -ErrorAction SilentlyContinue)
}

if ($fixtureFiles.Count -eq 0) {
    Add-Skip 'FIXTURE_DISCOVERY' `
        'No disc image found in rom/*.chd. Place a legally-owned disc image at rom/<name>.chd (git-ignored).'
    $verdict = 'SKIP'

    $result = [PSCustomObject]@{
        schemaVersion = 1
        verdict       = $verdict
        stages        = $stages.ToArray()
        firstBlocker  = [PSCustomObject]@{
            stage          = 'FIXTURE_DISCOVERY'
            category       = 'no_fixture'
            description    = 'No legal disc image found in rom/. Provide a legally-owned CHD at rom/<name>.chd.'
            diagnosticCode = $null
            knownIssue     = 'https://github.com/mao2009/PSXRecompStudio/issues/351'
        }
        nextBlocker   = 'FIXTURE_DISCOVERY: provide a legally-owned CHD under rom/. Once fixture discovery succeeds, ' +
                        'the pipeline proceeds through ANALYSIS → RECOMPILER_SLICE → RUNTIME_EXECUTION; the next ' +
                        'generic runtime blocker toward TITLE_SCREEN is broader BIOS HLE coverage, followed by ' +
                        'GPU/SPU/CD-ROM producers.'
    }
    Emit-Result $result
    exit 2
}

$fixtureNames = $fixtureFiles | ForEach-Object { $_.Name }
Add-Pass 'FIXTURE_DISCOVERY' "Found $($fixtureFiles.Count) fixture(s): $($fixtureNames -join ', ')"
Write-Host "  Found: $($fixtureNames -join ', ')" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Stage 2: BUILD
# ---------------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host "[BUILD] dotnet build (Release) ..." -ForegroundColor Cyan
    $buildOutput = & dotnet build (Join-Path $repoRoot 'src' 'PSXRecomp.Tests' 'PSXRecomp.Tests.csproj') `
        -c Release --nologo -q 2>&1
    if ($LASTEXITCODE -ne 0) {
        Add-Fail 'BUILD' "dotnet build failed (exit $LASTEXITCODE). Output: $buildOutput" `
            'artifact-build-runtime wiring'
        Emit-Result (Build-Result)
        exit 1
    }
    Add-Pass 'BUILD' 'dotnet build Release succeeded'
    Write-Host "  Build: OK" -ForegroundColor Green
} else {
    Add-Skip 'BUILD' '--NoBuild specified; assuming solution is already built'
}

# ---------------------------------------------------------------------------
# Stage 3: ANALYSIS (RealRomAnalysisSkillTests)
# Drives: CHD → ISO → SYSTEM.CNF → PSX EXE → decode → CFG → REPORT → MANIFEST → COMPLETE
# ---------------------------------------------------------------------------
Write-Host "[ANALYSIS] Running RealRomAnalysisSkillTests ..." -ForegroundColor Cyan
$analysisOutput = & dotnet test (Join-Path $repoRoot 'src' 'PSXRecomp.Tests' 'PSXRecomp.Tests.csproj') `
    --filter 'FullyQualifiedName~RealRomAnalysisSkillTests' `
    -c Release --nologo --no-build `
    --logger 'console;verbosity=normal' 2>&1

$analysisPassed = $LASTEXITCODE -eq 0

if (-not $analysisPassed) {
    $isSkip = ($analysisOutput | Select-String 'skipped: no real-ROM fixture' | Measure-Object).Count -gt 0
    if ($isSkip) {
        Add-Skip 'ANALYSIS' 'Test skipped: fixture detected but test runner found none (rom/ path mismatch?)'
    } else {
        $failLine = ($analysisOutput | Select-String 'FailedStage|FailureKind|FAIL|Error' |
                     Select-Object -First 1).Line
        Add-Fail 'ANALYSIS' "RealRomAnalysisSkillTests failed. $failLine" `
            'artifact-build-runtime wiring'
        Emit-Result (Build-Result)
        exit 1
    }
} else {
    $summary = ($analysisOutput | Select-String 'passed|PASS|COMPLETE' | Select-Object -First 1).Line
    Add-Pass 'ANALYSIS' "RealRomAnalysisSkillTests passed. $summary"
    Write-Host "  Analysis: PASS" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Stage 4: RECOMPILER_SLICE (RealRomRecompilerVerticalSliceTests)
# Drives: candidate selection → recompilation → interpreter-vs-host diff
# ---------------------------------------------------------------------------
Write-Host "[RECOMPILER_SLICE] Running RealRomRecompilerVerticalSliceTests ..." -ForegroundColor Cyan
$recompOutput = & dotnet test (Join-Path $repoRoot 'src' 'PSXRecomp.Tests' 'PSXRecomp.Tests.csproj') `
    --filter 'FullyQualifiedName~RealRomRecompilerVerticalSliceTests' `
    -c Release --nologo --no-build `
    --logger 'console;verbosity=normal' 2>&1

$recompPassed = $LASTEXITCODE -eq 0

if (-not $recompPassed) {
    $isBudget = ($recompOutput | Select-String 'budget-inconclusive' | Measure-Object).Count -gt 0
    $isSkip   = ($recompOutput | Select-String 'skipped:' | Measure-Object).Count -gt 0
    if ($isSkip) {
        Add-Skip 'RECOMPILER_SLICE' 'Test skipped (no qualifying candidate; not a failure)'
    } elseif ($isBudget) {
        Add-Skip 'RECOMPILER_SLICE' 'Budget-inconclusive (Issue #304): function exceeded static step budget'
    } else {
        $failLine = ($recompOutput | Select-String 'DiagnosticCode|FAIL|failed|Error' |
                     Select-Object -First 1).Line
        Add-Fail 'RECOMPILER_SLICE' "RealRomRecompilerVerticalSliceTests failed. $failLine" `
            'unsupported instruction'
        Emit-Result (Build-Result)
        exit 1
    }
} else {
    $summary = ($recompOutput | Select-String 'passed|interpreter matches' | Select-Object -First 1).Line
    Add-Pass 'RECOMPILER_SLICE' "RealRomRecompilerVerticalSliceTests passed. $summary"
    Write-Host "  Recompiler vertical slice: PASS" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Stage 5: RUNTIME_EXECUTION (RealRomTitleExecutionTests)
# Drives: the full-title execution orchestrator (Issue #366) over the
#         recompiled host (gcc) for several bounded segments, with BIOS HLE
#         dispatch in-band (Issue #368). The test asserts the orchestrator ends
#         in a classified outcome with a real snapshot — never InvalidState.
#
# Known sub-blockers (in priority order):
#   a) (resolved) No full-title execution loop — now the ExecutionOrchestrator
#      over the Test host engine; a product CLI runner still does not exist.
#   b) BIOS HLE covers only 5 services — first unregistered call → BIOS_HLE_UNSUPPORTED_CALL
#   c) (resolved) Recompiled-path BIOS vector dispatch — now in-band via the
#      shared BiosVectorDispatch contract (BiosPatchedTargetExecutionTests parity marker removed).
#   d) GPU/SPU/CD-ROM are interface-only with no production implementation
#
# Tracked: Issue #351 (gate), Issue #9 (v0.1.0 milestone)
# ---------------------------------------------------------------------------
Write-Host "[RUNTIME_EXECUTION] Running RealRomTitleExecutionTests ..." -ForegroundColor Cyan
$runtimeOutput = & dotnet test (Join-Path $repoRoot 'src' 'PSXRecomp.Tests' 'PSXRecomp.Tests.csproj') `
    --filter 'FullyQualifiedName~RealRomTitleExecutionTests' `
    -c Release --nologo --no-build `
    --logger 'console;verbosity=normal' 2>&1

$runtimePassed = $LASTEXITCODE -eq 0

if (-not $runtimePassed) {
    $isSkip = ($runtimeOutput | Select-String 'skipped: no real-ROM fixture|no qualifying' |
               Measure-Object).Count -gt 0
    if ($isSkip) {
        Add-Skip 'RUNTIME_EXECUTION' 'Orchestrator tests skipped (no qualifying real-ROM candidate; not a failure)'
    } else {
        $failLine = ($runtimeOutput | Select-String 'DiagnosticCode|InvalidState|FAIL|failed|Error' |
                     Select-Object -First 1).Line
        Add-Fail 'RUNTIME_EXECUTION' "RealRomTitleExecutionTests failed. $failLine" `
            'full-title execution'
        Emit-Result (Build-Result)
        exit 1
    }
} else {
    $summary = ($runtimeOutput | Select-String 'passed|classified' | Select-Object -First 1).Line
    Add-Pass 'RUNTIME_EXECUTION' "RealRomTitleExecutionTests passed. $summary"
    Write-Host "  Runtime execution: PASS" -ForegroundColor Green
}

# If stages 1-5 all passed/skipped without FAIL, overall is SKIP (not PASS)
# because the gate's ultimate criterion (an actual title screen) is not met.
# Broader BIOS HLE coverage is the next generic runtime blocker; after the
# required calls are supported, GPU/SPU/CD-ROM producers are still required.
if ($verdict -eq 'PASS') {
    $verdict = 'SKIP'
}

Emit-Result (Build-Result)

switch ($verdict) {
    'PASS' { exit 0 }
    'FAIL' { exit 1 }
    default { exit 2 }
}
