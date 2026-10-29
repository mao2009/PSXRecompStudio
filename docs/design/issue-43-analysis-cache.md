# Issue #43: Incremental Analysis and Cache (design)

- Status: Proposed (design only; no implementation in this change)
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r3. Addresses the second independent review of `c8299d4`
  (1 critical, 2 major, 9 minor). Every finding is resolved in the section it
  names, and the resolutions are stated as rules that can be tested.
- Scope: a design for the analysis cache. It does not change the decoder,
  the analyzer, the artifact format, or any public API.

## Labels

Every claim carries one of these labels:

- **CONFIRMED**: read directly in the source at the cited `file:line` during this
  design pass.
- **OBSERVED**: taken from a search result, a fetched page, or tool output, not
  from a line read. Absence claims ("no code matches") are OBSERVED.
- **PREDICTED**: a consequence of the design that no test has exercised yet.
- **UNVERIFIED**: not checked. Each one must be resolved before the
  implementation is accepted (section 9).

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
    Its doc comment states the key is sound because acceptance depends on the
    word and not on its address (`:231-232`).
- **OBSERVED** No AI analysis result producer exists in `src/`. A search for AI
  or LLM analysis result types returned no match.
- **CONFIRMED** The evidence taxonomy already contains an AI label:
  `AIPrediction = 6` (`src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`).
  This creates a naming collision risk, handled in 8.1.
- **CONFIRMED** Both CLI and GUI call the same pipeline entry points, so a shared
  cache has two callers to serve:
  - CLI: `src/PSXRecomp.Cli/CliInput.cs:100-116` (`LoadChdWithIdentity`: hashes
    the file in one open, then runs `RomAnalysisPipeline.RunFromChd` on a second
    open at `:115`).
  - GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`
    (`RunFromIsoImage`) and `:117` (`RunFromChd`). The GUI takes
    `discImageSha256` as a parameter (`:110-117`); who computes it on the GUI
    side was not read (UNVERIFIED, U10).
  Neither caller persists artifacts. Persistence lives in the test-project flow
  driver (see 2.2).

### 1.2 The YAML instruction definition

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml` declares
  `meta.version: "1.0"` at line 11 and is documented as the "Machine-readable SSOT
  for Decoder, Interpreter, Recompiler, Test Generator, Debugger, MCP" (line 2).
  Its governing decisions are `docs/adr/001-cpu-specification-ssot.md` and
  `docs/adr/002-instruction-definition-yaml.md`.
- **OBSERVED** No code parses that file at runtime or build time. A search of
  `*.cs`, `*.csproj`, `*.props`, `*.targets`, `*.ps1`, `*.sh`, and `*.py` for
  `yaml`, `YamlDotNet`, `r3000a-instructions`, or `instructions.yaml` finds only
  the CI workflow-lint script and documentation references.
- **CONFIRMED** The decoder is hand-written: `R3000aDecoder` is a static class of
  opcode-field constants (`src/PSXRecomp.Core/Cpu/R3000aDecoder.cs:6-8`,
  `:9-40`), and `Decode(uint)` is at `:70`.
- **CONFIRMED** Tests mention the YAML only in comments and names
  (`src/PSXRecomp.Tests/Cpu/R3000aOpcodeTests.cs:55`,
  `src/PSXRecomp.Tests/Cpu/R3000aDecoderGoldenTests.cs:373`).

**Consequence:** today a YAML edit cannot change analysis output. Including the
YAML in any key now would only cause over-invalidation (rule Y1, 4.3).

### 1.3 Versioning gap

- **CONFIRMED** Artifact shape is versioned per document:
  `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`,
  `CoverageSchemaVersion = 1`, and `FunctionProvenanceSchemaVersion = 1`
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-49`).
  The function-provenance document is optional and sits outside
  `manifest.json`'s `documents[]`. This list is complete for the artifact set.
- **CONFIRMED** The schema file says a consumer "must be able to tell a schema
  change from an analysis change" (`AnalysisArtifactSchema.cs:14-16`), but no
  constant identifies the analysis engine's behavior. `ArtifactFixtureIdentity`
  carries only the disc, executable, and format identity, with no analyzer or
  decoder revision (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactFixtureIdentity.cs:15-40`).
- **OBSERVED** A search for `analyzerRevision`, `AnalyzerVersion`, or
  `AnalyzerIdentity` in `src/**` returns no analysis-artifact match.
- **PREDICTED** A schema version does not detect a change in analysis logic. A
  manually maintained revision constant is also not a sufficient guard, because a
  missed bump yields stale entries that look valid. The per-stage fingerprint
  sets in 4.2 replace it as the correctness input.

### 1.4 Project metadata

- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
- **UNVERIFIED** That no project field feeds analysis. Only the schema constants
  were read (U1).

## 2. Dependency graph (producer chain)

### 2.1 Stage chain in `RomAnalysisPipeline`

`RunFromChd` and `RunFromIsoImage` converge on `RunFromIsoSectorReader`. The
chain below is in call order. Cache units are N7 through N12a. N14 and N15 are
recomputed on every run and have no cache key.

| # | Node | Inputs | Evidence |
|---|---|---|---|
| N1 | Input identity | image bytes or stream, `discImageSha256` | Supplied hash is only checked for non-emptiness: `RomAnalysisPipeline.cs:93-98` (stream), `:194-198` (bytes). The test driver computes its own hash at `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:100-106`. The pipeline must verify it (8.3). |
| N2 | CHD open | N1 | `RomAnalysisPipeline.cs:115-117` (`ChdReader.Open`) |
| N3 | ISO filesystem | N2 or raw ISO | `RomAnalysisPipeline.cs:128-129`, `:217`; `DiscImageAnalyzer.CreateIsoReader` at `:128` |
| N4 | SYSTEM.CNF and boot executable | N3 | boot path resolved in the stage block; executable hash at `:259` (`ComputeSha256`, `:576-579`) |
| N5 | PS-X EXE header and entry point | N4 | `RomAnalysisPipeline.cs:277-337`; `PsxExe.cs` and `PsxExeHeader.cs` |
| N6 | Text region bounds | N5 | `RomAnalysisPipeline.cs:340-347` (checks only that `TextSegment` is non-empty) |
| N7 | Decode (linear sweep) | N6, `textEnd`; `count` is not part of the sweep (5) | `RomAnalysisPipeline.cs:351-355`; loop `:507-541`; `R3000aDecoder.Decode` at `:516`; formatter at `:522-523` |
| N8 | Basic blocks and CFG | N7 decoded list (sliced to `count`) | `RomAnalysisPipeline.cs:381`; `BasicBlockBuilder.cs:20` takes `instructionCount` but never reads it (CONFIRMED: the parameter appears only at `:20`); its re-decode is at `:30` |
| N9 | Function discovery | N7 decoded list, N8 blocks, N8 edges | `RomAnalysisPipeline.cs:404`; `FunctionDiscovery.Build` at `FunctionDiscovery.cs:81-88` takes no count; re-decode at `:96` |
| N10 | BIOS call recognition | N7, N8, N9 | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, decode at `:176` and the delay-slot decode at `:195` |
| N11 | Report assembly | N7-N10 | `RomAnalysisPipeline.cs:400-444`; call-candidate count re-decodes at `:553` |
| N12a | Recompilation classification | N11 report, lowering validator | `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:88` (`Analyze`); per-instruction `Classify` and `ProbeLowerable` at `:223` and `:234-261`; `MipsToIrLowerer.LowerProgram` at `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176`, called at `RealRomCoverageAnalyzer.cs:251` |
| N12d | Differential section | caller-supplied validations | `RealRomCoverageAnalyzer.cs:85-88` (doc), `:185` (`Differential = BuildDifferential(validations)`), `:377-382` (`BuildDifferential`) |
| N14 | Artifact documents | N11, N12a, N12d | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:178-181`; `DeterministicArtifactBuilder.cs:74`, `:86-89`, `:101-106`, coverage added at `:115-121`, manifest at `:123-127` |
| N15 | Persistence | N14 | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50` (`Directory.CreateDirectory`, `File.WriteAllBytes`) |

**Edge summary**

- N1 to N7 is a strict chain. Each cached stage key includes its upstream key
  (3.2), so an upstream change reaches every stage below it through the key chain.
- N7 is the fan-out point. N8, N9, and N10 each consume the decoded list.
- N12a consumes every decoded instruction through `Classify` (`:223`), so the
  lowering validator is an input to the coverage classification.
- N12d is an input to document assembly only. It is never cached (4.7).
- N14 embeds N12a and N12d. Because it is recomputed every run, a change to its
  own code (`DeterministicArtifactBuilder.cs`, `ArtifactJson.cs`) changes only
  output bytes, never a cached payload.
- `RealRomAnalysisArtifacts.Coverage` is nullable
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomAnalysisArtifacts.cs:24`).
- `manifest.json` hashes each sibling document it lists
  (`AnalysisManifestDocument.cs:22-23`, `ArtifactDocumentReference.Sha256` at
  `:41-42`). The manifest's own comment at `AnalysisManifestDocument.cs:10-12`
  names three documents and is stale: the builder also adds `coverage.json`
  (`DeterministicArtifactBuilder.cs:117-121`), which is the source of truth here.

### 2.2 Who drives persistence and the completion markers

- **CONFIRMED** Persistence is in the test-project driver
  (`src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs`), not in
  `PSXRecomp.Core`. The driver calls the pipeline, then coverage, then
  `DeterministicArtifactBuilder.Build`, then the writer. The cache must move or
  mirror this driver logic into a production component (8.4, U3).
- **CONFIRMED** The `Manifest` and `Complete` stages are recorded by the test
  driver as stage results: `recorder.Pass(RomAnalysisStage.Manifest, ...)` at
  `RealRomAnalyzer.cs:261` and `recorder.Pass(RomAnalysisStage.Complete, ...)` at
  `:262`. The stage enum documents that the pipeline does not produce them
  (`src/PSXRecomp.Core/DiscImage/RomAnalysisStage.cs:10`).
- **Proposal, not an existing convention.** An on-disk `COMPLETE` file marker
  (6.1) does not exist in the code. It is a new proposal, modeled on the stage
  record that the driver already writes after persistence.

### 2.3 Dependency graph (summary)

```
N1 identity --> N2 CHD --> N3 ISO --> N4 boot exe --> N5 EXE header --> N6 text bounds
  --> N7 decode  [sets: extract + decode]
        |--> N8 basic blocks  [set: analysis]
        |--> N9 functions     [set: analysis]
        |        \--> N10 BIOS calls  [set: analysis; also N8]
        '--> N11 report
               --> N12a classification [set: coverage, includes lowering]
               --> N12d differential   [caller validations; never cached]
               --> N14 documents --> N15 persist   [recomputed each run]
```

Outside the chain: the YAML (no edge, 1.2) and the project metadata (no edge
found, UNVERIFIED, 1.4).

## 3. Cache key composition

### 3.1 Principle

A key identifies the exact content of a computation: all of its inputs and the
analyzer code that produced it. Keys contain no filesystem path
(`ArtifactFixtureIdentity.cs:11-12` already forbids local paths).

### 3.2 Fingerprint sets and the key

The correctness input is a set of **per-stage fingerprints**, not one global
fingerprint. The sets are defined in 4.2 and are named as follows:

- `setExtract`: code that turns the image into executable bytes and the entry
  and text bounds (N2 through N6). Carried by every cached key.
- `setDecode`: the decoder, the formatter, and the decode loop (N7). Carried by
  N7 and by every key below it through the chain.
- `setAnalysis`: basic blocks, functions, BIOS recognition, and report types
  (N8 through N11). Carried by N8 through N12a.
- `setCoverage`: the coverage classification and the lowering validator
  (N12a). Carried by N12a only.
- `dependencyFingerprint`: the build inputs that change compiled behavior (4.2).
  Carried by every cached key.

**Key encoding (MAJOR-3 of r1, specified exactly in 3.6).** A stage key is the
SHA-256 of a canonical JSON object produced by a dedicated key encoder.
`ArtifactJson.CanonicalOptions`
(`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:34-38`) sets
`WriteIndented = true` and camelCase naming, and its property order is the record
declaration order (`ArtifactJson.cs:23`). It does not sort keys, so it is not
used for keys.

**Fields.** Each row lists the cache units that carry the field.

| Field | Source | Carried by | Notes |
|---|---|---|---|
| `keyFormat` | cache constant | N7, N8, N9, N10, N11, N12a | Bumped when the key layout changes |
| `stage` | stage id | all | `N7`, `N8`, `N9`, `N10`, `N11`, `N12a` |
| `setExtractFingerprint` | 4.2 | all | Covers N2 through N6 code |
| `setDecodeFingerprint` | 4.2 | all | Covers N7 code and everything below |
| `setAnalysisFingerprint` | 4.2 | N8 through N12a | Covers N8 through N11 code |
| `setCoverageFingerprint` | 4.2 | N12a | Covers the classification and lowering code (includes lowering, CRITICAL-1 of r2) |
| `dependencyFingerprint` | 4.2 | all | Csproj, `Directory.Build.props`, resolved packages, SDK version |
| `engineRevision` | manual label | none | Human-readable only. It is not a key field (rule A2) |
| `schemaVersions` | `AnalysisArtifactSchema.*SchemaVersion` and `FunctionProvenanceSchemaVersion` | all | Existing constants (1.3) |
| `discImageSha256` | N1, verified by the pipeline (8.3) | all | Formal input identity (`ArtifactFixtureIdentity.cs:23-24`) |
| `executableSha256`, `executableSizeBytes` | N4 | all | From `ArtifactFixtureIdentity.cs:35-39` |
| `entryPoint`, `textEnd` | N5, N6 | N7 and below | Decode bounds |
| `instructionCount` | caller | N8 through N12a | Conservative. N8 does not read it (`BasicBlockBuilder.cs:20`), and N9 does not take it. Its effective input is the length of the sliced decoded list, which this field determines |
| `inputContainer` | N2 vs raw ISO | all | `"CHD"` or `"ISO"`; see 3.4 |
| `projectFields` | section 3.3 | per classification | Only fields that feed analysis; empty today |
| `upstreamKeys` | prior stage keys | N8 and below | Merkle chain. N7 has no upstream cache key (N6 is not a cache unit) |

### 3.3 Project fields

- **PREDICTED** Project metadata affects analysis only if a field is read by N1
  through N14. Each such field must be listed explicitly in `projectFields`.
- **UNVERIFIED** The full set of project fields was not enumerated (U1).

### 3.4 Container format

- **CONFIRMED** The pipeline records a skip when there is no container
  (`RomAnalysisPipeline.cs:163`). The hash is of the file bytes
  (`RealRomAnalyzer.cs:102-104`), so a raw ISO and a CHD with identical content
  have different `discImageSha256` values.
- **PREDICTED** Such a pair therefore misses today. This follows from the
  byte-level identity and is not a measured result.
- **PREDICTED** Including `inputContainer` makes the separation explicit.

### 3.5 Example keys (format only, not real values)

```
stage=N7 decode
setExtractFingerprint=<hex>
setDecodeFingerprint=<hex>
dependencyFingerprint=<hex>
schemaVersions={report:2,instructions:1,cfg:1,...}
discImageSha256=<hex>
executableSha256=<hex>
entryPoint=<u32>  textEnd=<u32>
inputContainer=CHD
upstreamKeys={}

stage=N8 basic blocks
setExtractFingerprint=<hex>  setDecodeFingerprint=<hex>  setAnalysisFingerprint=<hex>
dependencyFingerprint=<hex>
instructionCount=<int>
upstreamKeys={N7:<hex>}
```

The N7 key has no `instructionCount`. The N8 key has one.

### 3.6 Key encoder specification (MINOR-4 of r2)

The encoder output is exactly the following, with no added whitespace:

- **Encoding.** UTF-8, no byte-order mark, LF only. The output is one line with no
  trailing newline.
- **Objects.** `{`, then members separated by `,`, then `}`. Each member is
  `"name":value`. There is no space after the colon.
- **Member order.** Members are sorted by the UTF-8 byte sequence of the member
  name, compared byte by byte, shorter prefix first. Declaration order is never used.
- **Arrays.** `[`, elements separated by `,`, `]`. Elements keep their given order.
- **Strings.** `"`-delimited. `"` is written as `\"`, `\` as `\\`. Each control
  character U+0000 through U+001F is written as `\u00xx` with lowercase hex
  digits, including the characters that have short forms (`\n` is written
  `\u000a`). Every other character is written as its UTF-8 bytes, unescaped. The
  character U+007F is not escaped.
- **Integers.** Base 10, no leading `+`, no leading zeros, `-` only for negative
  values. Zero is `0`.
- **Booleans and null.** `true`, `false`, `null`.
- **No floats** are permitted in a key. An encoder that receives one fails.
- **Golden value.** T1 asserts the exact encoded text of a fixed sample and its
  SHA-256. Both values are committed with the test.

## 4. Invalidation rules

Each rule names the cache units whose keys change. "Chain" means that the key
of each later stage includes the upstream key (3.2), so the change also reaches
every stage below.

### 4.1 Decoder and formatter change

- **CONFIRMED** The decoder maps raw words to mnemonic, format, control flow,
  and delay-slot kind (`R3000aDecoder.cs:70`). Its consumers are:
  - `RomAnalysisPipeline.cs:516` (`DecodeInstructions`, N7);
  - `RomAnalysisPipeline.cs:553` (`CountCallReturnCandidates`, N11);
  - `BasicBlockBuilder.cs:30` (N8);
  - `FunctionDiscovery.cs:96` (N9);
  - `BiosCallRecognition.cs:176` and `:195` (N10, including the delay-slot decode).
- **CONFIRMED** The text output also depends on `MipsInstructionFormatter`.
  `RomAnalysisPipeline.cs:522-523` copies `FormatMnemonic` and `FormatOperands`
  into `DecodedInstruction.Mnemonic` and `Operands`. The register table is at
  `src/PSXRecomp.Core/DiscImage/MipsInstructionFormatter.cs:12-17`, and
  `FormatOperands` is at `:95`.
- **Rule D1.** A change to any file in `setDecode` (4.2) changes
  `setDecodeFingerprint`. Invalidated: N7, and by chain N8 through N12a. N1 through
  N6 are not cache units and keep their behavior.
- **Rule D1a.** The behavioral guard tuple includes the formatter output
  (`mnemonic`, `operands`) as well as `(opcode, funct, format, controlFlow,
  delaySlot)`.
- **Behavioral guard (PREDICTED, UNVERIFIED feasibility).** A fingerprint over a
  fixed, enumerated corpus of words is a secondary check. It catches a behavior
  change in the decoder's output without a source change, such as a change in a
  generated table. The corpus covers the 6-bit opcode and funct fields with fixed
  operand patterns, plus the golden vectors in `R3000aDecoderGoldenTests.cs`. It
  is a guard, not a proof, so the set fingerprint (4.2) remains the correctness
  input.

### 4.2 Per-stage fingerprint sets (replaces the global analyzer fingerprint)

**Resolution of CRITICAL-A.** The r2 design defined one analyzer fingerprint over
all of `src/PSXRecomp.Core/**/*.cs` and applied it to every key. That made every
edit to any Core file, including the lowerer and the decoder, invalidate the
whole cache. It contradicted rules L1 and D1 and tests T4 and T16. The global
fingerprint is withdrawn. Each stage carries only the fingerprint of the code
that can change its output.

**Set membership (explicit lists, MAJOR-1 of r2).** Every entry is a
repository-relative path. The sets are committed as literal lists in the
generator and in T13. The test does not compare against a glob. Nested sets
are defined by addition:

- **`setExtract`** (N2 through N6):
  `src/PSXRecomp.Core/DiscImage/ChdReader.cs`,
  `src/PSXRecomp.Core/DiscImage/ChdHeader.cs`,
  `src/PSXRecomp.Core/DiscImage/ChdMapEntry.cs`,
  `src/PSXRecomp.Core/DiscImage/ChdBitstream.cs`,
  `src/PSXRecomp.Core/DiscImage/ChdHuffmanDecoder.cs`,
  `src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs`,
  `src/PSXRecomp.Core/DiscImage/ChdCdSectorSource.cs`,
  `src/PSXRecomp.Core/DiscImage/Iso9660Reader.cs`,
  `src/PSXRecomp.Core/DiscImage/Iso9660DirectoryEntry.cs`,
  `src/PSXRecomp.Core/DiscImage/SystemCnfParser.cs`,
  `src/PSXRecomp.Core/DiscImage/PsxExe.cs`,
  `src/PSXRecomp.Core/DiscImage/PsxExeHeader.cs`,
  `src/PSXRecomp.Core/DiscImage/DiscImageAnalyzer.cs`,
  `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs`.
  The pipeline file is included because it contains every stage's control flow.
  Coarse by design (5.1 decision).
- **`setDecode`** (N7 and below) = `setExtract` plus everything in
  `src/PSXRecomp.Core/Cpu/` (all files there), and
  `src/PSXRecomp.Core/DiscImage/MipsInstructionFormatter.cs`.
- **`setAnalysis`** (N8 and below) = `setDecode` plus
  `src/PSXRecomp.Core/DiscImage/BasicBlock.cs`,
  `src/PSXRecomp.Core/DiscImage/BasicBlockBuilder.cs`,
  `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs`,
  `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs`,
  `src/PSXRecomp.Core/DiscImage/DecodedInstruction.cs`,
  `src/PSXRecomp.Core/DiscImage/DecodeFailure.cs`,
  `src/PSXRecomp.Core/DiscImage/DiscImageAnalysisReport.cs`,
  and the `src/PSXRecomp.Core/Analysis/Contracts/` files referenced by those
  types (the exact list is U19).
- **`setCoverage`** (N12a) = `setAnalysis` plus
  `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs`,
  `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomCoverageDocument.cs`
  (it defines the classification enum `RealRomCoverageClass`),
  `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs`,
  `src/PSXRecomp.Core/Recompiler/MipsToIrLoweringResult.cs`, and
  `src/PSXRecomp.Core/Recompiler/RecompilerContract.cs`. The exact lowering
  file list is U9.
- **`dependencyFingerprint`** = `src/PSXRecomp.Core/PSXRecomp.Core.csproj` and
  `Directory.Build.props` (repository root), plus two generated inputs (below).

**Files excluded by definition.** `bin/` and `obj/` (`.gitignore:5` ignores
`obj/`, and `.gitignore` also lists `bin/`), the generated fingerprint constant,
and every file not in a set. The generated constant is never an input. This
removes the cycle the reviewer identified.

**Dependency inputs not in source.**
- **Resolved packages.** The build reads the resolved package list for
  `PSXRecomp.Core` from `obj/project.assets.json` at build time, as sorted
  `(packageId, resolvedVersion)` pairs. The csproj pins only direct packages:
  `SharpCompress 0.50.4` (`PSXRecomp.Core.csproj:25`) and `loach.PureSharp 0.1.6`
  (`:24`). No lock file exists (CONFIRMED: no `packages.lock.json` or lock file
  in the tree). Transitive versions are therefore unpinned and must be captured
  from the build output. SharpCompress is used by `ChdCdCodec.cs:3`, and its
  decoded output can change under an unchanged image hash. A version change in it
  must invalidate N2 through N12a. It is therefore in `dependencyFingerprint`.
- **SDK and target.** The .NET SDK version and `TargetFramework` (`net10.0`,
  `PSXRecomp.Core.csproj:3`) are included, because a compiler change can change
  behavior with identical source.

**Hash input format (MAJOR-1 of r2).** For a set, the fingerprint is the SHA-256
of the following byte stream. Members are sorted by the UTF-8 bytes of their path.
For each member in that order, append:

1. the UTF-8 bytes of the repository-relative path, with forward slashes and no
   leading slash;
2. `0x0A`;
3. the decimal byte length of the normalized content, with no leading zeros;
4. `0x0A`;
5. the normalized content bytes;
6. `0x0A`.

Normalized content is the file's bytes with every CRLF pair replaced by LF. A
lone CR is kept. The BOM bytes, if any, are kept. Decision DEC-LF1 (below)
gives the rationale.

**Decision DEC-LF1 (line endings).** Hash the LF-normalized bytes. Rationale: a
checkout with different line-ending settings would otherwise produce a different
fingerprint for identical source, which is a cross-machine miss. The hazard is
real on this machine: git reported that "LF will be replaced by CRLF" for the
design file (OBSERVED, from the git warning during commit `6d038a0`; the r2
text labeled this as CONFIRMED, which was wrong). The cost is one known
exception. A multi-line verbatim string literal's value depends on its line
endings, so normalization would hide a change that alters only those endings. The
guard is lint test T20, which fails if any file in a set contains a multi-line
verbatim string literal.

**Generation.** A build target computes the fingerprints from the explicit lists
and emits them as a generated constant in `PSXRecomp.Core` (in `obj/`, excluded
above). It is run before compilation. Nothing is curated by hand beyond the
committed lists, and T13 checks those lists exactly (section 7).

**Closure (CRITICAL-A test).** An explicit list is correct only if it is closed:
every analysis-relevant file is in a set. The closure test enumerates every
`.cs` file under `src/PSXRecomp.Core/` (using a glob here, to find all files, not
to define membership) and requires each to be either in one of the committed
lists or in an explicit `NonAnalysis` list. The `NonAnalysis` list is reviewed
(for example, GPU, DMA, runtime host, execution engines). Whether a
`NonAnalysis` file is actually unreachable from a stage's entry points is a
Roslyn reference check (U14). Until U14 is done, closure is a classification
guarantee, not a reachability proof (UNVERIFIED).

**Which keys carry which fingerprint (rule A1, restated).** Each cached key
carries exactly the fingerprints in 3.2. A change to a file in a set changes the
fingerprint of that set, and therefore changes the keys that carry it. Keys
that do not carry the set are unaffected.

- **Rule A1.** The fingerprint of a set changes when and only when a file in that
  set changes (after normalization). The affected keys are those listed for that
  set in 3.2.
- **Rule A1a (over-invalidation accepted).** Comments and refactors inside a set
  change its fingerprint. This is a deliberate fail-safe.
- **Rule A2.** `engineRevision` is a label and is not a key field.

**Verify mode and stale entries (MAJOR-2 of r2).** Verify mode (Q4) recomputes a
hit and compares it with the cached payload. Verify mode catches a defect in a
fingerprint only if the entries it checks were produced by a different binary.
If CI populated the cache and verified it with the same binary, a stale entry
from an older analyzer would never be compared. The rule is therefore:

- **V-CI1.** The verify corpus is a set of **committed golden entries**,
  produced once by a build of the base commit on the synthetic corpus. The
  entries contain only outputs computed from synthetic inputs, so they hold no
  BIOS, ROM, or CHD content (U15 decides their location).
- **V-CI2.** CI verifies those golden entries with the new binary. For each
  golden entry, the new binary computes the key from the same inputs.
  - If the key matches, the payload is compared with a cold recompute. Any
    difference is `ANALYSIS_CACHE_VERIFY_MISMATCH`. That means a file is missing
    from a set, or a behavior changed without a fingerprint change.
  - If the key does not match, the fingerprint changed on purpose. The entry is
    reported as superseded, not as a failure.
- **V-CI3 (test).** A deliberately stale-but-valid entry is tested: an entry
  whose header and `COMPLETE` marker are consistent with the current key, but
  whose payload was produced by a mutated analyzer. Verify mode must report
  `ANALYSIS_CACHE_VERIFY_MISMATCH`. Normal mode must treat it as a quarantined
  miss with a warning. This is T9.

### 4.3 YAML change (`config/cpu/r3000a-instructions.yaml`)

- **CONFIRMED** The YAML has no runtime or build-time reader (1.2).
- **Rule Y1 (current-state rule, MINOR-3 of r2).** While no code reads the YAML,
  it is in no key and no set. A comment-only edit must not invalidate the cache.
  **Revisit this rule when a generator or runtime reader of
  `config/cpu/r3000a-instructions.yaml` lands.** The triggering decision is
  `docs/adr/002-instruction-definition-yaml.md`, which names Decoder,
  Interpreter, Recompiler, and others as consumers. The SSOT decision is
  `docs/adr/001-cpu-specification-ssot.md`.
- **Rule Y2.** When a generator lands, its generated output enters `setDecode`
  by listing the generated file. The YAML itself is not hashed raw, because
  whitespace and comments would over-invalidate. A YAML fingerprint, if needed,
  is the SHA-256 of the parsed, canonicalized structure.
- **UNVERIFIED** Whether any generator exists outside the searched file types.
  The Rust and C++ decoders exist (`src/PSXRecomp.Native/rust/src/cpu_decode.rs`,
  `src/PSXRecomp.Native/src/psx_cpu_decode.cpp`); whether they read the YAML was
  not checked (U2).

### 4.4 Project change

- **Rule P1.** A project edit invalidates only stages whose `projectFields`
  changed. Today that set is empty, so no project edit invalidates the cache.
- **Rule P2.** `ProjectFormatVersion` (`ProjectSchema.cs:19`) enters the key only
  if the project format changes what the analysis reads.

### 4.5 Input change

- **Rule I1.** A changed disc image or executable changes N1 or N4 and therefore
  everything downstream. This is a miss by construction, not an invalidation.

### 4.6 Lowering change (`setCoverage` minus `setAnalysis`)

**Resolution of CRITICAL-A for L1.** L1 is restated against the per-stage sets.

- **CONFIRMED** `Classify` calls `ProbeLowerable` for each instruction that
  reaches that branch (`RealRomCoverageAnalyzer.cs:223`), and `ProbeLowerable`
  calls `MipsToIrLowerer.LowerProgram` (`:251`). The class of each decoded
  instruction is therefore a function of the lowering validator.
- **Rule L1.** A change to a file that is in `setCoverage` but not in
  `setAnalysis` changes `setCoverageFingerprint`. Those files are
  `RealRomCoverageAnalyzer.cs`, `RealRomCoverageDocument.cs`,
  `MipsToIrLowerer.cs`, `MipsToIrLoweringResult.cs`, and `RecompilerContract.cs`.
  Invalidated: **N12a only**. N7 through N11 keep their hits, because their keys
  do not carry `setCoverageFingerprint`. N14 and N15 are recomputed, so they have
  no stale state.
- **Rule L1a.** A lowering change that alters `Classify` but is in a file outside
  `setCoverage` would be a closure defect. The closure test (4.2) and T16 detect
  it.
- **CONFIRMED** Coverage is optional in the artifact set
  (`RealRomAnalysisArtifacts.cs:24`), so N12a can be skipped without affecting the
  required documents.

### 4.7 Differential-section input (N12d)

- **Rule C1.** The differential section is built from validations that actually
  executed (`RealRomCoverageAnalyzer.cs:85-88`). Its absence means "nothing was
  proven". It is not a function of the analysis.
- **Rule C2 (chosen policy).** N12d is never cached and is in no key. It is
  assembled at N14 from the validations supplied on that run.
- **Why not a digest.** A digest of validation evidence in the N12 key was
  rejected. The validations come from executions whose identity and completeness
  the cache does not own. Assembling the section fresh is cheap and cannot be stale.
- **Follow-up refactor (MINOR-6 of r2).** To cache N12a alone, `Analyze` must be
  split so that classification and differential are separate calls. Today
  `Differential = BuildDifferential(validations)` is set inside the same
  constructor (`RealRomCoverageAnalyzer.cs:185`, `:377-382`). Until that split
  lands, N12a is cached as the coverage document produced with no validations, and
  N14 rebuilds the differential section. That needs `BuildDifferential` to be
  reachable from N14. The split is U18.
- **Test.** T15 compares warm and cold runs with the differential on and off.

## 5. Partial re-analysis units and cost

**Resolution of MAJOR-1 of r1 and the r2 points.** `count` is removed from the N7
key. N7 is the full sweep bounded only by `textEnd`. `count` is applied as a prefix
when N7's output is consumed.

### 5.1 Decode sweep (N7)

- **CONFIRMED** The current loop is bounded by `count` and by `textEnd`
  (`RomAnalysisPipeline.cs:507-511`). `maxAddress` is `exe.Header.TextEnd` (`:507`).
  The loop body reads `GetInstructionWord` inside a `try` (`:513-539`).
- **Decision DEC-N7 (cost, MINOR-6 of r2).** A cold N7 run decodes the whole text
  region up to `textEnd`, not the first `count` words as the current code does. The
  cost is linear in text size and is not measured (UNVERIFIED, U5). Each cache
  hit then returns a prefix with no re-decode.
- **Prefix rule.** An N7 entry with `sweepEnd = textEnd` serves any `count`. For a
  requested count `M`, the consumer takes the first `min(M, total)` instructions.
- **Failure rule (MINOR-3 of r2).** A decode failure at address `A` is included in
  a count-limited result iff `A < entry + 4*M`. The loop (`:511`) stops after `M`
  successful decodes, so a failure at or beyond `entry + 4*M` is never reached by
  a count-limited run.

### 5.2 Text shorter than the declared size (resolves the r2 UNVERIFIED)

- **CONFIRMED** The text check at `RomAnalysisPipeline.cs:340-347` rejects only a
  zero-length `TextSegment`. It does not compare with `header.TextSize`.
- **CONFIRMED** `PsxExe.GetInstructionWord` throws `ArgumentOutOfRangeException`
  when `offset + 4 > TextSegment.Length` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:21-30`).
- **CONFIRMED** That exception is caught by the decode loop's catch at
  `RomAnalysisPipeline.cs:531`, which records a `DecodeFailure` ("Address outside
  text segment bounds") and breaks (`:533-538`).
- **Consequence (correction of r2).** The r2 text said the loop condition
  "appears to prevent" out-of-range reads. For a segment shorter than `textEnd`,
  it does not. The read fails, the exception is caught, and the decode records one
  failure and stops. No exception escapes the stage. The decode is then a partial
  sweep with one failure.
- **Test T17** pins this: a synthetic executable whose text segment is shorter than
  its declared size passes `MIPS_DECODE` with exactly one failure at the first
  address past the segment, and no exception escapes. The cache stores that result
  like any other.

### 5.3 Other unit rules

- **Unit of reuse: the cache unit (N7, N8, N9, N10, N11, N12a).** A hit replaces
  that unit's computation. A miss recomputes it and, through the chain, every
  unit below it.
- **No sub-stage reuse below N7.** BasicBlockBuilder, function discovery, and BIOS
  recognition each consume the full decoded list. Per-block or per-function reuse
  needs a dependency model. It is out of scope for v1.
- **Unit for coverage.** Probe results are keyed by raw word
  (`RealRomCoverageAnalyzer.cs:231-237`). N12a is the cache unit, and its key
  carries `setCoverageFingerprint`, so the probe cache inside a run is sound.

## 6. Fail-closed handling of incomplete or corrupted cache

The rule is that a cache entry is used only if it is proven complete and correct.
Anything else is a miss. A miss is never an error, and a miss never produces
output that a cold run would not produce.

### 6.1 Entry layout (proposal)

Each cache unit is a directory with:

- `header`: format version, `key` (the full key text from 3.6 and its SHA-256),
  `payloadSha256`, `payloadSizeBytes`, the set fingerprints in effect, and
  `schemaVersions`.
- `payload`: the stage output in the key encoder's canonical form.
- `COMPLETE`: a marker written last (proposal; see 2.2).

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the entry directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, and only after W3 succeeds for every file.
- **W5.** Never write an entry for a unit whose stage did not pass. The stage
  records already distinguish pass from fail (`RomAnalysisStageRecorder`).
- **W6.** Concurrent writers of the same key write the same content. A rename that
  fails because the target exists is success only if the existing header's
  `payloadSha256` matches. Otherwise the entry is quarantined (6.4) and the write
  is retried once.

### 6.3 Read protocol (fail closed)

An entry is used only if all of the following hold. Any failure is a miss.

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses and its format version is supported.
- **R3.** `header.key` equals the key computed from the current inputs. This is the
  only check that detects a stale entry. R1, R2, and R4 through R6 prove only
  internal consistency. A stale entry from an older analyzer is internally
  consistent, so R3 is correct only because the set fingerprints are in the key
  (4.2).
- **R4.** `payload` size equals `payloadSizeBytes`.
- **R5.** SHA-256 of `payload` equals `payloadSha256`.
- **R6.** The payload deserializes under the current schema versions.
- **R7.** Any exception during R1 through R6 is caught and treated as a miss.

### 6.4 Quarantine and recovery

- **Q1.** A header or payload that fails R2 through R6 is renamed to a quarantine
  name that records the failing check, not a clock value.
- **Q2.** After a miss, the unit is recomputed and written by the write protocol.
- **Q3 (MINOR-1 of r1).** Dedicated codes: `ANALYSIS_CACHE_QUARANTINED` for Q1 and
  `ANALYSIS_CACHE_VERIFY_MISMATCH` for Q4. They are not reused from
  `AnalyzerAnalysisFailed` or `AnalyzerReportFailed`
  (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`). Under
  `docs/adr/019-diagnostics-and-recovery-contract.md`, a code is a validated
  `SCREAMING_SNAKE` identity, and registering the two codes beside the existing
  ones is the recommended practice. A quarantine is a warning on the run.
- **Q4.** Verify mode recomputes a hit and compares it with the cached payload. A
  mismatch is `ANALYSIS_CACHE_VERIFY_MISMATCH` and a hard failure. Verify mode
  runs in CI against the committed golden entries (4.2, V-CI1 to V-CI3).

### 6.5 Input identity is never cached as truth

- **I2.** The disc image SHA-256 is computed on every run from the full file
  (`RealRomAnalyzer.cs:102-104`), and the pipeline verifies it (8.3). A
  size-and-mtime shortcut is rejected because it is not a content identity.
- **PREDICTED** Full hashing dominates the warm-run cost on large images. The
  cost is UNVERIFIED (U5).

## 7. Deterministic test plan

Tests use synthetic inputs from `src/PSXRecomp.Tests/RealRomAnalysis/`:
`SyntheticDiscBuilder.cs`, `SyntheticChdBuilder.cs`, and
`SyntheticIsoBothEndianTests.cs` (CONFIRMED that these exist; their APIs are
UNVERIFIED, U7). No BIOS, ROM, or CHD content is used. Clocks are injected. Temp
paths are not asserted.

| ID | Test | Asserts |
|---|---|---|
| T1 | Key encoder (3.6) | The encoded text of a fixed sample equals the committed golden text byte for byte, and its SHA-256 equals the committed value. Members are in UTF-8 byte order. Reordering record members in source leaves the text unchanged. Control characters are escaped as `\u00xx`, and no floats are accepted |
| T2 | Key sensitivity | Changing `discImageSha256`, any set fingerprint, `dependencyFingerprint`, a schema version, or `inputContainer` changes exactly the keys that carry it (3.2). Changing `instructionCount` changes the N8, N9, N10, N11, and N12a keys, and **does not** change the N7 key |
| T3 | Project non-analysis edit | Changing a project field outside `projectFields` leaves every key unchanged |
| T4 | Set scope (rules D1, A1) | A change to a `setDecode` file (for example `R3000aDecoder.cs`) changes N7 and, by chain, N8 through N12a. A change to a `setCoverage`-only file (`RealRomCoverageAnalyzer.cs`) changes N12a only. A change to a `setAnalysis`-only file (`BasicBlockBuilder.cs`) changes N8 through N12a and leaves N7 unchanged |
| T5 | YAML comment-only edit | No key changes (rule Y1) |
| T6 | Corruption matrix | Each of: zero-length file, truncated payload, flipped byte, missing `COMPLETE`, wrong `key` in header, unsupported format version, malformed header, schema-version mismatch, produces a miss, a quarantine with `ANALYSIS_CACHE_QUARANTINED`, and output byte-identical to a cold run |
| T7 | Partial failure | A pipeline failure at N8 writes no entry for N8 or later; the N7 entry is the only one written |
| T8 | Warm equals cold | For each synthetic image, the warm run's artifact bytes (`ArtifactFile.ToUtf8Bytes`) equal the cold run's, byte for byte |
| T9 | Stale-but-valid entry (V-CI3) | A consistent entry whose payload comes from a mutated analyzer is reported as `ANALYSIS_CACHE_VERIFY_MISMATCH` in verify mode, and as a quarantined miss with a warning in normal mode |
| T10 | Concurrent write | Two writers with the same key leave one complete entry. A fake filesystem that interleaves the renames does not produce a torn entry |
| T11 | Shared store and identity (MAJOR-5 of r1) | The CLI and GUI entry points, given the same bytes, compute the same verified `discImageSha256`, the same key, and read the same entry. A wrong supplied hash fails at the Input stage with no cache write and no artifact |
| T12 | No path in key | No key or entry contains an absolute path (`PathRedactor.cs` under `src/PSXRecomp.Tests/RealRomAnalysis/`; its API is UNVERIFIED, U7) |
| T13 | Set membership (MAJOR-1 of r2) | Each committed set equals its literal list exactly: no missing file, no extra file, no `obj/` or `bin/` path, no generated constant. The fingerprint of a fixed sample tree equals a committed golden value. The fingerprint is unchanged when a file's CRLF endings are changed to LF. It is changed when a byte of content changes |
| T14 | Prefix reuse with failure filter (5.1) | An N7 entry computed without `count`, sliced to `M`, equals a cold decode at `M`, including the failures filtered by `A < entry + 4*M` |
| T15 | Differential on and off (CRITICAL-2 of r1) | Warm and cold runs compared with validations supplied, and without. Both match byte for byte. N12d is never read from the cache |
| T16 | Lowering scope (rule L1) | A change to `MipsToIrLowerer.cs` changes `setCoverageFingerprint`. It invalidates N12a only: N7 through N11 keep their hits. The N12a classes of a fixed synthetic corpus change when the validator changes |
| T17 | Short text segment (5.2) | An executable whose text segment is shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment, and no exception escapes |
| T18 | Identity verification (8.3) | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind, with no analysis run and no cache write |
| T19 | Dependency fingerprint | Changing a resolved `(packageId, version)` pair in the fixture list, or the SDK version string, changes `dependencyFingerprint` and every key |
| T20 | Multi-line verbatim literal lint (DEC-LF1) | Fails if any file in a committed set contains a multi-line verbatim string literal |
| T21 | Closure (4.2) | Every `.cs` under `src/PSXRecomp.Core/` is either in a committed set or in the `NonAnalysis` list. Each `NonAnalysis` entry is listed with a reason. The reachability check is U14 |

Determinism rules for every test:

- No wall-clock values in keys, headers, or assertions.
- No dependence on the order of unsorted collections. The artifact builder already
  sorts files (`DeterministicArtifactBuilder.cs:127`).
- No reliance on the machine's temp directory name.

## 8. Mapping to Issue #43 acceptance criteria

The issue's acceptance criteria are (OBSERVED, from a fetched summary of the issue;
the criteria are unchecked):

1. Define dependencies between analysis artifacts. **Section 2.**
2. Define the cache key. **Section 3.**
3. Define invalidation rules for Analyzer, Decoder, Project, and YAML changes.
   **Section 4 (rules D1, A1, L1, P1, Y1, Y2).**
4. Define the unit of partial re-analysis. **Section 5.**
5. Define conditions for reusing AI results. **Section 8.1.**
6. Define recovery for a corrupted cache. **Section 6.**
7. Settle one cache shared by CLI and GUI. **Section 8.2 and 8.3.**

### 8.1 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists (1.1).
- **CONFIRMED (MINOR-1 of r2).** `EvidenceType.AIPrediction = 6` already exists
  (`src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`), next to
  `AnalyzerFinding = 5` (`:16`).
- **Collision risk.** A cached AI result that reuses `AIPrediction` is labeled the
  same way as any future AI evidence, and a consumer that switches on
  `EvidenceType` could treat it as evidence. The rules below prevent that, but
  they do not stop an existing or future consumer from doing so. A consumer audit
  is U16.
- **Rule AI1.** An AI result is advisory. It is never stored as analysis output
  and never appears in an analysis document. It is stored in its own entry type,
  outside the analysis cache units.
- **Rule AI2.** An AI result is reused only when its key matches: the hashes of the
  artifacts it was computed from, the prompt or template revision, the model
  identifier, and the model settings. Any mismatch is a miss.
- **Rule AI3.** An AI result is invalidated when any analysis unit it depends on
  is invalidated. Because it is keyed on upstream hashes, this follows.
- **Rule AI4.** Whether a stored AI result uses `EvidenceType.AIPrediction` is a
  decision for its own ADR. The existing label must not be reused silently.

### 8.2 Shared CLI and GUI cache

- **CONFIRMED** Both front ends call the same pipeline (1.1), so the key function
  and the store format are shared code.
- **Policy S1.** One store format and one key function. The port and key types live
  in `PSXRecomp.Core`, and the file store lives in `PSXRecomp.Infrastructure`
  (8.4). The CLI and GUI pass only a store root, and no front end defines its own
  key.
- **Policy S2.** The store root is supplied by the host. Its location is UNVERIFIED
  (U4).
- **Policy S3.** Cross-process concurrency uses the write protocol (6.2, W6).
  Readers never see an incomplete entry, because `COMPLETE` is written last.
- **PREDICTED** Sharing is safe because keys contain no path and no front-end
  state. A front-end field added to a key later must be reviewed against this rule.

### 8.3 Identity verification (MAJOR-5 of r1)

- **CONFIRMED** The CLI hashes the file in its own pass and then runs the pipeline
  on a second open of the same path (`src/PSXRecomp.Cli/CliInput.cs:100-116`,
  `LoadChdWithIdentity`). The two passes are not atomic, so the file could change
  between them.
- **CONFIRMED** The GUI accepts `discImageSha256` as a parameter and passes it on
  (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:110-117`).
- **CONFIRMED** The pipeline checks only that the supplied hash is non-empty
  (`RomAnalysisPipeline.cs:93-98`, `:194-198`). The supplied value is trusted.
- **Rule V1.** The pipeline computes SHA-256 over the stream or byte array it runs,
  and compares it with the supplied value. A mismatch fails the Input stage with a
  dedicated failure kind, and no analysis runs. The cache uses only the verified
  hash. The stream path hashes from position 0 to end, then seeks back to 0 before
  the run. The stream contract already requires seekability
  (`RomAnalysisPipeline.cs:70-72`). The pipeline already uses SHA-256 in the Domain
  (`RomAnalysisPipeline.cs:2`, `:576-579`), so the hash is pure computation.
- **Rule V2.** The supplied hash becomes a claim that is checked, not an identity.
- **Migration (MINOR-8 of r2).** Rule V1 breaks existing callers that pass a
  constant. `DiscImageHostileSizeTests.cs:24` defines `Sha` as the literal
  `0123456789abcdef...` (64 hex characters). That is not the hash of the synthetic
  bytes it passes at `:347` (`RunFromChd(chd, Sha)`), so V1 would fail the test.
  About 50 callers of `RunFromChd` and `RunFromIsoImage` exist in the test projects
  (`DiscImageHostileSizeTests.cs` 9, `SyntheticChdReaderTests.cs` 2,
  `RealRomAnalyzer.cs` 2, `RomAnalysisPipelineTests.cs` 36,
  `RealRomProductionExecutionTests.cs` 1). How many use a constant is UNVERIFIED.
  The migration is U13: a shared test helper computes the real SHA-256 of the
  synthetic bytes, and every caller uses it.
- **Cost (PREDICTED).** Verification reads the image twice on the stream path. The
  cost is part of U5.

### 8.4 Architecture placement (MAJOR-4 of r1)

The architecture matrix (`docs/architecture-matrix.md`) assigns the pieces as
follows:

- Domain (`PSXRecomp.Core`) owns ports and deterministic contracts
  (`architecture-matrix.md:22`, rule 1 at `:64`). It must not reference
  `PSXRecomp.Infrastructure`.
- Infrastructure (`PSXRecomp.Infrastructure`) owns concrete managed host adapters.
  `File` and `Directory` access belongs there (`architecture-matrix.md:24`, `:55`,
  rule 2 at `:65`).
- Application and CLI do not call `File.*` or `Directory.*` for ordinary logic
  (rule 3 at `:66`). Wiring a concrete adapter is allowed only at one
  composition-root site, with a narrowly scoped AARC002 suppression (`:47`).

Resolution:

- **Core (Domain):** `IAnalysisCacheStore` as a port, the key and entry types, the
  key encoder (3.6), the lookup and validation logic (R1 through R7 as pure checks
  over bytes already read), and the quarantine decision. No file access.
- **Infrastructure:** `FileAnalysisCacheStore : IAnalysisCacheStore`, marked
  `[Infrastructure]`. It performs the temporary write, flush, rename, marker, and
  quarantine rename (6.2, 6.4).
- **Precedent.** `FileProjectMetadataStore`
  (`src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs`, marked
  `[Infrastructure]`, Issue #42) implements `IProjectMetadataStore`, which is
  defined in `PSXRecomp.Core.Project`. The cache follows the same split.
- **Composition.** The CLI and GUI receive the port. The one composition-root site
  that constructs `FileAnalysisCacheStore` is the only Application-to-Infrastructure
  reference, under the matrix's exception (`:47`).
- **ADR required.** A new port and adapter for the analysis cache, the move of
  persistence out of the test project (U3), and the use of any AI evidence label
  (8.1, Rule AI4) each need an ADR under `docs/adr/` before implementation. This
  document does not create one.

## 9. Open items and UNVERIFIED list

- **U1.** Enumerate every project field and classify it as analysis-relevant or not
  (3.3, rule P1).
- **U2.** Confirm that no build script or generator reads the YAML in
  `src/PSXRecomp.Native` or in any non-searched file type (rule Y2).
- **U3.** Decide where the production driver lives. Persistence sits in the test
  project (2.2). Moving it is a boundary decision that requires an ADR (8.4).
- **U4.** Decide the store root (S2).
- **U5.** Benchmark cold N7 over the full text region (DEC-N7), and full-image
  SHA-256 including the double read of Rule V1.
- **U6.** Confirm the decoder behavioral-guard corpus coverage (4.1).
- **U7.** Read `PathRedactor.cs` and `SyntheticDiscBuilder.cs` APIs before the test
  plan is implemented (7).
- **U8.** Check whether `DeterministicArtifactTests.cs` already pins golden bytes;
  if it does, part of T13 is covered (UNVERIFIED).
- **U9.** Determine the exact lowering file list for `setCoverage` (4.2). Confirm
  the membership of `RecompilerContract.cs`, `MipsToIrLoweringResult.cs`, and
  `RecompilerIrMemoryEffectClassifier.cs`, which is not in the list yet
  (UNVERIFIED).
- **U10.** Read the caller of `AnalyzeAndExecuteFromDisc` to see how the GUI computes
  `discImageSha256` (8.3).
- **U11.** Confirm whether an identity-mismatch failure kind can be added without
  changing the mapping in `src/PSXRecomp.Core/Diagnostics/DiagnosticAdapter.cs`.
- **U12.** Verify that the set-fingerprint build target works under the repository's
  native build and CI workflows (`.github/workflows/ci.yml`), which were not read.
- **U13.** Migrate the test callers of `RunFromChd` and `RunFromIsoImage` to a
  helper that computes a real SHA-256 (8.3). Count the callers that pass a constant.
- **U14.** Reachability check for `NonAnalysis` files (4.2). A Roslyn reference
  analysis from each stage's entry methods must show that no `NonAnalysis` file is
  referenced. Until it exists, closure is only a classification guarantee.
- **U15.** Choose the location and format of the committed golden entries used by
  V-CI1, and confirm they contain only synthetic-input outputs.
- **U16.** Audit consumers of `EvidenceType` for any that treat `AIPrediction` as
  evidence (8.1).
- **U17.** Confirm the dependency-input capture: how `obj/project.assets.json` is
  read by the build target, and that transitive versions are included (4.2).
- **U18.** Plan the `Analyze` split of N12a and N12d (4.7). It is a prerequisite for
  caching N12a alone without a rebuild in N14.
- **U19.** Enumerate the exact `Analysis/Contracts/` files referenced by the
  `setAnalysis` types (4.2). The Roslyn analysis of U14 can produce this list.

## 10. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact
  schemas.
- No cache implementation. This document is the design only.
- No AI result producer.
- No change to project file format.
- No stronger identity than full SHA-256 for inputs.
- No ADR is created here. ADRs are required before implementation (8.4).
