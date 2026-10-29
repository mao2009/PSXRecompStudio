# Issue #43: Incremental Analysis and Cache (design)

- Status: Proposed (design only; no implementation in this change)
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r2, addresses an independent review of `6d038a0` (3 critical, 5 major,
  4 minor findings). Each finding is resolved in the section it names.
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
  - CLI: `src/PSXRecomp.Cli/CliInput.cs:100-116` (`LoadChdWithIdentity`: hashes
    the file in one open, then runs `RomAnalysisPipeline.RunFromChd` on a second
    open at `:115`).
  - GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`
    (`RunFromIsoImage`) and `:117` (`RunFromChd`). The GUI takes
    `discImageSha256` as a parameter (`:110-117`); who computes it on the GUI
    side was not read (UNVERIFIED).
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
  `AnalyzerIdentity` in `src/**` returns no analysis-artifact match. The only
  product revision string reaches diagnostics, not analysis artifacts
  (`src/PSXRecomp.Cli/RunCommand.cs:190`).
- **PREDICTED** A schema version does not detect a change in analysis logic. A
  decoder fix that changes a mnemonic would leave every `SchemaVersion` unchanged
  while changing every artifact. A manually maintained revision constant is not a
  sufficient guard either (see 4.2, Rule A1). The analyzer identity therefore
  needs a mechanically derived fingerprint.

### 1.4 Project metadata

- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
- **UNVERIFIED** That no project field feeds analysis. Only the schema constants
  were read. Each project field must be classified before the key is final
  (see 3.3 and 9).

## 2. Dependency graph (producer chain)

### 2.1 Stage chain in `RomAnalysisPipeline`

`RunFromChd` and `RunFromIsoImage` converge on `RunFromIsoSectorReader`. The
chain below is in call order.

| # | Node | Inputs | Evidence |
|---|---|---|---|
| N1 | Input identity | image bytes or stream, `discImageSha256` | Caller-supplied hash is only checked for non-emptiness: `RomAnalysisPipeline.cs:93-98` (stream), `:194-198` (bytes). The hash is computed by the test driver at `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:100-106`. Revised in 8.3: the pipeline verifies it. |
| N2 | CHD open | N1 | `RomAnalysisPipeline.cs:115-117` (`ChdReader.Open`) |
| N3 | ISO filesystem | N2 or raw ISO | `RomAnalysisPipeline.cs:128-129`, `:217` |
| N4 | SYSTEM.CNF and boot executable | N3 | boot path resolved in the stage block; executable hash at `:259` (`ComputeSha256`, `:576-579`) |
| N5 | PS-X EXE header and entry point | N4 | `RomAnalysisPipeline.cs:277-337` |
| N6 | Text region bounds | N5 | `RomAnalysisPipeline.cs:342-347` |
| N7 | Decode (linear sweep) | N6, `textEnd`; `count` applied as a prefix (5) | `RomAnalysisPipeline.cs:351-355`, loop `:507-541`; `R3000aDecoder.Decode` at `:516`; formatter at `:522-523` |
| N8 | Basic blocks and CFG | N7, `count` | `RomAnalysisPipeline.cs:381`; `BasicBlockBuilder.cs:20`, decode at `:30` |
| N9 | Function discovery | N7, `count` | `RomAnalysisPipeline.cs:404`; decode at `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:96` |
| N10 | BIOS call recognition | N7, N8, N9 | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, decode at `:176`, `:195` |
| N11 | Report assembly | N7-N10 | `RomAnalysisPipeline.cs:400-444`; call-candidate count decodes at `:553` |
| N12a | Recompilation classification | N11 (report), lowering validator | `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:88` (`Analyze`), per-instruction `Classify` and `ProbeLowerable` at `:223` and `:234-261`; `MipsToIrLowerer.LowerProgram` at `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176`, called at `RealRomCoverageAnalyzer.cs:251` |
| N12d | Differential section | caller-supplied validations | `RealRomCoverageAnalyzer.cs:85-88` (doc), `:185` (`Differential = BuildDifferential(validations)`), `:377-382` (`BuildDifferential`) |
| N14 | Artifact documents | N11, N12a, N12d | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:178-181`; `DeterministicArtifactBuilder.cs:74`, `:86-89`, `:101-106`, `:115-121`, `:123-127` |
| N15 | Persistence | N14 | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50` (`Directory.CreateDirectory`, `File.WriteAllBytes`) |

**Edge summary**

- N1 to N2 to N3 to N4 to N5 to N6 to N7 is a strict chain. Any change above a
  node changes every node below it.
- N7 is the fan-out point. N8, N9, and N10 each consume the decoded list.
- N10 consumes the BIOS recognizer, which also depends on N8 and N9.
- N12a consumes every decoded instruction through `Classify` (`:223`), so the
  lowering validator is a real input to the coverage classification, not only
  to the probe.
- N12d is an input to the document assembly only. It is not a function of the
  analysis (see 4.7 and 5).
- N12a and N12d are optional: `RealRomAnalysisArtifacts.Coverage` is nullable
  (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomAnalysisArtifacts.cs:24`).
- N14 hashes every sibling document into `manifest.json`
  (`AnalysisManifestDocument.cs:22-23`, `ArtifactDocumentReference.Sha256` at
  `:41-42`).

### 2.2 Who drives persistence

- **CONFIRMED** Persistence is in the test-project driver
  (`src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs`), not in
  `PSXRecomp.Core`. The driver calls the pipeline, then coverage, then
  `DeterministicArtifactBuilder.Build`, then the writer. The cache must move or
  mirror this driver logic into a production component (see 8.4 and U3).
- **CONFIRMED** The `MANIFEST` and `COMPLETE` stages are recorded by the flow
  driver after persistence (`RomAnalysisPipeline.cs:23-25`). This is an existing
  commit-marker convention the cache can reuse.

### 2.3 Dependency graph (summary)

```
N1 identity --> N2 CHD --> N3 ISO --> N4 boot exe --> N5 EXE header
  --> N6 text bounds --> N7 decode  [R3000aDecoder, decode loop, formatter]
        |--> N8 basic blocks  [BasicBlockBuilder]
        |--> N9 functions     [FunctionDiscovery]
        |        \--> N10 BIOS calls  [BiosCallRecognizer; also needs N8]
        '--> N11 report (N7..N10)
               --> N12a classification [MipsToIrLowerer via Classify/probe]
               --> N12d differential   [caller validations; never cached]
               --> N14 documents --> N15 persist
```

Outside the chain: the YAML (no edge, section 1.2) and the project metadata
(no edge found, UNVERIFIED, section 1.4).

## 3. Cache key composition

### 3.1 Principle

A key identifies the exact content of a computation: all of its inputs and the
analyzer code that produced it. Keys contain no filesystem path
(`ArtifactFixtureIdentity.cs:11-12` already forbids local paths, and the cache
extends that rule).

### 3.2 Components

Cache units are N7, N8, N9, N10, N11, and N12a. N14 is recomputed on every run
(it is cheap), and N12d is never cached (rule C2).

**Key encoding (MAJOR-3 resolution).** A stage key is the SHA-256 of a canonical
JSON object produced by a dedicated key encoder, not by `ArtifactJson`.
`ArtifactJson.CanonicalOptions` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:34-38`)
sets `WriteIndented = true` and camelCase naming, and its documented property
order is the record declaration order (`ArtifactJson.cs:23`). It does not sort
keys, so reusing it would make a key depend on declaration order. The key
encoder instead:

- writes UTF-8, no BOM, LF, no insignificant whitespace;
- emits object members sorted by ordinal comparison of the member name;
- supports only strings, integers, booleans, null, and arrays (no floats, no
  culture-sensitive formatting);
- is independent of record declaration order.

The encoder is tested by T1 (sorted order, and key unchanged when a record's
members are reordered in source).

**Fields.** Fields marked (N7) apply to the decode stage and every stage below.

| Field | Source | Applies to | Notes |
|---|---|---|---|
| `keyFormat` | cache constant | all | Bumped when the key layout changes |
| `stage` | stage id | all | One of N7, N8, N9, N10, N11, N12a |
| `analyzerFingerprint` | build-time, section 4.2 | all | **Mandatory** correctness input (CRITICAL-3). Replaces reliance on any manual constant |
| `engineRevision` | manual constant | all | Human-readable label only. It is not a correctness input |
| `decoderFingerprint` | behavioral guard, section 4.1 | N7 and below | Covers the decoder and the formatter output (MAJOR-2) |
| `loweringFingerprint` | build-time (section 4.6) | N12a only | Covers the lowering validator used by `Classify` and the probe (CRITICAL-1) |
| `schemaVersions` | `AnalysisArtifactSchema.*SchemaVersion` and `FunctionProvenanceSchemaVersion` | all | Existing constants (section 1.3) |
| `discImageSha256` | N1, verified by the pipeline (8.3) | all | Formal input identity (`ArtifactFixtureIdentity.cs:23-24`) |
| `executableSha256`, `executableSizeBytes` | N4 | all | From `ArtifactFixtureIdentity.cs:35-39` |
| `entryPoint`, `textEnd` | N5, N6 | N7 and below | Decode bounds |
| `instructionCount` | caller | **N8 and below only** | Not part of the N7 key (MAJOR-1, see 5) |
| `inputContainer` | N2 vs raw ISO | all | `"CHD"` or `"ISO"`; see 3.4 |
| `projectFields` | section 3.3 | per classification | Only fields that feed analysis; empty today |
| `upstreamKeys` | prior stage keys | per stage | Merkle-style chain |

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
analyzerFingerprint=<hex>
decoderFingerprint=<hex>
schemaVersions={report:2,instructions:1,cfg:1,...}
discImageSha256=<hex>
executableSha256=<hex>
entryPoint=<u32>  textEnd=<u32>
inputContainer=CHD
upstreamKeys={N6:<hex>}
```

The N7 key has no `instructionCount`.

## 4. Invalidation rules

Each rule names the stage keys that change. "Downstream" means every node in
section 2.1 that consumes the changed node.

### 4.1 Decoder and formatter change

- **CONFIRMED** The decoder is the code that maps raw words to mnemonics,
  format, control flow, and delay-slot kind (`R3000aDecoder.cs:70`). Its consumers
  are:
  - `RomAnalysisPipeline.cs:516` (`DecodeInstructions`, N7);
  - `RomAnalysisPipeline.cs:553` (`CountCallReturnCandidates`, N11);
  - `BasicBlockBuilder.cs:30` (N8);
  - `FunctionDiscovery.cs:96` (N9);
  - `BiosCallRecognition.cs:176` and `:195` (N10, including the delay-slot decode).
- **CONFIRMED** The text output also depends on `MipsInstructionFormatter`.
  `RomAnalysisPipeline.cs:522-523` copies `FormatMnemonic` and `FormatOperands`
  into `DecodedInstruction.Mnemonic` and `Operands`. `FormatOperands` is at
  `src/PSXRecomp.Core/DiscImage/MipsInstructionFormatter.cs:95`, and the register
  name table is at `:12-17` (MAJOR-2).
- **Rule D1.** Any change to `R3000aDecoder`, `MipsInstructionFormatter`, or the
  decode loop (`RomAnalysisPipeline.cs:501-543`) changes `decoderFingerprint`.
  Invalidated: N7 and all of N8 through N14. N1 through N6 are not invalidated.
- **Rule D1a.** The behavioral fingerprint tuple includes the formatter output
  (`mnemonic`, `operands`) as well as `(opcode, funct, format, controlFlow,
  delaySlot)`, so a formatter-only change is detected by the guard.
- **Rule D2.** N1 through N6 are not invalidated. They do not call the decoder.
- **Fingerprint design (PREDICTED, UNVERIFIED feasibility).** Proposed: a
  build-time or test-time fingerprint of decoder and formatter behavior, computed
  over a fixed, enumerated corpus of words. The corpus enumerates the 6-bit
  opcode and funct fields with fixed register and immediate patterns, plus the
  golden vectors in `R3000aDecoderGoldenTests.cs`.
- **PREDICTED** A behavioral fingerprint over a finite corpus can miss a change
  that the corpus does not cover. It is therefore a secondary guard. The
  mandatory correctness input is `analyzerFingerprint` (4.2), which covers the
  decoder and formatter source automatically.

### 4.2 Analyzer change (`analyzerFingerprint`, mandatory)

**Resolution of CRITICAL-3.** Reliance on a manually bumped revision is rejected
as a fail-safe. A missed bump would yield stale entries that pass R1 through R6
(section 6.3), because those checks prove only self-consistency. The
correctness input is therefore a fingerprint derived mechanically from the
analyzer source.

- **Definition.** `analyzerFingerprint` is the SHA-256 of the analyzer source
  set. The source set is every file under `src/PSXRecomp.Core/**/*.cs`, plus the
  test-project flow driver (`src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs`
  and `RealRomArtifactWriter.cs`) while that driver still defines artifact
  content (section 2.2, U3). Files are sorted by repository-relative path. Each
  file's line endings are normalized to LF before hashing.
- **Generation.** A build target computes the hash and emits it as a generated
  constant in `PSXRecomp.Core`. A build from the same source yields the same
  constant. No file is curated by hand, so a new analyzer file is covered
  without anyone remembering to add it.
- **Rule A1 (mandatory).** Any change to the source set changes
  `analyzerFingerprint`. Invalidated: every stage key that carries it, which is the
  whole cache.
- **Rule A1a (over-invalidation accepted).** Comment edits, refactors, and edits
  to unrelated Core files also change the fingerprint. This is a deliberate
  fail-safe. Narrowing the set to a curated list is allowed only with a test that
  proves the set is closed under the analysis call graph. That test does not exist
  yet (UNVERIFIED feasibility).
- **Rule A2.** `engineRevision` is a label only. It is not a correctness input,
  so it can be left unchanged without risk.
- **Line-ending hazard.** A checkout with different line endings would change the
  hash across machines. Normalizing to LF before hashing (above) prevents a
  cross-machine miss. The repository currently emits a CRLF warning for files
  under `docs/design/` on this machine, which confirms the hazard is real
  (CONFIRMED, from the git output during the commit of 6d038a0).
- **Second line: verify mode in CI (Q4).** A CI job runs the cache in verify mode
  on a fixed synthetic corpus (no BIOS, ROM, or CHD content) and fails on any
  hit that disagrees with a cold run. This catches a bug in the fingerprint
  itself, which the mechanical hash alone cannot.

### 4.3 YAML change (`config/cpu/r3000a-instructions.yaml`)

- **CONFIRMED** The YAML has no runtime or build-time reader (section 1.2).
  Editing it changes no analysis output today.
- **Rule Y1 (current-state rule).** While no code reads the YAML, the YAML is not
  in the key. A comment-only edit must not invalidate the cache. **Revisit this
  rule when a generator or runtime reader of `config/cpu/r3000a-instructions.yaml`
  lands.** The consumers named in `docs/adr/002-instruction-definition-yaml.md`
  (Decoder, Interpreter, Recompiler, and others) are the trigger.
- **Rule Y2.** If a future change generates decoder tables from the YAML, the
  generated code is covered by `analyzerFingerprint` once it is in the source set.
  The YAML itself should not be hashed raw, because whitespace and comments would
  over-invalidate. If a YAML fingerprint is needed, it is the SHA-256 of the
  parsed, canonicalized structure. The YAML's governing decisions are
  `docs/adr/001-cpu-specification-ssot.md` and `docs/adr/002-instruction-definition-yaml.md`.
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

**Resolution of CRITICAL-1.** The coverage classification itself depends on the
lowering validator, not only the probe.

- **CONFIRMED** `Classify` calls `ProbeLowerable` for every instruction that
  reaches that branch (`RealRomCoverageAnalyzer.cs:223`), and `ProbeLowerable`
  calls `MipsToIrLowerer.LowerProgram` (`:251`). The `Lowerable` versus
  `UnsupportedInstruction` class of every decoded instruction is therefore a
  function of the lowering validator, and that class is written into the coverage
  document.
- **Rule L1.** A change to `MipsToIrLowerer`, its validator, or the probe window
  construction (`RealRomCoverageAnalyzer.cs:234-246`) changes
  `loweringFingerprint`. Invalidated: N12a, and N14 (which embeds N12a). N7
  through N11 are not invalidated.
- **Build-time generation.** `loweringFingerprint` is derived the same way as
  `analyzerFingerprint` (4.2), over the lowering source set: the Recompiler IR and
  lowering files reached by the probe. Its exact file list is UNVERIFIED (U9). If
  it cannot be closed, it falls back to the full `PSXRecomp.Core` source set,
  which is the safe default.
- **CONFIRMED** Coverage is optional in the artifact set
  (`RealRomAnalysisArtifacts.cs:24`), so N12a can be skipped without affecting the
  required documents.

### 4.7 Differential-section input

- **Rule C1.** The differential section is built from validations that actually
  executed (`RealRomCoverageAnalyzer.cs:85-88`). Its absence means "nothing was
  proven". It is not a function of the analysis.
- **Rule C2 (chosen policy).** N12d, the differential section, is never cached and
  is never part of any cache key. It is assembled at N14 from the validations the
  caller supplies on that run. N12a, the classification, is the only coverage
  content that is cached.
- **Why not a digest.** A digest of validation evidence in the N12 key was the
  alternative. It was rejected because the validations come from executions whose
  identity and completeness the cache does not own. Assembling the section fresh
  is cheap and cannot be stale.
- **Test.** T15 compares warm and cold runs with the differential on and off
  (section 7).

## 5. Partial re-analysis units

**Resolution of MAJOR-1.** `instructionCount` is removed from the N7 key. N7
stores the full sweep bounded only by `textEnd`, and `count` is applied as a
prefix when N7's output is consumed.

- **Unit of reuse: the pipeline stage.** A cache hit for a stage replaces that
  stage's computation. A miss recomputes it and every downstream stage.
- **Decode sweep (N7).** The sweep is linear from `entryPoint`, bounded by
  `textEnd` (`RomAnalysisPipeline.cs:507-511`). The current code bounds it by
  `count` as well (`:511`). For a fixed `entryPoint` and `textEnd`, a sweep with
  `count = M` is the first `min(M, total)` items of the unbounded sweep, because
  the loop is deterministic and has no state that depends on `count`.
- **Failure list.** `DecodeFailures` are produced only inside the loop
  (`:531-538`). A count-limited sweep therefore contains only failures whose
  address is below the address of the first excluded instruction. The payload
  stores failures with their addresses, and the consumer filters them to the
  requested prefix. UNVERIFIED: whether any failure can occur before `textEnd`
  at all, since the loop condition (`:511`) appears to prevent out-of-range reads.
- **Prefix rule.** An N7 entry with `sweepEnd = textEnd` serves any `count`. The
  consumer slices `instructions` to `min(count, total)` and filters failures
  (above). Nothing is re-decoded.
- **Downstream.** N8 through N11 consume the sliced list and include
  `instructionCount` in their keys (3.2). A request for a different `count` misses
  there, but reuses N7.
- **No sub-stage reuse below N7.** BasicBlockBuilder, function discovery, and BIOS
  recognition each consume the full decoded list. PREDICTED: per-block or
  per-function reuse would need an explicit dependency model. It is out of scope
  for the first version.
- **Unit for coverage.** Probe results are keyed by raw word
  (`RealRomCoverageAnalyzer.cs:231-237`). N12a is the cache unit, and its key
  contains `loweringFingerprint`, so the per-word probe cache inside a run is
  already sound.

## 6. Fail-closed handling of incomplete or corrupted cache

The rule is that a cache entry is used only if it is proven complete and
correct. Anything else is a miss. A miss is never an error, and a miss never
produces output that a cold run would not produce.

### 6.1 Entry layout

Each stage entry is a directory or a pair of files:

- `header`: format version, `key` (the full stage key), `payloadSha256`,
  `payloadSizeBytes`, `analyzerFingerprint`, `decoderFingerprint`,
  `loweringFingerprint` (when present), `engineRevision` (label), and the
  `schemaVersions` in effect.
- `payload`: the canonical stage output, using the key encoder's canonical form
  for the payload where it is not an artifact document.
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
- **R3.** `header.key` equals the key computed from the current inputs. This is
  the only check that detects a stale entry. R1, R2, and R4 through R6 prove only
  that an entry is internally consistent. A stale entry from an older analyzer is
  internally consistent, so R3 is correct only because `analyzerFingerprint` is in
  the key (4.2).
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
  using the write protocol. Recovery is automatic and needs no user action.
- **Q3 (MINOR-1 resolution).** Cache failures are reported with dedicated codes:
  `ANALYSIS_CACHE_QUARANTINED` for Q1 and `ANALYSIS_CACHE_VERIFY_MISMATCH` for Q4.
  They are not reused from `AnalyzerAnalysisFailed` or `AnalyzerReportFailed`
  (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`), because those
  describe analysis failures, and a cache fault is not one. Under
  `docs/adr/019-diagnostics-and-recovery-contract.md`, a code is a validated
  `SCREAMING_SNAKE` machine identity. Registering the two new codes beside the
  existing ones in `DiagnosticCodes` is the recommended practice. The cache fault
  is a warning on the run, not a failure.
- **Q4.** A verify mode recomputes a hit and compares it with the cached payload.
  A mismatch is a corruption or a fingerprint defect. It is reported as
  `ANALYSIS_CACHE_VERIFY_MISMATCH` and is a hard failure, so the cache never
  silently disagrees with a cold run. Verify mode runs in CI (4.2).

### 6.5 Input identity is never cached as truth

- **I2.** The disc image SHA-256 is computed on every run from the full file
  (`RealRomAnalyzer.cs:102-104`), and the pipeline verifies it (8.3). A
  size-and-mtime shortcut is rejected because it is not a content identity.
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
| T1 | Key encoder | Same inputs produce the same key string across runs (golden value). Object members are in ordinal sort order. Reordering a record's members in source leaves the key unchanged (MAJOR-3) |
| T2 | Key sensitivity matrix | Changing `discImageSha256`, `analyzerFingerprint`, `decoderFingerprint`, `loweringFingerprint`, a schema version, or `inputContainer` changes the key of exactly the expected stages. Changing `instructionCount` changes the N8 through N11 keys and **does not** change the N7 key (MAJOR-1) |
| T3 | Project non-analysis edit | Changing a project field outside `projectFields` leaves every key unchanged |
| T4 | Decoder bump scope | Changing `decoderFingerprint` invalidates N7 through N12a and N14, and leaves N1 through N6 hits (D1) |
| T5 | YAML comment-only edit | No key changes (Rule Y1) |
| T6 | Corruption matrix | Each of: zero-length file, truncated payload, flipped byte, missing `COMPLETE`, wrong `key` in header, unsupported format version, malformed header JSON, schema-version mismatch, produces a miss, a quarantine with `ANALYSIS_CACHE_QUARANTINED`, and output byte-identical to a cold run |
| T7 | Partial failure | A pipeline failure at N8 writes no entry for N8 or later; entries for N7 are the only ones written |
| T8 | Warm equals cold | For each synthetic image, the warm run's artifact files (`ArtifactFile.ToUtf8Bytes`) equal the cold run's, byte for byte |
| T9 | Verify mode | A deliberately corrupted-but-valid-header payload is reported as `ANALYSIS_CACHE_VERIFY_MISMATCH` (hard failure) in verify mode, and as a quarantined miss with a warning in normal mode |
| T10 | Concurrent write | Two writers with the same key leave one complete entry; a fake filesystem that interleaves the renames does not produce a torn entry |
| T11 | Shared store (MAJOR-5) | The CLI and GUI entry points, given the same bytes, compute the same `discImageSha256` by the pipeline's own verification, produce the same key, and read the same entry. A wrong supplied hash fails at the Input stage with no cache write and no artifact |
| T12 | No path in key | No key or entry contains an absolute path (`PathRedactor.cs` exists under `src/PSXRecomp.Tests/RealRomAnalysis/`; its API UNVERIFIED) |
| T13 | Fingerprint guard | A change to a golden document's bytes that is not accompanied by a change in `analyzerFingerprint` fails the test. The fingerprint source set equals the Core glob plus the driver files, and the hash is unchanged by CRLF versus LF line endings (CRITICAL-3, 4.2) |
| T14 | Prefix reuse | An N7 entry computed with `textEnd` and no count, sliced to `count = M`, equals a cold decode at `M`, including the filtered failure list (MAJOR-1, section 5) |
| T15 | Differential on and off (CRITICAL-2) | Warm and cold runs are compared twice: once with validations supplied and once without. Both must match byte for byte. A warm run with validations must show a differential section that the cold run also has, and a warm run without validations must show none. N12d is never read from the cache |
| T16 | Lowering change scope (CRITICAL-1) | Changing `loweringFingerprint` invalidates N12a and N14 and leaves N7 through N11 hits. The coverage classes of a fixed synthetic corpus change when the validator changes, and the warm run then recomputes them |

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
- **Policy S1.** One store format and one key function. The port and key types
  live in `PSXRecomp.Core`, and the file store lives in `PSXRecomp.Infrastructure`
  (8.4). The CLI and GUI pass only a store root; no front end defines its own key.
- **Policy S2.** The store root is supplied by the host. Its location is not
  decided here, because the choice depends on the existing project layout
  (UNVERIFIED; see section 9).
- **Policy S3.** Concurrency across processes uses the write protocol in 6.2
  (W6). Readers never see an incomplete entry, because `COMPLETE` is last.
- **PREDICTED** Sharing is safe only because keys contain no path and no
  front-end state. Any front-end field added to a key later must be reviewed
  against this rule.

### 8.3 Identity verification (MAJOR-5)

- **CONFIRMED** The CLI hashes the file in its own pass, then runs the pipeline
  on a second open of the same path (`src/PSXRecomp.Cli/CliInput.cs:100-116`,
  `LoadChdWithIdentity`). The two passes are not atomic: the file could change
  between them.
- **CONFIRMED** The GUI accepts `discImageSha256` as a parameter and passes it
  through (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:110-117`).
  Its caller was not read (UNVERIFIED).
- **CONFIRMED** The pipeline checks only that the supplied hash is non-empty:
  `RomAnalysisPipeline.cs:93-98` (stream path) and `:194-198` (bytes path). The
  supplied value is therefore trusted. A cache keyed on it would store results
  under an identity nothing verified.
- **Rule V1.** The pipeline computes SHA-256 over the stream or byte array it
  actually runs, and compares it with the supplied value. A mismatch fails the
  Input stage with a dedicated failure kind and no analysis runs. The cache uses
  only the verified hash. The stream path hashes from position 0 to end, then
  rewinds to position 0 before the run. The stream contract already requires
  seekability (`RomAnalysisPipeline.cs:70-72`). Hashing is pure computation with
  no host I/O, so it is consistent with the existing `ComputeSha256` use in the
  Domain (`RomAnalysisPipeline.cs:2`, `:576-579`).
- **Rule V2.** The supplied hash is kept as a claim that is checked, not as an
  identity. Callers that do not have a hash may pass `null` once the API is
  extended. The current non-empty requirement is replaced by the comparison.
- **Cost (PREDICTED).** Verification reads the image twice on the stream path.
  Its cost is part of U5.

### 8.4 Architecture placement (MAJOR-4)

The architecture matrix (`docs/architecture-matrix.md`) assigns the pieces as
follows:

- Domain (`PSXRecomp.Core`) owns ports and deterministic contracts
  (`architecture-matrix.md:22`, rule 1 at `:64`). It must not reference
  `PSXRecomp.Infrastructure`.
- Infrastructure (`PSXRecomp.Infrastructure`) owns concrete managed host
  adapters. `File` and `Directory` access belongs there
  (`architecture-matrix.md:24`, `:55`, rule 2 at `:65`).
- Application and CLI do not call `File.*` or `Directory.*` for ordinary logic
  (rule 3 at `:66`). Wiring a concrete adapter is allowed only at one
  composition-root site with a narrowly scoped AARC002 suppression (`:47`).

Resolution:

- **Core (Domain):** `IAnalysisCacheStore` as a port, the stage key and entry
  types, the key encoder (3.2), the lookup and validation logic (R1 through R7 as
  pure checks over bytes already read), and the quarantine decision. No file
  access.
- **Infrastructure:** `FileAnalysisCacheStore : IAnalysisCacheStore`, marked
  `[Infrastructure]`. It performs the temporary write, flush, rename, `COMPLETE`
  marker, and quarantine rename (6.2, 6.4).
- **Precedent.** `FileProjectMetadataStore` (`src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs`,
  marked `[Infrastructure]`, Issue #42) implements `IProjectMetadataStore`, which
  is defined in `PSXRecomp.Core.Project`. The cache follows the same split.
- **Composition.** The CLI and GUI receive the port. The single composition-root
  site that constructs `FileAnalysisCacheStore` is the only Application-to-Infrastructure
  reference, under the matrix's composition-root exception (`:47`).
- **ADR required.** A new port and adapter for the analysis cache, and the move of
  persistence out of the test project (U3), each need an ADR under
  `docs/adr/` before implementation. This document does not create one.

## 9. Open items and UNVERIFIED list

- **U1.** Enumerate every project field and classify it as analysis-relevant or
  not (section 3.3, rule P1).
- **U2.** Confirm that no build script or generator reads the YAML in
  `src/PSXRecomp.Native` or in any non-searched file type (rule Y2).
- **U3.** Decide where the production driver lives. Persistence currently sits
  in the test project (section 2.2). Moving it into `PSXRecomp.Core` (or a
  production Application component) is a boundary decision that requires an ADR
  (8.4).
- **U4.** Decide the store root. Options and their trade-offs are not evaluated
  here (S2).
- **U5.** Benchmark full-image SHA-256 on a large synthetic image, including the
  double read introduced by Rule V1, to size the warm-run cost (6.5, I2, 8.3).
- **U6.** Confirm the decode-prefix claim (T14) and the decoder fingerprint corpus
  coverage (4.1).
- **U7.** Read `PathRedactor.cs` and `SyntheticDiscBuilder.cs` APIs before the
  test plan is implemented (section 7).
- **U8.** Check whether `DeterministicArtifactTests.cs` already pins golden bytes;
  if it does, T13 may be partly covered (UNVERIFIED).
- **U9.** Determine the exact lowering source set for `loweringFingerprint`
  (4.6). The fallback is the full Core source set.
- **U10.** Read the caller of `AnalyzeAndExecuteFromDisc` to see how the GUI
  computes `discImageSha256` (8.3).
- **U11.** Confirm whether `FailureKind` values used by the pipeline can carry a
  new identity-mismatch kind without changing the diagnostic mapping in
  `src/PSXRecomp.Core/Diagnostics/DiagnosticAdapter.cs`.
- **U12.** Verify that the cache's analyzer-fingerprint build target works under
  the repository's native build and CI workflows (`.github/workflows/ci.yml`),
  which were not read.

## 10. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact
  schemas.
- No cache implementation. This document is the design only.
- No AI result producer.
- No change to project file format.
- No stronger identity than full SHA-256 for inputs.
- No ADR is created here. ADRs are required before implementation (8.4).
