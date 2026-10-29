# Issue #43: Incremental Analysis and Cache (design)

- Status: Proposed (design only; no implementation in this change)
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r4. Final fix cycle for the r3 review of `684b573`: closure made
  operational (F1), golden verification made non-vacuous (F2), and the stale-entry
  test made reachable with a named injection seam (F3). The minor items of that
  review were not included in the handoff text, so they are not addressed here
  (see section 11).
- Scope: a design for the analysis cache. It does not change the decoder,
  the analyzer, the artifact format, or any public API.

## Labels

Every claim carries one of these labels:

- **CONFIRMED**: read directly in the source at the cited `file:line`, or
  reproduced by a grep or count run during this design pass.
- **OBSERVED**: taken from a search result, a fetched page, or tool output, not
  from a line read. Absence claims are OBSERVED.
- **PREDICTED**: a consequence of the design that no test has exercised yet.
- **UNVERIFIED**: not checked. Each one is listed in section 9.

Paths are repository-relative. No BIOS, ROM, CHD content, or user file paths
appear in this document.

## 1. Current state

### 1.1 What exists today

- **CONFIRMED** No persistent analysis cache exists. The analysis flow is a pure
  function over caller-supplied bytes: "It is a pure domain component: all bytes
  are supplied by the caller and no artifact is written here"
  (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:23-24`).
- **CONFIRMED** The only caches are lifetime-scoped and not persisted:
  - CHD hunk cache, an in-memory dictionary per reader
    (`src/PSXRecomp.Core/DiscImage/ChdReader.cs:85`, `:97`, `:187-193`).
  - Coverage probe cache, keyed by raw instruction word, per `Analyze` call
    (`src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:98`, `:237-259`).
- **OBSERVED** No AI analysis result producer exists in `src/`.
- **CONFIRMED** The evidence taxonomy already contains an AI label:
  `AIPrediction = 6` (`src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`).
  Collision risk is handled in 8.1.
- **CONFIRMED** Both CLI and GUI call the same pipeline entry points:
  - CLI: `src/PSXRecomp.Cli/CliInput.cs:100-116` (`LoadChdWithIdentity`).
  - GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`,
    `:117`. The GUI caller of `discImageSha256` was not read (U10).
  Neither front end persists artifacts. Persistence lives in the test-project
  flow driver (2.2).

### 1.2 The YAML instruction definition

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml` declares
  `meta.version: "1.0"` at line 11 and describes itself as the "Machine-readable
  SSOT for Decoder, Interpreter, Recompiler, Test Generator, Debugger, MCP"
  (line 2). Its governing decisions are `docs/adr/001-cpu-specification-ssot.md`
  and `docs/adr/002-instruction-definition-yaml.md`.
- **OBSERVED** No code parses that file at runtime or build time. A search of
  `*.cs`, `*.csproj`, `*.props`, `*.targets`, `*.ps1`, `*.sh`, and `*.py` for
  `yaml`, `YamlDotNet`, `r3000a-instructions`, or `instructions.yaml` finds only
  the CI workflow-lint script and documentation references.
- **CONFIRMED** The decoder is hand-written: `R3000aDecoder` is a static class of
  opcode-field constants (`src/PSXRecomp.Core/Cpu/R3000aDecoder.cs:6-8`,
  `:9-40`), and `Decode(uint)` is at `:70`.

**Consequence:** a YAML edit cannot change analysis output today (rule Y1, 4.3).

### 1.3 Versioning gap

- **CONFIRMED** Artifact shape is versioned per document:
  `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`,
  `CoverageSchemaVersion = 1`, and `FunctionProvenanceSchemaVersion = 1`
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-49`).
- **CONFIRMED** No constant identifies the analysis engine's behavior.
  `ArtifactFixtureIdentity` carries only the disc, executable, and format
  identity (`ArtifactFixtureIdentity.cs:15-40`).
- **PREDICTED** A schema version does not detect a change in analysis logic. The
  per-stage fingerprint sets in 4.2 are the correctness input instead.

### 1.4 Project metadata

- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
- **UNVERIFIED** That no project field feeds analysis (U1).

## 2. Dependency graph (producer chain)

### 2.1 Stage chain in `RomAnalysisPipeline`

`RunFromChd` and `RunFromIsoImage` converge on `RunFromIsoSectorReader`. Cache
units are N7 through N12a. N14 and N15 are recomputed on every run and have no key.

| # | Node | Inputs | Evidence |
|---|---|---|---|
| N1 | Input identity | image bytes or stream, `discImageSha256` | Supplied hash is only checked for non-emptiness: `RomAnalysisPipeline.cs:93-98` (stream), `:194-198` (bytes). The test driver computes its own hash at `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:100-106`. The pipeline must verify it (8.3). |
| N2 | CHD open | N1 | `RomAnalysisPipeline.cs:115-117` (`ChdReader.Open`) |
| N3 | ISO filesystem | N2 or raw ISO | `RomAnalysisPipeline.cs:128-129`, `:217`; `DiscImageAnalyzer.CreateIsoReader` at `:128` |
| N4 | SYSTEM.CNF and boot executable | N3 | executable hash at `:259` (`ComputeSha256`, `:576-579`) |
| N5 | PS-X EXE header and entry point | N4 | `RomAnalysisPipeline.cs:277-337`; `PsxExe.cs`, `PsxExeHeader.cs` |
| N6 | Text region bounds | N5 | `RomAnalysisPipeline.cs:340-347` (checks only that `TextSegment` is non-empty) |
| N7 | Decode (linear sweep) | N6, `textEnd`; `count` is not part of the sweep (5) | `RomAnalysisPipeline.cs:351-355`; loop `:507-541`; `R3000aDecoder.Decode` at `:516`; formatter at `:522-523` |
| N8 | Basic blocks and CFG | N7 decoded list, sliced to `count` | `RomAnalysisPipeline.cs:381`; `BasicBlockBuilder.cs:20` takes `instructionCount` and never reads it (CONFIRMED: the name appears only at `:20`); re-decode at `:30` |
| N9 | Function discovery | N7 decoded list, N8 blocks and edges | `RomAnalysisPipeline.cs:404`; `FunctionDiscovery.Build` at `FunctionDiscovery.cs:81-88` takes no count; re-decode at `:96` |
| N10 | BIOS call recognition | N7, N8, N9 | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, decode at `:176` and delay-slot decode at `:195`; imports `Runtime` for `BiosCallNames` (`:3`, `:584`) |
| N11 | Report assembly | N7-N10 | `RomAnalysisPipeline.cs:400-444`; call-candidate re-decodes at `:553` |
| N12a | Recompilation classification | N11 report, lowering validator | `RealRomCoverageAnalyzer.cs:88` (`Analyze`); `Classify` and `ProbeLowerable` at `:223` and `:234-261`; `MipsToIrLowerer.LowerProgram` at `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176`, called at `RealRomCoverageAnalyzer.cs:251` |
| N12d | Differential section | caller-supplied validations | `RealRomCoverageAnalyzer.cs:85-88` (doc), `:185`, `:377-382` |
| N14 | Artifact documents | N11, N12a, N12d | `RealRomAnalyzer.cs:178-181`; `DeterministicArtifactBuilder.cs:74`, `:86-89`, `:101-106`, coverage at `:115-121`, manifest at `:123-127` |
| N15 | Persistence | N14 | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50` |

**Edge summary**

- N1 to N7 is a strict chain. Each cached key includes its upstream key (3.2), so
  an upstream change reaches every stage below through the chain.
- N7 is the fan-out point. N8, N9, and N10 consume the decoded list.
- N12a consumes every decoded instruction through `Classify` (`:223`), so the
  lowering validator is an input to the classification.
- N12d is an input to document assembly only. It is never cached (4.7).
- N14 is recomputed every run, so a change to its code changes output bytes but
  never a cached payload.
- `RealRomAnalysisArtifacts.Coverage` is nullable
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomAnalysisArtifacts.cs:24`).
- `AnalysisManifestDocument.cs:10-12` names three documents and is stale: the
  builder also adds `coverage.json` (`DeterministicArtifactBuilder.cs:117-121`).

### 2.2 Who drives persistence and the completion markers

- **CONFIRMED** Persistence is in the test-project driver
  (`src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs`). The driver calls the
  pipeline, then coverage, then `DeterministicArtifactBuilder.Build`, then the
  writer. The cache must move or mirror this logic into a production component (8.4, U3).
- **CONFIRMED** The `Manifest` and `Complete` stages are recorded by that driver:
  `recorder.Pass(RomAnalysisStage.Manifest, ...)` at `RealRomAnalyzer.cs:261` and
  `recorder.Pass(RomAnalysisStage.Complete, ...)` at `:262`. The stage enum notes
  that the pipeline does not produce them (`src/PSXRecomp.Core/DiscImage/RomAnalysisStage.cs:10`).
- **Proposal, not an existing convention.** An on-disk `COMPLETE` file marker is
  new. It is modeled on the stage record the driver already writes.

### 2.3 Dependency graph (summary)

```
N1 identity --> N2 CHD --> N3 ISO --> N4 boot exe --> N5 EXE --> N6 text bounds
  --> N7 decode  [sets: extract + decode]
        |--> N8 basic blocks  [set: analysis]
        |--> N9 functions     [set: analysis]
        |        \--> N10 BIOS calls  [set: analysis; also N8]
        '--> N11 report
               --> N12a classification [set: coverage, includes lowering]
               --> N12d differential   [caller validations; never cached]
               --> N14 documents --> N15 persist   [recomputed each run]
```

## 3. Cache key composition

### 3.1 Principle

A key identifies the exact content of a computation: all of its inputs and the
analyzer code that produced it. Keys contain no filesystem path
(`ArtifactFixtureIdentity.cs:11-12`).

### 3.2 Fingerprint sets and the key

The correctness input is a set of per-stage fingerprints (4.2):

- `setExtract`: code that turns the image into executable bytes and bounds (N2 to N6),
  and the stage and outcome types that gate persistence (W5). Carried by every key.
- `setDecode`: `setExtract` plus the decoder and formatter (N7). Carried by N7 and,
  through the chain, by every key below it.
- `setAnalysis`: `setDecode` plus basic blocks, functions, BIOS recognition, and
  the analysis contract types (N8 through N11). Carried by N8 through N12a.
- `setCoverage`: `setAnalysis` plus the classification and lowering validator
  (N12a). Carried by N12a only.
- `dependencyFingerprint`: build inputs that can change compiled behavior (4.2).
  Carried by every key.

**Key encoding.** A stage key is the SHA-256 of the canonical text produced by the
key encoder (3.6). `ArtifactJson` is not used: its options
(`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:34-38`) keep
declaration order (`ArtifactJson.cs:23`).

| Field | Source | Carried by | Notes |
|---|---|---|---|
| `keyFormat` | cache constant | all cache units | Bumped when the key layout changes |
| `stage` | stage id | all cache units | `N7`, `N8`, `N9`, `N10`, `N11`, `N12a` |
| `setExtractFingerprint` | 4.2 | all | |
| `setDecodeFingerprint` | 4.2 | N7 and below | |
| `setAnalysisFingerprint` | 4.2 | N8 through N12a | |
| `setCoverageFingerprint` | 4.2 | N12a | Includes lowering |
| `dependencyFingerprint` | 4.2 | all | |
| `engineRevision` | manual label | none | Not a key field |
| `schemaVersions` | `AnalysisArtifactSchema.*SchemaVersion`, `FunctionProvenanceSchemaVersion` | all | Existing constants (1.3) |
| `discImageSha256` | N1, verified (8.3) | all | `ArtifactFixtureIdentity.cs:23-24` |
| `executableSha256`, `executableSizeBytes` | N4 | all | `ArtifactFixtureIdentity.cs:35-39` |
| `entryPoint`, `textEnd` | N5, N6 | N7 and below | Decode bounds |
| `instructionCount` | caller | N8 through N12a | Conservative: N8 does not read it (`BasicBlockBuilder.cs:20`) and N9 does not take it. Its effective input is the sliced decoded-list length |
| `inputContainer` | N2 vs raw ISO | all | `"CHD"` or `"ISO"` (3.4) |
| `projectFields` | 3.3 | per classification | Analysis-relevant project fields only; empty today |
| `upstreamKeys` | prior cache-unit keys | N8 and below | Merkle chain. N7 has none (N6 is not a cache unit) |

### 3.3 Project fields

- **PREDICTED** Project metadata affects analysis only through fields read by N1 to
  N14. Each such field must be listed in `projectFields`.
- **UNVERIFIED** The full field set (U1).

### 3.4 Container format

- **CONFIRMED** The pipeline records a skip when there is no container
  (`RomAnalysisPipeline.cs:163`). The hash is of the file bytes
  (`RealRomAnalyzer.cs:102-104`), so a raw ISO and a CHD of identical content have
  different `discImageSha256` values.
- **PREDICTED** Such a pair misses today. `inputContainer` makes the separation explicit.

### 3.5 Example keys (format only)

```
stage=N7 decode
setExtractFingerprint=<hex>  setDecodeFingerprint=<hex>
dependencyFingerprint=<hex>
schemaVersions={report:2,instructions:1,cfg:1,...}
discImageSha256=<hex>  executableSha256=<hex>
entryPoint=<u32>  textEnd=<u32>
inputContainer=CHD
upstreamKeys={}

stage=N8 basic blocks
setExtractFingerprint=<hex>  setDecodeFingerprint=<hex>  setAnalysisFingerprint=<hex>
dependencyFingerprint=<hex>
instructionCount=<int>
upstreamKeys={N7:<hex>}
```

### 3.6 Key encoder specification

The encoder output is exactly the following, with no added whitespace:

- **Encoding.** UTF-8, no byte-order mark, LF only, one line, no trailing newline.
- **Objects.** `{`, members separated by `,`, `}`. Each member is `"name":value`.
  There is no space after the colon.
- **Member order.** Members are sorted by the UTF-8 bytes of the member name,
  compared byte by byte, shorter prefix first. Declaration order is never used.
- **Arrays.** `[`, elements separated by `,`, `]`, in the given order.
- **Strings.** `"`-delimited. `"` is written `\"`, `\` is written `\\`. Each
  control character U+0000 to U+001F is written `\u00xx` with lowercase hex,
  including those with short forms (`\n` is written `\u000a`). Every other
  character is written as its UTF-8 bytes, unescaped.
- **Integers.** Base 10, no leading `+`, no leading zeros, `-` only when negative.
- **Booleans and null.** `true`, `false`, `null`.
- **No floats.** An encoder given a float fails.
- **Golden value.** T1 asserts the exact text of a fixed sample and its SHA-256.

## 4. Invalidation rules

Each rule names the cache units whose keys change. "Chain" means the later key
includes the upstream key, so the change also reaches every stage below.

### 4.1 Decoder and formatter change

- **CONFIRMED** The decoder maps raw words to mnemonic, format, control flow, and
  delay-slot kind (`R3000aDecoder.cs:70`). Its consumers are
  `RomAnalysisPipeline.cs:516` (N7), `RomAnalysisPipeline.cs:553` (N11),
  `BasicBlockBuilder.cs:30` (N8), `FunctionDiscovery.cs:96` (N9),
  `BiosCallRecognition.cs:176` and `:195` (N10).
- **CONFIRMED** Text output depends on `MipsInstructionFormatter`:
  `RomAnalysisPipeline.cs:522-523` copies `FormatMnemonic` and `FormatOperands`
  into the decoded record. The register table is at
  `src/PSXRecomp.Core/DiscImage/MipsInstructionFormatter.cs:12-17`.
- **Rule D1.** A change to any file in `setDecode` changes `setDecodeFingerprint`.
  Invalidated: N7, and by chain N8 through N12a.
- **Rule D1a.** The behavioral guard tuple includes the formatter output
  (`mnemonic`, `operands`).
- **Behavioral guard (PREDICTED).** A fingerprint over a fixed corpus of words is a
  secondary check for a decoder output change without a source change. It is not
  the correctness input.

### 4.2 Per-stage fingerprint sets and closure

**Set membership.** Every entry is a repository-relative path. Membership is a
committed literal list in the generator and in T13. No glob defines membership.

- **`setExtract`** (19 files, N2 to N6, and persistence gating): the readers and
  parsers, plus the pipeline's own control flow, plus the stage and outcome types.
  - `src/PSXRecomp.Core/DiscImage/`: `ChdReader.cs`, `ChdHeader.cs`,
    `ChdMapEntry.cs`, `ChdBitstream.cs`, `ChdHuffmanDecoder.cs`, `ChdCdCodec.cs`,
    `ChdCdSectorSource.cs`, `Iso9660Reader.cs`, `Iso9660DirectoryEntry.cs`,
    `SystemCnfParser.cs`, `PsxExe.cs`, `PsxExeHeader.cs`, `DiscImageAnalyzer.cs`,
    `RomAnalysisPipeline.cs`.
  - F1 addition, one per listed file: `RomAnalysisStage.cs`,
    `RomAnalysisStageResult.cs`, `RomAnalysisStageRecorder.cs`,
    `RomAnalysisOutcome.cs`, `RomAnalysisStatus.cs`. The stage identifiers form
    part of cache-unit stage ids and the pass/fail gate of W5, so a change to them
    is in scope.
- **`setDecode`** (`setExtract` plus 17 files): all 16 files in
  `src/PSXRecomp.Core/Cpu/`, and `src/PSXRecomp.Core/DiscImage/MipsInstructionFormatter.cs`.
- **`setAnalysis`** (`setDecode` plus 26 files): `DiscImage/BasicBlock.cs`,
  `DiscImage/BasicBlockBuilder.cs`, `DiscImage/FunctionDiscovery.cs`,
  `DiscImage/BiosCallRecognition.cs`, `DiscImage/DecodedInstruction.cs`,
  `DiscImage/DecodeFailure.cs`, `DiscImage/DiscImageAnalysisReport.cs`,
  `Runtime/BiosCallNames.cs` (the only `Runtime` file referenced from the analysis
  side; `BiosCallRecognition.cs:584`), and all 18 files in
  `src/PSXRecomp.Core/Analysis/Contracts/` (the contract types the report and CFG use).
- **`setCoverage`** (`setAnalysis` plus 6 files): `Recompiler/RealRomCoverageAnalyzer.cs`,
  `Recompiler/MipsToIrLowerer.cs`, `Recompiler/MipsToIrLoweringResult.cs`,
  `Recompiler/RecompilerContract.cs` (it defines `RecompilerIrDiagnosticCode` at
  `:273` and `RecompilerIrProgram` at `:610`, which the lowerer returns),
  `DiscImage/AnalysisArtifacts/RealRomCoverageDocument.cs` (it defines the
  classification enum), and `DiscImage/AnalysisArtifacts/ArtifactFixtureIdentity.cs`
  (the classification payload embeds the fixture identity; F1 addition).
- **`dependencyFingerprint`** (not counted in the 236 Core files):
  - `src/PSXRecomp.Core/PSXRecomp.Core.csproj` and `Directory.Build.props` (repository root).
  - `src/Architecture/PSXRecompArchitectureAttributes.cs`. Coverage of this file
    is stated in 4.2.1.
  - The resolved packages and the SDK version (4.2.2).

#### 4.2.1 How `src/Architecture` is covered

- **CONFIRMED** `Directory.Build.props:22-24` adds
  `src/Architecture/PSXRecompArchitectureAttributes.cs` to every project whose
  `CompileArchitectureAttributes` property is `true`, linked as
  `Architecture/PSXRecompArchitectureAttributes.cs`. `PSXRecomp.Core.csproj:7`
  sets that property.
- **CONFIRMED** The file is 29 lines and declares only internal attribute classes
  (`DomainAttribute`, `ApplicationAttribute`, `InfrastructureAttribute`,
  `TestAttribute`, `GeneratedAttribute`). It has no code that runs in analysis.
- **Decision DEC-ARCH.** The file is in `dependencyFingerprint`, not in a
  per-stage set. It is a compiled input to Core, so a change to it changes the
  assembly, but it has no effect on analysis output. Including it in the
  dependency set is the conservative choice: a change invalidates the whole
  cache, which is acceptable for a file this rarely edited.

#### 4.2.2 Dependency inputs not in source

- **Resolved packages.** The build reads the resolved `(packageId, resolvedVersion)`
  pairs for `PSXRecomp.Core` from `obj/project.assets.json` at build time, sorted.
  The csproj pins only direct packages: `SharpCompress 0.50.4`
  (`PSXRecomp.Core.csproj:25`) and `loach.PureSharp 0.1.6` (`:24`). No lock file
  exists (CONFIRMED). SharpCompress is used by `ChdCdCodec.cs:3`, and a version
  change can change decoded content under an unchanged image hash, so its version
  is a key input.
- **SDK and target.** The .NET SDK version and `TargetFramework` `net10.0`
  (`PSXRecomp.Core.csproj:3`).

#### 4.2.3 Hash input format

For a set, the fingerprint is the SHA-256 of the following byte stream. Members
are sorted by the UTF-8 bytes of their path. For each member, append:

1. the UTF-8 bytes of the repository-relative path, forward slashes, no leading slash;
2. `0x0A`;
3. the decimal byte length of the normalized content, no leading zeros;
4. `0x0A`;
5. the normalized content bytes;
6. `0x0A`.

Normalized content is the file's bytes with each CRLF pair replaced by LF. A lone
CR is kept. A BOM, if present, is kept.

**Decision DEC-LF1 (line endings).** Hash LF-normalized bytes, so that a checkout
with different line-ending settings does not produce a different fingerprint for
the same source. The hazard is observed on this machine: git reported "LF will be
replaced by CRLF" for the design file during commit `6d038a0` (OBSERVED). The
known exception is a multi-line verbatim string literal, whose value depends on
its line endings. Lint test T20 rejects such literals in any set file.

#### 4.2.4 Closure

**Partition.** The closure test enumerates the tracked `.cs` files under
`src/PSXRecomp.Core/`. It enumerates the git index (`git ls-files`), not the disk, so
`obj/` and `bin/` are excluded by construction and by the glob (`obj/` and `bin/`
are ignored by `.gitignore:4-5`). Every file must belong to exactly one owner:

| Owner | Files | Basis |
|---|---|---|
| `setExtract` (E) | 19 | 4.2 |
| `setDecode`-only (D) | 17 | 4.2 |
| `setAnalysis`-only (A) | 26 | 4.2 |
| `setCoverage`-only (C) | 6 | 4.2 |
| NonAnalysis (N) | 168 | 4.2.5 |
| **Total** | **236** | matches the 236 tracked files at base `2e3ecad` (CONFIRMED by count) |

T21 fails when a file is in no owner, when a file is in two owners, when a
listed file is missing from the index, or when the total is not 236. A new Core
file therefore fails the build until it is assigned.

**What closure does and does not prove.** Closure is a classification
guarantee: no file is silently outside every set. Reachability is a separate claim:
that no N-file is called or referenced from an E, D, A, or C file. The greps run
in this pass were:

- `using PSXRecomp.Core.(Runtime|Dma|MemoryCard|Execution|Project|TitleIdentity|Diagnostics)`
  across the E, D, A, and C files: two hits, `BiosCallRecognition.cs:3` (Runtime,
  covered by `BiosCallNames.cs` in A) and `RomAnalysisOutcome.cs:2` (Diagnostics,
  see 4.2.5).
- Names of the Recompiler, root, and native files (`IndirectRootDiscovery`,
  `ReachableProgramBuilder`, `RecompilerIrMemoryEffectClassifier`, `LoadedCodeTable`,
  `ObservedRootPlanner`, `GeneratedHostBuild`, `PSXCoreWrapper`, `NativeInterop`,
  `IFileIdentityProbe`, `ProcessTreeInterop`) across the E, D, A, and C files: no hit.
- `RecompilerIrMemoryEffectClassifier` has no reference in `src/PSXRecomp.Core`
  other than its own file (CONFIRMED by grep).

A reference from the N side, found by a Roslyn analysis, is what U14 must still
rule out (UNVERIFIED). Until U14 runs, reachability rests on these greps.

#### 4.2.5 NonAnalysis entries, by prefix, with reasons

Each entry is a directory prefix, or a single file where noted. Counts are from
the classification pass.

| Prefix | Files | Reason |
|---|---|---|
| `Runtime/` (excluding `Runtime/BiosCallNames.cs`, which is in A) | 68 | Runtime device model, BIOS HLE execution, GPU, SPU, CD-ROM, MDEC, and controller input. The analysis side references only `BiosCallNames` (4.2.4). |
| `Diagnostics/` | 19 | Diagnostic model and reporting. `RomAnalysisOutcome.cs` (E) has a `Diagnostic` property attached by the caller (`RomAnalysisOutcome.cs:24`). That attached value is not part of any cache payload. |
| `Recompiler/` (excluding the four C files) | 17 | Host code generation, interpreter and executor bridges, loaded-code tables, root discovery, and generated-host build. No reference from E, D, A, or C files (4.2.4). |
| `Execution/` | 15 | Title-execution orchestration, engines, and firmware boot monitoring. |
| `Dma/` | 14 | Memory bus and DMA adapters. |
| `TitleIdentity/` | 7 | Title identity model. Used by `Runtime/Gpu/FrameSnapshot.cs`, not by the analysis side. |
| `DiscImage/AnalysisArtifacts/` (excluding `RealRomCoverageDocument.cs` and `ArtifactFixtureIdentity.cs`, which are in C) | 10 | Document serialization for N14 and N15. Cached payloads use the key encoder, not `ArtifactJson`. |
| `Project/` | 4 | Project metadata. Analysis-relevant fields are governed by rule P1 and `projectFields`. |
| `MemoryCard/` | 3 | Memory-card storage model. |
| `DiscImage/ChdMapStatistics.cs`, `DiscImage/IsoVolumeStatistics.cs` | 2 | Statistics computed for N14 (`DeterministicArtifactBuilder.cs:27-28`). The computation reads the reader's data but produces no cache payload. |
| Root of `src/PSXRecomp.Core/` (`FileIdentityInterop.cs`, `IFileIdentityProbe.cs`, `LinuxFileStatInterop.cs`, `MacOsFileStatInterop.cs`, `NativeInterop.cs`, `NativeLibraryResolver.cs`, `PSXCoreWrapper.cs`, `PosixStatResult.cs`, `ProcessTreeInterop.cs`) | 9 | Native interop and host file and process probing. Not referenced from the analysis side (4.2.4). |
| **Total** | **168** | |

#### 4.2.6 Generation and closure of the build targets

A build target computes each fingerprint from its committed list and emits the
results as a generated constant in `obj/`. The generated constant is not an input
(it is not in any list). The target runs before compilation (U12 checks it against
the native build and CI).

### 4.3 YAML change

- **CONFIRMED** The YAML has no runtime or build-time reader (1.2).
- **Rule Y1 (current-state rule).** While no code reads the YAML, it is in no key and no
  set. A comment-only edit must not invalidate the cache. **Revisit this rule when a
  generator or runtime reader of `config/cpu/r3000a-instructions.yaml` lands.**
  The triggering decision is `docs/adr/002-instruction-definition-yaml.md`. The
  SSOT decision is `docs/adr/001-cpu-specification-ssot.md`.
- **Rule Y2.** When a generator lands, its generated file is listed in `setDecode`.
  The YAML itself is not hashed raw, because whitespace and comments would
  over-invalidate.
- **UNVERIFIED** Whether any generator exists outside the searched file types (U2).

### 4.4 Project change

- **Rule P1.** A project edit invalidates only cache units whose `projectFields`
  changed. Today that set is empty.
- **Rule P2.** `ProjectFormatVersion` (`ProjectSchema.cs:19`) enters the key only if
  the project format changes what the analysis reads.

### 4.5 Input change

- **Rule I1.** A changed disc image or executable changes N1 or N4 and therefore
  every stage below. This is a miss by construction.

### 4.6 Lowering change (`setCoverage` minus `setAnalysis`)

- **CONFIRMED** `Classify` calls `ProbeLowerable` for each instruction that reaches
  that branch (`RealRomCoverageAnalyzer.cs:223`), and `ProbeLowerable` calls
  `MipsToIrLowerer.LowerProgram` (`:251`). The class of each decoded instruction is
  therefore a function of the lowering validator.
- **Rule L1.** A change to a file in `setCoverage` but not in `setAnalysis` changes
  `setCoverageFingerprint`. Those files are `RealRomCoverageAnalyzer.cs`,
  `MipsToIrLowerer.cs`, `MipsToIrLoweringResult.cs`, `RecompilerContract.cs`,
  `RealRomCoverageDocument.cs`, and `ArtifactFixtureIdentity.cs`. Invalidated: **N12a
  only**. N7 through N11 keep their hits, because their keys do not carry
  `setCoverageFingerprint`. N14 and N15 are recomputed.
- **Rule L1a.** A lowering change that alters `Classify` but lies in a file outside
  `setCoverage` is a closure defect. T21 and T16 detect it.

### 4.7 Differential-section input (N12d)

- **Rule C1.** The differential section is built from validations that actually
  executed (`RealRomCoverageAnalyzer.cs:85-88`). Its absence means "nothing was proven".
- **Rule C2.** N12d is never cached and is in no key. It is assembled at N14 from the
  validations supplied on that run.
- **Follow-up refactor.** To cache N12a alone, `Analyze` must separate classification
  from `Differential = BuildDifferential(validations)` (`RealRomCoverageAnalyzer.cs:185`,
  `:377-382`). Until then, N12a is cached as the document built with no validations,
  and N14 supplies the differential. That needs `BuildDifferential` to be reachable
  from N14 (U18).
- **Test.** T15 compares warm and cold runs with the differential on and off.

## 5. Partial re-analysis units and cost

### 5.1 Decode sweep (N7)

- **CONFIRMED** The current loop is bounded by `count` and by `textEnd`
  (`RomAnalysisPipeline.cs:507-511`). `maxAddress` is `exe.Header.TextEnd` (`:507`).
  The loop body reads `GetInstructionWord` inside a `try` (`:513-539`).
- **Decision DEC-N7 (cost).** A cold N7 run decodes the whole text region up to
  `textEnd`, not only the first `count` words. The cost is linear in text size and
  is not measured (U5). Each later request is served as a prefix with no re-decode.
- **Prefix rule.** An N7 entry with `sweepEnd = textEnd` serves any `count`. For a
  requested `M`, the consumer takes the first `min(M, total)` instructions.
- **Failure rule.** A decode failure at address `A` is included in a count-limited
  result iff `A < entry + 4*M`. The loop stops after `M` successful decodes
  (`:511`), so a failure at or beyond `entry + 4*M` is never reached by a
  count-limited run.

### 5.2 Text shorter than the declared size

- **CONFIRMED** The text check at `RomAnalysisPipeline.cs:340-347` rejects only a
  zero-length `TextSegment`, not a segment shorter than `header.TextSize`.
- **CONFIRMED** `PsxExe.GetInstructionWord` throws `ArgumentOutOfRangeException`
  when `offset + 4 > TextSegment.Length` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:21-30`).
- **CONFIRMED** The decode loop catches that exception (`RomAnalysisPipeline.cs:531`),
  records a `DecodeFailure` ("Address outside text segment bounds"), and breaks
  (`:533-538`). No exception escapes the stage, and the decode is a partial sweep
  with one failure.
- **Test T17** pins this: a segment shorter than its declared size passes
  `MIPS_DECODE` with exactly one failure at the first address past the segment.

### 5.3 Other unit rules

- **Unit of reuse: the cache unit (N7, N8, N9, N10, N11, N12a).** A hit replaces that
  unit's computation. A miss recomputes it and, through the chain, every unit below.
- **No sub-stage reuse below N7.** Per-block or per-function reuse needs a dependency
  model and is out of scope for v1.
- **Coverage probe.** Probe results are keyed by raw word (`RealRomCoverageAnalyzer.cs:231-237`).
  N12a is the cache unit, and its key carries `setCoverageFingerprint`, so the probe
  cache inside a run is sound.

### 5.4 Cost summary

- Cold N7: linear in text size (DEC-N7), not measured (U5).
- Identity: a full-image SHA-256 on every run, plus a second read on the stream path
  (8.3). Not measured (U5).
- Warm run: N7 prefix slicing and key computation, plus payload reads. Not measured (U5).

## 6. Fail-closed handling of incomplete or corrupted cache

The rule is that an entry is used only if it is proven complete and internally
consistent. A miss is never an error, and a miss never yields output a cold run would not.

### 6.1 Entry layout (proposal)

Each cache unit is a directory with:

- `header`: format version, `key` (the key text from 3.6), `payloadSha256`,
  `payloadSizeBytes`, the set fingerprints in effect, and `schemaVersions`.
- `payload`: the stage output in the key encoder's canonical form.
- `COMPLETE`: a marker written last (proposal, 2.2).

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the entry directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, and only after W3 succeeds for every file.
- **W5.** Never write an entry for a unit whose stage did not pass (the stage records
  distinguish pass from fail).
- **W6.** A rename that fails because the target exists is success only if the existing
  header's `payloadSha256` matches. Otherwise the entry is quarantined (6.4) and the
  write is retried once.

### 6.3 Read protocol (fail closed) and residual risk

An entry is used only if all of the following hold. Any failure is a miss.

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses and its format version is supported.
- **R3.** `header.key` equals the key computed from the current inputs.
- **R4.** `payload` size equals `payloadSizeBytes`.
- **R5.** SHA-256 of `payload` equals `payloadSha256`.
- **R6.** The payload deserializes under the current schema versions.
- **R7.** Any exception during R1 through R6 is caught and treated as a miss.

**What R1 through R7 prove.** They prove that an entry is complete and internally
consistent with the key it claims. They do not prove that the payload is what the
current analyzer would compute. R3 detects a stale entry only when the set
fingerprints in the key have changed, so the key is the only protection against an
analyzer change.

**Residual risk (F3).** A stale-but-consistent entry is one whose key is current but
whose payload was produced by different analyzer code. That occurs when a
behavior change lies in a file that is missing from its set (a closure defect) or
when a set fingerprint is not updated. Normal mode serves such an entry as a hit.
**The entry is used until verify mode runs in CI** (4.2, the golden verification),
and then only if the golden corpus exercises the changed behavior. A defect that
affects only real-title inputs and not the synthetic corpus is not caught by
verification. This residual risk is accepted for v1 and is stated here, not hidden.

### 6.4 Quarantine and recovery

- **Q1.** An entry that fails R2 through R6 is renamed to a quarantine name that
  records the failing check, not a clock value.
- **Q2.** After a miss, the unit is recomputed and written by the write protocol.
- **Q3.** Dedicated codes: `ANALYSIS_CACHE_QUARANTINED` for Q1 and
  `ANALYSIS_CACHE_VERIFY_MISMATCH` for verify failures (6.5 below). They are not
  reused from `AnalyzerAnalysisFailed` or `AnalyzerReportFailed`
  (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`). Under
  `docs/adr/019-diagnostics-and-recovery-contract.md` a code is a validated
  `SCREAMING_SNAKE` identity, and registering the two codes is recommended. A
  quarantine is a warning on the run.

### 6.5 Verification and the injection seam

The seam is injected through two Core types, so tests need no filesystem:

- `IAnalysisCacheStore`: the store port (8.4). Tests pass an in-memory fake.
- `AnalysisCacheOptions`: a value with `Mode` (`Normal` or `Verify`), the store, and
  a cold-recompute delegate `Func<CacheUnit, ColdResult>`. The delegate is the
  pipeline's own stage code. Tests pass a delegate that returns a known payload.

- **Normal mode.** A consistent entry (R1 to R7 pass) is returned as a hit with no
  cold recompute. A key-consistent entry with a stale payload is served as a hit (the
  residual risk of 6.3).
- **Verify mode.** For each cache unit, the pipeline runs the cold recompute on the
  current inputs and compares its canonical payload with the stored payload
  byte for byte. A difference is `ANALYSIS_CACHE_VERIFY_MISMATCH` and a hard failure.
  Verify mode does not return a cache hit to the caller.

### 6.6 Input identity is never cached as truth

- **I2.** The disc image SHA-256 is computed on every run from the full file
  (`RealRomAnalyzer.cs:102-104`), and the pipeline verifies it (8.3). A size-and-mtime
  shortcut is rejected because it is not a content identity.

## 7. Deterministic test plan

Tests use synthetic inputs from `src/PSXRecomp.Tests/RealRomAnalysis/`:
`SyntheticDiscBuilder.cs`, `SyntheticChdBuilder.cs`, and `SyntheticIsoBothEndianTests.cs`
(CONFIRMED that these exist; their APIs are UNVERIFIED, U7). No BIOS, ROM, or CHD
content is used. Clocks are injected. Temp paths are not asserted.

| ID | Test | Asserts |
|---|---|---|
| T1 | Key encoder (3.6) | The encoded text of a fixed sample equals the committed text byte for byte, and its SHA-256 equals the committed value. Members are in UTF-8 byte order. Reordering record members in source leaves the text unchanged. Control characters are `\u00xx`. Floats are rejected |
| T2 | Key sensitivity | Changing `discImageSha256`, any set fingerprint, `dependencyFingerprint`, a schema version, or `inputContainer` changes exactly the keys that carry it (3.2). Changing `instructionCount` changes the N8, N9, N10, N11, and N12a keys and **does not** change the N7 key |
| T3 | Project non-analysis edit | Changing a project field outside `projectFields` leaves every key unchanged |
| T4 | Set scope (D1, A1) | A change to a `setDecode` file (`R3000aDecoder.cs`) changes N7 and, by chain, N8 through N12a. A change to a `setCoverage`-only file (`RealRomCoverageAnalyzer.cs`) changes N12a only. A change to a `setAnalysis`-only file (`BasicBlockBuilder.cs`) changes N8 through N12a and leaves N7 unchanged |
| T5 | YAML comment-only edit | No key changes (Y1) |
| T6 | Corruption matrix | Zero-length file, truncated payload, flipped byte, missing `COMPLETE`, wrong `key`, unsupported format version, malformed header, schema-version mismatch: each produces a miss, a quarantine with `ANALYSIS_CACHE_QUARANTINED`, and output byte-identical to a cold run |
| T7 | Partial failure | A pipeline failure at N8 writes no entry for N8 or later; the N7 entry is the only one written |
| T8 | Warm equals cold | For each synthetic image, the warm run's artifact bytes equal the cold run's, byte for byte |
| T9 | Stale-but-consistent entry (F3) | Injection: `AnalysisCacheOptions` with an in-memory `IAnalysisCacheStore` whose entry has a valid header and `COMPLETE` for the current key, and a payload different from the cold result. **Normal mode** returns that payload as a hit and performs no cold recompute. **Verify mode** reports `ANALYSIS_CACHE_VERIFY_MISMATCH` and returns no hit |
| T10 | Concurrent write | Two writers with the same key leave one complete entry. A fake filesystem that interleaves renames does not produce a torn entry |
| T11 | Shared store and identity | The CLI and GUI entry points, given the same bytes, compute the same verified `discImageSha256`, the same key, and read the same entry. A wrong supplied hash fails at the Input stage, with no cache write and no artifact |
| T12 | No path in key | No key or entry contains an absolute path (`PathRedactor.cs` under `src/PSXRecomp.Tests/RealRomAnalysis/`; API UNVERIFIED, U7) |
| T13 | Set membership | Each committed list equals its literal list exactly. Counts are E 19, D 17, A 26, C 6. No `obj/` or `bin/` path, no generated constant. The fingerprint of a fixed sample tree equals a committed golden value. Unchanged under CRLF to LF. Changed by a single content byte |
| T14 | Prefix reuse with failure filter (5.1) | An N7 entry computed without `count`, sliced to `M`, equals a cold decode at `M`, including failures filtered by `A < entry + 4*M` |
| T15 | Differential on and off | Warm and cold runs compared with validations supplied and without. Both match byte for byte. N12d is never read from the cache |
| T16 | Lowering scope (L1) | A change to `MipsToIrLowerer.cs` changes `setCoverageFingerprint`, invalidates N12a only, and leaves N7 through N11 hits |
| T17 | Short text segment (5.2) | A segment shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment; no exception escapes |
| T18 | Identity verification (8.3) | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind, with no analysis and no cache write |
| T19 | Dependency fingerprint | Changing a resolved `(packageId, version)` pair, the SDK version, or `src/Architecture/PSXRecompArchitectureAttributes.cs` changes `dependencyFingerprint` and every key |
| T20 | Multi-line verbatim literal lint (DEC-LF1) | Fails if any file in a committed set contains a multi-line verbatim string literal |
| T21 | Closure (F1, 4.2.4) | Enumerates tracked `.cs` files under `src/PSXRecomp.Core/` from the git index (not the disk glob, so `obj/` and `bin/` are excluded). Requires exactly one owner per file, owner counts E 19, D 17, A 26, C 6, N 168, total 236. Fails on an unassigned file, a double-assigned file, a missing listed file, or a different total |
| T22 | Golden verification (F2, 6.5 and 4.2) | See 4.2.7. Requires a minimum number of compared entries per cache unit and fails on payload mismatch |

Determinism rules for every test:

- No wall-clock values in keys, headers, or assertions.
- No dependence on the order of unsorted collections.
- No reliance on the machine's temp directory name.

### 4.2.7 Golden verification (F2)

**Golden corpus.** The corpus is committed under
`src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGolden/` (decision for U15, below).
It holds one file per (cache unit, synthetic fixture), named `<unit>/<fixtureId>.golden.json`,
with the fixed fields `fixtureId`, `unit`, `payloadSha256`, and `payload` (canonical text
from 3.6). Fixture ids are `synthetic-iso-le`, `synthetic-iso-be`, `synthetic-chd`,
and `synthetic-short-text`. The four are generated by the existing synthetic builders
(U7). The units are N7, N8, N9, N10, N11, and N12a.

**Verification.** The verify job, run on every change in CI:

1. For every (unit, fixture) pair, cold-recompute the payload from the synthetic
   inputs with the current source tree. This happens regardless of whether the key
   changed.
2. Compare the recomputed canonical payload with the golden `payload` byte for byte.
   Any difference fails the job. The key is not part of the comparison.
3. Also run the cache path: populate an in-memory store from the cold run, read it
   back in normal mode, and compare the hit with the golden payload.
4. **Minimum comparisons.** For each unit N7 through N12a, the number of compared
   pairs must be at least 4, equal to the number of fixtures. A unit with fewer
   comparisons fails the job. A check that compares zero entries therefore cannot pass.

**Intentional change.** A change that alters any payload fails the job unless the
same change regenerates the goldens with the named command
`regenerate-analysis-goldens` and commits the regenerated files. The reviewer
reads the golden diff in that PR. This gate is enforced by review, not by a
machine check. That is the residual risk of this design, recorded in 6.3.

**Generator.** `regenerate-analysis-goldens` is a test-project command. It is built
from the working tree's analyzer sources (all four sets and the dependency set) plus
the key encoder (3.6). For the initial corpus, the generator is built from the base
commit's analyzer sources plus the new encoder, so the first goldens are the base
analyzer's outputs in the new encoding. Later regenerations use the working tree.

**Synthetic-only rule.** Goldens are computed only from synthetic fixtures. They
contain no BIOS, ROM, or CHD content and no user path.

**Failure reporting.** The job reports the unit, fixture, and first differing byte
offset, and uses `ANALYSIS_CACHE_VERIFY_MISMATCH`.

## 8. Mapping to Issue #43 acceptance criteria

The issue's acceptance criteria (OBSERVED, from a fetched summary; unchecked):

1. Define dependencies between analysis artifacts. **Section 2.**
2. Define the cache key. **Section 3.**
3. Define invalidation rules for Analyzer, Decoder, Project, and YAML changes.
   **Section 4 (D1, A1, L1, P1, Y1, Y2).**
4. Define the unit of partial re-analysis. **Section 5.**
5. Define conditions for reusing AI results. **Section 8.1.**
6. Define recovery for a corrupted cache. **Section 6.**
7. Settle one cache shared by CLI and GUI. **Sections 8.2 and 8.3.**

### 8.1 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists.
- **CONFIRMED** `EvidenceType.AIPrediction = 6` already exists
  (`src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`), next to
  `AnalyzerFinding = 5` (`:16`).
- **Collision risk.** An AI result labeled `AIPrediction` could be read as evidence
  by any consumer that switches on `EvidenceType`. The rules below prevent it inside
  this design, but they do not stop an existing or future consumer. A consumer audit
  is U16.
- **Rule AI1.** An AI result is advisory. It is never stored as analysis output and
  never appears in an analysis document. It has its own entry type, outside the
  cache units.
- **Rule AI2.** An AI result is reused only when its key matches: the hashes of the
  artifacts it was computed from, the prompt or template revision, the model
  identifier, and the model settings.
- **Rule AI3.** An AI result is invalidated when any cache unit it depends on is
  invalidated, through its upstream hashes.
- **Rule AI4.** Whether a stored AI result uses `EvidenceType.AIPrediction` is a
  decision for its own ADR. The existing label must not be reused silently.

### 8.2 Shared CLI and GUI cache

- **CONFIRMED** Both front ends call the same pipeline (1.1).
- **Policy S1.** One store format and one key function. The port and key types are in
  `PSXRecomp.Core`, and the file store is in `PSXRecomp.Infrastructure` (8.4). The CLI
  and GUI pass only a store root.
- **Policy S2.** The store root is supplied by the host. Its location is U4.
- **Policy S3.** Cross-process concurrency uses the write protocol (W6). Readers never
  see an incomplete entry.

### 8.3 Identity verification

- **CONFIRMED** The CLI hashes the file in its own pass and then runs the pipeline on a
  second open of the same path (`src/PSXRecomp.Cli/CliInput.cs:100-116`). The two passes
  are not atomic.
- **CONFIRMED** The GUI accepts `discImageSha256` as a parameter and passes it on
  (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:110-117`).
- **CONFIRMED** The pipeline checks only that the supplied hash is non-empty
  (`RomAnalysisPipeline.cs:93-98`, `:194-198`).
- **Rule V1.** The pipeline computes SHA-256 over the stream or byte array it runs and
  compares it with the supplied value. A mismatch fails the Input stage with a
  dedicated failure kind, and no analysis runs. The stream path hashes from position
  0 to end, then seeks back to 0. The stream contract already requires seekability
  (`RomAnalysisPipeline.cs:70-72`). The Domain already uses SHA-256
  (`RomAnalysisPipeline.cs:2`, `:576-579`).
- **Rule V2.** The supplied hash becomes a claim that is checked, not an identity.
- **Migration.** V1 breaks callers that pass a constant. `DiscImageHostileSizeTests.cs:24`
  defines `Sha` as the literal `0123456789abcdef...` (64 hex characters). That is not
  the hash of the bytes it passes at `:347`, so V1 would fail it. About 50 call sites
  of `RunFromChd` and `RunFromIsoImage` exist in the test projects (9 in
  `DiscImageHostileSizeTests.cs`, 2 in `SyntheticChdReaderTests.cs`, 2 in
  `RealRomAnalyzer.cs`, 36 in `RomAnalysisPipelineTests.cs`, 1 in
  `RealRomProductionExecutionTests.cs`). How many use a constant is UNVERIFIED (U13).
- **Cost.** Verification reads the stream twice on the stream path (U5).

### 8.4 Architecture placement

The architecture matrix (`docs/architecture-matrix.md`) assigns:

- Domain (`PSXRecomp.Core`) owns ports and deterministic contracts
  (`architecture-matrix.md:22`, rule 1 at `:64`). It must not reference
  `PSXRecomp.Infrastructure`.
- Infrastructure (`PSXRecomp.Infrastructure`) owns managed host adapters, including
  `File` and `Directory` access (`architecture-matrix.md:24`, `:55`, rule 2 at `:65`).
- Application and CLI do not call `File.*` or `Directory.*` for ordinary logic (rule 3
  at `:66`). Wiring a concrete adapter is allowed only at one composition-root site
  with a narrowly scoped AARC002 suppression (`:47`).

Resolution:

- **Core:** `IAnalysisCacheStore`, the key and entry types, the key encoder (3.6), the
  lookup and validation logic (R1 to R7 as pure checks), the quarantine decision, and
  `AnalysisCacheOptions` (6.5). No file access.
- **Infrastructure:** `FileAnalysisCacheStore : IAnalysisCacheStore`, marked
  `[Infrastructure]`, with the temporary write, flush, rename, marker, and quarantine
  rename (6.2, 6.4).
- **Precedent.** `FileProjectMetadataStore` (`src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs`,
  `[Infrastructure]`, Issue #42) implements `IProjectMetadataStore`, which is defined in
  `PSXRecomp.Core.Project`.
- **Composition.** The CLI and GUI receive the port. The one composition-root site that
  constructs `FileAnalysisCacheStore` is the only Application-to-Infrastructure reference.
- **ADR required** before implementation: the new port and adapter, the move of
  persistence out of the test project (U3), and any use of an AI evidence label (Rule AI4).
  This document does not create one.

## 9. Open items and UNVERIFIED list

- **U1.** Enumerate every project field and classify it as analysis-relevant or not (3.3, P1).
- **U2.** Confirm that no build script or generator reads the YAML outside the searched types (4.3).
- **U3.** Decide where the production driver lives. Requires an ADR (8.4).
- **U4.** Decide the store root (8.2).
- **U5.** Benchmark cold N7 over the full text region (DEC-N7), the double read of V1, and the warm path (5.4).
- **U6.** Confirm the behavioral-guard corpus coverage (4.1).
- **U7.** Read `PathRedactor.cs` and the synthetic builder APIs before the tests are implemented.
- **U8.** Check whether `DeterministicArtifactTests.cs` already pins golden bytes.
- **U9.** Confirm that `setCoverage` (6 files) is closed under the lowering call graph. The
  greps in 4.2.4 do not establish that the lowerer reaches no other Recompiler file.
- **U10.** Read the caller of `AnalyzeAndExecuteFromDisc` for the GUI hash (8.3).
- **U11.** Confirm an identity-mismatch failure kind can be added without changing the mapping in `DiagnosticAdapter.cs`.
- **U12.** Verify the set-fingerprint build target under the native build and `.github/workflows/ci.yml`.
- **U13.** Migrate the test callers of `RunFromChd` and `RunFromIsoImage` to a helper that
  computes a real SHA-256 (8.3). Count the callers that pass a constant.
- **U14.** Roslyn reachability check: no N-file (4.2.5) is referenced from an E, D, A, or C file. Closure (T21) is a classification guarantee until this runs.
- **U15. Decided here (owner may override).** Location: `src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGolden/`. Format: one canonical JSON file per (unit, fixture), `<unit>/<fixtureId>.golden.json`, with the fields in 4.2.7. Content: synthetic fixtures only.
- **U16.** Audit consumers of `EvidenceType` for any that treat `AIPrediction` as evidence (8.1).
- **U17.** Confirm the dependency-input capture of `obj/project.assets.json` (4.2.2).
- **U18.** Plan the `Analyze` split of N12a and N12d (4.7).
- **U19.** `setAnalysis` membership of the Analysis contract types (4.2) is taken as the full
  18-file directory; U14 can narrow it, but narrowing is not required for correctness.
- **U20.** Check the Architecture attribute file's compile-time link (`Directory.Build.props:22-24`) against the architecture contract file (`src/architecture.contract.json`), which was not read.

## 10. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact schemas.
- No cache implementation. This document is the design only.
- No AI result producer.
- No change to the project file format.
- No stronger identity than full SHA-256 for inputs.
- No ADR is created here. ADRs are required before implementation (8.4).

## 11. Review status

- r3 review, F1 (closure): resolved in 4.2.4 and 4.2.5, with counts E 19, D 17, A 26, C 6,
  N 168 (total 236), and T21 scoped to the git index.
- r3 review, F2 (vacuous golden verify): resolved in 4.2.7 and 6.5. Key mismatch is no
  longer treated as "superseded". Every unit is cold-recomputed and compared byte for
  byte, with a minimum of 4 comparisons per unit, and intentional change requires the
  named command `regenerate-analysis-goldens`.
- r3 review, F3 (T9 unreachable): resolved in 6.3 (residual risk), 6.5 (seam), and T9.
- r3 review, minor items m1 to m9: not addressed. The handoff gave only the three major
  findings and not the text of m1 to m9, so they cannot be mapped to edits in this
  revision. They need to be re-sent.
