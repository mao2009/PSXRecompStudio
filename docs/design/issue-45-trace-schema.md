# Issue #45 Debug / Trace Schema Design

**Status:** Proposal (design only, no code change)

**Related Issues:** #45 (Debug / Trace Infrastructure, open); related #40, #42, #44 (closed), #16, #132, #457 (closed), #49 (closed), #739 (open), #763 (open), #773 (open), #774

**Base:** `2e3ecad` (origin/main)

## 0. How to read the labels

Every claim carries one of four labels:

- **CONFIRMED**: read directly in source, docs, or test code at the cited location in this checkout.
- **OBSERVED**: stated by an existing doc, issue text, or the GitHub API response. Not re-run in this work.
- **PREDICTED**: a design expectation this document proposes. Not yet true.
- **INFERRED**: a conclusion drawn from code or docs, not measured.
- **UNVERIFIED**: not checked, or checkable only by running the system or a test that does not yet exist.

Two more terms are used throughout:

- **Local** output: any writer that is not the shareable projection (section 7.4). Local output may contain any field.
- **Share-candidate** output: a field that would pass the allowlist (section 7.2) if the shareable projection emitted it. A share-candidate is not shareable until the projection emits it. **PREDICTED** (design term).
- **Default-deny**: a field that is not shareable until a named decision or a named read is made. Unread shapes are default-deny (Q16).

### 0.1 Issue #45 body (verbatim, from the public API `body` field)

> ## 概要
> Recomp後のゲームや解析結果を調査できる共通Debug/Trace基盤を設計する。
>
> ## 対象
> - PC / PS1 virtual PC tracking
> - Registers
> - Memory access
> - MIPS instructions
> - Functions / basic blocks / CFG
> - Runtime events
> - GPU / SPU / CD / DMA events
> - Crash diagnostics
> - Structured trace
>
> ## 完了条件
> - [ ] Debug data modelを定義
> - [ ] Trace event schemaを定義
> - [ ] Runtimeからのtrace取得方式を定義
> - [ ] GUI/CLIで共通利用できる形式を定義
> - [ ] Crash/session/artifactとの関連付けを定義
> - [ ] Performance overheadとsampling方針を定義
>
> ## 関連
> - #40 Studio GUI
> - #42 Project / Workspace
> - #44 PS1 Hardware Runtime Architecture

**OBSERVED** (GitHub API, `body` field of `repos/mao2009/PSXRecompStudio/issues/45`). The body does not fix session ID or artifact ID formats, and does not mention BIOS or ROM data handling. **OBSERVED.**

**Assignees and state (GitHub API, checked for this revision):** #45 open, 0 assignees. #457 closed, 0 assignees. #49 closed, 0 assignees. #763 open, 0 assignees. #773 open, 0 assignees. #40 open, 0 assignees. #42 open, 0 assignees. #739 open. #44 closed. #786 is a pull request (closed). **OBSERVED.** No issue in this list records an owner.

**Owner ruling (binding):** the full register dump is removed from the shareable diagnostic output. GPR and COP0 full dumps are local-diagnostic only and never appear in a shareable document. Section 7 implements this.

## 1. Inventory of existing trace and diagnostics code

### 1.1 Shared diagnostic contract (Domain, `PSXRecomp.Core.Diagnostics`)

| Element | Location | Label |
|---|---|---|
| `Diagnostic` record (Code, Category, Severity, Stage, Context, Evidence, Recovery; no timestamp, host, user or path) | `src/PSXRecomp.Core/Diagnostics/Diagnostic.cs:30` | CONFIRMED |
| `DiagnosticCodes` registry (discoverability only) | `src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:20` | CONFIRMED |
| `DiagnosticContextEntry` (typed `uint`/`string` key-value) | `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:12` | CONFIRMED |
| Context key constants, including `InstructionOpcode = "opcode"` | `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:29` (class), `:38` (`InstructionOpcode`) | CONFIRMED |
| `DiagnosticEvidenceReference` (kind + identifier; references evidence, never embeds it) | `src/PSXRecomp.Core/Diagnostics/DiagnosticEvidenceReference.cs:48` | CONFIRMED |
| `DiagnosticJson` (deterministic, nulls written, LF) | `src/PSXRecomp.Core/Diagnostics/DiagnosticJson.cs:18` | CONFIRMED |
| `DiagnosticComparer` (stable total order) | `src/PSXRecomp.Core/Diagnostics/DiagnosticComparer.cs:14` | CONFIRMED |
| `DiagnosticExceptionPolicy` (process-fatal boundary, cancellation not a failure) | `src/PSXRecomp.Core/Diagnostics/DiagnosticExceptionPolicy.cs:21` | CONFIRMED |
| Architecture contract for the above | `docs/architecture/diagnostics.md` sections 2-6 | OBSERVED |

`docs/architecture/diagnostics.md` section 4 states that the full evidence and trace capture pipeline belongs to Issues #16, #45, and #132, and that the contract only defines how a diagnostic points at evidence. **OBSERVED** (`docs/architecture/diagnostics.md:86-88`).

### 1.2 Standard execution report and bundle (`#457`, closed)

| Element | Location | Label |
|---|---|---|
| `ExecutionDiagnosticReport` (schema `psxrecomp.execution-report.v1`; identifiers and classifications only) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:13` | CONFIRMED |
| Report fields, in order: `Schema`, `ProductRevision`, `InputSha256`, `Title`, `Revision`, `ArtifactIdentity`, `Outcome`, `State`, `GuestPc`, `DiagnosticCode`, `SegmentBudget`, `FramebufferSha256` | `ExecutionDiagnosticReport.cs:14-25` | CONFIRMED |
| `InputSha256` is normalised to bare, lowercase, 64-character hex (`NormalizeSha256` rejects anything else) | `ExecutionDiagnosticReport.cs:53`, `:67-75` | CONFIRMED |
| `RunCommand` sets `ArtifactIdentity` to `"sha256:" + ComputeSha256(artifactPath)`, where `ComputeSha256` returns bare lowercase hex. The prefix is added at the call site. | `src/PSXRecomp.Cli/RunCommand.cs:191`, `ComputeSha256` at `:206-210` | CONFIRMED |
| `artifactPath` is the built artifact, resolved by `ResolveArtifactPath` | `RunCommand.cs:81` | CONFIRMED |
| `inputSha256` is declared at `:52`, and assigned only inside `if (arguments.Report)` (`:54`, value at `:58` from `resolved.Sha256`) | `RunCommand.cs:52-58` | CONFIRMED |
| The diagnostic bundle is written only inside `if (arguments.Report)` (`:86`); `inputSha256` is passed to `WriteDiagnosticBundle` at `:93` | `RunCommand.cs:86-93` | CONFIRMED |
| The CLI passes an empty diagnostics array to the bundle writer, so the CLI bundle's `diagnostics.log` has no entries | `RunCommand.cs:197-201` (`Array.Empty<Diagnostic>()` at `:201`) | CONFIRMED |
| `ExecutionDiagnosticLog` (projects diagnostics onto an allow-list of context keys, max 256 entries) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:12`, allow-list `:17-30`, filter `:78-79` | CONFIRMED |
| `ExecutionDiagnosticLog` allows the `opcode` key (`DiagnosticContextKeys.InstructionOpcode`) as a `uint`. A value under this key is a raw instruction word. | `ExecutionDiagnosticLog.cs:28` | CONFIRMED |
| `ExecutionDiagnosticBundleWriter.Write` serialises `report` (`:51`), `environment` whole (`:52`), and `diagnosticLog` from `FormatTail` (`:53`), and writes four entries (name constants `:18-21`) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:16`, `:51-53`, `:18-21` | CONFIRMED |
| `issue.md` is formatted from `report` by `ExecutionIssueMarkdown.Format` (it prints the artifact identity and input hash) | `ExecutionDiagnosticBundleWriter.cs:54`; `ExecutionIssueMarkdown.cs:36` | CONFIRMED (call); the full issue.md text was not read |

### 1.3 Runtime host diagnostic (`#763`, open)

| Element | Location | Label |
|---|---|---|
| `RuntimeHostDiagnostic` record (per-run stop account; off by default, observation only; unknown fields are `null`, not guessed) | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:24` (record starts); fields `FinalPc` through `Metrics` follow | CONFIRMED |
| `RuntimeHostRunMetrics` record: `WallMilliseconds`, `MmioAccesses`, `TimeReports`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds`, `LastFallbackEntryPc`, `LastFallbackReturnPc`, `LastMmioPhysicalAddress`, `LastMmioWasWrite`, `FallbackInFlight`, `FallbackReturns` | `RuntimeHostDiagnostic.cs:146-160` | CONFIRMED |
| Per-run `runId` (GUID, generated once) | `src/PSXRecomp.RuntimeHost/Program.cs:198`; printed at `:210` | CONFIRMED |
| `runId` also written into the loaded-code mismatch report (`report["runId"] = runId`) | `src/PSXRecomp.RuntimeHost/Program.cs:624` | CONFIRMED |

### 1.4 Unit retirement trace (`#774`)

| Element | Location | Label |
|---|---|---|
| Design and schema: JSON Lines, `v:1`, `kind` records, session and handoff identity | `docs/development/unit-trace.md`, sections "Enabling", "Identity", "Record schema" | OBSERVED |
| `UnitTraceScope.Begin` (in-process enable for tests) and `UnitTraceRun` (child env, parent fallback file) | `src/PSXRecomp.Infrastructure/UnitTraceRun.cs:15`, `:54` | CONFIRMED |
| Session ID generated per run as `Guid.NewGuid().ToString("N")` and passed to the child via `PSX_TRACE_SESSION` | `src/PSXRecomp.Infrastructure/UnitTraceRun.cs:88`, `:112` | CONFIRMED |
| Native side: opt-in trace build, env `PSX_UNIT_TRACE_FILE`, `PSX_UNIT_TRACE_MAX_BYTES`, default cap `RECOMPILER_TRACE_DEFAULT_CAP`, truncation and end records | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:79`, `:1037-1077`, `:1155-1162` | CONFIRMED |
| Native `PSX_TRACE_SESSION` is accepted only when it is a non-empty token of at most 64 characters from `[0-9A-Za-z-]`; otherwise the session is left empty | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:1120-1137` | CONFIRMED |
| Joiner: `Complete` / `Partial` / `Rejected`, sequence gap checks, handoff pairing | `src/PSXRecomp.Core/Diagnostics/UnitTraceJoiner.cs:21`; status enum `:403`; segment source `:410`; `UnitTraceSegment` `:425`; `UnitTraceJoin` `:438` | CONFIRMED |
| Native trace records carry CPU state (`gpr` x32, `hi`, `lo`, `sr`, `cause`, `epc`, `pc`) at unit end, trace build only | `unit-trace.md` "Record schema"; `CpuCheckpoint` parser `src/PSXRecomp.Core/Diagnostics/CpuCheckpoint.cs:17`, `TryParse` `:38` | OBSERVED (doc); CONFIRMED (parser) |
| Disabled path emits byte-identical generated source | `unit-trace.md` "Enabling"; tests `HostCodeGenTests` (`UnitTrace_*`) | OBSERVED |

### 1.5 Interpreter fetch ring and fallback diagnostics

| Element | Location | Label |
|---|---|---|
| `ExecutionTraceSnapshot` record (fetches and transfers) | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:14` | CONFIRMED |
| `ExecutionTraceRing` class: fixed-capacity fetch ring plus control-transfer ring | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:17`; `DefaultCapacity = 64` at `:19` | CONFIRMED |
| The "never influences execution" statement is in the type's summary comment | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:11` | CONFIRMED |
| Engine exposes the ring as `RecentTrace` and fetches the instruction word via `FetchWordForTrace` | `src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:279`, `:891` | CONFIRMED |
| `FallbackReturnDiagnostics` | `src/PSXRecomp.Core/Execution/FallbackReturnDiagnostics.cs:148` | CONFIRMED (declaration only) |
| `MixedFallbackDiagnostics` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:33` | CONFIRMED (declaration only) |
| `HostMmioAccess(PhysicalAddress, Width, IsWrite)` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:161` | CONFIRMED |
| `ArtifactFallbackSession.Attribute`: per-entry version comparison, runs only with a transition observer | `src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:143-160` | OBSERVED (`docs/runtime/loaded-code-identity.md:51`) |

### 1.6 Loaded-code mismatch diagnostics (`#773`, open)

| Element | Location | Label |
|---|---|---|
| `LoadedCodeMismatchDiagnostics` (bounded records, bounded compared words) | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:19`, `:25`, `:42` | CONFIRMED |
| Per-record JSON: `pc`, `version`, `versionCount`, `comparedWords`, `ramPage`, `rejections`, `fullMatches`, `firstMismatchIndexRange`, `mismatchedWordsRange`, `ramHashChanges`, `aotSha256`, `ramSha256`, `windowStartPc`, `windowAot`, `windowRam`, `origins` | `LoadedCodeMismatchDiagnostics.cs:277-300` | CONFIRMED |
| Mismatch record keeps a 5-word window of AOT words and RAM words at the first refusal, written as `windowAot` and `windowRam` | `LoadedCodeMismatchDiagnostics.cs:30` (`WindowRadius = 2`), `:269-273`, `:296-297` | CONFIRMED |
| Source comment calls this "a bounded sample, not a dump of the image" | `LoadedCodeMismatchDiagnostics.cs:268` | CONFIRMED (comment) |
| `origins` is produced by `OriginsJson()`, whose body was not read | `LoadedCodeMismatchDiagnostics.cs:298` (call) | UNVERIFIED (shape not read) |

### 1.7 BIOS and ROM guards

| Element | Location | Label |
|---|---|---|
| `BiosAddressGuard` static class; `PhysicalBase = 0x1FC00000` | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:103` | CONFIRMED |
| `IsBiosAddress` | `BiosAddressGuard.cs:113` | CONFIRMED |
| `OverlapsBios` | `BiosAddressGuard.cs:120` | CONFIRMED |
| `RequireNoBiosImage` (refuses BIOS code in AOT generation) | `BiosAddressGuard.cs:146` | CONFIRMED |
| `BiosContentDiagnostic` record: counts and provenance text, no bytes | `BiosAddressGuard.cs:81` | CONFIRMED |
| Standalone report "never names an input". The code comment cites "Issue #786 B1"; #786 is a pull request, so the label in the comment is a mislabel. | `src/PSXRecomp.RuntimeHost/Program.cs:197` | OBSERVED (comment) |

### 1.8 OpenBIOS probe: writers inventoried

The probe is `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs`, with helpers in `OpenBiosProbeDifferential.cs`, `OpenBiosProbeAccounting.cs`, `OpenBiosProbeMdecLog.cs`, and `OpenBiosProbeBoundaryLog.cs`.

**Completeness claim.** The writers below were found by searching the probe sources for output calls (`output.WriteLine`, `error.WriteLine`, `Console.Error`, `WriteAllText`, `WriteAllBytes`, `WriteAtomically`, `StreamWriter`, and `output.Write`). The search found W1 to W13. The stderr search found 35 `error.WriteLine` calls, from `OpenBiosProbeCommand.cs:35` to `:657`, and W13 lists every one of them. **Whether this list is exhaustive is UNVERIFIED**: no tool proved that every output call was found. The required test (section 7.5, test 6) must also check that the writer list matches the code, and it searches for `error.WriteLine` as well as `output.WriteLine`.

**W1. Interpreter probe `--json` document.** Written directly at `:419` inside the `if (json)` branch, not through the common writer.

| Element | Location | Label |
|---|---|---|
| The anonymous object is serialised and written at `:419`; its fields `kind`, `backend`, `sha256`, `bootVerified`, `kernelBooted`, `milestones`, `titleStarted`, `executionState`, `guestPc`, `diagnosticCode`, `segmentsRetired`, `stop`, `captured`, `gte`, `frame` are at `:421-437` | `OpenBiosProbeCommand.cs:419` (write), `:421` (`kind`), `:423` (`sha256 = hash`), `:432` (`stop`), `:433` (`captured`), `:435` (`gte`), `:437` (`frame`) | CONFIRMED |
| `BuildStop` builds `pc`, `sr`, `cause`, `epc`, `badVAddr`, `gpr[32]`, `lastFetches` (`Hex(e.Pc):Hex(e.Word)`, last 16), `lastTransfers` (last 16, including `From.Word`) | `OpenBiosProbeCommand.cs:1004-1014` (`pc`/`sr`/`cause`/`epc`/`badVAddr` at `:1010-1011`; `gpr` at `:1012`; `lastFetches` at `:1013`; `lastTransfers` at `:1014`) | CONFIRMED |
| `BuildStop` is called with the interpreter engine's `FinalSnapshot` | `OpenBiosProbeCommand.cs:361` | CONFIRMED |
| `captured` is built at the capture PC: `pc`, `word = ReadGuestWord(pc)` (raw instruction word), `gpr[32]`, `lastTransfers` (PCs only) | `OpenBiosProbeCommand.cs:344-350`; `word` at `:346`; `gpr` at `:347` | CONFIRMED |
| `gte.lastUnsupportedCommand` is `Gte.LastUnsupportedCommand`, set to `command & 0x01FF_FFFF`; `gte.commandsExecuted` is a count | `OpenBiosProbeCommand.cs:435`; `src/PSXRecomp.Core/Runtime/Gte/GteRegisterBank.cs:106`, `:115` | CONFIRMED |
| `milestones` is an `OpenBiosBootReport` record: `ResetVectorFetched`, `KernelRunningFromRam`, `VectorsInstalled`, `ExceptionVectorExecuted`, `ShellEntered`, `ShellEnteredAtFetch`, `InterruptEntries`, `SyscallEntries`, `TitleEntryPc`, `FirstUnexpectedException`, plus `TitleStarted` and `KernelBooted` | `src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:10-40` (record at `:10-20`; `TitleStarted` at `:26`; `KernelBooted` at `:34`) | CONFIRMED |
| `milestones.firstUnexpectedException` is `OpenBiosUnexpectedException(ExcCode, Epc, BadVAddr, Cause, AtFetch)` | `src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:40`; read by the probe at `OpenBiosProbeCommand.cs:808` | CONFIRMED |
| `--capture-at <hex-pc>` option | `OpenBiosProbeCommand.cs:59-67` | CONFIRMED |
| The probe loads a 512 KiB OpenBIOS ROM and boots the firmware, so the interpreter fetches are expected to include BIOS-range PCs | `OpenBiosProbeCommand.cs:201-207` (ROM load); the boot design | CONFIRMED (ROM load); PREDICTED (BIOS-range fetches) |
| Whether any value in the `--json` output lies in the BIOS range | not checked (no probe run, no captured `--json` output inspected) | UNVERIFIED |

**W2. Generated-host `--json` document.** The document object is built at `:663-742` and passed to the common writer, which serialises it at `:743-746` and writes it at `:755`.

| Element | Location | Label |
|---|---|---|
| The generated-host document object (fields are classified in section 7.6) | `OpenBiosProbeCommand.cs:663-742` | CONFIRMED |
| Common serialisation and write: `SerializeToNode`, `consistency` added, text produced, written | `OpenBiosProbeCommand.cs:743-746`, `:755` | CONFIRMED |
| `diagnosticMessage` is set from `result.DiagnosticMessage` (free text) | `OpenBiosProbeCommand.cs:677` | CONFIRMED |
| `stopAt` is `{ pc, reachedByInterpreter, reachedByHost }` | `OpenBiosProbeCommand.cs:678-680` | CONFIRMED |
| `build` carries counts (`imageInstructions`, `nativeInstructions`, `blocks`, `roots`, `staticFallbackTargets`, `symbols` as a count) | `OpenBiosProbeCommand.cs:681-689` | CONFIRMED |
| `loadedImages[]` carries `name`, `blocks`, `nativeInstructions`, `skippedEntries` (PCs) | `OpenBiosProbeCommand.cs:690` | CONFIRMED |
| `loadedCodeVersions` is `loadedCode.Blocks.Count` | `OpenBiosProbeCommand.cs:691` | CONFIRMED |
| `interpretedEntries` is `observedPcs.Order()` (a PC array) | `OpenBiosProbeCommand.cs:692`; `observedPcs` at `:488-490` | CONFIRMED |
| `precompileMilliseconds` is `buildMs + compileMs` (not rounded at this point) | `OpenBiosProbeCommand.cs:693` | CONFIRMED |
| `atBoundary` is a dictionary keyed by PC (`Hex(b.Key)`) whose value is `new { nativeInstructions, seconds, mmioRoundTrips, timeReportRoundTrips }` (`:597-598`) | `OpenBiosProbeCommand.cs:550` (dictionary), `:597` (value shape), `:694` (output) | CONFIRMED |
| `execution` carries native and fallback counts, `topFallbackTargets` (strings of PC, counts, and a cause), and `timings = costs` (`:706`, shape not read) | `OpenBiosProbeCommand.cs:695-709` | CONFIRMED |
| `accounting` is `accounting.Report(...)` | `OpenBiosProbeCommand.cs:710-711` | CONFIRMED |
| `timings` is built at `:712-723` from `Math.Round(..., 1)` doubles (0.1 ms precision) | `OpenBiosProbeCommand.cs:712-724` | CONFIRMED |
| `aot` carries `generatedCBytes` and `artifactBytes` | `OpenBiosProbeCommand.cs:725` | CONFIRMED |
| `boundaries` is a dictionary keyed by PC whose values are `Describe()` objects | `OpenBiosProbeCommand.cs:726`; `OpenBiosProbeDifferential.cs:95-107` | CONFIRMED |
| `differential` is a dictionary keyed by PC whose values are `Compare` results, or a string for an unreached boundary | `OpenBiosProbeCommand.cs:727-731` | CONFIRMED |
| `referenceBoundariesMissing` is a PC array | `OpenBiosProbeCommand.cs:732` | CONFIRMED |
| `referenceSecondsAtBoundary` is a dictionary keyed by PC of `referenceSeconds` values (type not read here) | `OpenBiosProbeCommand.cs:733` | CONFIRMED (location); value type UNVERIFIED |
| `firstInterrupt` is `{ interpreter: Describe(), host: Describe() }` | `OpenBiosProbeCommand.cs:734` | CONFIRMED |
| `firstInterruptDifferential` is `Compare(referenceInterrupt, hostInterrupt)` or a string | `OpenBiosProbeCommand.cs:735-736` | CONFIRMED |
| `milestoneComparison` is `{ match, interpreter = referenceMilestones }` | `OpenBiosProbeCommand.cs:737-741` | CONFIRMED |
| `ProbeGuestState.Describe()` returns `pc`, `atFetch`, `cpu` (a map of named CPU values), `devices` (a map), `instructionWord` (raw word read from RAM at PC), `sr`, `cause`, `epc`, `ramSha256`, `guestCycles`, `scratchpadSha256` | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107`; `instructionWord` at `:101`; `ramSha256` at `:104` | CONFIRMED |
| The `cpu` map is built from a list that includes `hi`, `lo`, `sr`, `cause`, `epc`, `badvaddr` and the GPR entries (the GPR entries per the review) | `OpenBiosProbeDifferential.cs:36` | CONFIRMED (the six named); CONFIRMED-by-review (GPR entries) |
| `scratchpad_sha256` is a device digest | `OpenBiosProbeDifferential.cs:41` | CONFIRMED |
| `vram_sha256` is a device digest | `OpenBiosProbeDifferential.cs:83` | CONFIRMED |
| The differential emits per-field mismatch objects (`kind = "cpu"` with `interpreter` and `host` hex values) | `OpenBiosProbeDifferential.cs:117-118` | CONFIRMED |
| The differential emits a RAM mismatch object with `address`, `page`, `contextStart`, `interpreterBytes`, `hostBytes` (16 raw RAM bytes each side) | `OpenBiosProbeDifferential.cs:138-143` | CONFIRMED |
| The differential emits `interpreterRamSha256` and `hostRamSha256` (whole-RAM digests) | `OpenBiosProbeDifferential.cs:154-155` | CONFIRMED |
| `accounting.unsupportedOpcodes[]`: `excCode`, `opcode`, `pc`, `word` (raw instruction word), `atFallbackFetch` | `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:195-198` | CONFIRMED |
| Pinned by a test: `unsupported[0]["word"] == "0xFC000000"` and `unsupported[1]["opcode"] == 0x12` | `src/PSXRecomp.Tests/Cli/OpenBiosProbeAccountingTests.cs:183`, `:185` | CONFIRMED |

**W3. Non-JSON console output, interpreter path (`OpenBiosProbeCommand.cs:441-450`).**

| Element | Location | Label |
|---|---|---|
| Prints `OpenBIOS execution probe (sha256:{hash}, backend=...)` (firmware digest, `:443`), state, PC, and code | `OpenBiosProbeCommand.cs:441-445` | CONFIRMED |
| Prints `Stop: {JsonSerializer.Serialize(stop)}`, which includes the full `gpr[32]` and COP0 fields | `OpenBiosProbeCommand.cs:446-449` | CONFIRMED |
| Prints `Kernel boot milestones: {report}` | `OpenBiosProbeCommand.cs:450` | CONFIRMED |

**W12. Non-JSON summary (`OpenBiosProbeCommand.cs:749-751`, `Summarize` at `:838`).** When `--json` is not set, the document is parsed back and summarised line by line.

| Element | Location | Label |
|---|---|---|
| The summary is written at `:749-751` from `Summarize(JsonNode.Parse(text))` | `OpenBiosProbeCommand.cs:747-751` | CONFIRMED |
| `Summarize` prints `hotEntries[]` entries with `pc`, `symbol` (a symbol name), `reason`, `transitions`, `retiredInstructions` | `OpenBiosProbeCommand.cs:859-861` | CONFIRMED |
| `Summarize` prints `hotPcs[]` entries with `pc`, `symbol`, `fetches`, `share` | `OpenBiosProbeCommand.cs:864-866` | CONFIRMED |
| `Summarize` prints the differential result, and on a mismatch the `firstMismatch` JSON (may carry register or RAM values) | `OpenBiosProbeCommand.cs:874` | CONFIRMED |
| The `hotEntries` and `hotPcs` names are used by `Summarize`; their producer was not read in this revision | `OpenBiosProbeCommand.cs:859`, `:864` | CONFIRMED (consumer); producer UNVERIFIED |
| The summary also prints `timings` (`:879-881`), AOT byte counts (`:882`), and `consistency.differentialPass` (`:883`) | `OpenBiosProbeCommand.cs:879-884` | CONFIRMED |

**W4. `--retire-out <file>` (JSON lines, one per fetch at a position).**

| Element | Location | Label |
|---|---|---|
| The retirement writer is opened at `:317` | `OpenBiosProbeCommand.cs:317` | CONFIRMED |
| Each line carries `g`, `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `pipelineClean` | `OpenBiosProbeCommand.cs:328-333` | CONFIRMED |

**W5. `--pc-histogram <file>` (JSON).**

| Element | Location | Label |
|---|---|---|
| Written with `totalRamFetches`, `top` (PC plus count, top 64), and `buckets` (64-byte bucket start plus count, top 48). Comment: "Local diagnostic only" | `OpenBiosProbeCommand.cs:368-377` | CONFIRMED |

**W6. `--dump-frame <file.ppm>` (binary PPM).**

| Element | Location | Label |
|---|---|---|
| Writes the displayed frame as a binary PPM with 24-bit colour pixels. Comment: "Local diagnostic only (never committed)". | `OpenBiosProbeCommand.cs:378-411` | CONFIRMED |

**W7. `--dump-ram <file>` (raw 2 MiB main RAM).**

| Element | Location | Label |
|---|---|---|
| Option, requirement, and write | `OpenBiosProbeCommand.cs:81` (option), `:172` (requires `--capture-at`), `:337-342` (write) | CONFIRMED |

**W8. `--fallback-targets <file>` (fallback export JSON).**

| Element | Location | Label |
|---|---|---|
| The export is written with `WriteAtomically` after the run | `OpenBiosProbeCommand.cs:660`; `WriteAtomically` at `:981` | CONFIRMED |
| The export document `BuildFallbackTargetsDocument` contains `format`, `sha256` (the firmware digest, `hash`, `:973`), `run` (`ObservedRootPlanner.RunDocument(DescribeRun(hash, manifest, discSha256, ...))`, `:974`, which includes the firmware digest and the disc digest), `outcome` (`state`, `complete`, `stopAt`, `:975`), and `targets[]` (`pc`, `cause`, `entries`, `instructions`, `lastReturnPc`, `ramWord`, `:961-969`) | `OpenBiosProbeCommand.cs:952-977` | CONFIRMED |
| `ramWord` is a raw RAM word, read by `ramWordOf` | `OpenBiosProbeCommand.cs:968` | CONFIRMED |

**W9. `--mdec-events <file>` (JSON lines).**

| Element | Location | Label |
|---|---|---|
| Each line carries `engine`, `seq`, `g`, `kind`, `value` (raw 32-bit word), `pc`, `lastFallbackFetchPc`, `command` (raw 32-bit word), `remaining`, `control` (register), `fifoWords`, `busyBefore`, `busyAfter` | `src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:43-56` | CONFIRMED |

**W10. `--boundary-log <file>` (JSON lines).**

| Element | Location | Label |
|---|---|---|
| Each line is `Describe(engine, devices, vblank, cycles)`: device registers, `vramSha256`, and the boundary position | `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:40-56` | CONFIRMED (call and fields named by the review); the `Describe` body was not read in full |

**W11. `--list-disc <file>` (JSON).**

| Element | Location | Label |
|---|---|---|
| Writes the disc's file listing, including file names from the disc image | `OpenBiosProbeCommand.cs:251-289` | CONFIRMED (range); field shape not read |

**W13. Standard error (`stderr`) writers.** Messages written to stderr, which may carry local paths or free text.

| Element | Location | Label |
|---|---|---|
| Usage text and argument-validation messages, 35 `error.WriteLine` calls in total (the full list is below) | `OpenBiosProbeCommand.cs:35`, `:52`, `:63`, `:77`, `:102`, `:108`, `:120-121`, `:126-127`, `:132-133`, `:138-139`, `:147-148`, `:154`, `:160-161`, `:166-167`, `:172-173`, `:178-179`, `:190`, `:192` | CONFIRMED |
| Argument echoes: duplicate option (`:52`), option needing a hexadecimal PC (`:63`), option needing a value (`:77`), unknown option (`:102`), option needing a positive integer (`:108`). These print argv tokens. | `OpenBiosProbeCommand.cs:52`, `:63`, `:77`, `:102`, `:108` | CONFIRMED |
| Path in the message for an existing fallback export (`--fallback-targets {path} already exists`) | `OpenBiosProbeCommand.cs:154` | CONFIRMED |
| Exception type and message (`ex.Message`), which may contain local paths | `OpenBiosProbeCommand.cs:242` | CONFIRMED |
| Warnings with the boundary or MDEC failure text | `OpenBiosProbeCommand.cs:233`, `:235` | CONFIRMED |
| Progress lines with fetch counts, transitions, and seconds | `OpenBiosProbeCommand.cs:529`, `:608`, `:615` | CONFIRMED |
| Fallback export status lines (`did not finish`, `ended in {result.State}`) | `OpenBiosProbeCommand.cs:651`, `:657` | CONFIRMED |
| The 35 `error.WriteLine` lines, grouped: `:35` (usage), `:52`, `:63`, `:77`, `:102`, `:108` (argument echoes), `:120-121`, `:126-127`, `:132-133`, `:138-139`, `:147-148`, `:154`, `:160-161`, `:166-167`, `:172-173`, `:178-179`, `:190`, `:192` (validation and usage), `:233`, `:235` (warnings), `:242` (exception), `:529`, `:608`, `:615` (progress), `:651`, `:657` (status) | `OpenBiosProbeCommand.cs:35-657` | CONFIRMED (the search result; each line is in one of the groups above) |

### 1.9 Identity hashes already in use

| Element | Location | Label |
|---|---|---|
| Input identity: SHA-256 of EXE and disc; disc hash is for provenance only | `docs/runtime/loaded-code-identity.md:14`, `:45` | OBSERVED |
| `GuardStatsScope` run ID, random 64-bit hex per child process | `src/PSXRecomp.Infrastructure/GuardStatsScope.cs:18`; `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:1345` | CONFIRMED |
| `ObservedRunIdentity` (firmware SHA, manifest, disc SHA, probe options SHA) | `src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:46`, `:262` | CONFIRMED |

### 1.10 Gaps in the inventory

- **No shared session ID.** The unit trace has a session ID. The `#457` report has none. The standalone host has a separate `runId` (`Program.cs:198`, also written at `:624`). The guard-stats scope has another run ID. **CONFIRMED.**
- **No MMIO event stream.** `HostMmioAccess` and the `LastMmio*` metrics record only the last access. **CONFIRMED.**
- **No GPU, SPU, CD, or DMA event stream.** Only the device interfaces exist (`IGuestDeviceAccess.cs`, `ITimer.cs`, `IGpu.cs`, `ISpu.cs`, `ICdRom.cs`). **OBSERVED** (file list only).
- **No timestamps.** The deterministic JSON forms exclude time by design. **CONFIRMED** (`diagnostics.md` section 2).
- **Studio GUI and CLI consumption of a trace format**: none found in this inventory. **UNVERIFIED** (no exhaustive GUI search).

## 2. Common model

Four record families share one envelope. The envelope follows the existing `v:1` JSON Lines style of the unit trace, so parser patterns can be reused. **PREDICTED** (design).

What exists today versus what is new:

- The unit-trace native header carries `session` (`unit-trace.md`, "Record schema"). **OBSERVED.**
- The unit-trace fallback header carries both `session` and `stream`. **OBSERVED** (`unit-trace.md`, fallback file table).
- The per-record `session` field on every record, and the `stream` field on native and host records, are **new** in this design. **PREDICTED.**

```
Envelope (every record):
  v        int      schema version, starts at 1
  seq      uint     strictly increasing per stream; a gap rejects the join (matches unit-trace rule)
  stream   string   "native" | "fallback" | "host" | "gui" | "cli" (writer identity)  [new per record; header-only today on fallback]
  kind     string   one of the record kinds below
  session  string   session ID (section 4)  [new per record; header-only today on native]
  t        uint64?  optional monotonic ns from the writer; null when not sampled
```

Record families:

| Family | `kind` values | Core fields | Source of truth | Local or share-candidate? |
|---|---|---|---|---|
| PC / control | `unit`, `handoff`, `fallback`, `transfer`, `stop` | `pc`, `next`, `retired`, `termination`, `status` | Extends existing unit-trace kinds (`unit-trace.md`) | Trace file is local; PC and classification fields are share-candidates (section 7.6) |
| Register state | `checkpoint` | `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `globalRetired` | `CpuCheckpoint` (`CpuCheckpoint.cs:17`) | Local only (owner ruling) |
| MMIO | `mmio` | `physAddr`, `width`, `isWrite`, `value`?, `pc`? | `HostMmioAccess` (`MixedFallback.cs:161`) | `value` local only; the rest per section 7.6 |
| Runtime event | `event` | `device` (`gpu`, `spu`, `cdrom`, `dma`, `timer`, `irq`, `bios`), `name`, `code`?, `data`? | New; BIOS call identity exists (`BiosCallIdentity` in `src/PSXRecomp.Core/Runtime/BiosCallIdentity.cs`, used by `BiosServiceResult.cs:152`) | `data` local only; `name` and `code` per section 7.6 |

`BiosServiceResult.cs:152` is a use of `BiosCallIdentity` in `BiosDiagnostic`, not its definition. **CONFIRMED.**

Diagnostic records (section 1.1) are not trace records. They link into a trace by reference only (`DiagnosticEvidenceReference`). **CONFIRMED** (current contract); the link use is **PREDICTED.**

Field rules:

- Addresses are `uint` and written as hex strings in JSON, matching the existing unit trace. **OBSERVED.**
- No record carries a host path, user name, or wall-clock time by default. Time is opt-in (section 5).

## 3. Trace event schema

Recommended schema v1 extends the unit-trace file rather than introducing a new format. **PREDICTED.**

- Native stream: existing `unit`, `handoff`, `truncated`, `end` records (`unit-trace.md`), with `stream` and per-record `session` added.
- Fallback stream: existing `fallback`, `transfer`, `truncated`, `end` records, with per-record `session` added.
- New `mmio` and `event` kinds go in a host-written stream, not the generated C, so the native trace build stays byte-identical in the non-trace case. **PREDICTED.** (Byte-identical disabled path is **OBSERVED**, `unit-trace.md` "Enabling".)
- The joiner's sequence-gap and session-match rules apply per stream. A new joiner pass merges the host stream by `(session, globalRetired)`. **PREDICTED.**

Example (illustrative, not implemented):

```json
{"v":1,"seq":12,"stream":"host","kind":"mmio","session":"3f9c0a...","globalRetired":4821,"physAddr":"0x1F801070","width":4,"isWrite":true,"pc":"0x80012A44"}
```

`pc` on an MMIO record is optional because the native side does not report the access PC today. **OBSERVED:** `RuntimeHostRunMetrics` carries only the last address (`RuntimeHostDiagnostic.cs:146-160`), and `HostMmioAccess` (`MixedFallback.cs:161`) has no PC field.

Known semantic limits that the schema must carry, not hide: serviced-SYSCALL counting, PC after interrupt, termination 5 with fallback stop, SYSCALL in delay slot. **OBSERVED** (`unit-trace.md`, "Known semantic differences").

## 4. Linking to session ID and artifact ID

Existing identities and the proposed use of each:

| Identity | Existing field or source | Scope | Proposed use in trace |
|---|---|---|---|
| Session | `UnitTraceRun` GUID (`UnitTraceRun.cs:88`), passed as `PSX_TRACE_SESSION` | One run | `session` on every record; new optional `sessionId` in `report.json` (Q1) |
| Input identity | `ExecutionDiagnosticReport.InputSha256` (`ExecutionDiagnosticReport.cs:16`), set only on the `--report` path (`RunCommand.cs:54-58`) | One input file, same across rebuilds of the same input | Trace header field `inputSha256`, bare lowercase hex (the existing encoding); default-deny pending Q17 |
| Built-artifact identity | `ExecutionDiagnosticReport.ArtifactIdentity` (`ExecutionDiagnosticReport.cs:19`), set from `"sha256:" + hex` of the built artifact (`RunCommand.cs:191`) | One build output; changes whenever the build output changes | Trace header field `artifactSha256`, bare lowercase hex (prefix stripped) |
| Run ID | `runId` (`RuntimeHost/Program.cs:198`, also `:624`); `GuardStatsScope` run ID (`GuardStatsScope.cs:18`) | One run | Retire for trace purposes (Q2) |

**Encoding decision.** Both hash fields in the trace header are bare lowercase 64-character hex, with no `sha256:` prefix. `inputSha256` already has this form (`ExecutionDiagnosticReport.cs:67-75`). `artifactSha256` is taken from `ArtifactIdentity` with the literal `sha256:` prefix removed. The prefix is a presentation detail added at `RunCommand.cs:191`, so the trace stores the bare value and the report keeps its existing string unchanged. **PREDICTED.**

**Decision in this design.** No new `artifactId` field is added. The trace header carries the two hashes above. **PREDICTED.** Whether `artifactSha256` is stable across rebuilds of the same input is **UNVERIFIED** (depends on the build's determinism).

Linking rules (PREDICTED):

1. The parent generates `session` once. It goes into the environment (`PSX_TRACE_SESSION`), into every trace header and record, into `report.json` as an optional field, and into `diagnostics.log` lines as a context key.
2. A `Diagnostic` links to a trace by `DiagnosticEvidenceReference(kind=trace, identifier=session)`. A trace links back to its input and build by the header hashes above.
3. A join is rejected if `session` differs between streams. The unit trace already does this (`unit-trace.md`, "Identity").
4. The existing `ProductRevision` and `InputSha256` report fields are kept as they are. **CONFIRMED** (fields exist).

### 4.1 Standalone and packaged artifacts (ADR-027)

ADR-027 (`docs/adr/027-standalone-artifact-runtime-integration.md`, Status Accepted, Issue #739) has three layers, and the later layer supersedes the earlier one:

- **Decision and Step A scope (Phase 2 Step A):** the standalone artifact embeds its image and initial state and reads no input files, and `run --standalone` supplies the shared Runtime host from the CLI process. **OBSERVED** (`ADR-027:52` Decision). Step A scope, `:62-66`, lists "Host bundling" (`:62`) and "Direct launch" (`:64-66`) as not implemented in Step A.
- **Supersession (Step C), quoted in full:** "**Step C (PR #761) supersedes the above**: it implements host bundling, direct launch, and self-contained distribution as described in the Step C scope below." **OBSERVED** (`ADR-027:70-71`).
- **Step C packaged layout:** the Step C section (`ADR-027:128-175`) describes a distributed folder containing a native artifact plus `runtime/PSXRecomp.RuntimeHost[.exe]`, `runtime/runtime-host.json`, and `runtime/artifact-image.bin`. The host "reads `runtime/runtime-host.json`, locates the bundled `artifact-image.bin`, and runs the guest artifact through the shared Runtime" (`:137-139`). At 2e3ecad the manifest is at `:144`, `:154` and `:202`; the `artifact-image.bin` layout line is `:155`; and the image sidecar is `:203`. **OBSERVED** (`ADR-027:137-139`, `:144`, `:154`, `:155`, `:202`, `:203`).

So the Step A statement "reads no input files" holds only for user inputs (the game EXE or disc, which the packaged run does not take). It does not hold for the packaged layout, where the host reads a bundled image sidecar and a run-configuration file at runtime. **CONFIRMED** against the ADR text. The Step A `run --standalone` model is not the current model.

For a packaged run, the identities a trace can name are: the native artifact executable, `runtime/artifact-image.bin`, `runtime/runtime-host.json`, and the host binary. `InputSha256` is still produced only on the `--report` path (`RunCommand.cs:54-58`), and that path belongs to the `run` command, not to the packaged layout. Q3 asks which of these the trace carries.

## 5. Performance cost and sampling

What exists:

- Unit trace is opt-in and costs nothing when off (byte-identical generated source). Default output cap is set by `RECOMPILER_TRACE_DEFAULT_CAP` (`RecompilerHostCodeGen.cs:1162`); `unit-trace.md` states the default as 64 MiB. **OBSERVED.**
- The interpreter fetch ring is fixed-size (64 by default) and always on. **CONFIRMED** (`ExecutionTraceRing.cs:19`); its per-fetch cost was not measured. **UNVERIFIED.**
- Loaded-code mismatch comparison is bounded by `DefaultMaxComparedWords = 1<<24` and a record cap. **CONFIRMED** (`LoadedCodeMismatchDiagnostics.cs:25`).
- Fallback attribution runs per transition and only with an observer. **OBSERVED** (`docs/runtime/loaded-code-identity.md:51`).
- The full-RAM compare on every fallback return is the largest known avoidable cost on the mixed path. **INFERRED** from code (`docs/runtime/loaded-code-identity.md:83`, which says "INFERRED from code; not measured"). Not measured here.

Recommended policy (PREDICTED):

| Level | Trigger | What is recorded | Expected cost |
|---|---|---|---|
| L0 Off | default | Nothing new | 0 (must stay byte-identical) |
| L1 Boundary | `--trace` | Unit trace plus `handoff`, `transfer`, `stop`, `checkpoint` at existing boundaries | Per boundary; UNVERIFIED |
| L2 Event | `--trace=events` | Adds `event` and `mmio` records | Per MMIO access; UNVERIFIED, likely the dominant cost |
| L3 Sampled | `--trace=events --sample N` | Every N-th `mmio` or `event`, with a counter so drops are visible | Scales with 1/N; UNVERIFIED |

Sampling rules (PREDICTED):

- Sampling is deterministic (every N-th by `seq` within a stream), never random, so two runs of the same input are comparable.
- Every sampled-out record is counted in the next kept record (`skipped` field) so consumers know the trace is incomplete. Incomplete traces join as `Partial`, an existing status (`unit-trace.md`, "Joining").
- Caps are per stream, using the existing `truncated` record pattern.
- Diagnostics and `stop` records are never sampled out.

Measurements required before default levels are chosen: wall time and output bytes for L0, L1, and L2 on one packaged run. **UNVERIFIED** (no measurement in this work).

## 6. Crashes, sessions, artifacts

- A crash produces a `stop` record with the `Diagnostic` code and `session`, then the existing bundle is written with the trace files beside `report.json`. **PREDICTED.**
- A killed run leaves an unterminated trace. The existing rule (last partial line discarded, join `Partial`) covers this. **OBSERVED** (`unit-trace.md`, "No end line").
- The `#457` bundle is the shareable unit. Trace files join it only after the allowlist projection in section 7.2. **PREDICTED.**

## 7. Safety boundary: shareable output, local output, and the register dump ruling

### 7.1 Current state, by location

- Unit trace records contain CPU registers and PCs, not memory bytes. **OBSERVED** (`unit-trace.md`, "Record schema"). `CpuCheckpoint` has no memory field (`CpuCheckpoint.cs:17-25`), but it does carry `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc` (section 1.4). **CONFIRMED.**
- `ExecutionDiagnosticReport` has no field for input payloads, RAM, save data, or paths. **CONFIRMED** (`ExecutionDiagnosticReport.cs:8-10`). It does carry `ProductRevision`, `Revision`, and `FramebufferSha256` (section 7.6).
- `environment.json` is serialised whole into the shareable bundle (`ExecutionDiagnosticBundleWriter.cs:52`). It includes `OsVersion`, a free-text field (`ExecutionEnvironmentReport.cs:18`, `:39`). **CONFIRMED.** This is a shareable-path finding.
- The shareable diagnostic log admits an `opcode` key, which is a raw instruction word (`ExecutionDiagnosticLog.cs:28`), and the writer emits it (`ExecutionDiagnosticBundleWriter.cs:53`). The CLI passes an empty array, so the CLI bundle does not reach it today (`RunCommand.cs:201`). The writer API does. **CONFIRMED.**
- BIOS content diagnostic reports counts and provenance only. **CONFIRMED** (`BiosAddressGuard.cs:81`).
- **Exposure 1 (CONFIRMED):** `LoadedCodeMismatchDiagnostics` writes `windowAot`, `windowRam` (5 words each, `LoadedCodeMismatchDiagnostics.cs:296-297`), and `aotSha256` and `ramSha256` (`:293-294`). The window words are instruction words from the loaded image and from guest RAM. The comment calls the window bounded sampling. Bounded is not the same as redacted. Whether this output is ever published is **UNVERIFIED.**
- **Exposure 2 (CONFIRMED): raw instruction words in probe output.** The writers W1, W2, W3, W4, W8, W9, and W12 carry them (section 7.6 lists each occurrence): `stop.lastFetches` (`:1013`), `stop.lastTransfers` (`:1014`), `captured.word` (`:346`), `gte.lastUnsupportedCommand` (`:435`), `accounting.unsupportedOpcodes[].word` (`OpenBiosProbeAccounting.cs:197`), `ProbeGuestState.Describe().instructionWord` (`OpenBiosProbeDifferential.cs:101`), the MDEC `value` and `command` words (`OpenBiosProbeMdecLog.cs:43-56`), and the fallback export `ramWord` (`OpenBiosProbeCommand.cs:968`). Whether any of them comes from the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical; `BiosAddressGuard.PhysicalBase` at `BiosAddressGuard.cs:103`) is **UNVERIFIED.** The probe boots OpenBIOS, so BIOS-range fetches are PREDICTED.
- **Exposure 3 (CONFIRMED, local by current writer; not guaranteed; test pending):** `--dump-ram <path>` writes the raw 2 MiB main RAM to a user-named file (`OpenBiosProbeCommand.cs:337-342`). The file is not in the `--json` document and not in the four-entry `#457` bundle (`ExecutionDiagnosticBundleWriter.cs:18-21`). That is what the current writers do. Nothing enforces it. The leakage guarantee is **UNVERIFIED** until the test in section 7.5 exists.
- **Exposure 4 (CONFIRMED, owner ruling):** full register dumps. The owner ruling removes them from shareable output. They appear in: `stop.gpr`, `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`OpenBiosProbeCommand.cs:1010-1012`); `captured.gpr` (`:347`); the console `Stop:` line (`:446-449`); `--retire-out` lines (`:328-333`); `ProbeGuestState.Describe().cpu` (`OpenBiosProbeDifferential.cs:36`), and therefore `boundaries`, `firstInterrupt`, and `firstInterruptDifferential` (`OpenBiosProbeCommand.cs:726`, `:734`, `:735-736`); the checkpoint and fallback state objects (section 1.4); and `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (`OpenBiosBootMonitor.cs:40`). All of these are local only.

### 7.2 Shareable output is an explicit allowlist

**Rule.** A field is shareable only if it appears in this allowlist. A field not listed is excluded. Adding a field requires editing this list. The required test (section 7.5) will enforce the list once it is written. It does not exist yet. **PREDICTED.**

Each allowed class has one rule:

| Class | Rule | Allowed fields |
|---|---|---|
| PC (guest address) | 32-bit unsigned, written as `0x` plus 8 hex digits. It names where execution was, not what the bytes were. | `stop.pc`; `captured.pc`; `guestPc` (top level); `ExecutionDiagnosticReport.GuestPc`; `milestones.TitleEntryPc`; `ExecutionDiagnosticLog` `guestPc` and `guestAddress` context keys; `captured.lastTransfers` (PC-to-PC pairs only); `stop.lastTransfers` after removing `From.Word`; `LoadedCodeMismatchDiagnostics` `pc` and `windowStartPc`; `RuntimeHostDiagnostic` `FinalPc`, `LastAotBlockPc`, `TransferSourcePc`, `TransferTargetPc`; `RuntimeHostRunMetrics` `LastFallbackEntryPc`, `LastFallbackReturnPc`; `interpretedEntries` (an array of PCs, `:692`); `loadedImages[].skippedEntries` (PCs, `:690`); `referenceBoundariesMissing` (PCs, `:732`); the keys of `atBoundary` (`:694`) and `referenceSecondsAtBoundary` (`:733`); `stopAt.pc` (`:679`); `fallback-targets` `targets[].pc` and `lastReturnPc`, and `outcome.stopAt` (by writer rule, local, see W8) |
| Address (device or RAM location, not data) | 32-bit unsigned hex, as above. Names a location, not contents. | `RuntimeHostRunMetrics.LastMmioPhysicalAddress`; `LoadedCodeMismatchDiagnostics.ramPage` (a page index, written as hex) |
| Classification code | A value from a closed enum, or a registered `DiagnosticCode` (`^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*$`, `docs/architecture/diagnostics.md:55`). Never free text. | `diagnosticCode`; `executionState`; `kind`; `backend`; `engine`; `standalone`; `milestones` booleans (`ResetVectorFetched`, `KernelRunningFromRam`, `VectorsInstalled`, `ExceptionVectorExecuted`, `ShellEntered`); `bootVerified`, `kernelBooted`, `titleStarted`; `milestones.firstUnexpectedException.excCode` (exception code 0-31); `ExecutionDiagnosticReport` `Outcome`, `State`, `DiagnosticCode`; `RuntimeHostDiagnostic` `FallbackOutcome`, `FallbackRejectReason` (only if the writer enforces a closed set, else excluded), `ClassifiedExitCode`, `NativeTerminationReason`, `OrchestratorState`, `OrchestratorDiagnosticCode`, `TimedOut`, `FallbackEnabled`, `FallbackAttempted`, `TargetHasCompiledBlock`, `TargetInsideImage`, `LastMmioWasWrite`; trace `termination`, `status`; `milestoneComparison.match` (boolean) |
| Count | Non-negative integer. | `segmentsRetired`; `gte.commandsExecuted`; `build` counts (`imageInstructions`, `nativeInstructions`, `blocks`, `roots`, `staticFallbackTargets`, `symbols` as a count, `:681-689`); `loadedCodeVersions` (`:691`); `loadedImages[].blocks` and `nativeInstructions` (`:690`); `aot.generatedCBytes`, `aot.artifactBytes` (`:725`); `milestones.ShellEnteredAtFetch`, `milestones.InterruptEntries`, `milestones.SyscallEntries`; `milestones.firstUnexpectedException.atFetch`; `atBoundary` values `nativeInstructions`, `mmioRoundTrips`, `timeReportRoundTrips` (`:597-598`); `retired` (trace); `comparedWords`, `rejections`, `fullMatches`, `versionCount`, `version`, `ramHashChanges` (`LoadedCodeMismatchDiagnostics`); the range pairs `firstMismatchIndexRange`, `mismatchedWordsRange`; `RuntimeHostRunMetrics` `MmioAccesses`, `TimeReports`; `RuntimeHostDiagnostic` `AotRetiredInstructions`, `FallbackRetiredInstructions`, `FallbackTransitionCount`; `ExecutionDiagnosticReport.SegmentBudget`; `frame.nonZeroPixels` (count only, not the frame) |
| Whole-artifact digest | SHA-256, bare lowercase 64-character hex (section 4 encoding). Digests of a partial window, a region, RAM, VRAM, scratchpad, or any device memory are **not** allowed (section 7.3). Whether a digest of a user's copy of a licensed file is allowed is Q17. | Default-deny pending Q17: `ExecutionDiagnosticReport.InputSha256`, `ExecutionDiagnosticReport.ArtifactIdentity` (after prefix stripping), `discHash` context key (constant at `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:47`; producer at `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:391-451`, a firmware-distribution manifest, not a probe writer), `inputSha256` trace header field. Share-candidate: `artifactSha256` (the built artifact, a digest of this build's output, see Q17 for the rule that applies to it) |
| Duration | Milliseconds: the projection rounds to 0.1 ms. `timings` (`OpenBiosProbeCommand.cs:712-724`) is already rounded by the writer, and the projection rounds it again, which leaves it unchanged. `precompileMilliseconds` (`:693`) is not rounded by the writer, so the projection rounds it. Seconds: the projection rounds to 0.1 s (`atBoundary.seconds`, `:597`). Wall clock is not written as a timestamp. | `RuntimeHostRunMetrics.WallMilliseconds`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds` (`RuntimeHostDiagnostic.cs:146-150`); probe `timings` (`OpenBiosProbeCommand.cs:712-724`, read, rounded to 0.1 ms); `precompileMilliseconds` (`:693`, rounded by the projection); `atBoundary` values `seconds` (`:597`, rounded to 0.1 s by the projection) |
| Identifier | Session ID (section 4), 32-hex GUID. | `session` (new per-record field, section 2) |

Fields that are default-deny (not allowed until a decision or a read) are in section 7.6, under "default-deny".

### 7.3 Excluded fields (shareable output must not contain these)

| Excluded class | Fields | Why |
|---|---|---|
| General registers (owner ruling) | `stop.gpr` (`OpenBiosProbeCommand.cs:1012`); `captured.gpr` (`:347`); `gpr[32]` in every `checkpoint`; `gpr` in the native `unit` state and fallback `entry`/`exit` objects (`unit-trace.md`); `retire-out` `gpr` (`:328-333`); `ProbeGuestState.Describe().cpu` GPR entries (`OpenBiosProbeDifferential.cs:36`); console `Stop:` line (`:446-449`); `boundaries` (`OpenBiosProbeCommand.cs:726`, each value a `Describe()` with the `cpu` map); `firstInterrupt` (`:734`, both `Describe()` objects); `firstInterruptDifferential` (`:735-736`, a `Compare` result with `cpu` mismatch values); `differential` (`:727-731`, `Compare` results with `cpu` mismatch values) | Full register dump. Local only (owner ruling). |
| COP0 state (owner ruling) | `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`:1010-1011`); `sr`, `cause`, `epc` in every `checkpoint` and fallback state; `ProbeGuestState.Describe()` `sr`, `cause`, `epc` (`OpenBiosProbeDifferential.cs:95-107`) and `cpu` COP0 entries (`:36`); `badvaddr` (`:36`); `retire-out` `sr`, `cause`, `epc` (`:328-333`); `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (`OpenBiosBootMonitor.cs:40`); `boundaries`, `firstInterrupt`, `firstInterruptDifferential` (the same `Describe()` and `Compare` objects, `:726`, `:734`, `:735-736`) | COP0 full state. Local only (owner ruling). |
| HI and LO | `hi`, `lo` in checkpoint and state objects; `ProbeGuestState.Describe().cpu` entries `hi` and `lo` (`OpenBiosProbeDifferential.cs:36`); `retire-out` `hi`, `lo` (`:328-333`) | Part of the register dump. Excluded as a register dump. **PREDICTED** classification, pending Q13. |
| Raw instruction words | `captured.word` (`OpenBiosProbeCommand.cs:346`); `stop.lastFetches` (`:1013`); `stop.lastTransfers` `From.Word` (`:1014`); `gte.lastUnsupportedCommand` (`:435`); `accounting.unsupportedOpcodes[].word` (`OpenBiosProbeAccounting.cs:197`); `accounting.unsupportedOpcodes[].opcode` (`:197`), since it is the instruction's opcode field; `ProbeGuestState.Describe().instructionWord` (`OpenBiosProbeDifferential.cs:101`); `ExecutionDiagnosticLog` `opcode` context key (`ExecutionDiagnosticLog.cs:28`); MDEC `value` and `command` (`OpenBiosProbeMdecLog.cs:43-56`); `mmio.value`; `TransferOpcode` (`RuntimeHostDiagnostic`); the fallback export `ramWord` (`OpenBiosProbeCommand.cs:968`); `execution.topFallbackTargets` strings (`:704-705`, which carry the cause text and counts, see default-deny) | Instruction encoding from guest memory. Excluded from shareable output. |
| RAM contents | `windowAot`, `windowRam` (`LoadedCodeMismatchDiagnostics.cs:296-297`); `OpenBiosProbeDifferential` RAM context `interpreterBytes`, `hostBytes` (`:138-143`); `--dump-ram` file (W7); any memory bytes; any run of 8 or more consecutive hex words | Raw loaded-image and guest-RAM words. |
| Whole-RAM, VRAM, and device digests (owner direction) | `ramSha256` (`LoadedCodeMismatchDiagnostics.cs:294`; `OpenBiosProbeDifferential.cs:104`); `interpreterRamSha256`, `hostRamSha256` (`:154-155`); `vram_sha256` (`:83`); `scratchpad_sha256` (`:41`, `:106`) | A digest of RAM or device memory is a region digest, and is banned by the RAM-region rule in section 7.3. This is a banned class, not a default-deny. |
| Free text | `diagnosticMessage` (`OpenBiosProbeCommand.cs:677`); `fetchIndices` (`:672`, a constant explanation string); `OsVersion` (`ExecutionEnvironmentReport.cs:18`, `:39`); `Title` (`ExecutionDiagnosticReport.cs`); `FailureExceptionType` (`RuntimeHostDiagnostic`); `FallbackRejectReason` unless a closed set is enforced; the stdout summary text of W12 (`:859-874`); the stderr text of W13 | Free text is not a classification. It can carry host, title, or path content. |
| Symbol, file, and disc names | `accounting.hotSymbols` symbol names; `hotEntries[].symbol` and `hotPcs[].symbol` (`OpenBiosProbeCommand.cs:861`, `:866`); `loadedImages[].name`; `--list-disc` file names (W11) | User-supplied text (a symbol file or the user's disc). Not a classification. |
| Host and environment identity | `environment.json` fields `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, `TargetArchitecture`, `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion` (all single-line free text: `ExecutionEnvironmentReport.cs:26-34`, `:70-74`; `ValidateSingleLine` at `:79-87` only rejects CR and LF and trims); `ProductRevision`, `Revision` (default-deny, Q15); the `runId` value, which is host run identity (Q2) | Host identity. Not a trace field. |
| Trace-only values | `t` timestamps (default null; Q10); `skipped` counts are allowed as counts | Timestamps are opt-in and excluded from share-ready bundles (Q10). |
| Device register values and device digests | The `devices` map of `ProbeGuestState.Describe()` (`OpenBiosProbeDifferential.cs:100`), built from `CaptureDevices` (`:37`), with `scratchpad_sha256` (`:41`), `gpustat` (`:80`), `vram_sha256` (`:83`), and the `cd_*` entries (`cd_index`, `cd_status`, `cd_if`, `cd_ie`, `cd_responses`, `cd_data_bytes`, `cd_last_command`, `cd_reading`, `cd_mode`, `cd_irq_generation`, `:87-89`); the `devices` mismatch objects of `Compare` (`:119`) | Device register state is local (the same class as the boundary-log device registers, W10). Device digests are banned by the digest rule (section 7.3, whole-RAM, VRAM, and device digests). |
| Frame pixels | `--dump-frame` PPM (W6); `frame` pixel content; the `frame` digest (default-deny under Q14, section 7.6) | Pixel content is game output. Local only. |
| Disc and program contents | `--list-disc` (W11) and `--fallback-targets` (W8) contents beyond the classes above | Derived from the user's disc. Local only. |

### 7.4 Local/share boundary (rule)

1. **Shareable output** is produced only by one writer, the shareable projection. It builds a document from the allowlist in section 7.2 and nothing else. A document is shareable only when this projection built it. **PREDICTED.**
2. **Local output** is any other output. The writers W1-W13 of section 1.8, the trace files, `environment.json` as currently written, the mismatch report as currently written, and the `--json` documents of the probe are all local. Local output may contain any field, including the excluded classes of section 7.3. **CONFIRMED** (these outputs exist and contain those fields today).
3. A local output must not be attached to a bundle, a report, or an issue. The `#457` bundle writer accepts only shareable-projection documents. **PREDICTED** (the writer change does not exist; today the bundle writer takes typed records and does not check provenance).
4. **A local-only classification is not a guarantee of non-leakage.** Calling an output "local" is a label. It stops leakage only if the shareable projection is tested to emit nothing outside the allowlist, and the bundle writer is tested to refuse non-projection input. Until the tests in section 7.5 exist and pass, the leakage guarantee is **UNVERIFIED**.

### 7.5 Required tests and the fixtures they use

Fixtures are not written from section 7.3. Each fixture is generated by calling the real writer under test on a synthetic run, so the test covers what the writer actually emits. **PREDICTED** (none of these tests exist).

Required tests:

1. **`ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_field`** (new file under `src/PSXRecomp.Tests/Diagnostics/`). Fixtures: run the probe document builder (W1 and W2 code paths), the summary path (W12), `ProbeGuestState.Describe()` and `Compare`, `OpenBiosProbeAccounting`, `OpenBiosBootReport` (milestones), `RuntimeHostDiagnostic` and `RuntimeHostRunMetrics`, `ExecutionEnvironmentReport`, `ExecutionDiagnosticReport`, and `LoadedCodeMismatchDiagnostics.ToJsonNode()`. Feed their outputs to the projection. The test checks the projection's output, not the fixture.
2. **`ShareableOutputAllowlistTests.Shareable_writer_keys_are_allowlisted`.** Walks the projection's output and fails on any key path not in the section 7.2 allowlist.
3. **`ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_key_or_value`.** Applies the matching rules below to the projection's output.
4. **`ShareableOutputAllowlistTests.Bundle_writer_refuses_local_output`.** Asserts `ExecutionDiagnosticBundleWriter.Write` refuses a document that the projection did not build.
5. **`ExecutionDiagnosticBundleWriterTests.Bundle_writer_drops_opcode_context_key`** (required design change C2). Builds a bundle from a diagnostic carrying an `opcode` context key, and asserts the `diagnostics.log` entry contains no `opcode=` text and no value from that key. The writer drops the key: keys not on the allow-list are removed by the filter at `ExecutionDiagnosticLog.cs:78-79`, against the allow-list at `:17-30`, and the `opcode` key is removed from the allow-list. This is a drop, not a rejection, so the test name and this description both say "drops". **PREDICTED.**
6. **`WriterInventoryTests.Probe_writers_match_section_1_8`.** Greps the probe sources for `output.WriteLine`, `error.WriteLine`, `Console.Error`, and the file-writing calls (the search in section 1.8) and fails if a call is not listed as a W-row or inside the W13 line range. This is the test that makes the completeness claim of section 1.8 checkable. **PREDICTED.**

**Matching rules for test 3.**

- **Key match:** a key is excluded if it matches, case-insensitively, any term in this explicit list, as a substring of the key: `word`, `gpr`, `hi`, `lo`, `sr`, `cause`, `epc`, `badvaddr`, `opcode`, `bytes`, `ram`, `vram`, `scratchpad`, `window`, `dump`, `pixel`, `message`, `symbol`, `name`, `boundaries`, `firstinterrupt`. The substring match is a second line of defence. Test 2 is the primary check, because the allowlist is exact.
- **Hex-run rule:** a value fails if it is an array of 8 or more consecutive strings that each match `^0x[0-9A-Fa-f]{1,8}$`, or a single hex string of 32 or more bytes (64 or more hex digits) that is not a whole-artifact digest in the allowlist.
- **Free-text rule:** any string value that is not a classification code, a PC, an identifier, or a digest in the allowlist fails.

The leakage guarantee in section 7.4 stays **UNVERIFIED** until tests 1 through 6 exist and pass.

### 7.6 Writer and field classification

Each writer and each field is labelled local or share-candidate. "Share-candidate" means its fields pass the allowlist if the projection emits them. "Default-deny" means it is not allowed until the stated decision or read. "Banned" means it is excluded by a rule in section 7.3 and never emitted. Unread shapes are default-deny (Q16).

**Writers.**

| Writer | Location | Content | Label |
|---|---|---|---|
| W1 Interpreter probe `--json` | `OpenBiosProbeCommand.cs:419` (write), `:421-437` (object) | Mixed; see the W1 field table | Local |
| W2 Generated-host `--json` | `OpenBiosProbeCommand.cs:663-742` (object), `:743-746` (serialise), `:755` (write) | Mixed; see the W2 field table | Local |
| W3 Console output, interpreter path | `OpenBiosProbeCommand.cs:441-450` | Full GPRs and COP0 (`Stop:`); firmware digest (`:443`); milestones | Local |
| W4 `--retire-out` | `OpenBiosProbeCommand.cs:317`, `:328-333` | Full GPR, hi, lo, COP0 per fetch | Local (banned classes) |
| W5 `--pc-histogram` | `OpenBiosProbeCommand.cs:368-377` | `totalRamFetches`, PCs with counts, bucket starts with counts | Share-candidate fields (PC and count), local by writer; file comment says "Local diagnostic only" |
| W6 `--dump-frame` | `OpenBiosProbeCommand.cs:378-411` | Framebuffer pixels (PPM) | Local; pixels excluded (section 7.3) |
| W7 `--dump-ram` | `OpenBiosProbeCommand.cs:81`, `:172`, `:337-342` | Raw 2 MiB RAM | Local; banned |
| W8 `--fallback-targets` | `OpenBiosProbeCommand.cs:660`, `:952-977` | See the W8 field table | Local |
| W9 `--mdec-events` | `OpenBiosProbeMdecLog.cs:43-56` | Fields per line (`OpenBiosProbeMdecLog.cs:50-53`): `engine` (string, classification by the caller's engine name), `seq` and `g` (counts), `kind` (MDEC event kind name, classification), `value` (raw word, banned), `pc` and `lastFallbackFetchPc` (PCs or null), `command` (raw word, banned), `remaining` and `fifoWords` (counts), `control` (device register value, excluded under the device-register row of section 7.3), `busyBefore` and `busyAfter` (booleans) | Local. Share-candidates by writer rule: `engine`, `seq`, `g`, `kind`, `pc`, `lastFallbackFetchPc`, `remaining`, `fifoWords`, `busyBefore`, `busyAfter`. Excluded: `value`, `command`, `control`. |
| W10 `--boundary-log` | `OpenBiosProbeBoundaryLog.cs:40-56` | Device registers, `vramSha256` (banned), boundary PCs | Local; VRAM digest banned; device register values local |
| W11 `--list-disc` | `OpenBiosProbeCommand.cs:251-289` | Disc file names | Local; file names excluded (section 7.3) |
| W12 Non-JSON summary | `OpenBiosProbeCommand.cs:747-751`, `Summarize` at `:838-884` | Hot entries with symbol names (`:861`), hot PCs with symbol names (`:866`), the differential result and `firstMismatch` (`:874`), timings (`:879-881`, durations), AOT byte counts (`:882`, counts), `consistency.differentialPass` (`:883`, a boolean inside the default-deny `consistency` object) | Local; symbol names and mismatch detail excluded; PCs, counts, and durations share-candidate by writer rule |
| W13 stderr (35 `error.WriteLine` calls, `OpenBiosProbeCommand.cs:35-657`) | Full list in section 1.8 W13 | Usage and argument echoes (argv tokens, `:52`, `:63`, `:77`, `:102`, `:108`), local paths (`:154`, `:242` `ex.Message`), failure text (`:233`, `:235`), progress with seconds and counts (`:529`, `:608`, `:615`), status (`:651`, `:657`) | Local; free text, excluded |
| Trace files (native `<path>`, fallback `<path>.fallback`) | `unit-trace.md`; `RecompilerHostCodeGen.cs` | Unit and fallback records; CPU state with `gpr` | Local; PC and count fields share-candidates by writer rule |
| Checkpoint records | `CpuCheckpoint.cs:17` | `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `pc` | Local only (owner ruling) |
| Loaded-code mismatch report (`#773`) | `LoadedCodeMismatchDiagnostics.cs:277-300`; written by `Program.cs:624` path | See field table | Local (banned fields present) |
| Runtime host diagnostic and metrics (`#763`) | `RuntimeHostDiagnostic.cs:24`, `:146-160` | See field table | Local until projected |
| `report.json` (`#457`) | `ExecutionDiagnosticBundleWriter.cs:51`; `ExecutionDiagnosticReport.cs:14-25` | See field table | Share-candidate fields; written to the bundle without a projection today |
| `environment.json` (`#457`) | `ExecutionDiagnosticBundleWriter.cs:52`; `ExecutionEnvironmentReport.cs:11-45` | See field table | Excluded: `OsVersion` and every other single-line free-text field (no closed set exists); written whole today |
| `diagnostics.log` (`#457`) | `ExecutionDiagnosticBundleWriter.cs:53`; `ExecutionDiagnosticLog.cs:17-30` | Context keys, including `opcode` | Share-candidate after the C2 change |
| `issue.md` (`#457`) | `ExecutionIssueMarkdown.cs:36` | Derived from `report` | Same classes as `report.json` |

**W1 field table (`OpenBiosProbeCommand.cs:421-437`).**

| Field | Class | Label |
|---|---|---|
| `kind`, `backend`, `executionState`, `titleStarted`, `bootVerified`, `kernelBooted`, `diagnosticCode` | Classification | Share-candidate |
| `guestPc` | PC | Share-candidate |
| `segmentsRetired` | Count | Share-candidate |
| `sha256` (`:423`, firmware digest) | Firmware digest | Default-deny pending Q12 |
| `stop` (`:432`) | Mixed (see the `stop` rows below) | Mixed |
| `captured` (`:433`) | Mixed (see the `captured` rows below) | Mixed |
| `gte` (`:435`) | `commandsExecuted`: count; `lastUnsupportedCommand`: raw instruction field | `commandsExecuted`: share-candidate; `lastUnsupportedCommand`: excluded |
| `frame` (`:437`): `width`, `height`, `nonZeroPixels` | Counts | Share-candidate |
| `frame.sha256` (`:437`) | Frame digest, not a whole-artifact digest | Default-deny pending Q14 |
| `milestones` (`OpenBiosBootMonitor.cs:10-40`) | Booleans, counts, `TitleEntryPc` (PC), `FirstUnexpectedException` (below) | Booleans and counts share-candidate; `TitleEntryPc` share-candidate (PC); `firstUnexpectedException.excCode` and `.atFetch` share-candidate; `.epc`, `.badVAddr`, `.cause` excluded (COP0) |

**`stop` rows (`OpenBiosProbeCommand.cs:1004-1014`).**

| Field | Class | Label |
|---|---|---|
| `stop.pc` | PC | Share-candidate |
| `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` | COP0 | Excluded (owner ruling) |
| `stop.gpr` | General registers | Excluded (owner ruling) |
| `stop.lastFetches` | Raw instruction words | Excluded |
| `stop.lastTransfers` | PC-to-PC pairs with `From.Word` | PCs share-candidate; `From.Word` excluded |

**`captured` rows (`OpenBiosProbeCommand.cs:344-350`).**

| Field | Class | Label |
|---|---|---|
| `captured.pc` | PC | Share-candidate |
| `captured.word` | Raw instruction word | Excluded |
| `captured.gpr` | General registers | Excluded (owner ruling) |
| `captured.lastTransfers` | PC-to-PC pairs | Share-candidate |

**W2 field table (`OpenBiosProbeCommand.cs:663-742`).**

| Field | Location | Class | Label |
|---|---|---|---|
| `kind`, `engine`, `standalone` | `:665-667` | Classification | Share-candidate |
| `sha256` | `:668` (`probe.Hash`) | Firmware digest | Default-deny pending Q12 |
| `bootVerified`, `kernelBooted`, `titleStarted` | `:669-673` | Classification | Share-candidate |
| `milestones` | `:671` | As the milestones rows in the W1 table | Mixed (classes per W1) |
| `fetchIndices` | `:672` | Constant explanatory string | Excluded (free text) |
| `executionState`, `diagnosticCode` | `:674`, `:676` | Classification | Share-candidate |
| `guestPc` | `:675` | PC | Share-candidate |
| `diagnosticMessage` | `:677` | Free text | Excluded |
| `stopAt` | `:678-680` | `pc` (PC); `reachedByInterpreter`, `reachedByHost` (booleans) | Share-candidate |
| `build` | `:681-689` | Counts (see section 7.2) | Share-candidate |
| `loadedImages[]` | `:690` | `name` excluded; `blocks`, `nativeInstructions` counts; `skippedEntries` PCs | Mixed |
| `loadedCodeVersions` | `:691` | Count | Share-candidate |
| `interpretedEntries` | `:692` | PC array (`observedPcs`) | Share-candidate (PC class) |
| `precompileMilliseconds` | `:693` | Duration (`buildMs + compileMs`, rounded by the projection) | Share-candidate |
| `atBoundary` | `:694` (from `:550`, `:597-598`) | Keys: PC. Values: `nativeInstructions`, `mmioRoundTrips`, `timeReportRoundTrips` (counts), `seconds` (duration in seconds, rounded by the projection) | Share-candidate (PC key, counts, duration) |
| `execution` | `:695-709` | Native and fallback counts; `topFallbackTargets` strings (PC, counts, cause text); `timings = costs` (`:706`, shape not read) | Default-deny until read (whole object) |
| `accounting` | `:710-711` | See the `accounting` row in section 7.6 classes below | Mixed |
| `timings` | `:712-724` | Rounded durations (0.1 ms, read) | Share-candidate (duration class) |
| `aot` | `:725` | `generatedCBytes`, `artifactBytes`: sizes (counts) | Share-candidate |
| `boundaries` | `:726` (`Describe()` per PC) | Excluded (cpu map, `instructionWord`, `ramSha256`, `scratchpadSha256`, `sr`/`cause`/`epc`, and the `devices` map of `OpenBiosProbeDifferential.cs:100`, see the device row in section 7.3); residual `pc`, `atFetch`, `guestCycles` share-candidate by field rule | Mixed; the object as a whole is local |
| `differential` | `:727-731` (`Compare` per PC, or a string) | Mismatch objects carry register and RAM values (excluded) | Local |
| `referenceBoundariesMissing` | `:732` | PC array | Share-candidate (PC class) |
| `referenceSecondsAtBoundary` | `:733` | PC-keyed seconds (value type not read here) | Default-deny until the value type is read |
| `firstInterrupt` | `:734` | `interpreter` and `host`: `Describe()` objects | Local (cpu map, `instructionWord`, and the `devices` map of `OpenBiosProbeDifferential.cs:100` excluded, see the device row in section 7.3); residual `pc`, `atFetch`, `guestCycles` share-candidate by field rule |
| `firstInterruptDifferential` | `:735-736` | `Compare` result (register and RAM mismatch values), or a string | Local |
| `milestoneComparison` | `:737-741` | `match` (boolean); `interpreter` (milestones object, classes per W1) | `match`: share-candidate; `interpreter`: as milestones |
| `consistency` | `:744` (from `Consistency(node, boundaryPcs, probe.StopAt)`) | Built from the document; shape not read | Default-deny until read |

**W8 fallback export fields (`OpenBiosProbeCommand.cs:961-977`).**

| Field | Class | Label |
|---|---|---|
| `format` | Classification (`ObservedRootPlanner.TargetFormat`) | Share-candidate |
| `sha256` (`:973`, the firmware digest `hash`) | Firmware digest | Default-deny pending Q12 |
| `run` (`:974`) | `ObservedRootPlanner.RunDocument(DescribeRun(...))` (`ObservedRootPlanner.cs:46-51`, `:254`, `:262`): the firmware digest, the manifest identity, the disc digest, and `probeOptionsSha256`, a digest of `--roots`, `--code-bytes`, `--segments`, and `--segment-budget` (`ProbeOptionsSha256` at `ObservedRootPlanner.cs:254`). The whole object is one unit. | Default-deny: the object carries firmware and disc digests (Q12, Q17). The probe-options digest is inside the same object and is default-deny with it. |
| `outcome` (`:975`): `state` (classification), `complete` (boolean), `stopAt` (PC) | Mixed | `state`, `complete`: share-candidate; `stopAt`: share-candidate (PC) |
| `targets[]` (`:961-969`): `pc`, `lastReturnPc` (PC); `cause` (classification); `entries`, `instructions` (counts); `ramWord` (raw RAM word) | Mixed | `pc`, `lastReturnPc`, `cause`, `entries`, `instructions`: share-candidate by writer rule, local by writer; `ramWord`: banned |

**Accounting (`OpenBiosProbeAccounting.cs:195-198`; the W2 `accounting` object at `:710-711`).**

| Field | Class | Label |
|---|---|---|
| `unsupportedOpcodes[].excCode` | Classification | Share-candidate |
| `unsupportedOpcodes[].pc` | PC | Share-candidate |
| `unsupportedOpcodes[].atFallbackFetch` | Count | Share-candidate |
| `unsupportedOpcodes[].word`, `.opcode` (both on `OpenBiosProbeAccounting.cs:197`) | Raw instruction fields | Excluded |
| `hotSymbols` symbol names | Symbol names | Excluded |
| Other `accounting` fields | Shape not read | Default-deny until read (Q16) |

**`milestones` detail (`OpenBiosBootMonitor.cs:10-40`).** The shape is read from the record declaration (not from a serialised sample). The per-field classes are those in the W1 table above.

**`environment.json` fields (`ExecutionEnvironmentReport.cs:11-45`).**

| Field | Class | Label |
|---|---|---|
| `Schema` | Classification | Share-candidate |
| `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, `TargetArchitecture` | Free text: `ValidateSingleLine` (`ExecutionEnvironmentReport.cs:79-87`) rejects CR and LF and trims, so any single-line string is accepted. No closed set exists. | Excluded unless a closed set is added (Q15) |
| `OsVersion` | Free text (`:18`, `:39`) | Excluded |
| `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion` | Build and tool identifiers | Default-deny pending Q15 |

**`report.json` fields (`ExecutionDiagnosticReport.cs:14-25`).**

| Field | Class | Label |
|---|---|---|
| `Schema` | Classification | Share-candidate |
| `ProductRevision`, `Revision` | Build identifiers | Default-deny pending Q15 |
| `InputSha256` | Game-input digest | Default-deny pending Q17 |
| `Title` | Free text (game title) | Excluded |
| `ArtifactIdentity` | Digest of the built artifact, with `sha256:` prefix | Share-candidate (prefix stripped, section 4) |
| `Outcome`, `State` | Classification | Share-candidate |
| `GuestPc` | PC | Share-candidate |
| `DiagnosticCode` | Classification | Share-candidate |
| `SegmentBudget` | Count | Share-candidate |
| `FramebufferSha256` | Frame digest | Default-deny pending Q14 |

**Loaded-code mismatch report fields (`LoadedCodeMismatchDiagnostics.cs:277-300`).**

| Field | Class | Label |
|---|---|---|
| `pc`, `windowStartPc` | PC | Share-candidate |
| `version`, `versionCount`, `comparedWords`, `rejections`, `fullMatches`, `ramHashChanges` | Counts | Share-candidate |
| `ramPage` | Address (page index) | Share-candidate |
| `firstMismatchIndexRange`, `mismatchedWordsRange` | Count ranges | Share-candidate |
| `aotSha256` | Digest of AOT words for a version, a region digest | Banned |
| `ramSha256` | RAM digest | Banned |
| `windowAot`, `windowRam` | Raw words | Banned |
| `origins` | `OriginsJson()`, shape not read | Default-deny until read |

**Runtime metrics and host diagnostic (`RuntimeHostDiagnostic.cs:146-160` and the record at `:24`).**

| Field | Class | Label |
|---|---|---|
| `WallMilliseconds`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds` | Durations | Share-candidate (rounded by the projection) |
| `MmioAccesses`, `TimeReports` | Counts | Share-candidate |
| `LastFallbackEntryPc`, `LastFallbackReturnPc` | PC | Share-candidate |
| `LastMmioPhysicalAddress` | Address | Share-candidate |
| `LastMmioWasWrite` | Classification | Share-candidate |
| `FallbackInFlight`, `FallbackReturns` | Shapes not read | Default-deny until read |
| `FinalPc`, `LastAotBlockPc`, `TransferSourcePc`, `TransferTargetPc` | PC | Share-candidate |
| `TransferOpcode` | Raw instruction field | Excluded |
| `FallbackTargets` (list of `MixedFallbackTarget`) | Shape not read | Default-deny until read |
| `FailureExceptionType` | Type name (free text) | Excluded unless a closed set is enforced |
| `FallbackOutcome`, `OrchestratorState`, `OrchestratorDiagnosticCode`, `NativeTerminationReason`, `ClassifiedExitCode` | Classification | Share-candidate |
| `FallbackRejectReason` | Free text unless a closed set is enforced | Excluded unless the writer enforces a closed set |
| `AotRetiredInstructions`, `FallbackRetiredInstructions`, `FallbackTransitionCount` | Counts | Share-candidate |
| `TimedOut`, `FallbackEnabled`, `FallbackAttempted`, `TargetHasCompiledBlock`, `TargetInsideImage` | Booleans | Share-candidate |

**Trace records (`unit-trace.md`).** `pc`, `next`, `seq`, `retired`, `termination`, `status`, `handoff`, `start`, `end`, `native_total`: share-candidate. `gpr`, `hi`, `lo`, `sr`, `cause`, `epc`, `state`, `entry`, `exit`: local only (owner ruling).

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, and `event` kinds. Do not invent a second format. **PREDICTED** (rationale: the existing joiner, tests, and parsers already implement the rules).
2. Make `session` the single run identity for trace purposes. Retire `runId` (`RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them only as aliases. **PREDICTED.**
3. Reuse the existing `artifactSha256` and `inputSha256` fields, both as bare lowercase hex. `inputSha256` stays default-deny pending Q17. Add no new artifact ID field (section 4). **PREDICTED.**
4. **Required design changes, before any shareable output or bundle:**
   - **C2:** remove the `opcode` key from `ExecutionDiagnosticLog`'s allow-list (`ExecutionDiagnosticLog.cs:28`). The writer then drops it (test 5 of section 7.5, `Bundle_writer_drops_opcode_context_key`). Update `ExecutionDiagnosticLogTests.cs:37` (the `InstructionOpcode` context entry) and `:48` (the expected `opcode=0xDEADBEEF` in the formatted line).
   - **M2:** `environment.json` is removed from the shareable bundle, or projected to the allowlist with `OsVersion` removed (`ExecutionDiagnosticBundleWriter.cs:52`). `ProductRevision`, `Revision`, and the tool identifiers are default-deny until Q15. `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, and `TargetArchitecture` are free text (single-line only, `ExecutionEnvironmentReport.cs:79-87`) and are excluded unless a closed set is added.
   - **M4 and F-series:** the firmware digest (`OpenBiosProbeCommand.cs:423`, console `:443`, fallback export `:973`, `run` at `:974`) is default-deny until Q12. `frame.sha256` (`:437`) and `FramebufferSha256` are default-deny until Q14. The game-input digests are default-deny until Q17. Whole-RAM, VRAM, and device digests are banned (section 7.3).
   - **Unread shapes:** `execution`, `consistency`, `referenceSecondsAtBoundary`, `accounting` (other than the classified fields), and the runtime fields in section 7.6 are default-deny until read (Q16).
   - **M6:** tests that pin raw output must change with the fixes:
     - `OpenBiosProbeAccountingTests.cs:183` (`word`) and `:185` (`opcode`) pin the raw fields of `unsupportedOpcodes`. They stay on the local writer, and the projection test asserts absence.
     - `LoadedCodeMismatchDiagnosticsTests.cs:34-35` pin `windowAot` and `windowRam`. They stay on the local writer, and the projection test asserts absence.
     - `ExecutionDiagnosticLogTests.cs:37` and `:48` pin the `opcode` key, as above.
   - **Exposure 3 (`--dump-ram`):** keep local. Test 4 of section 7.5 and the projection tests cover it.
5. Build the shareable projection (section 7.2) and the required tests (section 7.5) before any trace or bundle is shared. **PREDICTED.**
6. Ship L1 (boundary) first. Its cost is near zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **PREDICTED.**

**Blocker (stated, not fixed in this design).** The shareable projection cannot be finished until the default-deny fields are read and their owners decide them. The unread shapes are `execution` (including `timings = costs`), `consistency`, `referenceSecondsAtBoundary` (value type), `accounting` (other fields), `origins`, `FallbackInFlight`, `FallbackReturns`, `FallbackTargets`. This is a read-and-decide task, not a design gap. It is recorded as a blocker for the implementation phase, not for this design. **OBSERVED** (the shapes were not read in this work).

## 9. Open questions and the decision each needs

Ownership note: the GitHub API shows no assignee on any issue read for this document (#45, #457, #49, #763, #773, #40, #42), and ADR-027 has no owner field. So no per-issue owner is recorded for any question below. **OBSERVED.** The decision-maker named in each row is the repository owner (mao2009). Closed items are named as such. Each row names an open tracking item where one exists.

| # | Question | Decision point | Tracking item | Evidence label |
|---|---|---|---|---|
| Q1 | Is `session` a new optional field in `report.json` (schema bump to `v2`) or an optional addition within `v1`? | Repository owner (mao2009) | #457 is closed, so the report schema has no open tracking issue. Open item for this decision: #45. | CONFIRMED that v1 has no session field; OBSERVED (#457 closed) |
| Q2 | Retire the standalone `runId` (`Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them as aliases? | Repository owner (mao2009) | #763 (open) and #773 (open) | CONFIRMED both exist and both are written |
| Q3 | For packaged artifacts (section 4.1), which identities does the trace header carry? Candidates: the native artifact executable hash, the `runtime/artifact-image.bin` sidecar hash (the image the host runs), the `runtime/runtime-host.json` hash, and `inputSha256` (only on the `run --report` path, `RunCommand.cs:54-58`). Should `inputSha256` be produced for packaged runs at all? | Repository owner (mao2009) | #739 (open; ADR-027 status Accepted, Issue #739) | OBSERVED (ADR-027:52, :62-66, :70-71, :128-175); CONFIRMED (`RunCommand.cs:54-58`) |
| Q4 | Are `windowAot`/`windowRam` (and `aotSha256`, `ramSha256`) in the loaded-code report kept in the local file? Under section 7.2 they are banned from shareable output. The remaining question is whether the local file keeps them. | Repository owner (mao2009) | #773 (open). The fix PR (#786) is closed. | CONFIRMED the words and digests are written |
| Q5 | Which trace level is the default for `PSXRecomp.RuntimeHost --diagnostics`? | Repository owner (mao2009) | #763 (open) | OBSERVED that the flag is off by default |
| Q6 | Is MMIO `value` ever allowed outside a local-only trace? Design position: no. | Repository owner (mao2009) | #45 (open). #457 is closed. | PREDICTED position |
| Q7 | Are GPU, SPU, CD, and DMA events in trace schema v1, or added in a later version? The #45 body lists them under "対象" (Scope) (section 0.1). | Repository owner (mao2009) | #45 (open) | OBSERVED (#45 body, section 0.1) |
| Q8 | What is the acceptable overhead budget for L2, for example percent of wall time on one standard run? | Repository owner (mao2009) | #45 (open; completion criterion "Performance overheadとsampling方針を定義") | UNVERIFIED (no budget in repo found in this work) |
| Q9 | Should the Studio GUI and CLI read the same JSON Lines files, or a joined summary only? | Repository owner (mao2009) | #45 (open; completion criterion "GUI/CLIで共通利用できる形式を定義"); #40 and #42 (both open) | OBSERVED (#45 body); UNVERIFIED (GUI code not searched) |
| Q10 | May trace timestamps appear in share-ready bundles, given the determinism rule in `docs/architecture/diagnostics.md` section 5? | Repository owner (mao2009) | #49 is closed, so the diagnostics contract has no open tracking issue. Open item for this decision: #45. | CONFIRMED that the current contract excludes time; OBSERVED (#49 closed) |
| Q11 | Does any value in the shareable output lie in the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical)? The shareable output under section 7.2 contains PCs and addresses, so the check is on those. **Concrete action:** run `openbios-probe <openbios.bin> --json --capture-at <hex-pc>` (`OpenBiosProbeCommand.cs:59-67`; `--dump-ram` also requires `--capture-at`, `:172`), then, for each PC-class and address-class value in the output, mask the segment bits and test it against `0x1FC00000`-`0x1FC7FFFF`. Record the result in the test log of the required test, not in this document. If any such value is in range, decide whether a BIOS-range value may appear in a shareable document. Raw words are not part of the shareable output, so they are not tested for this purpose. | Repository owner (mao2009) | #45 (open) | UNVERIFIED (not checked by running the probe) |
| Q12 | Is a SHA-256 of the firmware file (`sha256` at `OpenBiosProbeCommand.cs:423`; console `:443`; fallback export `:973`) acceptable in a shareable document? It identifies the user's BIOS image but carries no BIOS bytes. Default-deny until decided. | Repository owner (mao2009) | #45 (open) | OBSERVED (field exists); default-deny (section 7.6) |
| Q13 | Are HI and LO (section 7.3) to be shareable? The owner ruling covers GPR and COP0; this document classifies HI and LO as part of the register dump and excludes them. | Repository owner (mao2009) | #45 (open) | PREDICTED classification; the ruling does not name HI/LO |
| Q14 | Is a digest of the displayed frame (`frame.sha256`, `OpenBiosProbeCommand.cs:437`; `FramebufferSha256` in `ExecutionDiagnosticReport`) acceptable in a shareable document? It is a frame digest, not a whole-artifact digest. Default-deny until decided. | Repository owner (mao2009) | #45 (open) | CONFIRMED (fields exist); default-deny (section 7.6) |
| Q15 | Are build and tool identifiers shareable: `ProductRevision`, `Revision`, `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion`? Default-deny until decided. | Repository owner (mao2009) | #45 (open) | CONFIRMED (fields exist); default-deny (section 7.6) |
| Q16 | Shapes not yet read (`execution`, `consistency`, `referenceSecondsAtBoundary` value type, `accounting` other fields, `origins`, `FallbackInFlight`, `FallbackReturns`, `FallbackTargets`). Each stays default-deny until read and classified. Is that the accepted rule until the read is done? | Repository owner (mao2009) | #45 (open) | UNVERIFIED (shapes not read in this work) |
| Q17 | Are file digests of the user's own inputs shareable? `InputSha256` and `discHash` (constant at `DiagnosticContextEntry.cs:47`; producer at `FirmwareDistribution.cs:391-451`, not a probe writer) identify the user's copy of a licensed game file, the same way the firmware digest identifies the user's BIOS (Q12). Today `InputSha256` is written into the `#457` bundle's `report.json` (`ExecutionDiagnosticReport.cs:16`, bundle at `ExecutionDiagnosticBundleWriter.cs:51`), so the current bundle would change if the rule were default-deny. The design uses one rule for all three: a digest of a user-owned licensed file is default-deny until decided, and `artifactSha256` (a digest of this build's output, not a user file) is share-candidate. Owner to confirm this rule, or to split it. | Repository owner (mao2009) | #45 (open) | OBSERVED (fields and bundle writer); PREDICTED rule |

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- The leakage guarantee in section 7.4 is **UNVERIFIED** until the tests in section 7.5 exist and pass.
- The writer inventory (section 1.8) is a search result, not a proof of completeness. Test 6 of section 7.5 makes it checkable. **UNVERIFIED.**
- Several field shapes are named but not read (section 7.6, default-deny). They are a stated blocker (section 8), not a claimed classification.
- The `RunCommand` bundle gate was traced (section 1.2). No other path into the bundle was traced beyond the `ExecutionDiagnosticBundleWriter` lines cited.
- GUI code was not searched exhaustively.
- The `--dump-ram` file's contents were not inspected; its content class is taken from the code (`observed.ReadGuestWord` over KSEG0 main RAM), not from a produced file.
- The Q11 check was not run. No result is stated in this document.
- `milestones` is readable and is classified in section 7.6. Its JSON shape was taken from the record declaration (`OpenBiosBootMonitor.cs:10-40`), not from a serialised sample.
- The producers of `hotEntries`, `hotPcs`, and the `referenceSeconds` values were not read in this revision.
