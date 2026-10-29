# Issue #43: Incremental Analysis and Cache (design, r12)

- Status: **Design. No stage is cacheable.** Rule C (4.3) is the only cacheability rule: a stage is
  cacheable only when its U2 verdict, V-ID, and T-IDENT all hold. Until Rule C holds for a stage, that
  stage is uncacheable in every mode. An uncacheable stage is recomputed on every run, and no entry is
  written for it, in Verify mode as well (section 6.5). The hit path is not executed by any production run
  until Rule C holds, and no test may claim a production stage is cacheable (7.1, 7.3).
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r12. Addresses the review of `34845dd` (r11): five findings, mapped in 12.4. The findings of
  `f77183e` (r10) stay mapped in 12.3. The direction of r9 to r11 is not re-examined. Section 12 maps each finding to its resolution, and the resolutions are stated
  in this document, not only in the review text.

## 0. Why there is no static file partition

The r2 to r5 static file partition is withdrawn (owner ruling). Checked at base `2e3ecad` by `git grep`:

- **CONFIRMED** `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs` calls
  `BiosJumpTables.TryResolveVectorFamily` (`:336`) and uses `BiosCallFamily` (`:44`, `:66`, `:237`,
  `:286`, `:567`). Both are declared under `src/PSXRecomp.Core/Runtime/` (`BiosCallFamily` at
  `Runtime/BiosCallIdentity.cs:68`).
- **CONFIRMED** `src/PSXRecomp.Core/DiscImage/ChdCdSectorSource.cs` implements `ICdSectorSource` (`:15`)
  and uses `CdDiscToc` (`:30`, `:33`). Both are declared in `src/PSXRecomp.Core/Runtime/CdRom/ICdSectorSource.cs`:
  `CdDiscToc` at `:11`, `ICdSectorSource` at `:56`.
- **CONFIRMED** `ArtifactJson` is called from Core source files: `src/PSXRecomp.Core/Recompiler/RecompilerContract.cs:1126`,
  `:1132`, and `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomCoverageDocument.cs:245`.
- **CONFIRMED** `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:147` writes
  `AnalysisArtifactSchema.CoverageArtifactKind`.

A file partition cannot show that a result depends only on the declared files. The design therefore
uses (a) the whole Core assembly as the code identity (3.3), (b) an explicit input record per stage,
which holds the values the stage reads (3.5), and (c) an explicit list of implicit inputs that make a
stage uncacheable (3.4).

## 1. Current state

### 1.1 What exists today

- **CONFIRMED** No persistent analysis cache exists. The pipeline is a pure function of caller-supplied
  bytes: "It is a pure domain component: all bytes are supplied by the caller and no artifact is
  written here" (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:23-24`).
- **CONFIRMED** The only caches are lifetime-scoped: the CHD hunk cache (`src/PSXRecomp.Core/DiscImage/ChdReader.cs:85`,
  `:97`, `:187-193`) and the coverage probe cache (`src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:98`,
  `:237-259`).
- **OBSERVED** No AI analysis result producer exists. `git grep AIPrediction` at base returns only
  `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`.
- **CONFIRMED** Persistence is in the test-project driver, not in Core (2.3).

### 1.2 Stages are inlined

- **CONFIRMED** `RomAnalysisPipeline.RunFromIsoSectorReader` (starting at `RomAnalysisPipeline.cs:206`)
  runs every stage inline. The decode call is at `:355`, and the report at `:400-444`.
- **Prerequisite R-0 (U1).** Each cached stage is extracted into a function that takes one input record,
  holding values (3.5), and returns its output. `Analyze` is split into classification (N12a) and
  differential assembly (N12d, 2.2). The refactor must be behavior-preserving. Its tests are the
  existing suite plus T-PREFIX and T-DIFF. **Until U1 is done, the N12a classification function does
  not exist as a separate root, and U2 cannot be run for N12a** (9, dependency).

### 1.3 YAML, schema, project

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml:11` declares `meta.version: "1.0"`. **OBSERVED** no
  code parses it. Governing decisions are `docs/adr/001-cpu-specification-ssot.md` and
  `docs/adr/002-instruction-definition-yaml.md`. The YAML is out of every key until a reader exists
  (rule Y1).
- **CONFIRMED** Schema constants: `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`, `CoverageSchemaVersion = 1`,
  `FunctionProvenanceSchemaVersion = 1` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-49`; `FunctionProvenanceSchemaVersion` is declared at `:49`).
  `CoverageArtifactKind` at `:64`.
- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`). Which
  project fields reach analysis is UNVERIFIED (U3).

## 2. Entry points, stages, and inputs

### 2.1 Entry points (all callers verified at base)

The pipeline has three public entry points. Every caller found in the tree is listed.

| Entry point | Definition | Callers (CONFIRMED) | Stages covered | Supplied identity |
|---|---|---|---|---|
| `RunFromChd(byte[] discImageBytes, string discImageSha256, ...)` | `RomAnalysisPipeline.cs:42-62`; `ValidateInput` at `:57` | Core: `src/PSXRecomp.Core/DiscImage/DiscImageAnalyzer.cs:29` (inside `DiscImageAnalyzer.Analyze(byte[] chdBytes, string chdSha256, ...)`, `:26-29`). Test callers are not enumerated (UNVERIFIED, U7). | N1 to N15 except N13 (CHD path: N2 runs) | non-empty check only (`:194-198`) |
| `RunFromChd(Stream chdStream, string discImageSha256, ...)` | `RomAnalysisPipeline.cs:78-104` | Core: `src/PSXRecomp.Core/DiscImage/DiscImageAnalyzer.cs:44` (inside `DiscImageAnalyzer.Analyze(Stream chdStream, string chdSha256, ...)`, `:41-44`). CLI: `src/PSXRecomp.Cli/CliInput.cs:115`. GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:117`. Tests: `src/PSXRecompStudio.Tests/RealRomProductionExecutionTests.cs:75`. Test driver: `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:122`. | N1 to N15 except N13 (N2 runs) | non-empty check only (`:93-98`) |
| `RunFromIsoImage(byte[] isoImageBytes, string discImageSha256, ...)` | `RomAnalysisPipeline.cs:142-164` | GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97` | N1, N3 to N15 except N13 (N2 is skipped: `RomAnalysisPipeline.cs:163`) | non-empty check only (`:194-198`, via `ValidateInput`) |

Notes:

- **CONFIRMED** `DiscImageAnalyzer.Analyze` is a Core API that wraps the two `RunFromChd` overloads.
  Its callers are not in this table, because they are outside the pipeline's stage code.
- **CONFIRMED** The CLI path hashes the file in one pass (`CliInput.cs:106-109`) and opens it again for the
  pipeline (`:113-115`). The two passes are not atomic.
- **CONFIRMED** The GUI's `discImageSha256` enters as a parameter of `AnalyzeAndExecuteFromDisc`
  (`RealRomTitleExecutionService.cs:110-117`). Its caller was not read (UNVERIFIED, U6).

### 2.2 Stage table

"Candidate" means the stage is a cache candidate, not cacheable. Every candidate is uncacheable until
Rule C (4.3) holds for it. The record column names the values the stage's input record holds (3.5).

| Stage | Status | Input record (values, 3.5) | Evidence (reads, from the code) |
|---|---|---|---|
| N1 input identity | not cached | runs each run | `RomAnalysisPipeline.cs:93-98`, `:194-198`; `RealRomAnalyzer.cs:100-106` |
| N2 CHD open | not cached | runs each run; skipped on ISO | `RomAnalysisPipeline.cs:115-117`, `:163` |
| N3 ISO filesystem | not cached | runs each run | `RomAnalysisPipeline.cs:128-129`, `:217` |
| N4 boot executable | not cached | runs each run; executable SHA-256 at `:259` | `RomAnalysisPipeline.cs:259`, `:576-579` |
| N5 EXE header | not cached | runs each run | `RomAnalysisPipeline.cs:277-337`; `PsxExe.cs`, `PsxExeHeader.cs` |
| N6 text bounds | not cached | runs each run; non-empty check only | `RomAnalysisPipeline.cs:340-347` |
| N7 decode | candidate | `DecodeInput` (3.5) | `RomAnalysisPipeline.cs:501-543`; `PsxExe.cs:23` (`Header.TextStart`); `R3000aDecoder.Decode` at `:516` |
| N8 basic blocks | candidate | `BlockInput` (3.5) | `RomAnalysisPipeline.cs:381`; `BasicBlockBuilder.cs:27-31` (reads `RawWord`), `:34` (`decodeStart`) |
| N9 functions | candidate | `FunctionInput` (3.5) | `RomAnalysisPipeline.cs:404`; `FunctionDiscovery.cs:95-96` (reads `RawWord`), `:105` (entry), `:173` (text bounds) |
| N10 BIOS calls | candidate | `BiosInput` (3.5) | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, `:176`, `:195`, `:336`, `:584` |
| N11 report | candidate | `ReportInput` (3.5) | `RomAnalysisPipeline.cs:400-444`, identity at `:411-424`; call-candidate re-decode at `:553` |
| N12a classification | candidate, after U1 | `CoverageInput` (3.5): the N11 report and the fixture identity. Nothing else | `RealRomCoverageAnalyzer.cs:88`, classification `:223`, `:234-261`; `LowerProgram` call at `:251`; definition `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176` |
| N12d differential | not cached | `validations`, supplied on that run | `RealRomCoverageAnalyzer.cs:85-88`, assignment `:185`, `BuildDifferential` `:377-382` |
| N14 artifact documents | not cached | runs each run | `DeterministicArtifactBuilder.cs:74`, `:96-99`, `:115-121`, `:124`; `ArtifactJson.cs:32` |
| N15 persistence | not cached | runs each run | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50` (LOW-1: the writer lives in the test project; moving it is U3) |

**N13 is not a stage.** The numbering has no N13. The first revision (`6d038a0`) listed N13 as the coverage
probe, which calls `MipsToIrLowerer.LowerProgram` (call at `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:251`,
in `ProbeLowerable`, `:234-261`). That probe is part of the N12a classification row above. The number is not reused.
Every "N1 to N15" range in this document means the stages in this table, without N13.

**Replaced design (`analyzerFingerprint`).** The identifier `analyzerFingerprint` is absent from the code tree at
`f95bbae` and at `684b573` (zero matches tree-wide in each). It appears in revision `c8299d4` (12 matches tree-wide, all in `docs/design/issue-43-analysis-cache.md`; none in code).
In the current document it occurs in this paragraph and in the F4 row of 12.3. Its per-stage successor, the fingerprint sets of `684b573`, is withdrawn as well. This section describes a
replaced design. The N7 to N15 closure replaces the fingerprint: the whole Core MVID (3.3) gives the code identity,
and the reachability closure of each stage (3.4, 9) decides whether that stage may use the cache at all. No
code-fingerprint identifier is used in the current design.

**N12a and N12d.** `Analyze(report, fixture, validations = null)` (`RealRomCoverageAnalyzer.cs:88`) returns
`Differential = BuildDifferential(validations)` (`:185`). N12a is a function of `(report, fixture)` only.
N12d is a function of the N12a result and `validations`, and it is never cached.

### 2.3 Fixture identity and the driver

- **CONFIRMED** The driver builds `DeterministicArtifactInput` at `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:162-171`.
  It sets `FixtureId` (`:164`), `DiscImageFormat`, `DiscImageSha256`, `DiscImageSizeBytes`, `Chd`, `Iso`,
  and `Report`.
- **CONFIRMED** `DeterministicArtifactBuilder.BuildFixtureIdentity` is at
  `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/DeterministicArtifactBuilder.cs:159-178`. The driver
  calls it at `RealRomAnalyzer.cs:179`, inside `RealRomCoverageAnalyzer.Analyze(...)`.
- **CONFIRMED** The driver calls `DeterministicArtifactBuilder.Build` at `RealRomAnalyzer.cs:181`. That is N14,
  which is not cached.
- **Consequence.** `FixtureId` is an input of N12a, and it comes from the driver, not from Core. A
  different driver supplies a different N12a input.

### 2.4 Data flow

- A stage's output is the only thing a later stage reads from it. N8 reads the sliced decoded list
  (the output of N7 after the slice in 5.1), and nothing else of N7.
- A stage's input record holds the **values** it reads (3.5). The key is computed from a digest of the
  record (3.2). The digest is used for the key only.
- When an upstream stage is a cache hit, its payload is decoded by the codec (3.7, R6) into the value
  the downstream record holds.

## 3. Cache key

### 3.1 Principle

An entry is reused in a run only if the stage would compute the same payload in that run. The key covers:

- **The code**: the whole Core assembly identity (3.3).
- **The values read**: the input record, digested for the key (3.2) and held as values for the
  computation (3.5).
- **The implicit inputs**: anything read that is not in the record makes the stage uncacheable (3.4).
- **The build and runtime inputs outside Core source** (3.8).

### 3.2 Key definition and the role of digests

```
key(s) = SHA-256( canonical( keyFormat, s,
                             codeIdentity,
                             dependencyIdentity,
                             digest(inputRecord(s)) ) )

digest(R) = SHA-256( codecBytes(R) )
```

- `codecBytes(R)` is the canonical codec encoding of the record `R` (3.7). The codec is injective (T-CODEC).
- **A digest is never the input of a computation.** A stage computes from the record's values (3.5). A
  stage whose input is only a digest cannot be computed, and this design allows none.

### 3.3 Code identity: the whole Core assembly

- **Decision DEC-CODE.** `codeIdentity` is the metadata version identifier (MVID) of `PSXRecomp.Core`,
  with its full name. The build is deterministic, so the MVID is a content-derived identifier of the
  compiled output. It changes whenever the IL or the metadata of Core changes. Determinism is UNVERIFIED (U5).
- **Rule.** A change anywhere in Core changes `codeIdentity` and invalidates every cached stage. This is
  accepted: accuracy beats performance (owner ruling).
- **Why not a source-file closure digest.** A closure of source files cannot see a binding change outside
  it. The compiled assembly does. A closure is used only to scope the implicit-input scan (3.4).

**Non-Core code.** Code outside `PSXRecomp.Core` does not enter the code identity. This is correct only
because of the following, all CONFIRMED at base:

- The driver supplies only record values to the cached stages. The fixture identity record is built at
  `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:162-171`, and N12a receives it through the
  record. The driver's `DeterministicArtifactBuilder.Build` call (`:181`) is in N14, which is not cached.
- N14 and N15 are not cached. The writer, `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50`,
  and the artifact assembly run each time.
- The CLI (`CliInput.cs:100-116`) and the GUI (`RealRomTitleExecutionService.cs:97-117`) call the pipeline's
  entry points (2.1) and do not run analysis code of their own.

**Rule NC.** A cached stage's reached code may call non-Core code only through a record value or a declared
input. A cached stage that calls a non-Core symbol (a Test, Infrastructure, Application, or CLI symbol, or a
non-allow-listed package) is an implicit input and is uncacheable (3.4 items 4 and 5). U2 checks this (9).

### 3.4 Implicit inputs: uncacheable rules

A stage is **uncacheable** if its static closure (9.3) contains any of the following. An uncacheable
stage is recomputed on every run, and no entry is written for it in any mode (6.5). Each run reports
`ANALYSIS_CACHE_UNCACHEABLE` once per stage.

1. **Reflection.** `typeof` used for reflection, `GetType`, `Activator`, `MethodInfo`, or `System.Reflection`
   APIs. Also reflection-based serialization: the generic method `ArtifactJson.Serialize<T>` calls
   `JsonSerializer.Serialize(document, CanonicalOptions)` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:54`),
   which reflects over `T` at run time. The cached payload codec must not reflect (3.7).
2. **Dynamic code.** `dynamic`, expression-tree compilation, or runtime code generation. *Generic guard; the
   search is acceptance criterion 1 of 9.*
3. **Native calls.** P/Invoke, `LibraryImport`, or any call through `NativeInterop.cs` (root of Core).
4. **Delegates from outside.** A delegate or function value not declared in the input record.
5. **Interface or virtual dispatch with implementations outside Core.** Class hierarchy analysis over Core
   gives the implementation set. If any implementation lies outside Core, the set is unknown. This covers Rule NC.
6. **Generic instantiation with a type argument outside Core.**
7. **Static initialization and static mutable state.** Static initializers are part of the Core identity.
   `BiosJumpTables` (`src/PSXRecomp.Core/Runtime/BiosJumpTables.cs:50`) is a static class, and
   `ArtifactJson.CanonicalOptions` (`ArtifactJson.cs:34`) is a static field. A static initializer or field read
   by the stage is uncacheable if it reads any implicit input in items 8 to 12. A static field written after
   initialization is uncacheable unless the write is proven to occur only before the first stage run.
8. **Culture.** Any reached API whose result depends on `CultureInfo.CurrentCulture` or `CurrentUICulture`.
   The pipeline's formatting is not uniform: some sites use `string.Create(CultureInfo.InvariantCulture, ...)`
   (for example `RomAnalysisPipeline.cs:100-101`), while others interpolate without it (`:286` and `:333`).
   Each reached site is classified by U2.
9. **Environment.** `Environment.*` (variables, machine name, user, OS version, current directory).
   *Generic guard; acceptance criterion 1 of 9.*
10. **Time.** `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `Stopwatch` used in a value, time zone
    lookups. *Generic guard; acceptance criterion 1 of 9.*
11. **File system.** `System.IO` reads, directory enumeration, file metadata, and path-based native calls. A file
    read is an input and must be in the record as a value, not read implicitly. **Core sites at `34845dd` that read
    file content or metadata.** The search covers `FileInfo`, `DirectoryInfo`, `File.`, `Directory.`, `FileStream`,
    `SafeFileHandle`, `stat`, `/proc`, `DllImport`, `LibraryImport`, and the native loader.
    - Content: `ChdReader.cs:103` (`new FileStream`, in `ChdReader.Open(string)` at `:101`); `LoadImageManifest.cs:286`
      (`Directives`), `:341` (`ParseExeImage`), `:512` (`ReadStrictRootsFile`), `:557` (`ReadRootsFile`), `:561`
      (`ReadRoots`); `ProcessTreeInterop.cs:101` (`File.ReadAllText` of `/proc/<pid>/stat`) and `:91`
      (`Directory.EnumerateDirectories("/proc")`, a listing).
    - Metadata: `LoadImageManifest.cs:62` (`new FileInfo(path).Length`, in `Read` at `:55`);
      `FileIdentityInterop.cs:21` (`GetFileInformationByHandle` on a `SafeFileHandle`, in `TryGetIdentity` at `:15`);
      `LinuxFileStatInterop.cs:34` (`stat(path, ...)`, in `Stat` at `:19`); `MacOsFileStatInterop.cs:27` and `:33`
      (`stat$INODE64` and `stat`, in `Stat` at `:19`).
    - Native loader: `NativeLibraryResolver.cs` loads the `PSXRecomp.Native` library file from `AppContext.BaseDirectory`
      (`:60`) for every `NativeInterop` call. The native calls are also item 3.
    - Not file access, listed under item 9: `LoadImageManifest.cs:67` (`Path.GetFullPath`, which reads the current
      directory for a relative path).
    - Not Core file access: the `fopen`, `/proc`, and `readlink` text in `RecompiledArtifactCodeGen.cs` and
      `RecompilerHostCodeGen.cs` is emitted C inside raw string literals. `LoadImageManifest.cs:101` and `:404` read the
      disc through an injected `ICdSectorSource`, not by opening a path.

    Direct-call check at this SHA: `ChdReader.Open(string)` has no Core caller. The `LoadImageManifest` readers are
    called only from `src/PSXRecomp.Cli`, `src/PSXRecomp.Infrastructure`, and tests. `TryReadParentIds` is called only
    from `src/PSXRecomp.Infrastructure` and tests. The identity and stat sites are called only from
    `src/PSXRecomp.Infrastructure/Standalone/FileSystemIdentityProbe.cs` (`:67`, `:68`, `:142`) and tests. The pipeline
    uses the stream overload (`RomAnalysisPipeline.cs:117`), which opens no file. **Transitive reachability from a cached
    stage is UNVERIFIED.** A listed site that a stage reaches makes it uncacheable (9.3, condition (i)). No stage may be
    cached on the claim that its closure reads no file until U2 checks it.
12. **Randomness.** `Random`, `RandomNumberGenerator`, `Guid.NewGuid`, and hash codes of reference types.
    *Generic guard; acceptance criterion 1 of 9.*
13. **Unresolved symbols or an assembly outside the allow-list.** The allow-list is the BCL and `PSXRecomp.Core`.
    The architecture attribute source is compiled into Core (3.8). Any other package is outside the list, and a
    closure that reaches one is uncacheable. The packages named in 3.8 are outside the list: `SharpCompress`
    0.50.4 (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:25`, used by `src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs:3`)
    and `loach.PureSharp` 0.1.6 (`PSXRecomp.Core.csproj:24`, `PrivateAssets="all"`). Whether a stage reaches
    `loach.PureSharp` is UNVERIFIED (acceptance criterion 3 of 9).

Items 8 to 12 are the implicit inputs of the BCL. The design does not assume the BCL is fixed.

### 3.5 Input records (values, not digests)

Each cached stage is a function of exactly one argument: its input record. The record holds the **values**
the stage reads, and nothing else is read. An upstream output is available as a value, either because the
upstream stage ran in this run or because its cached payload was decoded by the codec (3.7).

**N7.** `DecodeInput { byte[] TextSegment; uint TextStart; uint TextEnd; uint EntryPoint; }`

- The sweep is the full sweep from `EntryPoint` to `TextEnd` (5.1). `count` is not a field.
- `TextStart` is a field because `PsxExe.GetInstructionWord` reads `Header.TextStart` (`PsxExe.cs:23`). The
  stage body does not call `PsxExe` methods; it reads the record through a pure helper with the same arithmetic.
- `TextSegment` is the whole text segment. The sweep reads a subrange, so the record is a superset.

**N8.** `BlockInput { uint DecodeStart; IReadOnlyList<DecodedInstruction> SlicedDecodedList; }`

- `DecodeStart` is `EntryPoint` (`BasicBlockBuilder.cs:34`).
- `SlicedDecodedList` is the N7 output after the prefix slice (5.1), held as values. `BasicBlockBuilder`
  reads each instruction's `RawWord` (`BasicBlockBuilder.cs:27-31`), so the list carries the raw words.
- `instructionCount` is not a field, because `BasicBlockBuilder.cs:20` never reads it.

**N9.** `FunctionInput { uint EntryPoint; uint TextStart; uint TextSize; IReadOnlyList<uint>? ExplicitEntries;
IReadOnlyList<DecodedInstruction> SlicedDecodedList; IReadOnlyList<BasicBlock> Blocks; IReadOnlyList<CfgEdge> Edges; }`

- `EntryPoint` is read at `FunctionDiscovery.cs:105` (`var seeds = new SortedSet<uint> { entryPoint };`).
- `TextStart` and `TextSize` are read at `FunctionDiscovery.cs:173`.
- `SlicedDecodedList` is read for `RawWord` at `FunctionDiscovery.cs:95-96`.
- `ExplicitEntries` is null or a list; the codec distinguishes the two.

**N10.** `BiosInput { IReadOnlyList<DecodedInstruction> SlicedDecodedList; IReadOnlyList<BasicBlock> Blocks;
FunctionDiscoveryArtifact Functions; }`

**N11.** `ReportInput`, all as values:

- Identity: `DiscImageSha256` (`RomAnalysisPipeline.cs:413`), `SystemCnfBootPath` (`:414`, the raw boot path),
  `ExecutableFileName` (`:415`), `ExecutableFileSize` (`:421`), `ExecutableFileHash` (`:422`).
- Header: `EntryPoint`, `TextStart`, `TextSize`, `SpInitial`, `GpInitial` (`:416-420`).
- Decoded data: `SlicedDecodedList`, the decode failures, `Blocks`, `Edges`, `Functions`, and the BIOS evidence (`:432`).

The identity and header fields are in the N11 record because the report embeds them. An entry keyed on the
decoded data alone would be reused for a different disc with the same boot executable.

**N12a.** `CoverageInput { DiscImageAnalysisReport Report; ArtifactFixtureIdentity Fixture; }`

- `Report` is the N11 output as a value. `Fixture` is the identity value of 2.3, including `FixtureId`.
  **Nothing else.** `validations` is not in this record.

**Static check.** For each stage:

- The stage signature has exactly one parameter, the record.
- The body and the reached Core code read no parameter, field, or static value outside the record and the
  code permitted by 3.4.
- **Split check (N12a).** The classification's reached closure does not reference `BuildDifferential`, the
  `validations` parameter, or the `Differential` member.
- Verified by U2 (9). Until U2 passes, the check has not run.

### 3.6 Reads at run time

Each record is wrapped by a recording accessor during a run. The recorder reports, for each field, how many
times it was read. The recorder is an input to the review of a record. It is **not** a basis for removing a
field (4.1, V6).

### 3.7 Canonical encoding and payload codec

- **Key encoding.** A dedicated encoder produces the key text. `ArtifactJson` is not used:
  `ArtifactJson.CanonicalOptions` (`ArtifactJson.cs:34-38`) keeps declaration order (`ArtifactJson.cs:23`).
  - UTF-8, no byte-order mark, LF only, one line, no trailing newline.
  - Objects: `{`, members separated by `,`, `}`. Each member is `"name":value`, with no space after the colon.
    The name is a JSON string.
  - Member order: UTF-8 byte order of the member name, shorter prefix first.
  - Arrays: `[`, elements separated by `,`, `]`, in the given order.
  - Strings: `"`-delimited. `"` is `\"`, `\` is `\\`. Control characters U+0000 to U+001F are `\u00xx` with
    lowercase hex, including those with short forms. Other characters are written as their UTF-8 bytes.
  - Integers: base 10, no leading `+` or zeros; `-` only when negative.
  - `true`, `false`, `null`. No floats (an encoder given one fails).
- **Payload codec.** Every stage output, and every record, is encoded by an explicit codec written per type.
  The codec is injective (T-CODEC), it does not reflect (3.4 item 1), and its code is inside Core, so it is
  inside `codeIdentity`. A cached payload is the codec encoding of the stage output. A hit decodes it back
  into the value. A decode failure is R6 (6.3).

### 3.8 Dependency identity (build and runtime inputs outside Core source)

`dependencyIdentity` covers the build and runtime inputs that can change a cached stage's result and that
are not Core source.

- **Package identities.**
  - `loach.PureSharp` 0.1.6, `PrivateAssets="all"` (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:24`).
  - `SharpCompress` 0.50.4 (`PSXRecomp.Core.csproj:25`), used by `ChdCdCodec.cs:3`.
  - `loach.ArchitectureAnalyzer` 0.1.0, `PrivateAssets="all"`, a build-time analyzer whose `AdditionalFiles` input
    is `src/architecture.contract.json`, in the `EnableArchitectureAnalyzer` item group
    (`Directory.Build.props:44-47`, package at `:45`).
  - The resolved versions are read from `obj/project.assets.json`. The read is by exact name and is done by the host,
    once per run, before any stage runs: the production `IStageCodeIdentity` implementation (File access belongs to
    Infrastructure, 8.1). No site listed in 3.4 item 11 reads it (checked at `34845dd`), so it is not a Core read. The
    value enters the key as `dependencyIdentity`. The csproj pins direct packages only, and there is no lock file (CONFIRMED by listing).
- **SDK version and runtime version.**
  - At build time, a target reads the MSBuild property `$(NETCoreSdkVersion)` (name UNVERIFIED, U5) and writes it into a
    generated source file in `obj/`. The generated file is compiled into Core, so its value is inside the MVID of 3.3.
    The host does not read the generated file. Its value reaches the key only through the MVID.
  - At run time, the runtime version is read from `RuntimeInformation.FrameworkDescription`. This is an explicit
    exception to item 9 of 3.4: an identity string read as a dependency identity, not an environment value that
    affects a result.
- **Target framework.** `TargetFramework` is `net10.0` (`PSXRecomp.Core.csproj:3`).
- **Architecture attribute source.** `src/Architecture/PSXRecompArchitectureAttributes.cs` is a **source file**, not a
  separate assembly. `Directory.Build.props:22-24` links it into every project whose `CompileArchitectureAttributes` is
  `true`, and `PSXRecomp.Core.csproj:7` sets it. Its `internal` attribute types are compiled into Core, so the MVID of
  3.3 covers them.
- **Failure rule.** The host reads four values before any stage runs: the resolved package versions
  (`obj/project.assets.json`), the build-time analyzer identity (`Directory.Build.props`), the runtime version
  (`RuntimeInformation`), and the MVID of 3.3 (read from the running Core assembly). If any of them cannot be read, the
  stage is uncacheable and reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` (6.4, F5). The SDK version and the target framework
  are not host-read. They reach the key only through the MVID (UNVERIFIED for the target framework: that its metadata is
  compiled into Core). No constant is substituted. An installed CLI or GUI has no source tree, so
  `obj/project.assets.json` and `Directory.Build.props` are absent there. In that build every run is uncacheable, and
  each run reports `ANALYSIS_CACHE_KEY_UNAVAILABLE`.

### 3.9 Decision: line endings

**DEC-LF1.** Digests of text use LF-normalized content: CRLF becomes LF, a lone CR is kept, and a BOM is kept.
A checkout with different line-ending settings must not change a digest for the same source. The git warning
"LF will be replaced by CRLF" for the design file was observed during commit `6d038a0` (OBSERVED). The MVID does
not depend on the checkout's line endings, because it is computed from the compiled output.

## 4. Invalidation

### 4.1 Rules

- **V1. Changed input value.** A changed record value changes the record digest, so the key changes, and the
  entry is not reused.
- **V2. Changed code.** A changed `codeIdentity` changes every key.
- **V3. Changed dependency.** A changed package, SDK, or runtime version changes every key.
- **V4. Untracked value.** A value read but not in the record is a defect (3.5 static check). Until fixed, the
  stage is uncacheable.
- **V5. Unknown dependency.** An implicit input, or a call that breaks Rule NC, makes the stage uncacheable.
- **V6. Field removal.** A record field is removed only on **static evidence** from U2: the reachability analysis
  shows that no reached code reads it. A field with zero reads in a corpus run is **not** evidence that the field
  is unused. A zero-read field is a flag for review. The removal is confirmed by T-DEP-DEAD (7.3), on a fixture set
  separate from the one used by T-READ.

### 4.2 Stage consequences

- **N7.** Any Core change changes `codeIdentity` (DEC-CODE). A change to the text segment changes `TextSegment`.
  `count` is not a key component, and the slice of 5.1 handles it.
- **N8.** Changes to the N7 output after the slice, to `DecodeStart`, or to Core.
- **N9 to N11.** Changes to the upstream values, the record fields, or Core.
- **N10.** `BiosJumpTables` and `BiosCallFamily` are Core symbols, so they are inside `codeIdentity`.
- **N11.** The identity and header fields of 3.5. A different disc hash or boot path changes the N11 key even when
  the executable is identical.
- **N12a.** Changes to the report, the fixture identity, or Core. It does not change with `validations`.
- **N12d.** Never cached. Built at N14 from the current run's validations.

### 4.3 Identity verification (V-ID)

- **Rule V-ID.** Before any stage runs, the pipeline computes SHA-256 over the stream or array it runs and compares
  it with the supplied value. A mismatch fails the Input stage with a dedicated failure kind and no analysis runs.
  The stream path hashes from position 0 to end and then seeks back to 0. The stream contract requires seekability
  (`RomAnalysisPipeline.cs:70-72`).
- **Status.** V-ID is **not implemented**. Today the pipeline checks only that the supplied hash is non-empty
  (`RomAnalysisPipeline.cs:93-98`, `:194-198`). The CLI hashes the file in a separate pass (`CliInput.cs:106-109`,
  then `:115` on a second open). The test driver hashes separately (`RealRomAnalyzer.cs:102-104`). Nothing checks
  that the supplied value matches the bytes that run.
- **Rule C (cacheability; the only cacheability rule).** A stage is cacheable only when all three hold: (a) its U2
  verdict is `cacheable` for the running Core MVID (9.3), which requires every condition (a) to (l) of the verdict rule in 9.3 (3.4 items 1 to 13 not reached, criteria 1 to 6 met);
  (b) V-ID is implemented; (c) T-IDENT passes (7.3). The rule
  applies to writes, Verify-mode comparison, and Normal-mode hits alike, because an entry keyed on an unverified
  identity would be unsound. Until Rule C holds, the stage is uncacheable in every mode (6.5). This is the current
  proposal. Other sections restate it or refer to it.
- **Migration.** `src/PSXRecomp.Tests/DiscImage/DiscImageHostileSizeTests.cs:24` defines `Sha` as a literal that is
  not the hash of the bytes passed at `:347`. About 50 call sites exist in the test projects (9 in that file, 2 in
  `SyntheticChdReaderTests.cs`, 2 in `RealRomAnalyzer.cs`, 36 in `RomAnalysisPipelineTests.cs`, 1 in
  `RealRomProductionExecutionTests.cs`). How many pass a constant is UNVERIFIED (U7).

## 5. Partial re-analysis: the decode prefix

### 5.1 Where the prefix is sliced

- **CONFIRMED** Today the count is applied inside the decode loop: `DecodeInstructions(exe, count)`
  (`RomAnalysisPipeline.cs:501-502`, call at `:355`), with the loop bound `decoded < count` (`:511`). BasicBlockBuilder
  receives that count-limited list (`:381`).
- **Decision DEC-N7.** N7 computes the full sweep from `EntryPoint` to `TextEnd`, with no count. The count-limited
  list that N8 sees is produced by a slice **before N8**: the first `min(count, total)` items of N7's output. The
  slice's output is N8's record value (3.5). The slice reproduces today's list exactly (T-PREFIX).
- **Cost.** A cold N7 decodes the whole text segment rather than the first `count` words (UNVERIFIED, U4).
- **Failure rule.** A decode failure at address `A` is included in a count-limited result iff `A < entry + 4*M`,
  where `M` is the requested count.

### 5.2 Text shorter than the declared size

- **CONFIRMED** `RomAnalysisPipeline.cs:340-347` rejects only a zero-length `TextSegment`.
- **CONFIRMED** `PsxExe.GetInstructionWord` throws `ArgumentOutOfRangeException` when
  `offset < 0 || offset + 4 > TextSegment.Length` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:24-27`). The negative case
  covers an address below `Header.TextStart`.
- **CONFIRMED** The decode loop catches it (`RomAnalysisPipeline.cs:531`), records a `DecodeFailure`, and stops
  (`:533-538`). No exception escapes the stage.
- **Test T-SHORT** pins this.

## 6. Entries, reads, writes, and failure

### 6.1 Entry layout (proposal)

Each entry is a directory named by the key's SHA-256. It holds:

- `header`: `keyFormat`, the key text and its SHA-256, `payloadSha256`, `payloadSizeBytes`, `codeIdentity`,
  `dependencyIdentity`.
- `payload`: the stage output, encoded by the codec (3.7).
- `COMPLETE`: written last (proposal; no on-disk marker exists today). The driver records `Manifest` and `Complete`
  as stage results (`RealRomAnalyzer.cs:261-262`), and `RomAnalysisStage.cs:10` says the pipeline does not produce them.

Each key has its own directory, so a failure on one key cannot affect another.

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the key's directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, after W3 succeeds for every file.
- **W5.** Never write an entry for a stage that did not pass, or for a stage that is uncacheable (6.5).
- **W6.** If the target already exists, it is accepted only when its header key, its `payloadSha256`, and its
  `COMPLETE` marker all match this write. Otherwise the existing target is treated as corrupt (6.4, F1) and this
  write is retried once.

### 6.3 Read protocol

An entry is used only if all of the following hold:

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses, and its `keyFormat` is supported.
- **R3.** `header.key` equals the key computed from the current record and code identity.
- **R4.** The payload size equals `payloadSizeBytes`.
- **R5.** SHA-256 of the payload equals `payloadSha256`.
- **R6.** The payload decodes under the current codec.
- **R7.** Any exception during R1 to R6 is caught and treated as a corrupt entry.

R1 to R7 prove only that an entry is internally consistent. R3 ties the entry to the current inputs, and it is
correct only because the key is complete (sections 3 and 9). A consistent entry whose key matches but whose payload
differs from what the stage now computes would be used in Normal mode. That is the stale-entry case of T-STALE (7.3).

### 6.4 Failure: fail safe

- **F1. Corrupt.** An entry failing R1 to R7, or failing W6, is quarantined by renaming it to a name that records the
  failing check. The stage is recomputed. The run reports `ANALYSIS_CACHE_QUARANTINED` as a warning.
- **F1a. Terminal rule for a failed quarantine.** If the rename itself fails, the entry is **poisoned** for the run: the
  run does not read it again, recomputes the stage in memory, and writes nothing to that key's directory. The run reports
  `ANALYSIS_CACHE_QUARANTINED` with `quarantineFailed = true`. Other keys are in separate directories and are not
  affected. The entry is not deleted automatically.
- **F2. Missing.** A miss. The stage is recomputed. No diagnostic is reported.
- **F3. Uncacheable.** The stage is recomputed on every run, and the run reports `ANALYSIS_CACHE_UNCACHEABLE` once per stage.
- **F4. Partial failure.** If a stage fails, no entry is written for it or for any stage below it (W5).
- **F5. Key input unavailable.** If any part of the key cannot be read (the package file, `Directory.Build.props`, the
  runtime version, or the MVID of 3.3; the generated SDK file is not a host-read input, see 3.8), the stage is uncacheable and the run reports
  `ANALYSIS_CACHE_KEY_UNAVAILABLE`. No constant is substituted.
- **Codes.** Four codes are proposed: `ANALYSIS_CACHE_QUARANTINED`, `ANALYSIS_CACHE_UNCACHEABLE`,
  `ANALYSIS_CACHE_KEY_UNAVAILABLE`, and `ANALYSIS_CACHE_VERIFY_MISMATCH`. None is registered today. Under
  `docs/adr/019-diagnostics-and-recovery-contract.md` a code is a validated `SCREAMING_SNAKE` identity. The existing
  analysis codes (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`) do not cover cache faults.

### 6.5 Uncacheable stages in every mode

- **Default status.** Until Rule C (4.3) holds for a stage (its U2 verdict for the running MVID, V-ID, and T-IDENT),
  that stage is **uncacheable**. This is the default
  for every stage.
- **Consequence.** An uncacheable stage writes **no entry in any mode**, including Verify mode. Verify mode for an
  uncacheable stage recomputes and compares with nothing, because nothing was written. This closes the path in which
  an entry written in Verify mode is read later, after Normal mode is enabled.
- **Entry count.** Until the gate passes, the number of entries written for every cached stage is 0. This is the check of T-COLD-WARM.

## 7. Modes, seams, and tests

### 7.1 Modes, gates, and test-only seams

- **Default mode is Verify.** In Verify mode, a cacheable stage is recomputed cold. If an entry exists, its payload
  is compared with the cold payload byte for byte. A difference is `ANALYSIS_CACHE_VERIFY_MISMATCH` and a hard failure.
  The cold result is returned, never the entry.
- **Normal mode** returns a valid entry as a hit. It is used for a stage only when Rule C (4.3) holds for that stage, the same condition as for Verify mode:
  the U2 verdict is cacheable for the running MVID (9.3), V-ID is implemented, and T-IDENT passes.
- The default is set in `AnalysisCacheOptions` and is not optional.
- **Production cannot reach the test seams.** A test that needs a cacheable verdict, or a fake code identity, injects it
  through the Core port `IStageCodeIdentity`. The fake implementation lives in the test project. Production code may not
  reference test code (`docs/architecture-matrix.md:43`), so a production composition root cannot bind a test
  implementation. The production composition root binds the production implementation only. A verdict that comes from the
  test project is never a gate and never marks a production stage cacheable.

Injection seams (Core types, public ports):

- `IAnalysisCacheStore`: the store port (8.1). Tests pass an in-memory fake, which lives in the test project.
- `IStageCodeIdentity`: returns `codeIdentity`, `dependencyIdentity`, and the stage's uncacheable verdict.
  Production binds the gate-backed implementation (9.3). Tests bind a fake.
- `IStageInputRecorder`: wraps an input record and counts reads per field (3.6).
- `AnalysisCacheOptions`: `Mode` (default `Verify`), the store, the code identity provider, the recorder, and a
  diagnostics sink.

### 7.2 Test scope

Several tests run the same code twice, or substitute a digest or a verdict in a fake. They check the cache mechanics.
They **cannot** detect a missed input, because the cold and warm runs share the same code and the same records. Two
kinds of test are not of that kind:

- **T-GOLDEN** compares cold payloads with committed payloads from an earlier revision, so it crosses a change of code.
- **T-CLOSURE-RT** compares the symbols an instrumented run executes with the closure that U2 analyses. It checks the
  analysis against the executed paths on the current revision. It does not compare two revisions, and it does not
  prove the static closure (9, criterion 4 does that).

### 7.3 Tests

Synthetic inputs come from `src/PSXRecomp.Tests/RealRomAnalysis/`: `SyntheticDiscBuilder.cs`, `SyntheticChdBuilder.cs`,
and `SyntheticIsoBothEndianTests.cs` (they exist; their APIs are UNVERIFIED, U8). No BIOS, ROM, or CHD content is used.
Clocks are injected. Temp paths are not asserted.

Two fixture sets are used, and they must not overlap:

- **Fixture set A** drives T-READ and T-COLD-WARM.
- **Fixture set B** drives T-DEP-DEAD and T-DEP-INPUT. Set B uses other synthetic configurations (different counts,
  text layouts, and disc identities), so a field read only in set-B configurations is not missed.

**Gating.** The tests below need a stored entry or a cacheable verdict. Their precondition is Rule C (4.3) for the
stage under test. They are **not run in production** before that precondition holds: T-COLD-WARM (hit assertions),
T-DEP-INPUT, T-IDENT (until V-ID exists), T-IDENT2, T-STALE (Normal-mode part), T-DEP-DEAD (step b), T-CORRUPT, T-QUAR-FAIL, T-W6, T-MISSING,
T-PARTIAL, T-DIFF-RUN (warm half), and T-GOLDEN. Before Rule C holds, a test-only verdict may exercise the mechanism.
Such a run proves the mechanism only. It does not count toward any gate, and it does not make a production stage
cacheable.

| ID | Test | Asserts | Scope and gate |
|---|---|---|---|
| T-COLD-WARM | Cold and warm equality, and entry count | For each set-A fixture: run cold, store, run warm. **While a stage is uncacheable (the default), the test asserts that the number of entries written for it is 0 and the number of hits is 0**, and that the warm output equals the cold output byte for byte. Once Rule C holds for a stage, the test also asserts hits equal the number of cacheable units, and the warm payload equals the cold payload per stage. | Runs now (entry count 0). Hit assertions run after Rule C holds |
| T-DEP-INPUT | Changed record value is not reused | For each stage and each record field, on set-B fixtures, change that one value. The key differs, the entry is not reused, and the recomputed payload is stored under the new key. | **After Rule C holds for the stage.** Before that, not run in production |
| T-DEP-CODE | Changed code identity is not reused | Substitute a changed `codeIdentity` in `IStageCodeIdentity` (test fake). The entry is not reused. | Test-only verdict; proves the mechanism, not any production cacheability |
| T-DEP-DEAD (MAJOR-2 of r8, LOW-3 of r8) | Field removal is checked by value and by static verdict | For each field whose set-A reads are zero, and only for those: (a) the U2 static verdict must say "unread"; (b) on set-B fixtures, the baseline run with the **unchanged** record has key **K0** and stores payload **P0** under K0. The changed record's key is **K1**, and its cold result is **C1**. The field is accepted only if **C1 equals P0** byte for byte. A field that fails (a) or (b) stays in the record. | Step (b) reads a stored entry, so it runs **after Rule C holds for the stage**. (a) runs with U2 |
| T-IDENT2 (C1 of r7) | Same executable, different disc identity | Two fixtures with the same boot executable bytes and different disc hashes (and, in a second case, a different boot path). The N11 and N12a keys differ, the second run does not reuse the first run's entry, and each payload contains its own disc hash and boot path. | **After Rule C holds for N11 and N12a** (it observes reuse). The key difference alone can be checked before |
| T-DIFF (MAJOR-1 of r7) | N12a is independent of validations | For a fixed `(report, fixture)`, vary `validations` (none, one run, two runs, different order). The N12a payload is byte-identical in every case. The document from N14 differs only in the `Differential` section. | Runs after U1 (N12a exists as a function) |
| T-UNCACHE | Uncacheable stage writes nothing | For the default status, and with an injected uncacheable verdict, each stage recomputes every run, writes no entry in Verify or Normal mode, and reports `ANALYSIS_CACHE_UNCACHEABLE` once. | Runs now |
| T-KEY-UNAVAIL (M8 of r8, MINOR-7 of r7) | Unreadable key input | Make the package file, `Directory.Build.props`, the runtime version, or the MVID unreadable in a fake, one at a time. The stage is uncacheable and reports `ANALYSIS_CACHE_KEY_UNAVAILABLE`. No constant key is produced. | Runs now (fake inputs) |
| T-READ | Record matches reads | Run each stage under the recorder on set-A fixtures. Every record field's read count is recorded. A zero-read field is listed as a flag for review (V6). It is not removed by this test. | Runs after U1 (records exist) |
| T-STATIC (M2 of r8, MAJOR-1 of r7) | Static check of the record and the split | Each stage signature has one parameter. Its reached body reads nothing outside the record. The N12a closure does not reach `BuildDifferential`, `validations`, or `Differential`. | Static, U2 (9) |
| T-CLOSURE-RT | Executed paths against the analysed closure | Run each candidate stage under an instrumented build that records executed methods and the environment, time, file, culture, and random APIs. Every executed Core method must be in the analysed closure, and no implicit-input API may execute. | Exercised paths only, current revision; instrumentation UNVERIFIED (9, criterion 5) |
| T-CORRUPT | Corrupt entries fail safe | For each corruption (zero-length file, truncated payload, flipped byte, wrong key in header, unsupported `keyFormat`, malformed header, schema mismatch), the run recomputes, the output equals the cold output byte for byte, and `ANALYSIS_CACHE_QUARANTINED` is reported once per corrupt entry. | Needs an entry; runs after Rule C holds for a stage. A test-only verdict may exercise it earlier |
| T-QUAR-FAIL (m6 of r8) | Failed quarantine is terminal | Make the rename fail for a corrupt entry. The run recomputes, reports `quarantineFailed = true`, writes nothing to that key, and writes successfully for a different key in the same run. | Needs an entry; same as T-CORRUPT |
| T-W6 (m5 of r8) | Existing target | A write whose target exists with a matching `payloadSha256` but a different key or a missing `COMPLETE` is treated as corrupt, not accepted. | Needs an entry; same as T-CORRUPT |
| T-MISSING | Missing entries are silent | Delete an entry. The run recomputes, the output equals the cold output, and no diagnostic is reported. | Needs an entry; same as T-CORRUPT |
| T-PARTIAL | Partial failure writes nothing below | A failure injected at N8 writes no entry for N8 or later. The N7 entry is the only one written. | Needs an entry; after Rule C holds for N7 |
| T-STALE | Stale-but-consistent entry (residual risk) | An entry whose header and `COMPLETE` marker match the current key, with a payload that differs from the cold result. In **Verify** mode (default) the run reports `ANALYSIS_CACHE_VERIFY_MISMATCH` and returns the cold result. In **Normal** mode the entry is returned. | **Normal mode is available only after Rule C (4.3) holds.** Verify-mode part may run with a test-only verdict |
| T-SHORT (5.2) | Short text segment | An executable whose text segment is shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment, and no exception escapes. | Runs now |
| T-PREFIX (5.1) | Prefix slice | N7 output sliced to `M` before N8 equals today's count-limited decode at `M`, including failures filtered by `A < entry + 4*M`. | Runs after R-0 (U1) |
| T-DIFF-RUN | Differential on and off, end to end | Cold and warm runs with validations supplied and without, both byte for byte. N12d is never read from the cache. | Cold half runs now. Warm half runs after Rule C holds |
| T-IDENT (4.3) | Identity verification | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind and no cache write. The CLI, the GUI, and the test driver, given the same bytes, compute the same verified hash. **Part of Rule C (4.3): this check gates writes, Verify mode, and Normal-mode hits alike.** | Runs after V-ID is implemented (not yet) |
| T-CODEC | Key encoder and payload codec | The key text of a fixed sample equals the committed text, and its SHA-256 equals the committed value. Member order is UTF-8 byte order. Floats are rejected. The codec round-trips each record and stage type, and is injective on the fixtures. | Runs now |
| T-LF | Verbatim string lint (DEC-LF1) | Fails if a file on a dependency input path contains a multi-line verbatim string literal. | Static, runs now |
| T-GOLDEN | Cross-revision regression | Committed golden payloads, produced from synthetic fixtures at an earlier revision, are compared with a cold recompute. Each cacheable stage must have at least 4 compared entries (one per fixture), so the check cannot pass with zero comparisons. An intentional payload change requires regenerating the goldens with the named command `regenerate-analysis-goldens`, and the golden diff is reviewed. Location: `src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGolden/`, one `<unit>/<fixtureId>.golden.json` per pair (proposal). | **Crosses revisions.** Runs once a stage is cacheable |

Determinism rules for every test: no wall-clock values in keys, headers, or assertions; no dependence on the order of
unsorted collections; no reliance on the temp directory name.

## 8. Architecture, store, and AI results

### 8.1 Architecture placement

The architecture matrix (`docs/architecture-matrix.md`) assigns:

- **CONFIRMED** Domain (`PSXRecomp.Core`) owns ports and deterministic contracts (`architecture-matrix.md:22`, rule 1
  at `:64`) and must not reference `PSXRecomp.Infrastructure`.
- **CONFIRMED** Infrastructure owns host adapters, including `File` and `Directory` access (`architecture-matrix.md:24`,
  `:55`, rule 2 at `:65`).
- **CONFIRMED** Application and CLI do not call `File.*` or `Directory.*` for ordinary logic (rule 3 at `:66`).
  Concrete adapter wiring is allowed at one composition-root site with an AARC002 suppression (`:47`).
- **CONFIRMED** Production code does not depend on test code (`architecture-matrix.md:43`).

Resolution:

- Core: `IAnalysisCacheStore`, the key and entry types, the key encoder and payload codec (3.7), the input records,
  `IStageCodeIdentity`, `IStageInputRecorder`, `AnalysisCacheOptions`, and the validation of R1 to R7 as checks over
  bytes already read.
- Infrastructure: `FileAnalysisCacheStore : IAnalysisCacheStore`, marked `[Infrastructure]`, with the file operations of
  6.2 and 6.4.
- Precedent: `src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs` implements `IProjectMetadataStore`, defined in
  `PSXRecomp.Core.Project` (Issue #42).
- **Writer placement (LOW-1).** The artifact writer is currently `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs`
  (`:44-50`), in the test project. Moving it into production is U3.
- **ADR required** before implementation: the port and adapter, the move of persistence out of the test project (U3),
  and the reading of the MVID (9.3). This document does not create one.

### 8.2 Stores and sharing

- The CLI and GUI pass a store root and the same key function. The store root's location is UNVERIFIED (U6).
- Cross-process concurrency uses 6.2.

### 8.3 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists.
- **CONFIRMED** `EvidenceType.AIPrediction = 6` exists at `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`,
  next to `AnalyzerFinding = 5` (`:16`).
- **Rules.** An AI result is advisory and is never stored as an analysis unit or in an analysis document. It is reused
  only when its key matches the digests of the artifacts it was computed from, the prompt or template revision, the model
  identifier, and the model settings. Whether it uses `EvidenceType.AIPrediction` is decided in its own ADR.

## 9. U2: the reachability gate, as a standalone task

U2 is the analysis that can make a stage cacheable. This section specifies it as a task. It has **not been run**. It is
not implemented by this design, and no production code changes with it.

### 9.1 Dependencies, owner, and tools

- **Depends on U1.** The N12a classification function exists only after the split of `Analyze` (1.2, 2.2). Roots
  for N12a cannot be specified before U1 is done. Criteria 1 to 4 for N7 to N11 can run before U1.
- **Tool.** A Roslyn-based analysis on the semantic model of `PSXRecomp.Core` (`Microsoft.CodeAnalysis`). The tool
  is a separate analysis program, in the test project or a tools folder. It is not a production dependency (8.1).
  Its package availability and version are UNVERIFIED.
- **Owner.** Not assigned in this document. The coordinator assigns the owner.
- **Non-goals of U2.** U2 does not implement the cache, does not change production code, does not declare any stage
  cacheable by itself, and does not implement V-ID.

### 9.2 Input

- The `PSXRecomp.Core` assembly built from the commit under review. Its MVID is read from the built assembly.
- The root list: the Core methods that each cached stage enters.
  - N7: the decode sweep (`RomAnalysisPipeline.cs:501-543`, the sweep without count).
  - N8: `BasicBlockBuilder.Build` (`BasicBlockBuilder.cs:20`).
  - N9: `FunctionDiscovery.Build` (`FunctionDiscovery.cs:81-88`).
  - N10: `BiosCallRecognizer.Recognize` (`BiosCallRecognition.cs:140`).
  - N11: the report assembly (`RomAnalysisPipeline.cs:400-444`).
  - N12a: the classification function split out of `Analyze` (`RealRomCoverageAnalyzer.cs:88`). **Exists only after U1.**
- The static reference graph of Core, from the semantic model.

### 9.3 Output, storage, and the gate

**Output.** For each root:

- `cacheable` or `uncacheable`, with evidence for each uncacheable verdict (the reached symbol and its rule in 3.4).
- For each record field of each stage: a static verdict, `read` or `unread`, with the reading site.
- The implicit-input scan of 3.4: each reached site that reads culture, environment, time, file system, randomness, or
  reflection, with its file and line.
- The runtime report of T-CLOSURE-RT: the executed symbols, and any that the static analysis did not reach.

**Storage.** The output is one JSON file per Core MVID, written by the U2 tool into
`src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGate/<mvid>.json` (proposal). Its fields: `mvid`, `commit`,
`toolVersion`, the per-stage verdicts, the field verdicts, the scan summary, the runtime summary, and the result of each
acceptance criterion.

**Verdict rule.** A stage's verdict is `cacheable` only when every condition below holds on the static closure of the
stage (criterion 4 of 9.4). Executed paths do not establish a condition: T-CLOSURE-RT (criterion 5) runs only the paths
a fixture executes, and an item reached on an unexecuted path still counts. If any condition cannot be established, the
verdict is `uncacheable`. Each uncacheable verdict names the reached symbol and the condition (9.3 output).

- (a) 3.4 item 1, reflection, including `ArtifactJson.Serialize<T>` (`ArtifactJson.cs:54`): none reached. Established
  by a static search of the closure for reflection and reflection-based serialization (criterion 1).
- (b) 3.4 item 2, dynamic code: none reached. Established by the criterion 1 search.
- (c) 3.4 item 3, native calls (every `DllImport` and `LibraryImport` in the closure, including `NativeInterop.cs` and
  the loader of `NativeLibraryResolver.cs`): none reached. Established by a static search of native declarations and
  the closure's call graph.
- (d) 3.4 item 4, delegates or function values not declared in the record: none reached. Established by a static scan
  of delegate and lambda creation sites in the closure, each matched to a record field.
- (e) 3.4 item 5, interface or virtual dispatch with an implementation outside Core: none. Established by class
  hierarchy analysis over Core, with every implementation inside Core.
- (f) 3.4 item 6, generic instantiation with a type argument outside Core: none. Established by a static scan of the
  constructed generic types and methods in the closure.
- (g) 3.4 item 7, static initializers and static state: none reads an input of items 8 to 12, and no static field is
  written after its first stage run. Established by a static scan of the static members in the closure.
- (h) 3.4 items 8 to 10 and 12, culture, environment, time, and randomness: none reached, or every reached site is
  classified (criteria 1 and 2) and shown not to affect a value. Environment includes the current-directory read at
  `LoadImageManifest.cs:67`.
- (i) 3.4 item 11, file system: no site listed in item 11 is reached. Established by the criterion 1 search over the
  listed sites, with reachability from the closure.
- (j) 3.4 item 13, unresolved symbols or assemblies outside the allow-list: none reached, including `SharpCompress` and
  `loach.PureSharp`. Established by criterion 3.
- (k) Criteria 1 to 3 are complete for the stage, so that items 8 to 10, 12, and 13 are decided, and the output is
  recorded with the running MVID (criterion 6).
- (l) Criteria 4 and 5 of 9.4 pass for the stage.

**Binding to the MVID.** The gate is read by the **host** (composition root or CLI). Core's file reads are listed in
3.4 item 11, and the gate read is not one of them. The host compares the file's `mvid` with the MVID of the running Core assembly, which Core exposes as a
value. If they are equal, the stage's verdict is `cacheable`, and Rule C (4.3) holds for the stage, the host passes the verdict into
`AnalysisCacheOptions` as a value. Otherwise the stage is uncacheable. A missing, unreadable, or mismatched gate file is
uncacheable and reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` (6.4, F5).

**Invalidation.** Any change to Core changes the MVID, so every earlier gate file no longer matches, and every stage is
uncacheable again. Re-enabling a stage requires: re-running U2 on the new MVID, committing the new gate file, and passing
the acceptance criteria for that stage. The earlier gate file is not deleted automatically. Its mismatch is the signal.

**Gate on CI (non-blocking).** CI reports whether the committed gate matches the build MVID. A mismatch is a warning and
keeps stages uncacheable. It is not a build failure, because an uncacheable stage is safe.

### 9.4 Acceptance criteria

1. **Guards and searches.** Guards 2, 9, 10, and 12 of 3.4 are searched for in the reached code, and each search and its
   result is recorded. A guard with no occurrence in the reached code is recorded as such. A guard with an occurrence
   becomes a rule for the stage that reaches it.
2. **Culture.** Each reached culture-sensitive site is classified. In particular `RomAnalysisPipeline.cs:286` and `:333`.
3. **Packages.** The reachability of `SharpCompress` (`ChdCdCodec.cs:3`) and of `loach.PureSharp`
   (`PSXRecomp.Core.csproj:24`) is determined for each stage and recorded.
4. **Static closure (the proof).** T-STATIC passes for each stage that the output marks cacheable. This criterion is the
   static proof of the closure for that stage.
5. **Executed paths.** T-CLOSURE-RT passes for each cacheable stage. It covers **executed paths only**. It is a consistency
   check, not a proof. Criterion 4 is the proof.
6. **Recording.** The output is produced for the commit under review and recorded with its MVID, in the file of 9.3.

Criteria 4 and 5 are conditions of the U2 verdict (9.3), and Rule C (4.3) adds V-ID and T-IDENT. Until Rule C holds,
the stage stays uncacheable (6.5). There is one gate, and it is Rule C.

## 10. Open items (UNVERIFIED unless stated)

- **U1.** Extract each stage into a function that takes one record, and split `Analyze` (1.2, 2.2). Behavior-preserving,
  UNVERIFIED. Prerequisite of N12a in U2 (9.1).
- **U2.** The gate of section 9. A blocker for every stage.
- **U3.** Move the artifact writer and the persistence out of the test project, and enumerate the project fields that reach
  analysis (8.1, 1.3). Needs an ADR.
- **U4.** Measure cold N7 over the full text segment (5.1).
- **U5.** Confirm `$(NETCoreSdkVersion)` in this repository, the generated-file mechanism of 3.8, and the build determinism
  that the MVID of 3.3 relies on.
- **U6.** Read the caller of `AnalyzeAndExecuteFromDisc` (`RealRomTitleExecutionService.cs:110-117`), and decide the store root.
- **U7.** Count the test call sites that pass a constant SHA (4.3, Migration), and the test callers of `RunFromChd(byte[])`
  (2.1 lists only the Core caller).
- **U8.** Read `PathRedactor.cs` and the synthetic builder APIs before the tests are implemented.
- **U9. Closed.** `ICdSectorSource.cs:11` (`CdDiscToc`) and `:56` (`ICdSectorSource`).
- **U10.** Confirm which declared stage outputs embed `ArtifactJson` content (`RealRomCoverageDocument.cs:245`, `RecompilerContract.cs:1126`).
- **V-ID.** Implement the identity verification of 4.3. One of the three conditions of Rule C (4.3), with U2 and T-IDENT.

## 11. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact schemas.
- No cache implementation. This is the design only.
- No stage is declared cacheable in this document. Every stage is uncacheable until Rule C (4.3) holds for it.
- No AI result producer.
- No change to the project file format.
- No ADR is created here.
- No U2 execution. U2 is specified in 9 and assigned by the coordinator.

## 12. Finding traceability

### 12.1 Review of `c0f916e` (r9), final review

| ID | Finding | Status in r10 | Resolved in |
|---|---|---|---|
| MAJOR-A | Records hold digests only | Resolved in r9 | 2.4, 3.2, 3.5 |
| MAJOR-B | Gated stages are not uncacheable; Verify writes | Resolved in r9 | 6.5, 7.3 (T-COLD-WARM) |
| MAJOR-C | V-ID not a gate | Resolved in r9 | 4.3, 7.1, T-IDENT |
| MEDIUM-D | U2 as an independent task | Specified in r9; completed in r10 (owner, tool, dependency on U1, storage, MVID binding, invalidation) | 9.1 to 9.4 |
| MEDIUM-E | Entry points and citations | **Partially resolved in r9; corrected in r10.** r9 omitted the Core caller `DiscImageAnalyzer.cs:29` of `RunFromChd(byte[])` and the Core caller `DiscImageAnalyzer.cs:44`, the test caller `RealRomProductionExecutionTests.cs:75`, and the driver call `RealRomAnalyzer.cs:122`, and it stated that no caller exists outside the tests. The status "resolved" recorded in r9 was not accurate. | 2.1 (all callers listed, with the test caller of the Stream overload) |
| MEDIUM-1 (r10 review) | Missing callers of `RunFromChd` | Resolved in r10 | 2.1 |
| MEDIUM-2 (r10 review) | Tests that need U2 are not marked gated | Resolved in r10 | 7.3 (gating paragraph and the Gate column), 7.1 (test-only seams cannot reach production) |
| MEDIUM-3 (r10 review) | U2 specification gaps | Resolved in r10 | 9.1 (dependency on U1, owner, tool, non-goals), 9.3 (storage, MVID binding, gate, invalidation, CI check), 9.4 (criterion 5 scope) |
| LOW-1 (r10 review) | Writer path | Resolved in r10 | 2.2 (N15 row), 3.3 (non-Core paragraph), 8.1 |
| LOW-2 (r10 review) | §12.2 defers to earlier text | Resolved in r10 | 12.2 below lists the substance of each finding |

### 12.2 Earlier findings, with their resolutions in this document

The review of `29e1488` (r7) and the review of `cdd1634` (r8) are summarized here, so that this document alone can be traced.

- **r7 MAJOR-1** (N12a reads validations): N12a is a function of `(report, fixture)` only, and `Differential` is in
  N12d. Record in 3.5, split check in 3.5, test T-DIFF.
- **r7 MAJOR-2** (zero-read fields removed on corpus evidence): a field is removed only on U2 static evidence and a
  T-DEP-DEAD check on set B (4.1 V6, 7.3). Zero-read counts are flags only (T-READ).
- **r7 MAJOR-3** (non-Core scope): 3.3 gives the non-Core paragraph and Rule NC, and 3.8 names the packages and the build
  inputs.
- **r7 MINOR-1** (a contradictory sentence on upstream code changes): deleted. The data flow is in 2.4.
- **r7 MINOR-2** (culture): reworded in 3.4 item 8, with `:286` and `:333`.
- **r7 MINOR-3** (guards without a current occurrence): labeled as generic guards in 3.4 items 2, 9, 10, and 12, with
  acceptance criterion 1 of 9.4.
- **r7 MINOR-4** (the serialization call): 3.4 item 1 gives `JsonSerializer.Serialize(document, CanonicalOptions)` inside
  `ArtifactJson.Serialize<T>`, at `ArtifactJson.cs:54`.
- **r7 MINOR-5** (N9 citations): `FunctionDiscovery.cs:105` for the entry, `:173` for the text bounds (3.5).
- **r7 MINOR-6** (missing package citations): 3.8 names `loach.ArchitectureAnalyzer` (`Directory.Build.props:45`) and
  `loach.PureSharp` (`PSXRecomp.Core.csproj:24`).
- **r7 MINOR-7** (SDK and runtime version mechanism): 3.8 gives the generated file at build time and the runtime version
  from `RuntimeInformation`, with T-KEY-UNAVAIL.
- **r7 MINOR-8** (SharpCompress outside the allow-list): 3.4 item 13.
- **r7 MINOR-9** (wording of the cross-revision claim): 7.2 (only T-GOLDEN crosses revisions).
- **r8 MAJOR-1 to MAJOR-3 (the review of `cdd1634`)**: resolved in r9, see 12.1 (MAJOR-A to MAJOR-C).
- **r8 MEDIUM-D and MEDIUM-E, LOW-1 to LOW-3**: resolved in r9, see 12.1 (MEDIUM-D completed in r10, MEDIUM-E corrected in r10).

The blockers are **U2** (section 9) and **V-ID** (4.3), with T-IDENT. Rule C (4.3) is the only cacheability rule. Until it holds for a stage,
that stage is uncacheable in every mode, and this document claims no stage is safe.

### 12.3 Review of `f77183e` (r10)

| ID | Finding | Status in r11 | Resolved in |
|---|---|---|---|
| F1 (MAJOR) | "Core performs no file reads" stated as fact, and 9.3 relied on it. Core has file-reading sites (ChdReader, LoadImageManifest, ProcessTreeInterop) | Resolved in r11: the sites were listed, reachability marked UNVERIFIED, and the verdict rule of 9.3 made U2 check it. **Corrected in r12:** the list is widened to content, metadata, and native-loader sites (3.4 item 11, see 12.4 R1), and every listed site is covered by 9.3 condition (i) | 3.4 item 11, 9.3 (verdict rule and binding), 3.8 |
| F2 (MEDIUM) | T-IDENT row said "Required for the Normal-mode gate", which conflicts with Rule C | Resolved in r11: the row now states that Rule C applies it to writes, Verify, and Normal | 7.3 table (T-IDENT), 4.3 (Rule C) |
| F3 (MEDIUM) | Criteria 4 and 5 of 9.4 were named as a gate in one place and absent from Rule C | Resolved in r11: criteria 4 and 5 are conditions of the U2 verdict, so they are part of Rule C; one gate | 9.3 (verdict rule), 4.3 (Rule C), 9.4 (end) |
| F4 (LOW) | analyzerFingerprint wording claimed the repository has no occurrence | Resolved in r11: absent from the code tree at `f95bbae` and `684b573`, present in `c8299d4`. **Corrected in r12:** all 12 matches at `c8299d4` are in `docs/design/issue-43-analysis-cache.md`, none in code; this document mentions it in 2.2 and in this row (see 12.4 R4) | 2.2 (paragraph after the table) |
| F5 (LOW) | Revision label r10 and no traceability for this round | Resolved in r11: label updated and this table added | header, Revision line, 12.3 |
| F6 (LOW) | The generated SDK file was both compiled into Core and listed as a host-read input | Resolved in r11: the host does not read it; its value enters through the MVID | 3.8 (SDK bullet, failure rule), F5, T-KEY-UNAVAIL |

### 12.4 Review of `34845dd` (r11)

| ID | Finding | Status in r12 | Resolved in |
|---|---|---|---|
| R1 (MAJOR) | The file-read list was incomplete: `FileInfo`, the identity handle query, and the stat interops were not searched | Resolved in r12: the search was widened, and the list covers content, metadata, and the native loader, each with line numbers. Every listed site is covered by 9.3 condition (i) | 3.4 item 11, 9.3 (condition (i)), 12.3 F1 |
| R2 (MAJOR) | The verdict rule did not include the 3.4 uncacheable items, criteria 1 to 3, or criterion 6, so a path no fixture executes could be cacheable | Resolved in r12: the verdict rule lists conditions (a) to (l), one for each 3.4 item and criteria 1 to 6, with the method that establishes each. Any condition that cannot be established makes the verdict uncacheable. Rule C (a) cites the whole set | 9.3 (verdict rule), 4.3 (Rule C) |
| R3 (MEDIUM) | T-IDENT was missing from the list of tests not run in production | Resolved in r12: T-IDENT is listed until V-ID exists | 7.3 (gating paragraph) |
| R4 (LOW) | analyzerFingerprint: the location of the 12 matches was not stated, and "only in this paragraph" was false | Resolved in r12: all 12 matches at `c8299d4` are in the design document, none in code; the document mentions it in 2.2 and in 12.3 F4 | 2.2, 12.3 F4 |
| R5 (LOW) | The 3.8 failure rule covered bullets the host does not read and omitted the MVID | Resolved in r12: the failure rule lists the four host-read values (package versions, build-time analyzer identity, runtime version, MVID); the SDK version and the target framework are stated as not host-read | 3.8 (failure rule), F5, T-KEY-UNAVAIL |
