<#
.SYNOPSIS
  Deterministically renders a captured synthetic-demo transcript into a small SVG
  (pipeline banner + terminal card) for README / site / announcement reuse (#505).

.DESCRIPTION
  Pure text transform: same transcript in -> byte-identical SVG out (no timestamps,
  no embedded fonts, LF endings). Needs no recording tool.

  Capture a transcript with:  pwsh scripts/demo/synthetic-demo.ps1 *> transcript.txt
  Default input is the committed capture docs/assets/demo/synthetic-demo-output.txt.
#>
param(
    [string]$Transcript = (Join-Path $PSScriptRoot '../../docs/assets/demo/synthetic-demo-output.txt'),
    [string]$Out = (Join-Path $PSScriptRoot '../../docs/assets/demo/synthetic-demo.svg')
)
$ErrorActionPreference = 'Stop'
function Esc($s) { [Security.SecurityElement]::Escape($s) }

$lines = @((Get-Content $Transcript) | ForEach-Object { $_.TrimEnd() })
while ($lines.Count -gt 0 -and -not $lines[-1]) { $lines = @($lines[0..($lines.Count - 2)]) }

$stages = 'PS-X EXE', 'MIPS analysis', 'IR / lowering', 'Host code', 'Native artifact', 'Execution'
$w = 960; $boxW = 140; $gap = 24; $lh = 20
$termTop = 128; $h = $termTop + 24 + $lh * $lines.Count + 56
$sb = [Text.StringBuilder]::new()
[void]$sb.Append("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$w`" height=`"$h`" viewBox=`"0 0 $w $h`" role=`"img`" aria-labelledby=`"t`">`n")
[void]$sb.Append("<title id=`"t`">PSXRecompStudio synthetic demo: pipeline and terminal output</title>`n")
[void]$sb.Append("<rect width=`"$w`" height=`"$h`" fill=`"#0d1117`"/>`n")
[void]$sb.Append("<text x=`"24`" y=`"34`" fill=`"#e6edf3`" font-family=`"sans-serif`" font-size=`"20`" font-weight=`"bold`">PSXRecompStudio: synthetic PS-X EXE, recompiled and run</text>`n")
for ($i = 0; $i -lt $stages.Count; $i++) {
    $x = 24 + $i * ($boxW + $gap)
    [void]$sb.Append("<rect x=`"$x`" y=`"56`" width=`"$boxW`" height=`"44`" rx=`"6`" fill=`"#161b22`" stroke=`"#3fb950`"/>`n")
    [void]$sb.Append("<text x=`"$($x + $boxW / 2)`" y=`"83`" text-anchor=`"middle`" fill=`"#e6edf3`" font-family=`"sans-serif`" font-size=`"14`">$(Esc $stages[$i])</text>`n")
    if ($i -lt $stages.Count - 1) { [void]$sb.Append("<text x=`"$($x + $boxW + $gap / 2)`" y=`"83`" text-anchor=`"middle`" fill=`"#8b949e`" font-family=`"sans-serif`" font-size=`"16`">&#8594;</text>`n") }
}
[void]$sb.Append("<rect x=`"24`" y=`"$termTop`" width=`"$($w - 48)`" height=`"$($h - $termTop - 40)`" rx=`"8`" fill=`"#010409`" stroke=`"#30363d`"/>`n")
for ($i = 0; $i -lt $lines.Count; $i++) {
    $c = if ($lines[$i].StartsWith('$')) { '#79c0ff' } elseif ($lines[$i] -like 'DEMO OK*') { '#3fb950' } else { '#e6edf3' }
    [void]$sb.Append("<text x=`"40`" y=`"$($termTop + 26 + $i * $lh)`" fill=`"$c`" font-family=`"monospace`" font-size=`"13`" xml:space=`"preserve`">$(Esc $lines[$i])</text>`n")
}
[void]$sb.Append("<text x=`"24`" y=`"$($h - 16)`" fill=`"#8b949e`" font-family=`"sans-serif`" font-size=`"12`">Synthetic repository-authored input (5 MIPS instructions). Not a commercial-title compatibility claim. Rendered from a captured transcript.</text>`n")
[void]$sb.Append("</svg>`n")
[IO.File]::WriteAllText($Out, $sb.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $Out ($((Get-Item $Out).Length) bytes)"
