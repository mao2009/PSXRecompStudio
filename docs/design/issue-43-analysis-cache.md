# Issue #43: Incremental Analysis and Cache (design)

- Status: Proposed (design only; no implementation in this change)
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Scope: a design for the analysis cache. It does not change the decoder,
  the analyzer, the artifact format, or any public API.

## Labels

Every claim carries one of these labels:

- **CONFIRMED**: read directly in the source at the cited `file:line` during this
  design pass.
- **OBSERVED**: taken from a search result or a fetched page, not from a line
  read. Absence claims ("no code matches") are OBSERVED.
- **PREDICTED**: a consequence of the design that no test has exercised yet.
- **UNVERIFIED**: not checked. Each one must be resolved before the
  implementation is accepted.

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
- **OBSERVED** No AI analysis result type or producer exists in `src/`
  (search for AI/LLM analysis result types returned no matches). Issue #43's
  "AI analysis result reuse" scope therefore has nothing to reuse yet.
- **CONFIRMED** Both CLI and GUI call the same pipeline entry points, so a shared
  cache has two callers to serve:
  - CLI: `src/PSXRecomp.Cli/CliInput.cs:115` (`RomAnalysisPipeline.RunFromChd`).
  - GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`
    (`RunFromIsoImage`) and `:117` (`RunFromChd`).
  Neither caller persists artifacts. Persistence lives in the test-project flow
  driver (see 2.2).

### 1.2 The YAML instruction definition

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml` declares
  `meta.version: "1.0"` at line 11 and is documented as the "Machine-readable SSOT
  for Decoder, Interpreter, Recompiler, Test Generator, Debugger, MCP" (line 2).
- **OBSERVED** No code parses that file at runtime or build time. A search of
  `*.cs`, `*.csproj`, `*.props`, `*.targets`, `*.ps1`, `*.sh`, and `*.py` for
  `yaml`, `YamlDotNet`, `r3000a-instructions`, or `instructions.yaml` finds only
  the CI workflow-lint script and documentation references. The YAML is not an
  input to any analysis output.
- **CONFIRMED** The decoder is hand-written: `R3000aDecoder` is a static class of
  opcode-field constants (`src/PSXRecomp.Core/Cpu/R3000aDecoder.cs:6-8`,
  `:9-40`), and `Decode(uint)` is at `:70`.
- **CONFIRMED** Tests mention the YAML only in comments and names
  (`src/PSXRecomp.Tests/Cpu/R3000aOpcodeTests.cs:55`,
  `src/PSXRecomp.Tests/Cpu/R3000aDecoderGoldenTests.cs:373`).

**Consequence:** today a YAML edit cannot change analysis output. Including the
YAML in the cache key now would only cause over-invalidation (see section 4.3).

### 1.3 Versioning gap

- **CONFIRMED** Artifact shape is versioned per document:
  `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`,
  `CoverageSchemaVersion = 1`
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-42`).
- **CONFIRMED** The schema file says a consumer "must be able to tell a schema
  change from an analysis change" (`AnalysisArtifactSchema.cs:14-16`), but no
  constant identifies the analysis engine's behavior. `ArtifactFixtureIdentity`
  carries only the disc, executable, and format identity, with no analyzer or
  decoder revision (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactFixtureIdentity.cs:15-40`).
- **OBSERVED** A search for `analyzerRevision`, `AnalyzerVersion`, or
  `AnalyzerIdentity` in `src/**` returns no analysis-artifact match. The only
  product revision string reaches diagnostics, not analysis artifacts
  (`src/PSXRecomp.Cli/RunCommand.cs:190`).
- **PREDICTED** A schema version does not detect a change in analysis logic. A
  decoder fix that changes a mnemonic would leave every `SchemaVersion` unchanged
  while changing every artifact. A new engine-level revision is therefore needed.

### 1.4 Project metadata

- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
- **UNVERIFIED** That no project field feeds analysis. Only the schema constants
  were read. Each project field must be classified before the key is final
  (see 3.2 and 9).

## 2. Dependency graph (producer chain)

### 2.1 Stage chain in `RomAnalysisPipeline`

`RunFromChd` and `RunFromIsoImage` converge on `RunFromIsoSectorReader`. The
chain below is in call order.

| # | Node | Inputs | Evidence |
|---|---|---|---|
| N1 | Input identity | image bytes or stream, `discImageSha256` | `RomAnalysisPipeline.cs:42-63`, `:78-104`; the hash is computed by the caller in `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:100-106` |
| N2 | CHD open | N1 | `RomAnalysisPipeline.cs:115-117` (`ChdReader.Open`) |
| N3 | ISO filesystem | N2 or raw ISO | `RomAnalysisPipeline.cs:128-129`, `:217` |
| N4 | SYSTEM.CNF and boot executable | N3 | boot path resolved in the stage block; executable hash at `:259` (`ComputeSha256`, `:576-579`) |
| N5 | PS-X EXE header and entry point | N4 | `RomAnalysisPipeline.cs:277-337` |
| N6 | Text region bounds | N5 | `RomAnalysisPipeline.cs:342-347` |
| N7 | Decode (linear sweep) | N6, `count`, `DefaultInstructionCount = 128` (`:31`) | `RomAnalysisPipeline.cs:351-355`, loop `:511-541`; `R3000aDecoder.Decode` at `:516` |
| N8 | Basic blocks and CFG | N7 | `RomAnalysisPipeline.cs:381`; `BasicBlockBuilder.cs:20`, decode at `:30` |
| N9 | Function discovery | N7 | `RomAnalysisPipeline.cs:404`; decode at `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:96` |
| N10 | BIOS call recognition | N7, N8, N9 | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, decode at `:176`, `:195` |
| N11 | Report assembly | N7-N10 | `RomAnalysisPipeline.cs:400-444` |
| N12 | Recompilation coverage | N11 (report), artifact identity | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:178-179`; `RealRomCoverageAnalyzer.cs:88` |
| N13 | Coverage probe | N12, lowering validator | `RealRomCoverageAnalyzer.cs:234-261`; `MipsToIrLowerer.LowerProgram` at `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176` (called at `RealRomCoverageAnalyzer.cs:251`) |
| N14 | Artifact documents | N11, N12 | `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/DeterministicArtifactBuilder.cs:74`, `:86-89`, `:101-106`, `:115-121`, `:123-127` |
| N15 | Persistence | N14 | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50` (`Directory.CreateDirectory`, `File.WriteAllBytes`) |

**Edge summary**

- N1 to N2 to N3 to N4 to N5 to N6 to N7 is a strict chain. Any change above a
  node changes every node below it.
- N7 is the fan-out point. N8, N9, and N10 each consume the full decoded list.
- N10 consumes the BIOS recognizer, which also depends on N8 and N9.
- N12 and N13 are optional: `RealRomAnalysisArtifacts.Coverage` is nullable
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomAnalysisArtifacts.cs:24`).
- N14 hashes every sibling document into `manifest.json`
  (`AnalysisManifestDocument.cs:22-23`, `ArtifactDocumentReference.Sha256` at
  `:41-42`). The manifest is already a content-addressed index of the outputs.

### 2.2 Who drives persistence

- **CONFIRMED** Persistence is in the test-project driver
  (`src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs`), not in
  `PSXRecomp.Core`. The driver calls the pipeline, then coverage, then
  `DeterministicArtifactBuilder.Build`, then the writer. The cache design must
  move or mirror this driver logic into a production component. That is a
  boundary decision, listed in section 9.
- **CONFIRMED** The `MANIFEST` and `COMPLETE` stages are recorded by the flow
  driver after persistence (`RomAnalysisPipeline.cs:23-25`). This is an existing
  commit-marker convention the cache can reuse.

### 2.3 Dependency graph (summary)

```
N1 identity --> N2 CHD --> N3 ISO --> N4 boot exe --> N5 EXE header
  --> N6 text bounds --> N7 decode  [R3000aDecoder, decode loop]
        |--> N8 basic blocks  [BasicBlockBuilder]
        |--> N9 functions     [FunctionDiscovery]
        |        \--> N10 BIOS calls  [BiosCallRecognizer; also needs N8]
        '--> N11 report (N7..N10)
               --> N12 coverage  --> N13 probe [MipsToIrLowerer]
               --> N14 documents --> N15 persist
```

Outside the chain: the YAML (no edge, section 1.2) and the project metadata
(no edge found, UNVERIFIED, section 1.4).

## 3. Cache key composition

### 3.1 Principle

A key identifies the exact content of a computation: all of its inputs and the
revision of every code path that produced it. Keys contain no filesystem path
(`ArtifactFixtureIdentity.cs:11-12` already forbids local paths, and the cache
extends that rule).

### 3.2 Components

A stage key is the SHA-256 of a canonical JSON object. The intended canonical
form is UTF-8, no BOM, LF, with sorted keys, reusing the `ArtifactJson` writer
used at `DeterministicArtifactBuilder.cs:96-98`. UNVERIFIED: that `ArtifactJson`
sorts object keys; this was not read. The key encoder must be tested for this
(T1). Its fields:

| Field | Source | Notes |
|---|---|---|
| `keyFormat` | cache constant | Bumped when the key layout changes |
| `stage` | stage id | One of N2 through N15 |
| `engineRevision` | new constant, e.g. `AnalysisEngineRevision` | Manually bumped on any analysis-behavior change (section 4.2) |
| `decoderFingerprint` | new (section 4.1) | Covers N7 and everything it feeds |
| `loweringFingerprint` | new | Covers N13 only; the probe result depends on `MipsToIrLowerer` |
| `schemaVersions` | `AnalysisArtifactSchema.*SchemaVersion` | Already exist (section 1.3) |
| `discImageSha256` | N1 | Formal input identity (`ArtifactFixtureIdentity.cs:23-24`) |
| `discImageSizeBytes` | N1 | Redundant with the hash, kept as a cheap cross-check |
| `executableSha256`, `executableSizeBytes` | N4 | Available from `ArtifactFixtureIdentity.cs:35-39` |
| `entryPoint`, `textEnd` | N5, N6 | Decode bounds |
| `instructionCount` | caller | Defaults to `DefaultInstructionCount` (`RomAnalysisPipeline.cs:31`) |
| `inputContainer` | N2 vs raw ISO | `"CHD"` or `"ISO"`; see 3.4 |
| `projectFields` | section 3.3 | Only fields that feed analysis; empty today |
| `upstreamKeys` | prior stage keys | Makes the key a Merkle-style chain |

### 3.3 Project fields

- **PREDICTED** Project metadata affects analysis only if a field is read by N1
  through N14. Each such field must be listed explicitly in `projectFields`.
  Fields that do not feed analysis must not be in the key, or every project edit
  would invalidate the cache.
- **UNVERIFIED** The full set of project fields was not enumerated. This is an
  open item (section 9).

### 3.4 Container format

- **CONFIRMED** The pipeline records a skip when there is no container
  (`RomAnalysisPipeline.cs:163`). The hash is of the file bytes
  (`RealRomAnalyzer.cs:102-104`), so a raw ISO and a CHD with identical content
  have different `discImageSha256` values.
- **PREDICTED** By construction, such a pair therefore misses today. It is a
  consequence of the byte-level identity, not a measured result.
- **PREDICTED** Including `inputContainer` makes the separation explicit and
  guards against an identity collision if a future identity uses decoded
  content. It costs nothing in the common case.

### 3.5 Example key (format only, not a real value)

```
stage=N7 decode
engineRevision=<rev>
decoderFingerprint=<hex>
schemaVersions={report:2,instructions:1,cfg:1,...}
discImageSha256=<hex>
executableSha256=<hex>
entryPoint=<u32>  textEnd=<u32>  instructionCount=<int>
inputContainer=CHD
upstreamKeys={N6:<hex>}
```

## 4. Invalidation rules

Each rule names the stage keys that change. "Downstream" means every node in
section 2.1 that consumes the changed node.

### 4.1 Decoder change (`R3000aDecoder`, decode loop)

- **CONFIRMED** The decoder is the only code that maps raw words to
  mnemonics, format, and control flow (`R3000aDecoder.cs:70`; consumed at
  `RomAnalysisPipeline.cs:516`, `BasicBlockBuilder.cs:30`,
  `FunctionDiscovery.cs:96`, `BiosCallRecognition.cs:176`).
- **Rule D1.** Any change to `R3000aDecoder` or to the decode loop
  (`RomAnalysisPipeline.cs:501-543`) bumps `decoderFingerprint`. Invalidated:
  N7 and all of N8 through N15.
- **Rule D2.** N1 through N6 are not invalidated. They do not call the decoder.
- **Fingerprint design (PREDICTED, UNVERIFIED feasibility).** A hash of the
  source file is not used: source hashes vary with comments, and shipped
  binaries do not have the source. Proposed: a build-time or test-time
  fingerprint of decoder behavior, computed over a fixed, enumerated corpus of
  words. The corpus enumerates the 6-bit opcode and funct fields with fixed
  register and immediate patterns, plus the golden vectors already in
  `R3000aDecoderGoldenTests.cs`. The fingerprint is a SHA-256 over
  `(opcode, funct, format, controlFlow, delaySlot)` tuples.
- **PREDICTED** A behavior fingerprint over a finite corpus can miss a change
  that the corpus does not cover (for example, a change that depends on a
  register field only). It is therefore a guard, not a proof. The primary
  mechanism is the manually bumped `engineRevision` (4.2).

### 4.2 Analyzer change (`engineRevision`)

Analyzer behavior is defined by every file that contributes to the artifacts.
The set includes at least:

- `src/PSXRecomp.Core/DiscImage/` (pipeline, `BasicBlockBuilder`,
  `FunctionDiscovery`, `BiosCallRecognition`, `DiscImageAnalysisReport`)
- `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/` (builder, JSON writer,
  schema constants)
- `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs` (N12 and N13)

- **Rule A1.** Any change to output for an unchanged input bumps
  `engineRevision`. Invalidated: every stage key that carries it. That is the
  whole cache.
- **Rule A2.** Pure refactors that keep output byte-identical do not bump it.
  Equivalence is checked by the golden tests in section 7.
- **PREDICTED** A manual bump relies on discipline. A missed bump is the main
  failure mode. Section 7 adds a golden-output test that fails when output
  changes without a revision bump, which turns a silent error into a visible
  one.

### 4.3 YAML change (`config/cpu/r3000a-instructions.yaml`)

- **CONFIRMED** The YAML has no runtime or build-time reader (section 1.2).
  Editing it changes no analysis output today.
- **Rule Y1.** While no code reads the YAML, the YAML is not in the key. A
  comment-only edit must not invalidate the cache.
- **Rule Y2.** If a future change generates decoder tables from the YAML, the
  generated code enters `decoderFingerprint` through D1. The YAML itself should
  not be hashed raw, because whitespace and comments would over-invalidate. If
  a fingerprint of the YAML is needed, it is the SHA-256 of the parsed,
  canonicalized structure.
- **UNVERIFIED** Whether any generator exists outside the searched file types
  (for example, a `.rs` build script or `.cpp` generator in
  `src/PSXRecomp.Native`). The Rust and C++ decoders exist
  (`src/PSXRecomp.Native/rust/src/cpu_decode.rs`,
  `src/PSXRecomp.Native/src/psx_cpu_decode.cpp`); whether they read the YAML was
  not checked.

### 4.4 Project change

- **Rule P1.** A project edit invalidates only stages whose `projectFields`
  changed. Today that set is empty, so no project edit invalidates the cache.
- **Rule P2.** `ProjectFormatVersion` (`ProjectSchema.cs:19`) enters the key
  only if the project format changes what the analysis reads.

### 4.5 Input change

- **Rule I1.** A changed disc image or executable changes N1 or N4 and therefore
  everything downstream. This is a miss by construction, not an invalidation.

### 4.6 Lowering change (`MipsToIrLowerer`)

- **Rule L1.** A change to `MipsToIrLowerer` or its validator bumps
  `loweringFingerprint`. Invalidated: N13 and N14 (coverage and the documents
  that contain it). N7 through N11 are not invalidated.
- **CONFIRMED** Coverage is optional in the artifact set
  (`RealRomAnalysisArtifacts.cs:24`), so N12 and N13 can be skipped without
  affecting the required documents.

## 5. Partial re-analysis units

- **Unit of reuse: the pipeline stage (N2 through N15).** A cache hit for a
  stage replaces that stage's computation. A miss recomputes it and every
  downstream stage.
- **Decode prefix reuse (N7).** The decode loop is a linear sweep from
  `entryPoint` with a bound of `count` and `textEnd`
  (`RomAnalysisPipeline.cs:507-511`). A decode for `count = N` is a prefix of a
  decode for `count = M > N` with the same key fields. PREDICTED: a request for
  more instructions can reuse the prefix. UNVERIFIED: whether the report
  computed from the prefix is identical to a fresh run at `M`. Downstream N8 to
  N10 consume the whole list, so prefix reuse stops at N7.
- **No sub-stage reuse below N7 at this stage.** BasicBlockBuilder, function
  discovery, and BIOS recognition each consume the full decoded list. PREDICTED:
  per-block or per-function reuse would need an explicit dependency model
  between blocks. It is out of scope for the first version.
- **Unit for coverage.** Probe results are keyed by raw word
  (`RealRomCoverageAnalyzer.cs:231-237`). That is a content key that a
  persistent cache can reuse across fixtures, as long as `loweringFingerprint`
  is part of the key.

## 6. Fail-closed handling of incomplete or corrupted cache

The rule is that a cache entry is used only if it is proven complete and
correct. Anything else is a miss. A miss is never an error, and a miss never
produces output that a cold run would not produce.

### 6.1 Entry layout

Each stage entry is a directory or a pair of files:

- `header`: format version, `key` (the full stage key), `payloadSha256`,
  `payloadSizeBytes`, `engineRevision`, `decoderFingerprint`,
  `loweringFingerprint`, and the `schemaVersions` in effect.
- `payload`: the canonical stage output.
- `COMPLETE`: a marker written last. Its presence means the entry is whole.
  This reuses the commit-marker convention in `RomAnalysisPipeline.cs:23-25`.

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the entry's
  directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, and only after W3 succeeds for every file.
- **W5.** Never write an entry for a stage whose outcome is not a pass
  (`RomAnalysisStageRecorder` already records per-stage pass or fail). A failed
  run leaves no partial entry for the failing stage.
- **W6.** Concurrent writers of the same key write the same content. A rename
  that fails because the target exists is treated as success only if the
  existing header's `payloadSha256` matches. Otherwise the entry is quarantined
  (6.4) and the write is retried once.

### 6.3 Read protocol (fail closed)

An entry is used only if all of the following hold. Any failure is a miss:

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses and its format version is supported.
- **R3.** `header.key` equals the key computed from the current inputs.
  A stale or wrong-key entry in the right directory is rejected.
- **R4.** `payload` size equals `payloadSizeBytes`.
- **R5.** SHA-256 of `payload` equals `payloadSha256`. This check is mandatory,
  because a truncated or bit-flipped payload is the main corruption case.
- **R6.** The payload deserializes under the current schema versions.
- **R7.** Any exception during R1 through R6 is caught and treated as a miss.
  A cache read never throws into the analysis path.

### 6.4 Quarantine and recovery

- **Q1.** A header or payload that fails R2 through R6 is renamed to a
  quarantine name. It is not deleted, so it can be inspected. Its name includes
  the failing check, not a clock value, so tests stay deterministic.
- **Q2.** After a miss, the stage is recomputed, and the fresh result is written
  using the write protocol. Recovery is therefore automatic and needs no user
  action.
- **Q3.** Cache failures are reported through the existing analyzer diagnostic
  codes (`DiagnosticCodes.AnalyzerAnalysisFailed`, `AnalyzerReportFailed`, at
  `src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`) as a warning on the
  run, not a failure. A new diagnostic code is UNVERIFIED as needed.
- **Q4.** A verify mode recomputes a hit and compares it with the cached payload.
  A mismatch is a corruption or a missed revision bump. It is reported as a
  hard failure, so the cache never silently disagrees with a cold run.

### 6.5 Input identity is never cached as truth

- **I2.** The disc image SHA-256 is computed on every run from the full file
  (`RealRomAnalyzer.cs:102-104`). A size-and-mtime shortcut is rejected because
  it is not a content identity.
- **PREDICTED** Full hashing dominates the cost of a warm run on large images.
  UNVERIFIED: the actual cost. A benchmark is needed before choosing a stronger
  shortcut. Any shortcut must still end in a content hash before a hit.

## 7. Deterministic test plan

All tests use synthetic inputs from `src/PSXRecomp.Tests/RealRomAnalysis/`:
`SyntheticDiscBuilder.cs`, `SyntheticChdBuilder.cs`, and
`SyntheticIsoBothEndianTests.cs` (CONFIRMED that these files exist; their APIs
were not reviewed, UNVERIFIED). No BIOS, ROM, or CHD content is used. Clocks
are injected. Temporary paths are not asserted. Filesystem access goes through
a fake or a temp directory whose name is not asserted.

| ID | Test | Asserts |
|---|---|---|
| T1 | Key stability | Same inputs produce the same key string across runs (golden key value) |
| T2 | Key sensitivity matrix | Changing each of `discImageSha256`, `instructionCount`, `engineRevision`, `decoderFingerprint`, `loweringFingerprint`, a schema version, or `inputContainer` changes the key of exactly the expected stages |
| T3 | Project non-analysis edit | Changing a project field outside `projectFields` leaves every key unchanged |
| T4 | Decoder bump scope | Bumping `decoderFingerprint` invalidates N7 through N15 and leaves N1 through N6 hits |
| T5 | YAML comment-only edit | No key changes (Rule Y1) |
| T6 | Corruption matrix | Each of: zero-length file, truncated payload, flipped byte, missing `COMPLETE`, wrong `key` in header, unsupported format version, malformed header JSON, schema-version mismatch, produces a miss, a quarantine, and output byte-identical to a cold run |
| T7 | Partial failure | A pipeline failure at N8 writes no entry for N8 or later; entries for N2 through N7 are the only ones written |
| T8 | Warm equals cold | For each synthetic image, the warm run's artifact files (`ArtifactFile.ToUtf8Bytes`) equal the cold run's, byte for byte |
| T9 | Verify mode | A deliberately corrupted-but-valid-header payload is reported as a hard failure in verify mode and as a silent miss in normal mode with a warning |
| T10 | Concurrent write | Two writers with the same key leave one complete entry; a fake filesystem that interleaves the renames does not produce a torn entry |
| T11 | Shared store | The CLI entry point and the GUI entry point compute the same key for the same input and read the same entry |
| T12 | No path in key | No key or entry contains an absolute path (`PathRedactor.cs` exists under `src/PSXRecomp.Tests/RealRomAnalysis/`; its API UNVERIFIED) |
| T13 | Golden-output guard | A change to a golden document's bytes without an `engineRevision` bump fails the test (Rule A1) |
| T14 | Prefix reuse | A decode cached at `count = N` reused for `count = M` produces the same N7 output as a cold decode at `M` (UNVERIFIED expectation, to be confirmed by the test) |

Determinism rules for every test:

- No wall-clock values in keys, headers, or assertions.
- No dependence on iteration order of unsorted collections (the artifact
  builder already sorts files: `DeterministicArtifactBuilder.cs:127`).
- No reliance on the machine's temp directory name.

## 8. Mapping to Issue #43 acceptance criteria

The issue's acceptance criteria are (OBSERVED, from a fetched summary of the
issue; the criteria are unchecked):

1. Define dependencies between analysis artifacts. **Covered by section 2.**
2. Define the cache key. **Covered by section 3.**
3. Define invalidation rules for Analyzer, Decoder, Project, and YAML changes.
   **Covered by section 4 (rules A1, D1, P1, Y1, Y2).**
4. Define the unit of partial re-analysis. **Covered by section 5.**
5. Define conditions for reusing AI results. **Section 8.1 below.**
6. Define recovery for a corrupted cache. **Covered by section 6.**
7. Settle one cache shared by CLI and GUI. **Section 8.2 below.**

### 8.1 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists today (section 1.1).
- **Rule AI1.** An AI result is advisory. It is never stored as analysis
  output and never appears in an analysis document. It is stored in its own
  entry type.
- **Rule AI2.** An AI result is reused only when its key matches: the upstream
  artifact hashes it was computed from, the prompt or template revision, the
  model identifier, and the model settings. Any mismatch is a miss.
- **Rule AI3.** An AI result is invalidated when any analysis stage it depends
  on is invalidated. Because it is keyed on upstream hashes, this follows
  automatically.
- **PREDICTED** Tying an AI result to analyzer evidence labels could make it look
  like analyzer output. The evidence taxonomy
  (`src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs`, with
  `AnalyzerFinding` at line 16) should be checked before any AI result is given
  an evidence type. UNVERIFIED: the full enum.

### 8.2 Shared CLI and GUI cache

- **CONFIRMED** Both front ends call the same pipeline (section 1.1), so the
  key function and the store format are shared code.
- **Policy S1.** One store format and one key function, in `PSXRecomp.Core`.
  The CLI and GUI pass only a store root; no front end defines its own key.
- **Policy S2.** The store root is supplied by the host. Its location is not
  decided here, because the choice depends on the existing project layout
  (UNVERIFIED; see section 9).
- **Policy S3.** Concurrency across processes uses the write protocol in 6.2
  (W6). Readers never see an incomplete entry, because `COMPLETE` is last.
- **PREDICTED** Sharing is safe only because keys contain no path and no
  front-end state. Any front-end field added to a key later must be reviewed
  against this rule.

## 9. Open items and UNVERIFIED list

- **U1.** Enumerate every project field and classify it as analysis-relevant or
  not (section 3.3, rule P1).
- **U2.** Confirm that no build script or generator reads the YAML in
  `src/PSXRecomp.Native` or in any non-searched file type (rule Y2).
- **U3.** Decide where the production driver lives. Persistence currently sits
  in the test project (section 2.2). Moving it into `PSXRecomp.Core` is a
  boundary decision with its own ADR.
- **U4.** Decide the store root. Options and their trade-offs are not evaluated
  here (S2).
- **U5.** Benchmark full-image SHA-256 on a large synthetic image to size the
  warm-run cost (6.5, I2).
- **U6.** Confirm the decode-prefix claim (T14) and the fingerprint corpus
  coverage (4.1).
- **U7.** Read `PathRedactor.cs` and `SyntheticDiscBuilder.cs` APIs before the
  test plan is implemented (section 7).
- **U8.** Check whether `DeterministicArtifactTests.cs` already pins golden bytes;
  if it does, T13 may be partly covered (UNVERIFIED).
- **U9.** Decide whether `loweringFingerprint` covers only `MipsToIrLowerer` or
  the whole Recompiler IR contract (`docs/development/recompiler-ir-contract.md`
  exists; not read).

## 10. Non-goals

- No change to the decoder, the YAML, the analyzer, or the artifact schemas.
- No cache implementation. This document is the design only.
- No AI result producer.
- No change to project file format.
- No stronger identity than full SHA-256 for inputs.
