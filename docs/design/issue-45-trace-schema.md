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
- **UNVERIFIED**: not checked, or checkable only by running the system.

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

### 1.8 OpenBIOS probe output (exposes raw words)

| Element | Location | Label |
|---|---|---|
| `BuildStop` builds `lastFetches` (`Hex(e.Pc):Hex(e.Word)`, last 16) and `lastTransfers` (last 16, includes `From.Word`) from the engine trace ring | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1004-1014` | CONFIRMED |
| `BuildStop` is called with the interpreter engine's `FinalSnapshot` | `OpenBiosProbeCommand.cs:361` | CONFIRMED |
| The `--json` document is the anonymous object with `kind = "openbios-probe"`; the same object holds `stop`, `captured`, and `gte` | `OpenBiosProbeCommand.cs:421` (`kind`), `:432` (`stop`), `:433` (`captured`), `:435` (`gte`) | CONFIRMED |
| `captured` is an object built at the capture PC, with `word = ReadGuestWord(pc)` (a raw instruction word), `pc`, `gpr[32]`, and `lastTransfers` (PCs only) | `OpenBiosProbeCommand.cs:344-350`; `word` at `:346` | CONFIRMED |
| `gte.lastUnsupportedCommand` is `observed.Gte.LastUnsupportedCommand`, set by `ExecuteCommand` to `command & 0x01FF_FFFF`: the 25-bit COP2 command field of the instruction that failed | `OpenBiosProbeCommand.cs:435`; `src/PSXRecomp.Core/Runtime/Gte/GteRegisterBank.cs:106`, `:115` | CONFIRMED |
| `gte.commandsExecuted` is a count | `OpenBiosProbeCommand.cs:435` | CONFIRMED |
| `--dump-ram <path>` writes the raw 2 MiB main RAM (KSEG0 `0x80000000`, `RamBytes`) to a file named by the user, not into the `--json` document | `OpenBiosProbeCommand.cs:81` (option), `:337-342` (write) | CONFIRMED |
| The `--dump-ram` file is not part of the `#457` bundle, whose four entries are fixed (`ExecutionDiagnosticBundleWriter.cs:18-21`) and do not include a RAM file | `ExecutionDiagnosticBundleWriter.cs:18-21` | CONFIRMED |
| The probe loads a 512 KiB OpenBIOS ROM and boots the firmware, so the interpreter fetches are expected to include BIOS-range PCs | `OpenBiosProbeCommand.cs:201-207` (ROM load); the boot design | CONFIRMED (ROM load); PREDICTED (BIOS-range fetches) |
| Whether any raw word in `lastFetches`, `lastTransfers`, `captured.word`, or `gte.lastUnsupportedCommand` comes from the BIOS range | not checked (no probe run, no captured `--json` output inspected) | UNVERIFIED |
| The register and COP0 fields are CPU state, not memory words; see section 7, Exposure 4 | `OpenBiosProbeCommand.cs:1011` (`sr`, `cause`, `epc`, `badVAddr`), `:1012` (`gpr`), `:433` (`captured.gpr`), `:805-808` (`firstUnexpectedException`) | CONFIRMED (locations); the JSON shape of `milestones` is UNVERIFIED (serialised from `OpenBiosBootReport`, not read as a schema) |

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

| Family | `kind` values | Core fields | Source of truth |
|---|---|---|---|
| PC / control | `unit`, `handoff`, `fallback`, `transfer`, `stop` | `pc`, `next`, `retired`, `termination`, `status` | Extends existing unit-trace kinds (`unit-trace.md`) |
| Register state | `checkpoint` | `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `globalRetired` | `CpuCheckpoint` (`CpuCheckpoint.cs:17`) |
| MMIO | `mmio` | `physAddr`, `width`, `isWrite`, `value`?, `pc`? | `HostMmioAccess` (`MixedFallback.cs:161`) |
| Runtime event | `event` | `device` (`gpu`, `spu`, `cdrom`, `dma`, `timer`, `irq`, `bios`), `name`, `code`?, `data`? | New; BIOS call identity exists (`BiosCallIdentity` in `src/PSXRecomp.Core/Runtime/BiosCallIdentity.cs`, used by `BiosServiceResult.cs:152`) |

`BiosServiceResult.cs:152` is a use of `BiosCallIdentity` in `BiosDiagnostic`, not its definition. **CONFIRMED.**

Diagnostic records (section 1.1) are not trace records. They link into a trace by reference only (`DiagnosticEvidenceReference`). **CONFIRMED** (current contract); the link use is **PREDICTED.**

Field rules:

- Addresses are `uint` and written as hex strings in JSON, matching the existing unit trace. **OBSERVED.**
- No record carries a host path, user name, or wall-clock time by default. Time is opt-in (section 5).
- `value` on an MMIO record is off by default (section 7).

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
- The `#457` bundle is the shareable unit. Trace files join it only after the redaction in section 7. **PREDICTED.**

## 7. Safety boundary: BIOS and ROM raw data

Current state, by location:

- Unit trace records contain CPU registers and PCs, not memory bytes. **OBSERVED** (`unit-trace.md`, "Record schema"). `CpuCheckpoint` has no memory field (`CpuCheckpoint.cs:17-25`). **CONFIRMED.**
- `ExecutionDiagnosticReport` has no field for input payloads, RAM, save data, or paths. **CONFIRMED** (`ExecutionDiagnosticReport.cs:8-10`).
- BIOS content diagnostic reports counts and provenance only. **CONFIRMED** (`BiosAddressGuard.cs:81`).
- **Exposure 1 (CONFIRMED):** `LoadedCodeMismatchDiagnostics` writes `windowAot` and `windowRam` (5 words each, around the first mismatch) into JSON (`LoadedCodeMismatchDiagnostics.cs:296-297`). These are instruction words from the loaded image and from guest RAM. The comment calls this bounded sampling. Bounded is not the same as redacted. Whether this output is ever published is **UNVERIFIED.**
- **Exposure 2 (CONFIRMED): four raw instruction-derived fields in the OpenBIOS probe `--json` output.** Each is produced from guest instruction data:
  1. `stop.lastFetches`, each `Hex(e.Pc):Hex(e.Word)` (`OpenBiosProbeCommand.cs:1013`): raw instruction words, with PCs.
  2. `stop.lastTransfers`, each including `From.Word` (`:1014`): raw instruction words.
  3. `captured.word`, `ReadGuestWord(pc)` at the capture PC (`:346`, written at `:433`): a raw instruction word.
  4. `gte.lastUnsupportedCommand` (`:435`): not a memory word, but the 25-bit COP2 command field taken from the failing instruction (`command & 0x01FF_FFFF`, `GteRegisterBank.cs:115`). It discloses part of an instruction encoding, so it is treated as an instruction-derived field here.

  **Whether any of these fields comes from the BIOS range** (`0x1FC00000`-`0x1FC7FFFF` physical; `BiosAddressGuard.PhysicalBase` at `BiosAddressGuard.cs:103`) **is UNVERIFIED.** The probe boots OpenBIOS, so BIOS-range fetches are PREDICTED. Any such entry would publish raw BIOS instruction data in the `--json` output. The concrete test is in Q11.
- **Exposure 3 (CONFIRMED, local-only by construction):** `--dump-ram <path>` writes the raw 2 MiB main RAM to a user-named file (`OpenBiosProbeCommand.cs:337-342`). It is not written into the `--json` document, and it is not part of the four-entry `#457` bundle (`ExecutionDiagnosticBundleWriter.cs:18-21`). Main RAM (KSEG0 `0x80000000`) is not the BIOS range. The file is still raw guest memory, so it must not be attached to any share-ready artifact. The code comment already says "local diagnostic output only". **Outside the share-ready boundary: yes, by construction. CONFIRMED.**
- **Exposure 4 (CPU state, decision made):** the register and COP0 fields below are CPU state, not memory words. **Decision: they do not count as raw words and are not removed.** They are listed so the boundary is explicit:
  - `stop.gpr`, `stop.sr`, `stop.cause`, `stop.epc`, `stop.badVAddr` (`OpenBiosProbeCommand.cs:1010-1012`);
  - `captured.gpr` (`:347`);
  - `milestones.firstUnexpectedException` with `epc`, `badVAddr`, and `cause`, from `OpenBiosUnexpectedException(ExcCode, Epc, BadVAddr, Cause, AtFetch)` (`OpenBiosBootMonitor.cs:40`; the probe reads it at `OpenBiosProbeCommand.cs:808`).

  These values are addresses and register contents. A value such as `epc` or `badVAddr` can lie in the BIOS range, and that is an address, not BIOS data. Q11 checks whether those addresses point into the BIOS range. **CONFIRMED** (locations); the JSON shape of `milestones` is **UNVERIFIED** (serialised from `OpenBiosBootReport`, not read as a schema).

Proposed boundary (PREDICTED):

1. Trace schema v1 has no field for memory bytes. A `value` field on `mmio` is allowed only in a local-only trace level, never in the share-ready bundle.
2. The share-ready bundle (`#457`) includes only trace records with no `value` and no word arrays. `windowAot`, `windowRam`, `stop.lastFetches`, `stop.lastTransfers`, `captured.word`, and `gte.lastUnsupportedCommand` are removed from the share-ready form, or reduced to a hash of the window or fetch list instead of printed values. The `--json` form of the OpenBIOS probe is treated the same way when it is shared. The CPU-state fields of Exposure 4 are not removed.
3. BIOS bytes never enter any trace or report. Only existing `BiosContentDiagnostic` counts and hashes may appear. `BiosAddressGuard` is the enforcement point. Exposures 2 and 4 must be checked against this rule first (Q11).
4. ROM and disc bytes never enter a trace. Identity is by hash only (`loaded-code-identity.md`, "The hash is not runtime proof" and the hash-for-identity rule).
5. `--dump-ram` output stays local. It is never added to a bundle or attached to a report. **PREDICTED.**
6. A writer-side test enforces (1) to (4): a trace written at L2 must contain no field outside the allow-list, and no hex run of 8 or more words that matches a loaded image window. **PREDICTED** (test does not exist).

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, and `event` kinds. Do not invent a second format. **PREDICTED** (rationale: the existing joiner, tests, and parsers already implement the rules).
2. Make `session` the single run identity for trace purposes. Retire `runId` (`RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them only as aliases. **PREDICTED.**
3. Reuse the existing `inputSha256` and `ArtifactIdentity` hashes, both as bare lowercase hex; add no new artifact ID field (section 4). **PREDICTED.**
4. Ship L1 (boundary) first. Its cost is near zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **PREDICTED.**
5. Fix the share-ready redaction gap (Exposures 1 and 2 in section 7) before any trace or bundle is shared. The gap is **CONFIRMED**; the fix is **PREDICTED.**

## 9. Open questions and the decision each needs

Ownership note: the GitHub API shows no assignee on any issue read for this document (#45, #457, #49, #763, #773, #40, #42), and ADR-027 has no owner field. So no per-issue owner is recorded for any question below. **OBSERVED.** The decision-maker named in each row is the repository owner (mao2009). Closed items are named as such. Each row names an open tracking item where one exists.

| # | Question | Decision point | Tracking item | Evidence label |
|---|---|---|---|---|
| Q1 | Is `session` a new optional field in `report.json` (schema bump to `v2`) or an optional addition within `v1`? | Repository owner (mao2009) | #457 is closed, so the report schema has no open tracking issue. Open item for this decision: #45. | CONFIRMED that v1 has no session field; OBSERVED (#457 closed) |
| Q2 | Retire the standalone `runId` (`Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them as aliases? | Repository owner (mao2009) | #763 (open) and #773 (open) | CONFIRMED both exist and both are written |
| Q3 | For packaged artifacts (section 4.1), which identities does the trace header carry? Candidates: the native artifact executable hash, the `runtime/artifact-image.bin` sidecar hash (the image the host runs), the `runtime/runtime-host.json` hash, and `inputSha256` (only on the `run --report` path, `RunCommand.cs:54-58`). Should `inputSha256` be produced for packaged runs at all? | Repository owner (mao2009) | #739 (open; ADR-027 status Accepted, Issue #739) | OBSERVED (ADR-027:52, :62-66, :70-71, :128-175); CONFIRMED (`RunCommand.cs:54-58`) |
| Q4 | Are `windowAot`/`windowRam` in the loaded-code report removed, reduced to hashes, or kept only in a local-only level? | Repository owner (mao2009) | #773 (open). The fix PR (#786) is closed. | CONFIRMED the words are written |
| Q5 | Which trace level is the default for `PSXRecomp.RuntimeHost --diagnostics`? | Repository owner (mao2009) | #763 (open) | OBSERVED that the flag is off by default |
| Q6 | Is MMIO `value` ever allowed outside a local-only trace? Design position: no. | Repository owner (mao2009) | #45 (open). #457 is closed. | PREDICTED position |
| Q7 | Are GPU, SPU, CD, and DMA events in trace schema v1, or added in a later version? The #45 body lists them under "対象" (Scope) (section 0.1). | Repository owner (mao2009) | #45 (open) | OBSERVED (#45 body, section 0.1) |
| Q8 | What is the acceptable overhead budget for L2, for example percent of wall time on one standard run? | Repository owner (mao2009) | #45 (open; completion criterion "Performance overheadとsampling方針を定義") | UNVERIFIED (no budget in repo found in this work) |
| Q9 | Should the Studio GUI and CLI read the same JSON Lines files, or a joined summary only? | Repository owner (mao2009) | #45 (open; completion criterion "GUI/CLIで共通利用できる形式を定義"); #40 and #42 (both open) | OBSERVED (#45 body); UNVERIFIED (GUI code not searched) |
| Q10 | May trace timestamps appear in share-ready bundles, given the determinism rule in `docs/architecture/diagnostics.md` section 5? | Repository owner (mao2009) | #49 is closed, so the diagnostics contract has no open tracking issue. Open item for this decision: #45. | CONFIRMED that the current contract excludes time; OBSERVED (#49 closed) |
| Q11 | Do any of the four raw-derived fields of Exposure 2 (`stop.lastFetches`, `stop.lastTransfers`, `captured.word`, `gte.lastUnsupportedCommand`) come from the BIOS range, and do any of the CPU-state addresses of Exposure 4 (`epc`, `badVAddr`, `captured.pc`, `stop.pc`) point into it? If so, must those values be removed from `--json` output? **Concrete action:** run `openbios-probe <openbios.bin> --json --capture-at <hex-pc>` (the option is `--capture-at`, `OpenBiosProbeCommand.cs:59-67`; `--dump-ram` requires it, `:172`), then for each raw word in the output, test its source address against `0x1FC00000`-`0x1FC7FFFF` (physical, after masking the segment bits). For `lastFetches` and `lastTransfers`, the PC in each entry is the address to test; for `captured.word`, the capture PC; for `gte.lastUnsupportedCommand`, the output does not record the instruction's address, so the action must also record the PC of the failing COP2 instruction, or the field is classified as untestable. | Repository owner (mao2009) | #45 (open) | UNVERIFIED (not checked by running the probe) |

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- The `RunCommand` bundle gate was traced (section 1.2). No other path into the bundle was traced.
- GUI code was not searched exhaustively.
- The `--dump-ram` file's contents were not inspected; its content class is taken from the code (`observed.ReadGuestWord` over KSEG0 main RAM), not from a produced file.
- The JSON shape of `milestones` was not read as a schema.
