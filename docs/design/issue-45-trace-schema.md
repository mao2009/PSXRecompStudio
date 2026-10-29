# Issue #45 Debug / Trace Schema Design

**Status:** Proposal (design only, no code change)

**Related Issues:** #45 (Debug / Trace Infrastructure); related #40, #42, #44, #16, #132, #457, #739, #763, #773, #774

**Base:** `2e3ecad` (origin/main)

## 0. How to read the labels

Every claim carries one of four labels:

- **CONFIRMED**: read directly in source, docs, or test code at the cited location in this checkout.
- **OBSERVED**: stated by an existing doc, issue text, or API response. Not re-run in this work.
- **PREDICTED**: a design expectation this document proposes. Not yet true.
- **INFERRED**: a conclusion drawn from code or docs, not measured.
- **UNVERIFIED**: not checked, or checkable only by running the system.

Issue #45 scope was read through a summary of the public GitHub issue body (verbatim text was not retrieved). **OBSERVED.** Its scope list names PC and PS1 virtual PC tracking, registers and memory access, MIPS instructions, functions, basic blocks and CFG, runtime events, GPU, SPU, CD, and DMA events, crash diagnostics, and structured trace output. Its six completion criteria are: a debug data model, a trace event schema, capture from the runtime, a GUI and CLI format, session/crash/artifact linking, and an overhead and sampling policy. **OBSERVED.** The issue does not fix session ID or artifact ID formats, and does not address BIOS or ROM data handling. **OBSERVED.**

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

### 1.2 Standard execution report and bundle (`#457`)

| Element | Location | Label |
|---|---|---|
| `ExecutionDiagnosticReport` (schema `psxrecomp.execution-report.v1`; identifiers and classifications only) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:13` | CONFIRMED |
| Report fields `InputSha256` (`:16`) and `ArtifactIdentity` (`:19`) | `ExecutionDiagnosticReport.cs:16-19` | CONFIRMED |
| `RunCommand` sets `ArtifactIdentity` to the SHA-256 of the built artifact file (`artifactPath`, resolved at `RunCommand.cs:81`) | `src/PSXRecomp.Cli/RunCommand.cs:81`, `:188-191` | CONFIRMED |
| `RunCommand` sets `inputSha256` only on the `--report` path (`resolved.Sha256` from `CliInput.LoadWithIdentity`); otherwise the `CliInput.Load` path is used and no input hash is taken in this block | `src/PSXRecomp.Cli/RunCommand.cs:45-60` | CONFIRMED |
| Where the bundle receives `inputSha256` on the non-`--report` path | `RunCommand.cs:85-95` | UNVERIFIED (not traced) |
| `ExecutionDiagnosticLog` (projects diagnostics onto a small allow-list of context keys, max 256 entries) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:12`, allow-lists `:17-30` | CONFIRMED |
| `ExecutionDiagnosticBundleWriter` (ZIP of `report.json`, `environment.json`, `diagnostics.log`, `issue.md`; canonical timestamps) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:16` | CONFIRMED |

### 1.3 Runtime host diagnostic (`#763`)

| Element | Location | Label |
|---|---|---|
| `RuntimeHostDiagnostic` (per-run stop account; off by default, observation only; unknown fields are `null`, not guessed) | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:24` | CONFIRMED |
| `RuntimeHostRunMetrics` (wall time, MMIO and time-report round trips, last MMIO physical address and write flag, fallback timing) | `RuntimeHostDiagnostic.cs:146` | CONFIRMED |
| Per-run `runId` (GUID, generated once) | `src/PSXRecomp.RuntimeHost/Program.cs:198`; printed at `:210` | CONFIRMED |
| `runId` also written into the loaded-code mismatch report (`report["runId"] = runId`) | `src/PSXRecomp.RuntimeHost/Program.cs:624` | CONFIRMED |

### 1.4 Unit retirement trace (`#774`, the only real trace pipeline)

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
| `ExecutionTraceRing` class: fixed-capacity fetch ring plus control-transfer ring; documented as never influencing execution | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:17`; `DefaultCapacity = 64` at `:19` | CONFIRMED |
| Engine exposes the ring as `RecentTrace` and fetches the instruction word via `FetchWordForTrace` | `src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:279`, `:891` | CONFIRMED |
| `FallbackReturnDiagnostics` | `src/PSXRecomp.Core/Execution/FallbackReturnDiagnostics.cs:148` | CONFIRMED (declaration only) |
| `MixedFallbackDiagnostics` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:33` | CONFIRMED (declaration only) |
| `HostMmioAccess(PhysicalAddress, Width, IsWrite)` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:161` | CONFIRMED |
| `ArtifactFallbackSession.Attribute`: per-entry version comparison, runs only with a transition observer | `src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:143-160` | OBSERVED (`docs/runtime/loaded-code-identity.md:51`) |

### 1.6 Loaded-code mismatch diagnostics (`#773`, `#786`)

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
| Standalone report "never names an input" (issue #786 B1 comment) | `src/PSXRecomp.RuntimeHost/Program.cs:197` | OBSERVED (comment) |

### 1.8 OpenBIOS probe stop record (exposes raw fetched words)

| Element | Location | Label |
|---|---|---|
| `BuildStop` builds `lastFetches` (`Hex(e.Pc):Hex(e.Word)`, last 16) and `lastTransfers` (last 16, includes `From.Word`) from the engine trace ring | `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs:1004-1014` | CONFIRMED |
| `BuildStop` is called with the interpreter engine's `FinalSnapshot` | `OpenBiosProbeCommand.cs:361` | CONFIRMED |
| The stop object is written into the `--json` document as `stop`, with `kind = "openbios-probe"` | `OpenBiosProbeCommand.cs:419-433` (stop at `:427`) | CONFIRMED |
| The probe loads a 512 KiB OpenBIOS ROM and boots the firmware, so the interpreter fetches are expected to include BIOS-range PCs | `OpenBiosProbeCommand.cs:201-207` (ROM load); the boot design | CONFIRMED (ROM load); PREDICTED (BIOS-range fetches) |
| Whether any fetched word in `lastFetches` or `lastTransfers` lies at a BIOS address, and whether its word is BIOS instruction data | not checked (no probe run, no captured `--json` output inspected) | UNVERIFIED |

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
| Session | `UnitTraceRun` GUID (`UnitTraceRun.cs:88`), passed as `PSX_TRACE_SESSION` | One run | `session` on every record; new `sessionId` in `report.json` (optional, Q1) |
| Input identity | `ExecutionDiagnosticReport.InputSha256` (`ExecutionDiagnosticReport.cs:16`), set on the `--report` path (`RunCommand.cs:52-58`) | One input file, same across rebuilds of the same input | Trace header field `inputSha256`, reusing the existing name |
| Built-artifact identity | `ExecutionDiagnosticReport.ArtifactIdentity` (`ExecutionDiagnosticReport.cs:19`), set from the file hash of the built artifact (`RunCommand.cs:191`) | One build output; changes whenever the build output changes | Trace header field `artifactSha256`, reusing the existing `ArtifactIdentity` value |
| Run ID | `runId` (`RuntimeHost/Program.cs:198`, also `:624`); `GuardStatsScope` run ID (`GuardStatsScope.cs:18`) | One run | Retire for trace purposes (Q2) |

**Decision in this design.** No new `artifactId` field is added. The trace header carries the two existing hashes under their existing meanings: `inputSha256` (reproducible across rebuilds of the same input) and `artifactSha256` (identifies the exact build output that ran). **PREDICTED.** Reason: `ArtifactIdentity` is already a built-artifact hash, so calling it an input hash would contradict the report. Whether `artifactSha256` is stable across rebuilds of the same input is **UNVERIFIED** (not checked; depends on the build's determinism).

Linking rules (PREDICTED):

1. The parent generates `session` once. It goes into the environment (`PSX_TRACE_SESSION`), into every trace header and record, into `report.json` as an optional field, and into `diagnostics.log` lines as a context key.
2. A `Diagnostic` links to a trace by `DiagnosticEvidenceReference(kind=trace, identifier=session)`. A trace links back to its input and build by the header hashes above.
3. A join is rejected if `session` differs between streams. The unit trace already does this (`unit-trace.md`, "Identity").
4. The existing `ProductRevision` and `InputSha256` report fields are kept as they are. **CONFIRMED** (fields exist).

Standalone artifacts: ADR-027 (Accepted, Issue #739) says the standalone artifact embeds its image and initial state and reads no input files at runtime, and that `run --standalone` supplies the host from the CLI process. **OBSERVED** (`docs/adr/027-standalone-artifact-runtime-integration.md`, "Decision" and "Step A scope"). So for a standalone run, the only runtime identity present is the built artifact unless `--report` also supplies an input hash. Q3 covers this.

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

Measurements required before default levels are chosen: wall time and output bytes for L0, L1, and L2 on one standalone packaged run. **UNVERIFIED** (no measurement in this work).

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
- **Exposure 2 (CONFIRMED):** the OpenBIOS probe `--json` output includes `stop.lastFetches` and `stop.lastTransfers` (`OpenBiosProbeCommand.cs:1004-1014`, written at `:419-433`). Each entry carries a raw instruction word (`Hex(e.Word)`, `Hex(From.Word)`). These are words the interpreter fetched from guest memory. **Whether any entry lies in the BIOS range (`0x1FC00000`-`0x1FC7FFFF` physical, with `BiosAddressGuard.PhysicalBase` at `BiosAddressGuard.cs:103`) is UNVERIFIED.** The probe is designed to boot OpenBIOS, so BIOS-range fetches are PREDICTED to occur. Any such entry would publish raw BIOS instruction words in the `--json` output.

Proposed boundary (PREDICTED):

1. Trace schema v1 has no field for memory bytes. A `value` field on `mmio` is allowed only in a local-only trace level, never in the share-ready bundle.
2. The share-ready bundle (`#457`) includes only trace records with no `value` and no word arrays. `windowAot`, `windowRam`, `lastFetches`, and `lastTransfers` are removed from the share-ready form, or reduced to a hash of the window or fetch list instead of printed words.
3. BIOS bytes never enter any trace or report. Only existing `BiosContentDiagnostic` counts and hashes may appear. `BiosAddressGuard` is the enforcement point. Exposure 2 must be checked against this rule first (see Q4 and Q11).
4. ROM and disc bytes never enter a trace. Identity is by hash only (`loaded-code-identity.md`, "The hash is not runtime proof" and the hash-for-identity rule).
5. A writer-side test enforces (1) to (4): a trace written at L2 must contain no field outside the allow-list, and no hex run of 8 or more words that matches a loaded image window. **PREDICTED** (test does not exist).

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, and `event` kinds. Do not invent a second format. **PREDICTED** (rationale: the existing joiner, tests, and parsers already implement the rules).
2. Make `session` the single run identity for trace purposes. Retire `runId` (`RuntimeHost/Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them only as aliases. **PREDICTED.**
3. Reuse the existing `inputSha256` and `ArtifactIdentity` (`artifactSha256`) hashes; add no new artifact ID field (section 4). **PREDICTED.**
4. Ship L1 (boundary) first. Its cost is near zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **PREDICTED.**
5. Fix the share-ready redaction gap (both exposures in section 7) before any trace or bundle is shared. The gap is **CONFIRMED**; the fix is **PREDICTED.**

## 9. Open questions and the decision each needs

Ownership note: the GitHub API responses read for this document show no assignee on #45, #457, #763, #773, #49, #40, or #42. #786 is a pull request, not an issue. ADR-027 has no owner field. So no per-issue owner is recorded for any question below. **OBSERVED.** The decision-maker named in each row is the repository owner (mao2009) unless the row says otherwise, and the tracking item is the one named in the row.

| # | Question | Decision point | Tracking item | Evidence label |
|---|---|---|---|---|
| Q1 | Is `session` a new optional field in `report.json` (schema bump to `v2`) or an optional addition within `v1`? | Repository owner (mao2009); no per-issue owner recorded | #457 (schema issue; no assignee recorded) | CONFIRMED that v1 has no session field |
| Q2 | Retire the standalone `runId` (`Program.cs:198`, written at `:624`) and the guard-stats run ID for trace purposes, or keep them as aliases? | Repository owner (mao2009); no per-issue owner recorded | #763 (runtime host diagnostic; no assignee recorded) and #773 (loaded-code report; no assignee recorded) | CONFIRMED both exist and both are written |
| Q3 | For `run --standalone` artifacts, which hash does the trace header carry? `ArtifactIdentity` (always set from the built artifact, `RunCommand.cs:191`) is available. `InputSha256` exists only on the `--report` path (`RunCommand.cs:52-58`). Should `InputSha256` also be computed on the standalone path, or is `artifactSha256` enough? ADR-027 says the standalone artifact reads no input files at runtime. | Repository owner (mao2009); no ADR owner field exists | #739 (the ADR-027 issue, per ADR-027 header) | OBSERVED (ADR-027, Accepted, Issue #739); CONFIRMED (`RunCommand.cs`) |
| Q4 | Are `windowAot` and `windowRam` in the loaded-code report removed, reduced to hashes, or kept only in a local-only level? | Repository owner (mao2009); no per-issue owner recorded | #786 is a pull request, not an issue; the issue carrying the loaded-code report is #773 (no assignee recorded) | CONFIRMED the words are written |
| Q5 | Which trace level is the default for `PSXRecomp.RuntimeHost --diagnostics`? | Repository owner (mao2009); no per-issue owner recorded | #763 (no assignee recorded) | OBSERVED that the flag is off by default |
| Q6 | Is MMIO `value` ever allowed outside a local-only trace? Design position: no. | Repository owner (mao2009); no per-issue owner recorded | #457 (no assignee recorded) | PREDICTED position |
| Q7 | Are GPU, SPU, CD, and DMA events in trace schema v1, or added in a later version? The #45 scope list names them (section 0). | Repository owner (mao2009); no per-issue owner recorded | #45 itself (open; no assignee recorded) | OBSERVED (#45 scope list) |
| Q8 | What is the acceptable overhead budget for L2, for example percent of wall time on one standard run? | Repository owner (mao2009); no per-issue owner recorded | #45 (completion criterion 6) | UNVERIFIED (no budget in repo found in this work) |
| Q9 | Should the Studio GUI (#40) and CLI read the same JSON Lines files, or a joined summary only? | Repository owner (mao2009); no per-issue owner recorded | #45 (criterion 4) with #40 and #42 (no assignees recorded) | OBSERVED (#45 criterion 4); UNVERIFIED (GUI code not searched) |
| Q10 | May trace timestamps appear in share-ready bundles, given the determinism rule in `docs/architecture/diagnostics.md` section 5? | Repository owner (mao2009); no per-issue owner recorded | #49 (no assignee recorded) | CONFIRMED that the current contract excludes time |
| Q11 | Does any fetched word in `OpenBiosProbeCommand` `stop.lastFetches` or `lastTransfers` lie in the BIOS range, and if so, must those words be removed from `--json` output? | Repository owner (mao2009); no per-issue owner recorded | #45 (safety boundary is not in its body; this design adds it) | UNVERIFIED (not checked by running the probe) |

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- Issue #45 was read through a summary of its public body; verbatim text was not retrieved. **OBSERVED.**
- The `RunCommand` path that passes `inputSha256` to the bundle on non-`--report` runs was not traced (`RunCommand.cs:85-95`). **UNVERIFIED.**
- GUI code was not searched exhaustively.
