# Issue #43: Incremental Analysis and Cache (design, r6)

- Status: Proposed. Design only. No implementation in this change.
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r6. A redesign by owner ruling. The static file partition of r2 to r5
  is withdrawn. The design review cycle restarts from zero.

## 0. What changed and why

**Owner ruling.** Redesign the cache closure on the actual dependency graph, not on a
static file partition. Accuracy beats performance.

**Why the r5 partition is withdrawn.** The partition was a claim that a set of files
was closed under the analysis. Checking it against the code shows the claim is false.
Each of the following was checked by `git grep` at base `2e3ecad`:

- **CONFIRMED** `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs` is a set-A file
  in r5. It calls `BiosJumpTables.TryResolveVectorFamily` (`:336`) and uses
  `BiosCallFamily` (`:44`, `:66`, `:237`, `:286`, `:567`). Neither type is in set A.
  `BiosJumpTables` and `BiosCallFamily` are declared under `src/PSXRecomp.Core/Runtime/`
  (`BiosCallFamily` at `Runtime/BiosCallIdentity.cs:68`).
- **CONFIRMED** `src/PSXRecomp.Core/DiscImage/ChdCdSectorSource.cs` is a set-E file in
  r5. It implements `ICdSectorSource` (`:15`) and uses `CdDiscToc` (`:30`, `:33`). The
  declaring files of `ICdSectorSource` and `CdDiscToc` were not located (UNVERIFIED).
- **CONFIRMED** `ArtifactJson` is called from C-side files:
  `src/PSXRecomp.Core/Recompiler/RecompilerContract.cs:1126`, `:1132`, and
  `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomCoverageDocument.cs:245`
  (`ToCanonicalJson`). r5 classified `ArtifactJson` as non-analysis.
- **CONFIRMED** `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:147` writes
  `ArtifactKind = AnalysisArtifactSchema.CoverageArtifactKind`. The artifact kind
  string is therefore a value that a set-C file produces and that a cached payload may
  carry. r5 did not account for it.

**Consequence.** A file partition cannot prove that a stage's result depends only on
its declared files. The design below does not use a partition. Each stage's cache key
is built from what the stage actually depends on: its static dependency graph, plus the
values it actually reads at run time. Anything whose dependencies cannot be determined
is never cached.

## 1. Current state

### 1.1 What exists today

- **CONFIRMED** No persistent analysis cache exists. The pipeline is a pure function
  over caller-supplied bytes: "It is a pure domain component: all bytes are supplied
  by the caller and no artifact is written here" (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:23-24`).
- **CONFIRMED** The only caches are lifetime-scoped:
  - CHD hunk cache, an in-memory dictionary per reader (`src/PSXRecomp.Core/DiscImage/ChdReader.cs:85`, `:97`, `:187-193`).
  - Coverage probe cache, keyed by raw instruction word, per `Analyze` call (`src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:98`, `:237-259`).
- **OBSERVED** No AI analysis result producer exists. `git grep AIPrediction` at base returns only the enum member at `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`.
- **CONFIRMED** The CLI and the GUI both call the same pipeline entry points:
  - CLI: `src/PSXRecomp.Cli/CliInput.cs:100-116` (`LoadChdWithIdentity`). Hash block at `:106-109`. Pipeline call at `:115`.
  - GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`, `:117`. The caller of `discImageSha256` is not read (UNVERIFIED).
- **CONFIRMED** Persistence is in the test-project driver, not in Core:
  `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:178-181` (coverage, then `DeterministicArtifactBuilder.Build`), and `RealRomArtifactWriter.cs:44-50`.

### 1.2 Stages are inlined today

- **CONFIRMED** `RomAnalysisPipeline.RunFromIsoSectorReader` (starts at `RomAnalysisPipeline.cs:206`) runs the stages inline. Decode is at `:351-374`,
  basic blocks at `:377-397`, the report at `:400-444`. There is no separate stage function.
- **Consequence (prerequisite R-0).** The design requires each cacheable stage to be a
  callable function with explicit inputs and a serializable output. Extracting them is
  a refactor that must precede the cache. It is not behavior-changing, and its
  equivalence is covered by the existing tests (UNVERIFIED, U1).

### 1.3 The YAML instruction definition

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml` declares `meta.version: "1.0"`
  (line 11). Its governing decisions are `docs/adr/001-cpu-specification-ssot.md` and
  `docs/adr/002-instruction-definition-yaml.md`.
- **OBSERVED** No code parses that file at runtime or build time (search of `*.cs`,
  `*.csproj`, `*.props`, `*.targets`, `*.ps1`, `*.sh`, `*.py`).
- **CONFIRMED** The decoder is hand-written: `src/PSXRecomp.Core/Cpu/R3000aDecoder.cs:6-8`, `:9-40`, `Decode(uint)` at `:70`.
- **Consequence.** A YAML edit cannot change analysis output today. The design keeps
  the YAML out of every key until a reader exists (section 4.6).

### 1.4 Schema versions

- **CONFIRMED** `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`, `CoverageSchemaVersion = 1`,
  `FunctionProvenanceSchemaVersion = 1` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-49`).
- **CONFIRMED** `CoverageArtifactKind` is at `AnalysisArtifactSchema.cs:64`. It is used
  by `RealRomCoverageAnalyzer.cs:147` (section 0).
- **PREDICTED** A schema version does not detect a change in analysis logic. Schema
  versions are kept in keys, but they are not the correctness mechanism.

### 1.5 Project metadata

- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
- **UNVERIFIED** Which project fields reach analysis (U3).

## 2. The actual dependency graph

### 2.1 Stages and their real inputs

Each row is a stage. "Reads" lists the values the stage function actually reads, with
the file that reads them. It is taken from the code, not from a file list.

| Stage | Cached | Reads (actual) | Evidence |
|---|---|---|---|
| N1 input identity | no | image bytes or stream; supplied hash (non-empty check only) | `RomAnalysisPipeline.cs:93-98`, `:194-198`; `RealRomAnalyzer.cs:100-106` |
| N2 CHD open | no | image bytes or stream | `RomAnalysisPipeline.cs:115-117` |
| N3 ISO filesystem | no | sectors from N2 or raw image | `RomAnalysisPipeline.cs:128-129`, `:217` |
| N4 boot executable | no | SYSTEM.CNF, boot file bytes; executable SHA-256 at `:259` | `RomAnalysisPipeline.cs:259`, `:576-579` |
| N5 EXE header | no | executable bytes | `RomAnalysisPipeline.cs:277-337`; `PsxExe.cs`, `PsxExeHeader.cs` |
| N6 text bounds | no | header fields; `TextSegment` | `RomAnalysisPipeline.cs:340-347` (non-empty check only) |
| N7 decode | yes | text bytes from `EntryPoint` to `TextEnd` (`RomAnalysisPipeline.cs:507-508`); `count` (loop bound, `:511`) | `RomAnalysisPipeline.cs:501-543`; `R3000aDecoder.Decode` at `:516`; formatter at `:522-523` |
| N8 basic blocks | yes | decoded list; `instructionCount` parameter, unused in the body (`BasicBlockBuilder.cs:20`); re-decodes at `:30` | `RomAnalysisPipeline.cs:381` |
| N9 functions | yes | decoded list, blocks, edges; no count parameter (`FunctionDiscovery.cs:81-88`); re-decode at `:96` | `RomAnalysisPipeline.cs:404` |
| N10 BIOS calls | yes | decoded list, blocks, functions; `BiosJumpTables`, `BiosCallFamily`, `BiosCallNames` | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, `:176`, `:195`, `:336`, `:584` |
| N11 report | yes | N7 through N10 outputs | `RomAnalysisPipeline.cs:400-444`; call-candidate re-decode at `:553` |
| N12a classification | yes | report; lowering validator; `ArtifactFixtureIdentity` (embedded) | `RealRomCoverageAnalyzer.cs:88`, `:223`, `:234-261`; `MipsToIrLowerer.cs:176`, call at `:251` |
| N12d differential | no (never cached) | caller-supplied validations | `RealRomCoverageAnalyzer.cs:85-88`, `:185`, `:377-382` |
| N14 artifact documents | no | N11, N12a, N12d; `ArtifactJson` serialization | `DeterministicArtifactBuilder.cs:74`, `:96-99`, `:115-121`, `:124`; `ArtifactJson.cs:32` |
| N15 persistence | no | N14 | `RealRomArtifactWriter.cs:44-50` |

### 2.2 Data flow

- The output of each cached stage is the only thing a later stage reads from it. For
  example, N8 reads the decoded list produced by N7, and nothing else of N7.
- The chain is therefore a data chain: a stage's key includes the digest of the actual
  values it reads from the previous stage's output. It does not include the previous
  stage's code identity. If an upstream code change leaves its output unchanged, the
  downstream entry stays valid, which is correct.
- N1 to N6 are not cached. They run on every run, so their values are actual values
  at the time of the run, not cached ones.

## 3. Cache key: the dependency-based definition

### 3.1 Principle

A cache entry for a stage is valid for a run if and only if the stage would compute the
same payload in that run. For that to hold, the key must cover:

- **Every value the stage reads.** These are the actual values, digested.
- **Every line of code the stage can execute.** This is the static dependency closure
  of the stage function, computed from the code, not from a list of files.

If both are covered, equal keys imply equal payloads. The rest of this section defines
how each part is determined and when that determination is not possible.

### 3.2 Key definition

For a cached stage `s`:

```
key(s) = SHA-256( canonical( keyFormat, s,
                             codeIdentity(s),
                             inputs(s) ) )
```

- `codeIdentity(s)` is the **dependency closure** digest of the stage function (3.3).
- `inputs(s)` is the set of values the stage reads, each digested. The set is declared
  as an explicit record type per stage (3.5), and it is checked against the reads (3.6).
- `canonical(...)` is the encoding in 3.7.

### 3.3 Code identity: the static dependency closure

- **Definition.** The closure of a stage function `f` is the set of Core symbols
  reachable from `f` through references: method calls, object creation, type use in
  signatures, bodies, fields, constants, base types, and implemented interfaces. Each
  symbol maps to its declaring source file. The closure is computed by a build-time
  tool on the Roslyn semantic model (UNVERIFIED feasibility, U2).
- **Digest.** `codeIdentity(s)` is the SHA-256 of the closure's member list. Each member
  is recorded as the repository-relative path of its declaring file, its normalized
  content digest, and the symbol's fully qualified name, sorted by the UTF-8 bytes of
  the path and then the name. Normalized content is the file with CRLF replaced by LF
  (DEC-LF1, below).
- **Graph change.** A change in the closure (a new reference, a removed reference, a
  moved symbol) changes the member list, so it changes `codeIdentity(s)` and the key.
  This is the rule "graph changes are misses".
- **Coverage of types the reviewer named.** `BiosJumpTables`, `BiosCallFamily`,
  `CdDiscToc`, `ICdSectorSource`, and `ArtifactJson` are in a closure whenever a
  reached symbol references them. The design does not assert that any of them is absent
  from a closure. The closure is computed, not written down.

### 3.4 Completeness: when the closure can be trusted

A closure is complete only if the static graph contains every symbol that can run. The
tool marks the stage **uncacheable** if any of the following is reached. An
uncacheable stage is always recomputed, and no entry is written for it.

1. **Reflection.** `typeof` on a symbol used only through reflection, `GetType`,
   `Activator`, `MethodInfo`, `Invoke` on runtime-looked-up members, or
   `System.Reflection` APIs.
2. **Dynamic code.** `dynamic`, `System.Linq.Expressions` compilation, or runtime code
   generation.
3. **Native calls.** A P/Invoke or `LibraryImport` declaration, or a call into
   `PSXRecomp.Native` through `NativeInterop.cs` (root of Core, CONFIRMED present).
4. **Delegates from outside.** A delegate or function value passed into the stage
   that is not one of the declared inputs (3.5). Its target is unknown.
5. **Interface or virtual dispatch with implementations outside Core.** Class
   hierarchy analysis over the Core assembly gives the implementation set. If any
   implementation lies outside Core, the set is unknown.
6. **Unresolved symbols.** A reference the semantic model cannot resolve.
7. **Assembly references outside the allow-list** (the BCL, `PSXRecomp.Architecture`,
   and the Core assembly). Any other assembly makes the closure unknown.

The allow-list in (7) includes the BCL. The BCL is not analyzed. Its behavior is
assumed fixed for a given runtime version. The runtime version is part of
`dependencyIdentity` (3.8), so a runtime change is a miss.

### 3.5 Input records

Each cached stage has an explicit input record. The record is the only way the
stage receives values. Example for N7:

```
DecodeStageInputs {
  uint   EntryPoint;
  uint   TextEnd;
  int    Count;            // read by the loop bound at RomAnalysisPipeline.cs:511
  byte[] TextBytes;        // the bytes read from EntryPoint to TextEnd, digested
}
```

Example for N8:

```
BasicBlockStageInputs {
  digest DecodedListDigest;   // digest of the N7 output actually passed in
  // instructionCount is NOT a field: BasicBlockBuilder.cs:20 never reads it
}
```

Rules:

- **Input rule I-a.** A field of an input record is in the key if and only if the
  stage reads it. A field that is never read is removed from the record.
- **Input rule I-b.** A stage that reads a value not in its record is non-compliant.
  The completeness check (3.4) treats the stage as uncacheable until the record is
  corrected.
- **Input rule I-c.** Digests are over the actual values. For `TextBytes`, the value
  is the byte range the decoder actually reads, not the whole executable.

### 3.6 Checking the reads

Two checks make the record match the code:

- **Static.** Each parameter and each record field used in the stage's closure is
  read by a statement the closure reaches. A parameter with no reads (for example
  `instructionCount` in `BasicBlockBuilder.Build`) is reported. Static, UNVERIFIED
  feasibility (U2).
- **Runtime.** The stage's input record is wrapped in a recording accessor during the
  synthetic corpus run. Each field's read count is recorded. A field with zero reads
  across the corpus is a failure of the test (T-READ). A field read but not present in
  the record is impossible by construction because reads go through the record.

### 3.7 Canonical encoding

The key text is produced by a dedicated encoder. `ArtifactJson` is not used for keys:
`ArtifactJson.CanonicalOptions` keeps declaration order
(`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:34-38`, `:23`).

- **Encoding.** UTF-8, no byte-order mark, LF only, one line, no trailing newline.
- **Objects.** `{`, members separated by `,`, `}`. Each member is `"name":value`, with
  no space after the colon. The name is a JSON string.
- **Member order.** Sorted by the UTF-8 bytes of the member name, compared byte by
  byte, shorter prefix first. Declaration order is never used.
- **Arrays.** `[`, elements separated by `,`, `]`, in the given order.
- **Strings.** `"`-delimited. `"` is `\"`, `\` is `\\`. Each control character U+0000 to
  U+001F is `\u00xx` with lowercase hex, including those with short forms. Other
  characters are written as their UTF-8 bytes, unescaped.
- **Integers.** Base 10, no leading `+`, no leading zeros, `-` only when negative.
- **Booleans and null.** `true`, `false`, `null`.
- **No floats.** An encoder given a float fails.
- **Golden value.** T-KEY asserts the exact text of a fixed sample and its SHA-256.

### 3.8 Dependency identity (outside the source)

`dependencyIdentity` covers inputs that are not Core source:

- The resolved package versions, from `obj/project.assets.json`. This is an explicit
  input exception: it is build output read by its exact name, and no other `obj/` file
  is read. The csproj pins only direct packages (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:24-25`),
  and no lock file exists (CONFIRMED by listing). SharpCompress is used by
  `src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs:3`, so its version is a dependency.
- The SDK version and runtime version. The source is the MSBuild property
  `$(NETCoreSdkVersion)` (UNVERIFIED in this repository, U5). `TargetFramework` is
  `net10.0` (`PSXRecomp.Core.csproj:3`).
- The compile inputs that are not in a closure: `Directory.Build.props` and
  `src/Architecture/PSXRecompArchitectureAttributes.cs`. The latter is compiled into
  Core when `CompileArchitectureAttributes` is set (`PSXRecomp.Core.csproj:7`). It is
  linked by `Directory.Build.props:22-24` and declares the internal marker attributes
  (`DomainAttribute`, `ApplicationAttribute`, `InfrastructureAttribute`, `TestAttribute`,
  `GeneratedAttribute`). Attributes are metadata and do not run, but a change alters
  the compiled assembly, so `dependencyIdentity` includes the file (CONFIRMED lines
  above; the effect on analysis output is UNVERIFIED).

### 3.9 Decision: line endings

**DEC-LF1.** Digests use LF-normalized content. CRLF pairs become LF, a lone CR is kept,
and a BOM is kept. Rationale: a checkout with different line-ending settings must not
change a digest for the same source. The git warning that "LF will be replaced by
CRLF" for the design file was observed during commit `6d038a0` (OBSERVED). The known
exception is a multi-line verbatim string literal, whose value depends on its line
endings. Lint test T-LF rejects such literals in any file in a closure.

## 4. Invalidation

### 4.1 Rules

- **V1. Changed input value.** A changed input digest changes the key. The entry is
  not reused. Recomputation writes a new entry.
- **V2. Changed closure.** A changed code identity changes the key. This includes a
  changed body, a changed reference, and a moved symbol.
- **V3. Changed dependency identity.** A changed package version, SDK version, or
  runtime version changes every key.
- **V4. Untracked value.** A value a stage reads that is not in its record is a
  defect of the record, detected by 3.6. Until it is fixed, the stage is uncacheable.
- **V5. Unknown dependency.** An unknown dependency (3.4) makes the stage uncacheable.
  An uncacheable stage is always recomputed.
- **V6. Parameter not read.** A parameter the stage does not read is not in the key.
  This is accurate, not a gap: changing it cannot change the payload.

### 4.2 Per-stage consequences

- **N7.** Changing the decoder, formatter, or any code in N7's closure (for example
  `R3000aDecoder.cs`) changes `codeIdentity(N7)`. A change to the text bytes changes
  `TextBytes`. Changing `count` changes the key only through the decoder's loop bound.
  See 5.1 for the sweep rule.
- **N8.** Its key includes the digest of the N7 output it reads. A change to N7's
  output, or to N8's closure, is a miss. A change to `instructionCount` is not a miss,
  because the parameter is not read (`BasicBlockBuilder.cs:20`).
- **N9 to N11.** Same pattern: input digests from the previous stage's output and the
  stage's own closure.
- **N10.** The closure includes `BiosJumpTables` and `BiosCallFamily` through the
  references in `BiosCallRecognition.cs` (section 0). A change to either is a miss for
  N10 and, through the data chain, for the stages whose output N10 changes.
- **N12a.** The closure includes the lowering validator through `Classify` and
  `ProbeLowerable` (`RealRomCoverageAnalyzer.cs:223`, `:234-261`) and the
  `MipsToIrLowerer` reached through `LowerProgram` (`MipsToIrLowerer.cs:176`). A
  change to the lowerer is a miss for N12a only, if N11's output is unchanged.
- **N12d.** Never cached. It is built at N14 from the validations of the current run
  (`RealRomCoverageAnalyzer.cs:85-88`). Caching it would need a digest of the
  validation evidence, which the cache does not own.

### 4.3 Input identity

- **I2.** The disc image SHA-256 is computed from the full file on every run on the
  test-driver path (`RealRomAnalyzer.cs:102-104`) and on the CLI path
  (`CliInput.cs:106-109`). The GUI path is UNVERIFIED (U6). The pipeline must verify the
  supplied hash on every path (4.4).

### 4.4 Identity verification (proposal)

- **Rule V-ID.** The pipeline computes SHA-256 over the stream or array it runs and
  compares it with the supplied value. A mismatch fails the Input stage with a dedicated
  failure kind. The stream path hashes from position 0 to end, then seeks back to 0. The
  stream contract already requires seekability (`RomAnalysisPipeline.cs:70-72`). The
  Domain already uses SHA-256 (`RomAnalysisPipeline.cs:2`, `:576-579`).
- **Migration.** `src/PSXRecomp.Tests/DiscImage/DiscImageHostileSizeTests.cs:24` defines
  `Sha` as a literal 64-character string that is not the hash of the bytes passed at
  `:347`. About 50 call sites exist in the test projects (9 in that file, 2 in
  `SyntheticChdReaderTests.cs`, 2 in `RealRomAnalyzer.cs`, 36 in
  `RomAnalysisPipelineTests.cs`, 1 in `RealRomProductionExecutionTests.cs`). How many
  pass a constant is UNVERIFIED (U7).

## 5. Partial re-analysis units

### 5.1 Decode sweep (N7)

- **CONFIRMED** The loop is bounded by `count` and by `textEnd`: `maxAddress` is
  `exe.Header.TextEnd` (`RomAnalysisPipeline.cs:507`), the start is `EntryPoint` (`:508`),
  and the loop stops at `decoded < count` (`:511`). Each word is read inside a `try` (`:513-539`).
- **Decision DEC-N7.** The N7 stage is defined as the sweep from `EntryPoint` to
  `TextEnd`, without `count`. A request for `count = M` is a prefix of that sweep,
  taken by the consumer. Cold N7 therefore decodes the whole text region. This is
  slower than the present code for a small `count`. The cost is UNVERIFIED (U4).
- **Prefix rule.** For a requested `M`, the consumer takes the first `min(M, total)`
  instructions.
- **Failure rule.** A decode failure at address `A` is included in a count-limited
  result iff `A < entry + 4*M`. The loop stops after `M` successful decodes (`:511`).

### 5.2 Text shorter than the declared size

- **CONFIRMED** The text check at `RomAnalysisPipeline.cs:340-347` rejects only a
  zero-length `TextSegment`.
- **CONFIRMED** `PsxExe.GetInstructionWord` throws `ArgumentOutOfRangeException` when
  `offset + 4 > TextSegment.Length` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:21-30`).
- **CONFIRMED** The decode loop catches it (`RomAnalysisPipeline.cs:531`), records a
  `DecodeFailure` ("Address outside text segment bounds"), and stops (`:533-538`). No
  exception escapes the stage.
- **Test T-SHORT** pins this behavior.

### 5.3 Other units

- The unit of reuse is a cached stage (N7, N8, N9, N10, N11, N12a). A hit replaces that
  stage. A miss recomputes it. Downstream stages are recomputed only if their own keys
  change, because their keys contain the digests of the values they read.
- No reuse within a stage (per block, per function) in v1.

## 6. Cold and warm, corruption, and diagnostics

### 6.1 Entry layout (proposal)

Each cache entry is a directory with:

- `header`: `keyFormat`, the full key text (3.7) and its SHA-256, `payloadSha256`,
  `payloadSizeBytes`, and the stage's `codeIdentity` and `dependencyIdentity`.
- `payload`: the stage output in the canonical form of 3.7.
- `COMPLETE`: a marker written last. This is a proposal. The code has no on-disk
  marker today. The driver records `Manifest` and `Complete` as stage results at
  `RealRomAnalyzer.cs:261-262`, and `RomAnalysisStage.cs:10` states that the pipeline
  does not produce them.

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the entry directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, after W3 succeeds for every file.
- **W5.** Never write an entry for a stage that did not pass, or for an uncacheable stage.
- **W6.** A rename that fails because the target exists succeeds only if the existing
  header's `payloadSha256` matches. Otherwise the entry is quarantined and the write is
  retried once.

### 6.3 Read protocol

An entry is used only if all of the following hold:

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses, and its `keyFormat` is supported.
- **R3.** `header.key` equals the key computed from the current inputs (3.2).
- **R4.** The payload size equals `payloadSizeBytes`.
- **R5.** SHA-256 of the payload equals `payloadSha256`.
- **R6.** The payload deserializes under the current schema.
- **R7.** Any exception during R1 to R6 is caught and treated as a corrupt entry.

R1 to R7 prove only that an entry is internally consistent. R3 is the only check that
ties an entry to the current code and inputs, and it is correct only if the key is
complete (3.4 to 3.6). A consistent entry whose key is current but whose payload
differs from what the stage computes now would be used. That can only happen if the
key is incomplete. Section 7 is the test for that case.

### 6.4 Corruption and missing entries: fail safe

- **F1. Corrupt.** An entry that fails R1, R2, R4, R5, R6, or R7 but exists, or whose
  R3 check fails, is quarantined by renaming it to a name that records the failing
  check. The stage is recomputed and the result written by 6.2. A diagnostic
  `ANALYSIS_CACHE_QUARANTINED` is reported on the run as a warning.
- **F2. Missing.** An entry that does not exist is a miss. The stage is recomputed. No
  diagnostic is reported. A missing entry is normal.
- **F3. Uncacheable.** A stage that is uncacheable (3.4) is recomputed on every run.
  The run reports `ANALYSIS_CACHE_UNCACHEABLE` as information, once per stage.
- **F4. Partial failure.** If a stage fails, no entry is written for it or for any stage
  below it (W5). The run reports the stage failure through the existing pipeline outcome.
- **Codes.** Three codes are proposed: `ANALYSIS_CACHE_QUARANTINED`,
  `ANALYSIS_CACHE_UNCACHEABLE`, and `ANALYSIS_CACHE_VERIFY_MISMATCH`. None is registered
  today. Under `docs/adr/019-diagnostics-and-recovery-contract.md`, a code is a validated
  `SCREAMING_SNAKE` identity, and registering them is recommended
  (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53` holds the existing analysis
  codes, which do not cover cache faults).

### 6.5 Cold and warm

Cold and warm runs must produce the same payloads. This is the central correctness
property. Section 7 defines the test.

## 7. Tests

Tests use synthetic inputs from `src/PSXRecomp.Tests/RealRomAnalysis/`:
`SyntheticDiscBuilder.cs`, `SyntheticChdBuilder.cs`, and `SyntheticIsoBothEndianTests.cs`
(these exist; their APIs are UNVERIFIED, U8). No BIOS, ROM, or CHD content is used.
Clocks are injected. Temp paths are not asserted.

Injection seams, all Core types:

- `IAnalysisCacheStore`: the store port (8.2). Tests pass an in-memory fake.
- `IStageCodeIdentity`: returns `codeIdentity` and the completeness verdict per stage.
  Tests pass a fake to simulate a changed closure or an incomplete one.
- `IStageInputRecorder`: wraps a stage's input record and counts reads per field (3.6).
- `AnalysisCacheOptions`: `Mode` (`Normal` or `Verify`), the store, the code identity
  provider, the recorder, and a diagnostics sink.

| ID | Test | Asserts |
|---|---|---|
| T-COLD-WARM | Cold and warm equality (6.5) | For each synthetic fixture and each cached stage N7 to N12a: run cold with an empty store, store the entries, then run warm. Every cacheable stage must report a hit (a warm run with zero hits fails). The warm payload equals the cold payload byte for byte, per stage and for the whole document (`ArtifactFile.ToUtf8Bytes`). |
| T-DEP-INPUT | Changed tracked input is not reused | For each stage and each field of its input record, change that one field in a fake. The key differs, the entry is not reused, and the recomputed payload is written under the new key. |
| T-DEP-CODE | Changed closure is not reused | Substitute a changed `codeIdentity` for a stage in `IStageCodeIdentity`. The entry is not reused. Downstream stages whose output is unchanged may still hit, and the test asserts exactly which. |
| T-DEP-DEAD | Unread parameter is accurate | Change a parameter that the stage does not read (for example `instructionCount` for N8). The key does not change and the entry is reused. This pins rule V6. |
| T-UNCACHE | Unknown dependency is never cached | Inject an incomplete verdict for a stage. The stage recomputes on every run, writes no entry, and reports `ANALYSIS_CACHE_UNCACHEABLE`. |
| T-READ | Input record matches reads | Run each cached stage under the recorder on the synthetic corpus. Every field of the input record is read at least once. A field with zero reads fails. |
| T-CLOSURE-RT | Runtime closure check | Run each cached stage under an instrumented build that records executed methods and types. Every executed symbol must be in the static closure. An executed symbol outside the closure fails the test, because the closure was incomplete. Limited to exercised paths (UNVERIFIED feasibility of the instrumentation, U2). |
| T-CORRUPT | Corrupt entries fail safe | For each corruption case (zero-length file, truncated payload, flipped byte, wrong key in header, unsupported `keyFormat`, malformed header, schema mismatch), the run recomputes, the output equals the cold output byte for byte, and `ANALYSIS_CACHE_QUARANTINED` is reported once per corrupt entry. |
| T-MISSING | Missing entries are silent misses | Delete an entry. The run recomputes, the output equals the cold output, and no diagnostic is reported. |
| T-PARTIAL | Partial failure writes nothing below | A failure injected at N8 writes no entry for N8 or later. The N7 entry is the only one written. |
| T-STALE | Stale-but-consistent entry (residual risk) | An entry whose header and `COMPLETE` marker match the current key, with a payload that differs from the cold result. **Normal mode** returns that payload as a hit and performs no cold recompute. **Verify mode** reports `ANALYSIS_CACHE_VERIFY_MISMATCH` and returns no hit. |
| T-SHORT | Short text segment (5.2) | An executable whose text segment is shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment, and no exception escapes. |
| T-PREFIX | Prefix reuse with failure filter (5.1) | An N7 entry computed without `count`, sliced to `M`, equals a cold decode at `M`, including failures filtered by `A < entry + 4*M`. |
| T-DIFF | Differential on and off | Cold and warm runs with validations supplied and without. Both match byte for byte. N12d is never read from the cache. |
| T-IDENT | Identity verification (4.4) | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind and no cache write. The CLI and GUI entry points, given the same bytes, compute the same verified hash. The GUI part is UNVERIFIED until U6 runs. |
| T-KEY | Key encoder (3.7) | The encoded text of a fixed sample equals the committed text byte for byte, and its SHA-256 equals the committed value. Member order is UTF-8 byte order. Floats are rejected. |
| T-LF | Multi-line verbatim literal lint (DEC-LF1) | Fails if any file in a closure contains a multi-line verbatim string literal. |
| T-GOLDEN | Cross-revision regression | Committed golden payloads, produced from synthetic fixtures, are compared with a cold recompute. Each cached stage must have at least 4 compared entries (one per fixture), so the check cannot pass with zero comparisons. An intentional payload change requires regenerating the goldens with the named command `regenerate-analysis-goldens`, and the golden diff is reviewed. Golden location: `src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGolden/`, one `<unit>/<fixtureId>.golden.json` per pair (proposal). |

Determinism rules for every test: no wall-clock values in keys, headers, or assertions;
no dependence on the order of unsorted collections; no reliance on the temp directory name.

## 8. Architecture, identity, and AI results

### 8.1 Architecture placement

The architecture matrix (`docs/architecture-matrix.md`) assigns the pieces:

- **CONFIRMED** Domain (`PSXRecomp.Core`) owns ports and deterministic contracts
  (`architecture-matrix.md:22`, rule 1 at `:64`) and must not reference
  `PSXRecomp.Infrastructure`.
- **CONFIRMED** Infrastructure owns managed host adapters, including `File` and
  `Directory` access (`architecture-matrix.md:24`, `:55`, rule 2 at `:65`).
- **CONFIRMED** Application and CLI do not call `File.*` or `Directory.*` for ordinary
  logic (rule 3 at `:66`). Concrete adapter wiring is allowed at one composition-root
  site with a narrowly scoped AARC002 suppression (`:47`).

Resolution:

- Core: `IAnalysisCacheStore`, the key and entry types, the key encoder (3.7), the
  input records, `IStageCodeIdentity`, `IStageInputRecorder`, `AnalysisCacheOptions`,
  and the validation logic (R1 to R7 as checks over bytes already read). No file access.
- Infrastructure: `FileAnalysisCacheStore : IAnalysisCacheStore`, marked
  `[Infrastructure]`, with the file operations of 6.2.
- Precedent: `src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs` implements
  `IProjectMetadataStore`, which is defined in `PSXRecomp.Core.Project` (Issue #42).
- The closure tool runs at build time or in CI. Its placement (a test-project tool, a
  build target, or a separate project) is UNVERIFIED (U2).
- **ADR required** before implementation: the port, the adapter, the move of persistence
  out of the test project (U3), the closure tool, and any use of an AI evidence label.
  This document does not create one.

### 8.2 Stores and seams

The store is injected. The CLI and the GUI pass a store root and the same key function.
The root's location is UNVERIFIED (U6).

### 8.3 Shared CLI and GUI cache

- Both front ends call the same pipeline (1.1). The key function and the store format
  are shared code.
- Cross-process concurrency uses the write protocol (6.2, W6).

### 8.4 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists (1.1).
- **CONFIRMED** `EvidenceType.AIPrediction = 6` exists at
  `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`, next to `AnalyzerFinding = 5` (`:16`).
- **Rules.** An AI result is advisory: it is never stored as an analysis unit or in an
  analysis document. It is reused only when its key matches the hashes of the artifacts
  it was computed from, the prompt or template revision, the model identifier, and the
  model settings. It is invalidated when any input digest it depends on changes. Whether
  it uses `EvidenceType.AIPrediction` is a decision for its own ADR (UNVERIFIED for
  consumers beyond the base search).

## 9. Open items (all UNVERIFIED unless stated)

- **U1.** Prerequisite R-0: extract each cached stage from `RunFromIsoSectorReader` into a function with an explicit input record (1.2). Equivalence of the refactor is covered by existing tests (UNVERIFIED).
- **U2.** Feasibility of the closure tool on the Roslyn semantic model: reference enumeration including types, attributes, base types, and interfaces; class hierarchy analysis; detection of reflection, dynamic, and native calls (3.4); the static unused-parameter check (3.6); and the runtime instrumentation for T-CLOSURE-RT. Each of these is UNVERIFIED. Until they run, no stage may be marked cacheable.
- **U3.** Enumerate the project fields that reach analysis (1.5).
- **U4.** Measure cold N7 over the full text region (DEC-N7).
- **U5.** Confirm the `$(NETCoreSdkVersion)` property in this repository (3.8).
- **U6.** Read the GUI caller of `discImageSha256` (`RealRomTitleExecutionService.cs:110-117`, caller not read), and decide the store root (8.2).
- **U7.** Count the test call sites that pass a constant SHA (4.4 Migration).
- **U8.** Read `PathRedactor.cs` and the synthetic builder APIs before the tests are implemented.
- **U9.** Locate the declarations of `ICdSectorSource` and `CdDiscToc` (section 0).
- **U10.** Confirm that the `obj/project.assets.json` input is the only `obj/` file read (3.8).
- **U11.** Check which declared stage outputs embed `ArtifactJson`-serialized content (`RealRomCoverageDocument.cs:245`, `RecompilerContract.cs:1126`) and whether a cached payload uses that encoding (3.7 uses the key encoder).

## 10. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact schemas.
- No cache implementation. This document is the design only.
- No AI result producer.
- No change to the project file format.
- No ADR is created here.

## 11. Review history

- Rounds 1 to 3 (on `6d038a0`, `c8299d4`, `684b573`) and the r4 and r5 revisions used a
  static file partition. That partition is withdrawn by the owner ruling and by the
  falsifications in section 0. Its findings are superseded, and no traceability to
  them is kept.
- The verified citations from those rounds that remain true are kept in sections 1 to
  5. Citations that were not re-run in this revision are labeled UNVERIFIED in section 9
  where they matter.
