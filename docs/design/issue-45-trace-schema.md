# Issue #45 Debug / Trace Schema Design

**Status:** Proposal (design only, no code change)

**Related Issues:** #45 (Debug / Trace Infrastructure, open); related #40, #42, #44 (closed), #16, #132, #457 (closed), #49 (closed), #739 (open), #763 (open), #773 (open), #774

**Base:** source line numbers in this document are at `2e3ecad`, and the writer inventory in section 1.8 (W1 to W13) is at `2e3ecad` too. `origin/main` is now `5d5b525`. The cited files are identical at `12b6a31` and `5d5b525`. Five cited files changed between `2e3ecad` and `12b6a31` (ADR-027, `OpenBiosProbeCommand.cs`, `OpenBiosProbeBoundaryLog.cs`, `InterpreterTitleExecutionEngine.cs`, `RecompiledHostExecutionEngine.cs`), so the line numbers stay at `2e3ecad`. Its probe changes all come from `656ed3c` (#850), which adds the `--compare-boundary-logs` writer (W14) and two `observer-failed` messages, and which is included in the inventory below.

## 0. How to read the labels

Every claim carries one of four labels:

- **OBSERVED**: a fact directly observed in an execution, log, or measurement. Statements from existing docs, issue text, or GitHub API responses are read, not re-run, so they are INFERRED or UNVERIFIED here.
- **CONFIRMED**: a fact that a reproducible verification met against an explicit expected condition, with the scope stated. This document has no run or test, so no claim here meets this definition.
- **INFERRED**: a conclusion derived from observation or implementation. Direct verification has not been established. Reading source, docs, or tests gives INFERRED. Design expectations of this proposal are INFERRED and are said so in the sentence.
- **UNVERIFIED**: an item for which there is not enough evidence to confirm it holds. This includes items not checked, and items checkable only by running a system or by a test that does not yet exist.

Two more terms are used throughout:

- **Local** output: any writer that is not the shareable projection (section 7.4). Local output may contain any field.
- **Share-candidate** output: a field that would pass the allowlist (section 7.2) if the shareable projection emitted it. A share-candidate is not shareable until the projection emits it. **INFERRED** (design term).
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

**INFERRED** (GitHub API, `body` field of `repos/mao2009/PSXRecompStudio/issues/45`). The body does not fix session ID or artifact ID formats, and does not mention BIOS or ROM data handling. **INFERRED.**

**Assignees and state (GitHub API, checked for this revision):** #45 open, 0 assignees. #457 closed, 0 assignees. #49 closed, 0 assignees. #763 open, 0 assignees. #773 open, 0 assignees. #40 open, 0 assignees. #42 open, 0 assignees. #739 open. #44 closed. #786 is a pull request (closed). **INFERRED.** No issue in this list records an owner.

**Owner ruling (binding):** the full register dump is removed from the shareable diagnostic output. GPR and COP0 full dumps are local-diagnostic only and never appear in a shareable document. Section 7 implements this.

## 1. Inventory of existing trace and diagnostics code

### 1.1 Shared diagnostic contract (Domain, `PSXRecomp.Core.Diagnostics`)

| Element | Location | Label |
|---|---|---|
| `Diagnostic` record (Code, Category, Severity, Stage, Context, Evidence, Recovery; no timestamp, host, user or path) | `src/PSXRecomp.Core/Diagnostics/Diagnostic.cs:30` | INFERRED |
| `DiagnosticCodes` registry (discoverability only) | `src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:20` | INFERRED |
| `DiagnosticContextEntry` (typed `uint`/`string` key-value) | `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:12` | INFERRED |
| Context key constants, including `InstructionOpcode = "opcode"` | `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:29` (class), `:38` (`InstructionOpcode`) | INFERRED |
| `DiagnosticEvidenceReference` (kind + identifier; references evidence, never embeds it) | `src/PSXRecomp.Core/Diagnostics/DiagnosticEvidenceReference.cs:48` | INFERRED |
| `DiagnosticJson` (deterministic, nulls written, LF) | `src/PSXRecomp.Core/Diagnostics/DiagnosticJson.cs:18` | INFERRED |
| `DiagnosticComparer` (stable total order) | `src/PSXRecomp.Core/Diagnostics/DiagnosticComparer.cs:14` | INFERRED |
| `DiagnosticExceptionPolicy` (process-fatal boundary, cancellation not a failure) | `src/PSXRecomp.Core/Diagnostics/DiagnosticExceptionPolicy.cs:21` | INFERRED |
| Architecture contract for the above | `docs/architecture/diagnostics.md` sections 2-6 | INFERRED |

`docs/architecture/diagnostics.md` section 4 states that the full evidence and trace capture pipeline belongs to Issues #16, #45, and #132, and that the contract only defines how a diagnostic points at evidence. **INFERRED** (`docs/architecture/diagnostics.md:86-88`).

### 1.2 Standard execution report and bundle (`#457`, closed)

| Element | Location | Label |
|---|---|---|
| `ExecutionDiagnosticReport` (schema `psxrecomp.execution-report.v1`; identifiers and classifications only) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:13` | INFERRED |
| Report fields, in order: `Schema`, `ProductRevision`, `InputSha256`, `Title`, `Revision`, `ArtifactIdentity`, `Outcome`, `State`, `GuestPc`, `DiagnosticCode`, `SegmentBudget`, `FramebufferSha256` | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:14-25` | INFERRED |
| `InputSha256` is normalised to bare, lowercase, 64-character hex (`NormalizeSha256` rejects anything else) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:53`, `:67-75` | INFERRED |
| `RunCommand` sets `ArtifactIdentity` to `"sha256:" + ComputeSha256(artifactPath)`, where `ComputeSha256` returns bare lowercase hex. The prefix is added at the call site. | `src/PSXRecomp.Cli/RunCommand.cs:191`, `ComputeSha256` at `:206-210` | INFERRED |
| `artifactPath` is the built artifact, resolved by `ResolveArtifactPath` | `src/PSXRecomp.Cli/RunCommand.cs:81` | INFERRED |
| `inputSha256` is declared at `:52`, and assigned only inside `if (arguments.Report)` (`:54`, value at `:58` from `resolved.Sha256`) | `src/PSXRecomp.Cli/RunCommand.cs:52-58` | INFERRED |
| The diagnostic bundle is written only inside `if (arguments.Report)` (`:86`); `inputSha256` is passed to `WriteDiagnosticBundle` at `:93` | `src/PSXRecomp.Cli/RunCommand.cs:86-93` | INFERRED |
| The CLI passes an empty diagnostics array to the bundle writer, so the CLI bundle's `diagnostics.log` has no entries | `src/PSXRecomp.Cli/RunCommand.cs:197-201` (`Array.Empty<Diagnostic>()` at `:201`) | INFERRED |
| `ExecutionDiagnosticLog` (projects diagnostics onto an allow-list of context keys, max 256 entries) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:12`, allow-list `:17-30`, filter `:78-79` | INFERRED |
| `ExecutionDiagnosticLog` allows the `opcode` key (`DiagnosticContextKeys.InstructionOpcode`) as a `uint`. A value under this key is a raw instruction word. | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:28` | INFERRED |
| `ExecutionDiagnosticBundleWriter.Write` serialises `report` (`:51`), `environment` whole (`:52`), and `diagnosticLog` from `FormatTail` (`:53`), and writes four entries (name constants `:18-21`) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:16`, `:51-53`, `:18-21` | INFERRED |
| `issue.md` is formatted from `report` by `ExecutionIssueMarkdown.Format` (it prints the artifact identity and input hash) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:54`; `src/PSXRecomp.Core/Diagnostics/ExecutionIssueMarkdown.cs:36` | INFERRED (call); the full issue.md text was not read |

### 1.3 Runtime host diagnostic (`#763`, open)

| Element | Location | Label |
|---|---|---|
| `RuntimeHostDiagnostic` record (per-run stop account; off by default, observation only; unknown fields are `null`, not guessed) | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:24` (record starts); fields `FinalPc` through `Metrics` follow | INFERRED |
| `RuntimeHostRunMetrics` record: `WallMilliseconds`, `MmioAccesses`, `TimeReports`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds`, `LastFallbackEntryPc`, `LastFallbackReturnPc`, `LastMmioPhysicalAddress`, `LastMmioWasWrite`, `FallbackInFlight`, `FallbackReturns` | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:146-160` | INFERRED |
| Per-run `runId` (GUID, generated once) | `src/PSXRecomp.RuntimeHost/Program.cs:198`; printed at `:210` | INFERRED |
| `runId` also written into the loaded-code mismatch report (`report["runId"] = runId`) | `src/PSXRecomp.RuntimeHost/Program.cs:624` | INFERRED |

### 1.4 Unit retirement trace (`#774`)

| Element | Location | Label |
|---|---|---|
| Design and schema: JSON Lines, `v:1`, `kind` records, session and handoff identity | `docs/development/unit-trace.md`, sections "Enabling", "Identity", "Record schema" | INFERRED |
| `UnitTraceScope.Begin` (in-process enable for tests) and `UnitTraceRun` (child env, parent fallback file) | `src/PSXRecomp.Infrastructure/UnitTraceRun.cs:15`, `:54` | INFERRED |
| Session ID generated per run as `Guid.NewGuid().ToString("N")` and passed to the child via `PSX_TRACE_SESSION` | `src/PSXRecomp.Infrastructure/UnitTraceRun.cs:88`, `:112` | INFERRED |
| Native side: opt-in trace build, env `PSX_UNIT_TRACE_FILE`, `PSX_UNIT_TRACE_MAX_BYTES`, default cap `RECOMPILER_TRACE_DEFAULT_CAP`, truncation and end records | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:79`, `:1037-1077`, `:1155-1162` | INFERRED |
| Native `PSX_TRACE_SESSION` is accepted only when it is a non-empty token of at most 64 characters from `[0-9A-Za-z-]`; otherwise the session is left empty | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:1120-1137` | INFERRED |
| Joiner: `Complete` / `Partial` / `Rejected`, sequence gap checks, handoff pairing | `src/PSXRecomp.Core/Diagnostics/UnitTraceJoiner.cs:21`; status enum `:403`; segment source `:410`; `UnitTraceSegment` `:425`; `UnitTraceJoin` `:438` | INFERRED |
| Native trace records carry CPU state (`gpr` x32, `hi`, `lo`, `sr`, `cause`, `epc`, `pc`) at unit end, trace build only | `unit-trace.md` "Record schema"; `CpuCheckpoint` parser `src/PSXRecomp.Core/Diagnostics/CpuCheckpoint.cs:17`, `TryParse` `:38` | INFERRED (doc); INFERRED (parser) |
| Disabled path emits byte-identical generated source | `unit-trace.md` "Enabling"; tests `HostCodeGenTests` (`UnitTrace_*`) | INFERRED |

### 1.5 Interpreter fetch ring and fallback diagnostics

| Element | Location | Label |
|---|---|---|
| `ExecutionTraceSnapshot` record (fetches and transfers) | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:14` | INFERRED |
| `ExecutionTraceRing` class: fixed-capacity fetch ring plus control-transfer ring | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:17`; `DefaultCapacity = 64` at `:19` | INFERRED |
| The "never influences execution" statement is in the type's summary comment | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:11` | INFERRED |
| Engine exposes the ring as `RecentTrace` and fetches the instruction word via `FetchWordForTrace` | `src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:279`, `:891` | INFERRED |
| `FallbackReturnDiagnostics` | `src/PSXRecomp.Core/Execution/FallbackReturnDiagnostics.cs:148` | INFERRED (declaration only) |
| `MixedFallbackDiagnostics` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:33` | INFERRED (declaration only) |
| `HostMmioAccess(PhysicalAddress, Width, IsWrite)` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:161` | INFERRED |
| `ArtifactFallbackSession.Attribute`: per-entry version comparison, runs only with a transition observer | `src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:143-160` | INFERRED (`docs/runtime/loaded-code-identity.md:51`) |

### 1.6 Loaded-code mismatch diagnostics (`#773`, open)

| Element | Location | Label |
|---|---|---|
| `LoadedCodeMismatchDiagnostics` (bounded records, bounded compared words) | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:19`, `:25`, `:42` | INFERRED |
| Per-record JSON: `pc`, `version`, `versionCount`, `comparedWords`, `ramPage`, `rejections`, `fullMatches`, `firstMismatchIndexRange`, `mismatchedWordsRange`, `ramHashChanges`, `aotSha256`, `ramSha256`, `windowStartPc`, `windowAot`, `windowRam`, `origins` | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:277-300` | INFERRED |
| Mismatch record keeps a 5-word window of AOT words and RAM words at the first refusal, written as `windowAot` and `windowRam` | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:30` (`WindowRadius = 2`), `:269-273`, `:296-297` | INFERRED |
| Source comment calls this "a bounded sample, not a dump of the image" | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:268` | INFERRED (comment) |
| `origins` is produced by `OriginsJson()`, whose body was not read | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:298` (call) | UNVERIFIED (shape not read) |

### 1.7 BIOS and ROM guards

| Element | Location | Label |
|---|---|---|
| `BiosAddressGuard` static class; `PhysicalBase = 0x1FC00000` | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:103` | INFERRED |
| `IsBiosAddress` | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:113` | INFERRED |
| `OverlapsBios` | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:120` | INFERRED |
| `RequireNoBiosImage` (refuses BIOS code in AOT generation) | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:146` | INFERRED |
| `BiosContentDiagnostic` record: counts and provenance text, no bytes | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:81` | INFERRED |
| Standalone report "never names an input". The code comment cites "Issue #786 B1"; #786 is a pull request, so the label in the comment is a mislabel. | `src/PSXRecomp.RuntimeHost/Program.cs:197` | INFERRED (comment) |

### 1.8 OpenBIOS probe: writers inventoried

The probe is `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs`, with helpers in `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs`, `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs`, `src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs`, and `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs`.

**Completeness claim.** The writers below were found by searching the probe sources for output calls (`output.WriteLine`, `error.WriteLine`, `error?.WriteLine`, `output?.WriteLine`, `Console.Error`, `WriteAllText`, `WriteAllBytes`, `WriteAtomically`, `StreamWriter`, and `output.Write`). The search found W1 to W14. W14 comes from `656ed3c` (#850) and is on `origin/main` only; base `2e3ecad` has W1 to W13. At base `2e3ecad` the stderr search found 35 `error.WriteLine` calls, from `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:35` to `:657`, and W13 lists every one of them. At `origin/main` the same search finds 42 stderr calls and 9 stdout writes (base: 8). The 7 new stderr calls and the 1 new stdout write are all in W14 or in the `observer-failed` message of W13. The `--boundary-log` usage text changed, with the same call count. **Whether this list is exhaustive is UNVERIFIED**: no tool proved that every output call was found. The required test (section 7.5, test 6) must also check that the writer list matches the code, and it searches for `error.WriteLine` as well as `output.WriteLine`.

**W1. Interpreter probe `--json` document.** Written directly at `:419` inside the `if (json)` branch, not through the common writer.

| Element | Location | Label |
|---|---|---|
| The anonymous object is serialised and written at `:419`; its fields `kind`, `backend`, `sha256`, `bootVerified`, `kernelBooted`, `milestones`, `titleStarted`, `executionState`, `guestPc`, `diagnosticCode`, `segmentsRetired`, `stop`, `captured`, `gte`, `frame` are at `:421-437` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:419` (write), `:421` (`kind`), `:423` (`sha256 = hash`), `:432` (`stop`), `:433` (`captured`), `:435` (`gte`), `:437` (`frame`) | INFERRED |
| `BuildStop` builds `pc`, `sr`, `cause`, `epc`, `badVAddr`, `gpr[32]`, `lastFetches` (`Hex(e.Pc):Hex(e.Word)`, last 16), `lastTransfers` (last 16, including `From.Word`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1004-1014` (`pc`/`sr`/`cause`/`epc`/`badVAddr` at `:1010-1011`; `gpr` at `:1012`; `lastFetches` at `:1013`; `lastTransfers` at `:1014`) | INFERRED |
| `BuildStop` is called with the interpreter engine's `FinalSnapshot` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:361` | INFERRED |
| `captured` is built at the capture PC: `pc`, `word = ReadGuestWord(pc)` (raw instruction word), `gpr[32]`, `lastTransfers` (PCs only) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:344-350`; `word` at `:346`; `gpr` at `:347` | INFERRED |
| `gte.lastUnsupportedCommand` is `Gte.LastUnsupportedCommand`, set to `command & 0x01FF_FFFF`; `gte.commandsExecuted` is a count | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:435`; `src/PSXRecomp.Core/Runtime/Gte/GteRegisterBank.cs:106`, `:115` | INFERRED |
| `milestones` is an `OpenBiosBootReport` record: `ResetVectorFetched`, `KernelRunningFromRam`, `VectorsInstalled`, `ExceptionVectorExecuted`, `ShellEntered`, `ShellEnteredAtFetch`, `InterruptEntries`, `SyscallEntries`, `TitleEntryPc`, `FirstUnexpectedException`, plus `TitleStarted` and `KernelBooted` | `src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:10-40` (record at `:10-20`; `TitleStarted` at `:26`; `KernelBooted` at `:34`) | INFERRED |
| `milestones.firstUnexpectedException` is `OpenBiosUnexpectedException(ExcCode, Epc, BadVAddr, Cause, AtFetch)` | `src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:40`; read by the probe at `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:808` | INFERRED |
| `--capture-at <hex-pc>` option | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:59-67` | INFERRED |
| The probe loads a 512 KiB OpenBIOS ROM and boots the firmware, so the interpreter fetches are expected to include BIOS-range PCs | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:201-207` (ROM load); the boot design | INFERRED (ROM load); INFERRED (BIOS-range fetches) |
| Whether any value in the `--json` output lies in the BIOS range | not checked (no probe run, no captured `--json` output inspected). Q11 provisional position (section 7.8): record a result only when it is measured; otherwise `unknown`. | UNVERIFIED |

**W2. Generated-host `--json` document.** The document object is built at `:663-742` and passed to the common writer, which serialises it at `:743-746` and writes it at `:755`.

| Element | Location | Label |
|---|---|---|
| The generated-host document object (fields are classified in section 7.6) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:663-742` | INFERRED |
| Common serialisation and write: `SerializeToNode`, `consistency` added, text produced, written | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:743-746`, `:755` | INFERRED |
| `diagnosticMessage` is set from `result.DiagnosticMessage` (free text) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:677` | INFERRED |
| `stopAt` is `{ pc, reachedByInterpreter, reachedByHost }` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:678-680` | INFERRED |
| `build` carries counts (`imageInstructions`, `nativeInstructions`, `blocks`, `roots`, `staticFallbackTargets`, `symbols` as a count) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:681-689` | INFERRED |
| `loadedImages[]` carries `name`, `blocks`, `nativeInstructions`, `skippedEntries` (PCs) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:690` | INFERRED |
| `loadedCodeVersions` is `loadedCode.Blocks.Count` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:691` | INFERRED |
| `interpretedEntries` is `observedPcs.Order()` (a PC array) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:692`; `observedPcs` at `:488-490` | INFERRED |
| `precompileMilliseconds` is `buildMs + compileMs` (not rounded at this point) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:693` | INFERRED |
| `atBoundary` is a dictionary keyed by PC (`Hex(b.Key)`) whose value is `new { nativeInstructions, seconds, mmioRoundTrips, timeReportRoundTrips }` (`:597-598`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:550` (dictionary), `:597` (value shape), `:694` (output) | INFERRED |
| `execution` carries native and fallback counts, `topFallbackTargets` (strings of PC, counts, and a cause), and `timings = costs` (`:706`, shape not read) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:695-709` | INFERRED |
| `accounting` is `accounting.Report(...)` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:710-711` | INFERRED |
| `timings` is built at `:712-724` from `Math.Round(..., 1)` doubles (0.1 ms precision) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:712-724` | INFERRED |
| `aot` carries `generatedCBytes` and `artifactBytes` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:725` | INFERRED |
| `boundaries` is a dictionary keyed by PC whose values are `Describe()` objects | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:726`; `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107` | INFERRED |
| `differential` is a dictionary keyed by PC whose values are `Compare` results, or a string for an unreached boundary | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:727-731` | INFERRED |
| `referenceBoundariesMissing` is a PC array | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:732` | INFERRED |
| `referenceSecondsAtBoundary` is a dictionary keyed by PC of `referenceSeconds` values (type not read here) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:733` | INFERRED (location); value type UNVERIFIED |
| `firstInterrupt` is `{ interpreter: Describe(), host: Describe() }` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:734` | INFERRED |
| `firstInterruptDifferential` is `Compare(referenceInterrupt, hostInterrupt)` or a string | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:735-736` | INFERRED |
| `milestoneComparison` is `{ match, interpreter = referenceMilestones }` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:737-741` | INFERRED |
| `ProbeGuestState.Describe()` returns `pc`, `atFetch`, `cpu` (a map of named CPU values), `devices` (a map), `instructionWord` (raw word read from RAM at PC), `sr`, `cause`, `epc`, `ramSha256`, `guestCycles`, `scratchpadSha256` | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107`; `instructionWord` at `:101`; `ramSha256` at `:104` | INFERRED |
| The `cpu` map is built from a list that includes `hi`, `lo`, `sr`, `cause`, `epc`, `badvaddr` and the 31 GPR entries | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:35-36` | INFERRED (GPR entries at `:35`; the six named entries at `:36`) |
| `scratchpad_sha256` is a device digest | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:41` | INFERRED |
| `vram_sha256` is a device digest | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:83` | INFERRED |
| The differential emits per-field mismatch objects (`kind = "cpu"` with `interpreter` and `host` hex values) | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:117-118` | INFERRED |
| The differential emits a RAM mismatch object with `address`, `page`, `contextStart`, `interpreterBytes`, `hostBytes` (16 raw RAM bytes each side) | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:138-143` | INFERRED |
| The differential emits `interpreterRamSha256` and `hostRamSha256` (whole-RAM digests) | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:154-155` | INFERRED |
| `accounting.unsupportedOpcodes[]`: `excCode`, `opcode`, `pc`, `word` (raw instruction word), `atFallbackFetch` | `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:195-198` | INFERRED |
| Pinned by a test: `unsupported[0]["word"] == "0xFC000000"` and `unsupported[1]["opcode"] == 0x12` | `src/PSXRecomp.Tests/Cli/OpenBiosProbeAccountingTests.cs:183`, `:185` | INFERRED |

**W3. Non-JSON console output, interpreter path (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:441-450`).**

| Element | Location | Label |
|---|---|---|
| Prints `OpenBIOS execution probe (sha256:{hash}, backend=...)` (firmware digest, `:443`), state, PC, and code | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:441-445` | INFERRED |
| Prints `Stop: {JsonSerializer.Serialize(stop)}`, which includes the full `gpr[32]` and COP0 fields | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:446-449` | INFERRED |
| Prints `Kernel boot milestones: {report}` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:450` | INFERRED |

**W12. Non-JSON summary (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:749-751`, `Summarize` at `:838`).** When `--json` is not set, the document is parsed back and summarised line by line.

| Element | Location | Label |
|---|---|---|
| The summary is written at `:749-751` from `Summarize(JsonNode.Parse(text))` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:747-751` | INFERRED |
| `Summarize` prints `hotEntries[]` entries with `pc`, `symbol` (a symbol name), `reason`, `transitions`, `retiredInstructions` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:859-861` | INFERRED |
| `Summarize` prints `hotPcs[]` entries with `pc`, `symbol`, `fetches`, `share` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:864-866` | INFERRED |
| `Summarize` prints the differential result, and on a mismatch the `firstMismatch` JSON (may carry register or RAM values) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:874` | INFERRED |
| The `hotEntries` and `hotPcs` names are used by `Summarize`; their producer was not read in this revision | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:859`, `:864` | INFERRED (consumer); producer UNVERIFIED |
| The summary also prints `timings` (`:879-881`), AOT byte counts (`:882`), and `consistency.differentialPass` (`:883`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:879-884` | INFERRED |

**W4. `--retire-out <file>` (JSON lines, one per fetch at a position).**

| Element | Location | Label |
|---|---|---|
| The retirement writer is opened at `:317` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:317` | INFERRED |
| Each line carries `g`, `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `pipelineClean` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:328-333` | INFERRED |

**W5. `--pc-histogram <file>` (JSON).**

| Element | Location | Label |
|---|---|---|
| Written with `totalRamFetches`, `top` (PC plus count, top 64), and `buckets` (64-byte bucket start plus count, top 48). Comment: "Local diagnostic only" | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:368-377` | INFERRED |

**W6. `--dump-frame <file.ppm>` (binary PPM).**

| Element | Location | Label |
|---|---|---|
| Writes the displayed frame as a binary PPM with 24-bit colour pixels. Comment: "Local diagnostic only (never committed)". | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:378-411` | INFERRED |

**W7. `--dump-ram <file>` (raw 2 MiB main RAM).**

| Element | Location | Label |
|---|---|---|
| Option, requirement, and write | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:81` (option), `:172` (requires `--capture-at`), `:337-342` (write) | INFERRED |

**W8. `--fallback-targets <file>` (fallback export JSON).**

| Element | Location | Label |
|---|---|---|
| The export is written with `WriteAtomically` after the run | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:660`; `WriteAtomically` at `:981` | INFERRED |
| The export document `BuildFallbackTargetsDocument` contains `format`, `sha256` (the firmware digest, `hash`, `:973`), `run` (`ObservedRootPlanner.RunDocument(DescribeRun(hash, manifest, discSha256, ...))`, `:974`, which includes the firmware digest and the disc digest), `outcome` (`state`, `complete`, `stopAt`, `:975`), and `targets[]` (`pc`, `cause`, `entries`, `instructions`, `lastReturnPc`, `ramWord`, `:961-969`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:952-977` | INFERRED |
| `ramWord` is a raw RAM word, read by `ramWordOf` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:968` | INFERRED |

**W9. `--mdec-events <file>` (JSON lines).**

| Element | Location | Label |
|---|---|---|
| Each line carries `engine`, `seq`, `g`, `kind`, `value` (raw 32-bit word), `pc`, `lastFallbackFetchPc`, `command` (raw 32-bit word), `remaining`, `control` (register), `fifoWords`, `busyBefore`, `busyAfter` | `src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:43-56` | INFERRED |

**W10. `--boundary-log <file>` (JSON lines).**

| Element | Location | Label |
|---|---|---|
| Each line is `Describe(engine, devices, vblank, cycles)`: device registers, `vramSha256`, and the boundary position | `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:40-56` | INFERRED (call and fields named by the review); the boundary-log `Describe` body (a different method from `ProbeGuestState.Describe()`, `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107`, which was read) was not read in full |

**W11. `--list-disc <file>` (JSON).**

| Element | Location | Label |
|---|---|---|
| Writes the disc's file listing, including file names from the disc image | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:251-289` | INFERRED (range); field shape not read |

**W13. Standard error (`stderr`) writers.** Messages written to stderr, which may carry local paths or free text. Two `observer-failed` messages were added by #850 (`origin/main`, `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:567` as `error?.WriteLine`, and `:891`). They carry an exception type name and message, which is free text and excluded.

| Element | Location | Label |
|---|---|---|
| Usage text and argument-validation messages, 35 `error.WriteLine` calls in total (the full list is below) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:35`, `:52`, `:63`, `:77`, `:102`, `:108`, `:120-121`, `:126-127`, `:132-133`, `:138-139`, `:147-148`, `:154`, `:160-161`, `:166-167`, `:172-173`, `:178-179`, `:190`, `:192` | INFERRED |
| Argument echoes: duplicate option (`:52`), option needing a hexadecimal PC (`:63`), option needing a value (`:77`), unknown option (`:102`), option needing a positive integer (`:108`). These print argv tokens. | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:52`, `:63`, `:77`, `:102`, `:108` | INFERRED |
| Path in the message for an existing fallback export (`--fallback-targets {path} already exists`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:154` | INFERRED |
| Exception type and message (`ex.Message`), which may contain local paths | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:242` | INFERRED |
| Warnings with the boundary or MDEC failure text | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:233`, `:235` | INFERRED |
| Progress lines with fetch counts, transitions, and seconds | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:529`, `:608`, `:615` | INFERRED |
| Fallback export status lines (`did not finish`, `ended in {result.State}`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:651`, `:657` | INFERRED |
| The 35 `error.WriteLine` lines, grouped: `:35` (usage), `:52`, `:63`, `:77`, `:102`, `:108` (argument echoes), `:120-121`, `:126-127`, `:132-133`, `:138-139`, `:147-148`, `:154`, `:160-161`, `:166-167`, `:172-173`, `:178-179`, `:190`, `:192` (validation and usage), `:233`, `:235` (warnings), `:242` (exception), `:529`, `:608`, `:615` (progress), `:651`, `:657` (status) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:35-657` | INFERRED (the search result; each line is in one of the groups above) |

**W14. `--compare-boundary-logs <a.jsonl> <b.jsonl>` (#850, commit `656ed3c`; `origin/main` only, not in base `2e3ecad`).** Takes no ROM. It reads two W10 boundary logs (local inputs) and writes one JSON object to stdout. Line numbers in this table are from `origin/main`.

| Element | Location | Label |
|---|---|---|
| Stdout document `{ leftBoundaries, rightBoundaries, commonBoundaries, firstMismatch }`, written with `output.WriteLine(document.ToJsonString())`; mode entry `CompareBoundaryLogs` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:278` (entry), `:339` (write) | INFERRED (origin/main) |
| `firstMismatch` is `{ index, vblank, field }`, or `null` when no compared field differs | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs` (document built inside the `try`, before `:339`); `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:130-142` (`FirstMismatch`) | INFERRED (origin/main) |
| `field` is a name from `ComparedFields()` (a record key name), not a value | `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:134-138` | INFERRED (origin/main) |
| Stderr: usage (`:49`); file-access failure with `ex.Message`, which can carry a local path (`:295`); format error with `ex.Message` (`:328`); internal error with `ex.Message` (`:335`); zero-common-boundaries message (`:343`) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:49`, `:295`, `:328`, `:335`, `:343` | INFERRED (origin/main) |
| Exit code 1 when `commonBoundaries` is 0, after the document has been written | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:339-345` | INFERRED (origin/main) |

### 1.9 Identity hashes already in use

| Element | Location | Label |
|---|---|---|
| Input identity: SHA-256 of EXE and disc. The phrase "for provenance" in that doc is not adopted here: under owner policy 3 a hash identifies a file and does not prove provenance or legitimacy | `docs/runtime/loaded-code-identity.md:14`, `:45` | INFERRED (the doc); policy 3 is INFERRED |
| `GuardStatsScope` run ID, random 64-bit hex per child process | `src/PSXRecomp.Infrastructure/GuardStatsScope.cs:18`; `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:1345` | INFERRED |
| `ObservedRunIdentity` (firmware SHA, manifest, disc SHA, probe options SHA) | `src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:46`, `:262` | INFERRED |

### 1.10 Gaps in the inventory

- **No shared session ID.** The unit trace has a session ID. The `#457` report has none. The standalone host has a separate `runId` (`src/PSXRecomp.RuntimeHost/Program.cs:198`, also written at `:624`). The guard-stats scope has another run ID. **INFERRED.**
- **No MMIO event stream.** `HostMmioAccess` and the `LastMmio*` metrics record only the last access. **INFERRED.**
- **No GPU, SPU, CD, or DMA event stream.** Only the device interfaces exist (`src/PSXRecomp.Core/Runtime/IGuestDeviceAccess.cs`, `src/PSXRecomp.Core/Runtime/ITimer.cs`, `src/PSXRecomp.Core/Runtime/IGpu.cs`, `src/PSXRecomp.Core/Runtime/ISpu.cs`, `src/PSXRecomp.Core/Runtime/ICdRom.cs`). **INFERRED** (file list only).
- **No timestamps.** The deterministic JSON forms exclude time by design. **INFERRED** (`diagnostics.md` section 2).
- **Studio GUI and CLI consumption of a trace format**: none found in this inventory. **UNVERIFIED** (no exhaustive GUI search).

## 2. Common model

Four record families share one envelope. The envelope follows the existing `v:1` JSON Lines style of the unit trace, so parser patterns can be reused. **INFERRED** (design).

What exists today versus what is new:

- The unit-trace native header carries `session` (`unit-trace.md`, "Record schema"). **INFERRED.**
- The unit-trace fallback header carries both `session` and `stream`. **INFERRED** (`unit-trace.md`, fallback file table).
- The per-record `session` field on every record, and the `stream` field on native and host records, are **new** in this design. **INFERRED.**

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
| Register state | `checkpoint` | `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `globalRetired` | `CpuCheckpoint` (`src/PSXRecomp.Core/Diagnostics/CpuCheckpoint.cs:17`) | Local only (owner ruling) |
| MMIO | `mmio` | `physAddr`, `width`, `isWrite`, `value`?, `pc`? | `HostMmioAccess` (`src/PSXRecomp.Core/Execution/MixedFallback.cs:161`) | `value` local only; the rest per section 7.6 |
| Runtime event | `event` | `device` (`gpu`, `spu`, `cdrom`, `dma`, `timer`, `irq`, `bios`), `name`, `code`?, `data`? | New; BIOS call identity exists (`BiosCallIdentity` in `src/PSXRecomp.Core/Runtime/BiosCallIdentity.cs`, used by `src/PSXRecomp.Core/Runtime/BiosServiceResult.cs:152`) | `data` local only; `name` and `code` per section 7.6 |

`src/PSXRecomp.Core/Runtime/BiosServiceResult.cs:152` is a use of `BiosCallIdentity` in `BiosDiagnostic`, not its definition. **INFERRED.**

Diagnostic records (section 1.1) are not trace records. They link into a trace by reference only (`DiagnosticEvidenceReference`). **INFERRED** (current contract); the link use is **INFERRED.**

Field rules:

- Addresses are `uint` and written as hex strings in JSON, matching the existing unit trace. **INFERRED.**
- No record carries a host path, user name, or wall-clock time by default. Time is opt-in (section 5).

## 3. Trace event schema

Recommended schema v1 extends the unit-trace file rather than introducing a new format. **INFERRED.**

- Native stream: existing `unit`, `handoff`, `truncated`, `end` records (`unit-trace.md`), with `stream` and per-record `session` added.
- Fallback stream: existing `fallback`, `transfer`, `truncated`, `end` records, with per-record `session` added.
- New `mmio` and `event` kinds go in a host-written stream, not the generated C, so the native trace build stays byte-identical in the non-trace case. **INFERRED.** (Byte-identical disabled path is **INFERRED**, `unit-trace.md` "Enabling".)
- The joiner's sequence-gap and session-match rules apply per stream. A new joiner pass merges the host stream by `(session, globalRetired)`. **INFERRED.**

Example (illustrative, not implemented):

```json
{"v":1,"seq":12,"stream":"host","kind":"mmio","session":"3f9c0a...","globalRetired":4821,"physAddr":"0x1F801070","width":4,"isWrite":true,"pc":"0x80012A44"}
```

`pc` on an MMIO record is optional because the native side does not report the access PC today. **INFERRED:** `RuntimeHostRunMetrics` carries only the last address (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:146-160`), and `HostMmioAccess` (`src/PSXRecomp.Core/Execution/MixedFallback.cs:161`) has no PC field.

Known semantic limits that the schema must carry, not hide: serviced-SYSCALL counting, PC after interrupt, termination 5 with fallback stop, SYSCALL in delay slot. **INFERRED** (`unit-trace.md`, "Known semantic differences").

**Stop reasons (owner policy 2).** `BudgetExhausted` is an independent stop reason. It is distinct from a normal exit, a guest stop, and a host-side timeout, and it is not evidence of compatibility. A trace stop record must keep it as its own reason and must not merge it into termination 0 (success) or into a timeout. The unit-trace format defines no budget termination value (`docs/development/unit-trace.md`, "Record schema"), so the field and value name for it are UNVERIFIED and are left to the implementation. The registry Outcome is `BudgetExhausted`, an independent Outcome and not `Blocked` (owner decision at design level, the same in #33 section 3.5 and #51 section 5.1). The implementation mapping at `src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:54` still maps it to `Blocked`; changing that code is a separate, pending change. `Cancelled` is NOT `Blocked`: it is its own Outcome `Cancelled` (see #33 section 3.5). A host-side timeout maps to `Error`. **INFERRED** (design).

## 4. Linking to session ID and artifact ID

Existing identities and the proposed use of each:

| Identity | Existing field or source | Scope | Proposed use in trace |
|---|---|---|---|
| Session | `UnitTraceRun` GUID (`src/PSXRecomp.Infrastructure/UnitTraceRun.cs:88`), passed as `PSX_TRACE_SESSION` | One run | `session` on every record; new optional `sessionId` in `report.json` (Q1) |
| Input identity | `ExecutionDiagnosticReport.InputSha256` (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:16`), set only on the `--report` path (`src/PSXRecomp.Cli/RunCommand.cs:54-58`) | One input file, same across rebuilds of the same input | Trace header field `inputSha256`, bare lowercase hex (the existing encoding); NOT SHAREABLE: commercial game input or EXE (owner ruling 5), and non-commercial game input (owner ruling 2; section 7.7) |
| Built-artifact identity | `ExecutionDiagnosticReport.ArtifactIdentity` (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:19`), set from `"sha256:" + hex` of the built artifact (`src/PSXRecomp.Cli/RunCommand.cs:191`) | One build output; changes whenever the build output changes | Trace header field `artifactSha256`, bare lowercase hex (prefix stripped); NOT SHAREABLE (owner ruling) |
| Run ID | `runId` (`src/PSXRecomp.RuntimeHost/Program.cs:198`, also `:624`); `GuardStatsScope` run ID (`src/PSXRecomp.Infrastructure/GuardStatsScope.cs:18`) | One run | Retire for trace purposes (Q2) |

**Encoding decision.** Both hash fields in the trace header are bare lowercase 64-character hex, with no `sha256:` prefix. `inputSha256` already has this form (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:67-75`). `artifactSha256` is taken from `ArtifactIdentity` with the literal `sha256:` prefix removed. The prefix is a presentation detail added at `src/PSXRecomp.Cli/RunCommand.cs:191`, so the trace stores the bare value and the report keeps its existing string unchanged. **INFERRED.**

**Decision in this design.** No new `artifactId` field is added. The trace header carries the two hashes above. **INFERRED.** Whether `artifactSha256` is stable across rebuilds of the same input is **UNVERIFIED** (depends on the build's determinism).

Linking rules (INFERRED):

1. The parent generates `session` once. It goes into the environment (`PSX_TRACE_SESSION`), into every trace header and record, into `report.json` as an optional field, and into `diagnostics.log` lines as a context key.
2. A `Diagnostic` links to a trace by `DiagnosticEvidenceReference(kind=trace, identifier=session)`. A trace links back to its input and build by the header hashes above.
3. A join is rejected if `session` differs between streams. The unit trace already does this (`unit-trace.md`, "Identity").
4. The existing `ProductRevision` and `InputSha256` report fields are kept as they are. **INFERRED** (fields exist).

### 4.1 Standalone and packaged artifacts (ADR-027)

ADR-027 (`docs/adr/027-standalone-artifact-runtime-integration.md`, Status Accepted, Issue #739) has three layers, and the later layer supersedes the earlier one:

- **Decision and Step A scope (Phase 2 Step A):** the standalone artifact embeds its image and initial state and reads no input files, and `run --standalone` supplies the shared Runtime host from the CLI process. **INFERRED** (`ADR-027:52` Decision). Step A scope, `:62-66`, lists "Host bundling" (`:62`) and "Direct launch" (`:64-66`) as not implemented in Step A.
- **Supersession (Step C), quoted in full:** "**Step C (PR #761) supersedes the above**: it implements host bundling, direct launch, and self-contained distribution as described in the Step C scope below." **INFERRED** (`ADR-027:70-71`).
- **Step C packaged layout:** the Step C section (`ADR-027:128-235` at 2e3ecad, to end of file) describes a distributed folder containing a native artifact plus `runtime/PSXRecomp.RuntimeHost[.exe]`, `runtime/runtime-host.json`, and `runtime/artifact-image.bin`. The host "reads `runtime/runtime-host.json`, locates the bundled `artifact-image.bin`, and runs the guest artifact through the shared Runtime" (`:137-139`). At 2e3ecad the manifest is at `:144`, `:154` and `:202`; the `artifact-image.bin` layout line is `:155`; and the image sidecar is `:203`. **INFERRED** (`ADR-027:137-139`, `:144`, `:154`, `:155`, `:202`, `:203`).

So the Step A statement "reads no input files" holds only for user inputs. A run of a `package <exe|chd>` distribution takes neither EXE nor disc; a `package --bios openbios` run reads `runtime/disc/` (issue-727 §2.3; `src/PSXRecomp.RuntimeHost/Program.cs:231-241`) (**INFERRED** for the `Program.cs` branch). It does not hold for the packaged layout, where the host reads a bundled image sidecar and a run-configuration file at runtime. **INFERRED** against the ADR text. The Step A `run --standalone` model is not the current model.

For a packaged run, the identities a trace can name are: the native artifact executable, `runtime/artifact-image.bin`, `runtime/runtime-host.json`, and the host binary. `InputSha256` is still produced only on the `--report` path (`src/PSXRecomp.Cli/RunCommand.cs:54-58`), and that path belongs to the `run` command, not to the packaged layout. Q3 asks which of these the trace carries.

## 5. Performance cost and sampling

What exists:

- Unit trace is opt-in and costs nothing when off (byte-identical generated source). Default output cap is set by `RECOMPILER_TRACE_DEFAULT_CAP` (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:1162`); `unit-trace.md` states the default as 64 MiB. **INFERRED.**
- The interpreter fetch ring is fixed-size (64 by default) and always on. **INFERRED** (`src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:19`); its per-fetch cost was not measured. **UNVERIFIED.**
- Loaded-code mismatch comparison is bounded by `DefaultMaxComparedWords = 1<<24` and a record cap. **INFERRED** (`src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:25`).
- Fallback attribution runs per transition and only with an observer. **INFERRED** (`docs/runtime/loaded-code-identity.md:51`).
- The full-RAM compare on every fallback return is the largest known avoidable cost on the mixed path. **UNVERIFIED** (not measured here). The cited doc (`docs/runtime/loaded-code-identity.md:83`) calls it "INFERRED from code; not measured"; this document uses the four labels in section 0 only, so the claim is UNVERIFIED.

Recommended policy (INFERRED):

| Level | Trigger | What is recorded | Expected cost |
|---|---|---|---|
| L0 Off | default | Nothing new | 0 (must stay byte-identical) |
| L1 Boundary | `--trace` | Unit trace plus `handoff`, `transfer`, `stop`, `checkpoint` at existing boundaries | Per boundary; UNVERIFIED |
| L2 Event | `--trace=events` | Adds `event` and `mmio` records | Per MMIO access; UNVERIFIED, likely the dominant cost |
| L3 Sampled | `--trace=events --sample N` | Every N-th `mmio` or `event`, with a counter so drops are visible | Scales with 1/N; UNVERIFIED |

Sampling rules (INFERRED):

- Sampling is deterministic (every N-th by `seq` within a stream), never random, so two runs of the same input are comparable.
- Every sampled-out record is counted in the next kept record (`skipped` field) so consumers know the trace is incomplete. Incomplete traces join as `Partial`, an existing status (`unit-trace.md`, "Joining").
- Caps are per stream, using the existing `truncated` record pattern.
- Diagnostics and `stop` records are never sampled out.

Measurements required before default levels are chosen: wall time and output bytes for L0, L1, and L2 on one packaged run. **UNVERIFIED** (no measurement in this work).

## 6. Crashes, sessions, artifacts

- A crash produces a `stop` record with the `Diagnostic` code and `session`, then the existing bundle is written with the trace files beside `report.json`. **INFERRED.**
- A killed run leaves an unterminated trace. The existing rule (last partial line discarded, join `Partial`) covers this. **INFERRED** (`unit-trace.md`, "No end line").
- The `#457` bundle is the shareable unit. Trace files join it only after the allowlist projection in section 7.2. **INFERRED.**

## 7. Safety boundary: shareable output, local output, and the register dump ruling

### 7.1 Current state, by location

- Unit trace records contain CPU registers and PCs, not memory bytes. **INFERRED** (`unit-trace.md`, "Record schema"). `CpuCheckpoint` has no memory field (`src/PSXRecomp.Core/Diagnostics/CpuCheckpoint.cs:17-25`), but it does carry `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc` (section 1.4). **INFERRED.**
- `ExecutionDiagnosticReport` has no field for input payloads, RAM, save data, or paths. **INFERRED** (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:8-10`). It does carry `ProductRevision`, `Revision`, and `FramebufferSha256` (section 7.6).
- `environment.json` is serialised whole into the shareable bundle (`src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:52`). It includes `OsVersion`, a free-text field (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:18`, `:39`). **INFERRED.** This is a shareable-path finding.
- The shareable diagnostic log admits an `opcode` key, which is a raw instruction word (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:28`), and the writer emits it (`src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:53`). The CLI passes an empty array, so the CLI bundle does not reach it today (`src/PSXRecomp.Cli/RunCommand.cs:201`). The writer API does. **INFERRED.**
- BIOS content diagnostic reports counts and provenance only. **INFERRED** (`src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:81`).
- **Exposure 1 (INFERRED):** `LoadedCodeMismatchDiagnostics` writes `windowAot`, `windowRam` (5 words each, `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:296-297`), and `aotSha256` and `ramSha256` (`:293-294`). The window words are instruction words from the loaded image and from guest RAM. The comment calls the window bounded sampling. Bounded is not the same as redacted. Whether this output is ever published is **UNVERIFIED.**
- **Exposure 2 (INFERRED): raw instruction words in probe output.** The writers W1, W2, W3, W4, W8, W9, and W12 carry them (section 7.6 lists each occurrence): `stop.lastFetches` (`:1013`), `stop.lastTransfers` (`:1014`), `captured.word` (`:346`), `gte.lastUnsupportedCommand` (`:435`), `accounting.unsupportedOpcodes[].word` (`src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:197`), `ProbeGuestState.Describe().instructionWord` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:101`), the MDEC `value` and `command` words (`src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:43-56`), and the fallback export `ramWord` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:968`). Whether any of them comes from the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical; `BiosAddressGuard.PhysicalBase` at `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:103`) is **unknown**: no measured evidence exists, and it is not guessed (Q11 provisional ruling). The probe boots OpenBIOS, so BIOS-range fetches are a design expectation (INFERRED), not a finding.
- **Exposure 3 (INFERRED, local by current writer; not guaranteed; test pending):** `--dump-ram <path>` writes the raw 2 MiB main RAM to a user-named file (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:337-342`). The file is not in the `--json` document and not in the four-entry `#457` bundle (`src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:18-21`). That is what the current writers do. Nothing enforces it. The leakage guarantee is **UNVERIFIED** until the test in section 7.5 exists.
- **Exposure 4 (INFERRED, owner ruling):** full register dumps. The owner ruling removes them from shareable output. They appear in: `stop.gpr`, `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1010-1012`); `captured.gpr` (`:347`); the console `Stop:` line (`:446-449`); `--retire-out` lines (`:328-333`); `ProbeGuestState.Describe().cpu` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:35-36`), and therefore `boundaries`, `firstInterrupt`, and `firstInterruptDifferential` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:726`, `:734`, `:735-736`); the checkpoint and fallback state objects (section 1.4); and `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (`src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:40`). All of these are local only.

### 7.2 Shareable output is an explicit allowlist

**Rule.** A field is shareable only if it appears in this allowlist. A field not listed is excluded. Adding a field requires editing this list. The required test (section 7.5) will enforce the list once it is written. It does not exist yet. **INFERRED.**

Each allowed class has one rule:

| Class | Rule | Allowed fields |
|---|---|---|
| PC (guest address) | 32-bit unsigned, written as `0x` plus 8 hex digits. It names where execution was, not what the bytes were. | `stop.pc`; `captured.pc`; `guestPc` (top level); `ExecutionDiagnosticReport.GuestPc`; `milestones.TitleEntryPc`; `ExecutionDiagnosticLog` `guestPc` and `guestAddress` context keys; `captured.lastTransfers` (PC-to-PC pairs only); `stop.lastTransfers` after removing `From.Word`; `LoadedCodeMismatchDiagnostics` `pc` and `windowStartPc`; `RuntimeHostDiagnostic` `FinalPc`, `LastAotBlockPc`, `TransferSourcePc`, `TransferTargetPc`; `RuntimeHostRunMetrics` `LastFallbackEntryPc`, `LastFallbackReturnPc`; `interpretedEntries` (an array of PCs, `:692`); `loadedImages[].skippedEntries` (PCs, `:690`); `referenceBoundariesMissing` (PCs, `:732`); the keys of `atBoundary` (`:694`) and `referenceSecondsAtBoundary` (`:733`); `stopAt.pc` (`:679`); `fallback-targets` `targets[].pc` and `lastReturnPc`, and `outcome.stopAt` (by writer rule, local, see W8) |
| Address (device or RAM location, not data) | 32-bit unsigned hex, as above. Names a location, not contents. | `RuntimeHostRunMetrics.LastMmioPhysicalAddress`; `LoadedCodeMismatchDiagnostics.ramPage` (a page index, written as hex) |
| Classification code | A value from a closed enum, or a registered `DiagnosticCode` (`^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*$`, `docs/architecture/diagnostics.md:55`). Never free text. | `diagnosticCode`; `executionState`; `kind`; `backend`; `engine`; `standalone`; `milestones` booleans (`ResetVectorFetched`, `KernelRunningFromRam`, `VectorsInstalled`, `ExceptionVectorExecuted`, `ShellEntered`); `bootVerified`, `kernelBooted`, `titleStarted`; `milestones.firstUnexpectedException.excCode` (exception code 0-31); `ExecutionDiagnosticReport` `Outcome`, `State`, `DiagnosticCode`; the Q15 closed-set identifiers (section 7.8); `RuntimeHostDiagnostic` `FallbackOutcome`, `FallbackRejectReason` (only if the writer enforces a closed set, else excluded), `ClassifiedExitCode`, `NativeTerminationReason`, `OrchestratorState`, `OrchestratorDiagnosticCode`, `TimedOut`, `FallbackEnabled`, `FallbackAttempted`, `TargetHasCompiledBlock`, `TargetInsideImage`, `LastMmioWasWrite`; trace `termination`, `status`; `milestoneComparison.match` (boolean) |
| Count | Non-negative integer. | `segmentsRetired`; `gte.commandsExecuted`; `build` counts (`imageInstructions`, `nativeInstructions`, `blocks`, `roots`, `staticFallbackTargets`, `symbols` as a count, `:681-689`); `loadedCodeVersions` (`:691`); `loadedImages[].blocks` and `nativeInstructions` (`:690`); `aot.generatedCBytes`, `aot.artifactBytes` (`:725`); `milestones.ShellEnteredAtFetch`, `milestones.InterruptEntries`, `milestones.SyscallEntries`; `milestones.firstUnexpectedException.atFetch`; `atBoundary` values `nativeInstructions`, `mmioRoundTrips`, `timeReportRoundTrips` (`:597-598`); `retired` (trace); `comparedWords`, `rejections`, `fullMatches`, `versionCount`, `version`, `ramHashChanges` (`LoadedCodeMismatchDiagnostics`); the range pairs `firstMismatchIndexRange`, `mismatchedWordsRange`; `RuntimeHostRunMetrics` `MmioAccesses`, `TimeReports`; `RuntimeHostDiagnostic` `AotRetiredInstructions`, `FallbackRetiredInstructions`, `FallbackTransitionCount`; `ExecutionDiagnosticReport.SegmentBudget`; `frame.nonZeroPixels` (count only, not the frame) |
| Digest fields (SHA-256, bare lowercase 64-character hex, section 4 encoding) | No member is shareable as a class member. A digest may be shared only if it is a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b) (owner ruling 4; section 7.7). No field in section 7.6 is such a digest (INFERRED). Every other digest is NOT SHAREABLE: the firmware digest (Q12), the commercial game-input and EXE digests (owner ruling 5), non-commercial game-input and non-commercial disc-image digests (owner rulings 2 and 3), commercial disc-image digests (owner ruling 4), frame digests (owner ruling 1), and the built-artifact digest (`artifactSha256`, NOT SHAREABLE, owner ruling). Digests of a partial window, a region, RAM, VRAM, scratchpad, or any device memory are not allowed (section 7.3). | None. Internal evidence only: the firmware digest (`sha256`, NOT SHAREABLE, Q12). NOT SHAREABLE (owner ruling): the artifact digest (`ExecutionDiagnosticReport.ArtifactIdentity` after prefix stripping, `artifactSha256`). |
| Duration | Milliseconds: the projection rounds to 0.1 ms. `timings` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:712-724`) is already rounded by the writer, and the projection rounds it again, which leaves it unchanged. `precompileMilliseconds` (`:693`) is not rounded by the writer, so the projection rounds it. Seconds: the projection rounds to 0.1 s (`atBoundary.seconds`, `:597`). Wall clock is not written as a timestamp. | `RuntimeHostRunMetrics.WallMilliseconds`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds` (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:146-150`); probe `timings` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:712-724`, read, rounded to 0.1 ms); `precompileMilliseconds` (`:693`, rounded by the projection); `atBoundary` values `seconds` (`:597`, rounded to 0.1 s by the projection) |
| Identifier | Session ID (section 4), 32-hex GUID. | `session` (new per-record field, section 2) |

Fields that are default-deny (not allowed until a decision or a read) are in section 7.6, under "default-deny".

### 7.3 Excluded fields (shareable output must not contain these)

| Excluded class | Fields | Why |
|---|---|---|
| General registers (owner ruling) | `stop.gpr` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1012`); `captured.gpr` (`:347`); `gpr[32]` in every `checkpoint`; `gpr` in the native `unit` state and fallback `entry`/`exit` objects (`unit-trace.md`); `retire-out` `gpr` (`:328-333`); `ProbeGuestState.Describe().cpu` GPR entries (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:35-36`); console `Stop:` line (`:446-449`); `boundaries` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:726`, each value a `Describe()` with the `cpu` map); `firstInterrupt` (`:734`, both `Describe()` objects); `firstInterruptDifferential` (`:735-736`, a `Compare` result with `cpu` mismatch values); `differential` (`:727-731`, `Compare` results with `cpu` mismatch values) | Full register dump. Local only (owner ruling). |
| COP0 state (owner ruling) | `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`:1010-1011`); `sr`, `cause`, `epc` in every `checkpoint` and fallback state; `ProbeGuestState.Describe()` `sr`, `cause`, `epc` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107`) and `cpu` COP0 entries (`:36`); `badvaddr` (`:36`); `retire-out` `sr`, `cause`, `epc` (`:328-333`); `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (`src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:40`); `boundaries`, `firstInterrupt`, `firstInterruptDifferential` (the same `Describe()` and `Compare` objects, `:726`, `:734`, `:735-736`) | COP0 full state. Local only (owner ruling). |
| HI and LO | `hi`, `lo` in checkpoint and state objects; `ProbeGuestState.Describe().cpu` entries `hi` and `lo` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:35-36`); `retire-out` `hi`, `lo` (`:328-333`) | Part of the register dump. Excluded as a register dump. **INFERRED** classification, pending Q13. |
| Raw instruction words | `captured.word` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:346`); `stop.lastFetches` (`:1013`); `stop.lastTransfers` `From.Word` (`:1014`); `gte.lastUnsupportedCommand` (`:435`); `accounting.unsupportedOpcodes[].word` (`src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:197`); `accounting.unsupportedOpcodes[].opcode` (`:197`), since it is the instruction's opcode field; `ProbeGuestState.Describe().instructionWord` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:101`); `ExecutionDiagnosticLog` `opcode` context key (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:28`); MDEC `value` and `command` (`src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:43-56`); `mmio.value`; `TransferOpcode` (`RuntimeHostDiagnostic`); the fallback export `ramWord` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:968`); `execution.topFallbackTargets` strings (`:704-705`, which carry the cause text and counts, see default-deny) | Instruction encoding from guest memory. Excluded from shareable output. |
| RAM contents | `windowAot`, `windowRam` (`src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:296-297`); `OpenBiosProbeDifferential` RAM context `interpreterBytes`, `hostBytes` (`:138-143`); `--dump-ram` file (W7); any memory bytes; any run of 8 or more consecutive hex words | Raw loaded-image and guest-RAM words. |
| Whole-RAM, VRAM, and device digests (owner direction) | `ramSha256` (`src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:294`; `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:104`); `interpreterRamSha256`, `hostRamSha256` (`:154-155`); `vram_sha256` (`:83`); `scratchpad_sha256` (`:41`, `:106`) | A digest of RAM or device memory is a region digest, and is banned by the RAM-region rule in section 7.3. This is a banned class, not a default-deny. |
| Free text | `diagnosticMessage` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:677`); `fetchIndices` (`:672`, a constant explanation string); `OsVersion` (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:18`, `:39`); `Title` (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs`); `FailureExceptionType` (`RuntimeHostDiagnostic`); `FallbackRejectReason` unless a closed set is enforced; the stdout summary text of W12 (`:859-874`); the stderr text of W13 and W14 | Free text is not a classification. It can carry host, title, or path content. |
| Symbol, file, and disc names | `accounting.hotSymbols` symbol names; `hotEntries[].symbol` and `hotPcs[].symbol` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:861`, `:866`); `loadedImages[].name`; `--list-disc` file names (W11) | User-supplied text (a symbol file or the user's disc). Not a classification. |
| Host and environment identity | Environment and build fields (`environment.json`: `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, `TargetArchitecture`, `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion`, `OsVersion`; report: `ProductRevision`, `Revision`) are governed by the Q15 ruling (section 7.8). They are shareable only as closed-set values; an unmatched value is written as `unknown`. Free text is excluded. `OsVersion` has no closed set, so it is excluded until Q15(b) is decided. Its local value follows owner policy 1: the observed OS description (`RuntimeInformation.OSDescription`, `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionEnvironmentCollector.cs:23`), or `unknown` if it cannot be obtained, never a guessed value. The current code does not do this: `ExecutionEnvironmentReport` rejects an empty value (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:28`). Writing `unknown` would be a code change and a public-contract change to the `psxrecomp.execution-environment.v1` output. It requires the separate compatibility review, is pending that review, and is neither approved as an implementation nor made by this document. The `runId` value is host run identity (Q2) and is excluded. | Host identity, except the closed-set values. |
| Trace-only values | `t` timestamps (default null; Q10); `skipped` counts are allowed as counts | Timestamps are opt-in and excluded from share-ready bundles (Q10). |
| Device register values and device digests | The `devices` map of `ProbeGuestState.Describe()` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:100`), built from `CaptureDevices` (`:37`), with `scratchpad_sha256` (`:41`), `gpustat` (`:80`), `vram_sha256` (`:83`), and the `cd_*` entries (`cd_index`, `cd_status`, `cd_if`, `cd_ie`, `cd_responses`, `cd_data_bytes`, `cd_last_command`, `cd_reading`, `cd_mode`, `cd_irq_generation`, `:87-89`); the `devices` mismatch objects of `Compare` (`:119`) | Device register state is local (the same class as the boundary-log device registers, W10). Device digests are banned by the digest rule (section 7.3, whole-RAM, VRAM, and device digests). |
| Frame pixels | `--dump-frame` PPM (W6); `frame` pixel content; the `frame` digest (NOT SHAREABLE, owner ruling 1, section 7.7) | Pixel content is game output. Local only. |
| Disc and program contents | `--list-disc` (W11) and `--fallback-targets` (W8) contents beyond the classes above | Derived from the user's disc. Local only. |

### 7.4 Local/share boundary (rule)

1. **Shareable output** is produced only by one writer, the shareable projection. It builds a document from the allowlist in section 7.2 and nothing else. A document is shareable only when this projection built it. **INFERRED.**
2. **Local output** is any other output. The writers W1-W14 of section 1.8, the trace files, `environment.json` as currently written, the mismatch report as currently written, and the `--json` documents of the probe are all local. Local output may contain any field, including the excluded classes of section 7.3. **INFERRED** (these outputs exist and contain those fields today).
3. A local output must not be attached to a bundle, a report, or an issue. The `#457` bundle writer accepts only shareable-projection documents. **INFERRED** (the writer change does not exist; today the bundle writer takes typed records and does not check provenance).
4. **A local-only classification is not a guarantee of non-leakage.** Calling an output "local" is a label. It stops leakage only if the shareable projection is tested to emit nothing outside the allowlist, and the bundle writer is tested to refuse non-projection input. Until the tests in section 7.5 exist and pass, the leakage guarantee is **UNVERIFIED**.

### 7.5 Required tests and the fixtures they use

Fixtures are not written from section 7.3. Each fixture is generated by calling the real writer under test on a synthetic run, so the test covers what the writer actually emits. **INFERRED** (none of these tests exist).

Required tests:

1. **`ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_field`** (new file under `src/PSXRecomp.Tests/Diagnostics/`). Fixtures: run the probe document builder (W1 and W2 code paths), the summary path (W12), `ProbeGuestState.Describe()` and `Compare`, `OpenBiosProbeAccounting`, `OpenBiosBootReport` (milestones), `RuntimeHostDiagnostic` and `RuntimeHostRunMetrics`, `ExecutionEnvironmentReport`, `ExecutionDiagnosticReport`, and `LoadedCodeMismatchDiagnostics.ToJsonNode()`. Feed their outputs to the projection. The test checks the projection's output, not the fixture.
2. **`ShareableOutputAllowlistTests.Shareable_writer_keys_are_allowlisted`.** Walks the projection's output and fails on any key path not in the section 7.2 allowlist.
3. **`ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_key_or_value`.** Applies the matching rules below to the projection's output.
4. **`ShareableOutputAllowlistTests.Bundle_writer_refuses_local_output`.** Asserts `ExecutionDiagnosticBundleWriter.Write` refuses a document that the projection did not build.
5. **`ExecutionDiagnosticBundleWriterTests.Bundle_writer_drops_opcode_context_key`** (required design change C2). Builds a bundle from a diagnostic carrying an `opcode` context key, and asserts the `diagnostics.log` entry contains no `opcode=` text and no value from that key. The writer drops the key: keys not on the allow-list are removed by the filter at `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:78-79`, against the allow-list at `:17-30`, and the `opcode` key is removed from the allow-list. This is a drop, not a rejection, so the test name and this description both say "drops". **INFERRED.**
6. **`WriterInventoryTests.Probe_writers_match_section_1_8`.** Greps the probe sources for `output.WriteLine`, `error.WriteLine`, `Console.Error`, and the file-writing calls (the search in section 1.8) and fails if a call is not listed as a W-row or inside the W13 line range. This is the test that makes the completeness claim of section 1.8 checkable. The search pattern must include the null-conditional forms `error?.WriteLine` and `output?.WriteLine`: a search for the literal `error.WriteLine` misses the `observer-failed` call at `origin/main` `OpenBiosProbeCommand.cs:567`. **INFERRED.**

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
| W1 Interpreter probe `--json` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:419` (write), `:421-437` (object) | Mixed; see the W1 field table | Local |
| W2 Generated-host `--json` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:663-742` (object), `:743-746` (serialise), `:755` (write) | Mixed; see the W2 field table | Local |
| W3 Console output, interpreter path | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:441-450` | Full GPRs and COP0 (`Stop:`); firmware digest (`:443`); milestones | Local |
| W4 `--retire-out` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:317`, `:328-333` | Full GPR, hi, lo, COP0 per fetch | Local (banned classes) |
| W5 `--pc-histogram` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:368-377` | `totalRamFetches`, PCs with counts, bucket starts with counts | Share-candidate fields (PC and count), local by writer; file comment says "Local diagnostic only" |
| W6 `--dump-frame` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:378-411` | Framebuffer pixels (PPM) | Local; pixels excluded (section 7.3) |
| W7 `--dump-ram` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:81`, `:172`, `:337-342` | Raw 2 MiB RAM | Local; banned |
| W8 `--fallback-targets` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:660`, `:952-977` | See the W8 field table | Local |
| W9 `--mdec-events` | `src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:43-56` | Fields per line (`src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:50-53`): `engine` (string, classification by the caller's engine name), `seq` and `g` (counts), `kind` (MDEC event kind name, classification), `value` (raw word, banned), `pc` and `lastFallbackFetchPc` (PCs or null), `command` (raw word, banned), `remaining` and `fifoWords` (counts), `control` (device register value, excluded under the device-register row of section 7.3), `busyBefore` and `busyAfter` (booleans) | Local. Share-candidates by writer rule: `engine`, `seq`, `g`, `kind`, `pc`, `lastFallbackFetchPc`, `remaining`, `fifoWords`, `busyBefore`, `busyAfter`. Excluded: `value`, `command`, `control`. |
| W10 `--boundary-log` | `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:40-56` | Device registers, `vramSha256` (banned), boundary PCs | Local; VRAM digest banned; device register values local; default-deny for the rest. Read: the call `Describe(engine, devices, vblank, cycles)` and the line write (`src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:40-56`). Not read: the body of the boundary-log `Describe` method, which is a different method from `ProbeGuestState.Describe()` (`src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107`, read). See Q16. |
| W11 `--list-disc` | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:251-289` | Disc file names | Local; file names excluded (section 7.3); default-deny for the rest. Not read: the output shape of the listing (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:251-289`). See Q16. |
| W12 Non-JSON summary | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:747-751`, `Summarize` at `:838-884` | Hot entries with symbol names (`:861`), hot PCs with symbol names (`:866`), the differential result and `firstMismatch` (`:874`), timings (`:879-881`, durations), AOT byte counts (`:882`, counts), `consistency.differentialPass` (`:883`, a boolean inside the default-deny `consistency` object) | Local; symbol names and mismatch detail excluded; PCs, counts, and durations share-candidate by writer rule |
| W13 stderr (35 `error.WriteLine` calls, `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:35-657`) | Full list in section 1.8 W13 | Usage and argument echoes (argv tokens, `:52`, `:63`, `:77`, `:102`, `:108`), local paths (`:154`, `:242` `ex.Message`), failure text (`:233`, `:235`), progress with seconds and counts (`:529`, `:608`, `:615`), status (`:651`, `:657`) | Local; free text, excluded |
| W14 `--compare-boundary-logs` (#850, `origin/main` only) | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:278-345` (origin/main) | Stdout: `leftBoundaries`, `rightBoundaries`, `commonBoundaries`, `firstMismatch.index`, `firstMismatch.vblank` (counts), `firstMismatch.field` (record key name). Stderr: messages with `ex.Message` | Local output (rule 2 of section 7.4). Share-candidate fields by field rule: the counts, `index`, `vblank`, and `field` (the closed set of `ComparedFields()` names is UNVERIFIED as written). Stderr text excluded. |
| Trace files (native `<path>`, fallback `<path>.fallback`) | `unit-trace.md`; `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs` | Unit and fallback records; CPU state with `gpr` | Local; PC and count fields share-candidates by writer rule |
| Checkpoint records | `src/PSXRecomp.Core/Diagnostics/CpuCheckpoint.cs:17` | `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `pc` | Local only (owner ruling) |
| Loaded-code mismatch report (`#773`) | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:277-300`; written by `src/PSXRecomp.RuntimeHost/Program.cs:624` path | See field table | Local (banned fields present) |
| Runtime host diagnostic and metrics (`#763`) | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:24`, `:146-160` | See field table | Local until projected |
| `report.json` (`#457`) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:51`; `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:14-25` | See field table | Share-candidate fields, except the game-input digest (NOT SHAREABLE, Q17) and the artifact digest (NOT SHAREABLE, owner ruling); written to the bundle without a projection today |
| `environment.json` (`#457`) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:52`; `src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:11-45` | See field table | Excluded: `OsVersion` and every other single-line free-text field (no closed set exists); written whole today |
| `diagnostics.log` (`#457`) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:53`; `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:17-30` | Context keys, including `opcode` and `discHash` | `opcode`: share-candidate after the C2 change, which removes it from the allow-list. `discHash` (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:20`): NOT SHAREABLE (owner rulings 3 and 4). Only a digest of a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b) may be shared (section 7.7); the C2 change does not clear it. |
| `issue.md` (`#457`) | `src/PSXRecomp.Core/Diagnostics/ExecutionIssueMarkdown.cs:36` | Derived from `report` | Same classes as `report.json` |

**W1 field table (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:421-437`).**

| Field | Class | Label |
|---|---|---|
| `kind`, `backend`, `executionState`, `titleStarted`, `bootVerified`, `kernelBooted`, `diagnosticCode` | Classification | Share-candidate |
| `guestPc` | PC | Share-candidate |
| `segmentsRetired` | Count | Share-candidate |
| `sha256` (`:423`, firmware digest) | Firmware digest | NOT SHAREABLE: genuine BIOS/firmware digest, internal evidence only (Q12, section 7.7) |
| `stop` (`:432`) | Mixed (see the `stop` rows below) | Mixed |
| `captured` (`:433`) | Mixed (see the `captured` rows below) | Mixed |
| `gte` (`:435`) | `commandsExecuted`: count; `lastUnsupportedCommand`: raw instruction field | `commandsExecuted`: share-candidate; `lastUnsupportedCommand`: excluded |
| `frame` (`:437`): `width`, `height`, `nonZeroPixels` | Counts | Share-candidate |
| `frame.sha256` (`:437`) | Frame digest, not a whole-artifact digest | NOT SHAREABLE: a frame digest is not a synthetic-medium digest (owner ruling 1) |
| `milestones` (`src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:10-40`) | Booleans, counts, `TitleEntryPc` (PC), `FirstUnexpectedException` (below) | Booleans and counts share-candidate; `TitleEntryPc` share-candidate (PC); `firstUnexpectedException.excCode` and `.atFetch` share-candidate; `.epc`, `.badVAddr`, `.cause` excluded (COP0) |

**`stop` rows (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1004-1014`).**

| Field | Class | Label |
|---|---|---|
| `stop.pc` | PC | Share-candidate |
| `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` | COP0 | Excluded (owner ruling) |
| `stop.gpr` | General registers | Excluded (owner ruling) |
| `stop.lastFetches` | Raw instruction words | Excluded |
| `stop.lastTransfers` | PC-to-PC pairs with `From.Word` | PCs share-candidate; `From.Word` excluded |

**`captured` rows (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:344-350`).**

| Field | Class | Label |
|---|---|---|
| `captured.pc` | PC | Share-candidate |
| `captured.word` | Raw instruction word | Excluded |
| `captured.gpr` | General registers | Excluded (owner ruling) |
| `captured.lastTransfers` | PC-to-PC pairs | Share-candidate |

**W2 field table (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:663-742`).**

| Field | Location | Class | Label |
|---|---|---|---|
| `kind`, `engine`, `standalone` | `:665-667` | Classification | Share-candidate |
| `sha256` | `:668` (`probe.Hash`) | Firmware digest | NOT SHAREABLE: genuine BIOS/firmware digest, internal evidence only (Q12, section 7.7) |
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
| `boundaries` | `:726` (`Describe()` per PC) | Excluded (cpu map, `instructionWord`, `ramSha256`, `scratchpadSha256`, `sr`/`cause`/`epc`, and the `devices` map of `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:100`, see the device row in section 7.3); residual `pc`, `atFetch`, `guestCycles` share-candidate by field rule | Mixed; the object as a whole is local |
| `differential` | `:727-731` (`Compare` per PC, or a string) | Mismatch objects carry register and RAM values (excluded) | Local |
| `referenceBoundariesMissing` | `:732` | PC array | Share-candidate (PC class) |
| `referenceSecondsAtBoundary` | `:733` | PC-keyed seconds (value type not read here) | Default-deny until the value type is read |
| `firstInterrupt` | `:734` | `interpreter` and `host`: `Describe()` objects | Local (cpu map, `instructionWord`, and the `devices` map of `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:100` excluded, see the device row in section 7.3); residual `pc`, `atFetch`, `guestCycles` share-candidate by field rule |
| `firstInterruptDifferential` | `:735-736` | `Compare` result (register and RAM mismatch values), or a string | Local |
| `milestoneComparison` | `:737-741` | `match` (boolean); `interpreter` (milestones object, classes per W1) | `match`: share-candidate; `interpreter`: as milestones |
| `consistency` | `:744` (from `Consistency(node, boundaryPcs, probe.StopAt)`) | Built from the document; shape not read | Default-deny until read |

**W8 fallback export fields (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:961-977`).**

| Field | Class | Label |
|---|---|---|
| `format` | Classification (`ObservedRootPlanner.TargetFormat`) | Share-candidate |
| `sha256` (`:973`, the firmware digest `hash`) | Firmware digest | NOT SHAREABLE: genuine BIOS/firmware digest, internal evidence only (Q12, section 7.7) |
| `run` (`:974`) | `ObservedRootPlanner.RunDocument(DescribeRun(...))` (`src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:46-51`, `:254`, `:262`): the firmware digest, the manifest identity, the disc digest, and `probeOptionsSha256`, a digest of `--roots`, `--code-bytes`, `--segments`, and `--segment-budget` (`ProbeOptionsSha256` at `src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:254`). The whole object is one unit. | Not shareable as a whole object. It carries a disc digest (NOT SHAREABLE; only a digest of a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b) may be shared, section 7.7) and the probe-options digest (NOT SHAREABLE: not a synthetic-medium digest). The firmware digest inside it is NOT SHAREABLE (genuine BIOS/firmware digest, internal evidence only, Q12) and must be excluded from the object. |
| `outcome` (`:975`): `state` (classification), `complete` (boolean), `stopAt` (PC) | Mixed | `state`, `complete`: share-candidate; `stopAt`: share-candidate (PC) |
| `targets[]` (`:961-969`): `pc`, `lastReturnPc` (PC); `cause` (classification); `entries`, `instructions` (counts); `ramWord` (raw RAM word) | Mixed | `pc`, `lastReturnPc`, `cause`, `entries`, `instructions`: share-candidate by writer rule, local by writer; `ramWord`: banned |

**Accounting (`src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:195-198`; the W2 `accounting` object at `:710-711`).**

| Field | Class | Label |
|---|---|---|
| `unsupportedOpcodes[].excCode` | Classification | Share-candidate |
| `unsupportedOpcodes[].pc` | PC | Share-candidate |
| `unsupportedOpcodes[].atFallbackFetch` | Count | Share-candidate |
| `unsupportedOpcodes[].word`, `.opcode` (both on `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:197`) | Raw instruction fields | Excluded |
| `hotSymbols` symbol names | Symbol names | Excluded |
| Other `accounting` fields | Shape not read | Default-deny until read (Q16) |

**`milestones` detail (`src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:10-40`).** The shape is read from the record declaration (not from a serialised sample). The per-field classes are those in the W1 table above.

**`environment.json` fields (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:11-45`).**

| Field | Class | Label |
|---|---|---|
| `Schema` | Classification | Share-candidate |
| `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, `TargetArchitecture` | Free text: `ValidateSingleLine` (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:79-87`) rejects CR and LF and trims, so any single-line string is accepted. No closed set exists yet; adding one is a required change. | Closed-set value under Q15 (section 7.8): a matched value is shareable; an unmatched value is written as `unknown`. |
| `OsVersion` | Free text (`:18`, `:39`); local value is the observed OS description or `unknown` (owner policy 1) | Excluded from shareable output |
| `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion` | Build and tool identifiers | Closed-set value under Q15 (section 7.8); an unmatched value is `unknown` |

**`report.json` fields (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:14-25`).**

| Field | Class | Label |
|---|---|---|
| `Schema` | Classification | Share-candidate |
| `ProductRevision`, `Revision` | Build identifiers | Closed-set value under Q15 (section 7.8); an unmatched value is `unknown` |
| `InputSha256` | Game-input digest (section 7.7) | NOT SHAREABLE: commercial game input or EXE (owner ruling 5), and non-commercial game input (owner ruling 2; section 7.7) |
| `Title` | Free text (game title) | Excluded |
| `ArtifactIdentity` | Digest of the built artifact, with `sha256:` prefix | NOT SHAREABLE: built-artifact digest (owner ruling) (prefix stripped, section 4) |
| `Outcome`, `State` | Classification | Share-candidate |
| `GuestPc` | PC | Share-candidate |
| `DiagnosticCode` | Classification | Share-candidate |
| `SegmentBudget` | Count | Share-candidate |
| `FramebufferSha256` | Frame digest | NOT SHAREABLE: not a synthetic-medium digest (owner ruling 1) |

**Loaded-code mismatch report fields (`src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:277-300`).**

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

**Runtime metrics and host diagnostic (`src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:146-160` and the record at `:24`).**

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

### 7.7 Recorded owner rulings (Q12, Q17) and digest publication policy

Q12 and Q17 are recorded owner rulings, recorded as given. This section also holds the digest publication policy, which applies to every digest in this document. Q11 and Q15 are provisional and are in section 7.8. Where a ruling leaves a value open, this section says so and does not fill it in.

**Q12 (firmware digest): NOT SHAREABLE (internal evidence only).** The owner ruling is that the SHA-256 of the firmware ROM file is not published. It stays internal evidence only and must not appear in any shareable document (`sha256` at `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:423`, console `:443`, fallback export `:973`). Raw BIOS bytes, ROM windows, and secrets are excluded in every case. Input kind: the BIOS ROM file as loaded (whole file bytes). Algorithm: SHA-256. Normalization: none, written as bare lowercase hex. Provenance: `sha256` is set from `Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()` at `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:206` (at `2e3ecad`), which reads the ROM file given on the command line. A digest identifies a file; it does not prove provenance or legitimacy. **Owner ruling: not published.**

**Digest publication policy (single source for every digest in this document).** Every digest in this document applies this policy.

- **Shareable (the only shareable class, owner ruling 4):** the SHA-256 of a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b). Only the digest is shared under this rule.
- **NOT SHAREABLE:**
  - Digests of commercial disc images. Reason: not a synthetic medium (owner ruling 4).
  - The commercial EXE digest, the commercial game-input digest, and any commercial BIOS digest. Reason: owner ruling 5.
  - The genuine (純正) BIOS/firmware digest. Reason: the owner ruled it internal evidence only (Q12).
  - Built-artifact digests (`ArtifactIdentity`, `artifactSha256`). Reason: owner ruling 5.
  - Non-commercial game-input and non-commercial disc-image digests (owner rulings 2 and 3), the frame digest (owner ruling 1), and any other digest. Reason: outside the shareable class (ruling 4).
- **Never published, in any class:** raw ROM, BIOS, or game data. Digests of region memory (RAM, VRAM, scratchpad, device memory, AOT window) are banned (section 7.3).

**Q17 (digest scopes): four different values, not one.** The digests below are different scopes. They must not be treated as the same value, and a status for one does not transfer to another.

| Digest | What is hashed | Field or source | Status |
|---|---|---|---|
| Game-input digest | the file the user supplied to `run --report`, as loaded (the whole CHD or EXE file bytes, depending on input type; `src/PSXRecomp.Cli/CliInput.cs:100-112` hashes the whole CHD file) | `ExecutionDiagnosticReport.InputSha256` (`src/PSXRecomp.Cli/RunCommand.cs:54-58`, `:58`); input-type rule INFERRED in `docs/runtime/loaded-code-identity.md:45` | NOT SHAREABLE: commercial game input or EXE (owner ruling 5), and non-commercial game input (owner ruling 2; section 7.7) |
| EXE digest | the main executable file bytes | no separate field; equals the game-input digest when the input is an EXE | NOT SHAREABLE: commercial game input or EXE (owner ruling 5), and non-commercial game input (owner ruling 2; section 7.7) |
| Disc-image digest | the whole disc image file (`FileSha256(discPath)`, `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:224`) | `discHash` (`src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:47`; the key is still on the diagnostics allowlist at `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:20`, so a future writer would reach diagnostics.log. No writer of `DiagnosticContextKeys.DiscHash` is found at 2e3ecad [INFERRED: grep at 2e3ecad]. The SHA-256 of the staged copy is computed at `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:391`, and the value is passed as a positional argument at `FirmwareDistribution.cs:451`, bound to the `DiscSha256` parameter of `FirmwareBootManifest.cs:35` (staging artifact)); the disc digest inside W8 `run` (`:974`) | Shareable only if it is a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b). A non-commercial disc image is NOT SHAREABLE (owner ruling 3). A commercial disc image is NOT SHAREABLE (owner ruling 4: not a synthetic medium) |
| Firmware digest | the BIOS ROM file as loaded | `sha256` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:423`, `:443`, `:973`) | NOT SHAREABLE: genuine BIOS/firmware digest, internal evidence only (Q12) |
| Artifact digest | the built artifact file | `ExecutionDiagnosticReport.ArtifactIdentity` (`src/PSXRecomp.Cli/RunCommand.cs:191`), `artifactSha256` | NOT SHAREABLE: built-artifact digest (owner ruling). Publication of an artifact that contains title-derived code is covered by that ruling. This build's output, not a user-owned input |
| Frame digest | displayed frame pixels | `frame.sha256` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:437`), `FramebufferSha256` | NOT SHAREABLE: not a synthetic-medium digest (owner ruling 1) |
| Region digests | RAM, VRAM, scratchpad, device memory, AOT window | `ramSha256`, `vram_sha256`, `scratchpad_sha256`, `aotSha256`, `interpreterRamSha256`, `hostRamSha256` | Banned (section 7.3) |

Owner rulings, as applied in this section: commercial EXE and commercial game-input digests are NOT SHAREABLE (ruling 5); non-commercial game-input digests are NOT SHAREABLE (ruling 2). Only a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b) may be shared (ruling 4); a hash of a commercial disc or a commercial BIOS/ROM must never be published. The artifact digest is NOT SHAREABLE (ruling 5). The firmware digest is NOT SHAREABLE (Q12). The frame digest is NOT SHAREABLE (ruling 1), and the non-commercial disc image is NOT SHAREABLE (ruling 3).

**Owner policy 3, applied to the digests above.** The input kind is the "What is hashed" column. The algorithm is SHA-256 at the cited call sites. The whole-file hash body was read: `FileSha256` computes the SHA-256 of the whole file as lower-case hex (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:998-1002` at `2e3ecad`; `:1136-1140` at `12b6a31`) (**INFERRED**). The digest's definition is `SHA256.HashData(bytes)` at `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:206` and is passed as `hash` at `:224` (at `2e3ecad`; **INFERRED** at that commit). Line 973 is only the export use (`sha256 = hash`). Normalization is none: whole file bytes, written as bare lowercase hex, as `InputSha256` is validated (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:67-75`). Publication: the digest publication policy (section 7.7) applies. The firmware digest is NOT SHAREABLE (Q12). The artifact digest is NOT SHAREABLE (owner ruling). The commercial game-input and EXE digests are NOT SHAREABLE (Q17). The disc-image digest is shareable only if it is a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b). A non-commercial disc image is NOT SHAREABLE (owner ruling 3), and a commercial disc image is NOT SHAREABLE (owner ruling 4). The frame digest is NOT SHAREABLE (owner ruling 1). Region digests are banned. Raw BIOS, ROM, or other raw bytes are never published. A digest identifies a file. It does not prove provenance or legitimacy. This paragraph states owner-ruled items only (Q12, Q17, and rules 1 to 5 of section 7.7). It does not claim that any open item is ruled. It is not an evidence label.

### 7.8 Provisional rulings (Q11, Q15)

Q11 and Q15 are provisional. They are not final until the owner confirms them.

**Q11 (BIOS-range check): unknown unless measured.** A result is recorded only when a probe run measures it, and the measurement is recorded as a comment on issue #45. The required test (section 7.5) does not exist yet, so the issue comment is the record. If a test is added later, its output becomes the record. The comment holds the probe command with placeholders only (for example `<openbios.bin>` and `<hex-pc>`), and the result, and nothing else. No local path, user name, or machine detail may appear, because issue #45 is public. Without a measurement the result is `unknown`. This document does not fill it in, and no value is inferred from the design. The BIOS range is `0x1FC00000`-`0x1FC7FFFF` (physical). Status: provisional ruling (Q11); not final until the owner confirms.

**Q15 (build/tool identifiers and environment fields): versioned closed set.** The shareable form of these fields is a closed set, versioned by a schema identifier (proposed name `psxrecomp.build-identity.v1`, INFERRED). A value that matches its domain is shareable. A value that does not match is written as `unknown`. No value is inferred or completed from another field. Status: provisional ruling (Q15); not final until the owner confirms. The member lists are the owner's to decide. The domains below are a proposal (INFERRED):

| Field | Proposed domain | Unmatched value |
|---|---|---|
| `ProductRevision`, `Revision` | lowercase hexadecimal commit identifier, `^[0-9a-f]{7,40}$` | `unknown` |
| `DotNetVersion`, `CompilerVersion` | `^[0-9]+\.[0-9]+(\.[0-9]+)?$` | `unknown` |
| `RuntimeIdentifier` | the runtime identifiers the build defines (list to be given by the owner) | `unknown` |
| `OsPlatform` | `Windows`, `Linux`, `macOS` | `unknown` |
| `OsArchitecture`, `ProcessArchitecture`, `TargetArchitecture` | `x64`, `x86`, `arm64` | `unknown` |
| `OsVersion` | Excluded until Q15(b) is decided (as in sections 7.3 and 7.6). If Q15(b) adopts a pattern, the proposed domain is `^[0-9]+(\.[0-9]+){1,3}$`; the observed OS description may not fit that pattern, so Q15(b) must choose a domain that matches the observed value. | Local record: `unknown` when not obtained (owner policy 1; meaning approved). The current code rejects an empty value (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:28`); writing `unknown` is a public-contract change that requires the compatibility review and is pending that review. Not shareable while excluded. |
| `Schema` | a constant identifier (`psxrecomp.execution-environment.v1`, `src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:14`), not an environment value | not applicable |

Required change: the validator `ValidateSingleLine` (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:79-87`) accepts any single line today (INFERRED). It must be replaced by the closed-set mapping above before any of these fields is shareable. Free-text values must not reach the shareable output. The current treatment of `OsVersion` is: excluded until Q15(b) is decided, the same as in sections 7.3 and 7.6.

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, and `event` kinds. Do not invent a second format. **INFERRED** (rationale: the existing joiner, tests, and parsers already implement the rules).
2. Make `session` the single run identity for trace purposes. Retire `runId` (`src/PSXRecomp.RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them only as aliases. **INFERRED.**
3. Reuse the existing `artifactSha256` and `inputSha256` fields, both as bare lowercase hex. `inputSha256` is NOT SHAREABLE (owner rulings 2 and 5; section 7.7); `artifactSha256` is NOT SHAREABLE (owner ruling 5). Add no new artifact ID field (section 4). **INFERRED.**
4. **Required design changes, before any shareable output or bundle:**
   - **C2:** remove the `opcode` key from `ExecutionDiagnosticLog`'s allow-list (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:28`). The writer then drops it (test 5 of section 7.5, `Bundle_writer_drops_opcode_context_key`). Update `src/PSXRecomp.Tests/Diagnostics/ExecutionDiagnosticLogTests.cs:37` (the `InstructionOpcode` context entry) and `:48` (the expected `opcode=0xDEADBEEF` in the formatted line).
   - **M2:** `environment.json` is removed from the shareable bundle, or projected to the allowlist with `OsVersion` removed (`src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:52`). `ProductRevision`, `Revision`, `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion`, `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, and `TargetArchitecture` are governed by the Q15 ruling (section 7.8): closed-set values are shareable, and an unmatched value is written as `unknown`. A closed set is a required change, because `ValidateSingleLine` (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:79-87`) accepts any single line today.
   - **M4 and F-series:** the firmware digest (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:423`, console `:443`, fallback export `:973`) is NOT SHAREABLE (internal evidence only, Q12 owner ruling), so it must be excluded from every shareable projection. The fallback `run` object stays default-deny (section 7.6). `frame.sha256` (`:437`) and `FramebufferSha256` are NOT SHAREABLE (owner ruling 1). The commercial game-input and EXE digests are NOT SHAREABLE (owner ruling 5); non-commercial input is NOT SHAREABLE (owner ruling 2). The artifact digest is NOT SHAREABLE (owner ruling 5). Whole-RAM, VRAM, and device digests are banned (section 7.3).
   - **Unread shapes:** `execution`, `consistency`, `referenceSecondsAtBoundary`, `accounting` (other than the classified fields), the runtime fields in section 7.6, the W10 boundary-log `Describe` body, and the W11 `--list-disc` output shape are default-deny until read (Q16).
   - **M6:** tests that pin raw output must change with the fixes:
     - `src/PSXRecomp.Tests/Cli/OpenBiosProbeAccountingTests.cs:183` (`word`) and `:185` (`opcode`) pin the raw fields of `unsupportedOpcodes`. They stay on the local writer, and the projection test asserts absence.
     - `src/PSXRecomp.Tests/Recompiler/LoadedCodeMismatchDiagnosticsTests.cs:34-35` pin `windowAot` and `windowRam`. They stay on the local writer, and the projection test asserts absence.
     - `src/PSXRecomp.Tests/Diagnostics/ExecutionDiagnosticLogTests.cs:37` and `:48` pin the `opcode` key, as above.
   - **Exposure 3 (`--dump-ram`):** keep local. Test 4 of section 7.5 and the projection tests cover it.
5. Build the shareable projection (section 7.2) and the required tests (section 7.5) before any trace or bundle is shared. **INFERRED.**
6. Ship L1 (boundary) first. Its cost is near zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **INFERRED.**

**Blocker (stated, not fixed in this design).** The shareable projection cannot be finished until the default-deny fields are read and their owners decide them. The unread shapes are `execution` (including `timings = costs`), `consistency`, `referenceSecondsAtBoundary` (value type), `accounting` (other fields), `origins`, `FallbackInFlight`, `FallbackReturns`, `FallbackTargets`, the W10 boundary-log `Describe` body, and the W11 `--list-disc` output shape. This is a read-and-decide task, not a design gap. It is recorded as a blocker for the implementation phase, not for this design. **INFERRED** (the shapes were not read in this work).

## 9. Open questions and the decision each needs

Ownership note: the GitHub API shows no assignee on any issue read for this document (#45, #457, #49, #763, #773, #40, #42), and ADR-027 has no owner field. So no per-issue owner is recorded for any question below. **INFERRED.** The decision-maker named in each row is the repository owner (mao2009). Closed items are named as such. Each row names an open tracking item where one exists.

| # | Question | Decision point | Tracking item | Evidence label |
|---|---|---|---|---|
| Q1 | Is `session` a new optional field in `report.json` (schema bump to `v2`) or an optional addition within `v1`? | Repository owner (mao2009) | #457 is closed, so the report schema has no open tracking issue. Open item for this decision: #45. | INFERRED that v1 has no session field; INFERRED (#457 closed) |
| Q2 | Retire the standalone `runId` (`src/PSXRecomp.RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them as aliases? | Repository owner (mao2009) | #763 (open) and #773 (open) | INFERRED both exist and both are written |
| Q3 | For packaged artifacts (section 4.1), which identities does the trace header carry? Candidates: the native artifact executable hash, the `runtime/artifact-image.bin` sidecar hash (the image the host runs), the `runtime/runtime-host.json` hash, and `inputSha256` (only on the `run --report` path, `src/PSXRecomp.Cli/RunCommand.cs:54-58`). Should `inputSha256` be produced for packaged runs at all? | Repository owner (mao2009) | #739 (open; ADR-027 status Accepted, Issue #739) | INFERRED (ADR-027:52, :62-66, :70-71, :128-235 at 2e3ecad); INFERRED (`src/PSXRecomp.Cli/RunCommand.cs:54-58`) |
| Q4 | Are `windowAot`/`windowRam` (and `aotSha256`, `ramSha256`) in the loaded-code report kept in the local file? Under section 7.2 they are banned from shareable output. The remaining question is whether the local file keeps them. | Repository owner (mao2009) | #773 (open). The fix PR (#786) is closed. | INFERRED the words and digests are written |
| Q5 | Which trace level is the default for `PSXRecomp.RuntimeHost --diagnostics`? | Repository owner (mao2009) | #763 (open) | INFERRED that the flag is off by default |
| Q6 | Is MMIO `value` ever allowed outside a local-only trace? Design position: no. | Repository owner (mao2009) | #45 (open). #457 is closed. | INFERRED position |
| Q7 | Are GPU, SPU, CD, and DMA events in trace schema v1, or added in a later version? The #45 body lists them under "対象" (Scope) (section 0.1). | Repository owner (mao2009) | #45 (open) | INFERRED (#45 body, section 0.1) |
| Q8 | What is the acceptable overhead budget for L2, for example percent of wall time on one standard run? | Repository owner (mao2009) | #45 (open; completion criterion "Performance overheadとsampling方針を定義") | UNVERIFIED (no budget in repo found in this work) |
| Q9 | Should the Studio GUI and CLI read the same JSON Lines files, or a joined summary only? | Repository owner (mao2009) | #45 (open; completion criterion "GUI/CLIで共通利用できる形式を定義"); #40 and #42 (both open) | INFERRED (#45 body); UNVERIFIED (GUI code not searched) |
| Q10 | May trace timestamps appear in share-ready bundles, given the determinism rule in `docs/architecture/diagnostics.md` section 5? | Repository owner (mao2009) | #49 is closed, so the diagnostics contract has no open tracking issue. Open item for this decision: #45. | INFERRED that the current contract excludes time; INFERRED (#49 closed) |
| Q11 | Does any value in the shareable output lie in the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical)? The shareable output under section 7.2 contains PCs and addresses, so the check is on those. **Concrete action:** run `openbios-probe <openbios.bin> --json --capture-at <hex-pc>` (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:59-67`; `--dump-ram` also requires `--capture-at`, `:172`), then, for each PC-class and address-class value in the output, mask the segment bits and test it against `0x1FC00000`-`0x1FC7FFFF`. Record the result as a comment on issue #45 (the required test does not exist yet, section 7.5), not in this document. If any such value is in range, decide whether a BIOS-range value may appear in a shareable document. Raw words are not part of the shareable output, so they are not tested for this purpose. | Repository owner (mao2009) | #45 (open) | UNVERIFIED: no measured evidence. |
| Q12 | Is a SHA-256 of the firmware file (`sha256` at `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:423`; console `:443`; fallback export `:973`) acceptable in a shareable document? (Answered: no. Owner ruling: not published; internal evidence only, section 7.7.) It identifies the user's BIOS image but carries no BIOS bytes. **Owner ruling: NOT SHAREABLE (internal evidence only).** | Repository owner (mao2009) | #45 (open) | INFERRED (field exists). |
| Q13 | Are HI and LO (section 7.3) to be shareable? The owner ruling covers GPR and COP0; this document classifies HI and LO as part of the register dump and excludes them. | Repository owner (mao2009) | #45 (open) | INFERRED classification; the ruling does not name HI/LO |
| Q14 | Is a digest of the displayed frame (`frame.sha256`, `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:437`; `FramebufferSha256` in `ExecutionDiagnosticReport`) acceptable in a shareable document? It is a frame digest, not a whole-artifact digest. NOT SHAREABLE (owner ruling 1, section 7.7). Answered; no owner question remains on this. | Repository owner (mao2009) | #45 (open) | INFERRED (fields exist) |
| Q15 | Provisional owner ruling (section 7.8): build and tool identifiers and environment fields are a versioned closed set. A matched value is shareable; an unmatched value is written as `unknown` and is never inferred. Open for the owner: (a) the member lists of each value domain (section 7.8 proposes them, INFERRED); (b) whether `OsVersion` gets a pattern domain or stays excluded. The current validator (`ValidateSingleLine`, `src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:79-87`) accepts any single line, so the closed-set validator is a required change. | Repository owner (mao2009) | #45 (open) | INFERRED (validator accepts any single line). |
| Q16 | Shapes not yet read (`execution`, `consistency`, `referenceSecondsAtBoundary` value type, `accounting` other fields, `origins`, `FallbackInFlight`, `FallbackReturns`, `FallbackTargets`, the W10 boundary-log `Describe` body, and the W11 `--list-disc` output shape). Each stays default-deny until read and classified. Is that the accepted rule until the read is done? | Repository owner (mao2009) | #45 (open) | UNVERIFIED (shapes not read in this work) |
| Q17 | Owner ruling (section 7.7): the digests are different scopes and must not be treated as one value. The four are: the game-input digest (`InputSha256`, the digest of the file the user supplied to `run --report`, `src/PSXRecomp.Cli/RunCommand.cs:54-58`); the EXE digest (the main executable file); the disc-image digest (`discHash`, the disc file; no writer of `DiagnosticContextKeys.DiscHash` is found at 2e3ecad, and the disc SHA-256 is computed at `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:391` and passed as `DiscSha256` at `:451`, a staging artifact); and the firmware digest (the BIOS ROM file, Q12). Owner ruling: the commercial game-input and EXE digests are NOT SHAREABLE (ruling 5). Non-commercial input is NOT SHAREABLE (ruling 2). The artifact digest is NOT SHAREABLE (ruling 5). Only a synthetic medium the project generates itself for publication, contains no third-party copyrighted material (a), and has a reproducible generation procedure with a stated publication scope (b) may be shared (ruling 4), and a hash of a commercial disc or a commercial BIOS/ROM must never be published. Today `InputSha256` is written into the `#457` bundle's `report.json` (`src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:16`, bundle at `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:51`), so the current bundle changes if the answer is no. | Repository owner (mao2009) | #45 (open) | INFERRED (`InputSha256` set at `src/PSXRecomp.Cli/RunCommand.cs:58`; EXE and disc-image hashes per `docs/runtime/loaded-code-identity.md:45`) |

**Status notes for Q11, Q12, Q15 and Q17.** The Evidence column of the table above uses only OBSERVED, CONFIRMED, INFERRED and UNVERIFIED. Status is recorded here instead. Q11: provisional ruling (section 7.8); no value is guessed. Q12: owner ruling, NOT SHAREABLE (section 7.7). Q15: provisional ruling (section 7.8). Q17: recorded owner ruling (section 7.7). The artifact digest is NOT SHAREABLE (owner ruling 5). Any future change to that ruling needs owner approval. Q14 (frame digests): NOT SHAREABLE (owner ruling 1). Non-commercial input and non-commercial disc images: NOT SHAREABLE (owner rulings 2 and 3).

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- The leakage guarantee in section 7.4 is **UNVERIFIED** until the tests in section 7.5 exist and pass.
- The writer inventory (section 1.8) is a search result, not a proof of completeness. Test 6 of section 7.5 makes it checkable. **UNVERIFIED.**
- Several field shapes are named but not read (section 7.6, default-deny). They are a stated blocker (section 8), not a claimed classification.
- The `RunCommand` bundle gate was traced (section 1.2). No other path into the bundle was traced beyond the `ExecutionDiagnosticBundleWriter` lines cited.
- GUI code was not searched exhaustively.
- The `--dump-ram` file's contents were not inspected; its content class is taken from the code (`observed.ReadGuestWord` over KSEG0 main RAM), not from a produced file.
- The Q11 check was not run. No result is stated in this document. The Q11 result is **unknown** until a measurement exists (section 7.8).
- `milestones` is readable and is classified in section 7.6. Its JSON shape was taken from the record declaration (`src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:10-40`), not from a serialised sample.
- The producers of `hotEntries`, `hotPcs`, and the `referenceSeconds` values were not read in this revision.
