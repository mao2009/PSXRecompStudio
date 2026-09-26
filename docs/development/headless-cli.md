# Headless CLI

**Status:** Stable

**Authority:** Reference

**Related Issues:** #460, #458, #459, #461, #457

**Related Components:** `src/PSXRecomp.Cli/`, `.github/workflows/ci.yml`,
`src/PSXRecomp.Tests/E2E/`, `src/PSXRecomp.Tests/Cli/`

The headless command-line surface of PSXRecompStudio: a small, deterministic
front end for the two production contracts from #458 (recompiled-host
artifact build) and #459 (runnable recompiled-artifact execution). It exists
so a PS-X EXE or, since #457, a supported CHD disc image can be recompiled and
executed without the GUI, under CI, or in scripts, while composing exactly the
same pipelines the Studio uses.

The CLI is deliberately a thin composition root. No compiler, build, Runtime,
or execution-loop semantics live in `PSXRecomp.Cli`; every behavior is owned by
the `PSXRecomp.Core` and `PSXRecomp.Infrastructure` contracts it calls into.

## Commands

```
psxrecomp recompile <input.exe|input.chd> --output <dir> [--json]
psxrecomp run       <input.exe|input.chd> [--output <dir>] [--segment-budget <n>] [--report] [--json]
```

`psxrecomp --help` prints usage, the option grammar, and the exit-code table.

### Input

The single input resolver is `CliInput.Load`, dispatched purely on the file
extension (OrdinalIgnoreCase), so the CLI's interpretation never depends on
file contents:

- `*.chd` is routed through the production CHD analysis pipeline
  (`RomAnalysisPipeline.RunFromChd` → the classified boot PS-X EXE →
  `PsxExeTitleInput.Build`). A CHD that cannot be resolved to a bootable PS-X
  EXE — an unreadable container, a disc with no `SYSTEM.CNF`, or a disc whose
  boot path does not hold a PS-X EXE — fails closed with the pipeline's
  `FailureKind`/`FailureReason` classifying the failure (recompile:
  `InvalidInput` + `INVALID_INPUT`; run: stderr + exit 1). It is never misread
  as an invalid PS-X EXE.
- any other extension keeps the original PS-X EXE path (`PsxExe.Load`), with
  its pre-#457 behavior unchanged.

### `recompile`

Builds the recompiled-host artifact (`recompiled-artifact`) for the PS-X EXE
or CHD input into `<dir>`. `--output <dir>` is required. `--segment-budget` is
not accepted for `recompile` (it is a run-time budget; the recompile contract
does not execute the program).

### `run`

Rebuilds the artifact and launches it against the recompiled-host execution
engine. `--output <dir>` is optional and defaults to the current directory.
`--segment-budget <n>` sets the per-segment instruction budget (a positive
integer). `--report` writes a privacy-safe `diagnostic-report.zip` into the
selected output directory after the production launcher returns a
`RecompiledArtifactResult`. The bundle is generated for successful and blocked
runs (and for a classified runtime failure that returns a result); input/build/
launcher failures that produce no result do not fabricate one. `--json` (valid
for both commands) emits the machine-readable envelope (see below) as the sole
stdout document.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (run completed; recompile build succeeded) |
| 1 | Tooling / input / build / launcher failure |
| 2 | (run) Guest control reached an explicit unsupported/blocked boundary |

## JSON envelopes

Emission is deterministic: no timestamps, absolute paths only where they are
the meaningful payload, and result fields taken verbatim from the production
`RecompiledArtifactResult` record (which itself is serialized with the shared
`ArtifactJson` configuration used by the Studio).

`recompile`:

```json
{ "kind": "recompile", "success": true, "status": "Succeeded",
  "artifact": "/abs/path/recompiled-artifact" }
```

On failure the envelope additionally carries `errorCode`. Input and CLI
composition failures use `INVALID_INPUT`, `INPUT_NOT_FOUND`, `UNSUPPORTED_INPUT`,
`CODEGEN_FAILED`, `IO_FAILURE`, `BUILD_FAILED`, or `TOOLING_FAILURE`; CHD inputs
that fail to resolve a bootable PS-X EXE always classify as `INVALID_INPUT`.
Build
service failures preserve their production diagnostic codes:
`OUTPUT_FAILED`, `TOOLCHAIN_UNAVAILABLE`, `TOOLCHAIN_TIMEOUT`,
`COMPILE_FAILED`, or `LINK_FAILED`. The envelope also includes a
human-readable `message`; `success` is `false`.

`run`:

```json
{ "kind": "run", "success": false,
  "artifact": "/abs/path/recompiled-artifact",
  "output": [ ...guest bytes written to the TTY... ],
  "result": { "outcome": 1, "exitCode": 2, "state": 4, "guestPc": 2147614720,
              "resultValue": 0, "engineName": "recompiled-host-artifact",
              "diagnosticCode": "UNRESOLVED_TRANSFER",
              "diagnosticMessage": "..." } }
```

`result` is the production `RecompiledArtifactResult`; numeric enum values are
serialized as numbers, matching the Studio's artifact schema. When the launch
itself fails (the artifact could not be built or spawned), `run` reports the
failure on stderr and exits `1`; it does not fabricate a `result` object,
because no production result exists to serialize. When the run succeeds but is
blocked (exit code 2), the JSON document is still emitted with
`"success": false`.

Without `--report` or `--frame-evidence`, the run JSON field set is unchanged.
With `--frame-evidence`, one `frameEvidence` object is appended:

```json
{ "frameEvidence": {
    "status": "available",
    "width": 256,
    "height": 240,
    "sha256": "<64 lowercase hex characters>",
    "productionState": 0,
    "diagnosticCode": null,
    "reason": null
} }
```

`status` is `available` only after the production interpreter observes
meaningful guest GPU frame activity (for example a VRAM write/rasterization or
explicit display enable). This is deliberately not a non-zero-pixel heuristic:
an explicitly produced black frame is valid evidence, while untouched power-on
VRAM is reported as `unavailable` with `reason: "no-frame-activity"`.
Other unavailable states leave hash fields null as appropriate and classify the
reason explicitly.
The `productionState` belongs to the supplemental production-interpreter
evidence run; the top-level `result` remains the generated-host run.

Without `--report`, the diagnostic-bundle field remains absent. With `--report`,
the same envelope adds one final field:

```json
{ "kind": "run", "...": "...",
  "diagnosticBundle": "/abs/path/diagnostic-report.zip" }
```

The path is presentation metadata only; it is not embedded inside the diagnostic
bundle itself.

### Diagnostic report bundle

`run --report` writes exactly four UTF-8 entries:

```text
diagnostic-report.zip
  report.json
  environment.json
  diagnostics.log
  issue.md
```

`report.json` carries the input SHA-256, artifact SHA-256 identity, execution
classification, guest PC/diagnostic code when available, and segment budget.
`environment.json` contains the bounded host/toolchain metadata allowed by the
#457 privacy contract. `diagnostics.log` uses the existing bounded sanitized
diagnostic-log contract; it may be empty when the launcher exposes no structured
diagnostic sequence beyond the production result. `issue.md` is generated from
the same sanitized report.

The bundle does **not** include the input EXE/CHD bytes, generated artifact
contents, raw RAM, memory-card/save data, credentials, usernames, or local
absolute paths. Nothing is uploaded automatically; the user inspects and shares
the bundle manually.

## Execution model

`run` always rebuilds the artifact from the input — a PS-X EXE or, since #457,
a supported CHD — using the #458 pipeline before launching it with #459. Relaunching a previously persisted artifact
alone is **not** supported: there is no `run <artifact>` mode and no manifest
describing a solo artifact's request. That capability is out of scope for #460
(the minimal CLI) and tracked under the #15 CLI framework discussion.

A successful build or launch implies only that the pipeline completed. Real
games may still stop at an explicit unsupported/blocked boundary (exit code 2)
when control reaches behavior the compiled image cannot continue through;
dynamic overlay recompilation (Issue #249) is the future upgrade path for
those cases.

## Scope

The CLI is the smallest useful surface for #460. A general command framework —
subcommand registration, shell completion, config files, persisted-artifact
relaunch — belongs to Issue #15 and is deliberately absent here.

## End-to-end proof (Issue #461)

Issue #461's first-runnable-artifact gate is held by `PSXRecomp.Tests.E2E`, an
always-run vertical proof plus a real-input gate, both driving the CLI's own
composition root:

- **`RecompiledArtifactE2ETests`** walks a synthetic PS-X EXE through the entire
  *production* pipeline — `PsxExe.Load` → `PsxExeTitleInput.Build` → decode +
  `MipsToIrLowerer` → `RecompilerHostCodeGen` → `RecompiledArtifactCodeGen` →
  `GeneratedHostBuildService` (#458) → `RecompiledArtifactLauncher` (#459) — and
  asserts the generated/recompiled code really executed (an observable result
  marker and TTY bytes only the artifact's own blocks could produce), that a
  repeated run is deterministic down to the generated source, and that both an
  unresolved control-flow transfer (exit 2, `UNRESOLVED_TRANSFER`) and an
  unregistered BIOS call (exit 1, `BIOS_HLE_UNSUPPORTED_CALL`) stop as classified
  boundaries — never a crash or a silent exit.
- **`RealExeE2ETests`** drives whatever legally user-supplied PS-X EXE exists
  under `rom/*.exe` through `psxrecomp run` and requires a classified, honest
  outcome every time: generated code that ran (production `engineName`), a
  classified blocked stop, or a fail-closed diagnostic — and an explicit skip with
  a reason when no fixture is present. Fixture discovery and the gitignore /
  artifact-policy exclusion contract live in `RealExeFixtures`, so a passing run
  can never smuggle a user's executable into the repository.