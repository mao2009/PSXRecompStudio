# Issue #46: Artifact identity, build provenance, and the management layer

Tracking: [#46](https://github.com/mao2009/PSXRecompStudio/issues/46). Related: #456 (first runnable artifact, complete through #458-#461), #15 (CLI build/run), #42 (Project / Workspace), #43 (incremental analysis and cache), #40 (Studio GUI).

Status: **design only**. No code, test, or shared document is changed by this document. Baseline: `origin/main` at `2e3ecad`.

## 0. Evidence labels and method

- **CONFIRMED**: read in the source at the cited `file:line` in this checkout.
- **OBSERVED**: stated in a repository doc, test name, or CI file that I read. I did not execute the test or the workflow.
- **PREDICTED**: a behavior I expect from the code. I did not run it.
- **UNVERIFIED**: not checked. Each one is listed as an open question or as an experiment that must run before a claim is made.

I did not build the project, run `gcc`, or run any test while writing this document. Every behavior claim about the toolchain or the binary is therefore PREDICTED or UNVERIFIED. The source claims are CONFIRMED by reading only.

No BIOS, ROM, disc, or other copyrighted content was read or is recorded here. Paths below are repository paths and the paths of the code that handles those inputs.

## 1. Where Issue #46 stands

The issue body marks the first runnable-artifact slice as complete (#458 adapter, #459/#460 CLI and run surface, #461 E2E). Its open items are: artifact identity, build manifest, input/toolchain/output association, build history, clean/rebuild/incremental policy, a shared CLI/GUI workflow, and reproducibility requirements. This document covers the first three, the reproducibility requirements, and the shape of the shared workflow. History and incremental policy are only sketched, because they depend on #42 and #43.

## 2. Current artifact generation path

The path has five stages. Each stage lists the code that does it, and the code is shared by several callers.

### 2.1 Stages and call sites

| Stage | Where | Notes |
|---|---|---|
| Input load | `src/PSXRecomp.Cli/RecompileCommand.cs:39` `CliInput.Load(...)` | `recompile` does not compute an input hash. `run` does, through `CliInput.LoadWithIdentity` (`src/PSXRecomp.Cli/CliInput.cs:56`, used at `src/PSXRecomp.Cli/RunCommand.cs:52-58`). CONFIRMED. |
| Lowering | `src/PSXRecomp.Cli/RecompileCommand.cs:40` `CliInput.Lower(...)` | Produces the IR program. |
| Host dispatch codegen (Recompiler, Core) | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:70` (program + loaded code), `:85-88` (overload with `traceUnits`, `guardStats`, `allowEmptyProgram`) | Called from `RecompileCommand.cs:42` with the defaults, and from the runtime engine with `traceUnits` and `guardStats` set (see 2.4). CONFIRMED. |
| Artifact driver codegen (Generated source, Core) | `src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs:249` `Generate(dispatch)` (standard), `:305` `GenerateStandalone(...)` (standalone, embeds initial state and program image) | `RecompileCommand.cs:86` and `:101` select between them. CONFIRMED. |
| Build (Infrastructure adapter) | `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:50` `Build` | Writes `<BinaryName>.c` (`:55-59`), compiles (`:74-91`), checks the object (`:93-99`), links (`:103-109`), optionally removes the object for firmware (`:111-136`), resolves `.exe` (`:138-147`). CONFIRMED. |
| Build contract (Core port) | `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:48` (request), `:63` (result), `:86` (`IGeneratedHostBuildService`) | The port has no version, toolchain, or provenance fields. CONFIRMED. |
| Native artifact | `<outputDir>/<BinaryName>` (or `.exe`) plus `<outputDir>/<BinaryName>.c` | `.o` is kept for game builds (`:76`, `:111-115`) and removed for firmware. `.c` is never removed by the adapter. CONFIRMED. |

Callers of the build port, all through `IGeneratedHostBuildService` or a direct `GeneratedHostBuildService`:

- `src/PSXRecomp.Cli/RecompileCommand.cs:115-116` (name `ArtifactBinaryName`, or the derived standalone name).
- `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:563` (name `"recompiled-artifact"`, in the constructor path whose parameter list starts at `:445`), and `:1281` (in `BuildEmbeddedArtifact`, starting at `:1235`). CONFIRMED.
- `src/PSXRecomp.Cli/PackageCommand.cs:170-178` (adds `-mwindows -static-libgcc` for `--gui`). CONFIRMED.
- `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:426-427` (staged build with `FirmwareDistribution: true`). CONFIRMED.
- `src/PSXRecomp.Infrastructure/Standalone/LiveGameRunner.cs:77` and `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:109` (default `new GeneratedHostBuildService()`). CONFIRMED.

### 2.2 Fixed names and overwrite

- The engine writes to a caller-supplied output directory with a fixed name, `recompiled-artifact` (`RecompiledHostExecutionEngine.cs:563`, `:1281`). A second build into the same directory overwrites `.c` and the binary. `File.WriteAllText` at `GeneratedHostBuildService.cs:59` has no existence check. CONFIRMED by reading. The overwrite behavior is PREDICTED from that.
- The engine also writes `artifact-input.txt` and `artifact-image.bin` into the same directory (`RecompiledHostExecutionEngine.cs:515-516`). CONFIRMED.
- Firmware distribution avoids this with a staging directory named `.<artifact>.staging-<guid>` (`FirmwareDistribution.cs:510`), then a commit. CONFIRMED.

### 2.3 Runtime inputs are external

`RecompileCommand.cs:99` says: "Standard artifact (requires input files at runtime)". The standard artifact reads its input at run time and is not self-contained. The standalone artifact (`GenerateStandalone`, `RecompileCommand.cs:86-95`) embeds the initial state and program image in its C source. CONFIRMED from the comment and the call. The embedding is PREDICTED from the parameter list (`RecompiledArtifactCodeGen.cs:305-315`), since the `programImage` parameter is passed through to the source.

The external BIOS path is a separate runtime input. It is read from the environment variable named at `RecompiledArtifactCodeGen.cs:629` (`ExternalBiosEnvironmentVariable`). CONFIRMED. The standalone source records `embedded_bios_external` (`RecompiledArtifactCodeGen.cs:456`). CONFIRMED.

### 2.4 Options that change the generated source

`RecompilerHostCodeGen.Generate` takes options that change the output text:

- `traceUnits` (`RecompilerHostCodeGen.cs:85`). The engine sets it from `UnitTraceScope.CurrentPath is not null` (`RecompiledHostExecutionEngine.cs:551`, `:1253`). CONFIRMED.
- `guardStats` (`RecompilerHostCodeGen.cs:85`, `:284` emits guard support when it is true). The engine sets it from `GuardStats`. `GuardStatsScope.cs:34` turns it on from the environment variable `PSXRECOMP_NATIVE_GUARD_STATS`. `FirmwareDistribution.cs:404` says the same at package time. CONFIRMED.
- `allowEmptyProgram` (`RecompilerHostCodeGen.cs:85`, used by `FirmwareDistribution.cs:405`). CONFIRMED.
- `hostBridge`, `directLaunchHostRelativePath`, `externalBios`, `windowedLaunch` on `GenerateStandalone` (`RecompiledArtifactCodeGen.cs:305-315`). CONFIRMED.

The consequence is that the same input can produce different C source depending on which entry point built it. `recompile` uses the defaults (`RecompileCommand.cs:42`), while `run` and `package` may set `traceUnits` or `guardStats`. PREDICTED from the code paths. This is the largest gap in the current design. Section 4 addresses it.

`RecompilerHostCodeGen.cs:57` declares `s_guardStats` as `[ThreadStatic]`, and `:88` sets it on each call. Concurrent calls on different threads therefore do not share it. CONFIRMED.

### 2.5 Runtime host provisioning

The runtime host (`runtime/`) is not produced by the codegen. `RuntimeHostBuildService.cs` runs `dotnet restore` and `dotnet publish -c Release` (`RuntimeHostBuildService.cs:146-171`, `:201`). CONFIRMED. Its output is not part of any identity today. See Section 3.4.

### 2.6 Existing identity and manifest pieces

| Piece | Where | What it covers |
|---|---|---|
| `RuntimeHostManifest` | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs:40-56`, schema `:57` (`CurrentSchemaVersion = 1`) | Run contract for the packaged host: entry PC, image, block PCs, budget. No input hash, no toolchain, no product version. CONFIRMED. Written by `PackageCommand.cs:234-264`. |
| `FirmwareBootManifest` | `src/PSXRecomp.Infrastructure/Standalone/FirmwareBootManifest.cs:21`, `:35`, `:141` | Already records `DiscSha256` and per-input `Sha256`, verified before every run. CONFIRMED. This is the closest existing precedent. |
| `LoadImageManifest` | `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:27` (`SourceSha256`), `:356` (computed) | SHA-256 of each EXE-based loaded image. CONFIRMED. |
| `ProjectManifestDocument.InputSha256` | `src/PSXRecomp.Core/Project/ProjectManifestDocument.cs:29` | Project-level input hash. `docs/runtime/loaded-code-identity.md` (row 1 of its table) says no production consumer exists outside `Project/`. OBSERVED from that doc. |
| `RunCommand` diagnostic bundle | `src/PSXRecomp.Cli/RunCommand.cs:52-58` (input hash), `:191` (`artifactIdentity: sha256:<binary>`), `:206-209` (`ComputeSha256`), `:212-215` (`GetProductRevision` from `AssemblyInformationalVersionAttribute`) | The only artifact identity that exists today, and it is computed after a run for diagnostics, not at build time. CONFIRMED. |
| `ExecutionEnvironmentCollector.Capture` | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionEnvironmentCollector.cs:28` | `compilerVersion: null`, always. CONFIRMED. |
| `DoctorCommand` compiler probe | `src/PSXRecomp.Cli/DoctorCommand.cs:107-108` | Runs `<compiler> --version` and keeps only success or failure. The version text is discarded. CONFIRMED. |

Note: the name `Manifest` is already used for three different things (`RuntimeHostManifest`, `LoadImageManifest`, `FirmwareBootManifest`), plus `ProjectManifestDocument`. A new build manifest needs a distinct name. This document uses `BuildManifest`.

### 2.7 Version and toolchain facts

- The compiler is `gcc` by default (`GeneratedHostBuildService.cs:24`), overridable per request (`GeneratedHostBuild.cs:52`, `CompilerExecutable`). Flags are fixed: `-std=c11 -O0 -Wall -Wextra` (`GeneratedHostBuildService.cs:25`), plus `ExtraCompilerArguments`. CONFIRMED.
- The link step passes the object, `-s` for firmware only (`GeneratedHostBuildService.cs:102-103`, `:209-211`), extra linker arguments, and `-o`. CONFIRMED.
- `Directory.Build.props` contains no `<Version>`, `<VersionPrefix>`, or `<SourceRevisionId>` setting. CONFIRMED by reading the file. So the product version comes from the SDK default for `InformationalVersion`, which is PREDICTED to include a git revision only when the SDK sets `SourceRevisionId`. UNVERIFIED.
- CI runs on `ubuntu-latest` and `ubuntu-24.04` (`.github/workflows/ci.yml`: `:16`, `:70`, `:82`, `:128`, `:198`, `:226`). The workflow installs `ninja-build` (`:94`, `:211`). No step installs or pins `gcc`. OBSERVED from the workflow text. The gcc version that CI uses is therefore whatever the runner image provides. UNVERIFIED.
- No generator or codegen format version constant was found in `RecompilerHostCodeGen`. The only version constant in the artifact driver is `ProtocolFallbackVersion = 1` (`RecompiledArtifactCodeGen.cs:222`), which covers the fallback protocol, not the source generator. CONFIRMED by grep. Search for an analyzer or decoder version constant was not done. UNVERIFIED.

## 3. Artifact identity and manifest

### 3.1 Identity classes

Keep four identities separate. Collapsing them is what caused the problems in #734, which this design follows (`docs/runtime/loaded-code-identity.md`).

1. **Input identity.** SHA-256 of the input bytes that the codegen consumed: the EXE file for PS-X EXE input (`CliInput.cs:79`), or the CHD stream for CHD input (`CliInput.cs:100`). It identifies provenance. It is not a runtime proof. Source: `CliInput.LoadWithIdentity`.
2. **Build key.** A hash of everything that decides the generated source and the build recipe, computed before building. Its inputs are listed in 3.2. Two builds with the same build key should produce the same source (Section 4.1). It is the natural lookup key for caching later (#43).
3. **Source identity.** SHA-256 of the generated `.c` bytes. Computed after codegen. It is the strongest "what did we generate" identity.
4. **Binary identity.** SHA-256 of the native binary. Computed after the build. It is the artifact identity that `RunCommand.cs:191` already reports, but only after a run.

Binary identity is not guaranteed to be stable across builds (Section 4.3). The source identity is the stable one that the design can promise.

### 3.2 Build key inputs

The build key hashes this set. Each item is a field in the manifest, so a mismatch is explainable:

- Input identity (3.1.1), and for a standard artifact the runtime-input list in 3.4.
- Entry roots (`arguments.EntryRoots`, `RecompileCommand.cs:40`) and the segment budget (`RecompileCommand.cs:38`).
- Codegen mode: standard or standalone (`RecompileCommand.cs:56`), and the standalone options: `hostBridge`, `directLaunchHostRelativePath`, `externalBios`, `windowedLaunch`, initial state (GPRs, HI, LO, PC, budget, memory, image load address).
- Codegen options: `traceUnits`, `guardStats`, `allowEmptyProgram` (2.4). These must be part of the key. Today they are not recorded anywhere.
- Loaded-code table (`LoadedCodeTable`) identity, from `LoadImageManifest`. Its source SHA-256 is already computed (`LoadImageManifest.cs:356`).
- Generator version (3.3).
- Build recipe: compiler identity (3.4), compiler arguments, link arguments, `FirmwareDistribution`, `-mwindows`/`-static-libgcc` for `--gui`.

### 3.3 Tool versions

| Version | Source today | Proposal |
|---|---|---|
| psxrecomp product | `AssemblyInformationalVersionAttribute` (`RunCommand.cs:212-215`) | Record it in the manifest. Do not use it in the build key, because every commit would then change every key. Use the generator version instead. |
| Codegen format | None (3.2) | Add a `RecompilerGeneratorVersion` constant, bumped by hand when the emitted C changes meaning. Precedent: `ProtocolFallbackVersion` (`RecompiledArtifactCodeGen.cs:222`). |
| Analyzer / decoder | Not located. UNVERIFIED. | Find it, or record that it does not exist. |
| Runtime host | `SchemaVersion = 1` (`RuntimeHostManifest.cs:57`) | Record the publish configuration and RID, and the hash of the published host binary. |
| .NET SDK | Not recorded | Record `dotnet --version` at provision time. UNVERIFIED that this is cheap. |

### 3.4 Toolchain and build provenance

Record, per build:

- Compiler: the `--version` first line (captured instead of discarded, `DoctorCommand.cs:107-108` shows the probe). Target triple, from `<compiler> -dumpmachine` (PREDICTED to work with gcc and MinGW; UNVERIFIED).
- Compiler and linker arguments actually passed (`GeneratedHostBuildService.cs:77-85`, `:103`).
- Step results: outcome and exit code per step (`ToolchainResult`, `GeneratedHostBuildService.cs:178`). Durations are not recorded in the deterministic part.
- Compiler executable path. This is a local path. Per `docs/development/artifact-policy.md:72-73`, deterministic artifacts must not carry local paths, so the path goes in the local-only history record (Section 5), not in the manifest.

### 3.5 Manifest contents and location

Proposed file: `<outputDir>/build-manifest.json`, schema version 1, written only after the build succeeds, atomically, and committed together with the artifact (the staging pattern in `FirmwareDistribution.cs:510`).

Deterministic fields (no timestamps, no local paths):

```text
schemaVersion, buildKey,
input:      { kind, sha256 }
codegen:    { mode, traceUnits, guardStats, allowEmptyProgram, standaloneOptions..., generatorVersion }
loadedCode: [ { name, kind, loadAddress, wordCount, sourceSha256 } ]
source:     { file, sha256 }
binary:     { file, sha256, sizeBytes }
toolchain:  { compilerVersionLine, targetTriple, compileArgs, linkArgs }
runtimeInputs: [ { name, requiredBy, kind } ]   // names only, never the BIOS or ROM content
productRevision: "<InformationalVersion>"       // informational, not part of buildKey
```

Non-deterministic or local data goes into a separate local record, not into the manifest: timestamps, compiler path, machine name, and the run history. Section 5.

The manifest is a sidecar. It is not embedded in the C source. Embedding would change the source bytes and invalidate the existing determinism tests (`HostCodeGenTests.cs:576`, `RecompiledArtifactCodeGenTests.cs:144`, `:223`, `:252`, OBSERVED test names). It would also make the source identity depend on the manifest that describes it.

`RuntimeHostManifest` is not changed. It is the run contract for the packaged host, and it should stay one.

## 4. Reproducibility: the guarantee and its limits

### 4.1 What is guaranteed today (tier 0)

Same IR, same loaded-code table, same options, same code version: the generated C source is byte-identical. This is what the determinism tests check, in-process (`HostCodeGenTests.cs:576`, OBSERVED). The docs state the rules (`docs/development/recompiler-host-codegen.md:301-310`, OBSERVED): fixed naming, fixed indentation, fixed block order by `EntryPc`, no timestamps, GUIDs, random names, or paths.

The code supports those rules: block iteration follows the IR list (`RecompilerHostCodeGen.cs:288`), loaded code is ordered by entry PC (`:293`, `:937`, and `LoadedCodeTable.cs:63`). CONFIRMED. The `Dictionary<int, string> valueNames` at `RecompilerHostCodeGen.cs:565` is used for lookups in the code I read. I did not confirm that no iteration over a `Dictionary` or `HashSet` reaches the output. UNVERIFIED for the whole file.

Tier 0 does not cover: a different `traceUnits` or `guardStats` setting, a different entry point (`recompile` vs `run` vs `package`), or a different code version.

### 4.2 Proposed guarantee (tier 1), after this design is implemented

Given the same input bytes (input SHA-256), the same psxrecomp generator version, the same codegen options, the same entry roots, and the same loaded-code table, the generated `.c` is byte-identical, and the `buildKey` is identical. The manifest's `sourceSha256` lets a reviewer verify it.

This tier is not promised until these are done:

- The codegen options are recorded (3.2), so two builds with different `guardStats` are not the same build.
- The generator version constant exists (3.3).
- A test builds the same input twice through the same entry point and compares the source hash. The same test can also compare across two entry points for the default options.

### 4.3 Binary identity (tier 2): not guaranteed, not yet measured

Binary byte-identity is the claim a user would want most, and I could not establish it. The factors that can break it:

- The compiler and linker version, and the target. PREDICTED to change the binary. UNVERIFIED.
- Build paths embedded in the binary. The firmware path strips them with `-s` (`GeneratedHostBuildService.cs:102`), and that comment says "strips the symbols and build-path strings". OBSERVED. So a non-stripped binary can contain build-path strings. Whether the game binary does, and whether it depends on the output directory, is UNVERIFIED.
- The object and source paths appear in the compiler argv (`GeneratedHostBuildService.cs:83`, `:85`). Whether they reach the binary without `-g` is UNVERIFIED.
- Timestamps. gcc does not embed a build timestamp by default. PREDICTED. UNVERIFIED for these flags and this gcc.

Experiment required before any tier-2 claim (run on one machine first, then two): build the same source twice into the same directory and into two different directories; compare binary hashes; then run on a second machine with the same compiler version. Record the results in this document.

### 4.4 What is not guaranteed, even at tier 1

- Runtime behavior of a standard artifact. It reads its input at run time (`RecompileCommand.cs:99`), so the same binary can behave differently with a different input file. CONFIRMED from the comment; behavior PREDICTED.
- The runtime host. `dotnet publish` output is not in any identity (Section 2.5). Whether it is reproducible is UNVERIFIED.
- Behavior across OS and compiler versions. Not guaranteed. The CI uses the runner's gcc (2.7).
- Any run that uses an external BIOS (`RecompiledArtifactCodeGen.cs:629`). The BIOS is never part of the artifact, so the BIOS file is outside the guarantee. Only its provenance label is recorded (`RuntimeHostManifest.cs:60-68` labels such as `user-supplied`). The BIOS content is never recorded, hashed into a committed file, or included in a manifest.
- Artifacts built with `guardStats` or `traceUnits`. These are measurement builds. They are not release builds, and the manifest must say so.

## 5. Build history

Proposed: an append-only local log, one JSON object per build, with the timestamp, compiler path, step outcomes, and the `buildKey`. It is local-only and is never committed, consistent with `docs/development/artifact-policy.md:72-73`. Its location depends on #42 (Project / Workspace), which is not implemented. Until then, the log can live next to the output directory (`<outputDir>/build-history.jsonl`) and be treated as a diagnostic, not as state.

Open: whether history is per-output-directory or per-project. See Section 7.

## 6. Clean, rebuild, and incremental policy (sketch)

- **Clean.** Remove exactly the files listed in `build-manifest.json` (`source`, `binary`, `.o` if present, `artifact-input.txt`, `artifact-image.bin`). A file not in the manifest is never deleted.
- **Rebuild.** Build when the manifest is missing, when `buildKey` differs, or when a listed file's hash does not match. Otherwise skip. This is the only incremental decision in v1. It does not touch analysis or codegen caching, which belongs to #43.
- **Verify.** Re-hash the listed files and compare with the manifest. This is cheap and needs no compiler.

## 7. Management layer that keeps the Build API

The goal is that every current caller compiles and behaves the same, and the new workflow sits above the port.

### 7.1 Keep the port

- `IGeneratedHostBuildService`, `GeneratedHostBuildRequest`, and `GeneratedHostBuildResult` (`GeneratedHostBuild.cs:48`, `:63`, `:86`) keep their current members. Any new request field is optional and goes at the end of the positional record, so every existing call site still compiles. The existing callers listed in 2.1 are not edited by this design.
- The result keeps its shape. Build provenance is gathered by the layer above, not returned through the port.

### 7.2 Add a layer above the port

A class in the Application layer (name proposed: `ArtifactBuildWorkflow`) with one entry point that CLI and GUI both call:

1. Compute the build key from the inputs in 3.2.
2. Check the existing manifest (Section 6, Verify and Rebuild).
3. Run the existing `IGeneratedHostBuildService.Build` into a staging directory, not the final one.
4. Re-hash the source and the binary, and write `build-manifest.json` into the staging directory.
5. Commit: move the staged files into the output directory, replacing only files the manifest lists.

`RecompileCommand` moves to this workflow first, because it is the CLI surface (#15). The engine and `PackageCommand` can keep calling the port directly until a later change.

### 7.3 What this does not change

- Generated C source (Section 3.5).
- `RuntimeHostManifest`, `FirmwareBootManifest`, `LoadImageManifest`.
- The fixed artifact name `recompiled-artifact` used by the engine. A later change can make the name part of the build key.

## 8. Recommendation

1. Add the build key, source hash, and binary hash now, computed by the workflow in 7.2, in a sidecar `build-manifest.json` (schema 1). Do not change generated C.
2. Record codegen options and the generator version in the build key. This closes the largest gap (2.4).
3. Capture the compiler `--version` and target at build time, through the same probe `DoctorCommand` already runs. Do not record local paths in the manifest.
4. Promise tier 1 only (Section 4.2). Run the tier-2 experiment (4.3) before any binary-identity claim.
5. Keep the existing Build API. Add the workflow above it. Migrate `RecompileCommand` first.
6. Keep history local-only and wait for #42 to decide its location.

This sequence respects the issue's priority: it does not block the vertical slice, and it does not change the artifact bytes that the existing tests depend on.

## 9. Open questions

1. **Owner decision:** is binary byte-identity a goal? If it is, the tier-2 experiment in 4.3 is required before implementation is scoped.
2. **Owner decision:** should `guardStats` and `traceUnits` builds be refused for release packaging, or only be recorded in the manifest? Today `package` can set `guardStats` through an environment variable (`GuardStatsScope.cs:34`).
3. **Compiler pinning:** accept any `gcc` and record its identity, or require a specific version? CI installs no compiler (2.7). UNVERIFIED which gcc the runners have.
4. **Generator version:** a hand-bumped constant, or a hash of the generator source? A hash changes on every commit and invalidates all keys. A constant depends on discipline.
5. **Embedded or sidecar:** this design uses a sidecar. Confirm that a reviewer does not need the key inside the binary.
6. **Analyzer and decoder version:** where is it, if it exists? Not located. UNVERIFIED.
7. **Runtime host reproducibility:** does `dotnet publish -c Release` with the default settings produce a stable output? UNVERIFIED.
8. **Overwrite policy:** should a build into a non-empty output directory fail, replace, or require an explicit flag? Today it replaces silently (2.2).
9. **Game build `.o` retention:** the object is kept for game builds (`GeneratedHostBuildService.cs:111-115`), and only firmware removes it. Is the retention intended?
10. **Compiler version in diagnostics:** `ExecutionEnvironmentCollector.cs:28` always writes `compilerVersion: null`. Should it be filled from the same probe?
11. **History location:** per output directory, or per project under #42?
12. **Cleanup in source:** `src/PSXRecomp.Cli/RecompileCommand.cs:28-31` has a `<summary>` for "The standalone artifact binary name." with no member under it. It looks like a leftover. Not part of this design; noted for whoever edits that file.
