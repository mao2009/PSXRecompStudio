<#
.SYNOPSIS
  Reproducible showcase demo (Issue #624, slice of #505): synthetic PS-X EXE ->
  `psxrecomp recompile` -> `psxrecomp run` -> deterministic result.

.DESCRIPTION
  The 5-instruction PS-X EXE is generated at run time from first principles, so no
  executable, ROM, BIOS or commercial asset is ever stored in the repository. It
  mirrors the synthetic fixture in src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs:
  putchar('P') through the BIOS A0 vector, then S1 = 0x7777.

  Requires gcc on PATH (the generated-host build invokes it).

.PARAMETER Psxrecomp
  Path to a released `psxrecomp` binary. When omitted, the CLI is built and run from
  source with `dotnet run` (Release).

.PARAMETER WorkDir
  Output directory (default: a temp directory; nothing is written into the repository).
#>
param(
    [string]$Psxrecomp,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'psxrecomp-demo')
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '../..')

function Invoke-Cli {
    if ($Psxrecomp) { & $Psxrecomp @args }
    else { & dotnet run --project (Join-Path $repo 'src/PSXRecomp.Cli') --configuration Release -- @args }
}

# --- 1. Generate the synthetic PS-X EXE (2048-byte header + 5 MIPS words) ---------
function Imm([int]$op, [int]$rt, [int]$imm) { [uint32](($op -shl 26) -bor ($rt -shl 16) -bor $imm) }
$words = @(
    (Imm 0x0D 9 0x3C),                              # ori  $t1,$zero,0x3C  (putchar)
    (Imm 0x0D 4 ([int][char]'P')),                  # ori  $a0,$zero,'P'
    [uint32]((3 -shl 26) -bor (0xA0 -shr 2)),       # jal  0xA0            (BIOS A0 vector)
    [uint32]0,                                      # nop  (delay slot)
    (Imm 0x0D 17 0x7777)                            # ori  $s1,$zero,0x7777 (marker)
)
$bytes = New-Object byte[] (2048 + 4 * $words.Count)
[Text.Encoding]::ASCII.GetBytes('PS-X EXE').CopyTo($bytes, 0)
$entry = 0x80010000u
foreach ($f in @(@(0x10, $entry), @(0x18, $entry), @(0x1C, (4 * $words.Count)), @(0x30, 0x801FFF00u))) {
    [BitConverter]::GetBytes([uint32]$f[1]).CopyTo($bytes, $f[0])
}
for ($i = 0; $i -lt $words.Count; $i++) { [BitConverter]::GetBytes([uint32]$words[$i]).CopyTo($bytes, 2048 + 4 * $i) }

New-Item -ItemType Directory -Force $WorkDir | Out-Null
$exe = Join-Path $WorkDir 'synthetic.exe'
[IO.File]::WriteAllBytes($exe, $bytes)
$sha = (Get-FileHash $exe).Hash.ToLower()
Write-Host "Generated synthetic input: synthetic.exe ($($bytes.Length) bytes, SHA-256 $sha)"
# Same bytes as GeneratedPsxExeFixtures.BiosPutCharMarker (src/PSXRecomp.Tests/E2E).
if ($sha -ne 'a863090b04d20a9f790c4cf533dd13edf5a222f3852c6178f28605a8c910ae6e') { throw 'unexpected input hash' }

# --- 2. recompile ------------------------------------------------------------------
Write-Host "`n`$ psxrecomp recompile synthetic.exe --output out --json"
$rc = Invoke-Cli recompile $exe --output (Join-Path $WorkDir 'out') --json | ConvertFrom-Json
$rc | Select-Object kind, success, status | Format-List | Out-String | Write-Host
if (-not $rc.success) { throw 'recompile failed' }

# --- 3. run (twice, to show determinism) --------------------------------------------
Write-Host "`$ psxrecomp run synthetic.exe --output out --json"
$runs = 1..2 | ForEach-Object { Invoke-Cli run $exe --output (Join-Path $WorkDir 'out') --json | ConvertFrom-Json }
$r = $runs[0]
$tty = [Text.Encoding]::ASCII.GetString([byte[]]$r.output)
"kind=$($r.kind) success=$($r.success) engine=$($r.result.engineName) exitCode=$($r.result.exitCode) tty='$tty'" | Write-Host

# --- 4. deterministic-result check ----------------------------------------------------
$same = ($runs[0] | ConvertTo-Json -Depth 8 -Compress) -eq ($runs[1] | ConvertTo-Json -Depth 8 -Compress)
if (-not ($r.success -and $tty -eq 'P' -and $r.result.engineName -eq 'recompiled-host-artifact' -and $same)) {
    Write-Host 'DEMO FAIL' -ForegroundColor Red; exit 1
}
Write-Host 'DEMO OK: generated code ran; TTY "P"; repeated run identical.' -ForegroundColor Green
