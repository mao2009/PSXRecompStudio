# Issue #46: Artifact identity, build provenance, and the management layer

Tracking: [#46](https://github.com/mao2009/PSXRecompStudio/issues/46). Related: #456 (first runnable artifact, complete through #458-#461), #15 (CLI build/run), #42 (Project / Workspace), #43 (incremental analysis and cache), #40 (Studio GUI), #48 (runtime configuration, W12).

Status: **design only**. No code, test, or shared document is changed by this document. Baseline: `origin/main` at `2e3ecad`.

## 0. Evidence labels and method

- **CONFIRMED**: read in the source at the cited `file:line` in this checkout.
- **OBSERVED**: stated in a repository doc, test name, or CI file that I read. I did not execute the test or the workflow.
- **PREDICTED**: a behavior I expect from the code. I did not run it.
- **UNVERIFIED**: not checked. Each one is listed as an open question or as an experiment that must run before a claim is made.

I did not build the project, run `gcc`, or run any test while writing this document. Every behavior claim about the toolchain or the binary is therefore PREDICTED or UNVERIFIED. The source claims are CONFIRMED by reading only.

No BIOS, ROM, disc, or other copyrighted content was read or is recorded here. Paths below are repository paths and the paths of the code that handles those inputs.

Layer rules this design follows: ADR-017 points 1-3 (`docs/adr/017-managed-host-io-port-adapter-boundary.md:29-31`), the managed host I/O rules in `docs/architecture-matrix.md` (rule 3 at `:66`, Forbidden API Matrix at `:92`), and the machine-enforced entries in `src/architecture.contract.json` (`forbiddenDependencies` at `:9`, `forbiddenApis` at `:18`). Where the matrix and the contract differ, the contract governs.

**Decision for this round (binding).** In v1, automated Build Skip is disabled. Every build runs. The design cannot prove that an untracked input (the C runtime, import libraries, headers, specs, driver configuration, or the compiler binary itself) is unchanged, so no Skip path is safe. A future Skip needs a separate design with a fingerprint of every file the driver resolves. This document covers identity, the manifest, and verification. It does not cover Skip.

## 1. Where Issue #46 stands

The issue body marks the first runnable-artifact slice as complete (#458 adapter, #459/#460 CLI and run surface, #461 E2E). Its open items are: artifact identity, build manifest, input/toolchain/output association, build history, clean/rebuild/incremental policy, a shared CLI/GUI workflow, and reproducibility requirements. This document covers the first three, the scope of the reproducibility requirements, and the shape of the shared workflow. History is only sketched, because it depends on #42. Incremental policy is out of scope for v1, because Skip is disabled.

## 2. Current artifact generation path

The path has five stages. Each stage lists the code that does it, and the code is shared by several callers.

### 2.1 Stages and call sites

| Stage | Where | Notes |
|---|---|---|
| Input load | `src/PSXRecomp.Cli/RecompileCommand.cs:39` `CliInput.Load(...)` | `recompile` does not compute an input hash. `run` does, through `CliInput.LoadWithIdentity` (`src/PSXRecomp.Cli/CliInput.cs:56`, used at `src/PSXRecomp.Cli/RunCommand.cs:52-58`). CONFIRMED. |
| Lowering | `src/PSXRecomp.Cli/RecompileCommand.cs:40` `CliInput.Lower(...)` | Produces the IR program. |
| Host dispatch codegen (Recompiler, Core) | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:70` (program + loaded code), `:85-88` (overload with `traceUnits`, `guardStats`, `allowEmptyProgram`) | Called from `src/PSXRecomp.Cli/RecompileCommand.cs:42` with the defaults, and from the runtime engine with `traceUnits` and `guardStats` set (see 2.4). CONFIRMED. |
| Artifact driver codegen (Generated source, Core) | `src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:249` `Generate(dispatch)` (standard), `:305` `GenerateStandalone(...)` (standalone, embeds initial state and program image) | `src/PSXRecomp.Cli/RecompileCommand.cs:86` and `:101` select between them. CONFIRMED. |
| Build (Infrastructure adapter) | `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:50` `Build` | Writes `<BinaryName>.c` (`:55-59`), compiles (`:74-91`), checks the object (`:93-99`), links (`:103-109`), optionally removes the object for firmware (`:111-136`), resolves `.exe` (`:138-147`). CONFIRMED. |
| Build contract (Core port) | `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:48` (request), `:63` (result), `:86` (`IGeneratedHostBuildService`) | The port has no version, toolchain, or provenance fields. CONFIRMED. |
| Native artifact | `<outputDir>/<BinaryName>` (or `.exe`) plus `<outputDir>/<BinaryName>.c` | `.o` is kept for game builds (`:76`, `:111-115`) and removed for firmware. `.c` is never removed by the adapter. CONFIRMED. |

Callers of the build port, all through `IGeneratedHostBuildService` or a direct `GeneratedHostBuildService`:

- `src/PSXRecomp.Cli/RecompileCommand.cs:115-116` (name `ArtifactBinaryName`, or the derived standalone name).
- `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:563` (name `"recompiled-artifact"`, in the constructor path: the constructor is at `:441` and its parameter list starts at `:442`), and `:1281` (in `BuildEmbeddedArtifact`, starting at `:1235`). CONFIRMED.
- `src/PSXRecomp.Cli/PackageCommand.cs:170-178` (adds `-mwindows -static-libgcc` for `--gui`). CONFIRMED.
- `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:426-427` (staged build with `FirmwareDistribution: true`). CONFIRMED.
- `src/PSXRecomp.Infrastructure/Standalone/LiveGameRunner.cs:77` and `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:109` (default `new GeneratedHostBuildService()`). CONFIRMED.

### 2.2 Fixed names and overwrite

- The engine writes to a caller-supplied output directory with a fixed name, `recompiled-artifact` (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:563`, `:1281`). A second build into the same directory overwrites `.c` and the binary. `File.WriteAllText` at `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:59` has no existence check. CONFIRMED by reading. The overwrite behavior is PREDICTED from that.
- The engine also writes `artifact-input.txt` and `artifact-image.bin` into the same directory (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:511-512`). CONFIRMED.
- Firmware distribution avoids this with a staging directory named `.<artifact>.staging-<guid>` (`src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:510`), then a promote (move into place). CONFIRMED.

### 2.3 Runtime inputs are external

`src/PSXRecomp.Cli/RecompileCommand.cs:99` says: "Standard artifact (requires input files at runtime)". The standard artifact reads its input at run time and is not self-contained. The standalone artifact (`GenerateStandalone`, `src/PSXRecomp.Cli/RecompileCommand.cs:86-95`) embeds the initial state and the program image in its C source. CONFIRMED: the source emits `static const uint8_t embedded_image[] = {` (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:457`).

This means the output directory already holds game-derived code and data before this design. The standalone `.c` contains the image, and the engine path writes it again as `artifact-image.bin` (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:512`). This design does not change or add to that. Section 3.5 records only hashes of these inputs.

The external BIOS path is a separate runtime input. It is read from the environment variable named at `src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:629` (`ExternalBiosEnvironmentVariable`). CONFIRMED. The standalone source records `embedded_bios_external` (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:456`). CONFIRMED.

### 2.4 Options that change the generated source

`RecompilerHostCodeGen.Generate` takes options that change the output text:

- `traceUnits` (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:85`). The engine sets it from `UnitTraceScope.CurrentPath is not null` (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:551`, `:1253`). CONFIRMED.
- `guardStats` (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:85`, `:284` emits guard support when it is true). The engine sets it from `GuardStats`. `src/PSXRecomp.Infrastructure/GuardStatsScope.cs:34` turns it on from the environment variable `PSXRECOMP_NATIVE_GUARD_STATS`. `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:404` says the same at package time. CONFIRMED.
- `allowEmptyProgram` (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:85`, used by `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:405-406`). CONFIRMED.
- `hostBridge`, `directLaunchHostRelativePath`, `externalBios`, `windowedLaunch` on `GenerateStandalone` (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:305-318`). CONFIRMED.

The consequence is that the same input can produce different C source depending on which entry point built it. `recompile` uses the defaults (`src/PSXRecomp.Cli/RecompileCommand.cs:42`), while `run` and `package` may set `traceUnits` or `guardStats`. PREDICTED from the code paths. This is the largest gap in the current design. Section 3.2 keys these options.

`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:57` declares `s_guardStats` as `[ThreadStatic]`, and `:88` sets it on each call. Concurrent calls on different threads therefore do not share it. CONFIRMED.

### 2.5 Runtime host provisioning

The runtime host (`runtime/`) is not produced by the codegen. `src/PSXRecomp.Infrastructure/RuntimeHostBuildService.cs` runs `dotnet restore` and `dotnet publish -c Release` (`:146-171`, `:201`). CONFIRMED. Its output is not part of any identity today. See Section 3.3 (runtime host row).

### 2.6 Existing identity and manifest pieces

| Piece | Where | What it covers |
|---|---|---|
| `RuntimeHostManifest` | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:40-56`, schema `:57` (`CurrentSchemaVersion = 1`) | Run contract for the packaged host: entry PC, image, block PCs, budget. No input hash, no toolchain, no product version. CONFIRMED. Written by `src/PSXRecomp.Cli/PackageCommand.cs:234-264`. |
| `FirmwareBootManifest` | `src/PSXRecomp.Infrastructure/Standalone/FirmwareBootManifest.cs:21`, `:35`, `:141` | Already records `DiscSha256` and per-input `Sha256`, verified before every run. CONFIRMED. This is the closest existing precedent. |
| `LoadImageManifest` | `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:27` (`SourceSha256`), `:356` (computed) | SHA-256 of each EXE-based loaded image. CONFIRMED. |
| `ProjectManifestDocument.InputSha256` | `src/PSXRecomp.Core/Project/ProjectManifestDocument.cs:29` | Project-level input hash. `docs/runtime/loaded-code-identity.md` (row 1 of its table) says no production consumer exists outside `Project/`. OBSERVED from that doc. |
| `RunCommand` diagnostic bundle | `src/PSXRecomp.Cli/RunCommand.cs:52-58` (input hash), `:191` (`artifactIdentity: sha256:<binary>`), `:206-209` (`ComputeSha256`), `:212-215` (`GetProductRevision` from `AssemblyInformationalVersionAttribute`) | The only artifact identity that exists today, and it is computed after a run for diagnostics, not at build time. CONFIRMED. |
| `ExecutionEnvironmentCollector.Capture` | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionEnvironmentCollector.cs:28` | `compilerVersion: null`, always. CONFIRMED. |
| `DoctorCommand` compiler probe | `src/PSXRecomp.Cli/DoctorCommand.cs:107-108` | Runs `<compiler> --version` and keeps only success or failure. The version text is discarded. CONFIRMED. |

Note: the name `Manifest` is already used for three different things (`RuntimeHostManifest`, `LoadImageManifest`, `FirmwareBootManifest`), plus `ProjectManifestDocument`. A new build manifest needs a distinct name. This document uses `BuildManifest`.

### 2.7 Version and toolchain facts

- The compiler is `gcc` by default (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:24`), overridable per request (`src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:52`, `CompilerExecutable`). Flags are fixed: `-std=c11 -O0 -Wall -Wextra` (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:25`), plus `ExtraCompilerArguments`. CONFIRMED.
- The link step passes the object, `-s` for firmware only (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:102-103`, `:209-211`), extra linker arguments, and `-o`. CONFIRMED.
- The root `Directory.Build.props` contains no `<Version>`, `<VersionPrefix>`, or `<SourceRevisionId>` setting. CONFIRMED by reading the file. So the product version comes from the SDK default for `InformationalVersion`, which is PREDICTED to include a git revision only when the SDK sets `SourceRevisionId`. UNVERIFIED.
- CI runs on `ubuntu-latest` and `ubuntu-24.04` (`.github/workflows/ci.yml`: `:16`, `:70`, `:82`, `:128`, `:198`, `:226`). The workflow installs `ninja-build` (`:94`, `:211`). No step installs or pins `gcc`. OBSERVED from the workflow text. The gcc version that CI uses is therefore whatever the runner image provides. UNVERIFIED.
- No generator or codegen format version constant was found in `RecompilerHostCodeGen`. The only version constant in the artifact driver is `ProtocolFallbackVersion = 1` (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:222`), which covers the fallback protocol, not the source generator. CONFIRMED by grep. Search for an analyzer or decoder version constant was not done. UNVERIFIED.

## 3. Artifact identity and manifest

**Scope (owner ruling, Q16 Option A).** This section defines the identity of the game binary and the host binary, and of the generated source they come from. These binaries are outside Runtime Configuration (issue #48, W12). W12 adopts Q16 Option A, so runtime configuration does not own their identity, and this design does. Runtime configuration (for example input paths, the BIOS location, or runtime flags) is not part of this key. The only runtime input recorded here is a name, listed under `runtimeInputs` in 3.5, and that name is recorded only as a dependency.

### 3.1 Identity classes

Keep four identities separate. Collapsing them is what caused the problems in #734, which this design follows (`docs/runtime/loaded-code-identity.md`).

1. **Input identity.** SHA-256 of the input bytes that the codegen consumed: the EXE file for PS-X EXE input (`src/PSXRecomp.Cli/CliInput.cs:79`), or the CHD stream for CHD input (`src/PSXRecomp.Cli/CliInput.cs:100`). It identifies provenance. It is not a runtime proof. Source: `CliInput.LoadWithIdentity`.
2. **Build key.** A hash of everything that decides the generated source and the build recipe, computed before building. Its inputs are listed in 3.2. The key records what was built. It is not a skip key, because Skip is disabled in v1.
3. **Source identity.** SHA-256 of the generated `.c` bytes. Computed after codegen. It is recorded and verified, and it identifies what was generated.
4. **Binary identity.** SHA-256 of the native binary. Computed after the build. It is the artifact identity that `src/PSXRecomp.Cli/RunCommand.cs:191` already reports, but only after a run.

Binary identity is not guaranteed to be stable across builds (Section 4.3). The design records and verifies the source and binary hashes. It makes no stability promise for either.

### 3.2 Build key inputs

The build key hashes this set. Each item is a field in the manifest, so a mismatch is explainable:

- Input identity (item 1 of 3.1). The input SHA-256 is computed in the CLI by `CliInput.LoadWithIdentity` (`src/PSXRecomp.Cli/CliInput.cs:56`). `recompile` does not call it today (`src/PSXRecomp.Cli/RecompileCommand.cs:39` calls `Load`), so the CLI must switch to it. The value reaches the workflow as `ArtifactBuildRequest.InputSha256` (Section 7.2). For a standard artifact, the runtime-input names are in 3.5 (`runtimeInputs`).
- Input kind: `exe` or `chd`, chosen by file extension (`src/PSXRecomp.Cli/CliInput.cs:44-46`). It is keyed, because it selects the loading path that produces the IR.
- Requested binary name (`BinaryName`). In standalone mode it is derived from the input file name (`src/PSXRecomp.Cli/RecompileCommand.cs:63`, `ResolveStandaloneArtifactName` at `:213`). It is a bare file name, not a path. Two inputs with identical bytes and different names therefore have different keys, and their manifests differ in `source.file`, `binary.file`, and `files`.
- Entry roots: `arguments.EntryRoots` (`src/PSXRecomp.Cli/RecompileCommand.cs:40`), passed into `CliInput.Lower` (`src/PSXRecomp.Cli/CliInput.cs:130-136`). They change the lowered program and therefore the generated source. The key holds them as a list of decimal values, in order. An empty list means no explicit roots.
- Segment budget (`src/PSXRecomp.Cli/RecompileCommand.cs:38`), keyed as `Codegen.Budget`.
- SHA-256 of the generated `Source` text (`source.sha256`). The CLI runs codegen before the workflow is called (`src/PSXRecomp.Cli/RecompileCommand.cs:42`, `:86-101`), and the workflow holds the result. This hash covers every input that changes `Source` that is not listed above, including the loaded-code table.
- Extra compiler or linker arguments. They are recorded in the manifest as given (`extraArguments`), and their presence is part of the record. They are not rejected unless they contain a path separator or drive qualifier (3.6).
- Codegen mode and standalone options, as the `codegen` fields listed in 3.5. Mode is standard or standalone (`src/PSXRecomp.Cli/RecompileCommand.cs:56`).
- Codegen options: `traceUnits`, `guardStats`, `allowEmptyProgram` (2.4). These must be part of the key. Today they are not recorded anywhere.
- Loaded-code table (`src/PSXRecomp.Core/Recompiler/LoadedCodeTable.cs`) identity, from `LoadImageManifest`. Its source SHA-256 is already computed (`src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:356`). Loaded-code names are not keyed separately. The CLI `recompile` path passes no loaded-code table: `src/PSXRecomp.Cli/RecompileCommand.cs:42` calls the single-argument `Generate`. On the engine path, the table is covered by `source.sha256`. Its descriptors go into the manifest as an explanation only.
- Generator version (3.3).
- Build recipe, path-free (3.6): the fixed compiler and linker tokens, the caller's extra arguments, `FirmwareDistribution`, and the `--gui` linker arguments. No absolute path and no staging GUID enters the key.
- Toolchain identity. Every component below is a version line or a triple, never an executable path. The workflow resolves the driver once, as `compiler = request.CompilerExecutable ?? GccGeneratedHostRecipe.DefaultCompiler` (`"gcc"`; the adapter default is `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:24`, the override field `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:52`). It passes `compiler` to `IToolchainProbe.Probe` and sets `CompilerExecutable: compiler` on the build request. Both receive the identical string.
  - Driver version: the first line of `<compiler> --version`.
  - Target triple: from `<compiler> -dumpmachine`.
  - Linker version: the link step runs the same driver (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:105`), which invokes `ld`. The probe runs `<compiler> -print-prog-name=ld` to locate the linker, then keys the first line of that linker's `--version`. The linker path is not keyed. If the driver prints a bare `ld` with no directory, the probe runs `ld --version` through `PATH`, which is the same resolution the build uses. If that run fails, `Available` is false (3.7 and 7.2).
  - C library (accepted limit). The C runtime and import libraries are not probed or keyed. A change is not detected. Because the build always runs, the next build uses the changed library, but its manifest does not record the change. Trigger for operator review: any update of the C runtime or import-library package, including a runtime-only update that leaves the driver version line unchanged. Section 3.7 gives the full rule.
  - Driver configuration (accepted limit). A driver configuration file (`<driver>.cfg`, UNVERIFIED for this toolchain) is not keyed. The same rule applies: the build runs, the manifest does not record the change. Trigger for operator review: any edit to a driver configuration file. A `@file` response file is recorded because it changes the argument list, and its presence is in the manifest (Section 6).
  - Environment and working directory. Environment names are reported in the local history (3.7). `PATH` and the current working directory are not keyed (3.7).

### 3.3 Tool versions

| Version | Source today | Proposal |
|---|---|---|
| psxrecomp product | `AssemblyInformationalVersionAttribute` (`src/PSXRecomp.Cli/RunCommand.cs:212-215`) | Record it in the manifest. Do not use it in the build key, because every commit would then change every key. Use the generator version instead. |
| Codegen format | None (3.2) | Add a `RecompilerGeneratorVersion` constant, bumped by hand when the emitted C changes meaning. Precedent: `ProtocolFallbackVersion` (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:222`). |
| Analyzer / decoder | Not located. UNVERIFIED. | Find it, or record that it does not exist. |
| Runtime host | `SchemaVersion = 1` (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:57`) | Record the publish configuration and RID, and the hash of the published host binary. |
| .NET SDK | Not recorded | Record `dotnet --version` at provision time. UNVERIFIED that this is cheap. |

### 3.4 Toolchain and build provenance

Record, per build:

- Compiler: the first line of `<compiler> --version`. This needs a required change to the existing adapter. `ToolchainResult` (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:178`) has only `Outcome`, `ExitCode`, and `Stderr`. `RunToolchain` reads stdout into `stdoutTask` (`:271`) and discards it (`:281`, `_ = ReadStreamWithinCleanupBudget(stdoutTask, ...)`). The change is a `Stdout` field on `ToolchainResult`, filled on success. The port `IGeneratedHostBuildService` does not change. CONFIRMED that the field and discard exist. The change itself is not implemented by this design.
- Target triple, from `<compiler> -dumpmachine`, which is a second probe invocation. PREDICTED to work with gcc and MinGW; UNVERIFIED.
- Ownership: the probe runs a process, so it belongs to Infrastructure (Section 7.2, `IToolchainProbe`). The existing `DoctorCommand` probe (`src/PSXRecomp.Cli/DoctorCommand.cs:107-108`) calls `GeneratedHostBuildService.RunToolchain` directly. After the change it should call `IToolchainProbe` as well, so both paths share one implementation. Until then it is unchanged.
- Compiler and linker arguments actually passed (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:77-85`, `:103`).
- Step results: outcome and exit code per step (`ToolchainResult`, `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:178`). Durations are not recorded in the deterministic part.
- Compiler executable path. This is a local path. Per `docs/development/artifact-policy.md:72-73`, deterministic artifacts must not carry local paths, so the path goes in the local-only history record (Section 5), not in the manifest.

### 3.5 Manifest contents and location

Proposed file: `build-manifest.json`, schema version 1. It has one location per build. A non-packaged build writes `<outputDir>/build-manifest.json`. A packaged build, HLE or firmware, writes `runtime/build-manifest.json` and nothing at `<outputDir>` (3.8). It is written once per build, by one writer (the caller at the composition root, through `IBuildArtifactStore.WriteText`, Section 7.2). It is written only after the artifact files are promoted (moved into place, using the staging pattern in `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:510`), and for an HLE package only after the image sidecar exists. A firmware package has no sidecar, so its carrier is written after its own build step (3.8). The workflow never writes it.

Deterministic fields (no timestamps, no local paths):

```text
schemaVersion, buildKey, binaryName,   // binaryName: the requested bare name, also a key input (3.2)
entryRoots: [ uint ],                  // decimal, in order; keyed (3.2)
extraArguments: { compiler: bool, linker: bool, values: [ string ] }   // as given; values are path-free (3.6)
input:      { kind, sha256 }                    // kind: "exe" | "chd"; hash only
codegen:    {
  mode,                         // "standard" | "standalone"
  traceUnits, guardStats, allowEmptyProgram,
  hostBridge, externalBios,
  directLaunchHostRelativePath, // string or null
  windowedLaunch,               // { hostBinaryName, argumentRelativePath, waitLimitMs } or null
  budget, initialPc, initialHi, initialLo, imageLoadAddress,
  initialGprSha256,             // hash over the GPR words
  initialMemorySha256,          // hash over the (address, value) pairs
  programImageInputSha256,      // hash over the program image bytes passed to codegen (key input, pre-build; NOT the H3 digest, see 3.8)
  generatorVersion
}
loadedCode: [ { name, kind, loadAddress, wordCount, sourceSha256 } ]   // explanation only, not keyed (3.2)
source:     { file, sha256, sizeBytes }     // file: "<name>.c", a bare file name
binary:     { file, sha256, sizeBytes }     // file: "<name>" or "<name>.exe", as produced
files:      [ "<name>.c", "<name>[.exe]", "<name>.o" (game builds only) ]   // bare names, what promote moves
toolchain:  { compilerVersionLine, linkerVersionLine, targetTriple, recipeTokens }   // path-free, see 3.6
packaged:   { imageSidecar: { file, sha256, sizeBytes } }   // HLE packages only; absent for firmware packages; H3 digest, read by the packaged host; NOT in buildKey; see 3.8. H2 is binary.sha256 above.
runtimeInputs: [ { name, kind, requiredBy, sha256? } ]   // names and hashes only, never the BIOS or ROM content
productRevision: "<InformationalVersion>"       // informational, not part of buildKey
```

The `initialGpr`, `initialMemory`, and program-image values are hashed and never listed in the manifest. The manifest records their hashes only, the same way the project manifest already stores the input SHA-256 (`src/PSXRecomp.Core/Project/ProjectManifestDocument.cs:29`). The C source and `artifact-image.bin` still contain the game-derived data they always did (Section 2.3).

**runtimeInputs.** For a standard artifact, the artifact reads its input at run time (`src/PSXRecomp.Cli/RecompileCommand.cs:99`). Its single entry is `{ name: "input", kind: "exe" | "chd", requiredBy: "standard run", sha256: <input.sha256> }`. It carries a name, never a path. Its hash is already in the key. For a standalone artifact the list is empty, except for an external BIOS. That adds `{ name: "PSXRECOMP_BIOS_ROM", kind: "environment", requiredBy: "externalBios" }`, which records the variable name only, never its value or the file content (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:629`).

Non-deterministic or local data goes into a separate local record, not into the manifest: timestamps, compiler path, machine name, environment variable names, and the run history. Section 5.

The manifest is a sidecar. It is not embedded in the C source. Embedding would change the source bytes and invalidate the existing determinism tests (`src/PSXRecomp.Tests/Recompiler/HostCodeGenTests.cs:576`, `src/PSXRecomp.Tests/Recompiler/RecompiledArtifactCodeGenTests.cs:144`, `:223`, `:252`, OBSERVED test names). It would also make the source identity depend on the manifest that describes it.

`RuntimeHostManifest` is not changed. It is the run contract for the packaged host, and it should stay one.

### 3.6 Path-free recipe

The build key and the manifest must not depend on where the build runs. Today the compiler and linker argv carry output-dependent values, all built from `OutputDirectory`: `sourcePath` (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:55`, passed at `:83`), `objectPath` (`:76`, passed at `:85`), and `binaryPath` (`:101`, passed as `-o` at `:219-220`). The workflow stages into `.<artifact>.staging-<guid>` (`src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:510`), so those values also carry a random GUID on every build. The raw argv is therefore run-dependent, and the design does not record it.

The recorded `recipeTokens` are these fixed tokens, in order:

- `DefaultCompilerArgTokens`: `-std=c11`, `-O0`, `-Wall`, `-Wextra`. Today this is a private array in the adapter (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:25`), so the Domain workflow cannot read it. See "Recipe ownership" below.
- The caller's `ExtraCompilerArguments`, as given.
- `-c`, then the placeholder `<source>`, then `-o`, then the placeholder `<object>` (`:82-85`).
- For the link step: `<object>`, then `-s` when `FirmwareDistribution` is set (`:102-103`), then the caller's `ExtraLinkerArguments`, then `-o`, then the placeholder `<binary>` (`:219-220`).

The placeholders `<source>`, `<object>`, and `<binary>` replace the three output-dependent values. Real paths go only into the local history record (Section 5).

**Caller-supplied extra arguments: rule.** The workflow rejects, before any build step, an extra compiler or linker argument that contains `/`, `\`, or `:` anywhere in the token. This covers rooted paths, drive-qualified paths, and forms such as `-I/does not exist/include`. `Path.IsPathRooted` is not enough, because it returns false for `-I/...`, and the adapter test at `src/PSXRecomp.Tests/Infrastructure/GeneratedHostBuildServiceTests.cs:213` uses exactly that form. A rejected request returns an outcome `ArgumentRejected` with code `PATH_ARGUMENT_NOT_ALLOWED`, makes no port call, and creates no staging area. The rule sits in the workflow. The adapter keeps accepting such tokens, so its existing test stays valid. The rule also rejects a few non-path tokens that contain `:`, such as `-Wl,--subsystem,windows:5.0` and `-DURL=http://...`. No production caller passes them, so that cost is accepted.

Tokens without a separator, such as `-Iinclude`, are accepted and recorded as given. They resolve against the process working directory, which is not keyed. That is an accepted limit (3.7).

**Recipe ownership.** The fixed tokens move into a Domain constant, `GccGeneratedHostRecipe` (`[Domain]`, `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs`). It holds `DefaultCompiler` (`"gcc"`), `DefaultCompilerArgTokens`, the literals `-c`, `-o`, and `-s`, and the three placeholders. The adapter consumes this constant (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:25`, `:82-85`, `:102-103`) and no longer keeps its own copy. The workflow computes `recipeTokens` from the same constant. The constant is named for gcc because every flag in it is a gcc flag. A second toolchain needs its own kind (for example an enum with `Gcc` and `Mingw`, keyed in the build key) and its own recipe constant. No such kind exists today. A change to a flag therefore changes the key, and the adapter cannot drift from it. This move is a required change and is not implemented by this design.

The tier-1 test in Section 4.2 checks this rule: the same input built into two different directories gives the same `buildKey` and the same manifest bytes.

### 3.7 Environment, PATH, working directory, and driver inputs

Each input that could change the output is one of three classes:

- **Keyed**: it is in the build key or in a checked identity in the manifest.
- **Presence-reported**: the probe reports whether it is set. The name goes into the local history entry. The build still runs. The name is not keyed, because the key must not depend on the machine.
- **Out of scope for the key**: it does not change output bytes under the stated basis, or it is not read by the build. The reason is given.

Since Skip is disabled, no class triggers a Skip decision. Every build runs.

**Presence-reported list.** `RunToolchain` does not set or clear the environment (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:244-250`), so the child process inherits the parent's variables. The probe reports the names that are set, using ordinal, ignore-case comparison. Windows environment names are case-insensitive, so `cpath` and `CPATH` are the same name. The fixed list is `CPATH`, `C_INCLUDE_PATH`, `CPLUS_INCLUDE_PATH`, `OBJC_INCLUDE_PATH`, `LIBRARY_PATH`, `COMPILER_PATH`, `GCC_EXEC_PREFIX`, `LD_RUN_PATH`, and `GNUTARGET`. Any name beginning with `GCC_` (prefix rule) is also reported, which includes `GCC_COLORS`. The list is fixed in the workflow. It cannot cover every variable a toolchain might read. No environment hash is kept. An earlier draft kept one to gate Skip. That draft is withdrawn with Skip.

**Driver and linker inputs, per row:**

| Input | Could change output | Class | Basis |
|---|---|---|---|
| `CPATH`, `C_INCLUDE_PATH`, `CPLUS_INCLUDE_PATH`, `OBJC_INCLUDE_PATH` | header search | presence-reported | fixed list; names only |
| `LIBRARY_PATH` | library search | presence-reported | fixed list |
| `COMPILER_PATH`, `GCC_EXEC_PREFIX` | tool and sub-program search | presence-reported | fixed list |
| `LD_RUN_PATH` | linker runtime search path | presence-reported | fixed list; PREDICTED to affect the link |
| `GNUTARGET` | selects the binary format of the linker | presence-reported | fixed list; PREDICTED to affect the link |
| any `GCC_*` name (for example `GCC_COLORS`) | driver behavior | presence-reported (prefix rule) | conservative; names cannot be enumerated |
| `PATH` | selects the driver and linker that run | not keyed | The probe and the build resolve the driver by the same name, in the same process, so the recorded driver version line describes the driver that ran. A different `gcc` found earlier on `PATH`, or in the working directory on Windows, is an accepted limit, not detected. |
| current working directory | relative `-I` tokens and any name the driver resolves from it | not keyed | accepted limit (3.6). Relative tokens are recorded as given. |
| `TMPDIR`, `TMP`, `TEMP` | location of temporary files | out of scope for the key | PREDICTED: gcc writes only temporaries here. Not used to decide anything. |
| `SOURCE_DATE_EPOCH` | `__DATE__` and `__TIME__` in the source, timestamps in archives | out of scope for the key | OBSERVED: no `__DATE__` or `__TIME__` in `src/PSXRecomp.Core/Recompiler`, `src/PSXRecomp.Cli`, or `src/PSXRecomp.Infrastructure` (grep). Not checked: the included runtime headers were not grepped. PREDICTED: the link does not use an archive. UNVERIFIED: clang may honour `SOURCE_DATE_EPOCH`, which would matter if clang were ever the driver. |
| `LANG`, `LC_ALL`, `LC_MESSAGES` | diagnostic language | out of scope for the key | PREDICTED: diagnostics only. UNVERIFIED for the exec charset of compiler output. |
| `DEPENDENCIES_OUTPUT`, `SUNPRO_DEPENDENCIES` | dependency-file output | out of scope for the key | PREDICTED: writes an extra file, not the binary. The adapter does not set them. |
| `<driver>.cfg` (driver configuration) | prepends driver options | accepted limit (not keyed) | UNVERIFIED for this toolchain. Section 3.2. |
| `@file` response files | adds arguments | keyed as presence, through the extra-argument record | 3.5 `extraArguments` |
| `-specs=` and other extra arguments | driver behavior | keyed as given | 3.5 `extraArguments`, rule in 3.6 |
| C runtime, import libraries, headers (package) | compile and link inputs | accepted limit (not keyed) | not probed. Section 3.2. |
| driver version line | driver behavior | keyed | 3.2 |
| linker version line | link behavior | keyed | 3.2 |
| target triple | ABI and default flags | keyed | 3.2 |

**Accepted limits.** A tracked limit is accepted only while it is stated. The owner rule is: when a limit could have changed, rebuild. In v1 the build always runs, so every build takes the changed input into account. The limit that remains is about the record. The manifest of a build does not show an untracked change (for example a C runtime update or a driver configuration edit), so the manifest cannot prove that such a change did not affect the output. Operators must treat these triggers as review events:

- any update of the C runtime or import-library package, including a runtime-only update that leaves the driver version line unchanged;
- any edit to a driver configuration file (`<driver>.cfg`) or to a `@file` response file;
- any reinstallation or edit of the toolchain directory, or a change of what `PATH` and the working directory resolve to.

The workaround is the same for each: rebuild. Because the build always runs, a rebuild is simply running the build again, and the operator should also delete the output directory when a clean record is wanted. Skip is never the workaround for an untracked input, and no Skip exists in v1 to misuse.

**Complete reproducibility is not guaranteed.** The always-build rule and the keyed inputs do not make the build reproducible across machines, toolchains, library versions, or working directories. Section 4 gives the scope of what the key covers.

### 3.8 Digests read by the packaged host (H2, H3)

The packaged host (issue #48, W12) verifies its inputs before use: H2 for every package, and H3 for HLE packages only. The fail-closed rules are those of W12 section 3.4, quoted in the paragraph "Fail closed" below. H2 is the game artifact named by `manifest.Artifact` in `runtime-host.json` (W12 section 3.4). H3 is the program image sidecar named by `manifest.Image` (`artifact-image.bin`). The expected digests live in one carrier, `build-manifest.json`, described in 3.5. They are not in `runtime-host.json`, because that would be a schema-2 change, which Q16 rejects (W12).

**Carrier fields, one per digest.**
- H2: `binary.sha256`, with `binary.file` as the bare file name of the artifact.
- H3: `packaged.imageSidecar.sha256`, with `packaged.imageSidecar.file` (`artifact-image.bin`, `RuntimeHostLayout.ImageFileName`, `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostLayout.cs:45`). **H3 applies to HLE packages only.** A firmware package has no sidecar and no `packaged` block, and H3 is not checked for it. The sidecar is written only on the HLE path (`src/PSXRecomp.Cli/PackageCommand.cs:231`), and firmware packages are routed away before that (`src/PSXRecomp.Cli/PackageCommand.cs:70-72`).

**H3 is not `codegen.programImageInputSha256`.** The codegen field is the hash of the bytes passed to codegen. It is computed before the build and is a key input (`src/PSXRecomp.Cli/PackageCommand.cs:155`). H3 is the hash of the file the packager writes after the build (`src/PSXRecomp.Cli/PackageCommand.cs:231`). The two values are the same only if both see the same bytes. The basis is that both call the same function, `ProgramImageBytes(input.InstructionWords)`, and that function writes each word little-endian with no header (`src/PSXRecomp.Cli/PackageCommand.cs:322-330`). No code in `src/` computes either hash today (grep, OBSERVED). **Equality is UNVERIFIED** until a test hashes the written `artifact-image.bin` and compares the result with the codegen input bytes.

**Location.** A packaged build, HLE or firmware, writes the manifest to `runtime/build-manifest.json` and writes nothing at `<outputDir>/build-manifest.json`. For an HLE package the manifest sits beside the sidecar. A non-packaged build writes `<outputDir>/build-manifest.json`. A build has one location and one writer.

**Firmware packages (carrier).** A firmware package (`FirmwarePackageCommand`, routed at `src/PSXRecomp.Cli/PackageCommand.cs:70-72`) has its carrier at `runtime/build-manifest.json`, with `binary.sha256` (H2) and no `packaged` block. H2 applies to every package, and the host runs its H2 check at `src/PSXRecomp.RuntimeHost/Program.cs:229`, before the firmware branch (W12 section 3.4). Without this carrier a firmware run would have no expected digest and would fail closed every time. The writer is the firmware packaging caller, which uses the same single writer, once, after the artifact is built (`src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:426-427`) and before its staging is committed. Moving that caller onto the writer is a listed migration item (7.2).

**Writer and order.** The single writer is the caller at the composition root (7.2), through `IBuildArtifactStore.WriteText`. For a packaged build the order is: the workflow builds and promotes the artifact (H2 is `binary.sha256`, computed from the staged artifact before promotion; promotion is a move of the same bytes). The packager then writes the sidecar (`PackageCommand.cs:231`), hashes it (H3), and writes `runtime/build-manifest.json` last. The manifest is never written before the digests it contains.

**Owner.** Issue #46 (this design, W14) owns the schema, the contents, and the writer. W12 owns the host check and its fail-closed behaviour (W12 section 3.4).

**Read-only input.** The packaged host reads the carrier as a read-only input. It never writes, regenerates, or edits the manifest or any digest. If a digest is wrong, the fix is on the writer side: rebuild or repackage. Editing the manifest is never the fix.

**Fail closed (W12 section 3.4, quoted).** "A missing or unreadable input, a missing or unreadable carrier, or a digest mismatch fails closed. The run is classified as a setup failure, and no guest code runs. A warning is not sufficient. The host reads the carrier as a read-only input and never writes, regenerates, or edits it or any digest." The H15 game-window path reads the same two files, and it must run the same two checks before its reads (W12 section 3.4). The check itself is implemented in W12.

**Other checks (UNVERIFIED).** W12 found no SHA-256 check for H2 or H3 in the code it read (OBSERVED there). Whether another code path checks H2 or H3 is UNVERIFIED.

**Packaging consequence (Q13).** `runtime/build-manifest.json` is a new packaged file. The ADR-027 Step C layout does not list it (W12 section 2.9). Adding it changes the packaged layout, and that decision belongs to the Q13 baseline decision, which the owner takes together with #46 (W12 section 3.4). The firmware carrier is also a new packaged file outside the layout, under the same decision. The integrity requirement stands regardless of Q13.

**Skip and build-key rule for the digests (citable by W12).** The digests in `build-manifest.json` (`binary.sha256`, `packaged.imageSidecar.sha256`) are post-build outputs. They are not in `buildKey`. They do not affect the build decision, because there is no Skip in v1 (Section 6). The host's reads of them are not build inputs. The identity rules in 3.1 and 3.5 and the accepted-limit rules in 3.7 are unchanged. `runtime-host.json` is not covered by these digests. Its integrity is W12's concern.

## 4. Reproducibility: scope and limits (no guarantee)

**Complete reproducibility is not guaranteed.** This section gives what the key covers and what it does not. It does not promise that a build is reproducible.

### 4.1 What is expected today (tier 0)

Expected, not run: the same IR, with default options, gives byte-identical C source when `RecompilerHostCodeGen.Generate(program)` is called twice in-process. The test `Determinism_Same_IR_Produces_ByteIdentical_Source` (`src/PSXRecomp.Tests/Recompiler/HostCodeGenTests.cs:576`, OBSERVED test name) does exactly that. Its scope is narrow. It calls only the single-argument overload with default options. It does not cover `traceUnits`, `guardStats`, a non-empty loaded-code table, or the artifact driver. The artifact-driver determinism tests (`src/PSXRecomp.Tests/Recompiler/RecompiledArtifactCodeGenTests.cs:144`, `:223`, `:252`, OBSERVED names) cover `GenerateStandalone` and `Generate`, and their option coverage was not checked. The docs state the rules (`docs/development/recompiler-host-codegen.md:301-310`, OBSERVED): fixed naming, fixed indentation, fixed block order by `EntryPc`, no timestamps, GUIDs, random names, or paths.

The code supports those rules: block iteration follows the IR list (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:288`), loaded code is ordered by entry PC (`:293`, `:937`, and `src/PSXRecomp.Core/Recompiler/LoadedCodeTable.cs:63`). CONFIRMED. The local `var valueNames = new Dictionary<int, string>()` at `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:565` is a method local, not a field, and it is used for lookups in the code I read. I did not confirm that no iteration over a `Dictionary` or `HashSet` reaches the output. UNVERIFIED for the whole file.

Tier 0 does not cover: a different `traceUnits` or `guardStats` setting, a different entry point (`recompile` vs `run` vs `package`), or a different code version.

### 4.2 Proposed key scope (tier 1), not a guarantee

Given the same input bytes (input SHA-256), the same psxrecomp generator version, the same codegen options, the same entry roots, and the same loaded-code table, the generated `.c` is byte-identical, and the `buildKey` is identical. The manifest's `source.sha256` field records this, and a reviewer can verify it. This is a description of what the key covers. It is not a stability promise.

Before this scope is relied on, these tests must exist and pass:

- The codegen options are recorded (3.2), so two builds with different `guardStats` are not the same build.
- The generator version constant exists (3.3).
- Test A (required): build the same input twice through the same entry point, into two different output directories. The `buildKey`, the `source.sha256` field, and the manifest bytes must all be identical. The test fails if any path, GUID, or timestamp reaches the key or the manifest.
- Test B (later): compare across entry points for the default options.
- Test C (required): equal-byte inputs built under two binary names (for example `game-a.exe` and `game-b.exe`) produce different `buildKey` values. Each build runs and produces its own name. Building the second name never reuses the first name's output. This test catches a key that omits the name, which Test A cannot catch, because Test A keeps the name fixed.
- Test D (required): the workflow rejects each of `-I/does not exist/include`, `C:\\lib`, and `-Wl,-rpath,/opt/lib` with `PATH_ARGUMENT_NOT_ALLOWED`. It makes zero calls to `IGeneratedHostBuildService` and creates no staging area. It accepts `-Iinclude`. The test uses a fake port that counts calls.
- Test E (required): two identical requests with an extra compiler or linker argument each call the build port. No code path skips the build.
- Test F (required): changing only `EntryRoots` or the input kind changes the `buildKey`, because both change `Source`. The `source.sha256` field must also change.
- Test G (required): a fake `IToolchainProbe` and a fake `IGeneratedHostBuildService` both receive the identical `compiler` string. The string is `request.CompilerExecutable` when set, and `"gcc"` otherwise. The test also covers `Available = false`, which must fail with `TOOLCHAIN_UNAVAILABLE` before staging and make no build call.
- Test H (required): the probe reports presence for `CPATH`, lowercase `cpath`, `LD_RUN_PATH`, `GNUTARGET`, and `GCC_COLORS`. Each name appears in the local history entry. None of them changes the build decision or the key.
- Test I (required): a change in the linker version line alone changes the `buildKey`.

### 4.3 Binary identity (tier 2): not guaranteed, not yet measured

Binary byte-identity is the claim a user would want most, and I could not establish it. The factors that can break it:

- The compiler and linker version, and the target. PREDICTED to change the binary. UNVERIFIED.
- Build paths embedded in the binary. The firmware path strips them with `-s` (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:102`), and that comment says "strips the symbols and build-path strings". OBSERVED. So a non-stripped binary can contain build-path strings. Whether the game binary does, and whether it depends on the output directory, is UNVERIFIED.
- The object and source paths appear in the compiler argv (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:83`, `:85`). Whether they reach the binary without `-g` is UNVERIFIED.
- Timestamps. gcc does not embed a build timestamp by default. PREDICTED. UNVERIFIED for these flags and this gcc.

Experiment required before any tier-2 claim (run on one machine first, then two): build the same source twice into the same directory and into two different directories; compare binary hashes; then run on a second machine with the same compiler version. Record the results in this document.

### 4.4 What is not guaranteed, even at tier 1

- Runtime behavior of a standard artifact. It reads its input at run time (`src/PSXRecomp.Cli/RecompileCommand.cs:99`), so the same binary can behave differently with a different input file. CONFIRMED from the comment; behavior PREDICTED.
- The runtime host. `dotnet publish` output is not in any identity (Section 2.5). Whether it is reproducible is UNVERIFIED.
- Behavior across OS and compiler versions. Not guaranteed. The CI uses the runner's gcc (2.7).
- Builds across C library, import library, header, specs, or driver configuration changes (Section 3.7). These are accepted limits, and the manifest does not record them.
- Any build whose working directory or `PATH` resolves a different compiler (Section 3.7).
- Confidentiality of the output directory. It holds game-derived code and data (2.3, 3.5). This design does not make it safe to share. It stays under the same rules as before.
- Any run that uses an external BIOS (`src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:629`). The BIOS is never part of the artifact, so the BIOS file is outside the guarantee. Only its provenance label is recorded (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:60-68` labels such as `user-supplied`). The BIOS content is never recorded, hashed into a committed file, or included in a manifest.
- Artifacts built with `guardStats` or `traceUnits`. These are measurement builds. They are not release builds, and the manifest must say so.

## 5. Build history

Proposed: an append-only local log, one JSON object per build, with the timestamp, compiler path, step outcomes, the `buildKey`, and the presence-reported environment names. It is local-only and is never committed, consistent with `docs/development/artifact-policy.md:72-73`. Its location depends on #42 (Project / Workspace), which is not implemented. Until then, the log can live next to the output directory (`<outputDir>/build-history.jsonl`). It is a diagnostic record. Nothing reads it to decide whether to build.

The workflow builds one `BuildHistoryEntry` (Domain, `[Domain]`, Section 7.2) and passes it to `IBuildHistoryLog.Append`. The entry carries the real paths as data: the compiler executable, the output directory, and the staging directory. The entry has no time field, because Domain forbids `DateTime.Now` and `DateTime.UtcNow` (`src/architecture.contract.json:25-26`; matrix Forbidden API table). The Infrastructure adapter adds only the timestamp when it writes the line. It does not add or change any other field.

Open: whether history is per-output-directory or per-project. See Section 9.

## 6. Clean and verify (no Skip in v1)

**Build always runs.** In v1 the workflow runs the build on every request. There is no Skip path, and no record, hash, or environment check can cause a build to be skipped. A future Skip needs a separate design. That design must fingerprint every file the driver resolves, including the C runtime, import libraries, headers, specs, driver configuration, and the compiler binary, and it must be reviewed on its own. This document does not define it.

- **Clean.** Remove exactly the files in the manifest's `files` list (Section 3.5): `<name>.c`, `<name>[.exe]`, and `<name>.o` for game builds. `build-manifest.json` is removed as well. It is named explicitly and not hashed, because it cannot list its own hash. The caller writes it after promotion (Section 7.2). For a packaged build the same rule applies to `runtime/build-manifest.json`, and clean removes that one file only. It never removes any other `runtime/` file, because the rest of the packaged layout is Q13's concern. A file not in that list, and not the manifest, is never deleted. `artifact-input.txt` and `artifact-image.bin` are not in the list. The engine writes them (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:511-512`), and the engine is not migrated to the workflow (Section 7.2), so clean does not touch them. Their removal belongs to the engine migration.
- **Verify.** Re-hash the listed files and compare with the manifest. This is a check that the output has not been changed since the build. It is not a build decision, and it never prevents a build. A mismatch is reported. It is not repaired by editing the manifest.

**Extra arguments.** The manifest records extra arguments as given. Each build with an extra argument runs like any other build. Relative tokens such as `-Iinclude` resolve against the working directory, which is not keyed. That is an accepted limit (3.7). Release builds keep the working directory fixed.

**Build coverage.** The workflow's cost before the build (load, lowering, and codegen) runs on every request, as does the build itself. For a CHD input, the load also runs the disc analysis (`src/PSXRecomp.Cli/CliInput.cs:115`, `RomAnalysisPipeline.RunFromChd`). That cost is PREDICTED to be the largest before Build, and it is not measured.

## 7. Management layer that keeps the Build API

The goal is that every current caller compiles and behaves the same, and the new workflow sits above the port. The layer rules are ADR-017 points 1-3 (Domain owns ports; Infrastructure owns file, process, and clock I/O; Application does not depend on Infrastructure except at one composition-root site) and matrix rule 3 (`docs/architecture-matrix.md:66`: Application does not acquire host resources directly).

### 7.1 Keep the port

- `IGeneratedHostBuildService`, `GeneratedHostBuildRequest`, and `GeneratedHostBuildResult` (`src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:48`, `:63`, `:86`) keep their current members. Any new request field is optional and goes at the end of the positional record, so every existing call site still compiles. The existing callers listed in 2.1 are not edited by this design.
- The result keeps its shape. Build provenance is gathered by the layer above, not returned through the port.

### 7.2 Domain ports, the workflow, and the composition root

**Why the workflow lives in Domain (PSXRecomp.Core).** `PSXRecomp.Cli` is classified as Infrastructure, because its namespace is `PSXRecomp.Infrastructure.Cli` (`src/PSXRecomp.Cli/RecompileCommand.cs:7`, Infrastructure namespace root in `src/architecture.contract.json:5`). The contract forbids Infrastructure from depending on Application (`src/architecture.contract.json:13`). So the CLI cannot reference an Application project such as `PSXRecompStudio`. A workflow shared by CLI and GUI must therefore be a Domain service that takes ports, and the GUI (Application) may call it too (Application to Domain is allowed, ADR-017 and matrix dependency table).

**Layer declaration.** The contract requires a layer declaration (`src/architecture.contract.json:58`, `"required": true`). Every new Domain type and port in this section carries `[Domain]`, as `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:47` does for the existing port. The new types live in `PSXRecomp.Core` and use no host call, no `DateTime`, and no `Guid`.

**Domain records.**

- `ArtifactBuildRequest(string InputSha256, CodegenOptions Codegen, CodegenInputs CodegenInputs, string Source, string BinaryName, string OutputDirectory, string? CompilerExecutable, IReadOnlyList<string> ExtraCompilerArguments, IReadOnlyList<string> ExtraLinkerArguments, bool FirmwareDistribution)`. It is the single input to the workflow.
  - `InputSha256` comes from the CLI (3.2).
  - `Source` is the generated C text from `RecompiledArtifactCodeGen` (`src/PSXRecomp.Cli/RecompileCommand.cs:86-101`). The workflow passes it to the port as `GeneratedHostBuildRequest.Source`, because the adapter writes the `.c` file itself (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:55-59`). The workflow also hashes it (`source.sha256`).
  - `Codegen` is a `CodegenOptions` and `CodegenInputs` is a `CodegenInputs`, both defined below.
- `CodegenOptions(string Mode, bool TraceUnits, bool GuardStats, bool AllowEmptyProgram, bool HostBridge, bool ExternalBios, bool HasWindowedLaunch, string DirectLaunchHostRelativePath, string WindowedHostBinaryName, string WindowedArgumentRelativePath, int WindowedWaitLimitMs, long Budget, long InitialPc, long InitialHi, long InitialLo, long ImageLoadAddress, string GeneratorVersion)`. Every field is a string, bool, int, or long. An absent path is `""`, and `HasWindowedLaunch` says whether the windowed fields apply.
- `CodegenInputs(IReadOnlyList<uint> InitialGpr, IReadOnlyList<(uint Address, uint Value)> InitialMemory, IReadOnlyList<byte> ProgramImage)`. These are the arrays. They are only read by the hashing helper and are never written to the key or the manifest.
- `BuildCodegenRecord(CodegenOptions Options, string InitialGprSha256, string InitialMemorySha256, string ProgramImageSha256)`. This is the record the key and the manifest use. It is produced by one pure Domain function, `BuildCodegenRecord.From(CodegenOptions options, CodegenInputs inputs)`, which hashes each array with SHA-256 over its little-endian bytes. The workflow calls it once, before the key is computed.
- `BuildStagingArea(string DirectoryPath)`. It is string-only.
- `BuildHistoryEntry`. It is string-only, except for an ordered list of strings. It has these fields: `BuildKey`, `Outcome`, `CompilerExecutable` (a real local path), `OutputDirectory` (real), `StagingDirectory` (real), `ProductRevision`, `PresentEnvironmentNames` (local only, a list of strings), and `Steps` (each element `"<step> <outcome> <exitCode>"`). It has no time field and no `FileInfo`, `DirectoryInfo`, or `DateTime` member.
- `ToolchainIdentity(bool Available, string? DriverVersionLine, string? LinkerVersionLine, string? TargetTriple, IReadOnlyList<string> PresentEnvironmentNames)`. The probe is in Infrastructure, because reading the environment is a host call (`Environment` is forbidden in Domain, `src/architecture.contract.json:22`). Only the names of the variables that are set are returned. Values are never returned.
  - `Available = false` means the driver, or the linker it reports, could not be run. The workflow then fails before any staging area is created, with `TOOLCHAIN_UNAVAILABLE`. This is the same status the adapter reports today (`GeneratedHostBuildStatus.ToolchainUnavailable`).

**Domain ports** (new, `PSXRecomp.Core`, `[Domain]`):

- `IBuildArtifactStore`: the only way the workflow touches files.
  - `BuildStagingArea CreateStaging(string outputDirectory)`
  - `string Sha256(BuildStagingArea area, string fileName)`
  - `void WriteText(BuildStagingArea area, string fileName, string text)`
  - `string? ReadOutputText(string outputDirectory, string fileName)`, used to read the existing manifest for Verify
  - `string Sha256Output(string outputDirectory, string fileName)`, used by Verify
  - `void Promote(BuildStagingArea area, string outputDirectory, IReadOnlyList<string> fileNames)`, which replaces only the listed files
  - `void Discard(BuildStagingArea area)`
- `IToolchainProbe`: `ToolchainIdentity Probe(string compilerExecutable)`. `ToolchainIdentity` is defined above. Process execution happens only behind this port.
- `IBuildHistoryLog`: `void Append(BuildHistoryEntry entry)`. The entry has no time field. The adapter stamps the time.

**Pure Domain code** (no port needed): the build key (SHA-256 over the canonical form of the Section 3.5 fields), the `BuildManifest` record and its canonical JSON text, and the Verify comparison of Section 6 (given hashes that the store returns).

**Infrastructure implementations** (`PSXRecomp.Infrastructure`; every host call this design adds, namely `File`, `Directory`, `Process`, `Guid`, and the clock, is implemented here):

- `FileBuildArtifactStore : IBuildArtifactStore`. The staging name uses a GUID, as `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:510` already does.
- `ProcessToolchainProbe : IToolchainProbe`. It wraps `GeneratedHostBuildService.RunToolchain` and the stdout change in 3.4. It reads the environment, using ordinal ignore-case names, for the presence list in 3.7.
- `FileBuildHistoryLog : IBuildHistoryLog`. It stamps the time.

**Workflow.** `ArtifactBuildWorkflow` (Domain, `PSXRecomp.Core`, `[Domain]`) takes `IGeneratedHostBuildService`, `IBuildArtifactStore`, `IToolchainProbe`, and `IBuildHistoryLog` through its constructor. It runs these steps:

1. Resolve `compiler = request.CompilerExecutable ?? GccGeneratedHostRecipe.DefaultCompiler` once. Probe it through `IToolchainProbe.Probe(compiler)`, and compute the build key from the inputs in 3.2, including the probed driver version line, linker version line, and target triple. If `Available` is false, fail with `TOOLCHAIN_UNAVAILABLE` before staging. No staging area is created, and no build runs.
2. If an existing manifest is present, run Verify on its listed files (Section 6). The result is reported. It does not decide whether the build runs.
3. Always run the build. Create a staging area through `CreateStaging`. The workflow passes the staging directory string as `OutputDirectory`, `request.Source` as `Source`, `request.BinaryName`, the extra arguments, the `FirmwareDistribution` flag, and `CompilerExecutable: compiler` (the same string the probe received, from step 1) to `IGeneratedHostBuildService.Build`. Without the explicit `CompilerExecutable`, the adapter would fall back to its own default (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:74`) and could build with a different driver than the one probed. The workflow never opens or creates that directory itself. On failure, `Discard`.
4. Hash the source and the binary through `Sha256`, and return the `BuildManifest` record. The workflow does not write the manifest.
5. `Promote` the artifact files, replacing only the names in `files`. Append one entry to `IBuildHistoryLog`.
6. The caller writes `build-manifest.json` once, with `WriteText`, to the location for that build (3.5, 3.8). For a packaged build, this happens after the packager writes the image sidecar. This is the only writer of the manifest.

**Composition root.** `PSXRecomp.Cli` is the composition root for the new port implementations. It alone constructs `FileBuildArtifactStore`, `ProcessToolchainProbe`, and `FileBuildHistoryLog`, and passes them to `ArtifactBuildWorkflow`. The claim covers only those new implementations. Other callers still construct `GeneratedHostBuildService` directly, as listed in 2.1 (`src/PSXRecomp.Infrastructure/Standalone/LiveGameRunner.cs:77`, `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:109`, `src/PSXRecomp.Cli/PackageCommand.cs:170`, constructor). The CLI may reference Infrastructure because it is classified as Infrastructure by namespace (`src/PSXRecomp.Cli/RecompileCommand.cs:7`, `docs/architecture-matrix.md:24`). ADR-017 point 3 governs the composition of Application-layer code. This design puts no Application code on the composition path, so that rule does not apply here. Whether the analyzer agrees is open question 14.

**Migration.** `RecompileCommand` (`src/PSXRecomp.Cli/RecompileCommand.cs:115-116`) moves to the workflow first, because it is the CLI surface (#15). The engine (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:563`, `:1281`) and `PackageCommand` (constructor `:170`, build call `:171-178`) keep calling the port directly until a later change. `DoctorCommand` moves its probe to `IToolchainProbe` (Section 3.4).

### 7.3 What this does not change

- Generated C source (Section 3.5).
- `RuntimeHostManifest`, `FirmwareBootManifest`, `LoadImageManifest`.
- The fixed artifact name `recompiled-artifact` used by the engine. A later change can make the name part of the build key.

## 8. Recommendation

1. Record the build key, source hash, and binary hash now, computed by `ArtifactBuildWorkflow` (Domain) through the ports in 7.2, and written once by the caller as `build-manifest.json` (schema 1), at the location in 3.8. Do not change generated C. Record and verify; make no stability promise.
2. Record codegen options and the generator version in the build key. This closes the largest gap (2.4).
3. Add a `Stdout` field to `ToolchainResult` (3.4). Capture the compiler `--version` and target through `IToolchainProbe`, implemented in Infrastructure. Do not record local paths in the manifest.
4. Keep Build always-on in v1 (Section 6). Treat any future Skip as a separate design with a file fingerprint.
5. Keep the existing Build API. Place the workflow in Domain over `IBuildArtifactStore`, `IToolchainProbe`, `IBuildHistoryLog`, and `IGeneratedHostBuildService`. Infrastructure implements the ports, and `PSXRecomp.Cli` composes them. Migrate `RecompileCommand` first.
6. Keep history local-only and wait for #42 to decide its location. Run the tier-2 experiment (4.3) before any binary-identity claim.

This sequence respects the issue's priority: it does not block the vertical slice, and it does not change the artifact bytes that the existing tests depend on.

## 9. Open questions

1. **Owner decision:** is binary byte-identity a goal? If it is, the tier-2 experiment in 4.3 is required before implementation is scoped.
2. **Owner decision:** should `guardStats` and `traceUnits` builds be refused for release packaging, or only be recorded in the manifest? Today `package` can set `guardStats` through an environment variable (`src/PSXRecomp.Infrastructure/GuardStatsScope.cs:34`).
3. **Compiler pinning:** accept any `gcc` and record its identity, or require a specific version? CI installs no compiler (2.7). UNVERIFIED which gcc the runners have.
4. **Generator version:** a hand-bumped constant, or a hash of the generator source? A hash changes on every commit and invalidates all keys. A constant depends on discipline.
5. **Embedded or sidecar:** this design uses a sidecar. Confirm that a reviewer does not need the key inside the binary.
6. **Analyzer and decoder version:** where is it, if it exists? Not located. UNVERIFIED.
7. **Runtime host reproducibility:** does `dotnet publish -c Release` with the default settings produce a stable output? UNVERIFIED.
8. **Overwrite policy:** should a build into a non-empty output directory fail, replace, or require an explicit flag? Today it replaces silently (2.2).
9. **Game build `.o` retention:** the object is kept for game builds (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:111-115`), and only firmware removes it. Is the retention intended?
10. **Compiler version in diagnostics:** `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionEnvironmentCollector.cs:28` always writes `compilerVersion: null`. Should it be filled from the same probe?
11. **History location:** per output directory, or per project under #42?
12. **Cleanup in source:** `src/PSXRecomp.Cli/RecompileCommand.cs:28-31` has a `<summary>` for "The standalone artifact binary name." with no member under it. It looks like a leftover. Not part of this design; noted for whoever edits that file.
13. **GUI composition root:** the Studio is Application-layer and may not reference Infrastructure (ADR-017 point 3). Which host bootstrap constructs the GUI's Infrastructure adapters? The matrix recommends a dedicated Host/Bootstrap layer (`docs/architecture-matrix.md:210`). Not decided here.
14. **Analyzer classification of the CLI:** the composition root in Section 7.2 assumes the analyzer treats `PSXRecomp.Cli` as Infrastructure by namespace (`src/PSXRecomp.Cli/RecompileCommand.cs:7`). UNVERIFIED until a build runs AARC on the new code.
15. **Resolved (M1), with one residual:** extra arguments that contain `/`, `\`, or `:` are rejected before any build (3.6), so no absolute path enters the key through them. Relative tokens such as `-Iinclude` are accepted, recorded as given, and resolve against the working directory, which is not keyed. The residual is accepted. Should a future toolchain kind (3.6) record the working directory instead?
16. **Presence list maintenance:** who owns the fixed presence list in 3.7, and what reviews a change to it? Names outside the list are not reported.
