# Issue #48: Runtime Configuration (design)

- Status: Design only. No code, schema, or manifest change is made by this document.
- Issue: https://github.com/mao2009/PSXRecompStudio/issues/48 (`[Runtime] Runtime Configuration`, open, labels `config`, `runtime`).
- Related: #40 Studio GUI, #44 PS1 Hardware Runtime Architecture, #47 Controller / Input Abstraction.
- Base: `origin/main` at `2e3ecad`.

## 0. Evidence labels and method

| Label | Meaning in this document |
|---|---|
| **CONFIRMED** | Read directly in the source, the git history, or the public GitHub API during this design pass. File:line is given. |
| **OBSERVED** | Found by a repository-wide search or inferred from the absence of a symbol. Absence is only as strong as the search; no exhaustive call-graph was built. |
| **PREDICTED** | A design inference or a consequence the author expects, not checked against running code. |
| **UNVERIFIED** | Not established. Includes behaviour that needs a build or run, and anything that would need a GitHub login. |

Method limits: no build, test, or run was executed for this document. The issue text was read from the public REST endpoint `api.github.com/repos/mao2009/PSXRecompStudio/issues/48` (CONFIRMED). The `gh` CLI is not authenticated in this environment, so issue comments and timeline events are UNVERIFIED (the issue reports `comments: 0` at read time, CONFIRMED).

No BIOS, ROM, disc image, or copyrighted data is referenced or required by this design. Environment variables are named by key only; no values or paths from a real installation are recorded.

## 1. Issue scope (CONFIRMED, issue body)

The issue lists these runtime areas to cover: Resolution, VSync / frame pacing, Audio, Controller, Memory Card, Disc / multi-disc, Graphics backend, Debug / trace mode, Runtime environment.

Completion criteria from the issue, mapped to this document:

| Criterion | Section |
|---|---|
| Define the runtime configuration schema | 3, 4 |
| Define the relation to project configuration | 5 |
| Define a GUI/CLI shared configuration method | 6 |
| Define the per-title override policy | 7 |
| Define the configuration versioning policy | 8 |
| Separate debug and production configuration | 9 |

## 2. Current state inventory

### 2.1 CLI (`psxrecomp`)

Entry point and dispatch:

- Usage strings for `recompile`, `doctor`, `run`, `package`: `src/PSXRecomp.Cli/Program.cs:27-31` (CONFIRMED).
- Option parser: a `switch` over tokens inside `Program.cs` starting near line 150 (CONFIRMED). Unknown `--` tokens fail with `unknown option '<token>'.` (`Program.cs:366`, CONFIRMED). A second positional token fails with `unexpected argument` (CONFIRMED by reading the default branch).
- Per-subcommand gating of options: `Dispatch(...)` passes `allowSegmentBudget`, `allowMixedFallback`, `allowReport`, `allowFrameEvidence`, `allowSelfContained`, `allowGuestFirmware`, `allowGui` (`Program.cs:55` and surrounding, CONFIRMED). Options outside their subcommand are rejected (for example `--report` is `run`-only, `Program.cs:259-266`; `--gui` is `package`-only, `Program.cs:283-290`).

Options as parsed (CONFIRMED, `Program.cs:150-354`):

| Option | Applies to | Parsed at | Value rule |
|---|---|---|---|
| `--output <dir>` | recompile, run, package | 153 | required for recompile and package |
| `--segment-budget <n>` | run | 162 | positive integer |
| `--mixed-fallback` / `--no-mixed-fallback` | run, package | 184, 199 | mutually exclusive |
| `--fallback-segment-budget <n>` | run, package | 214 | positive integer; requires mixed fallback |
| `--fallback-max-transitions <n>` | run, package | 214 | positive integer; requires mixed fallback |
| `--entry-root <0xPC>` | recompile, run, package | 243 | repeatable |
| `--report` | run | 259 | flag |
| `--frame-evidence` | run | 268 | flag |
| `--json` | all | 277 | flag |
| `--standalone` | recompile, run | 280 | flag |
| `--gui` | package | 283 | flag |
| `--self-contained` | package | 292 | flag |
| `--guest-firmware` / `--no-guest-firmware` | package | 301 | mutually exclusive |
| `--bios`, `--bios-rom`, `--bios-provenance`, `--aot-dir` | package | 323-347 | `--bios-provenance` accepts `user-supplied` or `synthetic` only |
| `--code-bytes`, `--instruction-budget` | package | 351-352 | non-zero `uint` |
| `--run-timeout-seconds` | package | 353 | 1 to 86400 |
| `--build-timeout-seconds` | package | 354 | 1 to 86400 |

Mixed fallback default: "Mixed execution is on by default for `run`" (`Program.cs:128-131`, ADR-012 amendment, Issue #693, CONFIRMED from the comment). An explicit `--no-mixed-fallback` restores the fail-closed stop.

Typed carriers: `ParsedArguments` (record in `Program.cs`, documented near line 508) and `FirmwareOptions` (`Program.cs:538`, CONFIRMED).

Subcommands that take no configuration beyond the above: `doctor` (`Program.cs:28`), `screenshot`, `fallback-roots`, and the `openbios-probe` family (`src/PSXRecomp.Cli/*.cs`, file list CONFIRMED by `git ls-files`; their option sets were not enumerated, UNVERIFIED).

### 2.2 Runtime host (`PSXRecomp.RuntimeHost`): every input it reads

The packaged host is the process a distribution launches. Its own documentation says it runs "on a machine with no Studio, no CLI and no development repository present" (`src/PSXRecomp.RuntimeHost/Program.cs:21`, CONFIRMED). The complete input list, as read by the code, is:

| # | Input | Read at | Condition | Label |
|---|---|---|---|---|
| H1 | `runtime-host.json` (positional argument 0) | `RuntimeHost/Program.cs:125-130` (path), `:218` (parsed with `RuntimeHostManifestJson.Parse`) | always | CONFIRMED |
| H2 | Artifact named by `manifest.Artifact` (`prebuiltArtifactPath`) | `RuntimeHost/Program.cs:229` | always | CONFIRMED |
| H3 | Image named by `manifest.Image` (`artifact-image.bin` in the packaged layout) | `RuntimeHost/Program.cs:244-248` | non-firmware runs | CONFIRMED |
| H4 | `firmware-boot.json` (`FirmwareBootManifest.FileName`) | presence check `FirmwareDistribution.cs:599-600` via `IsFirmwareDistribution`, called at `RuntimeHost/Program.cs:235`; parsed at `FirmwareDistribution.cs:651` during launch | firmware distributions only | CONFIRMED |
| H5 | Firmware AOT inputs under `runtime/firmware/`, each re-verified against its recorded SHA-256 | `FirmwareDistribution.cs:677` ("P1-3") and the bundle comment at `:106` | firmware distributions only | CONFIRMED (comment and call site read; SHA check body not read, UNVERIFIED) |
| H6 | Environment `PSXRECOMP_BIOS_ROM` (a path to a user-supplied BIOS file) | `RuntimeHost/Program.cs:239` | firmware runs only | CONFIRMED |
| H7 | Host CLI flags `--diagnostics <path>`, `--loaded-code-diagnostics <path>`, `--frame-out <path>` | `RuntimeHost/Program.cs:59`, `:72`, `:95` | optional | CONFIRMED |
| H8 | Input file identities, read only to refuse report targets that would overwrite an input | `RuntimeHost/Program.cs:141-205`, `ReadInputPaths` at `:537-560` | when a report target is set | CONFIRMED (read-only check, no configuration meaning) |

Two further groups are read by the artifact code that the host runs, not by the host's own `Program.cs`:

- Generated-code environment reads: `PSXRECOMP_BIOS_ROM` (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:639`); `PSX_GUARD_STATS_FILE`, `_RUN_ID`, `_PERIOD`, `_ABORT_AFTER` (`RecompilerHostCodeGen.cs:983`, `:994`, `:1018`, `:1021`); `PSX_TRACE_SESSION` (`:1126`); `PSX_UNIT_TRACE_FILE`, `_MAX_BYTES`, `_FROM`, `_TO` (`:1158-1161`). All CONFIRMED as strings in the code generator. Whether a given packaged artifact contains these reads depends on how it was built (UNVERIFIED; see 9.2).
- Not read by the packaged host: `project.json`, any runtime configuration file, any Studio state. OBSERVED: a search of `src/PSXRecomp.RuntimeHost/*.cs` for `project`, `Studio`, `ProjectManifest` and `runtime-config` returns only the comment at `Program.cs:21`.

### 2.3 Studio GUI (`PSXRecompStudio`)

- `src/PSXRecompStudio/Program.cs` (23 lines) forwards `args` to Avalonia's `StartWithClassicDesktopLifetime(args)` and does not parse any option (CONFIRMED).
- `src/PSXRecompStudio/Views/MainWindow.axaml` exposes two actions, `RunDiagnosticTitleButton` (line 51) and `RunRealTitleButton` (line 68), and status text blocks. There is no settings, options, or preferences control (CONFIRMED). A search for `Settings`, `Options`, `Preferences`, and `ConfigView` in `src/` returned no UI type (OBSERVED).
- `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:19-23` exposes constant display strings (`AppName`, `Version`, `Status`, runtime and platform info). No user-settable runtime property exists (CONFIRMED).
- Execution budgets are hard-coded constants in the service layer: `RealRomTitleExecutionService.DefaultOuterBudget = 64` (line 68) and `DefaultSegmentBudget = 4096` (line 71) (CONFIRMED).
- Memory cards: `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs` implements file-backed card storage (CONFIRMED). The Studio view model does not expose a slot selection (OBSERVED).

### 2.4 Game window (`PSXRecomp.GameWindow`)

- `App.ManifestPath` is set from `args[0]`, the packaged `runtime-host.json` (`src/PSXRecomp.GameWindow/Program.cs`, `App.axaml.cs:10`, CONFIRMED). If it is `null`, the window does not start a run (`MainWindow.axaml.cs:60`, CONFIRMED). No other argument is parsed (CONFIRMED).
- Frames are passed from a one-slot `LiveFrameSlot` (field at `src/PSXRecomp.GameWindow/MainWindow.axaml.cs:25`, CONFIRMED). No vsync, resolution, or backend option is present (OBSERVED).

### 2.5 Persisted configuration files

| File | Writer | Reader | Versioning | Source |
|---|---|---|---|---|
| `project.json` (Project manifest) | `FileProjectMetadataStore.Save` (`FileProjectMetadataStore.cs:22`) | `FileProjectMetadataStore.Load` (`:52`) | `ProjectSchema.ProjectFormatVersion = 1` (`ProjectSchema.cs:19`) | CONFIRMED |
| `runtime-host.json` (RuntimeHostManifest) | `PackageCommand` (`PackageCommand.cs:234-235`) | `RuntimeHostManifestJson.Parse` (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:113`), `PSXRecomp.RuntimeHost/Program.cs` | `RuntimeHostManifest.CurrentSchemaVersion = 1` (`RuntimeHostManifest.cs:57`) | CONFIRMED |
| `firmware-boot.json` (FirmwareBootManifest) | `FirmwareDistribution.cs:455` | `FirmwareDistribution.cs:651` | its own `FirmwareBootManifest.CurrentSchemaVersion` (`:445`) | CONFIRMED |

The `project.json` record carries only four fields: `SchemaVersion`, `ArtifactKind`, `ProjectId`, `InputSha256` (`ProjectManifestDocument.cs:21-29`, CONFIRMED). It contains no runtime setting. The file is documented as "the minimal, versioned Project/Workspace management-data manifest" (`ProjectManifestDocument.cs:6`, CONFIRMED).

The `RuntimeHostManifest` record (`RuntimeHostManifest.cs:40-54`, CONFIRMED) carries the runtime settings that reach the packaged run:

- `SchemaVersion`, `Artifact`, `GuestFirmware`, `MixedFallback`
- `FallbackSegmentInstructionBudget`, `FallbackMaxTransitions`
- `ImageLoadAddress`, `Image`, `BlockEntryPcs`, `ProgramEnd`, `ResultRegister`, `EntryPc`
- `RunTimeoutMs`
- `BiosProvenance` (optional)

Its reader validates fail-closed: a schema other than 1 is rejected (`RuntimeHostManifest.cs:133-136`), `RunTimeoutMs <= 0` is rejected (`:173`), and mixed fallback with a non-positive budget is rejected (`:187`), all CONFIRMED.

Schema versions are checked by strict equality in both `ProjectMetadataSerializer` (`ProjectMetadataSerializer.cs:81-85`, CONFIRMED) and `RuntimeHostManifestJson` (`RuntimeHostManifest.cs:133`, CONFIRMED). No migration code path exists in either (CONFIRMED by reading both readers).

### 2.6 Environment variables

Every environment read in the managed code and the code generator. Counted with `git grep -nE 'GetEnvironmentVariable|getenv' -- src ':(exclude)src/PSXRecomp.Tests'`: 19 matching lines in 8 files (CONFIRMED). Test sources are excluded; including them gives a larger count (reviewer-reported 46 lines in 18 files, not re-run here).

The "Run-affecting" column answers whether the variable can change what the guest executes or how long the run lasts.

| Variable | Read at | Effect | Run-affecting | Parse rule on bad value |
|---|---|---|---|---|
| `PSXRECOMP_BIOS_ROM` | `RecompiledArtifactCodeGen.cs:629` (constant), `:639` (generated C `getenv`); `RuntimeHost/Program.cs:239` (host) | Path to a user-supplied BIOS image. Its contents are not copied into the distribution (`docs/runtime/openbios-firmware-backend.md:504`, CONFIRMED). | Selects the firmware BIOS image, so yes for firmware runs | Missing or wrong-size file is an error at run time (`openbios-firmware-backend.md:510`, CONFIRMED by doc) |
| `PSXRECOMP_IPC_PROFILE` | `Infrastructure/Standalone/IpcProfile.cs:29`, `:48`, `:50` | Host IPC profile output path, opt-in (`openbios-firmware-backend.md:559`) | No (measurement output) | Non-empty path enables it (CONFIRMED) |
| `PSXRECOMP_TRANSITION_ATTRIBUTION` | `TransitionAttributionRecorder.cs:17`, `:35`, `:37` | Output path for transition attribution, opt-in (`openbios-firmware-backend.md:546`) | No (measurement output) | Non-empty path enables it (CONFIRMED) |
| `PSXRECOMP_NATIVE_GUARD_STATS` | `Infrastructure/GuardStatsScope.cs:34`; build-time at `FirmwareDistribution.cs:404` (comment) | Builds the artifact with guard counters (measurement) | No for execution; changes the artifact build | Non-empty enables (CONFIRMED) |
| `PSX_GUARD_STATS_FILE` | `GuardStatsScope.cs:15`; generated C `RecompilerHostCodeGen.cs:983` | Guard statistics output path | No | UNVERIFIED (parse code not read) |
| `PSX_GUARD_STATS_RUN_ID` | `GuardStatsScope.cs:18`; generated C `:994` | Run id for the child process | No | UNVERIFIED |
| `PSX_GUARD_STATS_PERIOD` | `GuardStatsScope.cs:21`; generated C `:1018` | Periodic dump interval, in dispatch units | No (output cadence only) | UNVERIFIED |
| `PSX_GUARD_STATS_ABORT_AFTER` | `GuardStatsScope.cs:23-24` (documented as "ends the child without its final dump"); set on the child at `Infrastructure/RecompiledHostExecutionEngine.cs:1349`; generated C `RecompilerHostCodeGen.cs:1021` | Ends the run after this many dispatch units | **Yes: it can end a run early** | UNVERIFIED |
| `PSX_UNIT_TRACE_FILE` | `Infrastructure/UnitTraceRun.cs:18`, `:24-26`; generated C `RecompilerHostCodeGen.cs:1158` | Selects a trace build and names its native trace file (`docs/development/unit-trace.md:21`) | Trace build may change timing; UNVERIFIED | Empty disables (CONFIRMED) |
| `PSX_UNIT_TRACE_MAX_BYTES` | generated C `RecompilerHostCodeGen.cs:1159` | Trace cap, default 64 MiB (`unit-trace.md:22`) | No | Invalid value disables tracing for that run and the run proceeds untraced (`unit-trace.md:22`, CONFIRMED) |
| `PSX_UNIT_TRACE_FROM` / `_TO` | generated C `RecompilerHostCodeGen.cs:1160-1161` | Trace window bounds | Trace-only; UNVERIFIED | UNVERIFIED |
| `PSX_TRACE_SESSION` | generated C `RecompilerHostCodeGen.cs:1121`, `:1126` | Session id passed parent to child (`unit-trace.md:40`) | No | Kept only if a short `[0-9A-Za-z-]` token (CONFIRMED) |

The environment-variable surface has no central registry. Each reader owns its own constant and parse rule (OBSERVED: the constants are declared in six different classes).

### 2.7 Execution budgets and defaults (same concept, different values)

| Name | Value | Source |
|---|---|---|
| `RunCommand.DefaultSegmentBudget` (CLI run) | `1_000_000u` | `src/PSXRecomp.Cli/RunCommand.cs:41` |
| `PackageCommand.DefaultSegmentBudget` | aliases `RunCommand` | `PackageCommand.cs:43` |
| `RealRomTitleExecutionService.DefaultSegmentBudget` (Studio) | `4096` | `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:71` |
| `RealRomTitleExecutionService.DefaultOuterBudget` (Studio) | `64` | same file, `:68` |
| `MixedFallbackOptions.DefaultSegmentInstructionBudget` | `1_000_000u` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:22` |
| `MixedFallbackOptions.DefaultMaxTransitions` | `100_000u` | `MixedFallback.cs:25` |
| `FirmwareDistributionRequest.DefaultFallbackSegmentInstructionBudget` | `100_000_000u` | `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:320` |
| `PackageCommand.DefaultRunTimeoutMs` (headless) | `60_000` | `PackageCommand.cs:46` |
| `PackageCommand.GuiRunTimeoutMs` (`--gui`) | `4 h` | `PackageCommand.cs:52` |
| `FirmwarePackageCommand.DefaultRunTimeoutSeconds` | `1800` | `src/PSXRecomp.Cli/FirmwarePackageCommand.cs:18` |
| `FirmwareDistributionBuilder.DefaultBuildTimeout` | `3 min` | `FirmwareDistribution.cs:345` |

OBSERVED: the "segment budget" default differs by a factor of 1000 between the CLI run (`1_000_000`) and the Studio's real-title service (`4096`). The firmware fallback default (`100_000_000`) is a third value. Recent commit `f845e45` ("スタンドアロン firmware の fallback 既定予算を有限にする（Refs #820）") made the standalone firmware fallback budget finite (CONFIRMED from git log). This shows the project already treats finite defaults as a rule, but the rule is applied per call site, not from one table.

### 2.8 Subsystems in the issue scope with no configuration surface

| Issue area | What exists | Configuration surface | Label |
|---|---|---|---|
| Resolution | `GpuMmioAdapter.GetDisplayResolution` (`src/PSXRecomp.Core/Dma/GpuMmioAdapter.cs:101`) reports the device's display size; it is not a user setting | none | CONFIRMED (absence of a setting) |
| VSync / frame pacing | No symbol found by search for `VSync`, `Vsync`, `FramePacing` in `src/` (OBSERVED). The guest-side VBlank model exists: `DeviceScheduler` (`src/PSXRecomp.Core/Runtime/DeviceScheduler.cs:33`, `VblankIntervalCycles` at `:39`) and `BiosTimerVblankIrqHandler` (`src/PSXRecomp.Core/Runtime/BiosTimerVblankIrqHandler.cs:38`). A test asserts that the field polled by OpenBIOS's `waitVSync` flips at VBlank (`src/PSXRecomp.Tests/Runtime/DeviceSchedulerTests.cs:274`, CONFIRMED). | none on the host side; guest timing is fixed by the model | OBSERVED (host symbol); CONFIRMED (guest VBlank model) |
| Audio | `ISpu` interface (`src/PSXRecomp.Core/Runtime/ISpu.cs`) and a Rust `spu.rs` (`src/PSXRecomp.Native/rust/src/spu.rs`); no host audio output class found | none | CONFIRMED (files), OBSERVED (no host sink) |
| Controller | `PhysicalControllerState<TDeviceKind>` (`src/PSXRecomp.Core/Runtime/Input/PhysicalControllerState.cs:20`), whose comment says it is "owned by a future OS/device adapter" (lines 5-16); `InputBindingMap<TButton>` is declared at `src/PSXRecomp.Core/Runtime/Input/InputBindingMap.cs:29`, with its `Resolve` method at `:114` | none reachable from CLI or GUI | CONFIRMED |
| Memory Card | `MemoryCardSlotConfiguration(Slot1Path, Slot2Path)` (`src/PSXRecomp.Core/MemoryCard/MemoryCardSlots.cs:44`); consumed by `RecompiledHostExecutionEngine` (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:141`) | no CLI option, no GUI control, no manifest field | CONFIRMED (type), OBSERVED (no CLI/GUI setter) |
| Disc / multi-disc | `DiscIdentity` with `DiscIndex` (`src/PSXRecomp.Core/TitleIdentity/DiscIdentity.cs:10-14`) | no selection option found | CONFIRMED (type), OBSERVED (no selector) |
| Graphics backend | Software GPU model (`src/PSXRecomp.Core/Runtime/Gpu/GpuDevice.cs`, `GpuRasterizer.cs`); frames shown through an Avalonia bitmap | no backend selection found | OBSERVED |
| Debug / trace | See 2.6 (environment variables) and CLI `--report`, `--frame-evidence`, runtime-host `--diagnostics` | no single switch; no debug/production profile | CONFIRMED |
| Runtime environment | `PSXRECOMP_BIOS_ROM` (H6) and `RuntimeHostManifest` (H1) | split across env and manifest | CONFIRMED |

Summary: of the nine areas the issue names, one (Memory Card) has a typed model in Core with no user entry point, one (Controller) has an input model with no OS adapter, and the rest have no model at all. Execution budgets and timeouts are the only area with a real, shipped, user-visible configuration surface (CLI options and manifest fields).

### 2.9 Architecture and ADR constraints

- ADR-027 Step C (`docs/adr/027-standalone-artifact-runtime-integration.md`, "Step C scope", lines 128-155, CONFIRMED): the distributed host runs "with no Studio, no CLI, and no .NET SDK at runtime" (lines 130-132). Its layout block (lines 148-156) and implementation list (line 138) name two packaged inputs: `runtime/runtime-host.json` and `artifact-image.bin`. The string `firmware-boot` does not appear anywhere in that ADR (CONFIRMED by search). The firmware inputs H4 and H5 are therefore not in the Step C baseline. Whether they are a Step C baseline change or a later amendment is UNVERIFIED.
- `docs/development/headless-cli.md:405-407` (CONFIRMED): "config files ... belongs to Issue #15 and is deliberately absent here." Config-file support was explicitly deferred to Issue #15 and is not part of #460's CLI.
- `docs/architecture-matrix.md` Layer rules (CONFIRMED): rule 1, Domain owns ports, never adapters; rule 2, Infrastructure executes host APIs (`System.IO.File`, `Directory`, `Process`, and so on); rule 3, Application does not acquire host resources directly.
- `src/architecture.contract.json:22` (CONFIRMED): `System.Environment` is a forbidden API in the Domain layer ("execution environment dependencies break determinism"). Line 37 forbids it in Test and line 48 in Generated; the contract does not restrict Infrastructure.

## 3. Shared runtime configuration schema (proposal)

### 3.1 Two tiers, and what the packaged host reads

The schema separates two kinds of value, because they have different lifetimes and different readers:

1. **Execution settings (frozen at package time).** Values that change what the guest runs or how long it may run: budgets, timeouts, mixed-fallback enable and limits, guest firmware on or off, BIOS provenance label, entry roots. They are resolved once at packaging time and written into `runtime-host.json` (schema 1, section 4). The packaged host reads them through H1.
2. **Host settings (launch-time).** Values that affect only presentation, input, and host-side output, not guest semantics: window size, presentation mode, input device mapping, memory-card file paths, diagnostics output paths. These are consumed by the launcher (Studio, GameWindow, or CLI `run`) before a run starts.

The packaged host reads no host setting in this design. Its complete read list is H1 to H8 in 2.2. No new file and no new environment variable is added to that list. So:

- The packaged run keeps the ADR-027 Step C baseline (`runtime-host.json` and `artifact-image.bin`, plus the firmware inputs H4 and H5 that already ship). This design adds nothing to it.
- A host setting that must reach the packaged run needs one of two changes, and each changes the Step C baseline: a new execution field in `runtime-host.json` (schema 2, section 4), or a new packaged input or host CLI flag. Such a change is listed as open question Q13 and is not proposed here.
- Memory-card paths are not read by the packaged host today (no field in H1-H8). They are consumed only where a `RecompiledHostExecutionEngine` is constructed (Q8).

This split keeps the Runtime execution contract unchanged: the guest sees only the frozen values in the manifest.

### 3.2 Proposed schema (logical, not a file format yet)

```text
RuntimeConfig (schemaVersion: int, kind: "psxrecomp.runtime.config")   // Project directory copy: non-path keys only
  execution:              // frozen at package time (-> runtime-host.json)
    mixedFallback:        bool
    fallbackSegmentBudget: uint   (positive; default 1_000_000 for run)
    fallbackMaxTransitions: uint  (positive; default 100_000)
    runTimeoutMs:         int     (positive; bounded; GUI cap 4 h)
    guestFirmware:        bool
    segmentBudget:        uint    (positive; default 1_000_000)
    entryRoots:           uint[]  (sorted, unique)
  host:                   // launch-time only; never read by the packaged host
    display:
      windowSize:         { width, height } | null
      presentation:       "fit" | "integer" | "stretch"   // UNVERIFIED need
      vsync:              bool | null                      // UNVERIFIED need (Q3)
    input:
      ports:              [ { port: 1|2, device: "pad"|"none", bindings: <InputBindingMap> } ]

PerUserStore (not shareable; see section 5.1)     // path-valued keys only
  memoryCard:
    slot1Path:            string | null
    slot2Path:            string | null

CLI only (never stored)
  --diagnostics, --loaded-code-diagnostics, --frame-out     // paths named per invocation

Title overlay (Project directory, per title; see section 7)
  identity:               { serial, region, revision, discIndex }
  overrides:              (subset of execution and non-path host keys)
```

Notes:

- Resolution, VSync, audio, and graphics backend are placed under `host`. They are not guest-visible in this design (PREDICTED: for PS1 the guest sees a fixed video timing; the host VSync choice would only change presentation pacing). This must be checked against the GPU and timing models before it is accepted (UNVERIFIED; Q3).
- Audio has no host sink in the repository (OBSERVED, 2.8), so the audio section is reserved, not implemented. Its key set must not be fixed until a host audio adapter is designed.
- Memory-card paths and diagnostic output paths are host paths. They never enter `runtime-host.json` (section 5.1 explains where they live).
- BIOS: the BIOS location stays an environment variable (H6) or a CLI option. The configuration stores only a provenance label, never a path to a real user BIOS, and never bytes or digests of it. `FirmwareDistribution.cs:442` confirms no BIOS bytes, digest or sidecar is written (CONFIRMED).
- Controller: `InputBindingMap` is the natural holder of bindings. The `input` section references it, and its shape is decided in #47, not here.

### 3.3 Field rules

- Every key has one owner module and one default defined in code, not in the file (single table of defaults; see 6.3).
- Unknown keys fail the load (fail closed). No silent drop.
- Numeric keys have explicit bounds. Timeouts are always finite (the 4 h GUI cap at `PackageCommand.cs:52` is the precedent).
- Paths are stored as given and resolved relative to the file that holds them, never to the working directory (PREDICTED; this avoids the ambiguity between CLI and GUI launches). Only the per-user store holds paths (section 5.1).

## 4. Relation to the existing manifest schema

- The existing `RuntimeHostManifest` (schema 1) already carries every execution value listed in 3.2 `execution`, with the same field names in camel case. The design maps `RuntimeConfig.execution` to these fields and does not replace them.
- The packaged distribution's input list is H1 to H8 (2.2). This design adds no packaged input, so `runtime-host.json` keeps schema 1 and the distribution layout keeps its Step C baseline.
- Adding any new execution field to `runtime-host.json` changes the manifest schema, so `CurrentSchemaVersion` must advance (2) and the reader must accept 2 only with that version (CONFIRMED rule at `RuntimeHostManifest.cs:133`). That changes the packaged-artifact contract, so it is out of scope for this issue unless the issue owner accepts it (Q13). This document does not propose a schema 2.
- Host settings (3.2 `host`) do not enter `runtime-host.json` and are not written as a new packaged file. They are applied by the launcher.

## 5. Relation to project configuration, and privacy

### 5.1 Privacy of path-valued keys

Path values are not safe in a shareable file. A path such as `C:\Users\<name>\...` reveals a local account name and directory layout. A Project directory is likely to be copied, zipped, committed, or attached to a bug report, so path values must not sit there. This design therefore splits storage by content:

- **Project directory** (shareable by design): `project.json` (identity and format only, 2.5) and `runtime-config.json`, holding only non-path keys (`execution` and the non-path `host` keys) and per-title overlays (section 7) that contain no path keys.
- **Per-user store** (not shareable): a file in the user's own application data folder, keyed by `ProjectId`, holding path-valued keys only (`memoryCard.slot1Path`, `memoryCard.slot2Path`). Its exact location is UNVERIFIED: no existing per-user settings convention was confirmed in the repository (Q14).
- **CLI only, never stored**: diagnostic output paths (`--diagnostics`, `--loaded-code-diagnostics`, `--frame-out`, H7). They are named per invocation.
- **Environment only**: the BIOS location (H6, `PSXRECOMP_BIOS_ROM`). It is never copied into any file.

Consequences:

- A Project directory copy never leaks a host path. A title overlay cannot carry a memory-card path, so the rule "a per-title override never changes host paths" is structural, not a convention.
- The packaged distribution never contains any of these keys, because the packaged host reads none of them (3.1).
- Q6 asks whether the Project directory is in fact the shareable unit. If it is not, the non-path keys can also move to the per-user store, but the rule that paths never enter the Project directory stays.

### 5.2 Project configuration relation

- `project.json` is the Project management manifest (`ProjectManifestDocument.cs:6`). It records identity (`ProjectId`, `InputSha256`) and format (`SchemaVersion`, `ArtifactKind`). It is not a settings store, and it should stay that way (PREDICTED).
- Runtime configuration is a separate file, in the Project directory, named `runtime-config.json` (proposed; name UNVERIFIED against any existing convention).
- `project.json` gains no runtime fields. Its single-purpose meaning is preserved, and a runtime-config change never needs a Project format bump (PREDICTED).

## 6. Shared GUI and CLI method

### 6.1 One model, two front ends

- The domain port is `IRuntimeConfigStore`, declared in `PSXRecomp.Core` (new, Domain). It follows the `IProjectMetadataStore` pattern (`src/PSXRecomp.Core/Project/IProjectMetadataStore.cs:18`, CONFIRMED). The `RuntimeConfig` record and its validation live in `PSXRecomp.Core` with no I/O.
- The concrete store, the file reads and writes, and the per-user store location live in `PSXRecomp.Infrastructure`, following `FileProjectMetadataStore` (CONFIRMED pattern).
- Environment-variable reads happen only in Infrastructure. This follows `docs/architecture-matrix.md` Layer rule 2 (Infrastructure executes host APIs) and rule 3 (Application does not acquire host resources directly), and `src/architecture.contract.json:22` (Domain forbids `System.Environment`). The Studio and GameWindow read no environment variable directly; they call the Infrastructure store. OBSERVED: the existing read at `RuntimeHost/Program.cs:239` sits in an entry point outside any Infrastructure adapter. Its layer classification is UNVERIFIED and is not changed by this design.
- The CLI maps its existing parsed options onto a partial `RuntimeConfig` overlay (`ParsedArguments`, documented near `Program.cs:508`, CONFIRMED). The CLI does not gain a new parser.
- The Studio binds its settings UI (not present today, 2.3) to the same `IRuntimeConfigStore`. It must not keep a separate copy of any default.
- Budgets currently duplicated in `RunCommand`, `RealRomTitleExecutionService`, `MixedFallbackOptions`, and `FirmwareDistributionRequest` (2.7) are replaced by references to the single defaults table (6.3). This is a refactor, and it changes no value unless the owner decides a value is wrong.

### 6.2 Precedence (lowest to highest)

Execution layers (1, 3, 4, 6) are resolved at packaging time. Host layers (2, 3, 4, 6) are resolved at launch. Each layer is tagged with which kind it affects.

1. Compiled-in defaults (the single defaults table, 6.3). Execution and host.
2. Per-user store (path-valued keys and personal host preferences). Host only, launcher-side.
3. Project `runtime-config.json`. Execution at packaging; host at launch.
4. Per-title overlay for the matching title identity (section 7). Execution at packaging; non-path host at launch.
5. Environment variables for diagnostic keys only. Launcher-side and generated-code-side. Never sets an execution setting, except the documented exception in 9.2.
6. CLI options for one invocation. Execution at packaging (`package`) or at run (`run`); host for the diagnostic flags.

The BIOS location (H6) is an input path, not a layer. It is not overridden by any layer.

A value that a higher layer does not set is inherited, not reset. Each effective value must record its source in the output (the `--json` result or the packaged `README`), so a result can be traced to a layer.

### 6.3 Single defaults table

One module in `PSXRecomp.Core` holds every default in 2.7, with a name and a comment for each. Existing constants are re-pointed to it. Each value keeps its current number until the owner changes it; the duplicated and inconsistent values (1_000_000 versus 4096, and 100_000_000 for firmware) are listed as open question Q1, not silently unified.

## 7. Per-title overrides

- **Key.** A title is identified by `DiscIdentity` (serial, region, revision, `DiscIndex`; `DiscIdentity.cs:10-14`, CONFIRMED). The override key is the serial plus region plus revision. The disc index is part of the key for multi-disc titles, so each disc has its own overlay (PREDICTED; UNVERIFIED how the Project records disc order).
- **Location.** Per-title overlays are stored in the Project directory, under a sub-folder keyed by the title identity, not in the application-wide settings. They are shareable and therefore hold no path-valued keys (5.1).
- **Allowed keys.** Execution keys may be overridden per title (for example a larger budget for a slow title). Non-path host keys may be overridden per title (for example controller mapping). Path-valued keys and diagnostic outputs may not be overridden per title, because they are not stored in that file. Debug keys may not be made permanent per title in a production profile (see 9).
- **Applied at packaging time** for execution keys. The effective value is frozen into the manifest. A per-title override therefore never needs a runtime lookup.
- **Not a source of identity.** An overlay never changes `ProjectId` or `InputSha256`, and it never changes which input file is accepted.
- **Mismatch.** If the overlay's identity does not match the loaded image's identity, the overlay is refused with a classified error, not ignored (PREDICTED; mirrors the fail-closed rule for manifests).

## 8. Configuration versioning and migration

### 8.1 Current behaviour (CONFIRMED)

- Each persisted file has its own integer version and a strict-equality check. Readers reject any other version, and there is no migration (2.5).
- `ProjectMetadataSerializer` says a shape or meaning change requires a bump (`ProjectSchema.cs:15-17`, CONFIRMED). That rule is good and should be kept.

### 8.2 Proposed policy for `runtime-config.json` and the per-user store

- Each file carries a top-level `schemaVersion` (integer) and `kind` (`"psxrecomp.runtime.config"`, or the per-user store's own kind), like `project.json`.
- Strict reject of versions newer than the reader understands. The message names both versions, as `RuntimeHostManifestJson` already does (CONFIRMED pattern).
- Older versions are migrated in memory by a pure function `Migrate(vN) -> vN+1`, one step at a time. The migrated document is written back only on an explicit save, never silently on load (PREDICTED; keeps the user's file unchanged until they choose to change it).
- Each migration step ships with a test that loads a frozen fixture of the old version. Fixtures contain no real titles, no BIOS data, and no paths from a real install.
- Unknown keys fail the load (3.3). This makes a typo visible instead of silently ignored, and it is also how an old reader refuses a newer file.
- The packaged `runtime-host.json` keeps its own schema version (1, 2.5). A change to runtime-config never implies a change to the manifest unless the execution section changes (section 4).

### 8.3 Version 1 of runtime-config

Version 1 is the first version; there is nothing to migrate from. The first implementation should adopt the keys that already exist as CLI options (2.1) and nothing else. Host keys are added in a later version, each with a migration step that adds defaults for the missing keys.

## 9. Safe defaults and debug separation

### 9.1 Safe defaults (PREDICTED, grounded in existing code)

- Mixed fallback on for `run` (`Program.cs:128-131`, CONFIRMED); the explicit opt-out remains available.
- All execution budgets and timeouts are finite and positive (CONFIRMED rules at `RuntimeHostManifest.cs:173, 187`; GUI cap `PackageCommand.cs:52`).
- Debug and trace outputs are off unless named (2.6: every debug variable is empty-means-off, CONFIRMED for the parsed ones; UNVERIFIED for the guard-stats parse).
- No user path is embedded in a shareable file (5.1). BIOS data and digests are never written into configuration (`openbios-firmware-backend.md:504`, CONFIRMED; `FirmwareDistribution.cs:442`, CONFIRMED).
- Unknown keys fail closed (3.3).
- Invalid values fail with a classified error, not a silent fallback. Note that the trace variable `PSX_UNIT_TRACE_MAX_BYTES` currently disables tracing on an invalid value and lets the run proceed (2.6, CONFIRMED). That is a different policy from the CLI options, which reject. Q4 asks which policy is wanted.

### 9.2 Debug versus production

- Introduce a `profile` in the config: `production` or `debug`. Debug enables the trace and guard-statistics keys (2.6) and the `--report`, `--frame-evidence`, and `--diagnostics` outputs. Production refuses those keys.
- A packaged artifact records its profile. Because a manifest change is a schema change (section 4), the first version of this should keep the profile in the packaging log and README, not in `runtime-host.json`. Moving it into the manifest is open question Q5.
- Environment variables for debug remain allowed in both profiles, with one explicit exception. The general rule is that debug variables cannot change execution settings (6.2 layer 5).
- **Exception: `PSX_GUARD_STATS_ABORT_AFTER`.** It is run-affecting. `GuardStatsScope.cs:23-24` documents that it "ends the child without its final dump", `RecompiledHostExecutionEngine.cs:1349` sets it on the child, and the generated code reads it at `RecompilerHostCodeGen.cs:1021` (CONFIRMED). So the generated code reads it ambiently from the process environment: a packaged artifact that contains the read would end early if the variable were set on the user's machine. Whether a distributed artifact contains that read depends on whether it was built with guard counters (`PSXRECOMP_NATIVE_GUARD_STATS` at package time, `FirmwareDistribution.cs:404`), which is UNVERIFIED for the codegen condition. Rule for this design: the debug profile refuses this key at packaging; the production profile never builds with guard counters; and no launcher forwards it.
- `PSX_UNIT_TRACE_FILE` selects a trace build. Whether that build changes guest timing is UNVERIFIED, so it is treated as a debug-only key that the production profile refuses, pending a check.

## 10. Recommendation

1. Adopt the two-tier model (3.1): frozen execution settings in the packaged manifest, and launch-time host settings applied by the launcher. The packaged host reads no new input, so the ADR-027 Step C baseline (including H1 to H8) is unchanged.
2. Keep `runtime-host.json` schema 1 as the execution carrier. Do not add fields in this issue. Map the existing CLI options to it (2.1, 2.5).
3. Add `runtime-config.json` (Project directory, non-path keys only) and a per-user store (path-valued keys only), both behind an `IRuntimeConfigStore` port in Core with the implementation in Infrastructure (5.1, 6.1).
4. Create a single defaults table and re-point the constants (6.3). Resolve the inconsistent segment-budget defaults first (Q1), because the table cannot be written without it.
5. Implement per-title overlays only after the title-identity key is confirmed (Q2), and only with non-path keys (section 7).
6. Keep audio, VSync, and graphics backend as reserved names in the design until their host adapters exist (2.8, 3.2). Do not invent keys for them.
7. Split debug and production profiles (9.2). Keep debug keys out of execution settings, with the `PSX_GUARD_STATS_ABORT_AFTER` exception stated above.

Suggested order of follow-up work (for the owner to schedule; not started here): defaults table, then runtime-config version 1 with only the existing CLI keys, then per-title overlay, then host keys one area at a time with #40 and #47.

## 11. Open questions

- **Q1. Which segment-budget default is correct?** The CLI run uses `1_000_000`, the Studio real-title path uses `4096`, and firmware fallback uses `100_000_000` (2.7). Should they be unified, or does each path keep its own default by design? Owner decision needed. (OBSERVED inconsistency.)
- **Q2. What is the title identity key?** Is it serial + region + revision + disc index (`DiscIdentity`, 7), and how does the Project record disc order for multi-disc titles? (UNVERIFIED.)
- **Q3. Does a host VSync or frame-pacing choice affect guest-visible timing?** The guest VBlank model is in `DeviceScheduler` and `BiosTimerVblankIrqHandler`, and the OpenBIOS `waitVSync` field flips at VBlank (`DeviceSchedulerTests.cs:274`, 2.8). If that model is the only timing source, VSync is host-only. Whether a host presentation choice ever feeds back into the guest is UNVERIFIED and must be checked before the key is placed (section 3.2).
- **Q4. Invalid-value policy.** Should an invalid environment variable disable a feature (current trace behaviour, 2.6) or fail the run (CLI behaviour, 2.1)? A single policy is needed.
- **Q5. Where does the profile (production or debug) live?** In `runtime-host.json` (needs schema 2, section 4) or only in the packaging record? Owner decision.
- **Q6. Is the Project directory a shareable unit?** Section 5.1 assumes it is, and keeps path keys out of it. If the owner decides it is not, that changes only where non-path keys live, not the rule that paths never enter it. Confirm the intended sharing model.
- **Q7. Should the Studio write `runtime-config.json`, or only read it?** A settings UI is needed for #40 and does not exist today (2.3). Owner decision on who owns the write path, and whether the Studio also writes the per-user store.
- **Q8. Memory card: which layer chooses the card file?** `MemoryCardSlotConfiguration` exists in Core (2.8), but no CLI or GUI setter was found, and the packaged host has no card input (H1 to H8). Is the intended entry point the per-user store with a launcher, or a package-time field? (UNVERIFIED call sites outside Infrastructure.)
- **Q9. Controller mapping owner.** Does #47 own the binding file format, with #48 only referencing it, as section 3.2 assumes? (UNVERIFIED with #47's scope.)
- **Q10. Are config files in scope at all?** `headless-cli.md:405-407` defers config files to Issue #15 (2.9). This design assumes #48 may introduce a runtime config file. Confirm that this does not collide with #15.
- **Q11. Does the issue's completion criterion "Debug/production configuration separated" require a runtime switch, or only a documented profile?** Section 9 assumes a profile field. (UNVERIFIED with issue owner.)
- **Q12. Issue comments and timeline.** The issue reports zero comments at read time (CONFIRMED), but the timeline was not read (`gh` not authenticated; UNVERIFIED). Any decision recorded there would change this design.
- **Q13. ADR-027 Step C baseline.** Step C lists two packaged inputs (`runtime-host.json`, `artifact-image.bin`), while the shipped host also reads `firmware-boot.json` and `runtime/firmware/` AOT inputs (H4, H5), and the ADR never mentions `firmware-boot` (2.9). Should the ADR be amended to record the firmware inputs as a baseline? Separately, any future host setting that reaches a packaged run (schema 2, a new packaged file, or a new host CLI flag) is a baseline change and needs the owner's approval.
- **Q14. Per-user store location and convention.** Which per-user application data location should hold path-valued keys, and does the repository already have a per-user settings convention? None was confirmed (5.1). UNVERIFIED.

## 12. Verification status of this document

- Source reads and git log were used for every CONFIRMED item. Line numbers are as of `2e3ecad`.
- The grep count in 2.6 uses the pathspec stated there. Counts that include test sources were reviewer-reported, not re-run here.
- No build, test, or run was performed. Any claim about runtime behaviour is UNVERIFIED or PREDICTED.
- No file other than this one was changed.
