#!/usr/bin/env pwsh

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'persona-e2e-classification.ps1')

$cases = @(
    @{
        Name           = 'explicit diagnostic code'
        Stage          = 'RECOMPILER_SLICE'
        Output         = @('DiagnosticCode: InvalidFlow')
        Category       = 'diagnostic_invalid_flow'
        DiagnosticCode = 'InvalidFlow'
    },
    @{
        Name           = 'bracketed diagnostic'
        Stage          = 'RECOMPILER_SLICE'
        Output         = @('Diagnostic: [UnsupportedInput] bad input')
        Category       = 'diagnostic_unsupported_input'
        DiagnosticCode = 'UnsupportedInput'
    },
    @{
        Name           = 'analysis failure kind'
        Stage          = 'ANALYSIS'
        Output         = @('FailureKind = InvalidExecutable')
        Category       = 'failure_kind_invalid_executable'
        DiagnosticCode = $null
    },
    @{
        Name           = 'differential mismatch'
        Stage          = 'RECOMPILER_SLICE'
        Output         = @('Mismatch: interpreter does not match generated host result')
        Category       = 'differential_mismatch'
        DiagnosticCode = $null
    },
    @{
        Name           = 'unsupported instruction fallback'
        Stage          = 'RECOMPILER_SLICE'
        Output         = @('instruction at PC 0x80012170 is not supported')
        Category       = 'unsupported_instruction'
        DiagnosticCode = $null
    },
    @{
        Name           = 'build failure'
        Stage          = 'BUILD'
        Output         = @('error CS1002: ; expected')
        Category       = 'build_failure'
        DiagnosticCode = $null
    },
    @{
        Name           = 'runtime invalid state'
        Stage          = 'RUNTIME_EXECUTION'
        Output         = @('Execution stopped with InvalidState')
        Category       = 'runtime_invalid_state'
        DiagnosticCode = $null
    },
    @{
        Name           = 'stage fallback'
        Stage          = 'ANALYSIS'
        Output         = @('unexpected test failure')
        Category       = 'analysis_failure'
        DiagnosticCode = $null
    }
)

foreach ($case in $cases) {
    $actual = Get-PersonaE2EFailureClassification -Stage $case.Stage -Output $case.Output

    if ($actual.Category -ne $case.Category) {
        throw "$($case.Name): expected category '$($case.Category)', got '$($actual.Category)'"
    }

    if ($actual.DiagnosticCode -ne $case.DiagnosticCode) {
        throw "$($case.Name): expected DiagnosticCode '$($case.DiagnosticCode)', got '$($actual.DiagnosticCode)'"
    }
}

Write-Host "PASS: $($cases.Count) Persona E2E failure-classification cases"
