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
- **Share-candidate** output: a field or document that would pass the allowlist (section 7.2) if it were produced by the shareable projection. A share-candidate is not shareable until the projection emits it. **PREDICTED** (design term).

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
| `ExecutionDiagnosticLog` (projects diagnostics onto an allow-list of context keys, max 256 entries) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:12`, allow-list `:17-30` | CONFIRMED |
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

### 1.8 OpenBIOS probe: all output writers

The probe (`src/PSXRecomp.Cli/OpenBiosProbeCommand.cs`) has more than one writer. Every writer below is inventoried. Each is classified in section 7.6.

**W1. Interpreter probe `--json` document (the anonymous object, `kind = "openbios-probe"`).**

| Element | Location | Label |
|---|---|---|
| The object's `kind`, `backend`, `sha256`, `bootVerified`, `kernelBooted`, `milestones`, `titleStarted`, `executionState`, `guestPc`, `diagnosticCode`, `segmentsRetired`, `stop`, `captured`, `gte`, `frame` fields | `OpenBiosProbeCommand.cs:421` (`kind`), `:423` (`sha256 = hash`), `:432` (`stop`), `:433` (`captured`), `:435` (`gte`), `:437` (`frame`) | CONFIRMED |
| `BuildStop` builds `pc`, `sr`, `cause`, `epc`, `badVAddr`, `gpr[32]`, `lastFetches` (`Hex(e.Pc):Hex(e.Word)`, last 16), `lastTransfers` (last 16, including `From.Word`) | `OpenBiosProbeCommand.cs:1004-1014` (`pc`/`sr`/`cause`/`epc`/`badVAddr` at `:1010-1011`; `gpr` at `:1012`; `lastFetches` at `:1013`; `lastTransfers` at `:1014`) | CONFIRMED |
| `BuildStop` is called with the interpreter engine's `FinalSnapshot` | `OpenBiosProbeCommand.cs:361` | CONFIRMED |
| `captured` is built at the capture PC: `pc`, `word = ReadGuestWord(pc)` (raw instruction word), `gpr[32]`, `lastTransfers` (PCs only) | `OpenBiosProbeCommand.cs:344-350`; `word` at `:346`; `gpr` at `:347` | CONFIRMED |
| `gte.lastUnsupportedCommand` is `Gte.LastUnsupportedCommand`, set to `command & 0x01FF_FFFF`; `gte.commandsExecuted` is a count | `OpenBiosProbeCommand.cs:435`; `src/PSXRecomp.Core/Runtime/Gte/GteRegisterBank.cs:106`, `:115` | CONFIRMED |
| `milestones` is an `OpenBiosBootReport` record. Its fields are readable: `ResetVectorFetched`, `KernelRunningFromRam`, `VectorsInstalled`, `ExceptionVectorExecuted`, `ShellEntered`, `ShellEnteredAtFetch`, `InterruptEntries`, `SyscallEntries`, `TitleEntryPc`, `FirstUnexpectedException`, plus the derived `TitleStarted` and `KernelBooted`. The probe passes it to `milestones = report`. | `src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:10-40` (record and derived properties at `:20`, `:29-38`) | CONFIRMED |
| `milestones.firstUnexpectedException` is `OpenBiosUnexpectedException(ExcCode, Epc, BadVAddr, Cause, AtFetch)` | `src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:40`; read by the probe at `OpenBiosProbeCommand.cs:808` | CONFIRMED |
| Other probe-document fields named by the review (`fetchIndices`, `engine`, `standalone`, `interpretedEntries`, `atBoundary`, `milestoneComparison`, `consistency`, `timings`, `aot`, `execution`, `accounting`, `loadedImages[].name`) | the document object, `OpenBiosProbeCommand.cs:663-756` (generated-host variant, see W2) and `:421-437` (interpreter variant) | CONFIRMED (names); their value shapes were not all read. Classified by name in section 7.6; shape UNVERIFIED for `aot`, `execution`, `milestoneComparison`, `consistency`, `timings`, `fetchIndices`. |
| The `--json` document is written by the common writer that serialises the whole node | `OpenBiosProbeCommand.cs:743-746` (`SerializeToNode`, `consistency`, text), `:755` (`output.WriteLine(text)`) | CONFIRMED |

**W2. Generated-host `--json` document (`OpenBiosProbeCommand.cs:663-756`).**

| Element | Location | Label |
|---|---|---|
| The generated-host document is built at `:663-756` and written at `:755` through the same path as W1 | `OpenBiosProbeCommand.cs:663-756`, `:743-746`, `:755` | CONFIRMED |
| `diagnosticMessage` is set from `result.DiagnosticMessage` (free text) | `OpenBiosProbeCommand.cs:677` (`diagnosticMessage = result.DiagnosticMessage`) | CONFIRMED |
| `--fallback-targets` export is written with `WriteAtomically` from the same run | `OpenBiosProbeCommand.cs:660` | CONFIRMED |
| `accounting.unsupportedOpcodes[]` entries: `excCode`, `opcode` (instruction field), `pc`, `word` (raw instruction word), `atFallbackFetch` | `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:195-198` | CONFIRMED |
| Pinned by a test: `unsupported[0]["word"] == "0xFC000000"` and `unsupported[1]["opcode"] == 0x12` | `src/PSXRecomp.Tests/Cli/OpenBiosProbeAccountingTests.cs:183`, `:185` | CONFIRMED |
| `ProbeGuestState.Describe()` returns `pc`, `atFetch`, `cpu` (a map of named CPU values), `devices` (a map), `instructionWord` (raw word read from RAM at PC), `sr`, `cause`, `epc`, `ramSha256`, `guestCycles`, `scratchpadSha256` | `src/PSXRecomp.Cli/OpenBiosProbeDifferential.cs:95-107`; `instructionWord` at `:101`; `ramSha256` at `:104` | CONFIRMED |
| The `cpu` map is built from a list that includes `hi`, `lo`, `sr`, `cause`, `epc`, `badvaddr` (and the 31 GPRs, per the review; the GPR entries are in the same list) | `OpenBiosProbeDifferential.cs:36` (`cpu.AddRange([...("hi", hi), ("lo", lo), ("sr", cop0.Sr), ("cause", cop0.Cause), ("epc", cop0.Epc), ("badvaddr", cop0.BadVAddr)])`) | CONFIRMED (the six listed); CONFIRMED-by-review (the GPR entries) |
| `scratchpad_sha256` is a device digest, added to the device list | `OpenBiosProbeDifferential.cs:41` | CONFIRMED |
| `vram_sha256` is a device digest, added to the state list | `OpenBiosProbeDifferential.cs:83` | CONFIRMED |
| The differential compares CPU and RAM and emits per-field mismatch objects (`kind = "cpu"` entries with `interpreter` and `host` hex values) | `OpenBiosProbeDifferential.cs:117-118` | CONFIRMED |
| The differential emits a RAM mismatch object with `address`, `page`, `contextStart`, `interpreterBytes`, `hostBytes` (16 raw RAM bytes each side in the context window) | `OpenBiosProbeDifferential.cs:138-143` | CONFIRMED |
| The differential emits `interpreterRamSha256` and `hostRamSha256` (whole-RAM digests) | `OpenBiosProbeDifferential.cs:154-155` | CONFIRMED |

**W3. Non-JSON console output (`OpenBiosProbeCommand.cs:441-450`).**

| Element | Location | Label |
|---|---|---|
| Prints `OpenBIOS execution probe (sha256:{hash}, backend=...)` (firmware digest, `:443`), state, PC, and code | `OpenBiosProbeCommand.cs:441-445` | CONFIRMED |
| Prints `Stop: {JsonSerializer.Serialize(stop)}`, which includes the full `gpr[32]` and COP0 fields | `OpenBiosProbeCommand.cs:446-449` | CONFIRMED |
| Prints `Kernel boot milestones: {report}` | `OpenBiosProbeCommand.cs:450` | CONFIRMED |

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
| Written with `WriteAtomically` | `OpenBiosProbeCommand.cs:660` | CONFIRMED |
| The export carries `ramWord` (a raw RAM word, read at `:968`) and `lastReturnPc` (`:952-977`) | `OpenBiosProbeCommand.cs:952-977` (`lastReturnPc`), `:968` (`ramWord`) | CONFIRMED (lines); the export's full schema was not read |

**W9. `--mdec-events <file>` (JSON lines, `OpenBiosProbeMdecLog.cs:43-56`).**

| Element | Location | Label |
|---|---|---|
| Each line carries `engine`, `seq`, `g`, `kind`, `value` (raw 32-bit word), `pc`, `lastFallbackFetchPc`, `command` (raw 32-bit word), `remaining`, `control` (register), `fifoWords`, `busyBefore`, `busyAfter` | `src/PSXRecomp.Cli/OpenBiosProbeMdecLog.cs:43-56` | CONFIRMED |

**W10. `--boundary-log <file>` (JSON lines, `OpenBiosProbeBoundaryLog.cs:40-56`).**

| Element | Location | Label |
|---|---|---|
| Each line is `Describe(engine, devices, vblank, cycles)`: device registers, `vramSha256`, and the boundary position | `src/PSXRecomp.Cli/OpenBiosProbeBoundaryLog.cs:40-56` | CONFIRMED (call and fields named by the review); the `Describe` body was not read in full |

**W11. `--list-disc <file>` (JSON, `OpenBiosProbeCommand.cs:251-289`).**

| Element | Location | Label |
|---|---|---|
| Writes the disc's file listing, including file names from the disc image | `OpenBiosProbeCommand.cs:251-289` | CONFIRMED (range); field shape not read |

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
| Input identity | `ExecutionDiagnosticReport.InputSha256` (`ExecutionDiagnosticReport.cs:16`), set only on the `--report` path (`RunCommand.cs:54-58`) | One input file, same across rebuilds of the same input | Trace header field `inputSha256`, bare lowercase hex (the existing encoding) |
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
- **Exposure 2 (CONFIRMED): raw instruction words in probe output.** The writers W1-W3 and W9 carry them (section 7.6 lists each occurrence): `stop.lastFetches` (`:1013`), `stop.lastTransfers` (`:1014`), `captured.word` (`:346`), `gte.lastUnsupportedCommand` (`:435`), `accounting.unsupportedOpcodes[].word` (`OpenBiosProbeAccounting.cs:197`), `ProbeGuestState.Describe().instructionWord` (`OpenBiosProbeDifferential.cs:101`), the MDEC `value` and `command` words (`OpenBiosProbeMdecLog.cs:43-56`), and the fallback export `ramWord` (`OpenBiosProbeCommand.cs:968`). Whether any of them comes from the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical; `BiosAddressGuard.PhysicalBase` at `BiosAddressGuard.cs:103`) is **UNVERIFIED.** The probe boots OpenBIOS, so BIOS-range fetches are PREDICTED.
- **Exposure 3 (CONFIRMED, local by current writer; not guaranteed; test pending):** `--dump-ram <path>` writes the raw 2 MiB main RAM to a user-named file (`OpenBiosProbeCommand.cs:337-342`). The file is not in the `--json` document and not in the four-entry `#457` bundle (`ExecutionDiagnosticBundleWriter.cs:18-21`). That is what the current writers do. Nothing enforces it. The leakage guarantee is **UNVERIFIED** until the test in section 7.5 exists.
- **Exposure 4 (CONFIRMED, owner ruling):** full register dumps. The owner ruling removes them from shareable output. They appear in: `stop.gpr`, `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`OpenBiosProbeCommand.cs:1010-1012`); `captured.gpr` (`:347`); the console `Stop:` line (`:446-449`); `--retire-out` lines (`:328-333`); `ProbeGuestState.Describe().cpu` (`OpenBiosProbeDifferential.cs:36`); the checkpoint and fallback state objects (section 1.4); and `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (`OpenBiosBootMonitor.cs:40`). All of these are local only.

### 7.2 Shareable output is an explicit allowlist

**Rule.** A field is shareable only if it appears in this allowlist. A field not listed is excluded. Adding a field requires editing this list. The required test (section 7.5) will enforce the list once it is written; it does not exist yet. **PREDICTED.**

Each allowed class has one rule:

| Class | Rule | Allowed fields |
|---|---|---|
| PC (guest address) | 32-bit unsigned, written as `0x` plus 8 hex digits. It names where execution was, not what the bytes were. | `stop.pc`; `captured.pc`; `guestPc` (top level); `ExecutionDiagnosticReport.GuestPc`; `milestones.TitleEntryPc`; `ExecutionDiagnosticLog` `guestPc` and `guestAddress` context keys; `captured.lastTransfers` (PC-to-PC pairs only); `stop.lastTransfers` after removing `From.Word`; `LoadedCodeMismatchDiagnostics` `pc` and `windowStartPc`; `RuntimeHostDiagnostic` `FinalPc`, `LastAotBlockPc`, `TransferSourcePc`, `TransferTargetPc`; `RuntimeHostRunMetrics` `LastFallbackEntryPc`, `LastFallbackReturnPc` |
| Address (device or RAM location, not data) | 32-bit unsigned hex, as above. Names a location, not contents. | `RuntimeHostRunMetrics.LastMmioPhysicalAddress`; `LoadedCodeMismatchDiagnostics.ramPage` (a page index, written as hex) |
| Classification code | A value from a closed enum, or a registered `DiagnosticCode` (`^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*$`, `docs/architecture/diagnostics.md:55`). Never free text. | `diagnosticCode`; `executionState`; `kind`; `backend`; `engine`; `standalone`; `atBoundary`; `milestones` booleans (`ResetVectorFetched`, `KernelRunningFromRam`, `VectorsInstalled`, `ExceptionVectorExecuted`, `ShellEntered`); `bootVerified`, `kernelBooted`, `titleStarted`; `milestones.firstUnexpectedException.excCode` (exception code 0-31); `ExecutionDiagnosticReport` `Outcome`, `State`, `DiagnosticCode`; `ExecutionEnvironmentReport` `OsPlatform`, `OsArchitecture`, `ProcessArchitecture`, `TargetArchitecture` (closed sets, if the setter enforces them, to be confirmed); `RuntimeHostDiagnostic` `FallbackOutcome`, `FallbackRejectReason`, `ClassifiedExitCode`, `NativeTerminationReason`, `OrchestratorState`, `OrchestratorDiagnosticCode`, `TimedOut`, `FallbackEnabled`, `FallbackAttempted`, `TargetHasCompiledBlock`, `TargetInsideImage`, `LastMmioWasWrite`; `trace` `termination`, `status` |
| Count | Non-negative integer. | `segmentsRetired`; `gte.commandsExecuted`; `interpretedEntries`; `milestones.ShellEnteredAtFetch`, `milestones.InterruptEntries`, `milestones.SyscallEntries`; `milestones.firstUnexpectedException.atFetch`; `retired` (trace); `comparedWords`, `rejections`, `fullMatches`, `versionCount`, `version`, `ramHashChanges` (`LoadedCodeMismatchDiagnostics`); the range pairs `firstMismatchIndexRange`, `mismatchedWordsRange`; `RuntimeHostRunMetrics` `MmioAccesses`, `TimeReports`; `RuntimeHostDiagnostic` `AotRetiredInstructions`, `FallbackRetiredInstructions`, `FallbackTransitionCount`; `ExecutionDiagnosticReport.SegmentBudget`; `frame.nonZeroPixels` (count only, not the frame) |
| Whole-artifact digest | SHA-256, bare lowercase 64-character hex (section 4 encoding). A digest of a partial window, a region, RAM, VRAM, scratchpad, or any device memory is **not** allowed (section 7.3). | `ExecutionDiagnosticReport.InputSha256`; `ExecutionDiagnosticReport.ArtifactIdentity` (after prefix stripping); `discHash` context key (`ExecutionDiagnosticLog`); `inputSha256` and `artifactSha256` trace header fields |
| Duration | Milliseconds as a number, rounded to a fixed precision in the writer. Wall clock is not written as a timestamp. | `RuntimeHostRunMetrics.WallMilliseconds`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds` (`RuntimeHostDiagnostic.cs:146-150`); probe `timings` (shape UNVERIFIED) |
| Identifier | Session ID (section 4), 32-hex GUID. | `session` (new per-record field, section 2) |

Fields that are default-deny (not allowed until a decision or a read) are in section 7.6, under "Default-deny".

### 7.3 Excluded fields (shareable output must not contain these)

| Excluded class | Fields | Why |
|---|---|---|
| General registers (owner ruling) | `stop.gpr` (`OpenBiosProbeCommand.cs:1012`); `captured.gpr` (`:347`); `gpr[32]` in every `checkpoint`; `gpr` in the native `unit` state and fallback `entry`/`exit` objects (`unit-trace.md`); `retire-out` `gpr` (`:328-333`); `ProbeGuestState.Describe().cpu` GPR entries (`OpenBiosProbeDifferential.cs:36`); console `Stop:` line (`:446-449`) | Full register dump. Local only (owner ruling). |
| COP0 state (owner ruling) | `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`:1010-1011`); `sr`, `cause`, `epc` in every `checkpoint` and fallback state; `ProbeGuestState.Describe()` `sr`, `cause`, `epc` (`OpenBiosProbeDifferential.cs:95-107`) and `cpu` COP0 entries (`:36`); `badvaddr` (`:36`); `retire-out` `sr`, `cause`, `epc` (`:328-333`); `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (`OpenBiosBootMonitor.cs:40`) | COP0 full state. Local only (owner ruling). |
| HI and LO | `hi`, `lo` in checkpoint and state objects; `ProbeGuestState.Describe().cpu` entries `hi` and `lo` (`OpenBiosProbeDifferential.cs:36`); `retire-out` `hi`, `lo` (`:328-333`) | Part of the register dump. Excluded as a register dump. **PREDICTED** classification, pending Q13. |
| Raw instruction words | `captured.word` (`OpenBiosProbeCommand.cs:346`); `stop.lastFetches` (`:1013`); `stop.lastTransfers` `From.Word` (`:1014`); `gte.lastUnsupportedCommand` (`:435`); `accounting.unsupportedOpcodes[].word` (`OpenBiosProbeAccounting.cs:197`); `accounting.unsupportedOpcodes[].opcode` (`:198`), since it is the instruction's opcode field; `ProbeGuestState.Describe().instructionWord` (`OpenBiosProbeDifferential.cs:101`); `ExecutionDiagnosticLog` `opcode` context key (`ExecutionDiagnosticLog.cs:28`); MDEC `value` and `command` (`OpenBiosProbeMdecLog.cs:43-56`); `mmio.value`; `TransferOpcode` (`RuntimeHostDiagnostic`); the fallback export `ramWord` (`OpenBiosProbeCommand.cs:968`) | Instruction encoding from guest memory. Excluded from shareable output. |
| RAM contents | `windowAot`, `windowRam` (`LoadedCodeMismatchDiagnostics.cs:296-297`); `OpenBiosProbeDifferential` RAM context `interpreterBytes`, `hostBytes` (`:138-143`); `--dump-ram` file (W7); any memory bytes; any run of 8 or more consecutive hex words | Raw loaded-image and guest-RAM words. |
| Whole-RAM, VRAM, and device digests (owner direction, M4) | `ramSha256` (`LoadedCodeMismatchDiagnostics.cs:294`; `OpenBiosProbeDifferential.cs:104`); `interpreterRamSha256`, `hostRamSha256` (`:154-155`); `vram_sha256` (`:83`); `scratchpad_sha256` (`:41`, `:106`) | A digest of RAM or device memory is a region digest, and is banned by the RAM-region rule in section 7.3. This is a banned class, not a default-deny. |
| Free text | `diagnosticMessage` (`OpenBiosProbeCommand.cs:677`); `OsVersion` (`ExecutionEnvironmentReport.cs:18`, `:39`); `Title` (`ExecutionDiagnosticReport.cs`); `FailureExceptionType` (`RuntimeHostDiagnostic`); `FallbackRejectReason` if it is free text (decision: treat as classification only if the writer enforces a closed set, else excluded) | Free text is not a classification. It can carry host, title, or path content. |
| Local file and symbol names | `accounting.hotSymbols` symbol names; `loadedImages[].name` (file names); `--list-disc` file names (W11) | File and symbol names are user-supplied text, not classifications. |
| Host and environment identity | `environment.json` fields `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion`, `ProcessArchitecture`, `OsArchitecture` beyond the closed sets; `ProductRevision`, `Revision` (default-deny, Q15); the `runId` value, which is host run identity (Q2) | Host identity. Not a trace field. |
| Trace-only values | `t` timestamps (default null; Q10); `skipped` counts are allowed as counts | Timestamps are opt-in and excluded from share-ready bundles (Q10). |
| Frame pixels | `--dump-frame` PPM (W6); `frame` pixel content; the `frame` digest (moved to default-deny, section 7.6) | Pixel content is game output. Local only. |
| Disc and program contents | `--list-disc` (W11), `--fallback-targets` (W8) contents beyond the classes above | Derived from the user's disc. Local only. |

### 7.4 Local/share boundary (rule)

1. **Shareable output** is produced only by one writer, the shareable projection. It builds a document from the allowlist in section 7.2 and nothing else. A document is shareable only when this projection built it. **PREDICTED.**
2. **Local output** is any other output. The writers W1-W11 of section 1.8, the trace files, `environment.json` as currently written, the mismatch report as currently written, and the `--json` documents of the probe are all local. Local output may contain any field, including the excluded classes of section 7.3. **CONFIRMED** (these outputs exist and contain those fields today).
3. A local output must not be attached to a bundle, a report, or an issue. The `#457` bundle writer accepts only shareable-projection documents. **PREDICTED** (the writer change does not exist; today the bundle writer takes typed records and does not check provenance).
4. **A local-only classification is not a guarantee of non-leakage.** Calling an output "local" is a label. It stops leakage only if the shareable projection is tested to emit nothing outside the allowlist, and the bundle writer is tested to refuse non-projection input. Until the tests in section 7.5 exist and pass, the leakage guarantee is **UNVERIFIED**.

### 7.5 Required tests and the fixtures they use

Fixtures are not written from section 7.3. Each fixture is generated by calling the real writer under test on a synthetic run, so the test covers what the writer actually emits. **PREDICTED** (none of these tests exist).

Required tests:

1. **`ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_field`** (new file under `src/PSXRecomp.Tests/Diagnostics/`). Fixtures: run the probe document builder (W1 and W2 code paths), `ProbeGuestState.Describe()`, `OpenBiosProbeAccounting`, `OpenBiosBootReport` (milestones), `RuntimeHostDiagnostic` and `RuntimeHostRunMetrics`, `ExecutionEnvironmentReport`, `ExecutionDiagnosticReport`, and `LoadedCodeMismatchDiagnostics.ToJsonNode()`. Feed their outputs to the projection. The test checks the projection's output, not the fixture.
2. **`ShareableOutputAllowlistTests.Shareable_writer_keys_are_allowlisted`.** Walks the projection's output and fails on any key path not in the section 7.2 allowlist.
3. **`ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_key_or_value`.** Applies the matching rules below to the projection's output.
4. **`ShareableOutputAllowlistTests.Bundle_writer_refuses_local_output`.** Asserts `ExecutionDiagnosticBundleWriter.Write` refuses a document that the projection did not build.
5. **`ExecutionDiagnosticBundleWriterTests.Bundle_writer_rejects_opcode_context_key`** (required design change C2). Builds a bundle from a diagnostic carrying an `opcode` context key, and asserts the `diagnostics.log` entry contains no `opcode=` text and no value from that key. The writer drops the key (fail-closed: keys not on the allowlist are dropped; the allow-list is `ExecutionDiagnosticLog.cs:17-30` and the filter is at `:78-79`). **PREDICTED.**

**Matching rules for test 3 (m4).**

- **Key match:** a key is excluded if it matches, case-insensitively, any term in this explicit list, as a substring of the key: `word`, `gpr`, `hi`, `lo`, `sr`, `cause`, `epc`, `badvaddr`, `opcode`, `bytes`, `ram`, `vram`, `scratchpad`, `window`, `dump`, `pixel`, `message`. The substring match is a second line of defence. Test 2 is the primary check, because the allowlist is exact.
- **Hex-run rule:** a value fails if it is an array of 8 or more consecutive strings that each match `^0x[0-9A-Fa-f]{1,8}$`, or a single hex string of 32 or more bytes (64 or more hex digits) that is not a whole-artifact digest in the allowlist.
- **Free-text rule:** any string value that is not a classification code, a PC, an identifier, or a digest in the allowlist fails.

The leakage guarantee in section 7.4 stays **UNVERIFIED** until tests 1 through 5 exist and pass.

### 7.6 Writer classification (C1, M1, M2)

Each writer and each field group is labelled local or share-candidate. "Share-candidate" means its fields pass the allowlist if the projection emits them. "Default-deny" means it is not allowed until the stated decision or read. "Banned" means it is excluded by a rule in section 7.3 and never emitted.

**Writers (C1, M1).**

| Writer | Location | Content | Label |
|---|---|---|---|
| Interpreter probe `--json` (W1) | `OpenBiosProbeCommand.cs:421-437`, written `:743-755` | Mixed, see field table below | Local |
| Generated-host `--json` (W2) | `OpenBiosProbeCommand.cs:663-756`, written `:755` | Mixed, see field table below; `diagnosticMessage` (`:677`); `accounting` (`OpenBiosProbeAccounting.cs:195-198`); `differential` (`OpenBiosProbeDifferential.cs`) | Local |
| Console `Stop` and milestones (W3) | `OpenBiosProbeCommand.cs:441-450` | Full GPRs and COP0 (`Stop:`); firmware digest (`:443`); milestones | Local |
| `--retire-out` (W4) | `OpenBiosProbeCommand.cs:317`, `:328-333` | Full GPR, hi, lo, COP0 per fetch | Local (banned classes) |
| `--pc-histogram` (W5) | `OpenBiosProbeCommand.cs:368-377` | `totalRamFetches`, PCs with counts, bucket starts with counts | Share-candidate fields (PC and count), but local by writer; file comment says "Local diagnostic only" |
| `--dump-frame` (W6) | `OpenBiosProbeCommand.cs:378-411` | Framebuffer pixels (PPM) | Local; pixels are excluded (section 7.3); pixel content not a share-candidate |
| `--dump-ram` (W7) | `OpenBiosProbeCommand.cs:81`, `:172`, `:337-342` | Raw 2 MiB RAM | Local; banned |
| `--fallback-targets` (W8) | `OpenBiosProbeCommand.cs:660`, `:952-977`, `:968` | `ramWord` (raw RAM word), `lastReturnPc` (PC) | Local; `ramWord` banned; `lastReturnPc` share-candidate by writer rule, local by writer |
| `--mdec-events` (W9) | `OpenBiosProbeMdecLog.cs:43-56` | `value` and `command` raw words, `control` register, `pc`, `lastFallbackFetchPc`, counts, flags | Local; raw words banned; `pc`, `lastFallbackFetchPc`, `seq`, `g`, `fifoWords`, `remaining` are share-candidates by writer rule |
| `--boundary-log` (W10) | `OpenBiosProbeBoundaryLog.cs:40-56` | Device registers, `vramSha256` (VRAM digest, banned), boundary PCs | Local; VRAM digest banned; device register values local |
| `--list-disc` (W11) | `OpenBiosProbeCommand.cs:251-289` | Disc file names | Local; file names excluded (section 7.3) |
| Trace files (native `<path>`, fallback `<path>.fallback`) | `unit-trace.md`; `RecompilerHostCodeGen.cs` | Unit and fallback records; CPU state with `gpr` | Local; PC and count fields are share-candidates by writer rule |
| Checkpoint records | `CpuCheckpoint.cs:17` | `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `pc` | Local only (owner ruling) |
| Loaded-code mismatch report (`#773`) | `LoadedCodeMismatchDiagnostics.cs:277-300`; written by `Program.cs:624` path | See field table | Local (banned fields present) |
| Runtime host diagnostic and metrics (`#763`) | `RuntimeHostDiagnostic.cs:24`, `:146-160` | See field table | Local until projected |
| `report.json` (`#457`) | `ExecutionDiagnosticBundleWriter.cs:51`; `ExecutionDiagnosticReport.cs:14-25` | See field table | Share-candidate fields; written to the bundle without a projection today |
| `environment.json` (`#457`) | `ExecutionDiagnosticBundleWriter.cs:52`; `ExecutionEnvironmentReport.cs:11-45` | See field table | Share-candidate fields only after `OsVersion` is removed; written whole today |
| `diagnostics.log` (`#457`) | `ExecutionDiagnosticBundleWriter.cs:53`; `ExecutionDiagnosticLog.cs:17-30` | Context keys, including `opcode` | Share-candidate after the C2 change |
| `issue.md` (`#457`) | `ExecutionIssueMarkdown.cs:36` | Derived from `report` | Same classes as `report.json` |

**Field tables (M2).**

*Probe `--json` fields (W1, W2).*

| Field | Class | Label |
|---|---|---|
| `kind`, `backend`, `engine`, `standalone`, `executionState`, `atBoundary`, `titleStarted`, `bootVerified`, `kernelBooted`, `diagnosticCode` | Classification | Share-candidate |
| `guestPc` | PC | Share-candidate |
| `segmentsRetired`, `interpretedEntries`, `fetchIndices` (shape UNVERIFIED) | Count | Share-candidate (`fetchIndices` default-deny until read) |
| `sha256` (`OpenBiosProbeCommand.cs:423`, firmware digest) | Whole-artifact digest | Default-deny pending Q12 |
| `frame` (`:437`): `width`, `height`, `nonZeroPixels`, `sha256` | `width`, `height`, `nonZeroPixels` are counts; `sha256` is a frame digest, not a whole-artifact digest | Counts: share-candidate. `sha256`: default-deny pending Q14 |
| `stop` (`:432`) | Mixed, see writer W1 | Share-candidate only for `pc`, `lastTransfers` PCs, `diagnostic` classifications; excluded for `gpr`, COP0, `lastFetches`, `word` |
| `captured` (`:433`) | Mixed | Share-candidate only for `pc` and `lastTransfers`; excluded for `word`, `gpr` |
| `gte` (`:435`) | `commandsExecuted`: count; `lastUnsupportedCommand`: raw instruction field | `commandsExecuted`: share-candidate; `lastUnsupportedCommand`: excluded |
| `milestones` (`OpenBiosBootMonitor.cs:10-40`) | Booleans, counts, `TitleEntryPc` (PC), `FirstUnexpectedException` (below) | Booleans and counts: share-candidate. `TitleEntryPc`: share-candidate (PC). `FirstUnexpectedException.excCode` and `.atFetch`: share-candidate. `FirstUnexpectedException.epc`, `.badVAddr`, `.cause`: excluded (COP0) |
| `milestoneComparison` | Shape not read | Default-deny until read |
| `consistency` | Shape not read (built from boundary PCs and the differential, `:743-745`) | Default-deny until read |
| `timings` | Durations | Share-candidate (shape UNVERIFIED; each value is a duration) |
| `aot`, `execution` | Shape not read | Default-deny until read |
| `accounting` | `unsupportedOpcodes[].excCode` (classification), `.pc` (PC), `.atFallbackFetch` (count): share-candidate. `.word`, `.opcode`: excluded. `hotSymbols` symbol names: excluded. Other fields: default-deny until read | Mixed |
| `loadedImages[].name` | File name | Excluded |
| `differential` (`OpenBiosProbeDifferential.cs`) | `cpu` map (excluded: GPR, COP0, hi, lo); `devices` map (`scratchpad_sha256`, `vram_sha256`: banned); `instructionWord` (excluded); `pc`, `atFetch`, `guestCycles` (PC, count, count: share-candidate); `ramSha256`, `scratchpadSha256` (banned); `interpreterRamSha256`, `hostRamSha256` (banned); RAM context bytes (banned); `kind = "cpu"` mismatch entries (excluded, carry register values) | Mixed |
| `diagnosticMessage` (`:677`) | Free text | Excluded |
| `fallback` and `fallbackTargets` exports | Covered by W8 | Local |

*`report.json` fields (`ExecutionDiagnosticReport.cs:14-25`).*

| Field | Class | Label |
|---|---|---|
| `Schema` | Classification (schema identity) | Share-candidate |
| `ProductRevision` | Build identifier | Default-deny pending Q15 |
| `InputSha256` | Whole-artifact digest | Share-candidate |
| `Title` | Free text (game title) | Excluded |
| `Revision` | Build identifier | Default-deny pending Q15 |
| `ArtifactIdentity` | Whole-artifact digest with `sha256:` prefix | Share-candidate (prefix stripped, section 4) |
| `Outcome`, `State` | Classification | Share-candidate |
| `GuestPc` | PC | Share-candidate |
| `DiagnosticCode` | Classification | Share-candidate |
| `SegmentBudget` | Count | Share-candidate |
| `FramebufferSha256` | Frame digest, not a whole-artifact digest | Default-deny pending Q14 |

*`environment.json` fields (`ExecutionEnvironmentReport.cs:11-45`).*

| Field | Class | Label |
|---|---|---|
| `Schema` | Classification | Share-candidate |
| `OsPlatform` | Classification | Share-candidate (closed set to be confirmed) |
| `OsVersion` | Free text (`:18`, `:39`) | Excluded (M2, C-level finding for the bundle) |
| `OsArchitecture`, `ProcessArchitecture` | Classification | Share-candidate (closed set to be confirmed) |
| `DotNetVersion` | Build/tool identifier | Default-deny pending Q15 |
| `RuntimeIdentifier` | Build/tool identifier | Default-deny pending Q15 |
| `CompilerVersion` | Build/tool identifier | Default-deny pending Q15 |
| `TargetArchitecture` | Classification | Share-candidate (closed set to be confirmed) |

*Loaded-code mismatch report fields (`LoadedCodeMismatchDiagnostics.cs:277-300`).*

| Field | Class | Label |
|---|---|---|
| `pc`, `windowStartPc` | PC | Share-candidate |
| `version`, `versionCount`, `comparedWords`, `rejections`, `fullMatches`, `ramHashChanges` | Counts | Share-candidate |
| `ramPage` | Address (page index) | Share-candidate |
| `firstMismatchIndexRange`, `mismatchedWordsRange` | Count ranges | Share-candidate |
| `aotSha256` | Digest of AOT words for a version, a region digest | Banned (region digest) |
| `ramSha256` | RAM digest | Banned |
| `windowAot`, `windowRam` | Raw words | Banned |
| `origins` | `OriginsJson()`, shape not read | Default-deny until read |

*Runtime metrics and host diagnostic (`RuntimeHostDiagnostic.cs:146-160` and the record at `:24`).*

| Field | Class | Label |
|---|---|---|
| `WallMilliseconds`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds` | Durations | Share-candidate |
| `MmioAccesses`, `TimeReports` | Counts | Share-candidate |
| `LastFallbackEntryPc`, `LastFallbackReturnPc` | PC | Share-candidate |
| `LastMmioPhysicalAddress` | Address | Share-candidate |
| `LastMmioWasWrite` | Classification | Share-candidate |
| `FallbackInFlight`, `FallbackReturns` | Shapes not read | Default-deny until read |
| `FinalPc`, `LastAotBlockPc`, `TransferSourcePc`, `TransferTargetPc` | PC | Share-candidate |
| `TransferOpcode` | Raw instruction field | Excluded |
| `FallbackTargets` (list of `MixedFallbackTarget`) | Shape not read | Default-deny until read |
| `FailureExceptionType` | Type name (free text) | Excluded unless closed set |
| `FallbackOutcome`, `OrchestratorState`, `OrchestratorDiagnosticCode`, `NativeTerminationReason`, `ClassifiedExitCode` | Classification | Share-candidate |
| `FallbackRejectReason` | Free text if not a closed set | Excluded unless the writer enforces a closed set |
| `AotRetiredInstructions`, `FallbackRetiredInstructions`, `FallbackTransitionCount` | Counts | Share-candidate |
| `TimedOut`, `FallbackEnabled`, `FallbackAttempted`, `TargetHasCompiledBlock`, `TargetInsideImage` | Booleans | Share-candidate |

*Milestones (`OpenBiosBootMonitor.cs:10-40`, label UNVERIFIED removed; the shape is readable).* See the probe table above.

*Trace records (`unit-trace.md`).* `pc`, `next`, `seq`, `retired`, `termination`, `status`, `handoff`, `start`, `end`, `native_total`: share-candidate. `gpr`, `hi`, `lo`, `sr`, `cause`, `epc`, `state`, `entry`, `exit`: local only (owner ruling).

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, and `event` kinds. Do not invent a second format. **PREDICTED** (rationale: the existing joiner, tests, and parsers already implement the rules).
2. Make `session` the single run identity for trace purposes. Retire `runId` (`RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them only as aliases. **PREDICTED.**
3. Reuse the existing `inputSha256` and `ArtifactIdentity` hashes, both as bare lowercase hex; add no new artifact ID field (section 4). **PREDICTED.**
4. **Required design changes, before any shareable output or bundle:**
   - **C2:** remove the `opcode` key from `ExecutionDiagnosticLog`'s allowlist (`ExecutionDiagnosticLog.cs:28`). Update `ExecutionDiagnosticLogTests.cs:37` (the `InstructionOpcode` context entry) and `:48` (the expected `opcode=0xDEADBEEF` in the formatted line). Add test 5 of section 7.5.
   - **M2:** `environment.json` is removed from the shareable bundle, or projected to the allowlist with `OsVersion` removed (`ExecutionDiagnosticBundleWriter.cs:52`). `ProductRevision`, `Revision`, and the tool identifiers are default-deny until Q15.
   - **M4:** the firmware digest (`OpenBiosProbeCommand.cs:423`, console `:443`) is default-deny until Q12. `frame.sha256` (`:437`) and `FramebufferSha256` are default-deny until Q14. Whole-RAM, VRAM, and device digests are banned (section 7.3).
   - **M6:** tests that pin raw output must change with the fixes:
     - `OpenBiosProbeAccountingTests.cs:183` (`word`) and `:185` (`opcode`) pin the raw fields of `unsupportedOpcodes`. They must change so the shareable projection emits no `word` or `opcode`. The local writer keeps them, so the test moves to the local writer and the projection test asserts absence.
     - `LoadedCodeMismatchDiagnosticsTests.cs:34-35` pin `windowAot` and `windowRam`. They stay on the local writer. The projection test asserts they are absent.
     - `ExecutionDiagnosticLogTests.cs:37` and `:48` pin the `opcode` key, as above.
   - **Exposure 3 (`--dump-ram`):** keep local. Test 4 of section 7.5 and the projection tests cover it.
5. Build the shareable projection (section 7.2) and the required tests (section 7.5) before any trace or bundle is shared. **PREDICTED.**
6. Ship L1 (boundary) first. Its cost is near zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **PREDICTED.**

**Blocker (stated, not fixed in this design).** The shareable projection cannot be finished until the default-deny fields are read (`aot`, `execution`, `milestoneComparison`, `consistency`, `fetchIndices`, `origins`, `FallbackInFlight`, `FallbackReturns`, `FallbackTargets`, `accounting` remaining fields) and their owners decide them. This is a read-and-decide task, not a design gap. It is recorded as a blocker for the implementation phase, not for this design. **OBSERVED** (the shapes were not read in this work).

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
| Q12 | Is a SHA-256 of the firmware file (`sha256` at `OpenBiosProbeCommand.cs:423`; console `:443`) acceptable in a shareable document? It identifies the user's BIOS image but carries no BIOS bytes. Default-deny until decided. | Repository owner (mao2009) | #45 (open) | OBSERVED (field exists); default-deny (section 7.6) |
| Q13 | Are HI and LO (section 7.3) to be shareable? The owner ruling covers GPR and COP0; this document classifies HI and LO as part of the register dump and excludes them. | Repository owner (mao2009) | #45 (open) | PREDICTED classification; the ruling does not name HI/LO |
| Q14 | Is a digest of the displayed frame (`frame.sha256`, `OpenBiosProbeCommand.cs:437`; `FramebufferSha256` in `ExecutionDiagnosticReport`) acceptable in a shareable document? It is a frame digest, not a whole-artifact digest. Default-deny until decided. | Repository owner (mao2009) | #45 (open) | CONFIRMED (fields exist); default-deny (section 7.6) |
| Q15 | Are build and tool identifiers shareable: `ProductRevision`, `Revision`, `DotNetVersion`, `RuntimeIdentifier`, `CompilerVersion`? Default-deny until decided. | Repository owner (mao2009) | #45 (open) | CONFIRMED (fields exist); default-deny (section 7.6) |
| Q16 | Shapes not yet read (`aot`, `execution`, `milestoneComparison`, `consistency`, `fetchIndices`, `timings`, `origins`, `FallbackInFlight`, `FallbackReturns`, `FallbackTargets`, `accounting` remaining fields). Each stays default-deny until read and classified. Is that the accepted rule until the read is done? | Repository owner (mao2009) | #45 (open) | UNVERIFIED (shapes not read in this work) |

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- The leakage guarantee in section 7.4 is **UNVERIFIED** until the tests in section 7.5 exist and pass.
- Several field shapes are named but not read (section 7.6, default-deny). They are a stated blocker (section 8), not a claimed classification.
- The `RunCommand` bundle gate was traced (section 1.2). No other path into the bundle was traced beyond the `ExecutionDiagnosticBundleWriter` lines cited.
- GUI code was not searched exhaustively.
- The `--dump-ram` file's contents were not inspected; its content class is taken from the code (`observed.ReadGuestWord` over KSEG0 main RAM), not from a produced file.
- The Q11 check was not run. No result is stated in this document.
- `milestones` is readable and is classified in section 7.6. Its JSON shape was taken from the record declaration (`OpenBiosBootMonitor.cs:10-40`), not from a serialised sample.
