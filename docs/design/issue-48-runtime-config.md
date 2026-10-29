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

### 2.2 Runtime host (`PSXRecomp.RuntimeHost`) and the game window: every input they read

The packaged host is the process a distribution launches. Its comment at `src/PSXRecomp.RuntimeHost/Program.cs:20-21` describes the target as a "machine with no Studio, no CLI and no development repository present." (line 21, quoted exactly; CONFIRMED). The complete input list is H1 to H15. It covers what the host reads directly, what the game window reads in-process (H15), and what the Infrastructure code they call reads. Per-variable detail is in 2.6.

| # | Input | Read at | Applies to | Label |
|---|---|---|---|---|
| H1 | `runtime-host.json` (argument 0) | `RuntimeHost/Program.cs:125-130` (path), `:218` (parsed by `RuntimeHostManifestJson.Parse`) | every package | CONFIRMED |
| H2 | Artifact named by `manifest.Artifact` (`prebuiltArtifactPath`). Required integrity check before use: SHA-256 against `binary.sha256` in `runtime/build-manifest.json` (section 3.4) | `RuntimeHost/Program.cs:229` | every package | CONFIRMED (read site); the check is required and not found in the code read (OBSERVED) |
| H3 | Image named by `manifest.Image` (`artifact-image.bin`). Required integrity check before use: SHA-256 against `packaged.imageSidecar.sha256` in `runtime/build-manifest.json` (section 3.4) | `RuntimeHost/Program.cs:244-248` | HLE packages only; the file is written by the HLE packaging path only (`PackageCommand.cs:231`, 2.9) | CONFIRMED (read site); the check is required and not found in the code read (OBSERVED) |
| H4 | `firmware-boot.json` | presence `FirmwareDistribution.cs:599-600` via `IsFirmwareDistribution`, called at `RuntimeHost/Program.cs:235`; parsed at `FirmwareDistribution.cs:651` | firmware distributions | CONFIRMED |
| H5 | `runtime/firmware/` AOT inputs, each re-verified against its recorded SHA-256. The comment is at `FirmwareDistribution.cs:677-678` ("P1-3"); the verification call is `FirmwareAotBundle.Verify` at `FirmwareDistribution.cs:679`; bundle layout comment at `:106` | firmware distributions | CONFIRMED (verification call and comments read; the hash function body was not read, UNVERIFIED) |
| H6 | Environment `PSXRECOMP_BIOS_ROM`, a path to a user-supplied BIOS file. Read by the host for firmware runs (`RuntimeHost/Program.cs:239`). The artifact the host runs opens the BIOS at run time (`RecompiledArtifactCodeGen.cs:639-641`), but only in firmware packages: the branch is `embedded_bios_external` (`:1028-1029`), set from `externalBios` (`:456`), which `FirmwareDistribution.cs:416` passes as true; the default is false (`RecompiledArtifactCodeGen.cs:317`) | firmware runs and firmware packages | CONFIRMED |
| H7 | Host CLI flags `--diagnostics <path>` (`RuntimeHost/Program.cs:59`), `--loaded-code-diagnostics <path>` (`:72`), `--frame-out <path>` (`:95`) | optional, every package | CONFIRMED |
| H8 | Input listings for report-target collision checks. `ReadInputPaths` (`RuntimeHost/Program.cs:538-570`) names the specific input files: the manifest, the artifact, the image, and for firmware the firmware boot manifest and the files it names. Only when a manifest or firmware boot manifest fails to parse does it fall back to `ConservativeInputs` (`:548`, `:562`), which lists the disc and firmware directories and the parent directory (`:577-587`). Read-only, no configuration meaning. Called only when a report target is set (`:154-158`) | when a report target is set; the directory listings only on the fallback | CONFIRMED |
| H9 | `runtime/disc/<leaf>`: the disc image named by `boot.DiscFile` (`FirmwareBootManifest.cs:20`). Path built at `FirmwareDistribution.cs:666`; refused if a symbolic link (`:667-670`); SHA-256 checked against `boot.DiscSha256` at `:672`; opened by `DiscSectorSourceOpener.Open` at `:683` | firmware distributions | CONFIRMED |
| H10 | `runtime/firmware/<roots leaf>` (`boot.RootsFile`) and `runtime/firmware/<load-images leaf>` (`boot.LoadImagesFile`), passed to `FirmwareAotPlan.Build` at `FirmwareDistribution.cs:686-688`; both must be AOT inputs (`FirmwareBootManifest.cs:148`) | firmware distributions | CONFIRMED |
| H11 | Environment `PSXRECOMP_IPC_PROFILE`: `IpcProfile.FromEnvironment()` through the engine's `Profile` initializer (`Infrastructure/RecompiledHostExecutionEngine.cs:1830`; `Standalone/IpcProfile.cs:48-50`), and the output path at `FirmwareDistribution.cs:730` | opt-in, every package | CONFIRMED |
| H12 | Environment `PSX_UNIT_TRACE_FILE`: `UnitTraceRun.CurrentPath` (`UnitTraceRun.cs:23-26`) through the engine's `_unitTrace` initializer (`RecompiledHostExecutionEngine.cs:1893`). This is a host-side read in any engine instance. The child-side reads are in generated trace code only, which exists only in a trace build (`traceUnits`, `RecompilerHostCodeGen.cs:302`, which no packaging call requests, 2.6): `PSX_UNIT_TRACE_FILE` at `RecompilerHostCodeGen.cs:1158`, `PSX_UNIT_TRACE_MAX_BYTES` at `:1159`, `PSX_UNIT_TRACE_FROM` and `PSX_UNIT_TRACE_TO` at `:1160-1161`, and `PSX_TRACE_SESSION` at `:1126` | host side: any engine; child side: trace builds only | CONFIRMED (sites); the timing effect of a trace build is UNVERIFIED |
| H13 | Environment `PSXRECOMP_TRANSITION_ATTRIBUTION`: `TransitionAttributionRecorder.FromEnvironment()` (`Standalone/TransitionAttributionRecorder.cs:35`, `:37`), used in the firmware launch path (`FirmwareDistribution.cs:705`), with the output path read at `:741` | firmware runs, opt-in | CONFIRMED |
| H14 | Guard-stats keys. Build-time: `PSXRECOMP_NATIVE_GUARD_STATS` (`GuardStatsScope.cs:34`, `FirmwareDistribution.cs:405-406` (`Generate(` at 405, the `guardStats` argument at 406)). Measurement, run-time in the guard artifact: `PSX_GUARD_STATS_FILE`, `_RUN_ID`, `_PERIOD` (generated reads at `RecompilerHostCodeGen.cs:983`, `:994`, `:1018`, inside `EmitGuardStatsSupport`, `:935`). Run-affecting: `PSX_GUARD_STATS_ABORT_AFTER` (generated read at `:1021`; can end a run early). All of these exist only when `s_guardStats` is set (`:284`), that is, in a guard build. The engine writes the same keys into a guard-stats child (`RecompiledHostExecutionEngine.cs:1341-1349`, a write) | guard builds only | CONFIRMED |
| H15 | Game window, in-process: the same files as H1 to H3 for HLE packages, read by the window process. The H2 and H3 integrity checks of section 3.4 must run before these reads. `PackagedArtifactLiveRunner.cs:38` (manifest), `:44-46` (artifact and image); guest-firmware manifests are refused at `:39-41`. Reached from `GameWindow/MainWindow.axaml.cs:69` | HLE packages, game window process | CONFIRMED |

Further facts:

- **Not read by the packaged host.** `project.json`, any runtime configuration file, and any Studio state. OBSERVED: a search of `src/PSXRecomp.RuntimeHost/*.cs` for `project`, `Studio`, `ProjectManifest` and `runtime-config` finds matches only in comments: `Program.cs:21` ("Studio") and `UnverifiedIdentityProbe.cs:9` ("test project").
- **Written into a firmware distribution, not read.** `runtime/bios-content-diagnostic.txt`: the file name is at `FirmwareDistribution.cs:337`, the write at `:459-460`. It holds counts and a provenance string only (`BiosAddressGuard.cs:224-242`, `DiagnoseContent`). It is not among H1 to H15. The comment at `FirmwareDistribution.cs:442` says no BIOS bytes or digests are written; this counts-only file is a separate output (CONFIRMED).

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
| `runtime-host.json` (RuntimeHostManifest) | `PackageCommand` (write at `PackageCommand.cs:264`; serialization at `:249`; `RuntimeHostManifest` construction at `:234-235`) | `RuntimeHostManifestJson.Parse` (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:113`), `PSXRecomp.RuntimeHost/Program.cs` | `RuntimeHostManifest.CurrentSchemaVersion = 1` (`RuntimeHostManifest.cs:57`) | CONFIRMED |
| `firmware-boot.json` (FirmwareBootManifest) | `FirmwareDistribution.cs:455` | `FirmwareDistribution.cs:651` | its own `FirmwareBootManifest.CurrentSchemaVersion` (`FirmwareDistribution.cs:445`, the argument of the construction at `:444`; definition at `FirmwareBootManifest.cs:42`) | CONFIRMED |

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

Scope: `git grep -nE 'GetEnvironmentVariable|getenv' -- src ':(exclude)src/PSXRecomp.Tests'` returns 19 matching lines in 8 files (CONFIRMED). Those lines include the `PATH` read (`PackageCommand.cs:421`), which is in the table. The table covers every variable on those lines, plus the child-process writes the engine makes for the same keys.

Column meanings:

- **Build or run**: "build" means the variable is read while packaging or code generation decides what an artifact contains. "run" means it is read when a process runs.
- **In packaged artifact**: whether the generated C that a distribution ships can contain the read.
- **Proposed production action (not implemented)**: "Refused" means packaging would reject the build flag that enables the read. "Not refusable" means the read is in shared host code that packaging does not generate, so packaging cannot remove it. For those, the design only forbids a launcher from setting the variable.

| Variable | Read sites | Build or run | In packaged artifact | Proposed production action (not implemented) | Parse rule on a bad value |
|---|---|---|---|---|---|
| `PSXRECOMP_BIOS_ROM` | constant `RecompiledArtifactCodeGen.cs:629`; artifact `getenv` `:639-641` (run time, opens the BIOS); host `RuntimeHost/Program.cs:239` | run | firmware packages only: the C branch is `embedded_bios_external` (`:1028-1029`), emitted from `externalBios` (`:456`); `FirmwareDistribution.cs:416` passes true, and the default is false (`RecompiledArtifactCodeGen.cs:317`) | Required input for firmware runs; not refused | Missing or wrong-size file is a run-time error (`openbios-firmware-backend.md:510`, CONFIRMED by doc) |
| `PSXRECOMP_NATIVE_GUARD_STATS` | build: `GuardStatsScope.cs:34` (scope check), `FirmwareDistribution.cs:405-406` (`Generate(` at 405, the `guardStats` argument at 406) (`guardStats: GuardStatsScope.Enabled`). Run in the in-process engine: `RecompiledHostExecutionEngine.cs:183` (`GuardStats => GuardStatsScope.Enabled`), feeding `guardStats` at `:551` and `:1253` | build, and run in the in-process engine | yes, when set at packaging: `RecompilerHostCodeGen.cs:284` emits `EmitGuardStatsSupport` (`:935`) | Refused: the guard build flag would be rejected at packaging | Non-empty enables (`GuardStatsScope.cs:34`, CONFIRMED) |
| `PSX_GUARD_STATS_FILE` | `GuardStatsScope.cs:15`; generated `getenv` `RecompilerHostCodeGen.cs:983`; the engine writes it into a guard-stats child (`RecompiledHostExecutionEngine.cs:1341-1349`) | run, measurement | only in a guard build (`:284`) | Refused, through the guard build flag | Empty or absent: no report. A temp name of 1024 bytes or more returns without a dump (`:985-986`, CONFIRMED) |
| `PSX_GUARD_STATS_RUN_ID` | `GuardStatsScope.cs:18`; generated `getenv` `RecompilerHostCodeGen.cs:994`; engine write `:1341-1349` | run, measurement | only in a guard build | Refused, through the guard build flag | Empty or absent: a time and clock fallback. Otherwise hex, parsed by `strtoull` with no error check, so an unparsable value gives 0 and the fallback runs again (`:994-996`, CONFIRMED) |
| `PSX_GUARD_STATS_PERIOD` | `GuardStatsScope.cs:21`; generated `getenv` `RecompilerHostCodeGen.cs:1018`; engine write `:1341-1349` | run, measurement | only in a guard build | Refused, through the guard build flag | Decimal. Absent, 0 or non-decimal keeps the default period `0x400000` (`:964`, `:1018-1020`, CONFIRMED) |
| `PSX_GUARD_STATS_ABORT_AFTER` | `GuardStatsScope.cs:23-24` (declared "scope-only, test hook"); generated `getenv` `RecompilerHostCodeGen.cs:1021`; engine write `RecompiledHostExecutionEngine.cs:1349` | run, run-affecting (can end the run early) | only in a guard build (`:284`); absent from production builds | Refused, through the guard build flag | Decimal. Absent, 0 or non-decimal disables it. A positive N makes the generated tick call `_Exit(86)` once the iteration count reaches N (`:1021-1022`, `:1026`, CONFIRMED) |
| `PSXRECOMP_IPC_PROFILE` | `IpcProfile.cs:48-50`; engine `Profile` initializer `RecompiledHostExecutionEngine.cs:1830`; firmware launch `FirmwareDistribution.cs:730` | run, host side | no; read by host code, not by generated C | Not refusable: a launcher must not set it | Non-whitespace path enables it (`IpcProfile.cs:48`, `IsNullOrWhiteSpace`, CONFIRMED) |
| `PSXRECOMP_TRANSITION_ATTRIBUTION` | `TransitionAttributionRecorder.cs:35`, `:37`; firmware launch `FirmwareDistribution.cs:705`, `:741` | run, host side | no; host code | Not refusable: a launcher must not set it | Non-whitespace path enables it (`TransitionAttributionRecorder.cs:35`, `IsNullOrWhiteSpace`, CONFIRMED) |
| `PSX_UNIT_TRACE_FILE` | `UnitTraceRun.cs:23-26` (`CurrentPath`); engine `_unitTrace` initializer `RecompiledHostExecutionEngine.cs:1893`; selects trace codegen for the in-process engine (`:551`, `:1253`); generated `getenv` `RecompilerHostCodeGen.cs:1158` (trace builds only) | run, host side; generated read in trace builds only | no; no packaging call requests trace codegen (9.2) | Host-side read not refusable; a launcher must not set it | Empty disables (CONFIRMED) |
| `PSX_UNIT_TRACE_MAX_BYTES` | generated `getenv` `RecompilerHostCodeGen.cs:1159` (trace codegen, emitted only when `traceUnits`, `:302`) | run, trace build only | no | Same as `PSX_UNIT_TRACE_FILE` | Invalid value disables tracing for that run (`unit-trace.md:22`, CONFIRMED) |
| `PSX_UNIT_TRACE_FROM` / `PSX_UNIT_TRACE_TO` | generated `getenv` `RecompilerHostCodeGen.cs:1160-1161` | run, trace build only | no | Same as `PSX_UNIT_TRACE_FILE` | UNVERIFIED |
| `PSX_TRACE_SESSION` | generated `getenv` `RecompilerHostCodeGen.cs:1126` (the comment is at `:1121`); the parent sets the child value through `UnitTraceRun.ChildEnvironment` | run, trace build only | no | Same as `PSX_UNIT_TRACE_FILE` | Kept only if a short `[0-9A-Za-z-]` token (CONFIRMED) |
| `PATH` | `PackageCommand.cs:421` (`FindOnPath`, toolchain lookup) | build, package time only; not a run | no | Not a build flag; not affected | Not applicable |

Facts behind the "In packaged artifact" column:

- Packaging call sites that generate host dispatch code: `FirmwareDistribution.cs:405` passes `guardStats: GuardStatsScope.Enabled` and no `traceUnits`. `PackageCommand.cs:104` passes neither (CONFIRMED). `RecompilerHostCodeGen.Generate` has no other caller in packaging.
- The HLE artifact C comes from `RecompiledArtifactCodeGen`, which contains no `recompiler_trace`, `PSX_UNIT_TRACE`, `PSX_GUARD` or `psx_gs_` text (CONFIRMED by grep, count 0). Its only environment read is `PSXRECOMP_BIOS_ROM`.
- Trace codegen, `UnitTraceWriterSource`, is emitted only when `traceUnits` is set (`RecompilerHostCodeGen.cs:302`). Trace codegen is requested by the in-process engine only (`RecompiledHostExecutionEngine.cs:551`, `:1253`).

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

- ADR-027 Step C (`docs/adr/027-standalone-artifact-runtime-integration.md`, "Step C scope", lines 128-217, CONFIRMED). The ADR's distributed host runs "with no Studio, no CLI, and no .NET SDK at runtime" (lines 130-132). Its layout block (lines 149-157, CONFIRMED) is the baseline, in full. The quoted text is the ADR's; text after "Doc annotation" is this document's reading and is not in the ADR:
  - `<game>[.exe]`: ADR text "native AOT artifact (bridge-capable, direct-launch)". Doc annotation: the game executable; read by H2 and H15.
  - `runtime/PSXRecomp.RuntimeHost[.exe]`: ADR text "shared managed Runtime host". Doc annotation: the host binary itself.
  - `runtime/runtime-host.json`: ADR text "guest run configuration". Doc annotation: written for every package (`PackageCommand.cs:264`, 2.5).
  - `runtime/artifact-image.bin`: ADR text "embedded program image (sidecar)". Doc annotation: HLE packages only. `PackageCommand.cs:231` writes it on the non-firmware path. Firmware packages built through `FirmwarePackageCommand` (`PackageCommand.cs:72`, when firmware options are given) are not written by that line; this follows from the control flow, not from a test.
  - `README.txt`: ADR text only as a layout entry. Doc annotation: none beyond that.
  Doc annotation: the ADR does not mention the firmware inputs and outputs (`firmware-boot.json`, `runtime/firmware/`, `runtime/disc/`, `bios-content-diagnostic.txt`). The string `firmware-boot` has no match in that ADR (CONFIRMED by search).
  - **Decided (Q16, Option A, owner ruling, recorded 2026-10-29).** The game executable and the host binary are outside the Runtime Configuration contract. They stay in the ADR-027 baseline above. Their identity belongs to Artifact Provenance (#46, W14); section 3.4 states the split.
  - Whether the ADR is amended to list the firmware inputs and outputs, or the ADR is out of date, is UNVERIFIED (Q13).
- `docs/development/headless-cli.md:405-408` (CONFIRMED): "config files ... belongs to Issue #15 and is deliberately absent here." Config-file support was explicitly deferred to Issue #15 and is not part of #460's CLI.
- `docs/architecture-matrix.md` Layer rules (CONFIRMED): rule 1, Domain owns ports, never adapters; rule 2, Infrastructure executes host APIs (`System.IO.File`, `Directory`, `Process`, and so on); rule 3, Application does not acquire host resources directly.
- `src/architecture.contract.json:22` (CONFIRMED): `System.Environment` is a forbidden API in the Domain layer ("execution environment dependencies break determinism"). Line 37 forbids it in Test and line 48 in Generated; the contract does not restrict Infrastructure.

## 3. Shared runtime configuration schema (proposal)

### 3.1 Two tiers, and what the packaged host reads

The schema separates two kinds of value, because they have different lifetimes and different readers:

1. **Execution settings (frozen at package time).** Values that change what the guest runs or how long it may run: budgets, timeouts, mixed-fallback enable and limits, guest firmware on or off, BIOS provenance label, entry roots. They are resolved once at packaging time and written into `runtime-host.json` (schema 1, section 4). The packaged host reads them through H1.
2. **Host settings (launch-time).** Values that affect only presentation, input, and host-side output, not guest semantics: window size, presentation mode, input device mapping, memory-card file paths, diagnostics output paths. These are consumed by the launcher (Studio, GameWindow, or CLI `run`) before a run starts.

The packaged host reads no host setting in this design. Its complete read list is H1 to H15 in 2.2. No config file and no new environment variable is added to that list. The one new packaged file is the integrity carrier `runtime/build-manifest.json`, which the host reads for the H2 and H3 check (3.4). It is a new packaged input, decided under Q13. So:

- The ADR-027 Step C baseline is the full layout in 2.9: `<game>[.exe]`, `runtime/PSXRecomp.RuntimeHost[.exe]`, `runtime/runtime-host.json` (every package), `runtime/artifact-image.bin` (HLE packages only), and `README.txt`. The two binaries are outside the config contract (Q16, Option A, owner ruling), and their identity belongs to #46 (W14). Integrity still applies (section 3.4): H2 is checked against `binary.sha256` and H3 against `packaged.imageSidecar.sha256`, both in `runtime/build-manifest.json`. This design adds no input to the packaged run. The firmware inputs (H4, H5, H9, H10) and H6 to H8 and H11 to H14 are outside that baseline; they already ship and this design does not add them.
- A host setting that must reach the packaged run needs one of two changes, and each changes the Step C baseline: a new execution field in `runtime-host.json` (schema 2, section 4), or a new packaged input or host CLI flag. Such a change is listed as open question Q13 and is not proposed here.
- Memory-card paths are not read by the packaged host today (no field among H1 to H15). They are consumed only where a `RecompiledHostExecutionEngine` is constructed (Q8).

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
      ports:              [ { port: 1|2, device: "pad"|"none", bindings: <InputBindingMap> } ]   // not written until the binding schema is checked (5.1)

PerUserStore (not shareable; see section 5.1)     // path-valued keys only
  memoryCard:
    slot1Path:            string | null
    slot2Path:            string | null

CLI or environment only (never stored)
  --diagnostics, --loaded-code-diagnostics, --frame-out     // paths named per invocation

Title overlay (Project directory, per title; see section 7)
  identity:               { serial, region, revision, discIndex }
  overrides:              (subset of execution and non-path host keys)
```

Notes:

- Resolution, VSync, audio, and graphics backend are placed under `host`. They are not guest-visible in this design (PREDICTED: for PS1 the guest sees a fixed video timing; the host VSync choice would only change presentation pacing). This must be checked against the GPU and timing models before it is accepted (UNVERIFIED; Q3).
- Audio has no host sink in the repository (OBSERVED, 2.8), so the audio section is reserved, not implemented. Its key set must not be fixed until a host audio adapter is designed.
- Memory-card paths and diagnostic output paths are host paths. They never enter `runtime-host.json` (section 5.1 explains where they live).
- BIOS: the BIOS location stays an environment variable (H6) or a CLI option. The configuration stores only a provenance label, never a path to a real user BIOS, and never bytes or digests of it. `FirmwareDistribution.cs:442` says no BIOS bytes or digests are written. The counts-only `bios-content-diagnostic.txt` is a separate file (2.2; CONFIRMED).
- Controller: `InputBindingMap` is the natural holder of bindings. The `input` section references it, and its shape is decided in #47, not here.

### 3.3 Field rules

- Every key has one owner module and one default defined in code, not in the file (single table of defaults; see 6.3).
- Unknown keys fail the load (fail closed). No silent drop.
- Numeric keys have explicit bounds. Timeouts are always finite (the 4 h GUI cap at `PackageCommand.cs:52` is the precedent).
- Paths are stored as given and resolved relative to the file that holds them, never to the working directory (PREDICTED; this avoids the ambiguity between CLI and GUI launches). Only the per-user store holds paths (section 5.1).

### 3.4 Split with Artifact Provenance (#46, W14)

- **Dependency.** Artifact Provenance is designed in `origin/batch2/w14-artifact-provenance-design:docs/design/issue-46-artifact-provenance.md` (cited at W14 HEAD `0dbad5f`). That design is not merged. This section depends on it for the identity of the two binaries and for the digest carrier.
- **Config here (this issue).** The execution and host settings in `runtime-host.json` and `runtime-config.json`, the per-user store, the title overlays, the defaults, the precedence, and the versioning rules (sections 3 to 8). Nothing in this issue computes or stores a hash of a binary.
- **Identity there (#46).** The identity of `<game>[.exe]` and `runtime/PSXRecomp.RuntimeHost[.exe]`. W14 records the publish configuration, the RID, and the hash of the published host binary (its table at line 149). W14 also states that `RuntimeHostManifest` is not changed (its line 204), which matches this design's schema-1 decision (section 4).
- **Integrity requirement (owner ruling, Q16).** The packaged host must verify the SHA-256 of two inputs against the expected digests in the carrier below, and must fail closed on any failure.
  - H2, the game artifact named by `manifest.Artifact` (`RuntimeHost/Program.cs:229`), is checked against `binary.sha256`, with `binary.file` as the bare file name (W14 line 278). The check runs for every package, before the firmware branch at `Program.cs:235`.
  - H3, the program image sidecar named by `manifest.Image` (`RuntimeHost/Program.cs:244-248`), is checked against `packaged.imageSidecar.sha256`, with `packaged.imageSidecar.file` as the bare file name (W14 line 279). H3 applies to HLE packages only. Firmware packages have no sidecar, so H3 is not checked for them (W14 line 285). The write site is `PackageCommand.cs:231`, on the HLE path only (CONFIRMED, 2.9).
  - H15, the game window's in-process read of the same two files (`PackagedArtifactLiveRunner.cs:44-46`, reached from `GameWindow/MainWindow.axaml.cs:69`), must run the same two checks, with the same results, before the same reads. The window is another entry point to the same packaged run and cannot apply a weaker rule. If the window path does not verify, it must not start the run.
- **Verified bytes are the used bytes.** A hash of bytes that are not the bytes used proves nothing. Therefore:
  - H3: the host reads the image once into `imageBytes` (`Program.cs:248`), computes the SHA-256 of that same array, and only then builds `imageWords` from it. `PackagedArtifactLiveRunner.cs:46` has the same shape and must be changed the same way. No second read of the file is allowed between the hash and the use.
  - H2: the artifact is launched by the operating system from a path (`prebuiltArtifactPath`, `Program.cs:229`), and process creation takes a path, not a handle. A handle on the original file therefore cannot be the object that runs. Checking the path alone leaves the file replaceable between the hash and the launch. The method is fixed here as a private copy. The owner confirms it as Q18:
    1. The host creates a private directory for the run, with an unpredictable name and an access control list that gives write and delete rights only to the running user. The target platform here is Windows. The directory is outside the distribution.
    2. The host reads the original artifact once into a buffer, and writes that buffer to the private copy, created with create-new so that an existing file is an error.
    3. The host opens the copy for reading with sharing `FileShare.Read` only (no write or delete sharing), keeps that handle open from the hash until the process is created, and computes the SHA-256 over the bytes read through that handle. The value must equal `binary.sha256`.
    4. The host launches the copy by its path while the handle is still open. Other principals cannot open the copy for write or delete, so the launched bytes are the hashed bytes.
    5. The host never launches the original path. The private directory is deleted after the run, or on failure.
    Steps 3 and 4 rely on Windows sharing semantics. The POSIX equivalent is UNVERIFIED and is decided in the implementation issue (3.5).
- **Fail-closed conditions.** Each of the following is a classified setup failure, no guest code runs, and a warning is not sufficient:
  - the input is missing or unreadable (H2 or H3);
  - the carrier is missing or unreadable;
  - the carrier is invalid. This covers: JSON that does not parse; `schemaVersion` missing, not an integer, or not 1 (the reader accepts schema 1 only); unknown keys at any level, rejected as in 3.3; a required field missing (`binary.file` and `binary.sha256` for every package; `packaged.imageSidecar.file` and `packaged.imageSidecar.sha256` for HLE packages); a digest that is not 64 lowercase hexadecimal characters; a `packaged` block present in a firmware carrier, or absent from an HLE carrier; `binary.file` not equal to the artifact file name that `manifest.Artifact` resolves to; `packaged.imageSidecar.file` not equal to the image file name that `manifest.Image` resolves to; a carrier whose mode disagrees with the distribution mode;
  - a digest mismatch (H2 or H3).
  The distribution mode is decided by `FirmwareRuntimeLauncher.IsFirmwareDistribution` (presence of `firmware-boot.json`, `FirmwareDistribution.cs:599-600`). The host uses the same test at `Program.cs:235`. The carrier does not decide the mode. The host reads the carrier as a read-only input and never writes, regenerates, or edits it or any digest. A wrong digest is fixed on the writer side, by rebuilding or repackaging (W14 line 291).
- **Carrier: `runtime/build-manifest.json`, owned by Artifact Provenance (#46, W14).** For packaged builds, W14 places the manifest at `runtime/build-manifest.json`, beside the sidecar, and writes nothing at `<outputDir>/build-manifest.json` (W14 section 3.8, Location, line 283; the location rule is also at section 3.5, line 165). Firmware packages have the same carrier with `binary.sha256` and no `packaged` block (W14 line 285). Issue #46 owns the carrier's schema, contents, and writer. This design names the fields, the location, and the reader's rules. W14's codegen field `programImageInputSha256` (section 3.5, line 183) is the hash of the bytes passed to codegen, computed before the build. It is not the H3 digest, and this design does not use it. Whether `packaged.imageSidecar.sha256` equals the SHA-256 of the bytes that `PackageCommand.cs:231` writes is UNVERIFIED and is a check for #46.
- **Digests are outside the build key.** `binary.sha256` and `packaged.imageSidecar.sha256` are post-build outputs. They are not in `buildKey`, and they do not change Skip. Skip is disabled in v1 (W14 section 3.8, "Skip and build-key rule for the digests", line 299).
- **Why not `runtime-host.json`.** Putting the digests there would add a schema-2 field, which Q16 Option A rejects (section 4). Hence a separate carrier, owned by #46.
- **Packaging consequence (Q13).** `runtime/build-manifest.json` is a new packaged input that the ADR-027 Step C layout does not list. Adding it changes the packaged layout. That layout change belongs to the Q13 baseline decision, which the owner takes together with #46. The integrity requirement above does not depend on that decision and stands regardless.
- **What this check does not claim (tamper resistance).** This check detects a mismatch between the carrier and the actual bytes. It does not resist an actor that can edit both the carrier and the bytes. Against such an actor, no check in this design is sufficient. `runtime-host.json` has no digest in the carrier, so it is outside this check (Q16, Q17).
- **runtime-host.json integrity.** This design does not verify `runtime-host.json`. W14 section 3.8 (line 299) says its integrity is W12's concern. This design does not take that concern. The question is open as Q17 and must be settled with W14.
- **Current code, for context.** The host already verifies firmware inputs: H9 (disc) at `FirmwareDistribution.cs:672`, and H5 (AOT inputs) through `FirmwareAotBundle.Verify` at `:679` (comment at `:677-678`) (CONFIRMED). H1 is validated by schema and bounds only (`RuntimeHostManifest.cs:133-136`, `:173`, `:187`) (CONFIRMED). No SHA-256 check exists for H2 or H3 in `RuntimeHost/Program.cs`, `PackagedArtifactLiveRunner.cs` or `RuntimeHostManifest.cs`, so H15 has none either (OBSERVED, by search). Whether another code path checks them is UNVERIFIED.
- **Boundary rule.** Config here never sets, overrides, or substitutes an identity value. An identity mismatch is a classified run failure, not a config setting.

### 3.5 Implementation issue for the integrity check (proposed)

This section defines the work item. It is not an implementation.

- **Dependency on Issue A.** Issue A is the carrier writer, owned by #46 (W14). It is defined in W14 section 3.9 (HEAD `0dbad5f`). Issue B needs the carrier that Issue A writes. Until Issue A lands, acceptance criteria 1, 3, and 5 are tested with fixtures written by test code that follows the schema in 3.4. After Issue A lands, one test in each of those groups must read Issue A's real output.
- **Scope.** (1) The H2 and H3 load sites in `src/PSXRecomp.RuntimeHost/Program.cs` (H2 at `:229`, before the firmware branch at `:235`; H3 at `:244-248`). (2) `src/PSXRecomp.Infrastructure/Standalone/PackagedArtifactLiveRunner.cs:44-46`, the game window path (H15). (3) A new carrier reader: read-only, parses `runtime/build-manifest.json` under the rules in 3.4, and takes the distribution mode from `FirmwareRuntimeLauncher.IsFirmwareDistribution`. (4) The H2 private-copy procedure in 3.4, if Q18 confirms it. (5) The test seam for acceptance 9.
- **Assignee.** The #48 track (W12, this design). The owner's confirmation of this assignment is pending. This document does not treat it as final.
- **Acceptance criteria.** Each must be a test or a checked case:
  1. Match (fixture until Issue A lands): H2 and H3 match their carrier digests, and the run proceeds.
  2. Mismatch: a wrong H2 digest and a wrong H3 digest each fail closed.
  3. Missing (fixture until Issue A lands): a missing H2 file, a missing H3 file (HLE), and a missing carrier each fail closed.
  4. Unreadable: an input or the carrier that cannot be read fails closed.
  5. Carrier invalid (fixture until Issue A lands): each case listed in 3.4 fails closed, one test per case.
  6. Firmware: a firmware package whose H2 matches is accepted, and H3 is not checked for it.
  7. Game window: `PackagedArtifactLiveRunner` gives the same accept and refuse results as the host for cases 1 to 6.
  8. Read-only: the carrier and the inputs are opened read-only, and the test asserts that the run writes neither.
  9. Substitution (requires the seam below): a test replaces H3, and separately H2, after its hash is computed and before its use. The run is refused, or the bytes used are unchanged. For H3 the test asserts that the array that was hashed is the array from which `imageWords` is built. For H2 the test asserts that the launched path is the private copy from 3.4 and that replacing the original file does not change the launched bytes.
  10. Fail-closed order: no guest code runs before every required check has passed.
- **Seam for acceptance 9.** Criterion 9 cannot be observed from outside the host, so it needs an internal test seam, and the seam is a precondition of acceptance. The seam has three parts. (a) An observation point that reports the byte array passed to the SHA-256 computation for H3, and the byte array from which `imageWords` is built, so that the test can assert reference equality. (b) A hook that runs between the hash and the use, so that the test can replace the file at that point. (c) For H2, an observation point that reports the handle or copy that was hashed, and the path given to the process start. The seam is internal and test-only, and it must not change production behaviour.

## 4. Relation to the existing manifest schema

- Mapping from 3.2 `execution` to `RuntimeHostManifest` (schema 1, `RuntimeHostManifest.cs:40-54`, CONFIRMED):
  - `runTimeoutMs` -> `RunTimeoutMs`; `mixedFallback` -> `MixedFallback`; `fallbackSegmentBudget` -> `FallbackSegmentInstructionBudget`; `fallbackMaxTransitions` -> `FallbackMaxTransitions`; `guestFirmware` -> `GuestFirmware`.
  - `entryRoots` -> `BlockEntryPcs` (sorted and unique, CONFIRMED); the single `EntryPc` field carries the primary entry.
  - `segmentBudget`: **no field**. The manifest has no segment-budget field (CONFIRMED), and the packaged host hard-codes `segmentBudget: uint.MaxValue` at `RuntimeHost/Program.cs:307` (CONFIRMED).
- **Segment budget for packaged runs (not silently dropped).** `package` rejects `--segment-budget` (`Program.cs:57`, `allowSegmentBudget: false`), so `PackageCommand` uses `DefaultSegmentBudget` (`PackageCommand.cs:43`, `:75-76`). That value is passed at `PackageCommand.cs:152` (`uint.MaxValue` for `--gui`); the callee at that call was not read (UNVERIFIED). The `run` subcommand's `--segment-budget` (`Program.cs:162`, `allowSegmentBudget: true` at `:56`) does not reach packaged runs. The package-time chain to the artifact is verified: `PackageCommand.cs:76` (value), `:144-152` (argument to `RecompiledArtifactCodeGen.GenerateStandalone`, `uint.MaxValue` for `--gui`), `RecompiledArtifactCodeGen.cs:361` (passed as `budget`), `:422` (emitted as `embedded_budget`), `:1020` (dispatch budget) and `:1049` (`recompiler_dispatch(&state, budget)`). Only the host-transfer path, from the embedded budget to the packaged run in `RuntimeHost/Program.cs:307`, is UNVERIFIED. Consequence: a user-settable packaged segment budget has no schema-1 carrier. It is either a package-time build input only (no schema change; the host keeps reading nothing new), or a schema-2 field. Q15 asks which (section 10, item 2).
- The packaged distribution's input list is H1 to H15 (2.2). This design adds one packaged input, the integrity carrier `runtime/build-manifest.json` (3.4, Q13), and no config input, so `runtime-host.json` keeps schema 1. The distribution layout otherwise keeps its Step C layout (2.9). `runtime-host.json` applies to every package; `artifact-image.bin` applies to HLE packages only.
- Adding any new execution field to `runtime-host.json` (for example `segmentBudget`, Q15) changes the manifest schema, so `CurrentSchemaVersion` must advance (2) and the reader must accept 2 only with that version (CONFIRMED rule at `RuntimeHostManifest.cs:133`). That changes the packaged-artifact contract, so it is out of scope for this issue unless the issue owner accepts it (Q13). This document does not propose a schema 2.
- Host settings (3.2 `host`) do not enter `runtime-host.json` and are not written as a new packaged file. They are applied by the launcher.

## 5. Relation to project configuration, and privacy

### 5.1 Privacy of path-valued keys

Path values are not safe in a shareable file. A path such as `C:\Users\<name>\...` reveals a local account name and directory layout. A Project directory is likely to be copied, zipped, committed, or attached to a bug report, so path values must not sit there. This design therefore splits storage by content:

- **Project directory** (treated as shareable; an assumption, Q6): `project.json` (identity and format only, 2.5) and `runtime-config.json`, holding only non-path keys (`execution` and the non-path `host` keys) and per-title overlays (section 7) that contain no path keys.
- **Per-user store** (not shareable): a file in the user's own application data folder, keyed by `ProjectId`, holding path-valued keys only (`memoryCard.slot1Path`, `memoryCard.slot2Path`). Its exact location is UNVERIFIED: no existing per-user settings convention was confirmed in the repository (Q14).
- **CLI or environment only, never stored**: diagnostic output paths `--diagnostics`, `--loaded-code-diagnostics`, `--frame-out` (H7), and the environment variables that name output paths, `PSX_UNIT_TRACE_FILE` (H12), `PSX_GUARD_STATS_FILE` (H14), `PSXRECOMP_IPC_PROFILE` (H11) and `PSXRECOMP_TRANSITION_ATTRIBUTION` (H13). Each is named per invocation or per shell session and is never written to any file in this design.
- **Environment only**: the BIOS location (H6, `PSXRECOMP_BIOS_ROM`). It is never copied into any file.
- **Bindings check before placement.** `input.ports[].bindings` (section 3.2) may hold device identifiers that reveal host hardware or a machine. Its schema is deferred to #47, so the `input` section is not written to any file until the binding schema has been checked for host-specific data (device identifiers, paths, machine or user names).

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
2. Per-user store (path-valued keys only, 5.1). Host only, launcher-side.
3. Project `runtime-config.json`. Execution at packaging; host at launch.
4. Per-title overlay for the matching title identity (section 7). Execution at packaging; non-path host at launch.
5. Environment variables for diagnostic keys only. Launcher-side and generated-code-side. Never sets an execution setting. 9.2 covers `PSX_GUARD_STATS_ABORT_AFTER`, the only run-affecting key.
6. CLI options for one invocation. Execution at packaging (`package`) or at run (`run`); host for the diagnostic flags.

The BIOS location (H6) is an input path, not a layer. It is not overridden by any layer.

A value that a higher layer does not set is inherited, not reset. Each effective value must record its source in the output (the `--json` result or the packaged `README`), so a result can be traced to a layer.

### 6.3 Single defaults table

One module in `PSXRecomp.Core` holds every default in 2.7, with a name and a comment for each. Existing constants are re-pointed to it. Each value keeps its current number until the owner changes it; the duplicated and inconsistent values (1_000_000 versus 4096, and 100_000_000 for firmware) are listed as open question Q1, not silently unified.

## 7. Per-title overrides

- **Key.** A title is identified by `DiscIdentity` (serial, region, revision, `DiscIndex`; `DiscIdentity.cs:10-14`, CONFIRMED). The override key is the serial plus region plus revision. The disc index is part of the key for multi-disc titles, so each disc has its own overlay (PREDICTED; UNVERIFIED how the Project records disc order).
- **Location.** Per-title overlays are stored in the Project directory, under a sub-folder keyed by the title identity, not in the application-wide settings. The Project directory is treated as shareable (Q6), so overlays hold no path-valued keys (5.1).
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
- Debug and trace outputs are off unless named (2.6). IPC and transition attribution take a non-whitespace path (`IpcProfile.cs:48`, `TransitionAttributionRecorder.cs:35`). The guard-statistics parse rules are in 2.6 (CONFIRMED).
- No user path is embedded in a shareable file (5.1). BIOS data and digests are never written into configuration (`openbios-firmware-backend.md:504`, CONFIRMED; `FirmwareDistribution.cs:442`, CONFIRMED).
- Unknown keys fail closed (3.3).
- Invalid values fail with a classified error, not a silent fallback. Note that the trace variable `PSX_UNIT_TRACE_MAX_BYTES` currently disables tracing on an invalid value and lets the run proceed (2.6, CONFIRMED). That is a different policy from the CLI options, which reject. Q4 asks which policy is wanted.

### 9.2 Debug versus production

- **Profile.** The config has a `profile` field: `production` or `debug`. Debug allows the guard-statistics build (`PSXRECOMP_NATIVE_GUARD_STATS` at package time) and the `--report`, `--frame-evidence` and `--diagnostics` outputs. Production refuses the guard-statistics build flag.
- **The packaging-time rule.** Production refuses `PSXRECOMP_NATIVE_GUARD_STATS` and any other guard-statistics or trace build flag at packaging. Today the only such flag is `PSXRECOMP_NATIVE_GUARD_STATS` (`GuardStatsScope.Enabled`, `FirmwareDistribution.cs:405-406` (`Generate(` at 405, the `guardStats` argument at 406)). No packaging call requests trace codegen (2.6), so the trace reads are absent from every packaged artifact (CONFIRMED).
- **Consequence for `PSX_GUARD_STATS_ABORT_AFTER`.** The read is emitted only inside `EmitGuardStatsSupport`, and that is emitted only when `s_guardStats` is set (`RecompilerHostCodeGen.cs:284`, CONFIRMED). So the `getenv` at `:1021` is absent from every production build. Setting `PSX_GUARD_STATS_ABORT_AFTER` at packaging does not change the artifact, because the artifact contains no read to change. The rule is therefore stated on the build flag, not on the environment variable.
- **Run-time host-side keys.** `PSXRECOMP_IPC_PROFILE`, `PSXRECOMP_TRANSITION_ATTRIBUTION` and the host-side `PSX_UNIT_TRACE_FILE` read are in shared host code. Packaging cannot refuse them (2.6). The design forbids launchers from setting them and forbids them as execution settings. Nothing else in this design mitigates them, and that is a residual risk.
- **Debug environment variables and execution settings.** The general rule is that environment variables cannot change execution settings (6.2 layer 5). In a production build the only run-affecting read is absent (see above). In a debug build the only run-affecting read is `PSX_GUARD_STATS_ABORT_AFTER`, which is present only in a guard build, and 2.6 is the complete list.

## 10. Recommendation

1. Adopt the two-tier model (3.1): frozen execution settings in the packaged manifest, and launch-time host settings applied by the launcher. The packaged host reads one new input for integrity, the carrier `runtime/build-manifest.json` (3.4). It is a new packaged input decided under Q13, and it reads no new configuration input. The ADR-027 Step C layout (2.9) is unchanged. Its two program binaries, `<game>[.exe]` and `runtime/PSXRecomp.RuntimeHost[.exe]`, stay in the baseline and outside the config contract per Q16 (Option A), with their identity in #46. The packaged host checks H2 against `binary.sha256` and H3 against `packaged.imageSidecar.sha256` in `runtime/build-manifest.json`, failing closed, and the game window (H15) does the same (section 3.4); the firmware and environment inputs H4 to H14 are outside that baseline (2.9, Q13).
2. Keep `runtime-host.json` schema 1 as the execution carrier. Do not add fields in this issue. Map the existing CLI options to it (2.1, 2.5). `segmentBudget` has no schema-1 field; packaged runs keep today's behaviour (section 4, Q15), and it is not silently dropped.
3. Add `runtime-config.json` (Project directory, non-path keys only) and a per-user store (path-valued keys only), both behind an `IRuntimeConfigStore` port in Core with the implementation in Infrastructure (5.1, 6.1).
4. Create a single defaults table and re-point the constants (6.3). Resolve the inconsistent segment-budget defaults first (Q1), because the table cannot be written without it.
5. Implement per-title overlays only after the title-identity key is confirmed (Q2), and only with non-path keys (section 7).
6. Keep audio, VSync, and graphics backend as reserved names in the design until their host adapters exist (2.8, 3.2). Do not invent keys for them.
7. Split debug and production profiles (9.2). Keep debug keys out of execution settings, and refuse the guard build flag in production, which removes the only run-affecting read (9.2).

Suggested order of follow-up work (for the owner to schedule; not started here): defaults table, then runtime-config version 1 with only the existing CLI keys, then per-title overlay, then host keys one area at a time with #40 and #47.

## 11. Open questions

- **Q1. Which segment-budget default is correct?** The CLI run uses `1_000_000`, the Studio real-title path uses `4096`, and firmware fallback uses `100_000_000` (2.7). Should they be unified, or does each path keep its own default by design? Owner decision needed. (OBSERVED inconsistency.)
- **Q2. What is the title identity key?** Is it serial + region + revision + disc index (`DiscIdentity`, 7), and how does the Project record disc order for multi-disc titles? (UNVERIFIED.)
- **Q3. Does a host VSync or frame-pacing choice affect guest-visible timing?** The guest VBlank model is in `DeviceScheduler` and `BiosTimerVblankIrqHandler`, and the OpenBIOS `waitVSync` field flips at VBlank (`DeviceSchedulerTests.cs:274`, 2.8). If that model is the only timing source, VSync is host-only. Whether a host presentation choice ever feeds back into the guest is UNVERIFIED and must be checked before the key is placed (section 3.2).
- **Q4. Invalid-value policy.** Should an invalid environment variable disable a feature (current trace behaviour, 2.6) or fail the run (CLI behaviour, 2.1)? A single policy is needed.
- **Q5. Where does the profile (production or debug) live?** In `runtime-host.json` (needs schema 2, section 4) or only in the packaging record? Owner decision.
- **Q6. Is the Project directory a shareable unit?** Section 5.1 treats it as shareable as an assumption, and keeps path keys out of it. If the owner decides it is not, that changes only where non-path keys live, not the rule that paths never enter it. Confirm the intended sharing model.
- **Q7. Should the Studio write `runtime-config.json`, or only read it?** A settings UI is needed for #40 and does not exist today (2.3). Owner decision on who owns the write path, and whether the Studio also writes the per-user store.
- **Q8. Memory card: which layer chooses the card file?** `MemoryCardSlotConfiguration` exists in Core (2.8), but no CLI or GUI setter was found, and the packaged host has no card input (H1 to H15). Is the intended entry point the per-user store with a launcher, or a package-time field? (UNVERIFIED call sites outside Infrastructure.)
- **Q9. Controller mapping owner.** Does #47 own the binding file format, with #48 only referencing it, as section 3.2 assumes? (UNVERIFIED with #47's scope.)
- **Q10. Are config files in scope at all?** `headless-cli.md:405-408` defers config files to Issue #15 (2.9). This design assumes #48 may introduce a runtime config file. Confirm that this does not collide with #15.
- **Q11. Does the issue's completion criterion "Debug/production configuration separated" require a runtime switch, or only a documented profile?** Section 9 assumes a profile field. (UNVERIFIED with issue owner.)
- **Q12. Issue comments and timeline.** The issue reports zero comments at read time (CONFIRMED), but the timeline was not read (`gh` not authenticated; UNVERIFIED). Any decision recorded there would change this design.
- **Q13. ADR-027 Step C layout.** The layout (2.9) lists the game executable and the host binary, `runtime-host.json` (every package) and `artifact-image.bin` (HLE only). Shipped firmware packages add H4, H5, H9, H10 and `bios-content-diagnostic.txt`, which the ADR never mentions (CONFIRMED by search). Should the ADR be amended to record them? Owner decision. Any host setting that reaches a packaged run is also a baseline change. The carrier `runtime/build-manifest.json` (3.4) is also a new packaged input that the layout does not list.
- **Q14. Per-user store location and convention.** Which per-user application data location should hold path-valued keys, and does the repository already have a per-user settings convention? None was confirmed (5.1). UNVERIFIED.
- **Q15. Segment budget for packaged runs.** `segmentBudget` has no field in `runtime-host.json` schema 1, `package` rejects `--segment-budget`, and the packaged host hard-codes `uint.MaxValue` (4). Should a packaged segment budget be a user setting? If yes, is it a package-time build input only (no schema change) or a schema-2 manifest field, and is it recorded in the packaging log? Owner decision. The package-time chain to the artifact is verified (section 4); only the host-transfer path to the packaged run is UNVERIFIED.
- **Q16. Decided: Option A.** Owner ruling, recorded 2026-10-29 (binding). Are `<game>[.exe]` and `runtime/PSXRecomp.RuntimeHost[.exe]` inside the runtime configuration contract? Ruling: no. Both binaries are outside the Runtime Configuration contract. Their identity is managed in Artifact Provenance (issue #46, W14; design `origin/batch2/w14-artifact-provenance-design:docs/design/issue-46-artifact-provenance.md`). Option B (pinning both by SHA-256 in `runtime-host.json`, a schema-2 change) is not taken. This ruling is not permission to skip runtime integrity checks: the host still verifies the input identity it reads, and a mismatch fails closed (section 3.4).
- **Q17. Integrity of `runtime-host.json`.** The carrier digests do not cover it (Q16; section 3.4). W14 section 3.8 (line 299) says its integrity is W12's concern, while this design does not cover it. Owner decision: (a) this design declares `runtime-host.json` outside the check, and W14 line 299 is corrected to say so; or (b) a check is added later and assigned to #46. Recommended: (a) now, with the W14 correction made by #46. Either way, the two documents must say the same thing.
- **Q18. H2 launch method (owner decision).** Section 3.4 fixes the method: a private copy, read-only with a `FileShare.Read` handle held from the hash to process creation, launched by path, with the original path never launched. Rejected: (a) a handle on the original file, because process creation takes a path; (c) launching the original after hashing, because the file can be replaced between the hash and the launch. The owner confirms the method, including the directory access rules and the sharing rule. W14 open question 17 (its line 509 at `0dbad5f`) asks the same question from the writer side. Both documents must cite the same decision.

## 12. Verification status of this document

- Source reads and git log were used for every CONFIRMED item. Line numbers are as of `2e3ecad`.
- The grep count in 2.6 uses the pathspec stated there. Counts that include test sources were reviewer-reported, not re-run here.
- No build, test, or run was performed. Any claim about runtime behaviour is UNVERIFIED or PREDICTED.
- No file other than this one was changed.
