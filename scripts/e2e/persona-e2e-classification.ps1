Set-StrictMode -Version Latest

function ConvertTo-PersonaE2ECategoryToken {
    param([Parameter(Mandatory)][string]$Value)

    $token = $Value -replace '([a-z0-9])([A-Z])', '$1_$2'
    $token = $token -replace '[^A-Za-z0-9]+', '_'
    return $token.Trim('_').ToLowerInvariant()
}

function Get-PersonaE2EFailureClassification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$Output
    )

    $text = if ($null -eq $Output) {
        ''
    } else {
        ($Output | ForEach-Object { "$_" }) -join "`n"
    }
    $diagnosticCode = $null

    $diagnosticMatch = [regex]::Match(
        $text,
        '(?im)\bDiagnosticCode\b\s*[:=]\s*\[?([A-Za-z0-9_.-]+)\]?'
    )
    if (-not $diagnosticMatch.Success) {
        $diagnosticMatch = [regex]::Match(
            $text,
            '(?im)\bDiagnostic\b\s*:\s*\[([A-Za-z0-9_.-]+)\]'
        )
    }

    if ($diagnosticMatch.Success) {
        $diagnosticCode = $diagnosticMatch.Groups[1].Value
        $token = ConvertTo-PersonaE2ECategoryToken $diagnosticCode
        return [PSCustomObject]@{
            Category       = "diagnostic_$token"
            DiagnosticCode = $diagnosticCode
        }
    }

    $failureKindMatch = [regex]::Match(
        $text,
        '(?im)\bFailureKind\b\s*[:=]\s*\[?([A-Za-z0-9_.-]+)\]?'
    )
    if ($failureKindMatch.Success) {
        $token = ConvertTo-PersonaE2ECategoryToken $failureKindMatch.Groups[1].Value
        return [PSCustomObject]@{
            Category       = "failure_kind_$token"
            DiagnosticCode = $null
        }
    }

    if ($text -match '(?im)\bMismatch\b|interpreter.+(?:!=|does not match|mismatch)|host.+(?:!=|does not match|mismatch)') {
        return [PSCustomObject]@{
            Category       = 'differential_mismatch'
            DiagnosticCode = $null
        }
    }

    if ($text -match '(?im)unsupported (?:instruction|opcode)|instruction.+not supported') {
        return [PSCustomObject]@{
            Category       = 'unsupported_instruction'
            DiagnosticCode = $null
        }
    }

    if ($text -match '(?im)\bInvalidState\b') {
        return [PSCustomObject]@{
            Category       = 'runtime_invalid_state'
            DiagnosticCode = $null
        }
    }

    if ($Stage -eq 'BUILD' -or
        $text -match '(?im)\b(?:MSB\d+|CS\d{4}|build failed|restore failed|compilation failed)\b') {
        return [PSCustomObject]@{
            Category       = 'build_failure'
            DiagnosticCode = $null
        }
    }

    $stageToken = ConvertTo-PersonaE2ECategoryToken $Stage
    return [PSCustomObject]@{
        Category       = "${stageToken}_failure"
        DiagnosticCode = $null
    }
}
