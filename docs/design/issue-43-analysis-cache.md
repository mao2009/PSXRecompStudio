# Issue #43: Incremental Analysis and Cache (design, r9)

- Status: **Design. No stage is cacheable.** Until the U2 gate passes for a stage, that
  stage is uncacheable in every mode. An uncacheable stage is recomputed on every run,
  and no entry is written for it, in Verify mode as well (section 6.5). The hit path is
  therefore not exercised by any test until U2 passes.
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r9. Addresses the final review of `cdd1634` (r8): 3 major, 2 medium, 3 low.
  Section 12 maps each finding to its resolution. Section 9 specifies U2 as a standalone
  task.

## 0. Why there is no static file partition

The r2 to r5 static file partition is withdrawn (owner ruling). Checked at base
`2e3ecad` by `git grep`:

- **CONFIRMED** `src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs` calls
  `BiosJumpTables.TryResolveVectorFamily` (`:336`) and uses `BiosCallFamily` (`:44`,
  `:66`, `:237`, `:286`, `:567`). Both are declared under `src/PSXRecomp.Core/Runtime/`
  (`BiosCallFamily` at `Runtime/BiosCallIdentity.cs:68`).
- **CONFIRMED** `src/PSXRecomp.Core/DiscImage/ChdCdSectorSource.cs` implements
  `ICdSectorSource` (`:15`) and uses `CdDiscToc` (`:30`, `:33`). Both are declared in
  `src/PSXRecomp.Core/Runtime/CdRom/ICdSectorSource.cs`: `CdDiscToc` at `:11` and
  `ICdSectorSource` at `:56`.
- **CONFIRMED** `ArtifactJson` is called from C-side files:
  `src/PSXRecomp.Core/Recompiler/RecompilerContract.cs:1126`, `:1132`, and
  `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/RealRomCoverageDocument.cs:245`.
- **CONFIRMED** `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:147` writes
  `AnalysisArtifactSchema.CoverageArtifactKind`.

A file partition cannot show that a result depends only on the declared files. The
design therefore uses (a) the whole Core assembly as the code identity (3.3), (b) an
explicit input record per stage, which holds the values the stage reads (3.5), and (c) an
explicit list of implicit inputs that make a stage uncacheable (3.4).

## 1. Current state

### 1.1 What exists today

- **CONFIRMED** No persistent analysis cache exists. The pipeline is a pure function of
  caller-supplied bytes: "It is a pure domain component: all bytes are supplied by the
  caller and no artifact is written here" (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:23-24`).
- **CONFIRMED** The only caches are lifetime-scoped: the CHD hunk cache
  (`src/PSXRecomp.Core/DiscImage/ChdReader.cs:85`, `:97`, `:187-193`) and the coverage
  probe cache (`src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs:98`, `:237-259`).
- **OBSERVED** No AI analysis result producer exists. `git grep AIPrediction` at base
  returns only `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`.
- **CONFIRMED** Persistence is in the test-project driver, not in Core (section 2.3).

### 1.2 Stages are inlined

- **CONFIRMED** `RomAnalysisPipeline.RunFromIsoSectorReader` (starting at
  `RomAnalysisPipeline.cs:206`) runs every stage inline. The decode call is at `:355`,
  and the report at `:400-444`.
- **Prerequisite R-0.** Each cached stage is extracted into a function that takes one
  input record, holding values (3.5), and returns its output. Coverage is split into
  classification (N12a) and differential assembly (N12d, 2.1). The refactor must be
  behavior-preserving. Its tests are the existing suite plus T-PREFIX and T-DIFF (U1).

### 1.3 YAML, schema, project

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml:11` declares `meta.version: "1.0"`.
  **OBSERVED** no code parses it. Governing decisions are `docs/adr/001-cpu-specification-ssot.md`
  and `docs/adr/002-instruction-definition-yaml.md`. The YAML is out of every key until a
  reader exists (rule Y1).
- **CONFIRMED** Schema constants: `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`, `CoverageSchemaVersion = 1`,
  `FunctionProvenanceSchemaVersion = 1` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-49`).
  `CoverageArtifactKind` at `:64`.
- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
  Which project fields reach analysis is UNVERIFIED (U3).

## 2. Entry points, stages, and inputs

### 2.1 Entry points (MEDIUM-E)

The pipeline has three public entry points, and the callers differ.

| Entry point | Definition | Callers (CONFIRMED) | Stages covered | Supplied identity |
|---|---|---|---|---|
| `RunFromChd(byte[] discImageBytes, string discImageSha256, ...)` | `RomAnalysisPipeline.cs:42-62`; `ValidateInput` at `:57` | none in `src/` outside tests (callers not read; UNVERIFIED) | N1 to N15 (CHD path: N2 runs) | non-empty check only (`:194-198`) |
| `RunFromChd(Stream chdStream, string discImageSha256, ...)` | `RomAnalysisPipeline.cs:78-104` | CLI `src/PSXRecomp.Cli/CliInput.cs:115`; GUI `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:117` | N1 to N15 (N2 runs) | non-empty check only (`:93-98`) |
| `RunFromIsoImage(byte[] isoImageBytes, string discImageSha256, ...)` | `RomAnalysisPipeline.cs:142-164` | GUI `RealRomTitleExecutionService.cs:97` | N1, N3 to N15 (N2 is skipped: `RomAnalysisPipeline.cs:163`) | non-empty check only (`:194-198` via `ValidateInput`) |

Notes:

- **CONFIRMED** The CLI path hashes the file in one pass (`CliInput.cs:106-109`) and then
  opens the file again for the pipeline (`:113-115`). The two passes are not atomic.
- **CONFIRMED** The GUI's `discImageSha256` comes in as a parameter of
  `AnalyzeAndExecuteFromDisc` (`RealRomTitleExecutionService.cs:110-117`). Its caller was
  not read (UNVERIFIED, U6).
- **CONFIRMED** The test driver `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs`
  hashes the file itself (`:102-104`) and calls `RunFromChd(Stream)` (`:122`).

### 2.2 Stage table

"Candidate" means the stage is a cache candidate, not cacheable. Every candidate is
uncacheable until U2 passes for it (section 9). The record column names the values the
stage's input record holds (3.5).

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
| N12a classification | candidate | `CoverageInput` (3.5): the N11 report and the fixture identity. Nothing else | `RealRomCoverageAnalyzer.cs:88`, classification `:223`, `:234-261`; `LowerProgram` call at `:251`; definition `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176` |
| N12d differential | not cached | `validations`, supplied on that run | `RealRomCoverageAnalyzer.cs:85-88`, assignment `:185`, `BuildDifferential` `:377-382` |
| N14 artifact documents | not cached | runs each run | `DeterministicArtifactBuilder.cs:74`, `:96-99`, `:115-121`, `:124`; `ArtifactJson.cs:32` |
| N15 persistence | not cached | runs each run | `RealRomArtifactWriter.cs:44-50` |

**N12a and N12d (MAJOR-1 of r7, carried).** `Analyze(report, fixture, validations = null)`
(`RealRomCoverageAnalyzer.cs:88`) returns `Differential = BuildDifferential(validations)`
(`:185`). N12a is a function of `(report, fixture)` only. N12d is a function of the N12a
result and `validations`, and it is never cached.

### 2.3 Fixture identity and the driver (MEDIUM-E)

- **CONFIRMED** The driver builds `DeterministicArtifactInput` at
  `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:162-171`. It sets `FixtureId`
  (`:164`), `DiscImageFormat`, `DiscImageSha256`, `DiscImageSizeBytes`, `Chd`, `Iso`, and
  `Report`.
- **CONFIRMED** `DeterministicArtifactBuilder.BuildFixtureIdentity` is at
  `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/DeterministicArtifactBuilder.cs:159-178`,
  and the driver calls it at `RealRomAnalyzer.cs:179`, inside
  `RealRomCoverageAnalyzer.Analyze(...)`.
- **CONFIRMED** The driver calls `DeterministicArtifactBuilder.Build` at
  `RealRomAnalyzer.cs:181`. That is N14, which is not cached.
- **Consequence.** `FixtureId` is an input of N12a, and it comes from the driver, not from
  Core. Its record value is supplied by the caller (3.5, N12a). A different driver
  supplies a different `FixtureId` and therefore a different N12a input.

### 2.4 Data flow

- A stage's output is the only thing a later stage reads from it. N8 reads the sliced
  decoded list (the output of N7 after the slice in 5.1), and nothing else of N7.
- A stage's input record holds the **values** it reads (3.5). The key is computed from a
  digest of the record (3.2). The digest is used for the key and for nothing else.
- When an upstream stage is a cache hit, its payload is decoded by the codec (3.7, R6)
  into the value that the downstream record holds. A hit never leaves a downstream stage
  with a digest it cannot evaluate.

## 3. Cache key

### 3.1 Principle

An entry is reused in a run only if the stage would compute the same payload in that run.
The key therefore covers:

- **The code**: the whole Core assembly identity (3.3).
- **The values read**: the input record, digested for the key (3.2), and held as values
  for the computation (3.5).
- **The implicit inputs**: anything read that is not in the record makes the stage
  uncacheable (3.4).
- **The build and runtime inputs outside Core source** (3.8).

### 3.2 Key definition, and the role of digests (MAJOR-A)

```
key(s) = SHA-256( canonical( keyFormat, s,
                             codeIdentity,
                             dependencyIdentity,
                             digest(inputRecord(s)) ) )

digest(R) = SHA-256( codecBytes(R) )
```

- `codecBytes(R)` is the canonical codec encoding of the record `R` (3.7). The codec is
  injective, which T-CODEC tests.
- **A digest is never the input of a computation.** A stage computes from the record's
  values (3.5). The digest is used only to form the key. A stage whose input is only a
  digest cannot be computed, and this design does not allow one. The r8 wording that
  implied a digest was enough is withdrawn.

### 3.3 Code identity: the whole Core assembly

- **Decision DEC-CODE.** `codeIdentity` is the metadata version identifier (MVID) of
  `PSXRecomp.Core`, with its full name. The build is deterministic, so the MVID is a
  content-derived identifier of the compiled output. It changes whenever the IL or the
  metadata of Core changes, including bindings, extension methods, implicit operators,
  partial declarations, static initializers, and global usings that affect Core's
  compilation. Determinism of the build is UNVERIFIED (U5).
- **Rule.** A change anywhere in Core changes `codeIdentity` and invalidates every cached
  stage. This is accepted: accuracy beats performance (owner ruling).
- **Why not a source-file closure digest.** A closure of source files cannot see a binding
  change that lies outside it. The compiled assembly does. A closure is used only to scope
  the implicit-input scan (3.4), not as a code key.

**Non-Core code (MAJOR-3 of r7).** Code outside `PSXRecomp.Core` does not enter the code
identity. This is correct only because of the following, all CONFIRMED at base:

- The driver supplies only record values to the cached stages. The fixture identity
  record is built at `RealRomAnalyzer.cs:162-171`, and N12a receives it through the
  record. The driver's `DeterministicArtifactBuilder.Build` call (`:181`) is in N14, which
  is not cached.
- N14 and N15 are not cached. The writer (`RealRomArtifactWriter.cs:44-50`) and the
  artifact assembly run each time.
- The CLI (`CliInput.cs:100-116`) and the GUI (`RealRomTitleExecutionService.cs:97-117`)
  call the pipeline's entry points (2.1) and do not run analysis code of their own.

**Rule NC.** A cached stage's reached code may call non-Core code only through a record
value or a declared input. A cached stage that calls a non-Core symbol (a Test,
Infrastructure, Application, or CLI symbol, or a non-allow-listed package) is an implicit
input and is uncacheable (3.4 items 4 and 5). The U2 analysis (section 9) is the check.

### 3.4 Implicit inputs: uncacheable rules

A stage is **uncacheable** if its reachability (scoped by U2) contains any of the following.
An uncacheable stage is recomputed on every run, and no entry is written for it in any
mode (6.5). Each run reports `ANALYSIS_CACHE_UNCACHEABLE` once per stage.

1. **Reflection.** `typeof` used for reflection, `GetType`, `Activator`, `MethodInfo`, or
   `System.Reflection` APIs. Also reflection-based serialization: the generic method
   `ArtifactJson.Serialize<T>` calls `JsonSerializer.Serialize(document, CanonicalOptions)`
   (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:54`), which reflects over
   `T` at run time. The cached payload codec must not reflect (3.7).
2. **Dynamic code.** `dynamic`, expression-tree compilation, or runtime code generation.
   *Generic guard. Not searched for in this revision; the search is an acceptance
   criterion of U2.*
3. **Native calls.** P/Invoke, `LibraryImport`, or any call through `NativeInterop.cs` (root of Core).
4. **Delegates from outside.** A delegate or function value not declared in the input
   record. Its target is unknown.
5. **Interface or virtual dispatch with implementations outside Core.** Class hierarchy
   analysis over Core gives the implementation set. If any implementation lies outside
   Core, the set is unknown. This covers Rule NC.
6. **Generic instantiation with a type argument outside Core.** The instantiation's code is
   not in the Core assembly.
7. **Static initialization and static mutable state.** Static initializers are part of the
   Core identity. `BiosJumpTables` (`src/PSXRecomp.Core/Runtime/BiosJumpTables.cs:50`) is a
   static class, and `ArtifactJson.CanonicalOptions` (`ArtifactJson.cs:34`) is a static
   field. A static initializer or static field read by the stage is uncacheable if it reads
   any implicit input in items 8 to 12. A static field written after initialization is
   uncacheable unless the write is proven to occur only before the first stage run.
8. **Culture.** Any reached API whose result depends on `CultureInfo.CurrentCulture` or
   `CurrentUICulture`. The pipeline's formatting is not uniform: some sites use
   `string.Create(CultureInfo.InvariantCulture, ...)` (for example `RomAnalysisPipeline.cs:100-101`),
   while others interpolate without it (`:286` and `:333`). Each reached site must be
   checked by U2.
9. **Environment.** `Environment.*` (variables, machine name, user, OS version, current
   directory). *Generic guard. Not searched for in this revision; acceptance criterion of U2.*
10. **Time.** `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `Stopwatch` used in a
    value, and time zone lookups. *Generic guard; acceptance criterion of U2.*
11. **File system.** `System.IO` reads or directory enumeration. A file read is an input and
    must be in the record as a value, not read implicitly.
12. **Randomness.** `Random`, `RandomNumberGenerator`, `Guid.NewGuid`, and hash codes of
    reference types. *Generic guard; acceptance criterion of U2.*
13. **Unresolved symbols or an assembly outside the allow-list.** The allow-list is the BCL
    and `PSXRecomp.Core`. The architecture attribute source is compiled into Core (3.8), so
    it is part of Core. Any other package is outside the list, and a closure that reaches one
    is uncacheable. The packages named in 3.8 are outside the list: `SharpCompress` 0.50.4
    (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:25`, used by `src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs:3`)
    and `loach.PureSharp` 0.1.6 (`PSXRecomp.Core.csproj:24`, `PrivateAssets="all"`). Whether a
    stage reaches `loach.PureSharp` is UNVERIFIED (U2).

Items 8 to 12 are the implicit inputs of the BCL. The design does not assume the BCL is fixed.

### 3.5 Input records (values, not digests)

Each cached stage is a function of exactly one argument: its input record. The record holds
the **values** the stage reads, and nothing else is read. Each value that is an upstream
output is available as a value, either because the upstream stage ran in this run or
because its cached payload was decoded by the codec (3.7).

**N7.** `DecodeInput { byte[] TextSegment; uint TextStart; uint TextEnd; uint EntryPoint; }`

- The sweep is the full sweep from `EntryPoint` to `TextEnd` (5.1). `count` is not a field.
- `TextStart` is a field because `PsxExe.GetInstructionWord` reads `Header.TextStart`
  (`PsxExe.cs:23`). The stage body does not call `PsxExe` methods. It reads the record
  through a pure helper with the same arithmetic.
- `TextSegment` is the whole text segment. The sweep reads a subrange, so the record is a
  superset. That over-invalidates, and it is accurate.

**N8.** `BlockInput { uint DecodeStart; IReadOnlyList<DecodedInstruction> SlicedDecodedList; }`

- `DecodeStart` is `EntryPoint` (`BasicBlockBuilder.cs:34`).
- `SlicedDecodedList` is the output of N7 after the prefix slice (5.1), held as values.
  BasicBlockBuilder reads each instruction's `RawWord` (`BasicBlockBuilder.cs:27-31`), so
  the list must carry the raw words, not a digest of them.
- `instructionCount` is not a field, because `BasicBlockBuilder.cs:20` never reads it.

**N9.** `FunctionInput { uint EntryPoint; uint TextStart; uint TextSize;
IReadOnlyList<uint>? ExplicitEntries; IReadOnlyList<DecodedInstruction> SlicedDecodedList;
IReadOnlyList<BasicBlock> Blocks; IReadOnlyList<CfgEdge> Edges; }`

- `EntryPoint` is read at `FunctionDiscovery.cs:105` (`var seeds = new SortedSet<uint> { entryPoint };`).
- `TextStart` and `TextSize` are read at `FunctionDiscovery.cs:173`
  (`TextRegion = new ExecutableTextRegion(textStart, textSize)`).
- `SlicedDecodedList` is read for `RawWord` at `FunctionDiscovery.cs:95-96`, so the values are
  required.
- `ExplicitEntries` is null or a list; the codec distinguishes the two.

**N10.** `BiosInput { IReadOnlyList<DecodedInstruction> SlicedDecodedList;
IReadOnlyList<BasicBlock> Blocks; FunctionDiscoveryArtifact Functions; }`

**N11.** `ReportInput`, all as values:

- Identity: `DiscImageSha256` (`RomAnalysisPipeline.cs:413`), `SystemCnfBootPath` (`:414`,
  the raw boot path), `ExecutableFileName` (`:415`), `ExecutableFileSize` (`:421`),
  `ExecutableFileHash` (`:422`).
- Header: `EntryPoint`, `TextStart`, `TextSize`, `SpInitial`, `GpInitial` (`:416-420`).
- Decoded data: `SlicedDecodedList`, the decode failures, `Blocks`, `Edges`, `Functions`, and
  the BIOS evidence (`:432`).

The identity and header fields are in the N11 record because the report embeds them. An
entry keyed on the decoded data alone would be reused for a different disc with the same
boot executable (C1 of r7).

**N12a.** `CoverageInput { DiscImageAnalysisReport Report; ArtifactFixtureIdentity Fixture; }`

- `Report` is the N11 output as a value. `Fixture` is the identity value of 2.3, including
  `FixtureId`. **Nothing else.** `validations` is not in this record (MAJOR-1 of r7).

**Static check (M2, MAJOR-1 of r7).** For each stage:

- The stage signature has exactly one parameter, the record.
- The body and the reached Core code read no parameter, field, or static value outside the
  record and the code permitted by 3.4.
- **Split check (N12a).** The classification's reached closure does not reference
  `BuildDifferential`, the `validations` parameter, or the `Differential` member.
- Verified by the U2 analysis (section 9). Until U2 passes, the check has not run.

### 3.6 Reads at run time

Each record is wrapped by a recording accessor during a run. The recorder reports, for each
field, how many times it was read. The recorder is an input to the review of a record. It
is **not** a basis for removing a field (4.1, V6).

### 3.7 Canonical encoding and payload codec

- **Key encoding.** A dedicated encoder produces the key text. `ArtifactJson` is not used:
  `ArtifactJson.CanonicalOptions` (`ArtifactJson.cs:34-38`) keeps declaration order
  (`ArtifactJson.cs:23`).
  - UTF-8, no byte-order mark, LF only, one line, no trailing newline.
  - Objects: `{`, members separated by `,`, `}`. Each member is `"name":value`, with no
    space after the colon. The name is a JSON string.
  - Member order: UTF-8 byte order of the member name, shorter prefix first.
  - Arrays: `[`, elements separated by `,`, `]`, in the given order.
  - Strings: `"`-delimited. `"` is `\"`, `\` is `\\`. Control characters U+0000 to U+001F
    are `\u00xx` with lowercase hex, including those with short forms. Other characters are
    written as their UTF-8 bytes.
  - Integers: base 10, no leading `+` or zeros; `-` only when negative.
  - `true`, `false`, `null`. No floats (an encoder given one fails).
- **Payload codec.** Every stage output, and every record, is encoded by an explicit codec
  written per type. The codec is injective (T-CODEC), it does not reflect (3.4 item 1), and
  its code is inside Core, so it is inside `codeIdentity`. A cached payload is the codec
  encoding of the stage output. A cache hit decodes the payload back into the value. A decode
  failure is R6 (6.3).

### 3.8 Dependency identity (build and runtime inputs outside Core source)

`dependencyIdentity` covers the build and runtime inputs that can change a cached stage's
result and that are not Core source. Each item names its source.

- **Package identities.**
  - `loach.PureSharp` 0.1.6, `PrivateAssets="all"` (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:24`).
  - `SharpCompress` 0.50.4 (`PSXRecomp.Core.csproj:25`), used by `ChdCdCodec.cs:3`.
  - `loach.ArchitectureAnalyzer` 0.1.0, `PrivateAssets="all"`, a build-time analyzer whose
    `AdditionalFiles` input is `src/architecture.contract.json`, in the
    `EnableArchitectureAnalyzer` item group (`Directory.Build.props:44-47`, package at `:45`).
  - The resolved versions are read from `obj/project.assets.json`. This is an explicit exception:
    it is the only `obj/` file read, and it is read by exact name. The csproj pins direct packages
    only, and there is no lock file (CONFIRMED by listing).
- **SDK version and runtime version.**
  - At build time, a target reads the MSBuild property `$(NETCoreSdkVersion)` (its name is
    UNVERIFIED, U5) and writes it into a generated source file in `obj/`. The generated file
    is compiled into Core, so its value is inside the MVID of 3.3.
  - At run time, the runtime version is read from `RuntimeInformation.FrameworkDescription`.
    This is an explicit exception to item 9 of 3.4: it is an identity string read as a
    dependency identity, not an environment value that affects a result.
- **Target framework.** `TargetFramework` is `net10.0` (`PSXRecomp.Core.csproj:3`).
- **Architecture attribute source (LOW-1).** `src/Architecture/PSXRecompArchitectureAttributes.cs`
  is **not** a separate assembly. It is a source file that `Directory.Build.props:22-24` links into
  every project whose `CompileArchitectureAttributes` is `true`, and `PSXRecomp.Core.csproj:7` sets
  it. Its `internal` attribute types are compiled into Core, so the MVID of 3.3 covers them.
- **Failure rule (M8 of r8).** If any of these cannot be read, the stage is uncacheable and
  reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` (6.4, F5). No constant is substituted.

### 3.9 Decision: line endings

**DEC-LF1.** Digests of text use LF-normalized content: CRLF becomes LF, a lone CR is kept, and a
BOM is kept. A checkout with different line-ending settings must not change a digest for the same
source. The git warning "LF will be replaced by CRLF" for the design file was observed during
commit `6d038a0` (OBSERVED). The MVID of 3.3 does not depend on the checkout's line endings,
because it is computed from the compiled output.

## 4. Invalidation

### 4.1 Rules

- **V1. Changed input value.** A changed record value changes the record digest, so the key
  changes, and the entry is not reused.
- **V2. Changed code.** A changed `codeIdentity` changes every key.
- **V3. Changed dependency.** A changed package, SDK, or runtime version changes every key.
- **V4. Untracked value.** A value read but not in the record is a defect (3.5 static check).
  Until fixed, the stage is uncacheable.
- **V5. Unknown dependency.** An implicit input, or a call that breaks Rule NC, makes the stage
  uncacheable.
- **V6. Field removal.** A record field is removed only on **static evidence** from U2: the
  reachability analysis shows that no reached code reads it. A field with zero reads in a corpus
  run is **not** evidence that the field is unused. A zero-read field is a flag for review. The
  removal is confirmed by T-DEP-DEAD (7.3), on a fixture set separate from the one used by T-READ.

### 4.2 Stage consequences

- **N7.** Any Core change changes `codeIdentity` (DEC-CODE). A change to the text segment changes
  `TextSegment`. `count` is not a key component, and the slice of 5.1 handles it.
- **N8.** Changes to the N7 output after the slice, to `DecodeStart`, or to Core.
- **N9 to N11.** Changes to the upstream values, the record fields, or Core.
- **N10.** `BiosJumpTables` and `BiosCallFamily` are Core symbols, so they are inside `codeIdentity`.
- **N11.** The identity and header fields of 3.5. A different disc hash or boot path changes the N11
  key even when the executable is identical.
- **N12a.** Changes to the report, the fixture identity, or Core. It does not change with
  `validations`, because `validations` is not in the record.
- **N12d.** Never cached. Built at N14 from the current run's validations.

### 4.3 Identity verification (V-ID)

- **Rule V-ID.** Before any stage runs, the pipeline computes SHA-256 over the stream or array it
  runs and compares it with the supplied value. A mismatch fails the Input stage with a dedicated
  failure kind and no analysis runs. The stream path hashes from position 0 to end and then seeks
  back to 0. The stream contract requires seekability (`RomAnalysisPipeline.cs:70-72`).
- **Status.** V-ID is **not implemented**. Today the pipeline checks only that the supplied hash is
  non-empty (`RomAnalysisPipeline.cs:93-98`, `:194-198`), and the CLI hashes the file in a separate
  pass (`CliInput.cs:106-109`, then `:115` on a second open). The test driver hashes separately
  (`RealRomAnalyzer.cs:102-104`). Nothing checks that the supplied value matches the bytes that run.
- **Gate (MAJOR-C of r8).** V-ID is a gate for Normal mode, together with U2 (7.1). Its
  implementation and the passing of T-IDENT are required before Normal mode is enabled for any
  stage. V-ID also gates writes, because an entry keyed on an unverified identity would be
  unsound.
- **Migration.** `src/PSXRecomp.Tests/DiscImage/DiscImageHostileSizeTests.cs:24` defines `Sha` as a
  literal that is not the hash of the bytes passed at `:347`. About 50 call sites exist in the test
  projects (9 in that file, 2 in `SyntheticChdReaderTests.cs`, 2 in `RealRomAnalyzer.cs`, 36 in
  `RomAnalysisPipelineTests.cs`, 1 in `RealRomProductionExecutionTests.cs`). How many pass a
  constant is UNVERIFIED (U7).

## 5. Partial re-analysis: the decode prefix

### 5.1 Where the prefix is sliced

- **CONFIRMED** Today the count is applied inside the decode loop: `DecodeInstructions(exe, count)`
  (`RomAnalysisPipeline.cs:501-502`, call at `:355`), with the loop bound `decoded < count` (`:511`).
  BasicBlockBuilder receives that count-limited list (`:381`).
- **Decision DEC-N7.** N7 computes the full sweep from `EntryPoint` to `TextEnd`, with no count. The
  count-limited list that N8 sees is produced by a slice **before N8**: the first `min(count, total)`
  items of N7's output. The slice's output is N8's record value (3.5). The slice reproduces today's
  list exactly (T-PREFIX).
- **Cost.** A cold N7 decodes the whole text segment rather than the first `count` words
  (UNVERIFIED, U4).
- **Failure rule.** A decode failure at address `A` is included in a count-limited result iff
  `A < entry + 4*M`, where `M` is the requested count.

### 5.2 Text shorter than the declared size

- **CONFIRMED** `RomAnalysisPipeline.cs:340-347` rejects only a zero-length `TextSegment`.
- **CONFIRMED** `PsxExe.GetInstructionWord` throws `ArgumentOutOfRangeException` when
  `offset < 0 || offset + 4 > TextSegment.Length` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:24-27`).
  The negative case covers an address below `Header.TextStart` (LOW-2).
- **CONFIRMED** The decode loop catches it (`RomAnalysisPipeline.cs:531`), records a `DecodeFailure`,
  and stops (`:533-538`). No exception escapes the stage.
- **Test T-SHORT** pins this.

## 6. Entries, reads, writes, and failure

### 6.1 Entry layout (proposal)

Each entry is a directory named by the key's SHA-256. It holds:

- `header`: `keyFormat`, the key text and its SHA-256, `payloadSha256`, `payloadSizeBytes`,
  `codeIdentity`, `dependencyIdentity`.
- `payload`: the stage output, encoded by the codec (3.7).
- `COMPLETE`: written last (proposal; no on-disk marker exists today). The driver records
  `Manifest` and `Complete` as stage results (`RealRomAnalyzer.cs:261-262`), and
  `RomAnalysisStage.cs:10` says the pipeline does not produce them.

Each key has its own directory, so a failure on one key cannot affect another.

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the key's directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, after W3 succeeds for every file.
- **W5.** Never write an entry for a stage that did not pass, or for a stage that is uncacheable
  (6.5).
- **W6.** If the target already exists, it is accepted only when its header key, its
  `payloadSha256`, and its `COMPLETE` marker all match this write. Otherwise the existing target is
  treated as corrupt (6.4, F1) and this write is retried once.

### 6.3 Read protocol

An entry is used only if all of the following hold:

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses, and its `keyFormat` is supported.
- **R3.** `header.key` equals the key computed from the current record and code identity.
- **R4.** The payload size equals `payloadSizeBytes`.
- **R5.** SHA-256 of the payload equals `payloadSha256`.
- **R6.** The payload decodes under the current codec.
- **R7.** Any exception during R1 to R6 is caught and treated as a corrupt entry.

R1 to R7 prove only that an entry is internally consistent. R3 ties the entry to the current
inputs, and it is correct only because the key is complete (sections 3 and 9). A consistent
entry whose key matches but whose payload differs from what the stage now computes would be used
in Normal mode. That is the stale-entry case of section 7.

### 6.4 Failure: fail safe

- **F1. Corrupt.** An entry failing R1 to R7, or failing W6, is quarantined by renaming it to a name
  that records the failing check. The stage is recomputed. The run reports
  `ANALYSIS_CACHE_QUARANTINED` as a warning.
- **F1a. Terminal rule for a failed quarantine.** If the rename itself fails, the entry is
  **poisoned** for the run: the run does not read it again, recomputes the stage in memory, and
  writes nothing to that key's directory. The run reports `ANALYSIS_CACHE_QUARANTINED` with
  `quarantineFailed = true`. Other keys are in separate directories and are not affected. The entry
  is not deleted automatically.
- **F2. Missing.** A miss. The stage is recomputed. No diagnostic is reported.
- **F3. Uncacheable.** The stage is recomputed on every run, and the run reports
  `ANALYSIS_CACHE_UNCACHEABLE` once per stage.
- **F4. Partial failure.** If a stage fails, no entry is written for it or for any stage below it
  (W5).
- **F5. Key input unavailable.** If any part of the key cannot be read (the package file,
  `Directory.Build.props`, the generated SDK file, the runtime version, or the MVID of 3.3), the
  stage is uncacheable and the run reports `ANALYSIS_CACHE_KEY_UNAVAILABLE`. No constant is
  substituted.
- **Codes.** Four codes are proposed: `ANALYSIS_CACHE_QUARANTINED`, `ANALYSIS_CACHE_UNCACHEABLE`,
  `ANALYSIS_CACHE_KEY_UNAVAILABLE`, and `ANALYSIS_CACHE_VERIFY_MISMATCH`. None is registered today.
  Under `docs/adr/019-diagnostics-and-recovery-contract.md` a code is a validated `SCREAMING_SNAKE`
  identity. The existing analysis codes (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`)
  do not cover cache faults.

### 6.5 Uncacheable stages in every mode (MAJOR-B of r8)

- **Default status.** Until U2 passes for a stage, that stage is **uncacheable**. This is the
  default for every stage, not a candidate state.
- **Consequence.** An uncacheable stage writes **no entry in any mode**, including Verify mode.
  Verify mode for an uncacheable stage recomputes and compares with nothing, because nothing was
  written. This closes the path in which an entry written in Verify mode is later read after
  Normal mode is enabled.
- **Entry count.** Until U2 passes, the number of entries written for every cached stage is 0. This
  is the check of T-COLD-WARM.

## 7. Modes, seams, and tests

### 7.1 Modes and gates

- **Default mode is Verify.** In Verify mode, a cacheable stage is recomputed cold. If an entry
  exists, its payload is compared with the cold payload byte for byte. A difference is
  `ANALYSIS_CACHE_VERIFY_MISMATCH` and a hard failure. The cold result is returned, never the entry.
- **Normal mode** returns a valid entry as a hit. It is enabled for a stage only when **all** of the
  following hold: U2 passes for that stage, V-ID is implemented (4.3), and T-IDENT passes.
- Until then, Normal mode is unavailable for every cached stage.
- The default is set in `AnalysisCacheOptions` and is not optional.

Injection seams (all Core types):

- `IAnalysisCacheStore`: the store port (8.1). Tests pass an in-memory fake.
- `IStageCodeIdentity`: returns `codeIdentity`, `dependencyIdentity`, and the stage's
  uncacheable verdict. Tests pass a fake.
- `IStageInputRecorder`: wraps an input record and counts reads per field (3.6).
- `AnalysisCacheOptions`: `Mode` (default `Verify`), the store, the code identity provider, the
  recorder, and a diagnostics sink.

### 7.2 Test scope

Several tests run the same code twice, or substitute a digest in a fake. They check the cache
mechanics on the current code. They **cannot** detect a missed input, because the cold and warm
runs share the same code and the same records. Two kinds of test are not of that kind:

- **T-GOLDEN** compares cold payloads with committed payloads from an earlier revision, so it
  crosses a change of code.
- **T-CLOSURE-RT** compares the symbols an instrumented run executes with the closure that U2
  analyses. It checks the analysis against the executed code on the current revision. It does not
  compare two revisions.

Each row of 7.3 states its scope.

### 7.3 Tests

Synthetic inputs come from `src/PSXRecomp.Tests/RealRomAnalysis/`: `SyntheticDiscBuilder.cs`,
`SyntheticChdBuilder.cs`, and `SyntheticIsoBothEndianTests.cs` (they exist; their APIs are UNVERIFIED,
U8). No BIOS, ROM, or CHD content is used. Clocks are injected. Temp paths are not asserted.

Two fixture sets are used, and they must not overlap:

- **Fixture set A** drives T-READ and T-COLD-WARM.
- **Fixture set B** drives T-DEP-DEAD and T-DEP-INPUT. Set B uses other synthetic configurations
  (different counts, text layouts, and disc identities), so a field read only in set-B configurations
  is not missed.

| ID | Test | Asserts | Scope |
|---|---|---|---|
| T-COLD-WARM (MAJOR-B) | Cold and warm equality, and entry count | For each set-A fixture: run cold, store, run warm. **While a stage is uncacheable (the default), the test asserts that the number of entries written for it is 0 and the number of hits is 0**, and that the warm output equals the cold output byte for byte. Once U2 and V-ID pass for a stage, the test also asserts hits equal the number of cacheable units, and the warm payload equals the cold payload per stage. | Current code only (7.2) |
| T-DEP-INPUT | Changed record value is not reused | For each stage and each record field, on set-B fixtures, change that one value. The key differs, the entry is not reused, and the recomputed payload is stored under the new key. | Current code only |
| T-DEP-CODE | Changed code identity is not reused | Substitute a changed `codeIdentity` in `IStageCodeIdentity`. The entry is not reused. | Current code only; a fake digest, not a real change |
| T-DEP-DEAD (MAJOR-2 of r8, LOW-3) | Field removal is checked by value and by static verdict | For each field whose set-A reads are zero, and only for those: (a) the U2 static verdict must say "unread"; (b) on set-B fixtures, take a baseline run with the **unchanged** record, whose key is **K0**, and read its stored payload **P0** from the entry under K0. Then compute the changed record's key **K1** and the cold result **C1** with the changed value. The field is accepted only if **C1 equals P0** byte for byte. A field that fails (a) or (b) stays in the record. A key comparison alone is not accepted. | Set B and U2 static verdict |
| T-IDENT2 (C1 of r7) | Same executable, different disc identity | Two fixtures with the same boot executable bytes and different disc hashes (and, in a second case, a different boot path). The N11 and N12a keys differ, the second run does not reuse the first run's entry, and each payload contains its own disc hash and boot path. | Current code only |
| T-DIFF (MAJOR-1 of r7) | N12a is independent of validations | For a fixed `(report, fixture)`, vary `validations` (none, one run, two runs, different order). The N12a payload is byte-identical in every case. The document from N14 differs only in the `Differential` section. | Current code only |
| T-UNCACHE | Uncacheable stage writes nothing | For the default status, and with an injected uncacheable verdict, each stage recomputes every run, writes no entry in Verify or Normal mode, and reports `ANALYSIS_CACHE_UNCACHEABLE` once. | Current code only |
| T-KEY-UNAVAIL (M8 of r8, MINOR-7 of r7) | Unreadable key input | Make the package file, `Directory.Build.props`, the generated SDK file, the runtime version, or the MVID unreadable in a fake, one at a time. The stage is uncacheable and reports `ANALYSIS_CACHE_KEY_UNAVAILABLE`. No constant key is produced. | Current code only |
| T-READ | Record matches reads | Run each stage under the recorder on set-A fixtures. Every record field's read count is recorded. A zero-read field is listed as a flag for review (V6). It is not removed by this test. | Current code only (runtime) |
| T-STATIC (M2 of r8, MAJOR-1 of r7) | Static check of the record and the split | Each stage signature has one parameter. Its reached body reads nothing outside the record. The N12a closure does not reach `BuildDifferential`, `validations`, or `Differential`. Implemented by U2; until U2 passes, not run. | Static, U2 |
| T-CLOSURE-RT | Executed symbols against the analysed closure | Run each candidate stage under an instrumented build that records executed methods and the environment, time, file, culture, and random APIs. Every executed Core method must be in the analysed closure, and no implicit-input API may execute. | Current revision, exercised paths only; instrumentation UNVERIFIED (U2) |
| T-CORRUPT | Corrupt entries fail safe | For each corruption (zero-length file, truncated payload, flipped byte, wrong key in header, unsupported `keyFormat`, malformed header, schema mismatch), the run recomputes, the output equals the cold output byte for byte, and `ANALYSIS_CACHE_QUARANTINED` is reported once per corrupt entry. | Current code only; applies once a stage is cacheable |
| T-QUAR-FAIL (m6 of r8) | Failed quarantine is terminal | Make the rename fail for a corrupt entry. The run recomputes, reports `quarantineFailed = true`, writes nothing to that key, and writes successfully for a different key in the same run. | Current code only; applies once a stage is cacheable |
| T-W6 (m5 of r8) | Existing target | A write whose target exists with a matching `payloadSha256` but a different key or a missing `COMPLETE` is treated as corrupt, not accepted. | Current code only; applies once a stage is cacheable |
| T-MISSING | Missing entries are silent | Delete an entry. The run recomputes, the output equals the cold output, and no diagnostic is reported. | Current code only; applies once a stage is cacheable |
| T-PARTIAL | Partial failure writes nothing below | A failure injected at N8 writes no entry for N8 or later. The N7 entry is the only one written. | Current code only; applies once a stage is cacheable |
| T-STALE | Stale-but-consistent entry (residual risk) | An entry whose header and `COMPLETE` marker match the current key, with a payload that differs from the cold result. In **Verify** mode (default) the run reports `ANALYSIS_CACHE_VERIFY_MISMATCH` and returns the cold result. In **Normal** mode (only after the gate of 7.1) the entry is returned. | Current code only |
| T-SHORT (5.2) | Short text segment | An executable whose text segment is shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment, and no exception escapes. | Current code only |
| T-PREFIX (5.1) | Prefix slice | N7 output sliced to `M` before N8 equals today's count-limited decode at `M`, including failures filtered by `A < entry + 4*M`. | Current code only |
| T-DIFF-RUN | Differential on and off, end to end | Cold and warm runs with validations supplied and without, both byte for byte. N12d is never read from the cache. | Current code only |
| T-IDENT (4.3) | Identity verification | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind and no cache write. The CLI, the GUI, and the test driver, given the same bytes, compute the same verified hash. **Required for the Normal-mode gate (7.1).** The GUI part is UNVERIFIED (U6). | Current code only, after V-ID is implemented |
| T-CODEC | Key encoder and payload codec | The key text of a fixed sample equals the committed text, and its SHA-256 equals the committed value. Member order is UTF-8 byte order. Floats are rejected. The codec round-trips each record and stage type, and is injective on the fixtures. | Current code only |
| T-LF | Verbatim string lint (DEC-LF1) | Fails if a file on a dependency input path contains a multi-line verbatim string literal. | Static |
| T-GOLDEN | Cross-revision regression | Committed golden payloads, produced from synthetic fixtures at an earlier revision, are compared with a cold recompute. Each cacheable stage must have at least 4 compared entries (one per fixture), so the check cannot pass with zero comparisons. An intentional payload change requires regenerating the goldens with the named command `regenerate-analysis-goldens`, and the golden diff is reviewed. Location: `src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGolden/`, one `<unit>/<fixtureId>.golden.json` per pair (proposal). | **Crosses revisions** |

Determinism rules for every test: no wall-clock values in keys, headers, or assertions; no dependence
on the order of unsorted collections; no reliance on the temp directory name.

## 8. Architecture, store, and AI results

### 8.1 Architecture placement

The architecture matrix (`docs/architecture-matrix.md`) assigns:

- **CONFIRMED** Domain (`PSXRecomp.Core`) owns ports and deterministic contracts
  (`architecture-matrix.md:22`, rule 1 at `:64`) and must not reference `PSXRecomp.Infrastructure`.
- **CONFIRMED** Infrastructure owns host adapters, including `File` and `Directory` access
  (`architecture-matrix.md:24`, `:55`, rule 2 at `:65`).
- **CONFIRMED** Application and CLI do not call `File.*` or `Directory.*` for ordinary logic
  (rule 3 at `:66`). Concrete adapter wiring is allowed at one composition-root site with an
  AARC002 suppression (`:47`).

Resolution:

- Core: `IAnalysisCacheStore`, the key and entry types, the key encoder and payload codec (3.7), the
  input records, `IStageCodeIdentity`, `IStageInputRecorder`, `AnalysisCacheOptions`, and the
  validation of R1 to R7 as checks over bytes already read.
- Infrastructure: `FileAnalysisCacheStore : IAnalysisCacheStore`, marked `[Infrastructure]`, with the
  file operations of 6.2 and 6.4.
- Precedent: `src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs` implements
  `IProjectMetadataStore`, defined in `PSXRecomp.Core.Project` (Issue #42).
- **ADR required** before implementation: the port and adapter, the move of persistence out of the
  test project (U3), and the reading of the MVID. This document does not create one.

### 8.2 Stores and sharing

- The CLI and GUI pass a store root and the same key function. The store root's location is
  UNVERIFIED (U6).
- Cross-process concurrency uses 6.2.

### 8.3 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists.
- **CONFIRMED** `EvidenceType.AIPrediction = 6` exists at
  `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`, next to `AnalyzerFinding = 5` (`:16`).
- **Rules.** An AI result is advisory and is never stored as an analysis unit or in an analysis
  document. It is reused only when its key matches the digests of the artifacts it was computed
  from, the prompt or template revision, the model identifier, and the model settings. Whether it
  uses `EvidenceType.AIPrediction` is decided in its own ADR.

## 9. U2 as a standalone task (MEDIUM-D of r8)

U2 is the analysis that makes any stage cacheable. It is specified here as a task with an input,
an output, and acceptance criteria. It is not a design claim, and it has **not been run**.

**Input.**

- The `PSXRecomp.Core` assembly built from the commit under review, with its MVID.
- The root list, the Core methods that each cached stage enters:
  - N7: the decode sweep (`RomAnalysisPipeline.cs:501-543`, the sweep without count).
  - N8: `BasicBlockBuilder.Build` (`BasicBlockBuilder.cs:20`).
  - N9: `FunctionDiscovery.Build` (`FunctionDiscovery.cs:81-88`).
  - N10: `BiosCallRecognizer.Recognize` (`BiosCallRecognition.cs:140`).
  - N11: the report assembly (`RomAnalysisPipeline.cs:400-444`).
  - N12a: the classification function split out of `Analyze` (`RealRomCoverageAnalyzer.cs:88`).
- The static reference graph of Core, with Roslyn's semantic model.

**Output.**

- For each root: `cacheable` or `uncacheable`, with evidence for each uncacheable verdict (the
  reached symbol and its rule in 3.4).
- For each record field of each stage: a static verdict, `read` or `unread`, with the reading site.
- The implicit-input scan of 3.4: each reached site that reads culture, environment, time, file
  system, randomness, or reflection, with its file and line.
- The runtime closure report from T-CLOSURE-RT: the executed symbols, and any that the static
  analysis did not reach.

**Acceptance criteria.**

1. Guards 2, 9, 10, and 12 of 3.4 are searched for in the reached code, and each search and its
   result is recorded. Each generic guard is either found to have no occurrence in the reached code, or
   becomes a rule for that stage.
2. Each reached culture-sensitive site is checked. In particular `RomAnalysisPipeline.cs:286` and
   `:333` are classified.
3. The reachability of `SharpCompress` (`ChdCdCodec.cs:3`) and of `loach.PureSharp`
   (`PSXRecomp.Core.csproj:24`) is determined, and recorded for each stage.
4. T-STATIC passes for every stage that the output marks cacheable.
5. T-CLOSURE-RT passes for every stage that the output marks cacheable.
6. The output is produced for the current commit and recorded with its MVID.

Until criteria 4 and 5 pass for a stage, that stage stays uncacheable (6.5).

## 10. Open items (UNVERIFIED unless stated)

- **U1.** Extract each stage into a function that takes one record, and split `Analyze` (1.2, 2.2).
  Behavior-preserving, UNVERIFIED.
- **U2.** The standalone task of section 9. BLOCKER for every stage.
- **U3.** Enumerate the project fields that reach analysis (1.3).
- **U4.** Measure cold N7 over the full text segment (5.1).
- **U5.** Confirm `$(NETCoreSdkVersion)` in this repository, the generated-file mechanism of 3.8, and
  the build determinism that the MVID of 3.3 relies on.
- **U6.** Read the caller of `RunFromChd(byte[])` (`RomAnalysisPipeline.cs:42-62`) and of
  `AnalyzeAndExecuteFromDisc` (`RealRomTitleExecutionService.cs:110-117`), and decide the store root.
- **U7.** Count the test call sites that pass a constant SHA (4.3, Migration).
- **U8.** Read `PathRedactor.cs` and the synthetic builder APIs before the tests are implemented.
- **U9. Closed.** `ICdSectorSource.cs:11` (`CdDiscToc`) and `:56` (`ICdSectorSource`).
- **U10.** Confirm which declared stage outputs embed `ArtifactJson` content
  (`RealRomCoverageDocument.cs:245`, `RecompilerContract.cs:1126`).
- **V-ID.** Implement the identity verification of 4.3. A gate for Normal mode, with U2 and T-IDENT.

## 11. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact schemas.
- No cache implementation. This is the design only.
- No stage is declared cacheable in this document. Every stage is uncacheable until U2 and V-ID pass
  for it.
- No AI result producer.
- No change to the project file format.
- No ADR is created here.

## 12. Finding traceability

### 12.1 Review of `cdd1634` (r8), final review

| ID | Finding | Resolved in |
|---|---|---|
| MAJOR-A | Input records hold digests only, but BasicBlockBuilder and FunctionDiscovery read `RawWord` | 3.2 (digests only for the key; a digest is never a computation input), 3.5 (records hold values: N8 `SlicedDecodedList`, N9 `SlicedDecodedList`), 2.4 (hits decode the payload back into values), R-0 (1.2) |
| MAJOR-B | Stages gated by U2 are not uncacheable, and Verify mode may write entries | 6.5 (uncacheable in every mode, no writes), 2.2 status column, 7.1, T-COLD-WARM (entry count 0 until the gate) |
| MAJOR-C | V-ID is not a gate; the implementation is missing | 4.3 (status: not implemented; a gate for Normal mode with U2 and T-IDENT; also gates writes), 7.1, T-IDENT, 10 (V-ID item) |
| MEDIUM-D | U2 as an independent task | 9 (input, output, acceptance criteria 1 to 6) |
| MEDIUM-E | Entry points and citations | 2.1 (entry-point table, ISO path, `RunFromChd(byte[])`), 2.2 (stage table with N2 skipped on ISO), 2.3 (fixture identity: `RealRomAnalyzer.cs:162-171`, `DeterministicArtifactBuilder.cs:159-178`, call at `:179`, `Build` at `:181`) |
| LOW-1 | `PSXRecomp.Architecture` is a source file, not an assembly | 3.8 (architecture attribute source) and 3.4 item 13 |
| LOW-2 | `GetInstructionWord` negative offset | 5.2 (`PsxExe.cs:24-27`) |
| LOW-3 | T-DEP-DEAD (b) and the source of the cached payload | 7.3 T-DEP-DEAD (baseline run under K0, stored payload P0, changed key K1, cold result C1, check C1 = P0) |

### 12.2 Review of `29e1488` (r7) and earlier

Carried forward from r8. The r8 traceability table is superseded by this table. The r8 items
(MAJOR-1 to MAJOR-3, MINOR-1 to MINOR-9 of the review of `29e1488`) are resolved as follows, and each
is still in force:

- MAJOR-1 (N12a and validations): 2.2 N12a row, 3.5 N12a record, T-DIFF.
- MAJOR-2 (zero-read fields): 4.1 V6, T-READ (flag only), T-DEP-DEAD (set B and static verdict).
- MAJOR-3 (non-Core scope): 3.3 (non-Core paragraph and Rule NC), 3.4 item 13, 3.8.
- MINOR-1 to MINOR-9: see r8 (`cdd1634`) for the text; the corresponding passages are in 2.4, 3.4 items
  1, 2, 8, 9, 10, 12, 3.5 (N9), 3.8, 3.4 item 13, 7.2.

The blocker is **U2** (section 9). It is the only item that gates a stage, and it is specified as an
independent task with acceptance criteria. Until U2 and V-ID pass for a stage, that stage is
uncacheable in every mode, and this document claims no stage is safe.
