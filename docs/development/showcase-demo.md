# Synthetic Showcase Demo

**Status:** Experimental

**Authority:** Reference

**Related Issues:** #505, #624

**Related Components:** `scripts/demo/synthetic-demo.ps1`, `docs/assets/demo/`, `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs`

A reproducible walk through the current pipeline using **only synthetic input**:

```text
synthetic PS-X EXE -> psxrecomp recompile -> generated host artifact -> psxrecomp run -> deterministic result
```

This is a proof that the pipeline works end to end on a five-instruction
repository-authored program. It is **not** a compatibility claim: complete
commercial PS1 titles are not recompilable today (see the README limitations).

## The input

The script generates the PS-X EXE at run time; nothing is committed (the
[artifact policy](artifact-policy.md) forbids any PS-X EXE in the tree). The
bytes are identical to the `bios-putchar-marker` fixture in
`GeneratedPsxExeFixtures` (SHA-256
`a863090b04d20a9f790c4cf533dd13edf5a222f3852c6178f28605a8c910ae6e`, checked by
the script). The program calls BIOS `putchar('P')` through the A0 vector, sets
`$s1 = 0x7777`, and runs off the end of the image.

## Run it

Prerequisites: PowerShell 7 (`pwsh`) and `gcc` on `PATH` (the generated-host
build invokes it), plus either a [released `psxrecomp`](https://github.com/mao2009/PSXRecompStudio/releases/latest) or the .NET SDK to build from source.

```bash
# From a release binary
pwsh scripts/demo/synthetic-demo.ps1 -Psxrecomp ./psxrecomp

# From source (builds the CLI in Release)
pwsh scripts/demo/synthetic-demo.ps1
```

The script writes into a temp directory (`-WorkDir` to override), runs
`recompile`, then runs `run --json` twice, and exits `0` only if the run
succeeded on the `recompiled-host-artifact` engine, the guest wrote exactly
`P` to the TTY, and both runs produced identical JSON.

## Expected output

Committed as [`docs/assets/demo/synthetic-demo-output.txt`](../assets/demo/synthetic-demo-output.txt)
(captured from a real run of the script):

```text
Generated synthetic input: synthetic.exe (2068 bytes, SHA-256 a863090b04d20a9f790c4cf533dd13edf5a222f3852c6178f28605a8c910ae6e)

$ psxrecomp recompile synthetic.exe --output out --json

kind    : recompile
success : True
status  : Succeeded


$ psxrecomp run synthetic.exe --output out --json
kind=run success=True engine=recompiled-host-artifact exitCode=0 tty='P'
DEMO OK: generated code ran; TTY "P"; repeated run identical.
```

## Verified against a published release

The script was run against the released Windows binary
`psxrecomp-v0.0.0-alpha.2-win-x64.zip` (the latest release when checked,
2026-09-29; archive checked against the release `SHA256SUMS.txt`) with
`pwsh scripts/demo/synthetic-demo.ps1 -Psxrecomp <path>\psxrecomp.exe` and `gcc`
(MinGW-w64) on `PATH`. It exited `0`, and its output was identical to the
committed capture above, including `engine=recompiled-host-artifact` and TTY
`P`. The Linux and macOS archives were not run for this check.

## Visual recording (not yet produced)

No image or video is committed: none is generated in CI, and no capture
dependency was added. A real terminal recording or screenshot of the run above
is still to be made and is tracked in #505. To produce one, run the script in a
terminal and record it with any local tool (for example `asciinema rec` or your
OS screen recorder), then share the result together with the limitation note
above. Record where it came from (tool, script revision, release version) next
to the asset. Do not include any ROM, BIOS, or commercial-title footage.

## Not covered

Real-title input, `--report` diagnostic bundles, and CHD input are documented in
the [headless CLI reference](headless-cli.md).
