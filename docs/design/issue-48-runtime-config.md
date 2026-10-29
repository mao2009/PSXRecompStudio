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

### 2.2 Runtime host (`psxrecomp-runtime-host`)

- `src/PSXRecomp.RuntimeHost/Program.cs`: positional manifest path plus options `--diagnostics <path>` (line 59), `--loaded-code-diagnostics <path>` (line 72), `--frame-out <path>` (line 95). CONFIRMED.
- Path-collision checks between report targets and inputs (`Program.cs` around lines 141-205 and 421-520, CONFIRMED by reading the signatures).
- The host reads its configuration only from `runtime-host.json` (`RuntimeHostManifest`, see 2.5). It does not read the Studio's project file.

### 2.3 Studio GUI (`PSXRecompStudio`)

- `src/PSXRecompStudio/Program.cs` (23 lines) forwards `args` to Avalonia's `StartWithClassicDesktopLifetime(args)` and does not parse any option (CONFIRMED).
- `src/PSXRecompStudio/Views/MainWindow.axaml` exposes two actions, `RunDiagnosticTitleButton` (line 51) and `RunRealTitleButton` (line 68), and status text blocks. There is no settings, options, or preferences control (CONFIRMED). A search for `Settings`, `Options`, `Preferences`, and `ConfigView` in `src/` returned no UI type (OBSERVED).
- `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:19-23` exposes constant display strings (`AppName`, `Version`, `Status`, runtime and platform info). No user-settable runtime property exists (CONFIRMED).
- Execution budgets are hard-coded constants in the service layer: `RealRomTitleExecutionService.DefaultOuterBudget = 64` (line 68) and `DefaultSegmentBudget = 4096` (line 71) (CONFIRMED).
- Memory cards: `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs` implements file-backed card storage (CONFIRMED). The Studio view model does not expose a slot selection (OBSERVED).

### 2.4 Game window (`PSXRecomp.GameWindow`)

- `App.ManifestPath` is set from `args[0]`, the packaged `runtime-host.json` (`src/PSXRecomp.GameWindow/Program.cs`, `App.axaml.cs:10`, CONFIRMED). If it is `null`, the window does not start a run (`MainWindow.axaml.cs:60`, CONFIRMED).
- Frames are passed from a one-slot `LiveFrameSlot` (`MainWindow.axaml.cs:20-21`, CONFIRMED). No vsync, resolution, or backend option is present (OBSERVED).

### 2.5 Persisted configuration files

| File | Writer | Reader | Versioning | Source |
|---|---|---|---|---|
| `project.json` (Project manifest) | `FileProjectMetadataStore.Save` (`FileProjectMetadataStore.cs:22`) | `FileProjectMetadataStore.Load` (`:52`) | `ProjectSchema.ProjectFormatVersion = 1` (`ProjectSchema.cs:19`) | CONFIRMED |
| `runtime-host.json` (RuntimeHostManifest) | `PackageCommand` (`PackageCommand.cs:234-235`) | `RuntimeHostManifestJson.Parse` (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:113`), `PSXRecomp.RuntimeHost/Program.cs` | `RuntimeHostManifest.CurrentSchemaVersion = 1` (`RuntimeHostManifest.cs:57`) | CONFIRMED |
| Firmware boot manifest | `FirmwareDistributionBuilder` (`FirmwareDistribution.cs:445`) | runtime launcher | its own `FirmwareBootManifest.CurrentSchemaVersion` | CONFIRMED |

The `project.json` record carries only four fields: `SchemaVersion`, `ArtifactKind`, `ProjectId`, `InputSha256` (`ProjectManifestDocument.cs:21-29`, CONFIRMED). It contains no runtime setting. The file is documented as "the minimal, versioned Project/Workspace management-data manifest" (`ProjectManifestDocument.cs:6`, CONFIRMED).

The `RuntimeHostManifest` record (`RuntimeHostManifest.cs:40-54`, CONFIRMED) carries the runtime settings that reach the packaged run:

- `SchemaVersion`, `Artifact`, `GuestFirmware`, `MixedFallback`
- `FallbackSegmentInstructionBudget`, `FallbackMaxTransitions`
- `ImageLoadAddress`, `Image`, `BlockEntryPcs`, `ProgramEnd`, `ResultRegister`, `EntryPc`
- `RunTimeoutMs`
- `BiosProvenance` (optional)

Its reader validates fail-closed: a schema other than 1 is rejected (`RuntimeHostManifest.cs:133-136`), `RunTimeoutMs <= 0` is rejected (`:173`), and mixed fallback with a non-positive budget is rejected (`:187`), all CONFIRMED.

Schema versions are checked by strict equality in both `ProjectMetadataSerializer` (`ProjectMetadataSerializer.cs:81-85`, CONFIRMED) and `RuntimeHostManifestJson` (`:133`, CONFIRMED). No migration code path exists in either (CONFIRMED by reading both readers).

### 2.6 Environment variables

Runtime and diagnostic switches read with `Environment.GetEnvironmentVariable` or `getenv` in generated C (CONFIRMED by the 19-match grep for `GetEnvironmentVariable` / `getenv`):

| Variable | Read at | Effect | Parse rule on bad value |
|---|---|---|---|
| `PSXRECOMP_BIOS_ROM` | `RecompiledArtifactCodeGen.cs:629` (constant), `:639` (generated C `getenv`); `PSXRecomp.RuntimeHost/Program.cs:239` | Path to a user-supplied BIOS image. Its contents are not copied into the distribution (`docs/runtime/openbios-firmware-backend.md:504`, CONFIRMED). | Missing or wrong-size file is an error at run time (`openbios-firmware-backend.md:510`, CONFIRMED by doc) |
| `PSXRECOMP_IPC_PROFILE` | `Infrastructure/Standalone/IpcProfile.cs:29`, `:48`, `:50` | Host IPC profile output path, opt-in (`openbios-firmware-backend.md:559`) | Non-empty path enables it (CONFIRMED) |
| `PSXRECOMP_TRANSITION_ATTRIBUTION` | `TransitionAttributionRecorder.cs:17`, `:35`, `:37` | Output path for transition attribution, opt-in (`openbios-firmware-backend.md:546`) | Non-empty path enables it (CONFIRMED) |
| `PSXRECOMP_NATIVE_GUARD_STATS` | `Infrastructure/GuardStatsScope.cs:34` | Enables guard statistics when non-empty | Non-empty enables (CONFIRMED) |
| `PSX_GUARD_STATS_FILE` / `_RUN_ID` / `_PERIOD` / `_ABORT_AFTER` | `GuardStatsScope.cs:15, 18, 21, 24` | Guard statistics output, run id, period, and abort threshold | UNVERIFIED (parse code not read) |
| `PSX_UNIT_TRACE_FILE` | `Infrastructure/UnitTraceRun.cs:18`, `:24-26` | Selects trace build and output path (`docs/development/unit-trace.md:21`) | Empty disables (CONFIRMED) |
| `PSX_UNIT_TRACE_MAX_BYTES` | generated C, `Core/Recompiler/RecompilerHostCodeGen.cs:1155-1159` | Trace cap, default 64 MiB (`unit-trace.md:22`) | Invalid value disables tracing for that run and the run proceeds untraced (`unit-trace.md:22`, CONFIRMED) |
| `PSX_TRACE_SESSION` | generated C, `RecompilerHostCodeGen.cs:1121`, `:1126` | Session id passed parent to child (`unit-trace.md:40`) | Kept only if a short `[0-9A-Za-z-]` token (CONFIRMED) |

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
| VSync / frame pacing | No symbol found by search for `VSync`, `Vsync`, `FramePacing` in `src/` | none | OBSERVED |
| Audio | `ISpu` interface (`src/PSXRecomp.Core/Runtime/ISpu.cs`) and a Rust `spu.rs` (`src/PSXRecomp.Native/rust/src/spu.rs`); no host audio output class found | none | CONFIRMED (files), OBSERVED (no host sink) |
| Controller | `PhysicalControllerState<TDeviceKind>` (`src/PSXRecomp.Core/Runtime/Input/PhysicalControllerState.cs:20`), whose comment says it is "owned by a future OS/device adapter" (lines 5-16); `InputBindingMap` (`InputBindingMap.cs:114`) | none reachable from CLI or GUI | CONFIRMED |
| Memory Card | `MemoryCardSlotConfiguration(Slot1Path, Slot2Path)` (`src/PSXRecomp.Core/MemoryCard/MemoryCardSlots.cs:44`); consumed by `RecompiledHostExecutionEngine` (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:141`) | no CLI option, no GUI control, no manifest field | CONFIRMED (type), OBSERVED (no CLI/GUI setter) |
| Disc / multi-disc | `DiscIdentity` with `DiscIndex` (`src/PSXRecomp.Core/TitleIdentity/DiscIdentity.cs:10-14`) | no selection option found | CONFIRMED (type), OBSERVED (no selector) |
| Graphics backend | Software GPU model (`src/PSXRecomp.Core/Runtime/Gpu/GpuDevice.cs`, `GpuRasterizer.cs`); frames shown through an Avalonia bitmap | no backend selection found | OBSERVED |
| Debug / trace | See 2.6 (environment variables) and CLI `--report`, `--frame-evidence`, runtime-host `--diagnostics` | no single switch; no debug/production profile | CONFIRMED |
| Runtime environment | `PSXRECOMP_BIOS_ROM` and `RuntimeHostManifest` | split across env and manifest | CONFIRMED |

Summary: of the nine areas the issue names, one (Memory Card) has a typed model in Core with no user entry point, one (Controller) has an input model with no OS adapter, and the rest have no model at all. Execution budgets and timeouts are the only area with a real, shipped, user-visible configuration surface (CLI options and manifest fields).

### 2.9 Project-level and ADR constraints

- ADR-027 (`docs/adr/027-standalone-artifact-runtime-integration.md`): the packaged artifact runs with no Studio at run time (CONFIRMED from the ADR's first section). Any setting the packaged run needs must therefore be frozen into the artifact at packaging time, not read from a live Studio state.
- `docs/development/headless-cli.md:405-407` (CONFIRMED): "config files ... belongs to Issue #15 and is deliberately absent here." Config-file support was explicitly deferred to Issue #15 and is not part of #460's CLI.
- `docs/development/headless-cli.md:232` (CONFIRMED): the shared `ArtifactJson` serializer configuration is used by the Studio. This is the only shared JSON configuration named in the docs.

## 3. Shared runtime configuration schema (proposal)

### 3.1 Two-tier model

The schema must separate two kinds of value, because they have different lifetimes:

1. **Execution settings (frozen).** Values that change what the guest runs or how long it may run: budgets, timeouts, mixed-fallback enable and limits, guest firmware on or off, BIOS provenance label, entry roots. These are resolved once at packaging time and written into `runtime-host.json` (or a later schema version of it). They are never read from a user file at run time, because the packaged run has no Studio (ADR-027).
2. **Host settings (live).** Values that affect only the host and not guest semantics: window size, presentation, frame output path, input device mapping, memory-card file locations on the host, diagnostics output. These may be read at run time by the host, but must not alter guest-visible behaviour.

This split is the central recommendation. It keeps the Runtime execution contract unchanged: the guest sees only frozen values, and host settings cannot reach the Runtime's inputs except through the existing manifest and the existing host adapters.

### 3.2 Proposed schema (logical, not a file format yet)

```text
RuntimeConfig (schemaVersion: int, kind: "psxrecomp.runtime.config")
  execution:              // frozen at package time
    mixedFallback:        bool
    fallbackSegmentBudget: uint   (positive; default 1_000_000 for run)
    fallbackMaxTransitions: uint  (positive; default 100_000)
    runTimeoutMs:         int     (positive; bounded; GUI cap 4 h)
    guestFirmware:        bool
    segmentBudget:        uint    (positive; default 1_000_000)
    entryRoots:           uint[]  (sorted, unique)
  host:                   // read at run time, never affects guest semantics
    display:
      windowSize:         { width, height } | null
      presentation:       "fit" | "integer" | "stretch"   // UNVERIFIED need
      vsync:              bool | null                      // UNVERIFIED need
    input:
      ports:              [ { port: 1|2, device: "pad"|"none", bindings: <InputBindingMap> } ]
    memoryCard:
      slot1Path:          string | null   // host path; not in the manifest
      slot2Path:          string | null
    diagnostics:
      frameOutPath, diagnosticsPath, loadedCodeDiagnosticsPath: string | null
  title:                  // optional per-title overlay, see section 7
    identity:             { serial, region, revision, discIndex }
    overrides:            (subset of execution and host)
```

Notes:

- Resolution, VSync, audio, and graphics backend are placed under `host`. They are not guest-visible in this design (PREDICTED: for PS1 the guest sees a fixed video timing; a host VSync choice only changes presentation pacing). This must be checked against the GPU and timing models before it is accepted (UNVERIFIED).
- Audio has no host sink in the repository (OBSERVED, 2.8), so the audio section is reserved, not implemented. Its key set must not be fixed until a host audio adapter is designed.
- Memory card paths are host paths. They must not be written into `runtime-host.json` if that file is meant to be shareable. Keep them in `host` and out of the packaged manifest (PREDICTED; the manifest currently has no card field, CONFIRMED).
- BIOS: the BIOS location stays an environment variable or a CLI option. The configuration file stores only a provenance label, never a path to a real user BIOS or any bytes or digests of it.
- Controller: `InputBindingMap` is the natural holder of bindings. The `input` section references it, and its shape is decided in #47, not here.

### 3.3 Field rules

- Every key has one owner module and one default defined in code, not in the file (single table of defaults; see 6.3).
- Unknown keys fail the load (fail closed). No silent drop.
- Numeric keys have explicit bounds. Timeouts are always finite (the 4 h GUI cap at `PackageCommand.cs:52` is the precedent).
- Paths are stored as given and resolved relative to the config file's directory, never to the working directory (PREDICTED; this avoids the ambiguity between CLI and GUI launches).

## 4. Relation to the existing manifest schema

- The existing `RuntimeHostManifest` (schema 1) already carries every execution value listed in 3.2 `execution`, with the same field names in camel case. The design should map `RuntimeConfig.execution` to these fields, not replace them.
- Adding any new execution field to `runtime-host.json` changes the manifest schema, so it requires `CurrentSchemaVersion` to advance (2) and the reader to accept 2 only with that version (CONFIRMED rule at `RuntimeHostManifest.cs:133`). That is a change to the packaged-artifact contract, so it is out of scope for this issue unless the issue owner accepts it. This document does not propose a schema 2.
- Host-only keys (3.2 `host`) do not enter `runtime-host.json` at all. They are carried by the GUI or CLI invocation (or a host-side file next to the manifest, see 6.2).

## 5. Relation to project configuration

- `project.json` is the Project management manifest (`ProjectManifestDocument.cs:6`). It records identity (`ProjectId`, `InputSha256`) and format (`SchemaVersion`, `ArtifactKind`). It is not a settings store, and it should stay that way (PREDICTED).
- Runtime configuration is a separate file, in the Project directory, named `runtime-config.json` (proposed; name UNVERIFIED against any existing convention).
- Precedence: a project's runtime configuration overrides the application default, and a per-title override (section 7) overrides the project's runtime configuration. A CLI option overrides both for one invocation. See 6.2.
- `project.json` gains no runtime fields. Its single-purpose meaning is preserved, and a runtime-config change never needs a Project format bump (PREDICTED).

## 6. Shared GUI and CLI method

### 6.1 One model, two front ends

- The `RuntimeConfig` record and its validation live in `PSXRecomp.Core` (domain, no I/O). This matches the layering in `src/architecture.contract.json` and the `[Domain]` attribute already used in `ProjectSchema` (CONFIRMED).
- Load and save live in `PSXRecomp.Infrastructure`, following the `FileProjectMetadataStore` pattern (CONFIRMED pattern).
- The CLI maps its existing parsed options onto a partial `RuntimeConfig` overlay (the same `ParsedArguments` record, `Program.cs` near 508, CONFIRMED). The CLI does not gain a new parser.
- The Studio binds its settings UI (not present today, 2.3) to the same `RuntimeConfig` model. It must not keep a separate copy of any default.
- Budgets currently duplicated in `RunCommand`, `RealRomTitleExecutionService`, `MixedFallbackOptions`, and `FirmwareDistributionRequest` (2.7) are replaced by references to the single defaults table (6.3). This is a refactor, and it changes no value unless the owner decides a value is wrong.

### 6.2 Precedence (lowest to highest)

1. Compiled-in defaults (the single defaults table, 6.3).
2. Application-level user settings (per user, host only). Optional; PREDICTED to be needed by the GUI only.
3. Project `runtime-config.json`.
4. Per-title overlay for the matching title identity (section 7).
5. Environment variables for diagnostic keys only. Environment variables never set execution settings (proposal, see 9.2).
6. CLI options for one invocation.

A value that a higher layer does not set is inherited, not reset. Each effective value must record its source in the output (the `--json` result or the packaged `README`), so a result can be traced to a layer.

### 6.3 Single defaults table

One module in `PSXRecomp.Core` holds every default in 2.7, with a name and a comment for each. Existing constants are re-pointed to it. Each value keeps its current number until the owner changes it; the duplicated and inconsistent values (1_000_000 versus 4096, and 100_000_000 for firmware) are listed as open question Q1, not silently unified.

## 7. Per-title overrides

- **Key.** A title is identified by `DiscIdentity` (serial, region, revision, `DiscIndex`; `DiscIdentity.cs:10-14`, CONFIRMED). The override key is the serial plus region plus revision. The disc index is part of the key for multi-disc titles, so each disc has its own overlay (PREDICTED; UNVERIFIED how the Project records disc order).
- **Location.** Per-title overlays are stored in the Project directory, under a sub-folder keyed by the title identity, not in the application-wide settings. This keeps a title's settings with its project and avoids leaking another title's data.
- **Allowed keys.** Execution keys may be overridden per title (for example a larger budget for a slow title). Host keys may be overridden per title (for example controller mapping). Debug keys may not be made permanent per title in a production profile (see 9).
- **Applied at packaging time** for execution keys. The effective value is frozen into the manifest. This means a per-title override never needs a runtime lookup.
- **Not a source of identity.** An overlay never changes `ProjectId` or `InputSha256`, and it never changes which input file is accepted.
- **Mismatch.** If the overlay's identity does not match the loaded image's identity, the overlay is refused with a classified error, not ignored (PREDICTED; mirrors the fail-closed rule for manifests).

## 8. Configuration versioning and migration

### 8.1 Current behaviour (CONFIRMED)

- Each persisted file has its own integer version and a strict-equality check. Readers reject any other version, and there is no migration (2.5).
- `ProjectMetadataSerializer` says a shape or meaning change requires a bump (`ProjectSchema.cs:15-17`, CONFIRMED). That rule is good and should be kept.

### 8.2 Proposed policy for `runtime-config.json`

- Top-level `schemaVersion` (integer) and `kind` (`"psxrecomp.runtime.config"`), like `project.json`.
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
- Debug and trace outputs are off unless named (2.6: all debug variables are empty-means-off, CONFIRMED).
- No user path is embedded in a shareable file. BIOS data and digests are never written into configuration (`openbios-firmware-backend.md:504`, CONFIRMED).
- Unknown keys fail closed (3.3).
- Invalid values fail with a classified error, not a silent fallback. Note that the trace variable `PSX_UNIT_TRACE_MAX_BYTES` currently disables tracing on an invalid value and lets the run proceed (2.6, CONFIRMED). That is a different policy from the CLI options, which reject. Q4 asks which policy is wanted.

### 9.2 Debug versus production

- Introduce a `profile` in the config: `production` or `debug`. Debug enables the trace and guard-statistics keys (2.6) and the `--report`, `--frame-evidence`, and `--diagnostics` outputs. Production refuses those keys.
- A packaged artifact records its profile. Because a manifest change is a schema change (section 4), the first version of this should keep the profile in the packaging log and README, not in `runtime-host.json`. Moving it into the manifest is open question Q5.
- Environment variables for debug remain allowed in both profiles, but they cannot change execution settings (6.2 layer 5). This keeps a debug session from silently changing the guest's budgets.

## 10. Recommendation

1. Adopt the two-tier model (3.1): frozen execution settings and live host settings. This is the only split that respects ADR-027 and keeps the Runtime execution contract unchanged.
2. Keep `runtime-host.json` schema 1 as the execution carrier. Do not add fields in this issue. Map the existing CLI options to it (2.1, 2.5).
3. Add `runtime-config.json` (a Project-level file, versioned like `project.json`) for host and per-title settings. Its model lives in Core, and its load and save live in Infrastructure (6.1).
4. Create a single defaults table and re-point the constants (6.3). Resolve the inconsistent segment-budget defaults first (Q1), because the table cannot be written without it.
5. Implement per-title overlays only after the title-identity key is confirmed (Q2).
6. Keep audio, VSync, and graphics backend as reserved names in the design until their host adapters exist (2.8, 3.2). Do not invent keys for them.
7. Split debug and production profiles (9.2). Keep debug keys out of execution settings.

Suggested order of follow-up work (for the owner to schedule; not started here): defaults table, then runtime-config version 1 with only the existing CLI keys, then per-title overlay, then host keys one area at a time with #40 and #47.

## 11. Open questions

- **Q1. Which segment-budget default is correct?** The CLI run uses `1_000_000`, the Studio real-title path uses `4096`, and firmware fallback uses `100_000_000` (2.7). Should they be unified, or does each path keep its own default by design? Owner decision needed. (OBSERVED inconsistency.)
- **Q2. What is the title identity key?** Is it serial + region + revision + disc index (`DiscIdentity`, 7), and how does the Project record disc order for multi-disc titles? (UNVERIFIED.)
- **Q3. Does a host VSync or frame-pacing choice affect guest-visible timing?** If the guest's timing is fixed by the emulated model, VSync is host-only. This must be checked against the GPU and timing code before the key is placed (UNVERIFIED; section 3.2).
- **Q4. Invalid-value policy.** Should an invalid environment variable disable a feature (current trace behaviour, 2.6) or fail the run (CLI behaviour, 2.1)? A single policy is needed.
- **Q5. Where does the profile (production or debug) live?** In `runtime-host.json` (needs schema 2, section 4) or only in the packaging record? Owner decision.
- **Q6. Is `runtime-host.json` a shareable file?** If yes, it must never carry host paths; memory-card paths then move entirely to `host`. Confirm the intended sharing model (PREDICTED need).
- **Q7. Should the Studio write `runtime-config.json`, or only read it?** A settings UI is needed for #40 and does not exist today (2.3). Owner decision on who owns the write path.
- **Q8. Memory card: which layer chooses the card file?** `MemoryCardSlotConfiguration` exists in Core (2.8), but no CLI or GUI setter was found. Is the intended entry point the host config (section 3.2 `host.memoryCard`) or the package? (UNVERIFIED call sites outside Infrastructure.)
- **Q9. Controller mapping owner.** Does #47 own the binding file format, with #48 only referencing it, as section 3.2 assumes? (UNVERIFIED with #47's scope.)
- **Q10. Are config files in scope at all?** `headless-cli.md:405-407` defers config files to Issue #15 (2.9). This design assumes #48 may introduce a runtime config file. Confirm that this does not collide with #15.
- **Q11. Does the issue's completion criterion "Debug/production configuration separated" require a runtime switch, or only a documented profile?** Section 9 assumes a profile field. (UNVERIFIED with issue owner.)
- **Q12. Issue comments and timeline.** The issue reports zero comments at read time (CONFIRMED), but the timeline was not read (`gh` not authenticated; UNVERIFIED). Any decision recorded there would change this design.

## 12. Verification status of this document

- Source reads and git log were used for every CONFIRMED item. Line numbers are as of `2e3ecad`.
- No build, test, or run was performed. Any claim about runtime behaviour is UNVERIFIED or PREDICTED.
- No file other than this one was changed.
