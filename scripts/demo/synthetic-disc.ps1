<#
.SYNOPSIS
  Deterministic synthetic Mode 2 Form 1 CD image generator (Issue #732, Phase 2).

.DESCRIPTION
  Builds a bootable, copyright-free disc entirely from first principles: a SYSTEM.CNF
  and a synthetic PS-X EXE inside an ISO 9660 volume, every 2048-byte user-data sector
  wrapped in a raw 2352-byte Mode 2 Form 1 frame. The generator is a pure function of its
  inputs, so it reproduces the same bytes on every machine; the SHA-256 below is the
  checked contract shared with
  src/PSXRecomp.Tests/RealRomAnalysis/SyntheticDiscReproducibilityTests.cs, which builds
  the same disc through SyntheticDiscBuilder and asserts the same value.

  No BIOS, commercial executable, ROM or disc image is read or written. The output goes to
  a temporary directory by default and is never committed.

.PARAMETER WorkDir
  Output directory (default: a temp directory). Nothing is written into the repository.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'psxrecomp-synthetic-disc')
)
$ErrorActionPreference = 'Stop'

# --- constants shared with src/PSXRecomp.Tests/RealRomAnalysis/SyntheticDiscBuilder.cs ----
$SectorSize = 2048
$RawSector = 2352
$PvdSector = 16
$RootSector = 18
$FirstFile = 20
$BootPath = 'cdrom:\PSXRECOMP.EXE;1'
$ExeIsoName = 'PSXRECOMP.EXE;1'
$ExpectedSha256 = '38243c8aadf5f44b1aaa8b8084df2b92bf39027e46f5f42d47fd309477a36522'

# --- 1. the boot PS-X EXE: the checked BiosPutCharMarker fixture, built from first principles ----
function Imm([int]$op, [int]$rt, [int]$imm) { [uint32](($op -shl 26) -bor ($rt -shl 16) -bor $imm) }
$words = @(
    (Imm 0x0D 9 0x3C),                                   # ori  $t1,$zero,0x3C   (putchar)
    (Imm 0x0D 4 ([int][char]'P')),                       # ori  $a0,$zero,'P'
    [uint32]((3 -shl 26) -bor (0xA0 -shr 2)),            # jal  0xA0             (BIOS A0 vector)
    [uint32]0,                                           # nop  (delay slot)
    (Imm 0x0D 17 0x7777)                                 # ori  $s1,$zero,0x7777 (marker)
)
$exe = New-Object byte[] (0x800 + 4 * $words.Count)
[Text.Encoding]::ASCII.GetBytes('PS-X EXE').CopyTo($exe, 0)
foreach ($field in @(@(0x10, 0x80010000L), @(0x18, 0x80010000L), @(0x1C, (4 * $words.Count)), @(0x30, 0x801FFF00L))) {
    [BitConverter]::GetBytes([uint32]$field[1]).CopyTo($exe, [int]$field[0])
}
for ($i = 0; $i -lt $words.Count; $i++) { [BitConverter]::GetBytes([uint32]$words[$i]).CopyTo($exe, 0x800 + 4 * $i) }

# --- 2. ISO 9660 volume (matches SyntheticIsoImageBuilder) ----
$systemCnf = [Text.Encoding]::ASCII.GetBytes("BOOT = $BootPath`r`nTCB = 4`r`nEVENT = 10`r`nSTACK = 801FFFF0`r`n")
$files = @(
    @{ Name = 'SYSTEM.CNF;1'; Bytes = $systemCnf },
    @{ Name = $ExeIsoName; Bytes = $exe }
)
$cursor = $FirstFile
foreach ($file in $files) {
    $file.Location = $cursor
    $cursor += [Math]::Max(1, [int][Math]::Ceiling($file.Bytes.Length / $SectorSize))
}
$iso = New-Object byte[] ($cursor * $SectorSize)

function WriteU32([byte[]]$buffer, [int]$offset, [uint32]$value) {
    [BitConverter]::GetBytes($value).CopyTo($buffer, $offset)
}
function WriteDirRecord([byte[]]$buffer, [int]$offset, [uint32]$location, [uint32]$size, [byte]$flags, [byte[]]$name) {
    $recordLength = 33 + $name.Length
    if ($recordLength % 2 -ne 0) { $recordLength++ }
    $buffer[$offset] = [byte]$recordLength
    $buffer[$offset + 1] = 0
    WriteU32 $buffer ($offset + 2) $location
    WriteU32 $buffer ($offset + 10) $size
    $buffer[$offset + 25] = $flags
    $buffer[$offset + 32] = [byte]$name.Length
    $name.CopyTo($buffer, $offset + 33)
    return $recordLength
}
$pvd = $PvdSector * $SectorSize
$iso[$pvd] = 1
[Text.Encoding]::ASCII.GetBytes('CD001').CopyTo($iso, $pvd + 1)
$iso[$pvd + 6] = 1
[Text.Encoding]::ASCII.GetBytes('PSXRECOMP_TEST'.PadRight(32).Substring(0, 32)).CopyTo($iso, $pvd + 40)
WriteU32 $iso ($pvd + 80) ([uint32]$cursor)
WriteDirRecord $iso ($pvd + 156) ([uint32]$RootSector) ([uint32]$SectorSize) ([byte]0x02) ([byte[]]@(0)) | Out-Null
$root = $RootSector * $SectorSize
$root += WriteDirRecord $iso $root ([uint32]$RootSector) ([uint32]$SectorSize) ([byte]0x02) ([byte[]]@(0))
foreach ($file in $files) {
    $root += WriteDirRecord $iso $root ([uint32]$file.Location) ([uint32]$file.Bytes.Length) ([byte]0x00) ([Text.Encoding]::ASCII.GetBytes($file.Name))
}
foreach ($file in $files) {
    $file.Bytes.CopyTo($iso, [int]$file.Location * $SectorSize)
}

# --- 3. wrap in Mode 2 Form 1 raw sectors ---
function Bcd([int]$value) { [byte](([int][Math]::Floor($value / 10) -shl 4) -bor ($value % 10)) }
$disc = New-Object byte[] ($cursor * $RawSector)
for ($lba = 0; $lba -lt $cursor; $lba++) {
    $base = $lba * $RawSector
    for ($i = 1; $i -le 10; $i++) { $disc[$base + $i] = 0xFF }
    $absolute = $lba + 150
    $disc[$base + 12] = Bcd ([Math]::Floor($absolute / 4500))
    $disc[$base + 13] = Bcd ([Math]::Floor($absolute / 75) % 60)
    $disc[$base + 14] = Bcd ($absolute % 75)
    $disc[$base + 15] = 2
    $disc[$base + 18] = 0x08
    $disc[$base + 22] = 0x08
    [Array]::Copy($iso, $lba * $SectorSize, $disc, $base + 24, $SectorSize)
}

# --- 4. write and verify the checked contract ---
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$path = Join-Path $WorkDir 'synthetic-disc.bin'
[IO.File]::WriteAllBytes($path, $disc)
$sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower()
Write-Host "Generated $path ($($disc.Length) bytes, SHA-256 $sha)"
if ($sha -ne $ExpectedSha256) { throw "unexpected synthetic disc hash: $sha" }
Write-Host 'SYNTHETIC DISC OK: deterministic Mode 2 Form 1 disc matches the checked contract.' -ForegroundColor Green
