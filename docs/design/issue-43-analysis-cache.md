# Issue #43: Incremental Analysis and Cache (design, r8)

- Status: **Candidate design. No stage is cacheable yet.** Every cached stage is
  gated by U2 (the reachability and implicit-input analysis, section 9). Until U2
  passes, every cached stage is uncacheable and is recomputed on every run. The hit
  path is therefore not exercised by any test until U2 passes.
- Issue: #43 "[Analysis] Incremental Analysis & Cache" (related: #42, #39)
- Base: `origin/main` at `2e3ecad`
- Revision: r8. Addresses the review of r7 (`29e1488`): 3 major and 9 minor findings.
  Section 12 maps each finding to its resolution.

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
explicit input record per stage, checked against the code (3.5), and (c) an explicit
list of implicit inputs that make a stage uncacheable (3.4).

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
- **CONFIRMED** CLI and GUI call the same pipeline entry points.
  - CLI: `src/PSXRecomp.Cli/CliInput.cs:100-116` (`LoadChdWithIdentity`), hash block
    `:106-109`, pipeline call `:115`.
  - GUI: `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`, `:117`.
    The caller of `discImageSha256` is not read (UNVERIFIED, U6).
- **CONFIRMED** Persistence is in the test-project driver:
  `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:164-171` (fixture identity
  record, `FixtureId = fixtureId` at `:164`), `:178-181` (coverage, then
  `DeterministicArtifactBuilder.Build`), and `RealRomArtifactWriter.cs:44-50`.

### 1.2 Stages are inlined

- **CONFIRMED** `RomAnalysisPipeline.RunFromIsoSectorReader` (starting at
  `RomAnalysisPipeline.cs:206`) runs every stage inline. The decode call is at `:355`,
  the report at `:400-444`.
- **Prerequisite R-0.** Each cached stage is extracted into a function that takes only
  its input record (3.5) and returns a serializable output. Coverage is split into
  classification (N12a) and differential assembly (N12d), see 2.1. The refactor must be
  behavior-preserving. Its test is the existing suite plus T-PREFIX and T-DIFF (U1).

### 1.3 YAML, schema, project

- **CONFIRMED** `config/cpu/r3000a-instructions.yaml:11` declares `meta.version: "1.0"`.
  **OBSERVED** no code parses it. Governing decisions are `docs/adr/001-cpu-specification-ssot.md`
  and `docs/adr/002-instruction-definition-yaml.md`. The YAML is out of every key until
  a reader exists (rule Y1).
- **CONFIRMED** Schema constants: `ManifestSchemaVersion = 2`, `ReportSchemaVersion = 2`,
  `InstructionsSchemaVersion = 1`, `CfgSchemaVersion = 1`, `CoverageSchemaVersion = 1`,
  `FunctionProvenanceSchemaVersion = 1` (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisArtifactSchema.cs:27-49`).
  `CoverageArtifactKind` at `:64`.
- **CONFIRMED** `ProjectFormatVersion = 1` (`src/PSXRecomp.Core/Project/ProjectSchema.cs:19`).
  Which project fields reach analysis is UNVERIFIED (U3).

## 2. The dependency graph and the input of each stage

### 2.1 Stage table

"Cached" is a candidate status. Every candidate is gated by U2 (section 9).

| Stage | Cached | Input record (3.5) | Evidence (reads, from the code) |
|---|---|---|---|
| N1 input identity | no | none (runs each run) | `RomAnalysisPipeline.cs:93-98`, `:194-198`; `RealRomAnalyzer.cs:100-106` |
| N2 CHD open | no | runs each run | `RomAnalysisPipeline.cs:115-117` |
| N3 ISO filesystem | no | runs each run | `RomAnalysisPipeline.cs:128-129`, `:217` |
| N4 boot executable | no | runs each run; executable SHA-256 at `:259` | `RomAnalysisPipeline.cs:259`, `:576-579` |
| N5 EXE header | no | runs each run | `RomAnalysisPipeline.cs:277-337`; `PsxExe.cs`, `PsxExeHeader.cs` |
| N6 text bounds | no | runs each run; non-empty check only | `RomAnalysisPipeline.cs:340-347` |
| N7 decode | candidate, gated by U2 | `DecodeInput` (3.5) | `RomAnalysisPipeline.cs:501-543`; `PsxExe.cs:23` (`Header.TextStart`); `R3000aDecoder.Decode` at `:516` |
| N8 basic blocks | candidate, gated by U2 | `BlockInput` (3.5) | `RomAnalysisPipeline.cs:381`; `BasicBlockBuilder.cs:34` (`decodeStart`), `:30` (re-decode) |
| N9 functions | candidate, gated by U2 | `FunctionInput` (3.5) | `RomAnalysisPipeline.cs:404`; `FunctionDiscovery.cs:105` (entry), `:173` (text bounds), re-decode `:96` |
| N10 BIOS calls | candidate, gated by U2 | `BiosInput` (3.5) | `RomAnalysisPipeline.cs:432`; `BiosCallRecognition.cs:140`, `:176`, `:195`, `:336`, `:584` |
| N11 report | candidate, gated by U2 | `ReportInput` (3.5): identity and header fields, plus digests | `RomAnalysisPipeline.cs:400-444`, identity at `:411-424`; call-candidate re-decode at `:553` |
| N12a classification | candidate, gated by U2 | `CoverageInput` (3.5): the N11 report digest and the fixture identity fields. **Nothing else** (MAJOR-1) | `RealRomCoverageAnalyzer.cs:88` (`Analyze`), classification at `:223`, `:234-261`; `LowerProgram` call at `:251`; definition at `src/PSXRecomp.Core/Recompiler/MipsToIrLowerer.cs:176` |
| N12d differential | no (never cached) | `validations`, supplied by the caller on that run | `RealRomCoverageAnalyzer.cs:85-88` (doc), assignment at `:185`, `BuildDifferential` at `:377-382` |
| N14 artifact documents | no | runs each run | `DeterministicArtifactBuilder.cs:74`, `:96-99`, `:115-121`, `:124`; `ArtifactJson.cs:32` |
| N15 persistence | no | runs each run | `RealRomArtifactWriter.cs:44-50` |

**N12a and N12d (MAJOR-1).** `Analyze(report, fixture, validations = null)`
(`RealRomCoverageAnalyzer.cs:88`) returns a document whose `Differential` is
`BuildDifferential(validations)` (`:185`). The classification is computed in the same
function. The split is:

- **N12a** is a function of `(report, fixture)` only. Its payload is the classification
  and no differential. Its record is `CoverageInput`, and its key is built from that
  record alone.
- **N12d** is a function of the N12a payload and `validations`. It is never cached, and
  it is assembled at N14.

The split is a prerequisite (R-0). Two checks enforce it:

- **Static split check.** The classification function's reached closure must not
  reference `BuildDifferential`, the `validations` parameter, or the `Differential`
  member. This is part of the U2 static check (3.5).
- **T-DIFF** (section 7) varies `validations` while holding `(report, fixture)` fixed
  and asserts that the N12a payload is byte-identical across the variations.

### 2.2 Data flow

- A stage's output is the only thing a later stage reads from it. N8 reads the decoded
  list (the sliced output of N7), and nothing else of N7.
- A stage's key includes the digests of the values it actually reads (3.5). A downstream
  entry is reused only when those digests are unchanged, and that is decided per key.
- N1 to N6, N12d, and N14 to N15 run every time. Their values are actual values of the run.

## 3. Cache key

### 3.1 Principle

An entry is reused in a run only if the stage would compute the same payload in that
run. The key therefore covers:

- **The code**: the whole Core assembly identity (3.3).
- **The values read**: an explicit input record, digested (3.5).
- **The implicit inputs**: anything read that is not in the record makes the stage
  uncacheable (3.4).
- **The build and runtime inputs outside Core source** (3.8).

### 3.2 Key definition

```
key(s) = SHA-256( canonical( keyFormat, s,
                             codeIdentity,
                             dependencyIdentity,
                             inputRecordDigest(s) ) )
```

- `codeIdentity`: the Core assembly identity (3.3).
- `dependencyIdentity`: the build and runtime inputs that can change a cached stage's
  result and that are not Core source: the package identities, the SDK and runtime
  versions, and the build-time inputs (3.8). Non-Core source is not a key component on
  its own. A cached stage reaches non-Core source only through its record (3.3).
- `inputRecordDigest(s)`: the digest of the stage's input record (3.5).

### 3.3 Code identity: the whole Core assembly

- **Decision DEC-CODE.** `codeIdentity` is the metadata version identifier (MVID) of
  the `PSXRecomp.Core` assembly, together with its full name. The build is
  deterministic, so the MVID is a content-derived identifier of the compiled output. It
  changes whenever the IL or the metadata of Core changes, including bindings, extension
  methods, implicit operators, partial declarations, static initializers, and global
  usings that affect Core's compilation. Determinism of the build is UNVERIFIED (U5).
- **Rule.** A change anywhere in Core changes `codeIdentity` and invalidates every
  cached stage. This is accepted: accuracy beats performance (owner ruling).
- **Why not a source-file closure digest.** A closure of source files cannot see a
  binding change that lies outside it (for example an extension method in another file,
  or a global using). The compiled assembly does see it. A closure is used only to scope
  the implicit-input scan (3.4), not as a code key.

**Non-Core code (MAJOR-3).** Code outside `PSXRecomp.Core` does not enter the code
identity. This is correct because of the following, all CONFIRMED at base:

- The driver supplies only record values to the cached stages. The fixture identity
  record is built at `src/PSXRecomp.Tests/RealRomAnalysis/RealRomAnalyzer.cs:164-171`,
  and N12a receives it through the record. The driver's `DeterministicArtifactBuilder.Build`
  call (`:179-181`) is in N14, which is not cached.
- N14 and N15 are not cached. The writer (`RealRomArtifactWriter.cs:44-50`) and the
  artifact assembly run each time.
- The CLI (`src/PSXRecomp.Cli/CliInput.cs:100-116`) and the GUI
  (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97-117`) call the
  pipeline's entry points and do not run analysis code of their own.

**Rule NC.** A cached stage's reached code may call non-Core code only through an
explicit record value or a declared input. A cached stage that calls a non-Core symbol
(for example a Test, Infrastructure, Application, or CLI symbol, or a non-allow-listed
package) is an implicit input and is uncacheable (3.4 items 4 and 5). The static
closure (U2) is the check for this rule.

### 3.4 Implicit inputs: uncacheable rules

A stage is **uncacheable** if its reachability (scoped by U2) contains any of the
following. An uncacheable stage is recomputed on every run, writes no entry, and reports
`ANALYSIS_CACHE_UNCACHEABLE` once. The rules are stated against what the code can read.

1. **Reflection.** `typeof` used for reflection, `GetType`, `Activator`, `MethodInfo`, or
   `System.Reflection` APIs. Also reflection-based serialization: the generic method
   `ArtifactJson.Serialize<T>` calls `JsonSerializer.Serialize(document, CanonicalOptions)`
   (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:54`), which reflects
   over `T` at run time. The cached payload codec must not reflect (3.7).
2. **Dynamic code.** `dynamic`, expression-tree compilation, or runtime code generation.
   *Generic guard; no current occurrence was searched for in this revision.*
3. **Native calls.** P/Invoke, `LibraryImport`, or any call through `NativeInterop.cs`
   (root of Core).
4. **Delegates from outside.** A delegate or function value not declared in the input
   record. Its target is unknown.
5. **Interface or virtual dispatch with implementations outside Core.** Class hierarchy
   analysis over Core gives the implementation set. If any implementation lies outside
   Core, the set is unknown. This covers calls from a cached stage into non-Core code
   (Rule NC).
6. **Generic instantiation with a type argument outside Core.** The instantiation's
   code is not in the Core assembly.
7. **Static initialization and static mutable state.** Static initializers are part of
   the Core identity. `BiosJumpTables` (`src/PSXRecomp.Core/Runtime/BiosJumpTables.cs:50`)
   is a static class, and `ArtifactJson.CanonicalOptions` (`ArtifactJson.cs:34`) is a
   static field. A static initializer or static field read by the stage is uncacheable
   if it reads any implicit input in items 8 to 12. A static field written after
   initialization is uncacheable unless the write is proven to occur only before the
   first stage run.
8. **Culture.** Any reached API whose result depends on `CultureInfo.CurrentCulture` or
   `CurrentUICulture`. The pipeline's formatting is not uniform: many sites use
   `string.Create(CultureInfo.InvariantCulture, ...)` (for example `RomAnalysisPipeline.cs:100-101`),
   while others interpolate without it (for example `:286` and `:333`). Each reached
   site must be checked (UNVERIFIED, U2).
9. **Environment.** `Environment.*` (variables, machine name, user, OS version, current
   directory). *Generic guard; not searched in this revision.*
10. **Time.** `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `Stopwatch` used
    in a value, and time zone lookups. *Generic guard; not searched in this revision.*
11. **File system.** `System.IO` reads or directory enumeration. A file read is an input
    and must be in the record as a digest, not read implicitly.
12. **Randomness.** `Random`, `RandomNumberGenerator`, `Guid.NewGuid`, and hash codes of
    reference types. *Generic guard; not searched in this revision.*
13. **Unresolved symbols or an assembly outside the allow-list.** The allow-list is the
    BCL, `PSXRecomp.Architecture`, and `PSXRecomp.Core` itself. Any other package is
    outside it, and a closure that reaches one is uncacheable. The packages named in
    3.8 are outside the list: `SharpCompress` 0.50.4
    (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:25`, used by `src/PSXRecomp.Core/DiscImage/ChdCdCodec.cs:3`)
    and `loach.PureSharp` 0.1.6 (`PSXRecomp.Core.csproj:24`, `PrivateAssets="all"`).
    A closure that reaches `SharpCompress` is uncacheable. Whether any stage reaches
    `loach.PureSharp` is UNVERIFIED (U2).

Items 8 to 12 are the implicit inputs of the BCL. The design does not assume the BCL is
fixed. Each item is checked explicitly.

### 3.5 Input records

Each cached stage is a function of exactly one argument: its input record. The record
holds every value the stage reads, and nothing else is read.

**N7.** `DecodeInput { byte[] TextSegment; uint TextStart; uint TextEnd; uint EntryPoint; }`

- The sweep is the full sweep from `EntryPoint` to `TextEnd` (5.1). `count` is not a
  field.
- `TextStart` is a field because `PsxExe.GetInstructionWord` reads `Header.TextStart`
  (`PsxExe.cs:23`). The stage body does not call `PsxExe` methods. It reads the record
  through a pure helper with the same arithmetic (M2).
- `TextSegment` is the whole text segment. The sweep reads a subrange, so the record is a
  superset. That over-invalidates, and it is accurate.

**N8.** `BlockInput { uint DecodeStart; Digest SlicedDecodedList; }`

- `DecodeStart` is `EntryPoint` (`BasicBlockBuilder.cs:34`).
- `SlicedDecodedList` is the output of N7 after the prefix slice (5.1), digested. The
  slice happens before N8 and is part of the input (M3). `instructionCount` is not a
  field, because `BasicBlockBuilder.cs:20` never reads it.

**N9.** `FunctionInput { uint EntryPoint; uint TextStart; uint TextSize;
Digest ExplicitEntries; Digest SlicedDecodedList; Digest Blocks; Digest Edges; }`

- `EntryPoint` is read at `FunctionDiscovery.cs:105` (`var seeds = new SortedSet<uint> { entryPoint };`).
- `TextStart` and `TextSize` are read at `FunctionDiscovery.cs:173`
  (`TextRegion = new ExecutableTextRegion(textStart, textSize)`).
- `ExplicitEntries` is absent or present; its digest is the absent marker when null.

**N10.** `BiosInput { Digest SlicedDecodedList; Digest Blocks; Digest Functions; }`

**N11.** `ReportInput`:

- Identity: `DiscImageSha256` (`RomAnalysisPipeline.cs:413`), `SystemCnfBootPath`
  (`:414`, the raw boot path), `ExecutableFileName` (`:415`), `ExecutableFileSize`
  (`:421`), `ExecutableFileHash` (`:422`).
- Header: `EntryPoint`, `TextStart`, `TextSize`, `SpInitial`, `GpInitial` (`:416-420`).
- Derived: `DecodeStartAddress` (`:423`, equal to `EntryPoint`), `DecodedInstructionCount`,
  and digests of the sliced decoded list, failures, blocks, edges, functions, and BIOS
  evidence.

The identity and header fields are in the N11 record because the report embeds them. An
entry keyed on the decoded data alone would be reused for a different disc with the same
boot executable.

**N12a.** `CoverageInput`: the N11 report digest, and the `ArtifactFixtureIdentity` fields,
including `FixtureId` (`RealRomAnalyzer.cs:164`), the disc image format, the disc image
size, and the executable identity. **Nothing else**: `validations` is not in this record
(MAJOR-1, 2.1).

**Static check (M2, MAJOR-1).** For each stage:

- The stage signature has exactly one parameter, the record.
- The body and the reached Core code read no parameter, field, or static value outside
  the record and the reached code permitted by 3.4.
- **Split check (N12a).** The classification's reached closure does not reference
  `BuildDifferential`, the `validations` parameter, or the `Differential` member.
- Verified by the reachability analysis of U2. Until U2 passes, the static check has not run.

### 3.6 Reads at run time

Each record is wrapped by a recording accessor during a run. The recorder reports, for
each field, how many times it was read. The recorder is an input to the review of a
record, and it is **not** a basis for removing a field (4.1, V6).

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
    are `\u00xx` with lowercase hex, including those with short forms. Other characters
    are written as their UTF-8 bytes.
  - Integers: base 10, no leading `+` or zeros; `-` only when negative.
  - `true`, `false`, `null`. No floats (an encoder given one fails).
- **Payload codec.** Cached payloads are encoded by an explicit codec written per record
  type. It does not reflect (3.4, item 1). Its code is inside Core, so it is inside
  `codeIdentity`. T-CODEC tests it.

### 3.8 Dependency identity (build and runtime inputs outside Core source)

`dependencyIdentity` covers the build and runtime inputs that can change a cached
stage's result and that are not Core source. Each item names its source.

- **Package identities.** Every package reference that affects the build or the runtime:
  - `loach.PureSharp` 0.1.6, `PrivateAssets="all"` (`src/PSXRecomp.Core/PSXRecomp.Core.csproj:24`);
  - `SharpCompress` 0.50.4 (`PSXRecomp.Core.csproj:25`), used by `ChdCdCodec.cs:3`;
  - `loach.ArchitectureAnalyzer` 0.1.0, `PrivateAssets="all"`, a build-time analyzer
    whose `AdditionalFiles` input is `src/architecture.contract.json`, in the
    `EnableArchitectureAnalyzer` item group (`Directory.Build.props:44-47`, package at `:45`).
  - The resolved versions are read from `obj/project.assets.json`. This is an explicit
    exception: it is the only `obj/` file read, and it is read by exact name. The csproj
    pins direct packages only, and there is no lock file (CONFIRMED by listing).
- **SDK version and runtime version (MINOR-7).** The mechanism is this:
  - At build time, a target reads the MSBuild property `$(NETCoreSdkVersion)` (UNVERIFIED
    name, U5) and writes it into a generated source file in `obj/`. The generated file
    is compiled into Core, so its value is inside the MVID of 3.3.
  - At run time, the runtime version is read from `RuntimeInformation.FrameworkDescription`.
    This is an explicit exception to item 9 of 3.4: it is an identity string, read as a
    dependency identity, and it is not an environment value that affects a result.
  - The run-time value `RuntimeInformation.FrameworkDescription` and the build-time SDK
    value are both part of `dependencyIdentity`. The generated file's contents are
    UNVERIFIED until U5 runs.
- **Target framework.** `TargetFramework` is `net10.0` (`PSXRecomp.Core.csproj:3`).
- **Build inputs.** `Directory.Build.props` (repository root), and the attribute source
  `src/Architecture/PSXRecompArchitectureAttributes.cs`, linked into Core by
  `Directory.Build.props:22-24` when `CompileArchitectureAttributes` is set
  (`PSXRecomp.Core.csproj:7`). The attributes are compiled into Core, so the MVID of 3.3
  covers them.
- **Failure rule (M8).** If any of these cannot be read, the stage is uncacheable and
  reports `ANALYSIS_CACHE_KEY_UNAVAILABLE` (6.4, F5). No constant is substituted.

### 3.9 Decision: line endings

**DEC-LF1.** Digests of text use LF-normalized content: CRLF becomes LF, a lone CR is kept,
and a BOM is kept. A checkout with different line-ending settings must not change a digest
for the same source. The git warning "LF will be replaced by CRLF" for the design file was
observed during commit `6d038a0` (OBSERVED). The MVID of 3.3 does not depend on the
checkout's line endings, because it is computed from the compiled output.

## 4. Invalidation

### 4.1 Rules

- **V1. Changed input value.** A changed record digest changes the key, and the entry is
  not reused.
- **V2. Changed code.** A changed `codeIdentity` changes every key.
- **V3. Changed dependency.** A changed package, SDK, or runtime version changes every key.
- **V4. Untracked value.** A value read but not in the record is a defect (3.5 static
  check). Until fixed, the stage is uncacheable.
- **V5. Unknown dependency.** An implicit input, or a call that breaks Rule NC, makes the
  stage uncacheable.
- **V6. Field removal (MAJOR-2).** A record field is removed only on **static evidence**
  from U2: the reachability analysis shows that no reached code reads it. A field with
  zero reads in a corpus run is **not** evidence that the field is unused. A zero-read
  field is a flag for review, not a removal. The removal is confirmed by T-DEP-DEAD (7.3)
  on a fixture set that is separate from the one used for T-READ.

### 4.2 Stage consequences

- **N7.** Any Core change changes `codeIdentity` (DEC-CODE). A change to the text segment
  changes `TextSegment`. `count` is not a key component, and the slice of 5.1 handles it.
- **N8.** Changes to the N7 output after the slice, to `DecodeStart`, or to Core.
- **N9 to N11.** Changes to the upstream digests, the record fields, or Core.
- **N10.** `BiosJumpTables` and `BiosCallFamily` are Core symbols, so they are inside
  `codeIdentity`. Changing them invalidates N10 and every stage that depends on N10's output.
- **N11.** The identity and header fields of 3.5. A different disc hash or boot path
  changes the N11 key even when the executable is identical (C1 of r7).
- **N12a.** Changes to the report digest, the fixture identity fields, or Core. It does
  not change with `validations`, because `validations` is not in the record (MAJOR-1).
- **N12d.** Never cached. It is built at N14 from the current run's validations.

### 4.3 Input identity verification

- **Rule V-ID.** The pipeline computes SHA-256 over the stream or array it runs and
  compares it with the supplied value. A mismatch fails the Input stage with a dedicated
  failure kind. The stream path hashes from position 0 to end and then seeks back to 0.
  The stream contract requires seekability (`RomAnalysisPipeline.cs:70-72`).
- **Where the hash is computed today.** The CLI hashes the file in its own pass and then
  runs the pipeline on a second open (`CliInput.cs:106-109`, `:115`). The test driver
  hashes separately (`RealRomAnalyzer.cs:102-104`). The pipeline checks only that the
  supplied hash is non-empty (`RomAnalysisPipeline.cs:93-98`, `:194-198`). The GUI path is
  UNVERIFIED (U6).
- **Migration.** `src/PSXRecomp.Tests/DiscImage/DiscImageHostileSizeTests.cs:24` defines
  `Sha` as a literal that is not the hash of the bytes passed at `:347`. About 50 call
  sites exist in the test projects (9 in that file, 2 in `SyntheticChdReaderTests.cs`,
  2 in `RealRomAnalyzer.cs`, 36 in `RomAnalysisPipelineTests.cs`, 1 in
  `RealRomProductionExecutionTests.cs`). How many pass a constant is UNVERIFIED (U7).

## 5. Partial re-analysis: the decode prefix

### 5.1 Where the prefix is sliced

- **CONFIRMED** Today the count is applied inside the decode loop: `DecodeInstructions(exe, count)`
  (`RomAnalysisPipeline.cs:501-502`, call at `:355`), with the loop bound `decoded < count`
  (`:511`). The downstream BasicBlockBuilder receives that count-limited list (`:381`).
- **Decision DEC-N7.** N7 computes the full sweep from `EntryPoint` to `TextEnd`, with no
  count. The count-limited list that N8 sees is produced by a slice **before N8**: the
  slice takes the first `min(count, total)` items of N7's output, and its digest is N8's
  input (3.5). The slice reproduces today's list exactly (T-PREFIX).
- **Cost.** A cold N7 decodes the whole text segment rather than the first `count` words
  (UNVERIFIED, U4).
- **Failure rule.** A decode failure at address `A` is included in a count-limited result
  iff `A < entry + 4*M`, where `M` is the requested count.

### 5.2 Text shorter than the declared size

- **CONFIRMED** `RomAnalysisPipeline.cs:340-347` rejects only a zero-length `TextSegment`.
- **CONFIRMED** `PsxExe.GetInstructionWord` throws `ArgumentOutOfRangeException` when
  `offset + 4 > TextSegment.Length` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:21-30`).
- **CONFIRMED** The decode loop catches it (`RomAnalysisPipeline.cs:531`), records a
  `DecodeFailure`, and stops (`:533-538`). No exception escapes the stage.
- **Test T-SHORT** pins this.

## 6. Entries, reads, writes, and failure

### 6.1 Entry layout (proposal)

Each entry is a directory named by the key's SHA-256. It holds:

- `header`: `keyFormat`, the key text and its SHA-256, `payloadSha256`,
  `payloadSizeBytes`, `codeIdentity`, `dependencyIdentity`.
- `payload`: the stage output in the codec of 3.7.
- `COMPLETE`: written last (proposal; no on-disk marker exists today). The driver records
  `Manifest` and `Complete` as stage results (`RealRomAnalyzer.cs:261-262`), and
  `RomAnalysisStage.cs:10` says the pipeline does not produce them.

Each key has its own directory, so a failure on one key cannot affect another.

### 6.2 Write protocol

- **W1.** Write `payload` and `header` to temporary names in the key's directory.
- **W2.** Flush to disk.
- **W3.** Rename into place.
- **W4.** Write `COMPLETE` last, after W3 succeeds for every file.
- **W5.** Never write an entry for a stage that did not pass, or for an uncacheable stage.
- **W6.** If the target already exists, it is accepted only when its header key, its
  `payloadSha256`, and its `COMPLETE` marker all match this write. Otherwise the existing
  target is treated as corrupt (6.4, F1) and this write is retried once.

### 6.3 Read protocol

An entry is used only if all of the following hold:

- **R1.** `COMPLETE` exists.
- **R2.** `header` parses, and its `keyFormat` is supported.
- **R3.** `header.key` equals the key computed from the current inputs.
- **R4.** The payload size equals `payloadSizeBytes`.
- **R5.** SHA-256 of the payload equals `payloadSha256`.
- **R6.** The payload decodes under the current codec.
- **R7.** Any exception during R1 to R6 is caught and treated as a corrupt entry.

R1 to R7 prove only that an entry is internally consistent. R3 ties the entry to the
current inputs, and it is correct only because the key is complete (sections 3 and 9). A
consistent entry whose key matches but whose payload differs from what the stage now
computes would be used in Normal mode. That is the stale-entry case of section 7.

### 6.4 Failure: fail safe

- **F1. Corrupt.** An entry failing R1 to R7, or failing W6, is quarantined by renaming it
  to a name that records the failing check. The stage is recomputed and written by 6.2.
  The run reports `ANALYSIS_CACHE_QUARANTINED` as a warning.
- **F1a. Terminal rule for a failed quarantine.** If the rename itself fails, the entry is
  **poisoned** for the run: the run does not read it again, recomputes the stage in memory,
  and writes nothing to that key's directory. The run reports `ANALYSIS_CACHE_QUARANTINED`
  with `quarantineFailed = true`. Other keys are in separate directories and are not
  affected. The entry is not deleted automatically.
- **F2. Missing.** A miss. The stage is recomputed. No diagnostic is reported.
- **F3. Uncacheable.** The stage is recomputed on every run, and the run reports
  `ANALYSIS_CACHE_UNCACHEABLE` once per stage.
- **F4. Partial failure.** If a stage fails, no entry is written for it or for any stage
  below it (W5).
- **F5. Key input unavailable.** If any part of the key cannot be read (the package file,
  `Directory.Build.props`, the generated SDK file, the runtime version, or the MVID of 3.3),
  the stage is uncacheable and the run reports `ANALYSIS_CACHE_KEY_UNAVAILABLE`. No constant
  is substituted for a missing input.
- **Codes.** Four codes are proposed: `ANALYSIS_CACHE_QUARANTINED`,
  `ANALYSIS_CACHE_UNCACHEABLE`, `ANALYSIS_CACHE_KEY_UNAVAILABLE`, and
  `ANALYSIS_CACHE_VERIFY_MISMATCH`. None is registered today. Under
  `docs/adr/019-diagnostics-and-recovery-contract.md` a code is a validated `SCREAMING_SNAKE`
  identity. The existing analysis codes (`src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:47-53`)
  do not cover cache faults.

## 7. Modes, seams, and tests

### 7.1 Modes and seams

- **Default mode is Verify.** In Verify mode, every stage is recomputed cold. If an entry
  exists, its payload is compared with the cold payload byte for byte. A difference is
  `ANALYSIS_CACHE_VERIFY_MISMATCH` and a hard failure. The cold result is returned, never
  the entry.
- **Normal mode** returns a valid entry as a hit. It may be enabled only after U2 passes
  for the stage. Before that, Normal mode is unavailable for every cached stage.
- The default is set in `AnalysisCacheOptions` and is not optional.

Injection seams (all Core types):

- `IAnalysisCacheStore`: the store port (8.1). Tests pass an in-memory fake.
- `IStageCodeIdentity`: returns `codeIdentity`, `dependencyIdentity`, and the stage's
  uncacheable verdict. Tests pass a fake.
- `IStageInputRecorder`: wraps an input record and counts reads per field (3.6).
- `AnalysisCacheOptions`: `Mode` (default `Verify`), the store, the code identity provider,
  the recorder, and a diagnostics sink.

### 7.2 Test scope

Several tests run the same code twice, or substitute a digest in a fake. They check the
cache mechanics on the current code. They **cannot** detect a missed input, because the
cold and warm runs share the same code and the same records. Two kinds of test are not
of that kind:

- **T-GOLDEN** compares cold payloads with committed payloads from an earlier revision,
  so it crosses a change of code.
- **T-CLOSURE-RT** compares the symbols an instrumented run executes with the closure that
  U2 analyses. It is a check of the analysis against the executed code on the current
  revision. It does not compare two revisions.

Each row of 7.3 states its scope.

### 7.3 Tests

Synthetic inputs come from `src/PSXRecomp.Tests/RealRomAnalysis/`: `SyntheticDiscBuilder.cs`,
`SyntheticChdBuilder.cs`, and `SyntheticIsoBothEndianTests.cs` (they exist; their APIs are
UNVERIFIED, U8). No BIOS, ROM, or CHD content is used. Clocks are injected. Temp paths are
not asserted.

Two fixture sets are used, and they must not overlap:

- **Fixture set A** drives T-READ and T-COLD-WARM.
- **Fixture set B** drives T-DEP-DEAD and T-DEP-INPUT. Set B uses other synthetic
  configurations (different counts, different text layouts, and a different disc
  identity), so that a field read only in set-B configurations is not missed.

| ID | Test | Asserts | Scope |
|---|---|---|---|
| T-COLD-WARM | Cold and warm equality | For each set-A fixture and each cached stage: cold run with an empty store, store entries, warm run. Each stage that is a candidate and passed U2 must report a hit (a warm run with zero hits fails). The warm payload equals the cold payload byte for byte, per stage and for the document. Until U2 passes, the test asserts zero hits and byte equality, which checks that uncacheable stages recompute correctly. | Current code only (7.2) |
| T-DEP-INPUT | Changed record value is not reused | For each stage and each record field, on set-B fixtures, change that one field. The key differs, the entry is not reused, and the recomputed payload is stored under the new key. | Current code only |
| T-DEP-CODE | Changed code identity is not reused | Substitute a changed `codeIdentity` in `IStageCodeIdentity`. The entry is not reused. | Current code only; a fake digest, not a real change |
| T-DEP-DEAD (MAJOR-2) | Field removal is checked by value and by static verdict | For each field whose set-A reads are zero, and only for those: (a) the U2 static verdict must say "unread"; (b) on set-B fixtures, recompute cold with the **changed** field value; the cold payload must equal the **cached** payload byte for byte. A field that fails (a) or (b) stays in the record. A key comparison alone is not accepted. | Set B and U2 static verdict |
| T-IDENT2 (C1, m4) | Same executable, different disc identity | Two fixtures with the same boot executable bytes and different disc hashes (and, in a second case, a different boot path). The N11 and N12a keys differ, the second run does not reuse the first run's entry, and each payload contains its own disc hash and boot path. | Current code only |
| T-DIFF (MAJOR-1) | N12a is independent of validations | For a fixed `(report, fixture)`, vary `validations` (none, one run, two runs, and different order). The N12a payload is byte-identical in every case. The document from N14 differs only in the `Differential` section. | Current code only |
| T-UNCACHE | Uncacheable stage is never cached | Inject an uncacheable verdict for a stage. The stage recomputes every run, writes no entry, and reports `ANALYSIS_CACHE_UNCACHEABLE` once. | Current code only |
| T-KEY-UNAVAIL (M8, MINOR-7) | Unreadable key input | Make the package file, `Directory.Build.props`, the generated SDK file, the runtime version, or the MVID unreadable in a fake, one at a time. The stage is uncacheable and reports `ANALYSIS_CACHE_KEY_UNAVAILABLE`. No constant key is produced. | Current code only |
| T-READ | Record matches reads | Run each cached stage under the recorder on set-A fixtures. Every record field read is recorded. A zero-read field is listed as a flag for review (V6). It is not removed by this test. | Current code only (runtime) |
| T-STATIC (M2, MAJOR-1) | Static check of the record and the split | Each stage signature has one parameter, its reached body reads nothing outside the record, and the N12a closure does not reach `BuildDifferential`, `validations`, or `Differential`. Implemented by the U2 analysis; until U2 passes, the check is not run. | Static, U2 |
| T-CLOSURE-RT | Executed symbols against the analysed closure | Run each candidate stage under an instrumented build that records executed methods and the environment, time, file, culture, and random APIs. Every executed Core method must be in the analysed closure, and no implicit-input API may execute. | Current revision, exercised paths only; instrumentation UNVERIFIED (U2) |
| T-CORRUPT | Corrupt entries fail safe | For each corruption (zero-length file, truncated payload, flipped byte, wrong key in header, unsupported `keyFormat`, malformed header, schema mismatch), the run recomputes, the output equals the cold output byte for byte, and `ANALYSIS_CACHE_QUARANTINED` is reported once per corrupt entry. | Current code only |
| T-QUAR-FAIL (m6) | Failed quarantine is terminal | Make the rename fail for a corrupt entry. The run recomputes, reports `quarantineFailed = true`, writes nothing to that key, and writes successfully for a different key in the same run. | Current code only |
| T-W6 (m5) | Existing target | A write whose target exists with a matching `payloadSha256` but a different key or a missing `COMPLETE` is treated as corrupt, not accepted. | Current code only |
| T-MISSING | Missing entries are silent | Delete an entry. The run recomputes, the output equals the cold output, and no diagnostic is reported. | Current code only |
| T-PARTIAL | Partial failure writes nothing below | A failure injected at N8 writes no entry for N8 or later. The N7 entry is the only one written. | Current code only |
| T-STALE | Stale-but-consistent entry (residual risk) | An entry whose header and `COMPLETE` marker match the current key, with a payload that differs from the cold result. In **Verify** mode (default) the run reports `ANALYSIS_CACHE_VERIFY_MISMATCH` and returns the cold result. In **Normal** mode (only after U2) the entry is returned. | Current code only |
| T-SHORT (5.2) | Short text segment | An executable whose text segment is shorter than its declared size passes `MIPS_DECODE` with exactly one failure at the first address past the segment, and no exception escapes. | Current code only |
| T-PREFIX (5.1) | Prefix slice | N7 output sliced to `M` before N8 equals today's count-limited decode at `M`, including failures filtered by `A < entry + 4*M`. | Current code only |
| T-DIFF-RUN | Differential on and off, end to end | Cold and warm runs with validations supplied and without, both byte for byte. N12d is never read from the cache. | Current code only |
| T-IDENT | Identity verification (4.3) | A supplied hash that differs from the computed hash fails the Input stage with a dedicated failure kind and no cache write. The CLI and GUI, given the same bytes, compute the same verified hash (GUI part UNVERIFIED, U6). | Current code only |
| T-CODEC | Key encoder and payload codec | The key text of a fixed sample equals the committed text, and its SHA-256 equals the committed value. Member order is UTF-8 byte order. Floats are rejected. The payload codec round-trips each record type. | Current code only |
| T-LF | Verbatim string lint (DEC-LF1) | Fails if a file on a dependency input path contains a multi-line verbatim string literal. | Static |
| T-GOLDEN | Cross-revision regression | Committed golden payloads, produced from synthetic fixtures at an earlier revision, are compared with a cold recompute. Each cached stage must have at least 4 compared entries (one per fixture), so the check cannot pass with zero comparisons. An intentional payload change requires regenerating the goldens with the named command `regenerate-analysis-goldens`, and the golden diff is reviewed. Location: `src/PSXRecomp.Tests/RealRomAnalysis/AnalysisCacheGolden/`, one `<unit>/<fixtureId>.golden.json` per pair (proposal). | **Crosses revisions** |

Determinism rules for every test: no wall-clock values in keys, headers, or assertions;
no dependence on the order of unsorted collections; no reliance on the temp directory name.

## 8. Architecture, store, and AI results

### 8.1 Architecture placement

The architecture matrix (`docs/architecture-matrix.md`) assigns:

- **CONFIRMED** Domain (`PSXRecomp.Core`) owns ports and deterministic contracts
  (`architecture-matrix.md:22`, rule 1 at `:64`) and must not reference `PSXRecomp.Infrastructure`.
- **CONFIRMED** Infrastructure owns host adapters, including `File` and `Directory` access
  (`architecture-matrix.md:24`, `:55`, rule 2 at `:65`).
- **CONFIRMED** Application and CLI do not call `File.*` or `Directory.*` for ordinary logic
  (rule 3 at `:66`). Concrete adapter wiring is allowed at one composition-root site with
  an AARC002 suppression (`:47`).

Resolution:

- Core: `IAnalysisCacheStore`, the key and entry types, the key encoder and payload codec
  (3.7), the input records, `IStageCodeIdentity`, `IStageInputRecorder`,
  `AnalysisCacheOptions`, and the validation of R1 to R7 as checks over bytes already read.
- Infrastructure: `FileAnalysisCacheStore : IAnalysisCacheStore`, marked `[Infrastructure]`,
  with the file operations of 6.2 and 6.4.
- Precedent: `src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs` implements
  `IProjectMetadataStore`, defined in `PSXRecomp.Core.Project` (Issue #42).
- **ADR required** before implementation: the port and adapter, the move of persistence out
  of the test project (U3), and the reading of the MVID. This document does not create one.

### 8.2 Stores and sharing

- The CLI and GUI pass a store root and the same key function. The store root's location
  is UNVERIFIED (U6).
- Cross-process concurrency uses 6.2.

### 8.3 AI result reuse (proposal)

- **OBSERVED** No AI result producer exists.
- **CONFIRMED** `EvidenceType.AIPrediction = 6` exists at
  `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:17`, next to `AnalyzerFinding = 5` (`:16`).
- **Rules.** An AI result is advisory and is never stored as an analysis unit or in an
  analysis document. It is reused only when its key matches the digests of the artifacts
  it was computed from, the prompt or template revision, the model identifier, and the
  model settings. Whether it uses `EvidenceType.AIPrediction` is decided in its own ADR.

## 9. Open items (UNVERIFIED unless stated)

- **U2. Gate for every cached stage (BLOCKER until it passes).** The reachability analysis
  on the Roslyn semantic model, with class hierarchy analysis, generic instantiation,
  static-initializer reads, the split check of N12a, the record check of 3.5, and the
  instrumentation for T-CLOSURE-RT. None was run. If U2 cannot establish a complete scan
  for a stage, that stage stays uncacheable. This document claims no stage is safe until
  U2 passes for it.
- **U1.** Extract each cached stage into a function that takes only its record, and split
  `Analyze` into classification and differential assembly (1.2, 2.1). Behavior-preserving, UNVERIFIED.
- **U3.** Enumerate the project fields that reach analysis (1.3).
- **U4.** Measure cold N7 over the full text segment (5.1).
- **U5.** Confirm `$(NETCoreSdkVersion)` in this repository, the generated-file mechanism
  of 3.8, and the build determinism that the MVID of 3.3 relies on.
- **U6.** Read the GUI caller of `discImageSha256` (`RealRomTitleExecutionService.cs:110-117`),
  and decide the store root.
- **U7.** Count the test call sites that pass a constant SHA (4.3, Migration).
- **U8.** Read `PathRedactor.cs` and the synthetic builder APIs before the tests are implemented.
- **U9. Closed.** The declarations are `src/PSXRecomp.Core/Runtime/CdRom/ICdSectorSource.cs:11`
  (`CdDiscToc`) and `:56` (`ICdSectorSource`). This closes the location question. It does not close U2.
- **U10.** Confirm which declared stage outputs embed `ArtifactJson` content
  (`RealRomCoverageDocument.cs:245`, `RecompilerContract.cs:1126`).

## 10. Non-goals

- No change to the decoder, the formatter, the YAML, the analyzer, or the artifact schemas.
- No cache implementation. This is the design only.
- No stage is declared cacheable in this document. The gate is U2.
- No AI result producer.
- No change to the project file format.
- No ADR is created here.

## 11. Review history

- Rounds 1 to 3 and the r4 to r5 revisions used a static file partition. That partition is
  withdrawn (section 0). Its findings are superseded.
- The review of `9ced7d1` (r6) is answered by r7 (`29e1488`), and the review of `29e1488`
  (r7) is answered in section 12.

## 12. Finding traceability (review of 29e1488, r7)

| ID | Finding | Resolved in |
|---|---|---|
| MAJOR-1 | N12a reads validations | 2.1 (N12a and N12d rows; the split), 3.5 (N12a record and split check), 3.5 static check, T-DIFF, T-STATIC |
| MAJOR-2 | Zero-read fields removed on corpus evidence | 3.6 (recorder is not a basis for removal), 4.1 (V6), T-READ (flag only), T-DEP-DEAD (static verdict and a separate fixture set B), 7.3 (fixture sets A and B) |
| MAJOR-3 | Non-Core scope | 3.2 (corrected wording), 3.3 (non-Core paragraph and Rule NC), 3.4 item 5, 3.8 (names the packages and build inputs) |
| MINOR-1 | Contradictory "unchanged output leaves downstream valid" sentence | Deleted from 2.2; the data flow in 2.2 now says reuse is decided per key |
| MINOR-2 | "InvariantCulture consistently" claim | 3.4 item 8 (reworded: some sites use it, others do not, with `:286` and `:333`) |
| MINOR-3 | Items 2, 9, 10, 12 have no current occurrence | 3.4 items 2, 9, 10, 12 labelled as generic guards |
| MINOR-4 | `JsonSerializer.Serialize<T>` wording | 3.4 item 1 (`JsonSerializer.Serialize(document, CanonicalOptions)` inside `ArtifactJson.Serialize<T>`, `ArtifactJson.cs:54`) |
| MINOR-5 | N9 citation of `TextStart` and `TextSize` | 2.1 and 3.5 N9: entry at `FunctionDiscovery.cs:105`, text bounds at `:173` |
| MINOR-6 | Missing `Directory.Build.props:45` and `PSXRecomp.Core.csproj:24` | 3.8 (both named, with `loach.ArchitectureAnalyzer` and `loach.PureSharp`) |
| MINOR-7 | SDK version mechanism; runtime version missing from T-KEY-UNAVAIL | 3.8 (generated file at build time; runtime version via `RuntimeInformation`; explicit exception to item 9), T-KEY-UNAVAIL, F5 |
| MINOR-8 | SharpCompress not named in the allow-list | 3.4 item 13 (package identity named; a closure reaching it is uncacheable) |
| MINOR-9 | "only two tests compare across a change" | 7.2 (T-CLOSURE-RT is reworded as a check of the current revision; only T-GOLDEN crosses revisions) |

The blocker is **U2**. It is the reachability analysis that the static record check, the N12a
split check, the implicit-input scan, and the runtime closure check need. It has not been
built or run. Until U2 passes for a stage, that stage is uncacheable, and this document
claims no stage is safe.
