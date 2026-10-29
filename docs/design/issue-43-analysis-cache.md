# Issue #43: Incremental Analysis and Cache (design, r24)

- Status: **Design. No stage is cacheable.** Rule C (4.3) is the only cacheability rule: a stage is
  cacheable only when its U2 verdict, V-ID, and T-IDENT all hold. Until Rule C holds for a stage, that
  stage is uncacheable in every mode. An uncacheable stage is recomputed on every run, and no entry is
  written for it, in Verify mode as well (section 6.5). The hit path is not executed by any production run
  until Rule C holds, and no test may claim a production stage is cacheable (7.1, 7.3). V-ID is not implemented, so Rule C
  cannot hold for any stage, and a matching gate file does not make a stage cacheable.
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r24. Addresses the review of `0a5bcff` (r23): seven findings, mapped in 12.16. The findings of `e3db1c8` (r22) are in 12.15, `2311b62` (r21) in 12.14, `597e1aa` (r20) in 12.13, `f4b95ab` (r19) in 12.12, `882500e` (r18) in 12.11, `5e658f5` (r17) in 12.10, `6a871a5` (r16) in 12.9, `e1afbba` (r15) in 12.8, and the findings of
  `6f1494a` (r14) are in 12.7, `d11733c` (r13) in 12.6, `d1c41ac` (r12) in 12.5, `34845dd` (r11) in 12.4, and
  `f77183e` (r10) in 12.3. The direction of r9 to r15 is not re-examined. Section 12 maps each finding to its resolution, and the resolutions are stated
  in this document, not only in the review text.
- Source identity: the reviewed head is `0a5bcff`. The `src/` tree is identical through `0a5bcff`, because this revision
  changes only the doc. It is also identical at `34845dd`, `d1c41ac`, `d11733c`, `e1afbba`, `6a871a5`, `5e658f5`, `882500e`, `f4b95ab`, `597e1aa`, and `2311b62`
  (`git diff --stat` is empty across them), so every check label in this document is `2311b62`. The grep for the seams and
  codes (7.3) finds none at `882500e`, the same result as at `e1afbba` and `6a871a5`. SHAs in section 12 name the
  reviewed heads, not check labels.

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
| N12a classification | not a candidate until U1 creates the classification unit; uncacheable until then | `CoverageInput` (3.5): the N11 report and the fixture identity. Nothing else | `RealRomCoverageAnalyzer.cs:88`, classification `:223`, `:234-261`; `LowerProgram` call at `:251`; definition `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176` |
| N12d differential | not cached | `validations`, supplied on that run | `RealRomCoverageAnalyzer.cs:85-88`, assignment `:185`, `BuildDifferential` `:377-382` |
| N14 artifact documents | not cached | runs each run | `DeterministicArtifactBuilder.cs:74`, `:96-99`, `:115-121`, `:124`; `ArtifactJson.cs:32` |
| N15 persistence | not cached | runs each run | `src/PSXRecomp.Tests/RealRomAnalysis/RealRomArtifactWriter.cs:44-50` (LOW-1: the writer lives in the test project; moving it is U3) |

**N13 is not a stage.** The numbering has no N13. The first revision (`6d038a0`) listed N13 as the coverage
probe, which calls `MipsToIrLowerer.LowerProgram` (call at `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:251`,
in `ProbeLowerable`, `:234-261`). That probe is part of the N12a classification row above. The number is not reused.
Every "N1 to N15" range in this document means the stages in this table, without N13.

**Replaced design (`analyzerFingerprint`).** The identifier `analyzerFingerprint` is absent from the code tree at
`f95bbae` and at `684b573` (zero matches tree-wide in each). It appears in revision `c8299d4` (12 matches tree-wide, all in `docs/design/issue-43-analysis-cache.md`; none in code).
In the current document it occurs in six lines: this paragraph (2.2), the F4 row of 12.3, the R4 row of 12.4, the S2 row of 12.5, the T2 row of 12.6, and the U5 row of 12.7. Its per-stage successor, the fingerprint sets of `684b573`, is withdrawn as well. This section describes a
replaced design. The N7 to N15 closure replaces the fingerprint: the whole Core MVID (3.3) gives the code identity,
and the reachability closure of each stage (3.4, 9) decides whether that stage may use the cache at all. No
code-fingerprint identifier is used in the current design.

**N12a and N12d.** `Analyze(report, fixture, validations = null)` (`RealRomCoverageAnalyzer.cs:88`) returns
`Differential = BuildDifferential(validations)` (`:185`). N12a is a function of `(report, fixture)` only.
N12d is a function of the N12a result and `validations`, and it is never cached. The current body of `Analyze`
(`src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:88-187`) contains `BuildDifferential` at `:185` (verified at
`2311b62`), so it cannot be the N12a unit as it stands. N12a is not a candidate, and its verdict is `uncacheable`, until U1
creates a classification unit that excludes `:185`.

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

- A stage's output is the only thing a later stage reads from it. N8 reads the decoded list that N7 returns. The current
  N7 body is count-based, so that list is count-limited (5.1). The prefix slice of 5.1 applies only if DEC-N7 is adopted.
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
  with its full name. If the build is deterministic (UNVERIFIED, U5), the MVID is a content-derived identifier of the
  compiled output. It changes whenever the IL or the metadata of Core changes. Determinism is UNVERIFIED (U5).
- **Rule.** If the build is deterministic (UNVERIFIED, U5), a change anywhere in Core changes `codeIdentity` and invalidates
  every cached stage. A non-deterministic build gives a new MVID on every build, which invalidates every entry and gives
  no reuse across builds (U5). This is
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

A stage is **uncacheable** if its static closure (9.3) contains any of the following. This is the only rule: a reached
item makes the stage uncacheable, and this document defines no exception to it. "Reached" means statically reachable
in the closure, whether or not a fixture executes it. An uncacheable
stage is recomputed on every run, and no entry is written for it in any mode (6.5). Each run reports
`ANALYSIS_CACHE_UNCACHEABLE` once per stage.

1. **Reflection.** `typeof` used for reflection, `GetType`, `Activator`, `MethodInfo`, or `System.Reflection`
   APIs. Also reflection-based serialization: the generic method `ArtifactJson.Serialize<T>` calls
   `JsonSerializer.Serialize(document, CanonicalOptions)` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:54`),
   which reflects over `T` at run time. The cached payload codec must not reflect (3.7).
   The sites below are a search result at `2311b62`, not an exhaustive list. Condition (a) of 9.3 requires this search
   to be re-run before any verdict. Serialization by `JsonSerializer` over a generic or concrete type: `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:54`,
   `src/PSXRecomp.Core/Diagnostics/DiagnosticJson.cs:32` and `:38` (`Deserialize<T>`), `src/PSXRecomp.Core/DiscImage/DiscImageAnalysisReport.cs:55`,
   `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:57`, and `src/PSXRecomp.Core/Project/ProjectMetadataSerializer.cs:65`. Attribute-based converters,
   `[JsonConverter(typeof(...))]`, on the Diagnostic types (for example `src/PSXRecomp.Core/Diagnostics/DiagnosticCategory.cs:19`), which
   the serializer resolves by reflection. Other reflection-based calls: `exception.GetType().Name` at
   `src/PSXRecomp.Core/DiscImage/RomAnalysisStageRecorder.cs:67`, in the exception overload of `Fail` (`:65`). The exception
   overload has ten call sites in Core, all in `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs`, each in
   one of these clauses, quoted exactly: `catch (FileNotFoundException ex)` (`:240` for `:242`, `:263` for `:265`), which has no filter, or `catch (Exception ex) when (IsClassifiableFailure(ex))` (`:122` for `:124`, `:223` for `:225`, `:245` for `:247`, `:268` for `:270`, `:298` for `:300`, `:357` for `:359`, `:383` for `:385`, `:435` for `:437`). The search covered every `Fail(` call in
   `src/PSXRecomp.Core` at `2311b62`:
   `:124` (N2, CHD open), `:225` (N3, filesystem), `:242` and `:247` (N4, SYSTEM.CNF), `:265` and `:270` (N4, boot
   executable), `:300` (N5, EXE header), `:359` (N7, decode), `:385` (N8, basic blocks), and `:437` (N11, report). The
   N11 catch at `:435` belongs to the `try` at `:401`, which also contains the N9 call `FunctionDiscovery.Build` (`:404`)
   and the N10 call `BiosCallRecognizer.Recognize` (`:432`). The test project adds exception-overload calls outside Core:
   `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:113`, `:153`, and `:255` (Input and Manifest stages), and
   `src/PSXRecomp.Tests/RealRomAnalysis/RomAnalysisStageRecorderTests.cs:57`. None of those stages is a cache candidate,
   so no verdict depends on them. Within Core, the other `Fail` calls use the string overload (`:52`), which does not
   call `GetType`: `:95`, `:173`, `:190`, `:196`, `:277`, `:285`, `:307`, `:314`, `:325`, `:332`, `:342`, `:369`, and
   `:391`. The only route to `:67` is a call to the exception overload. The recorder type is referenced in Core only in
   `RomAnalysisPipeline.cs`, `RomAnalysisOutcome.cs`, and its own file (grep at `2311b62`, which includes
   `src/PSXRecomp.Core/Cpu`; `R3000aDecoder.cs` references none of them). Other `Fail` methods, such as the one in
   `src/PSXRecomp.Core/Runtime/PrintfService.cs`, are unrelated. Each cache candidate's reach is decided by its
   unit in the table of 9.2, which gives one rule per stage. Under that table no unit contains a call to the exception
   overload, because each such call is in a driver `catch` block outside the unit. U2 verifies this on the closure. `Enum.IsDefined(typeof(...))` and `Enum.TryParse` at
   `src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:340`; `Enum.ToObject`, `Type.GetTypeCode`, and `Enum.GetUnderlyingType` at
   `src/PSXRecomp.Core/Runtime/Input/InputBindingMap.cs:135` and `:139`; and `typeof(NativeLibraryResolver).Assembly` at
   `src/PSXRecomp.Core/NativeLibraryResolver.cs:49`. The reach of these last sites is not yet checked.
2. **Dynamic code.** `dynamic`, expression-tree compilation, or runtime code generation. *Generic guard; the
   search is acceptance criterion 1 of 9.*
3. **Native calls.** P/Invoke, `LibraryImport`, or any call through `src/PSXRecomp.Core/NativeInterop.cs` (root of Core).
4. **Delegates from outside.** A delegate or function value not declared in the input record.
5. **Interface or virtual dispatch with implementations outside Core.** Class hierarchy analysis over Core
   gives the implementation set. If any implementation lies outside Core, the set is unknown. This covers Rule NC.
6. **Generic instantiation with a type argument outside Core.**
7. **Static state.** Static members fall into two classes, and the single rule applies to each as follows.
   - (i) **Static mutable state.** A reached read or write of a static field or property that is not `const` or
     `readonly`, or a read of a `static readonly` field whose object is written after its initialization, makes the stage
     uncacheable. A reached static initializer that sets such a field is part of the same case.
   - (ii) **Static methods and read-only values.** A call to a static method, and a read of a `const` or `static readonly`
     value that is never written after initialization, do not by themselves make the stage uncacheable. A static method
     is checked through its body: its body is part of the closure and is subject to items 1 to 13 like any other code.
   - **Test.** For each reached static member, U2 classifies it as (i) or (ii). A stage is uncacheable if any reached member
     is (i), or if any reached code, including a static method body, reaches an environment or process-state read
     (items 8 to 12). Whether a `static readonly` value is never written is decided by U2 (UNVERIFIED). Until U2 decides
     it, such a value counts as (i).
   - **Examples.** Each 9.2 root is a static method or a block, with the visibility named in 9.2. For example `FunctionDiscovery.Build` (public static)
     (`src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:81`), `BasicBlockBuilder.Build`
     (`src/PSXRecomp.Core/DiscImage/BasicBlockBuilder.cs:20`), `BiosCallRecognizer.Recognize` (public static, class `BiosCallRecognizer` at `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs:130`)
     (`src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs:140`), and `DecodeInstructions` (private static,
     `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:502`). The N11 block sits inside the private static
     `RunFromIsoSectorReader` (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:206`). Calling a static method is not a
     static-state read, whatever its visibility. `ArtifactJson.CanonicalOptions`
     (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:34`) is a `static readonly` field holding a
     `JsonSerializerOptions` object, which is mutable, so it counts as (i) until U2 shows that it is never written.
8. **Culture.** Any reached API whose result depends on `CultureInfo.CurrentCulture` or `CurrentUICulture`.
   The pipeline's formatting is not uniform: some sites use `string.Create(CultureInfo.InvariantCulture, ...)`
   (for example `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:100-101`), while others interpolate without it (`:286` and `:333`).
   Any reached culture-sensitive site makes the stage uncacheable. U2 lists each reached site with its file and line as
   evidence. This document defines no exception, so the sites at `:286` and `:333` make a stage uncacheable if it reaches them.
9. **Environment.** `Environment.*` (variables, machine name, user, OS version, current directory), and platform
   identity: `OperatingSystem.Is*`, `RuntimeInformation.FrameworkDescription`, `RuntimeInformation.ProcessArchitecture`, `RuntimeInformation.IsOSPlatform`, and
   `BitConverter.IsLittleEndian`. Core sites at `2311b62`: `src/PSXRecomp.Core/FileIdentityInterop.cs:19`; `src/PSXRecomp.Core/LinuxFileStatInterop.cs:21`
   (`IsLinux`, `IsLittleEndian`) and `:23` (`ProcessArchitecture`); `src/PSXRecomp.Core/MacOsFileStatInterop.cs:21`;
   `src/PSXRecomp.Core/NativeLibraryResolver.cs:102` and `:106` (`IsOSPlatform`, in `CurrentOs` at `:100`); `src/PSXRecomp.Core/ProcessTreeInterop.cs:18-20`.
   A direct-call check finds none of these types referenced from `src/PSXRecomp.Core/DiscImage`,
   `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs`, `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs`, or
   `src/PSXRecomp.Core/Recompiler/RecompilerContract.cs`, or `src/PSXRecomp.Core/Cpu` (grep at `2311b62`: no platform read there), so no
   stage body reaches them by a direct call. `src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs` takes platform values as parameters and
   reads none. Build-time reads (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:19`) are not runtime reads. **Transitive reach from a cached stage
   is UNVERIFIED,** and U2 checks it (criterion 1 of 9.4). *Generic guard; acceptance criterion 1 of 9.*
10. **Time.** `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `Stopwatch` used in a value, time zone
    lookups. *Generic guard; acceptance criterion 1 of 9.*
11. **File system.** `System.IO` reads, directory enumeration, file metadata, and path-based native calls. A file
    read is an input and must be in the record as a value, not read implicitly. **Core sites at `2311b62` that read
    file content or metadata.** The search covers `FileInfo`, `DirectoryInfo`, `File.`, `Directory.`, `FileStream`,
    `SafeFileHandle`, `stat`, `/proc`, `DllImport`, `LibraryImport`, and the native loader.
    - Content: `src/PSXRecomp.Core/DiscImage/ChdReader.cs:103` (`new FileStream`, in `ChdReader.Open(string)` at `:101`); `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:286`
      (`Directives`), `:341` (`ParseExeImage`), `:512` (`ReadStrictRootsFile`), `:557` (`ReadRootsFile`), `:561`
      (`ReadRoots`); `src/PSXRecomp.Core/ProcessTreeInterop.cs:101` (`File.ReadAllText` of `/proc/<pid>/stat`) and `:91`
      (`Directory.EnumerateDirectories("/proc")`, a listing).
    - Metadata: `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:62` (`new FileInfo(path).Length`, in `Read` at `:55`);
      `src/PSXRecomp.Core/FileIdentityInterop.cs:21` (`GetFileInformationByHandle` on a `SafeFileHandle`, in `TryGetIdentity` at `:15`);
      `src/PSXRecomp.Core/LinuxFileStatInterop.cs:34` (`stat(path, ...)`, in `Stat` at `:19`); `src/PSXRecomp.Core/MacOsFileStatInterop.cs:27` and `:33`
      (`stat$INODE64` and `stat`, in `Stat` at `:19`).
    - Native loader, two separate events in `src/PSXRecomp.Core/NativeLibraryResolver.cs`. (a) Module load: the `[ModuleInitializer]`
      (`:46-50`) runs when the module loads, and it only registers the resolver (`SetDllImportResolver` at `:49`).
      (b) First binding: the runtime calls `Resolve` (`:52`) when it first binds a P/Invoke into the assembly, and
      `Resolve` tries the `PSXRecomp.Native` library file in `AppContext.BaseDirectory` (`:60`). The runtime then caches
      the bound handle (runtime behavior, not tested here). The native calls are also item 3.
    - Not file access, listed under item 9: `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:67` (`Path.GetFullPath`, which reads the current
      directory for a relative path).
    - Not Core file access: the `fopen`, `/proc`, and `readlink` text in `src/PSXRecomp.Core/Recompiler/RecompiledArtifactCodeGen.cs` and
      `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs` is emitted C inside raw string literals. `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:101` and `:404` read the
      disc through an injected `ICdSectorSource`, not by opening a path.

    Direct-call check at this SHA: `ChdReader.Open(string)` has no Core caller. The `LoadImageManifest` readers are
    called only from `src/PSXRecomp.Cli`, `src/PSXRecomp.Infrastructure`, and tests. `TryReadParentIds` is called only
    from `src/PSXRecomp.Infrastructure` and tests. The identity and stat sites are called only from
    `src/PSXRecomp.Infrastructure/Standalone/FileSystemIdentityProbe.cs` (`:67`, `:68`, `:142`) and tests. The pipeline
    uses the stream overload (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:117`), which opens no file. **Transitive reachability from a cached
    stage is UNVERIFIED.** A listed site that a stage reaches makes it uncacheable (9.3, condition (i)). No stage may be
    cached on the claim that its closure reads no file until U2 checks it.
12. **Randomness.** `Random`, `RandomNumberGenerator`, `Guid.NewGuid`, and hash codes of reference types.
    *Generic guard; acceptance criterion 1 of 9.*
13. **Unresolved symbols or an assembly outside the allow-list.** The allow-list is the BCL and `PSXRecomp.Core`.
    The architecture attribute source is compiled into Core (3.8). Any other package is outside the list, and a
    closure that reaches one is uncacheable. The packages named in 3.8 are outside the list: `SharpCompress`
    0.50.4 (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:25`, used by `src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs:3`)
    and `loach.PureSharp` 0.1.6 (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:24`, `PrivateAssets="all"`). Whether a stage reaches
    `loach.PureSharp` is UNVERIFIED (acceptance criterion 3 of 9).

Items 8 to 12 are the implicit inputs of the BCL. The design does not assume the BCL is fixed.

### 3.5 Input records (values, not digests)

Each cached stage is a function of exactly one argument: its input record. The record holds the **values**
the stage reads, and nothing else is read. An upstream output is available as a value, either because the
upstream stage ran in this run or because its cached payload was decoded by the codec (3.7).

**N7 (current count-based body).** `DecodeInput { byte[] TextSegment; uint TextStart; uint TextEnd; uint EntryPoint; int Count; }`

- The current body takes `count` (`RomAnalysisPipeline.cs:502`, loop bound at `:511`), so the record holds `Count`. The
  count-free sweep and its record without `Count` are the proposal DEC-N7 (5.1), not adopted.
- `TextStart` is a field because `PsxExe.GetInstructionWord` reads `Header.TextStart` (`PsxExe.cs:23`). The
  stage body does not call `PsxExe` methods; it reads the record through a pure helper with the same arithmetic.
- `TextSegment` is the whole text segment. The sweep reads a subrange, so the record is a superset.

**N8.** `BlockInput { uint DecodeStart; IReadOnlyList<DecodedInstruction> SlicedDecodedList; }`

- `DecodeStart` is `EntryPoint` (`BasicBlockBuilder.cs:34`).
- `SlicedDecodedList` is the N7 output, held as values. Under the current count-based body it is count-limited; the
  prefix slice of 5.1 applies only if DEC-N7 is adopted. `BasicBlockBuilder`
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
- Decoded data: `SlicedDecodedList` (`:425`), the decode failures (`:426`), `BasicBlocks` (`:427`), and `CfgEdges` (`:428`).
- Not record fields: `Functions` (`:404`, from `FunctionDiscovery.Build`) and the BIOS evidence (`:432`, from
  `BiosCallRecognizer.Recognize`). The unit computes both itself, so N11 recomputes the N9 and N10 values and does not
  read their caches. The current pipeline does not consume N9 or N10 caches at all, because N11 calls them. A pipeline that
  passes their outputs into N11 would add them to this record, and that change is a proposal.

The identity and header fields are in the N11 record because the report embeds them. An entry keyed on the
decoded data alone would be reused for a different disc with the same boot executable.

**N12a.** `CoverageInput { DiscImageAnalysisReport Report; ArtifactFixtureIdentity Fixture; }` (the target record of U1. N12a is not a
candidate until U1 creates its unit, and the current `Analyze` body is not this unit, see 2.2)

- `Report` is the N11 output as a value. `Fixture` is the identity value of 2.3, including `FixtureId`.
  **Nothing else.** `validations` is not in this record.

**Static check.** For each stage:

- The stage signature has exactly one parameter, the record (for a method unit; a block unit such as N11 has the record described in 3.5 and 9.2).
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
    once per run, before any stage runs. The reads are done by the production key reader, an Infrastructure adapter
    created by the CLI or RuntimeHost composition code (8.1, `FileStageKeyInputReader`, a proposed name). It implements
    `IStageCodeIdentity` by returning values. **Direct-call verified at `2311b62`, transitive reach UNVERIFIED (U2 checks it, 9.4 criterion 1):** the stage closure
    performs no file access except the sites listed in 3.4 item 11, and none of those sites reads these inputs. The
    value enters the key as `dependencyIdentity`. The csproj pins direct packages only, and there is no lock file (CONFIRMED by listing).
- **SDK version and runtime version.**
  - **PROPOSED, UNVERIFIED (U5).** No build target in this repository reads `$(NETCoreSdkVersion)` or writes a generated
    source file. At `2311b62`, `git grep` finds the name in no `.props`, `.targets`, or `.csproj` file. The only custom
    targets are `BuildNative` (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:57`) and `IncludeNativeLibraryForTests`
    (`src/PSXRecomp.Tests/PSXRecomp.Tests.csproj:70`), which handle native artifacts. The mechanism to be built and
    verified under U5: a target reads `$(NETCoreSdkVersion)` and writes it into a generated source file in `obj/`,
    compiled into Core, so the value would be inside the MVID of 3.3. The host would not read the generated file.
  - **Key status.** The SDK-version field of `dependencyIdentity` depends on this mechanism. Until U5 confirms it, that
    field has no verified value and no verified source in the repository, so the key is **not yet established** for any
    stage.
  - At run time, the runtime version is read from `RuntimeInformation.FrameworkDescription` by the host, in the key
    computation before any stage runs. No Core source reads it: at `2311b62` `git grep` finds it only in
    `src/PSXRecomp.Cli`, `src/PSXRecomp.Infrastructure`, and `src/PSXRecompStudio`. A stage closure is Core-only (9.2),
    so the read is outside every closure and is not a reached item of 3.4. No exception is needed. If a Core stage ever
    reached it, item 9 would make that stage uncacheable, because item 9 names `RuntimeInformation.FrameworkDescription`
    (3.4 item 9).
- **Target framework.** `TargetFramework` is `net10.0` (`PSXRecomp.Core.csproj:3`).
- **Architecture attribute source.** `src/Architecture/PSXRecompArchitectureAttributes.cs` is a **source file**, not a
  separate assembly. `Directory.Build.props:22-24` links it into every project whose `CompileArchitectureAttributes` is
  `true`, and `PSXRecomp.Core.csproj:7` sets it. Its `internal` attribute types are compiled into Core, so the MVID of
  3.3 covers them.
- **Failure rule.** The host reads four values before any stage runs: the resolved package versions
  (`obj/project.assets.json`), the build-time analyzer identity (`Directory.Build.props`), the runtime version
  (`RuntimeInformation`), and the MVID of 3.3 (read from the running Core assembly). If any of them cannot be read, the
  stage is uncacheable, and the reported code follows the precedence rule of 9.3 (6.4, F5): an unreadable MVID gives
  `ANALYSIS_CACHE_KEY_UNAVAILABLE` first; otherwise a missing, unreadable, or mismatched gate file gives
  `ANALYSIS_CACHE_UNCACHEABLE`; otherwise an unreadable package file, `Directory.Build.props`, or runtime version gives
  `ANALYSIS_CACHE_KEY_UNAVAILABLE`. The SDK version and the target framework
  are not host-read. The SDK version reaches the key only through the MVID, and only if U5 confirms that mechanism (not yet
  established). The target framework reaches it only through compiled metadata (UNVERIFIED, U5). No constant is
  substituted. **UNVERIFIED ASSUMPTION** (no installer layout is checked in this repository): an installed CLI or GUI
  has no source tree, so `obj/project.assets.json` and `Directory.Build.props` are absent there. The citations are
  `.gitignore:5` (`obj/` is ignored, so it is build output and not committed) and `git ls-files`, which lists
  `Directory.Build.props` at the repository root. If the assumption holds, that build also has no gate file. Every run in it
  is therefore uncacheable and reports `ANALYSIS_CACHE_UNCACHEABLE` by the precedence rule of 9.3 (the MVID is readable, and the
  missing gate file is checked before any dependency input), so the missing dependency input is not reported. `ANALYSIS_CACHE_KEY_UNAVAILABLE` would appear only in a build that
  ships a matching gate file but lacks a key input.

### 3.9 Decision: line endings

**DEC-LF1.** Digests of text use LF-normalized content: CRLF becomes LF, a lone CR is kept, and a BOM is kept.
A checkout with different line-ending settings must not change a digest for the same source. The git warning
"LF will be replaced by CRLF" for the design file was observed during commit `6d038a0` (OBSERVED). The MVID does
not depend on the checkout's line endings, because it is computed from the compiled output. This holds only if the
build is deterministic, which is UNVERIFIED until U5 confirms it (3.3).

## 4. Invalidation

### 4.1 Rules

- **V1. Changed input value.** A changed record value changes the record digest, so the key changes, and the
  entry is not reused.
- **V2. Changed code.** A changed `codeIdentity` changes every key.
- **V3. Changed dependency.** A changed package or runtime version changes every key. A changed SDK version changes
  every key only if U5 confirms that the SDK version reaches the key (3.8). Until U5 is resolved, that part of V3 is
  conditional, and the key is not yet established for the SDK-version field.
- **V4. Untracked value.** A value read but not in the record is a defect (3.5 static check). Until fixed, the
  stage is uncacheable.
- **V5. Unknown dependency.** An implicit input, or a call that breaks Rule NC, makes the stage uncacheable.
- **V6. Field removal.** A record field is removed only on **static evidence** from U2: the reachability analysis
  shows that no reached code reads it. A field with zero reads in a corpus run is **not** evidence that the field
  is unused. A zero-read field is a flag for review. The removal is confirmed by T-DEP-DEAD (7.3), on a fixture set
  separate from the one used by T-READ.

### 4.2 Stage consequences

- **N7.** Any Core change changes `codeIdentity` (DEC-CODE). A change to the text segment changes `TextSegment`.
  `count` is a field of the current N7 record (3.5), so it is a key component. The count-free design and its slice are the
  proposal DEC-N7 (5.1), not adopted.
- **N8.** Changes to the N7 output (count-limited by the current body; the slice of 5.1 applies only if DEC-N7 is adopted),
  to `DecodeStart`, or to Core.
- **N9 to N11.** Changes to the upstream values, the record fields, or Core.
- **N10.** `BiosJumpTables` and `BiosCallFamily` are Core symbols, so they are inside `codeIdentity`.
- **N11.** The identity and header fields of 3.5. A different disc hash or boot path changes the N11 key even when
  the executable is identical.
- **N12a.** Changes to the report, the fixture identity, or Core. It does not change with `validations`. This applies to the
  unit U1 creates, which must exclude `BuildDifferential`. N12a is not a candidate until then, and its verdict is uncacheable.
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
  (b) V-ID is implemented, a global condition that is the same for every stage; (c) T-IDENT passes (7.3). The rule
  applies to writes, Verify-mode comparison, and Normal-mode hits alike, because an entry keyed on an unverified
  identity would be unsound. Until Rule C holds, the stage is uncacheable in every mode (6.5). A matching gate file is one
  input only: V-ID is not implemented, so Rule C cannot hold, and every stage is uncacheable regardless of the gate file. This
  is the current proposal. Other sections restate it or refer to it.
- **Migration.** `src/PSXRecomp.Tests/DiscImage/DiscImageHostileSizeTests.cs:24` defines `Sha` as a literal that is
  not the hash of the bytes passed at `:347`. About 50 call sites exist in the test projects (9 in that file, 2 in
  `SyntheticChdReaderTests.cs`, 2 in `RealRomAnalyzer.cs`, 36 in `RomAnalysisPipelineTests.cs`, 1 in
  `RealRomProductionExecutionTests.cs`). How many pass a constant is UNVERIFIED (U7).

## 5. Partial re-analysis: the decode prefix

### 5.1 Where the prefix is sliced

- **CONFIRMED** Today the count is applied inside the decode loop: `DecodeInstructions(exe, count)`
  (`RomAnalysisPipeline.cs:501-502`, call at `:355`), with the loop bound `decoded < count` (`:511`). BasicBlockBuilder
  receives that count-limited list (`:381`).
- **Proposal DEC-N7 (not adopted).** A count-free N7 would compute the full sweep from `EntryPoint` to `TextEnd`, with no
  count. In that proposal the count-limited list that N8 sees is produced by a slice **before N8**: the first `min(count, total)` items of N7's output. The
  slice's output is N8's record value (3.5). The slice reproduces today's list exactly (T-PREFIX).
- **Cost.** A cold N7 decodes the whole text segment rather than the first `count` words (UNVERIFIED, U4).
- **Failure rule.** A decode failure at address `A` is included in a count-limited result iff `A < entry + 4*M`,
  where `M` is the requested count.
- **Adoption.** The current N7 unit and record are the count-based body (`RomAnalysisPipeline.cs:501-543`, `count` at `:502`
  and `:511`). DEC-N7 is adopted only if the owner decides it, or if U4 measures the full-sweep cost and the owner accepts
  it. Until then nothing in this document assumes the count-free sweep.

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
  as stage results (`src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:261-262`), and `src/PSXRecomp.Core/DiscImage/RomAnalysisStage.cs:10` says the pipeline does not produce them.

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
- **R3.** `header.key` equals the key computed from the current record, code identity, and dependency identity (3.2).
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
- **F3. Uncacheable.** The stage is recomputed on every run, and the run reports `ANALYSIS_CACHE_UNCACHEABLE` once per
  stage. This is the code for every state in which no cacheable verdict exists: the default state before U2 runs (no gate
  file exists), a gate file that is missing, unreadable, or mismatched, and a stage whose verdict is uncacheable. Precedence
  (9.3): this check comes second, after an unreadable MVID (which gives KEY_UNAVAILABLE), so a state without a matching gate file reports this code even when a dependency input is also
  unreadable.
- **F4. Partial failure.** If a stage fails, no entry is written for it or for any stage below it (W5).
- **F5. Key input unavailable.** If any part of the key cannot be read (the package file, `Directory.Build.props`, the
  runtime version, or the MVID of 3.3), the stage is uncacheable. The run reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` when the MVID
  cannot be read (precedence step 1 of 9.3, because no gate file can be matched), and also when a gate file matches the running
  MVID and a dependency input cannot be read (step 3). A missing, unreadable, or mismatched gate file is F3 (step 2). The code means that a key
  input is unreadable. It does not cover the gate file, which is reported under F3. The
  generated SDK file of 3.8 is PROPOSED (U5) and is not a host-read input. No constant is substituted.
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
- **Entry count.** Until Rule C holds for a stage, the number of entries written for every cached stage is 0. This is the check of T-COLD-WARM.

## 7. Modes, seams, and tests

### 7.1 Modes, seams, and test-only injection

- **Default mode is Verify.** In Verify mode, a cacheable stage is recomputed cold. If an entry exists, its payload
  is compared with the cold payload byte for byte. A difference is `ANALYSIS_CACHE_VERIFY_MISMATCH` and a hard failure.
  The cold result is returned, never the entry.
- **Normal mode** returns a valid entry as a hit. It is used for a stage only when Rule C (4.3) holds for that stage, the same condition as for Verify mode:
  the U2 verdict is cacheable for the running MVID (9.3), V-ID is implemented, and T-IDENT passes. V-ID is not yet
  implemented, so no stage meets this condition today.
- The default is set in `AnalysisCacheOptions` and is not optional.
- **Production cannot reach the test seams.** A test that needs a cacheable verdict, or a fake code identity, injects it
  through the Core port `IStageCodeIdentity`. The fake implementation lives in the test project. Production code may not
  reference test code (`docs/architecture-matrix.md:43`), so a production composition root cannot bind a test
  implementation. The production composition root binds the production implementation only. A verdict that comes from the
  test project is never a Rule C condition and never marks a production stage cacheable.

Injection seams (Core types, public ports):

- `IAnalysisCacheStore`: the store port (8.1). Tests pass an in-memory fake, which lives in the test project.
- `IStageCodeIdentity`: returns, for each stage, the Rule C inputs as values: `codeIdentity`, `dependencyIdentity`, the U2 verdict for the stage (`cacheable` or `uncacheable`), the V-ID state, and the T-IDENT state. The port is the single
  carrier of these inputs. A stage is cacheable only when all three conditions of Rule C hold (4.3). The port does not
  decide that by itself.
  Production binds the gate-file-backed implementation (9.3). Tests bind a fake.
- `IStageInputRecorder`: wraps an input record and counts reads per field (3.6).
- `AnalysisCacheOptions`: `Mode` (default `Verify`), the store, the code identity provider (the `IStageCodeIdentity` that carries the Rule C inputs), the recorder, and a
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
Such a run proves the mechanism only. It does not count toward Rule C or any gate-file check, and it does not make a production stage
cacheable. A test marked "needs seam" in the table cannot run at all until the named seam, code, or codec exists in the
tree; none of those exists at `2311b62`.

| ID | Test | Asserts | Scope and precondition |
|---|---|---|---|
| T-COLD-WARM | Cold and warm equality, and entry count | For each set-A fixture: run cold, store, run warm. **While a stage is uncacheable (the default), the test asserts that the number of entries written for it is 0 and the number of hits is 0**, and that the warm output equals the cold output byte for byte. Once Rule C holds for a stage, the test also asserts hits equal the number of cacheable units, and the warm payload equals the cold payload per stage. | Needs seams `IAnalysisCacheStore` and `AnalysisCacheOptions` (not yet present at `2311b62`). Hit assertions run after Rule C holds |
| T-DEP-INPUT | Changed record value is not reused | For each stage and each record field, on set-B fixtures, change that one value. The key differs, the entry is not reused, and the recomputed payload is stored under the new key. | **After Rule C holds for the stage.** Before that, not run in production |
| T-DEP-CODE | Changed code identity is not reused | Substitute a changed `codeIdentity` in `IStageCodeIdentity` (test fake). The entry is not reused. | Test-only verdict; proves the mechanism, not any production cacheability |
| T-DEP-DEAD (MAJOR-2 of r8, LOW-3 of r8) | Field removal is checked by value and by static verdict | For each field whose set-A reads are zero, and only for those: (a) the U2 static verdict must say "unread"; (b) on set-B fixtures, the baseline run with the **unchanged** record has key **K0** and stores payload **P0** under K0. The changed record's key is **K1**, and its cold result is **C1**. The field is accepted only if **C1 equals P0** byte for byte. A field that fails (a) or (b) stays in the record. | Step (b) reads a stored entry, so it runs **after Rule C holds for the stage**. (a) runs with U2 |
| T-IDENT2 (C1 of r7) | Same executable, different disc identity | Two fixtures with the same boot executable bytes and different disc hashes (and, in a second case, a different boot path). The N11 keys differ, the second run does not reuse the first run's N11 entry, and the N11 payload contains its own disc hash and boot path. N12a is added to this test when U1 creates a candidate unit (3.5, 9.2). | **After Rule C holds for N11** (it observes reuse). The key difference alone can be checked before |
| T-DIFF (MAJOR-1 of r7) | N12a is independent of validations | For a fixed `(report, fixture)`, vary `validations` (none, one run, two runs, different order). The N12a payload is byte-identical in every case. The document from N14 differs only in the `Differential` section. | Runs after U1 (N12a exists as a function) |
| T-UNCACHE | Uncacheable stage writes nothing | For the default status (no gate file, F3), and with an uncacheable U2 verdict injected through `IStageCodeIdentity` (7.1), each stage recomputes every run, writes no entry in Verify or Normal mode, and reports `ANALYSIS_CACHE_UNCACHEABLE` once. | Needs seams `AnalysisCacheOptions` and `IStageCodeIdentity`, and the code `ANALYSIS_CACHE_UNCACHEABLE` (none present at `2311b62`) |
| T-KEY-UNAVAIL (M8 of r8, MINOR-7 of r7) | Unreadable key input | Three cases, with a fake `IStageCodeIdentity`. (a) The MVID unreadable: reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` (precedence step 1 of 9.3). (b) A matching gate file in the fake, and the package file, `Directory.Build.props`, or the runtime version unreadable, one at a time: reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` (step 3). (c) The same unreadable input with no matching gate file: reports `ANALYSIS_CACHE_UNCACHEABLE` (step 2), which shows the order. In every case the stage is uncacheable, and no constant key is produced. | Needs seam `IStageCodeIdentity` (a fake in the test project) and the code `ANALYSIS_CACHE_KEY_UNAVAILABLE` (none present at `2311b62`) |
| T-READ | Record matches reads | Run each stage under the recorder on set-A fixtures. Every record field's read count is recorded. A zero-read field is listed as a flag for review (V6). It is not removed by this test. For N11, the record fields are those of 3.5; the N9 and N10 values that the unit computes at `:404` and `:432` are not record fields. | Runs after U1 (records exist) |
| T-STATIC (M2 of r8, MAJOR-1 of r7) | Static check of the record and the split | Each stage signature has one parameter. Its reached body reads nothing outside the record. The N12a closure does not reach `BuildDifferential`, `validations`, or `Differential`. | Static, U2 (9) |
| T-CLOSURE-RT | Executed paths against the analysed closure | Run each candidate stage under an instrumented build that records executed methods and the environment, time, file, culture, and random APIs. Every executed Core method must be in the analysed closure, and no implicit-input API may execute. | Exercised paths only, current revision; instrumentation UNVERIFIED (9, criterion 5) |
| T-CORRUPT | Corrupt entries fail safe | For each corruption (zero-length file, truncated payload, flipped byte, wrong key in header, unsupported `keyFormat`, malformed header, schema mismatch), the run recomputes, the output equals the cold output byte for byte, and `ANALYSIS_CACHE_QUARANTINED` is reported once per corrupt entry. | Needs an entry; runs after Rule C holds for a stage. A test-only verdict may exercise it earlier |
| T-QUAR-FAIL (m6 of r8) | Failed quarantine is terminal | Make the rename fail for a corrupt entry. The run recomputes, reports `quarantineFailed = true`, writes nothing to that key, and writes successfully for a different key in the same run. | Needs an entry; same as T-CORRUPT |
| T-W6 (m5 of r8) | Existing target | A write whose target exists with a matching `payloadSha256` but a different key or a missing `COMPLETE` is treated as corrupt, not accepted. | Needs an entry; same as T-CORRUPT |
| T-MISSING | Missing entries are silent | Delete an entry. The run recomputes, the output equals the cold output, and no diagnostic is reported. | Needs an entry; same as T-CORRUPT |
| T-PARTIAL | Partial failure writes nothing below | A failure injected at N8 writes no entry for N8 or later. The N7 entry is the only one written. | Needs an entry; after Rule C holds for N7 |
| T-STALE | Stale-but-consistent entry (residual risk) | An entry whose header and `COMPLETE` marker match the current key, with a payload that differs from the cold result. In **Verify** mode (default) the run reports `ANALYSIS_CACHE_VERIFY_MISMATCH` and returns the cold result. In **Normal** mode the entry is returned. | **Normal mode is available only after Rule C (4.3) holds.** Verify-mode part may run with a test-only verdict |
| T-SHORT (5.2) | Short text segment | An executable whose text segment is shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment, and no exception escapes. | Runs now |
| T-PREFIX (5.1) | Prefix slice | N7 output sliced to `M` before N8 equals today's count-limited decode at `M`, including failures filtered by `A < entry + 4*M`. | Runs after R-0 (U1), and only if DEC-N7 is adopted |
| T-DIFF-RUN | Differential on and off, end to end | Cold and warm runs with validations supplied and without, both byte for byte. N12d is never read from the cache. | Cold half runs now. Warm half runs after Rule C holds |
| T-IDENT (4.3) | Identity verification | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind and no cache write. The CLI and the test driver each hash the bytes they run (`src/PSXRecomp.Cli/CliInput.cs:106-109`, `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:102-105`), and the test checks that they agree on the same bytes. The GUI does not hash: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:110-117` passes `discImageSha256` through to `RunFromChd`, and where that value originates is UNVERIFIED (U6, the caller is unread). **Part of Rule C (4.3): this check gates writes, Verify mode, and Normal-mode hits alike.** | Runs after V-ID is implemented (not yet) |
| T-CODEC | Key encoder and payload codec | The key text of a fixed sample equals the committed text, and its SHA-256 equals the committed value. Member order is UTF-8 byte order. Floats are rejected. The codec round-trips each record and stage type, and is injective on the fixtures. | Needs the key encoder and payload codec of 3.7 (not yet present at `2311b62`) |
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
- Infrastructure, key reader (the layer for the reads of 3.8): `FileStageKeyInputReader` (a proposed name), marked
  `[Infrastructure]`. It reads `obj/project.assets.json`, `Directory.Build.props`, the gate file of 9.3, the runtime
  version, and the running MVID (the read of 9.3), and returns values for `IStageCodeIdentity`. The CLI or RuntimeHost composition code creates it (see the layer note below). Core defines only the
  port `IStageCodeIdentity`. **Direct-call verified at `2311b62`, transitive reach UNVERIFIED (U2 checks it, 9.4 criterion 1):** the stage closure
  performs no file access except the sites listed in 3.4 item 11, and none of those sites reads these inputs.
- **Layer note (AARC002).** The creator is in an Infrastructure-layer project: `src/PSXRecomp.Cli` (namespace
  `PSXRecomp.Infrastructure.Cli`) or `src/PSXRecomp.RuntimeHost` (namespace `PSXRecomp.Infrastructure.RuntimeHost`).
  `src/architecture.contract.json` classifies those namespaces as Infrastructure, so the reference needs no `AARC002`
  suppression. `src/PSXRecompStudio` (namespace root `PSXRecompStudio`, Application layer) references no Infrastructure
  project. If it were to create the reader, it would need the one composition-root `AARC002` suppression that
  `docs/architecture-matrix.md:47` allows, with a written rationale, and wiring beyond that one site needs an architecture
  change (`:47`, `:210`). This design does not grant that suppression, so the GUI runs without the cache until the owner
  decides (open item).
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

## 9. U2: the reachability analysis, as a standalone task

U2 is one of the three conditions of Rule C (4.3). V-ID and T-IDENT are the other two. This section specifies it as a task. It has **not been run**. It is
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
  - N7: the decode sweep, the body of `DecodeInstructions` (private static, `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:501-543`,
    which takes `count` at `:502` and bounds the loop by `decoded < count` at `:511`). The count-free sweep is the proposal
    DEC-N7 (5.1). It does not exist, and U2 analyses it only if DEC-N7 is adopted, which needs the owner's decision or a
    U4 measurement that supports it.
  - N8: `BasicBlockBuilder.Build` (public static, `src/PSXRecomp.Core/DiscImage/BasicBlockBuilder.cs:19-20`).
  - N9: `FunctionDiscovery.Build` (public static, `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:81-88`).
  - N10: `BiosCallRecognizer.Recognize` (public static, in class `BiosCallRecognizer` at
    `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs:130`, method at `:140`; called from
    `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:432`).
  - N11: the report assembly, a block at `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:400-444` inside the private
    static `RunFromIsoSectorReader` (`:206`). It is a block, not a method, so U2 takes it as a region of that method.
  - N12a: the body of the public static `Analyze` (`src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:88-187`),
    which calls the private static `Classify` (`:196`) at `:107` and sets `Differential` from `BuildDifferential` at `:185`.
    Because `:185` is inside this method, the body is **not** a valid N12a unit. N12a is not a candidate until U1 creates a
    classification unit that excludes `:185`.
    No whole-analysis classification root exists at `2311b62`. The classification in `Analyze` is a loop over the decoded instructions (`:104-107`), and `Classify` (`:196`) decides one instruction, so it is not the N12a root. A whole-analysis classification function is a **proposal**, and U1 must create it before U2 can
    take it as a root.
- The static reference graph of Core, from the semantic model.

**Unit per stage (one rule per stage).** A method-level unit is the named method and everything it calls. A block-level
unit is the statements of the named `try` block, excluding its `catch` clauses, which belong to the pipeline driver and
are not cached. Locations are at `2311b62`. For the block unit N11, the record is the set of values the block reads from its enclosing method
(3.5), and the one-parameter rule of the static check (3.5) applies to method units.

| Stage | Cache candidate | Unit and rule | Location | Calls the exception overload of `Fail` inside the unit? |
|---|---|---|---|---|
| N1 input identity | no | driver checks (not a cache unit) | `RomAnalysisPipeline.cs:92-98`, `:182-198` | no (string overload only) |
| N2 CHD open | no | driver `try` (not a cache unit) | `:115-124` | no in the unit (`:124` is in the driver catch) |
| N3 ISO filesystem | no | driver `try` (not a cache unit) | `:215-225` | no in the unit (`:225` is in the driver catch) |
| N4 boot executable | no | driver `try` blocks (not a cache units) | `:232-247`, `:255-270` | no in the unit (`:242`, `:247`, `:265`, `:270` are in catches) |
| N5 EXE header | no | driver block (not a cache unit) | `:277-337` | no in the unit (`:300` is in a catch) |
| N6 text bounds | no | driver block (not a cache unit) | `:340-347` | no |
| N7 decode | yes | method `DecodeInstructions`, current count-based body | `RomAnalysisPipeline.cs:501-543` | no (the call at `:355` and the catch at `:357-359` are in the driver) |
| N8 basic blocks | yes | method `BasicBlockBuilder.Build` | `src/PSXRecomp.Core/DiscImage/BasicBlockBuilder.cs:19-20` | no |
| N9 functions | yes | method `FunctionDiscovery.Build` | `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:81-88` | no |
| N10 BIOS calls | yes | method `BiosCallRecognizer.Recognize` | `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs:140` | no |
| N11 report | yes | block: statements of the `try` at `RomAnalysisPipeline.cs:401`, excluding the catch at `:435` | `:403-433` | no (`:437` is in the catch) |
| N12a classification | no, uncacheable until U1 | method `Analyze` as it stands contains `BuildDifferential` at `:185`, so it is not a unit; none exists yet | `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:88-187` (`:185` inside) | no |
| N12d, N14, N15 | no | not cached | | |

### 9.3 Output, storage, and the gate file

**Output.** For each root:

- `cacheable` or `uncacheable`, with evidence for each uncacheable verdict (the reached symbol and its rule in 3.4).
- For each record field of each stage: a static verdict, `read` or `unread`, with the reading site.
- The implicit-input scan of 3.4: each reached site that reads culture, environment, time, file system, randomness, or
  reflection, with its file and line.
- The runtime report of T-CLOSURE-RT: the executed symbols, and any that the static analysis did not reach.

**Storage.** The output is one JSON file per Core MVID, written by the U2 tool into
`src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGateFile/<mvid>.json` (proposal). Its fields: `mvid`, `commit`,
`toolVersion`, the per-stage verdicts, the field verdicts, the scan summary, the runtime summary, and the result of each
acceptance criterion.

**Verdict rule.** A stage's verdict is `cacheable` only when every condition below holds on the static closure of the
stage (criterion 4 of 9.4). A reached item of 3.4 items 1 to 13 makes the stage uncacheable; there is no exception in
this document. U2 is a function of the Core source at the commit under review: two runs on the same MVID must reach the
same verdict, so its output must not depend on fixtures, run order, or the host (determinism of the U2 tool is not yet
checked). Executed paths establish no condition. Criterion 5 (T-CLOSURE-RT) is a required condition that must pass. It is not
an establishment of a condition, because it runs only the paths a fixture executes, and an item reached on an unexecuted
path still counts. If any condition cannot be established, the
verdict is `uncacheable`. Each uncacheable verdict names the reached symbol and the condition (9.3 output).

- (a) 3.4 item 1, reflection, including `ArtifactJson.Serialize<T>` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:54`):
  none reached. Established by the criterion 1 search for reflection and reflection-based serialization.
- (b) 3.4 item 2, dynamic code: none reached. Established by the criterion 1 search.
- (c) 3.4 item 3, native calls (every `DllImport` and `LibraryImport` in the closure, including `src/PSXRecomp.Core/NativeInterop.cs`
  and the loader in `src/PSXRecomp.Core/NativeLibraryResolver.cs`): none reached. Established by the criterion 1 search
  for native declarations and calls.
- (d) 3.4 item 4, delegates or function values not declared in the record: none reached. Established by the criterion 1
  search of delegate and lambda creation sites, each matched to a record field.
- (e) 3.4 item 5, interface or virtual dispatch with an implementation outside Core: none. Established by criterion 3,
  class hierarchy analysis over Core, with every implementation inside Core.
- (f) 3.4 item 6, generic instantiation with a type argument outside Core: none. Established by the criterion 1 search of
  the constructed generic types and methods in the closure.
- (g) 3.4 item 7, static state: no reached member is (i) (static mutable state), and no reached code reads environment or
  process state. Established by the criterion 1 search of static members in the closure, each classified (i) or (ii) as
  item 7 defines. Static method calls alone do not establish a failure of (g).
- (h) 3.4 items 8 to 10 and 12, culture, environment, time, and randomness: none reached. Culture is established by
  criterion 2, and environment, time, and randomness by criterion 1. Any reached site makes the stage uncacheable, and this
  document defines no exception (3.4 lead). Environment includes the current-directory read at
  `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:67`.
- (i) 3.4 item 11, file system: no site listed in item 11 is reached. Established by the criterion 1 search over the
  listed sites, with reachability from the closure.
- (j) 3.4 item 13, unresolved symbols or assemblies outside the allow-list: none reached, including `SharpCompress` and
  `loach.PureSharp`. Established by criterion 3.
- (k) Criteria 1 to 3 are complete for the stage, so that items 8 to 10, 12, and 13 are decided, and the output is
  recorded with the running MVID (criterion 6).
- (l) Criteria 4 and 5 of 9.4 pass for the stage. Criterion 5 is a required condition: it does not establish any of (a) to (k).

**Binding to the MVID.** The gate file is read by the **host** (composition root or CLI). Core's file reads are listed in
3.4 item 11, and the gate-file read is not one of them. The host compares the file's `mvid` with the MVID of the running Core assembly. Core does not expose it, and no
MVID read exists in `src/` at `2311b62` (`git grep` for `ModuleVersionId`, `MetadataVersion`, and `MVID` finds none outside
tests). **UNVERIFIED:** the read is not yet written. The host would take it from the loaded assembly's module (for example
`ManifestModule.ModuleVersionId`), which is a reflection call that item 1 forbids inside a stage closure. The call sits in
the host's composition root (8.1), outside every stage closure, so item 1 does not apply to it. U5 confirms the MVID. If they are equal, the host passes the five Rule C inputs (`codeIdentity`, `dependencyIdentity`, the U2 verdict from the
gate file, the V-ID state, and the T-IDENT state) into
`IStageCodeIdentity` as a value (the single carrier of the Rule C inputs, 7.1; `AnalysisCacheOptions` holds that port). The gate file's
verdict is one input to Rule C (4.3), not the whole rule. A stage is cacheable only when Rule C holds in full: the U2
verdict, V-ID, and T-IDENT. V-ID is not implemented, so until it exists every stage is uncacheable, whether or not a gate file matches. Otherwise the stage is uncacheable. A missing, unreadable, or mismatched gate file is
uncacheable and reports `ANALYSIS_CACHE_UNCACHEABLE` (F3). In the default state no gate file exists, so the same code applies.
**Precedence (one code per state).** The checks run in this order, and the first failure decides the code.
1. The running MVID cannot be read. Report `ANALYSIS_CACHE_KEY_UNAVAILABLE`, because no gate file can be matched without it.
2. The gate file is missing, unreadable, or its `mvid` differs from the running MVID. Report `ANALYSIS_CACHE_UNCACHEABLE`
   (F3), even when a dependency input is also unreadable.
3. The gate file matches, and a dependency input (the package file, `Directory.Build.props`, or the runtime version) cannot be
   read. Report `ANALYSIS_CACHE_KEY_UNAVAILABLE`.
When none of these fails, the gate file's verdict is one input to Rule C. Rule C also needs V-ID and T-IDENT, so until V-ID
exists the stage stays uncacheable (4.3).

**Invalidation.** Any change to Core changes the MVID, so every earlier gate file no longer matches, and every stage is
uncacheable again. Re-enabling a stage requires Rule C (4.3) on the new MVID: (a) a U2 verdict for the stage from a run on the new MVID that is cacheable, with the conditions of the verdict rule (9.3); (b) V-ID implemented, a global condition as in Rule C (b); (c) T-IDENT passing. Re-running U2 on the new MVID produces (a). The gate file for the new MVID is then written as the store record of (a). It is a record, not a Rule C condition. The 9.4 criteria are part of (a). The earlier gate file is not deleted automatically. Its mismatch is the signal. Re-enabling is impossible while V-ID is
unimplemented, so no stage can be re-enabled today.

**Gate-file check on CI (non-blocking).** CI reports whether the committed gate file matches the build MVID. A mismatch is a warning and
keeps stages uncacheable. It is not a build failure, because an uncacheable stage is safe.

### 9.4 Acceptance criteria

1. **Searches over the closure (items 1, 2 to 4, 6, 7, 9 to 12 of 3.4).** For each stage, search the reached code for the
   forms each item names: reflection and reflection-based serialization (item 1), dynamic code (item 2), native
   declarations and calls (item 3), delegate and lambda creation sites (item 4), constructed generic types (item 6),
   static members (item 7), environment and platform reads (item 9), time (item 10), file-system sites (item 11), and
   randomness (item 12). Each search and its result is recorded. A search with no reached occurrence is recorded as such.
   A reached occurrence makes the stage uncacheable (3.4 lead).
2. **Culture.** Each reached culture-sensitive site is listed with its file and line, and makes the stage uncacheable
   (3.4 item 8). In particular `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:286` and `:333`.
3. **Class hierarchy and packages (items 5 and 13).** Class hierarchy analysis over Core gives the implementation set for
   item 5. The reachability of `SharpCompress` (`src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs:3`), of `loach.PureSharp`
   (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:24`), and of any unresolved symbol is determined for each stage and recorded.
4. **Static closure (the proof).** T-STATIC is run and recorded for every stage, whether or not its verdict is cacheable; a failure makes that stage uncacheable. This criterion is the
   static proof of the closure for that stage.
5. **Executed paths.** T-CLOSURE-RT is run and recorded for every stage, whether or not its verdict is cacheable; a failure makes that stage uncacheable. It covers **executed paths only**. It is a consistency
   check, not a proof. Criterion 4 is the proof.
6. **Recording.** The output is produced for the commit under review and recorded with its MVID, in the file of 9.3.

Criteria 4 and 5 are conditions of the U2 verdict (9.3), and Rule C (4.3) adds V-ID and T-IDENT. Until Rule C holds,
the stage stays uncacheable (6.5). There is one cacheability rule, and it is Rule C.

## 10. Open items (UNVERIFIED unless stated)

- **U1.** Extract each stage into a function that takes one record, and split `Analyze` (1.2, 2.2). Behavior-preserving,
  UNVERIFIED. Prerequisite of N12a in U2 (9.1).
- **U2.** The verdict procedure of section 9. A blocker for every stage.
- **U3.** Move the artifact writer and the persistence out of the test project, and enumerate the project fields that reach
  analysis (8.1, 1.3). Needs an ADR.
- **U4.** Measure cold N7 over the full text segment (5.1).
- **U5.** Confirm `$(NETCoreSdkVersion)` and the generated-file mechanism of 3.8 (neither is present at `2311b62`; `git grep` finds no such target), and the build determinism
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

### 12.5 Review of `d1c41ac` (r12)

| ID | Finding | Status in r13 | Resolved in |
|---|---|---|---|
| S1 (MEDIUM) | The 3.8 SDK bullet and V3 stated in the present tense that a build target reads `$(NETCoreSdkVersion)` and writes a generated file. No such target exists at `d1c41ac` | Resolved in r13: the mechanism is marked PROPOSED and UNVERIFIED (U5); V3 is conditional on U5; the key is stated as not yet established for the SDK-version field | 3.8 (SDK bullet, key status, failure rule), V3, U5 |
| S2 (LOW) | The list of places where the identifier `analyzerFingerprint` occurs was incomplete | Resolved in r13: the list names 2.2, the F4 row, and the R4 row | 2.2 |
| S3 (LOW) | "checked at `34845dd`" cited the previous review SHA, and the installed-build claim had no label or citation | Resolved in r13: the check is stated at `d1c41ac` (`src/` identical to `34845dd`); the installed-build claim is labelled UNVERIFIED ASSUMPTION with citations | 3.8 (installed-build paragraph) |
| S4 (LOW) | NativeLibraryResolver was said to load on every NativeInterop call, and the loader wording mixed module load with first binding | Resolved in r13: the statement tested is the first-binding one (`Resolve`, `:52`, on the runtime's first binding of a P/Invoke). The `[ModuleInitializer]` (`:46-50`) is stated separately as module-load registration only | 3.4 item 11 |
| S5 (LOW) | Item 9 covered only `Environment.*` | Resolved in r13: platform-identity reads are added to item 9 and criterion 1, with a direct-call result and UNVERIFIED transitive reach | 3.4 item 9, 9.4 criterion 1 |
| S6 (LOW) | The reflection list was not exhaustive | Resolved in r13: the sites found are added; the list is marked as a search result; condition (a) requires a re-run before any verdict. `RomAnalysisStageRecorder.cs:67` (`GetType` on the exception path, statically reached from the pipeline's catch blocks) is recorded | 3.4 item 1, 9.3 condition (a) |
| S7 (LOW) | Criterion 5 both established a condition and was required | Resolved in r13: criterion 5 is a gate that establishes no condition | 9.3 (verdict rule, condition (l)) |

### 12.6 Review of `d11733c` (r13)

| ID | Finding | Status in r14 | Resolved in |
|---|---|---|---|
| T1 (MEDIUM) | Reached implicit inputs had three rules: "contains any of" (3.4 lead), "classified" and "shown not to affect a value" (item 8, condition (h)), and "becomes a rule" (criterion 1) | Resolved in r14: one rule. Any reached item of 3.4 items 1 to 13 makes the stage uncacheable, and this document defines no exception. The lead, item 8, criteria 1 and 2, condition (h), and Rule C state it the same way. U2 must be deterministic for a given MVID | 3.4 lead and item 8, 9.3 (verdict rule and (h)), 9.4 criteria 1 and 2, 4.3 (Rule C) |
| T2 (LOW) | The analyzerFingerprint location list was incomplete and mis-sectioned | Resolved in r14: the four occurrences are listed with their sections: 2.2, the F4 row of 12.3, the R4 row of 12.4, and the S2 row of 12.5 | 2.2 |
| T3 (LOW) | Check labels mixed `d1c41ac` and `34845dd` with the head SHA, which the document never named | Resolved in r14: check labels unified to `d11733c`; a source-identity statement added in the header | Header (Source identity), check labels in sections 2 to 10 |
| T4 (LOW) | The gate file was reported with `ANALYSIS_CACHE_KEY_UNAVAILABLE`, which F5 did not define for the gate | Resolved in r14: F5 is extended to cover a missing, unreadable, or mismatched gate file, and states what the code means | F5, 9.3 (binding) |
| T5 (LOW) | R3 omitted `dependencyIdentity`, which the key formula includes | Resolved in r14: R3 includes it | R3, 3.2 |
| T6 (LOW) | Unverified statements were written as fact: the build determinism (3.3), the line-ending independence of the MVID (3.9), and the generated SDK file in F5 | Resolved in r14: each is qualified as UNVERIFIED (U5) or PROPOSED | 3.3, 3.9, F5 |
| T7 (LOW) | The loader wording mixed module load with first binding | Resolved in r14: the two events are stated separately, and S4 says that the first-binding statement is the one tested | 3.4 item 11, 12.5 S4 |

### 12.7 Review of `6f1494a` (r14)

| ID | Finding | Status in r15 | Resolved in |
|---|---|---|---|
| U1 (MAJOR) | The runtime-version read (`RuntimeInformation.FrameworkDescription`) was called "an explicit exception to item 9", while the document defines no exception | Resolved in r15: the exception is removed. `git grep` at `d11733c` finds the read only in `src/PSXRecomp.Cli`, `src/PSXRecomp.Infrastructure`, and `src/PSXRecompStudio`, none in Core. The host reads it outside every Core-only stage closure, so it is not a reached item and the single rule holds | 3.8 (run-time version bullet) |
| U2 (MEDIUM) | Criteria did not cover the items cited for them: item 1, item 3, and item 11 were cited to criterion 1, which covered only guards 2, 9, 10, 12 and the platform reads | Resolved in r15: criterion 1 now covers the searches for items 1, 2, 3, 4, 6, 7, 9, 10, 11, and 12; criterion 2 covers item 8; criterion 3 covers items 5 and 13. Each condition (a) to (j) cites the criterion that covers its item | 9.3 (conditions (a) to (j)), 9.4 criteria 1 to 3 |
| U3 (MEDIUM) | The default state (no gate file, U2 not run) was reported with two codes: `ANALYSIS_CACHE_UNCACHEABLE` (F3, T-UNCACHE) and `ANALYSIS_CACHE_KEY_UNAVAILABLE` (F5, 9.3) | Resolved in r15: one code per state. The default state, and any gate file that is missing, unreadable, or mismatched, report `ANALYSIS_CACHE_UNCACHEABLE` (F3). `ANALYSIS_CACHE_KEY_UNAVAILABLE` is reserved for an unreadable key input. This supersedes T4 of 12.6, which had put the gate file under the key code | F3, F5, 9.3 (binding), T-UNCACHE |
| U4 (LOW) | Item 7 had an escape ("unless the write is proven to occur only before the first stage run") and was limited to items 8 to 12, which departs from the single rule | Resolved in r15: item 7 is under the same rule as the other items (a reached static initializer, read, or write makes the stage uncacheable), with the conservative consequence stated. Condition (g) matches | 3.4 item 7, 9.3 condition (g) |
| U5 (LOW) | The T2 row of 12.6 contains the identifier `analyzerFingerprint`, and the 2.2 list did not name it | Resolved in r15: the 2.2 list names the T2 row | 2.2 |
| U6 (LOW) | "A change anywhere in Core changes `codeIdentity`" was unqualified, while the MVID is content-derived only if the build is deterministic (U5) | Resolved in r15: the rule is qualified by the determinism condition (UNVERIFIED, U5), and the non-deterministic case is stated | 3.3 (DEC-CODE rule) |
| U7 (LOW) | Citations in 3.4 items and the verdict conditions used bare file names | Resolved in r15: they use full `src/` paths, and the partial forms in item 9 are also full paths | 3.4 items 1 to 13, 9.3 conditions |

### 12.8 Review of `e1afbba` (r15)

| ID | Finding | Status in r16 | Resolved in |
|---|---|---|---|
| V1 (MAJOR) | Item 7 read literally made every stage uncacheable, because each 9.2 root is a static member: N8, N9, and N10 are static methods, N7 is a private static method, and N11 is a block inside the private static method `RunFromIsoSectorReader`. Corrected in r17 (W1) | Resolved in r16: item 7 is split into (i) static mutable state, which makes a stage uncacheable when reached, and (ii) static methods and read-only values, which do not by themselves. The test is stated for each reached member. Condition (g) and 9.2 agree. Whether N8 and N9 are cacheable under this rule is UNVERIFIED and left to U2 | 3.4 item 7, 9.2 (roots), 9.3 condition (g) |
| V2 (MEDIUM) | The installed-build state (no gate file, and key inputs unreadable) had two codes, and "one code per state" had no precedence rule | Resolved in r16: precedence rule added. The gate check comes first, so a state without a matching gate reports `ANALYSIS_CACHE_UNCACHEABLE`. `ANALYSIS_CACHE_KEY_UNAVAILABLE` only when the gate matches and a key input is unreadable. Stated in 3.8, F3, F5, and 9.3 | 3.8 (failure rule, installed build), F3, F5, 9.3 (binding and precedence). Order as corrected in r17 (W3): an unreadable MVID gives KEY_UNAVAILABLE first, then a missing or mismatched gate file gives UNCACHEABLE, then an unreadable dependency input with a matching gate gives KEY_UNAVAILABLE |
| V3 (MEDIUM) | T-COLD-WARM, T-UNCACHE, T-KEY-UNAVAIL, and T-CODEC were marked "Runs now", but their seams, codec, and codes do not exist in `src/` | Resolved in r16: each is relabelled "needs" with the seam, code, or codec it needs, and 7.3 says such a test cannot run until the named item exists | 7.3 (table cells and gating paragraph) |
| V4 (LOW) | The list of locations of the fingerprint identifier omitted the U5 row | Resolved in r16: the 2.2 paragraph names all six lines, which the grep count confirms | 2.2 |
| V5 (LOW) | Item 9 did not name `RuntimeInformation.FrameworkDescription`, so 3.8 relied on the words "platform identity" | Resolved in r16: item 9 names it, and 3.8 cites item 9 for it | 3.4 item 9, 3.8 (run-time version bullet) |

### 12.9 Review of `6a871a5` (r16)

| ID | Finding | Status in r17 | Resolved in |
|---|---|---|---|
| W1 (MEDIUM) | Item 7 and 9.2 said every root is a public static method. N7 is `private static` (`RomAnalysisPipeline.cs:501-502`), and N11 is a block inside the `private static` `RunFromIsoSectorReader` (`:206`, block at `:400-444`) | Resolved in r17: 9.2 and item 7 name the visibility of each root, and state that a root can be a method or a block. U2 must resolve private members and blocks, not only public methods. V1 of 12.8 is corrected | 9.2 (roots), 3.4 item 7, 12.8 V1 |
| W2 (MEDIUM) | The class at `BiosCallRecognition.cs:130` was named `BiosCallRecognition`; it is `BiosCallRecognizer`, and its call site is `RomAnalysisPipeline.cs:432` | Resolved in r17: the class name is corrected in 9.2 and item 7, and the call site is named | 9.2 (N10), 3.4 item 7 |
| W3 (MEDIUM) | An unreadable MVID was reported as KEY_UNAVAILABLE, but the precedence rule needed a matching gate, so T-KEY-UNAVAIL could not pass as written | Resolved in r17: the order is explicit in 9.3, 3.8, F3, F5, and 12.8. Step 1: an unreadable MVID gives KEY_UNAVAILABLE. Step 2: a missing, unreadable, or mismatched gate file gives UNCACHEABLE. Step 3: an unreadable dependency input with a matching gate gives KEY_UNAVAILABLE. T-KEY-UNAVAIL states its three cases | 9.3 (precedence), 3.8, F3, F5, T-KEY-UNAVAIL, 12.8 V2 |
| W4 (LOW) | The seam and codec absence was cited at `e1afbba`, and the header said `d11733c` was the current head | Resolved in r17: check labels are `6a871a5`, the header names `6a871a5` as the current head, and the seam grep is stated as unchanged | Header (Source identity), 7.3, 7.3 table |
| W5 (LOW) | The N12a root cited `RealRomCoverageAnalyzer.cs:88`, the whole `Analyze` method, as "the classification function split out of `Analyze`", which does not exist | Resolved in r17: the root is the body of `Analyze` (`RealRomCoverageAnalyzer.cs:88-187`). The split is a proposal that U1 must make | 9.2 (N12a) |

### 12.10 Review of `5e658f5` (r17)

| ID | Finding | Status in r18 | Resolved in |
|---|---|---|---|
| X1 (MEDIUM) | The exception overload of `Fail` was listed at three sites (`:124`, `:225`, `:247`) only. It has ten call sites, including `:359` (N7), `:385` (N8), and `:437` (N11) | Resolved in r18: the search is complete at `5e658f5` (ten sites, each stage named). The N9 and N10 calls sit in the N11 `try` at `:401`, and the unit rule for each is stated | 3.4 item 1 |
| X2 (LOW) | The source identity named `6a871a5` as the current head, and the `src/` diff to `5e658f5` was not stated | Corrected in r19 (Y1): r18 changed the label but left the header naming `5e658f5` as the current head. Now the header names the head checked in this revision, `882500e`, and the identical-tree statement covers it | Header (Source identity) |
| X3 (LOW) | The N7 root was described as the sweep without count, but the body takes `count` | Resolved in r18: the body is described with `count` (`:502`, `:511`). The count-free sweep is the proposal DEC-N7 | 9.2 (N7) |
| X4 (LOW) | N12a cited the per-instruction `Classify` next to a claim that no classification function exists | Resolved in r18: no whole-analysis classification root exists. `Classify` decides one instruction and is not the N12a root | 9.2 (N12a) |
| X5 (LOW) | The V1 rationale in 12.8 was false for N11, and only the Resolved column had been corrected | Resolved in r18: the finding text of V1 is corrected | 12.8 (V1) |
| X6 (LOW) | The running MVID was said to be exposed by Core, with no mechanism or citation | Resolved in r18: the read is placed in the host's composition root, outside every stage closure, and marked UNVERIFIED. `git grep` finds no MVID read at `5e658f5` | 9.3 (binding) |

### 12.11 Review of `882500e` (r18)

| ID | Finding | Status in r19 | Resolved in |
|---|---|---|---|
| Y1 (MEDIUM) | The header still named `5e658f5` as the current head, so X2 was not resolved | Resolved in r19: the header names `882500e`, and the identical-tree statement covers it. X2 is corrected above | Header (Source identity), 12.10 X2 |
| Y2 (MEDIUM) | The count-free sweep was called "Decision DEC-N7" in 5.1 and described as settled in 3.5 and 4.2, while 9.2 called it a proposal | Resolved in r19: 5.1 says "Proposal DEC-N7 (not adopted)" with an adoption bullet (owner decision, or U4 measurement plus owner acceptance). 2.4, 3.5 (N7 and N8), 4.2 (N7 and N8), 5.1, T-PREFIX, and 9.2 state the current count-based N7 record and root, with `count` | 2.4, 3.5, 4.2, 5.1, 7.3 (T-PREFIX), 9.2 |
| Y3 (MEDIUM) | "All stage bodies are blocks of RunFromChdOpen or RunFromIsoSectorReader, so a method-level unit reaches every site" was false for N7, whose root is the method `DecodeInstructions` | Resolved in r19: one unit rule per stage is given in a table in 9.2 (method-level for N7, N8, N9, N10, and N12a; block-level for N11). Under the table, no unit contains a call to the exception overload. Item 1 refers to the table and no longer uses the "all blocks" argument | 9.2 (unit table), 3.4 item 1 |
| Y4 (LOW) | "All in RomAnalysisPipeline.cs" and "no other path to `:67`" were inaccurate, because the test project has exception-overload calls | Resolved in r19: the test project's calls are listed (`RealRomAnalyzer.cs:113`, `:153`, `:255`, and `RomAnalysisStageRecorderTests.cs:57`). They are outside Core and outside every cache candidate. The only route to `:67` is a call to the exception overload | 3.4 item 1 |
| Y5 (LOW) | The layer of the production key reads (`project.assets.json`, `Directory.Build.props`, the gate file) was unassigned in 8.1 | Resolved in r19: 8.1 assigns the key reader to Infrastructure as `FileStageKeyInputReader` (a proposed name), created by the host's composition root. Core defines only the port and does no file access. 3.8 refers to it | 8.1 (Resolution), 3.8 (run-time reads) |

### 12.12 Review of `f4b95ab` (r19)

| ID | Finding | Status in r20 | Resolved in |
|---|---|---|---|
| Z1 (MEDIUM) | The N12a unit row was the method `Analyze`, which contains `BuildDifferential` at `:185`, so it contradicted 3.5 (N12a must not reach `BuildDifferential` or `validations`) | Resolved in r20 by option (b): N12a is not a candidate and is uncacheable until U1 creates a classification unit that excludes `:185`. The 2.2 row, the N12a paragraph, 3.5, 4.2, and the 9.2 bullet and table all say this | 2.2, 3.5 (N12a), 4.2, 9.2 (N12a bullet and unit table) |
| Z2 (LOW) | The verdict had two carriers: `IStageCodeIdentity` in 7.1 and `AnalysisCacheOptions` in 9.3 | Resolved in r20: `IStageCodeIdentity` is the single carrier of the verdict. `AnalysisCacheOptions` holds that port. 7.1, 9.3, and T-UNCACHE say so | 7.1, 9.3 (binding), 7.3 (T-UNCACHE) |
| Z3 (LOW) | The key reader's list omitted the MVID read, and "Core performs no file access" was contradicted by the Core-wide sites of 3.4 item 11 | Resolved in r20: the MVID read is in the host-side list. The Core claim now reads "the stage closure performs no file access except the sites listed in 3.4 item 11, and none of those reads these inputs" | 8.1 (reader), 3.8 |
| Z4 (LOW) | The composition root that creates the reader was not named, and the AARC002 position under `docs/architecture-matrix.md:47` was not stated | Resolved in r20: the creators are the `PSXRecomp.Cli` and `PSXRecomp.RuntimeHost` composition code, whose namespaces are Infrastructure, so no suppression is needed. `PSXRecompStudio` (Application) would need the one composition-root suppression of `:47`, which this design does not grant (owner item) | 8.1 (layer note), 3.8 |
| Z5 (LOW) | The exception-overload clauses were paraphrased, and the filters were not shown | Resolved in r20: the clauses are quoted exactly, with the filter, and each is mapped to its `Fail` line | 3.4 item 1 |
| Z6 (LOW) | The no-reach scope in item 9 omitted `src/PSXRecomp.Core/Cpu`, and the item 1 scope was not stated per file | Resolved in r20: `Cpu` has no recorder, `Fail`, platform read, or reflection pattern (grep at `f4b95ab`). The recorder's references are listed. `PrintfService.Fail` is unrelated | 3.4 item 1, 3.4 item 9 |

### 12.13 Review of `597e1aa` (r20)

| ID | Finding | Status in r21 | Resolved in |
|---|---|---|---|
| AA1 (MEDIUM) | §9.3 said a matching gate made the stage cacheable and that "the gate's verdict applies", but Rule C also needs V-ID and T-IDENT, and V-ID is not implemented. A gate-backed reader would have given Normal-mode hits without V-ID | Resolved in r21: the gate file's verdict is one input to Rule C. A stage is cacheable only when Rule C holds in full (U2 verdict, V-ID, T-IDENT). V-ID is not implemented, so every stage is uncacheable whether or not a gate file matches. The header, 4.3, 7.1, and 9.3 say so | 9.3 (binding and precedence), header, 4.3 (Rule C), 7.1 (Normal mode) |
| AA2 (LOW) | The statement that the stage closure performs no file access except the 3.4 item 11 sites, and reads none of the key inputs, was stated as fact. The check was direct-call only | Resolved in r21: §3.8 and §8.1 carry the same wording, with "direct-call check at `597e1aa`; transitive reach is UNVERIFIED, and U2 checks it under 9.4 criterion 1" | 3.8 (run-time reads), 8.1 (reader) |
| AA3 (LOW) | T-IDENT2 asserted that the N11 and N12a keys differ, but N12a is not a candidate until U1 | Resolved in r21: the assertion covers N11 only, and T-IDENT2 is extended to N12a when U1 creates a candidate unit. Its gate is Rule C for N11 | 7.3 (T-IDENT2) |

### 12.14 Review of `2311b62` (r21)

| ID | Finding | Status in r22 | Resolved in |
|---|---|---|---|
| BB1 (MEDIUM) | §2 (U2 framing) said U2 "can make a stage cacheable", which is the framing r21 meant to remove | Resolved in r22: U2 is one of the three conditions of Rule C, with V-ID and T-IDENT as the other two | 9 (U2 framing) |
| BB2 (LOW) | AA2 said §3.8 and §8.1 carry the check at `f4b95ab`, but both say `597e1aa` | Resolved in r22: the AA2 row names `597e1aa` | 12.13 (AA2) |
| BB3 (LOW) | The §3.8 and §8.1 file-access sentences used different wording | Resolved in r22: both carry the same sentence, labelled direct-call verified at `2311b62`, with transitive reach UNVERIFIED | 3.8 (run-time reads), 8.1 (key reader) |
| BB4 (LOW) | `IStageCodeIdentity` was said to return "the stage's uncacheable verdict", while §9.3 passes the U2 cacheable verdict and T-UNCACHE injects an uncacheable one | Resolved in r22: the port returns the Rule C inputs for each stage (`codeIdentity`, `dependencyIdentity`, the U2 verdict, the V-ID state, and the T-IDENT state). A stage is cacheable only when all three conditions hold. §7.1, §9.3, and T-UNCACHE match this | 7.1 (IStageCodeIdentity, AnalysisCacheOptions), 9.3 (binding), 7.3 (T-UNCACHE) |
| BB5 (LOW) | "gate" meant both the gate file and Rule C | Resolved in r22: "gate file" is used for the file in the header, §3.8, §4.3, §6.4 (F3, F5), §7.1, §7.3 (the gating paragraph and the T-UNCACHE and T-KEY-UNAVAIL rows), §8.1, and §9.3, as listed here, not only in the rows of 12.14 (its "gate-file check" wording). "Rule C" is used for the cacheability decision. Criterion 5 is "a required condition", and "There is one cacheability rule, and it is Rule C" | 7.1, 7.3, 9.3, 9.4 criterion 5 and its condition (l), 4.3 |
| BB6 (LOW) | The source-identity header named `597e1aa` as the head checked, and did not name `2311b62` | Resolved in r22: the header names `2311b62` as the current head, and the identical-tree statement covers it | Header (Source identity) |

### 12.15 Review of `e3db1c8` (r22)

| ID | Finding | Status in r23 | Resolved in |
|---|---|---|---|
| CC1 (MEDIUM) | §9.3 "Invalidation" listed three re-enabling steps and omitted V-ID and T-IDENT, which Rule C requires | Resolved in r23: re-enabling requires all of Rule C on the new MVID (a cacheable U2 verdict for the stage, V-ID implemented for the stage, T-IDENT passing, the gate file committed as the store record of that verdict, and the 9.4 acceptance criteria). Re-enabling is impossible while V-ID is unimplemented | 9.3 (invalidation) |
| CC2 (LOW) | The §9.3 binding sentence passed only the U2 verdict into `IStageCodeIdentity`, while §7.1 and 12.14 BB4 define five inputs | Resolved in r23: the binding sentence lists the five inputs (`codeIdentity`, `dependencyIdentity`, the U2 verdict from the gate file, the V-ID state, and the T-IDENT state) | 9.3 (binding) |
| CC3 (LOW) | The header line 8 used "gate" for the gate file | Resolved in r23: "a matching gate file does not make a stage cacheable" | Header (status) |
| CC4 (LOW) | BB5 named T-COLD-WARM as a location of the "gate file" wording, but that row does not contain it | Resolved in r23, and corrected in r24 (DD3): the BB5 list is now complete (header, §3.8, §4.3, §6.4, §7.1, §7.3, §8.1, §9.3) | 12.14 (BB5) |
| CC5 (LOW) | BB3 said the check was labelled `597e1aa`, but §3.8 and §8.1 say `2311b62` | Resolved in r23: BB3 names `2311b62` | 12.14 (BB3) |
| CC6 (LOW) | The header called `2311b62` the current head, while the document's own head is `e3db1c8` | Resolved in r23, and corrected in r24 (DD2): the header names the reviewed head, `0a5bcff`, and says that the `src/` tree is identical through it. The body check labels are `2311b62` | Header (Source identity) |
| CC7 (LOW) | §3.5 and §4.2 said "U1 target" and "uncacheable until then" without saying N12a is not a candidate, unlike §2.2 and §9.2 | Resolved in r23: §3.5, §4.2, and §9.2 say "not a candidate until U1 creates its unit", and the verdict is uncacheable until then | 3.5 (N12a), 4.2 (N12a), 9.2 (N12a bullet) |

### 12.16 Review of `0a5bcff` (r23)

| ID | Finding | Status in r24 | Resolved in |
|---|---|---|---|
| DD1 (MEDIUM) | §3.5 listed `Functions` and the BIOS evidence as N11 record values, but the unit computes them at `RomAnalysisPipeline.cs:404` and `:432`. §9.2 and §3.5 disagreed on the static check | Resolved in r24 by choosing (b): N11 recomputes the N9 and N10 values. The record lists only the values the block reads from the enclosing method (identity and header fields, `instructions`, the decode failures, `BasicBlocks`, `CfgEdges`). N11 does not read N9 or N10 caches. The one-parameter rule applies to method units, and a block unit's record is in 3.5. T-READ now says so | 3.5 (N11), 9.2 (unit table note, static check), T-READ |
| DD2 (LOW) | The header and CC6 named `e3db1c8` as the current head, which is not the head the doc was checked at | Resolved in r24: the header names the reviewed head, `0a5bcff`, and says `src/` is identical through it, because the revision changes only the doc. The body check labels are `2311b62` | Header (Source identity), 12.15 (CC6) |
| DD3 (LOW) | The gate-file location lists in BB5 and CC4 were incomplete | Resolved in r24: the list is now the header, §3.8, §4.3, §6.4 (F3, F5), §7.1, §7.3 (the gating paragraph and the T-UNCACHE and T-KEY-UNAVAIL rows), §8.1, and §9.3 | 12.14 (BB5), 12.15 (CC4) |
| DD4 (LOW) | The re-enabling list in §9.3 did not match Rule C: it added "committing the new gate file" and called V-ID per-stage | Resolved in r24: the re-enabling list has the same conditions as Rule C: (a) a cacheable U2 verdict on the new MVID, (b) V-ID implemented as a global condition, (c) T-IDENT passing. The gate file is the store record of (a), not a Rule C condition. Rule C (b) is marked global | 4.3 (Rule C), 9.3 (invalidation) |
| DD5 (LOW) | §9.4 criteria 4 and 5 applied only to stages marked cacheable, so condition (l) depended on the marking | Resolved in r24: T-STATIC and T-CLOSURE-RT are run and recorded for every stage. A failure makes that stage uncacheable | 9.4 (criteria 4 and 5), 9.3 (condition (l)) |
| DD6 (LOW) | T-IDENT said the GUI computes the same hash. The GUI passes `discImageSha256` through, and its origin is unread | Resolved in r24: the row names the CLI and the test driver as the hashers, and states that the GUI passes the value through, with its origin UNVERIFIED (U6) | 7.3 (T-IDENT) |
| DD7 (LOW) | Two citations (`RomAnalysisStage.cs:10` and `RealRomAnalyzer.cs:261-262`) had no path | Resolved in r24: both use full `src/` paths | 2.1 gating note (RomAnalysisStage), 6.1 |
