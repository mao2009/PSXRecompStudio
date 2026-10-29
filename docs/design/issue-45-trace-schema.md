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

**Owner ruling (binding, recorded in this revision):** the full register dump is removed from the shareable diagnostic output. GPR and COP0 full dumps are local-diagnostic only and never appear in a shareable document. Section 7 implements this.

## 1. Inventory of existing trace and diagnostics code

### 1.1 Shared diagnostic contract (Domain, `PSXRecomp.Core.Diagnostics`)

| Element | Location | Label |
|---|---|---|
| `Diagnostic` record (Code, Category, Severity, Stage, Context, Evidence, Recovery; no timestamp, host, user or path) | `src/PSXRecomp.Core/Diagnostics/Diagnostic.cs:30` | CONFIRMED |
| `DiagnosticCodes` registry (discoverability only) | `src/PSXRecomp.Core/Diagnostics/DiagnosticCodes.cs:20` | CONFIRMED |
| `DiagnosticContextEntry` (typed `uint`/`string` key-value) | `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:12` | CONFIRMED |
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
| Report fields `InputSha256` (`:16`) and `ArtifactIdentity` (`:19`) | `ExecutionDiagnosticReport.cs:16-19` | CONFIRMED |
| `InputSha256` is normalised to bare, lowercase, 64-character hex (`NormalizeSha256` rejects anything else) | `ExecutionDiagnosticReport.cs:53`, `:67-75` | CONFIRMED |
| `RunCommand` sets `ArtifactIdentity` to `"sha256:" + ComputeSha256(artifactPath)`, where `ComputeSha256` returns bare lowercase hex. The prefix is added at the call site. | `src/PSXRecomp.Cli/RunCommand.cs:191`, `ComputeSha256` at `:206-210` | CONFIRMED |
| `artifactPath` is the built artifact, resolved by `ResolveArtifactPath` | `RunCommand.cs:81` | CONFIRMED |
| `inputSha256` is declared at `:52`, and assigned only inside `if (arguments.Report)` (`:54`, value at `:58` from `resolved.Sha256`) | `RunCommand.cs:52-58` | CONFIRMED |
| The diagnostic bundle is written only inside `if (arguments.Report)` (`:86`); `inputSha256` is passed to `WriteDiagnosticBundle` at `:93` | `RunCommand.cs:86-93` | CONFIRMED |
| `ExecutionDiagnosticLog` (projects diagnostics onto a small allow-list of context keys, max 256 entries) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:12`, allow-lists `:17-30` | CONFIRMED |
| `ExecutionDiagnosticLog` allows the `opcode` context key (`DiagnosticContextKeys.InstructionOpcode`) as a `uint`. A value under this key is a raw instruction word, so the existing shareable log already admits one. | `ExecutionDiagnosticLog.cs:28`; key defined at `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:38` | CONFIRMED |
| `ExecutionDiagnosticBundleWriter` writes exactly four entries: `report.json`, `environment.json`, `diagnostics.log`, `issue.md` (name constants); canonical timestamps | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:16`, entry-name constants `:18-21` | CONFIRMED |

### 1.3 Runtime host diagnostic (`#763`, open)

| Element | Location | Label |
|---|---|---|
| `RuntimeHostDiagnostic` (per-run stop account; off by default, observation only; unknown fields are `null`, not guessed) | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:24` | CONFIRMED |
| `RuntimeHostRunMetrics` (wall time, MMIO and time-report round trips, last MMIO physical address and write flag, fallback timing) | `RuntimeHostDiagnostic.cs:146` | CONFIRMED |
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
| Mismatch record keeps a 5-word window of AOT words and RAM words at the first refusal, and writes them to JSON as `windowAot` and `windowRam` | `LoadedCodeMismatchDiagnostics.cs:30` (`WindowRadius = 2`), `:269-273`, `:296-297` | CONFIRMED |
| Source comment calls this "a bounded sample, not a dump of the image" | `LoadedCodeMismatchDiagnostics.cs:268` | CONFIRMED (comment) |

### 1.7 BIOS and ROM guards

| Element | Location | Label |
|---|---|---|
| `BiosAddressGuard` static class; `PhysicalBase = 0x1FC00000` | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:103` | CONFIRMED |
| `IsBiosAddress` | `BiosAddressGuard.cs:113` | CONFIRMED |
| `OverlapsBios` | `BiosAddressGuard.cs:120` | CONFIRMED |
| `RequireNoBiosImage` (refuses BIOS code in AOT generation) | `BiosAddressGuard.cs:146` | CONFIRMED |
| `BiosContentDiagnostic` record: counts and provenance text, no bytes | `BiosAddressGuard.cs:81` | CONFIRMED |
| Standalone report "never names an input" (code comment cites "Issue #786 B1"; #786 is a pull request, so the comment's label is a mislabel) | `src/PSXRecomp.RuntimeHost/Program.cs:197` | OBSERVED (comment) |

### 1.8 OpenBIOS probe output (exposes raw words and full register dumps)

| Element | Location | Label |
|---|---|---|
| `BuildStop` builds `lastFetches` (`Hex(e.Pc):Hex(e.Word)`, last 16) and `lastTransfers` (last 16, includes `From.Word`) from the engine trace ring | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1004-1014` | CONFIRMED |
| `BuildStop` also emits `pc`, `sr`, `cause`, `epc`, `badVAddr`, and a full `gpr[32]` array | `OpenBiosProbeCommand.cs:1010-1012` | CONFIRMED |
| `BuildStop` is called with the interpreter engine's `FinalSnapshot` | `OpenBiosProbeCommand.cs:361` | CONFIRMED |
| The `--json` document is the anonymous object with `kind = "openbios-probe"`; the same object holds `stop`, `captured`, `gte`, `milestones`, `frame`, and `sha256` | `OpenBiosProbeCommand.cs:421` (`kind`), `:423` (`sha256 = hash`), `:432` (`stop`), `:433` (`captured`), `:435` (`gte`) | CONFIRMED |
| `captured` is an object built at the capture PC, with `word = ReadGuestWord(pc)` (a raw instruction word), `pc`, `gpr[32]`, and `lastTransfers` (PCs only) | `OpenBiosProbeCommand.cs:344-350`; `word` at `:346`; `gpr` at `:347` | CONFIRMED |
| `gte.lastUnsupportedCommand` is `observed.Gte.LastUnsupportedCommand`, set by `ExecuteCommand` to `command & 0x01FF_FFFF`: the 25-bit COP2 command field of the instruction that failed | `OpenBiosProbeCommand.cs:435`; `src/PSXRecomp.Core/Runtime/Gte/GteRegisterBank.cs:106`, `:115` | CONFIRMED |
| `gte.commandsExecuted` is a count | `OpenBiosProbeCommand.cs:435` | CONFIRMED |
| `milestones.firstUnexpectedException` is an `OpenBiosUnexpectedException(ExcCode, Epc, BadVAddr, Cause, AtFetch)` record | `OpenBiosBootMonitor.cs:40`; the probe reads it at `OpenBiosProbeCommand.cs:808` | CONFIRMED (record); the JSON shape of `milestones` is UNVERIFIED (serialised from `OpenBiosBootReport`, not read as a schema) |
| `frame.sha256` is a digest of the displayed frame; `frame` also carries `width`, `height`, `nonZeroPixels` | `OpenBiosProbeCommand.cs:437` | CONFIRMED |
| `--dump-ram <path>` writes the raw 2 MiB main RAM (KSEG0 `0x80000000`, `RamBytes`) to a file named by the user, not into the `--json` document. It requires `--capture-at` | `OpenBiosProbeCommand.cs:81` (option), `:172` (requirement), `:337-342` (write) | CONFIRMED |
| The `--dump-ram` file is not part of the `#457` bundle, whose four entries are fixed (`ExecutionDiagnosticBundleWriter.cs:18-21`) and do not include a RAM file | `ExecutionDiagnosticBundleWriter.cs:18-21` | CONFIRMED |
| The probe `--capture-at <hex-pc>` option (`OpenBiosProbeCommand.cs:59-67`) | `OpenBiosProbeCommand.cs:59-67` | CONFIRMED |
| The probe loads a 512 KiB OpenBIOS ROM and boots the firmware, so the interpreter fetches are expected to include BIOS-range PCs | `OpenBiosProbeCommand.cs:201-207` (ROM load); the boot design | CONFIRMED (ROM load); PREDICTED (BIOS-range fetches) |
| Whether any value in the `--json` output lies in the BIOS range | not checked (no probe run, no captured `--json` output inspected) | UNVERIFIED |

### 1.9 Identity hashes already in use

| Element | Location | Label |
|---|---|---|
| Input identity: SHA-256 of EXE and disc; disc hash is for provenance only | `docs/runtime/loaded-code-identity.md:14`, `:45` | OBSERVED |
| `GuardStatsScope` run ID, random 64-bit hex per child process | `src/PSXRecomp.Infrastructure/GuardStatsScope.cs:18`; `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:1345` | CONFIRMED |
| `ObservedRunIdentity` (firmware SHA, manifest, disc SHA, probe options SHA) | `src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:46`, `:262` | CONFIRMED |

### 1.10 Gaps in the inventory

- **No shared session ID.** The unit trace has a session ID. The `#457` report has none. The standalone host has a separate `runId` (`Program.cs:198`, also written at `:624`). The guard-stats scope has another run ID. **CONFIRMED** (by the listed locations).
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

| Family | `kind` values | Core fields | Source of truth | Shareable? |
|---|---|---|---|---|
| PC / control | `unit`, `handoff`, `fallback`, `transfer`, `stop` | `pc`, `next`, `retired`, `termination`, `status` | Extends existing unit-trace kinds (`unit-trace.md`) | PCs and classifications only (section 7.2) |
| Register state | `checkpoint` | `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `globalRetired` | `CpuCheckpoint` (`CpuCheckpoint.cs:17`) | Local-only (owner ruling) |
| MMIO | `mmio` | `physAddr`, `width`, `isWrite`, `value`?, `pc`? | `HostMmioAccess` (`MixedFallback.cs:161`) | `value` local-only; the rest per section 7.2 |
| Runtime event | `event` | `device` (`gpu`, `spu`, `cdrom`, `dma`, `timer`, `irq`, `bios`), `name`, `code`?, `data`? | New; BIOS call identity exists (`BiosCallIdentity` in `src/PSXRecomp.Core/Runtime/BiosCallIdentity.cs`, used by `BiosServiceResult.cs:152`) | `data` local-only; `name` and `code` per section 7.2 |

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
- `ExecutionDiagnosticReport` has no field for input payloads, RAM, save data, or paths. **CONFIRMED** (`ExecutionDiagnosticReport.cs:8-10`).
- The existing shareable diagnostic log admits an `opcode` key, which is a raw instruction word (`ExecutionDiagnosticLog.cs:28`). **CONFIRMED.** This is an existing shareable-output path, not a trace path, and the allowlist in section 7.2 removes it.
- BIOS content diagnostic reports counts and provenance only. **CONFIRMED** (`BiosAddressGuard.cs:81`).
- **Exposure 1 (CONFIRMED):** `LoadedCodeMismatchDiagnostics` writes `windowAot` and `windowRam` (5 words each, around the first mismatch) into JSON (`LoadedCodeMismatchDiagnostics.cs:296-297`). These are instruction words from the loaded image and from guest RAM. The comment calls this bounded sampling. Bounded is not the same as redacted. Whether this output is ever published is **UNVERIFIED.**
- **Exposure 2 (CONFIRMED): raw instruction words in the OpenBIOS probe `--json` output:**
  1. `stop.lastFetches` (`OpenBiosProbeCommand.cs:1013`);
  2. `stop.lastTransfers`, including `From.Word` (`:1014`);
  3. `captured.word` (`:346`, written at `:433`);
  4. `gte.lastUnsupportedCommand` (`:435`), the 25-bit COP2 command field of the failing instruction (`GteRegisterBank.cs:115`).

  Whether any of these comes from the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical; `BiosAddressGuard.PhysicalBase` at `BiosAddressGuard.cs:103`) is **UNVERIFIED.** The probe boots OpenBIOS, so BIOS-range fetches are PREDICTED.
- **Exposure 3 (CONFIRMED, local-only by construction):** `--dump-ram <path>` writes the raw 2 MiB main RAM to a user-named file (`OpenBiosProbeCommand.cs:337-342`). It is not in the `--json` document and not in the four-entry `#457` bundle (`ExecutionDiagnosticBundleWriter.cs:18-21`). It is raw guest memory and stays local.
- **Exposure 4 (CONFIRMED, owner ruling):** full register dumps. `stop.gpr`, `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`OpenBiosProbeCommand.cs:1010-1012`); `captured.gpr` (`:347`); the native and fallback checkpoint `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc` (section 1.4); and `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` (section 1.8). **Ruling: the full GPR and COP0 dumps are local-diagnostic only and never appear in a shareable document.** The ruling is binding. Section 7.2 excludes them.

### 7.2 Shareable output is an explicit allowlist

**Rule.** A field is shareable only if it appears in this allowlist. A field not listed is excluded. Adding a field requires editing this list, and the required test (section 7.4) enforces the list. **PREDICTED.**

Each allowed class has one rule:

| Class | Rule | Allowed fields (from the existing output) |
|---|---|---|
| PC (guest address) | 32-bit unsigned, written as `0x` plus 8 hex digits. It names where execution was, not what the bytes were. | `stop.pc`; `captured.pc`; `guestPc` (top level); `captured.lastTransfers` (PC-to-PC pairs only, no words); `stop.lastTransfers` after removing `From.Word` (see excluded list); `ExecutionDiagnosticLog` `guestPc` and `guestAddress` context keys; `ExecutionDiagnosticReport.GuestPc` |
| Classification code | A value from a closed enum or a registered `DiagnosticCode` (`^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*$`, `DiagnosticCode.cs`). Never free text. | `diagnosticCode`; `executionState`; `kind`; `backend`; `termination` (trace records); `status` (trace records); `ExecutionDiagnosticReport.State`; `bootVerified`, `kernelBooted`, `titleStarted` (booleans, treated as classifications) |
| Count | Non-negative integer. | `segmentsRetired`; `gte.commandsExecuted`; `retired` (trace records); `comparedWords`, `rejections`, `fullMatches`, `versionCount` (`LoadedCodeMismatchDiagnostics`, `ToJsonNode`); `frame.nonZeroPixels` (`OpenBiosProbeCommand.cs:437`) |
| Digest of a whole artifact | SHA-256, bare lowercase 64-character hex (section 4 encoding). Digests of partial windows, or of RAM regions, are not allowed. | `sha256` (`OpenBiosProbeCommand.cs:423`, the firmware digest; whether a firmware digest is acceptable to share is Q12); `frame.sha256` (`:437`, a digest of the displayed frame); `inputSha256`; `artifactSha256`; `windowAot`/`windowRam` replaced by a digest of the whole loaded image, not of the window |
| Duration | Milliseconds as a number, rounded to a fixed precision in the writer. Wall clock is not written as a timestamp. | `RuntimeHostRunMetrics.WallMilliseconds`, `FallbackTransferMilliseconds`, `FallbackInterpreterMilliseconds` (`RuntimeHostDiagnostic.cs:146-150`) |
| Identifier | Session ID (section 4), 32-hex GUID. | `session` (new per-record field, section 2) |

Fields that are not in the allowlist, and therefore excluded from every shareable document, are listed in section 7.3.

### 7.3 Excluded fields (shareable output must not contain these)

| Excluded class | Fields | Why |
|---|---|---|
| General registers (owner ruling) | `stop.gpr`; `captured.gpr`; `gpr[32]` in every `checkpoint` record; `gpr` in the native `unit` state and fallback `entry`/`exit` objects (`unit-trace.md`) | Full register dump. Local-diagnostic only (owner ruling). |
| COP0 state (owner ruling) | `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr`; `sr`, `cause`, `epc` in every `checkpoint` record and fallback state; `milestones.firstUnexpectedException.epc`, `.badVAddr`, `.cause` | COP0 full state. Local-diagnostic only (owner ruling). |
| HI/LO (register dump) | `hi`, `lo` in checkpoint and state objects | Part of the register dump; excluded as a register dump, not as COP0. **PREDICTED** classification of HI/LO as register dump, pending a decision if the owner wants them shareable. |
| Raw instruction words | `captured.word`; `stop.lastFetches` (each `Hex(e.Word)`); `stop.lastTransfers` (`From.Word`); `gte.lastUnsupportedCommand`; `ExecutionDiagnosticLog` `opcode` context key (`ExecutionDiagnosticLog.cs:28`); `mmio.value`; any `word` field | Instruction encoding from guest memory. Excluded from shareable output. |
| RAM contents | `windowAot`; `windowRam`; `--dump-ram` file; any memory bytes; any hex run of words | Raw loaded-image and guest-RAM words. |
| Loaded-image identity beyond digests | any per-word list | Covered by the digest rule. |
| `milestones` fields not yet read | the whole `milestones` object, until its JSON shape is read and each field is classified | Shape is UNVERIFIED (section 1.8). Excluded by default under the allowlist. |
| Host and environment | `environment.json` contents beyond the existing projection; host paths; user names; `runId`-based host data | Existing bundle rule (`#457`); host identity is not a trace field. |
| Trace-only values | `t` timestamps (section 2, default null); `skipped` counters are allowed as counts | Timestamps are opt-in and excluded from share-ready bundles (Q10). |

### 7.4 Local/share boundary (rule)

1. **Shareable output** is produced only by a single writer, the shareable projection, which builds a document from the allowlist in section 7.2 and nothing else. A document is shareable only when this projection built it. **PREDICTED.**
2. **Local output** is any other output: the `--json` document of the OpenBIOS probe as it exists today, `--dump-ram`, `windowAot`/`windowRam`, and every `checkpoint` record. Local output may contain any field, including the excluded classes of section 7.3. **CONFIRMED** (these outputs exist and contain those fields today).
3. A local output must not be attached to a bundle, a report, or an issue. The `#457` bundle writer accepts only shareable-projection documents. **PREDICTED** (the writer change does not exist; today the bundle writer takes typed records and does not check provenance).
4. **A local-only classification is not a guarantee of non-leakage.** Calling an output "local" is a label. It stops leakage only if the shareable projection is tested to emit nothing outside the allowlist, and the bundle writer is tested to refuse non-projection input. Until the test in section 7.5 exists, the leakage guarantee is **UNVERIFIED**.

### 7.5 Required test

**Required test:** `ShareableOutputAllowlistTests.Shareable_writer_emits_no_excluded_field`, in `src/PSXRecomp.Tests/Diagnostics/`. **PREDICTED** (the test does not exist; **UNVERIFIED**).

The test must:

- run the shareable projection over fixtures that contain every excluded field of section 7.3 (a synthetic `stop`, `captured`, `gte`, `checkpoint`, `windowAot`/`windowRam`, and `milestones` object);
- walk the resulting JSON recursively and fail if any key or value matches an excluded name from section 7.3, or any hex run of 8 or more consecutive 32-bit words;
- fail if any key in the output is not in the section 7.2 allowlist;
- be paired with a second test, `ShareableOutputAllowlistTests.Bundle_writer_refuses_local_output`, which asserts the bundle writer rejects a document not built by the projection.

Until both tests pass, the leakage guarantee in section 7.4 stays **UNVERIFIED**.

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, and `event` kinds. Do not invent a second format. **PREDICTED** (rationale: the existing joiner, tests, and parsers already implement the rules).
2. Make `session` the single run identity for trace purposes. Retire `runId` (`RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them only as aliases. **PREDICTED.**
3. Reuse the existing `inputSha256` and `ArtifactIdentity` hashes, both as bare lowercase hex; add no new artifact ID field (section 4). **PREDICTED.**
4. Build the shareable projection (section 7.2) and the two required tests (section 7.5) before any trace or bundle is shared. Remove `opcode` from `ExecutionDiagnosticLog`'s allowlist as part of that work. **PREDICTED.**
5. Ship L1 (boundary) first. Its cost is near zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **PREDICTED.**

## 9. Open questions and the decision each needs

Ownership note: the GitHub API shows no assignee on any issue read for this document (#45, #457, #49, #763, #773, #40, #42), and ADR-027 has no owner field. So no per-issue owner is recorded for any question below. **OBSERVED.** The decision-maker named in each row is the repository owner (mao2009). Closed items are named as such. Each row names an open tracking item where one exists.

| # | Question | Decision point | Tracking item | Evidence label |
|---|---|---|---|---|
| Q1 | Is `session` a new optional field in `report.json` (schema bump to `v2`) or an optional addition within `v1`? | Repository owner (mao2009) | #457 is closed, so the report schema has no open tracking issue. Open item for this decision: #45. | CONFIRMED that v1 has no session field; OBSERVED (#457 closed) |
| Q2 | Retire the standalone `runId` (`Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them as aliases? | Repository owner (mao2009) | #763 (open) and #773 (open) | CONFIRMED both exist and both are written |
| Q3 | For packaged artifacts (section 4.1), which identities does the trace header carry? Candidates: the native artifact executable hash, the `runtime/artifact-image.bin` sidecar hash (the image the host runs), the `runtime/runtime-host.json` hash, and `inputSha256` (only on the `run --report` path, `RunCommand.cs:54-58`). Should `inputSha256` be produced for packaged runs at all? | Repository owner (mao2009) | #739 (open; ADR-027 status Accepted, Issue #739) | OBSERVED (ADR-027:52, :62-66, :70-71, :128-175); CONFIRMED (`RunCommand.cs:54-58`) |
| Q4 | Are `windowAot`/`windowRam` in the loaded-code report removed, reduced to hashes, or kept only in a local-only level? Under section 7.2 they are excluded from shareable output. The remaining question is whether the local file keeps them. | Repository owner (mao2009) | #773 (open). The fix PR (#786) is closed. | CONFIRMED the words are written |
| Q5 | Which trace level is the default for `PSXRecomp.RuntimeHost --diagnostics`? | Repository owner (mao2009) | #763 (open) | OBSERVED that the flag is off by default |
| Q6 | Is MMIO `value` ever allowed outside a local-only trace? Design position: no. | Repository owner (mao2009) | #45 (open). #457 is closed. | PREDICTED position |
| Q7 | Are GPU, SPU, CD, and DMA events in trace schema v1, or added in a later version? The #45 body lists them under "対象" (Scope) (section 0.1). | Repository owner (mao2009) | #45 (open) | OBSERVED (#45 body, section 0.1) |
| Q8 | What is the acceptable overhead budget for L2, for example percent of wall time on one standard run? | Repository owner (mao2009) | #45 (open; completion criterion "Performance overheadとsampling方針を定義") | UNVERIFIED (no budget in repo found in this work) |
| Q9 | Should the Studio GUI and CLI read the same JSON Lines files, or a joined summary only? | Repository owner (mao2009) | #45 (open; completion criterion "GUI/CLIで共通利用できる形式を定義"); #40 and #42 (both open) | OBSERVED (#45 body); UNVERIFIED (GUI code not searched) |
| Q10 | May trace timestamps appear in share-ready bundles, given the determinism rule in `docs/architecture/diagnostics.md` section 5? | Repository owner (mao2009) | #49 is closed, so the diagnostics contract has no open tracking issue. Open item for this decision: #45. | CONFIRMED that the current contract excludes time; OBSERVED (#49 closed) |
| Q11 | Does any value in the shareable output lie in the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical)? The shareable output under section 7.2 contains PCs, so the check is on those. **Concrete action:** run `openbios-probe <openbios.bin> --json --capture-at <hex-pc>` (`OpenBiosProbeCommand.cs:59-67`; `--dump-ram` also requires `--capture-at`, `:172`), then, for each PC-class value in the output, mask the segment bits and test it against `0x1FC00000`-`0x1FC7FFFF`. Record the result in the test log of the required test, not in this document. If any PC-class value is in range, decide whether a BIOS-range PC may appear in a shareable document. Raw words are not part of the shareable output (section 7.3), so they are not tested for this purpose. | Repository owner (mao2009) | #45 (open) | UNVERIFIED (not checked by running the probe) |
| Q12 | Is a SHA-256 of the firmware file (`sha256` at `OpenBiosProbeCommand.cs:423`) acceptable in a shareable document? It identifies the user's BIOS image but carries no BIOS bytes. | Repository owner (mao2009) | #45 (open) | OBSERVED (field exists); PREDICTED position: allowed as a digest, pending the decision |
| Q13 | Are HI and LO (section 7.3) to be shareable? The owner ruling covers GPR and COP0; this document classifies HI and LO as part of the register dump and excludes them. | Repository owner (mao2009) | #45 (open) | PREDICTED classification; the ruling does not name HI/LO |

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- The leakage guarantee in section 7.4 is **UNVERIFIED** until the tests in section 7.5 exist and pass.
- The `RunCommand` bundle gate was traced (section 1.2). No other path into the bundle was traced.
- GUI code was not searched exhaustively.
- The `--dump-ram` file's contents were not inspected; its content class is taken from the code (`observed.ReadGuestWord` over KSEG0 main RAM), not from a produced file.
- The JSON shape of `milestones` was not read as a schema. It is excluded by default until it is.
- The Q11 check was not run. No result is stated in this document.
