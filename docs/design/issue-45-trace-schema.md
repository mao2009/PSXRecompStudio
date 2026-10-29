# Issue #45 Debug / Trace Schema Design

**Status:** Proposal (design only, no code change)

**Related Issues:** #45 (Debug / Trace Infrastructure); related #40, #42, #44, #16, #132, #457, #763, #774

**Base:** `2e3ecad` (origin/main)

## 0. How to read the labels

Every claim carries one of four labels:

- **CONFIRMED**: read directly in source, docs, or test code at the cited location in this checkout.
- **OBSERVED**: stated by an existing doc or test comment at the cited location. Not re-run in this work.
- **PREDICTED**: a design expectation this document proposes. Not yet true.
- **UNVERIFIED**: not checked, or checkable only by running the system or by reading a source not available here.

Issue #45 scope was read from the public GitHub API summary (the body was not quoted in full). Its six completion criteria are: define a debug data model, a trace event schema, capture from the runtime, a GUI and CLI format, session/crash/artifact linking, and overhead and sampling policy. **OBSERVED** (issue summary via public API; body not reproduced here). The issue text does not fix session ID or artifact ID formats, and does not address BIOS or ROM data handling. **OBSERVED** (same source).

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

`docs/architecture/diagnostics.md` section 4 states that the full evidence and trace capture pipeline belongs to Issues #16, #45, #132, and that the contract only defines how a diagnostic points at evidence. **OBSERVED** (`docs/architecture/diagnostics.md:86-88`).

### 1.2 Standard execution report and bundle (`#457`)

| Element | Location | Label |
|---|---|---|
| `ExecutionDiagnosticReport` (schema `psxrecomp.execution-report.v1`; identifiers and classifications only; no RAM, save data, paths) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticReport.cs:13` | CONFIRMED |
| Report fields: `InputSha256`, `ArtifactIdentity`, `GuestPc`, `DiagnosticCode`, `FramebufferSha256` | `ExecutionDiagnosticReport.cs:13-25` | CONFIRMED |
| `ExecutionDiagnosticLog` (projects diagnostics onto a small allow-list of context keys, max 256 entries) | `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:12`, allow-lists `:17-30` | CONFIRMED |
| `ExecutionDiagnosticBundleWriter` (ZIP of `report.json`, `environment.json`, `diagnostics.log`, `issue.md`; canonical timestamps) | `src/PSXRecomp.Infrastructure/Diagnostics/ExecutionDiagnosticBundleWriter.cs:16` | CONFIRMED |

### 1.3 Runtime host diagnostic (`#763`)

| Element | Location | Label |
|---|---|---|
| `RuntimeHostDiagnostic` (per-run stop account; off by default, observation only; unknown fields are `null`, not guessed) | `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostDiagnostic.cs:24` | CONFIRMED |
| `RuntimeHostRunMetrics` (wall time, MMIO and time-report round trips, last MMIO physical address and write flag, fallback timing) | `RuntimeHostDiagnostic.cs:146` | CONFIRMED |
| Per-run `runId` (GUID, printed to stderr when loaded-code report is requested) | `src/PSXRecomp.RuntimeHost/Program.cs:198`, `:210` | CONFIRMED |

### 1.4 Unit retirement trace (`#774`, the only real trace pipeline)

| Element | Location | Label |
|---|---|---|
| Design and schema, JSON Lines, `v:1`, `kind` records, session and handoff identity | `docs/development/unit-trace.md`, sections "Enabling", "Identity", "Record schema" | OBSERVED |
| `UnitTraceScope.Begin` (in-process enable for tests) and `UnitTraceRun` (child env, parent fallback file) | `src/PSXRecomp.Infrastructure/UnitTraceRun.cs:15`, `:54` | CONFIRMED |
| Session ID generated per run as `Guid.NewGuid().ToString("N")` and passed to child via `PSX_TRACE_SESSION` | `src/PSXRecomp.Infrastructure/UnitTraceRun.cs:88`, `:112` | CONFIRMED |
| Native side: opt-in trace build, env `PSX_UNIT_TRACE_FILE`, `PSX_UNIT_TRACE_MAX_BYTES` (default cap in `RECOMPILER_TRACE_DEFAULT_CAP`), `PSX_TRACE_SESSION` sanitised to `[0-9A-Za-z-]`, truncation and end records | `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:79`, `:1037-1077`, `:1121-1162` | CONFIRMED |
| Joiner: `Complete` / `Partial` / `Rejected`, sequence gap checks, handoff pairing | `src/PSXRecomp.Core/Diagnostics/UnitTraceJoiner.cs:21`, status enum `:403`, segment source `:410`, `UnitTraceSegment` `:425`, `UnitTraceJoin` `:438` | CONFIRMED |
| Native trace records carry CPU state (`gpr` x32, `hi`, `lo`, `sr`, `cause`, `epc`, `pc`) at unit end, trace build only | `unit-trace.md` "Record schema"; `CpuCheckpoint.cs:17`, `TryParse` `:38` | OBSERVED (doc) / CONFIRMED (parser) |
| Disabled path emits byte-identical generated source | `unit-trace.md` "Enabling"; test `HostCodeGenTests` (`UnitTrace_*`) | OBSERVED |

### 1.5 Interpreter fetch ring and fallback diagnostics

| Element | Location | Label |
|---|---|---|
| `ExecutionTraceRing`: fixed-capacity fetch ring (default 64) plus control-transfer ring; "never influences execution" | `src/PSXRecomp.Core/Execution/ExecutionTraceRing.cs:14`, `:17`, `DefaultCapacity` `:19` | CONFIRMED |
| `FallbackReturnDiagnostics` | `src/PSXRecomp.Core/Execution/FallbackReturnDiagnostics.cs:148` | CONFIRMED (declaration only; contents not read) |
| `MixedFallbackDiagnostics` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:33` | CONFIRMED (declaration only) |
| `HostMmioAccess(PhysicalAddress, Width, IsWrite)` | `src/PSXRecomp.Core/Execution/MixedFallback.cs:161` | CONFIRMED |
| `ArtifactFallbackSession.Attribute`: per-entry version comparison, runs only with a transition observer | `src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:143-160` | OBSERVED (`docs/runtime/loaded-code-identity.md:51`) |

### 1.6 Loaded-code mismatch diagnostics (`#773`, `#786`)

| Element | Location | Label |
|---|---|---|
| `LoadedCodeMismatchDiagnostics` (bounded records, bounded compared words) | `src/PSXRecomp.Core/Recompiler/LoadedCodeMismatchDiagnostics.cs:19`, `:25`, `:42` | CONFIRMED |
| Mismatch record keeps a 5-word window of AOT words and RAM words at the first refusal, and writes them to JSON as `windowAot` and `windowRam` | `LoadedCodeMismatchDiagnostics.cs:30` (`WindowRadius = 2`), `:269-273`, `:296-297` | CONFIRMED |
| Comment calls this "a bounded sample, not a dump of the image" | `LoadedCodeMismatchDiagnostics.cs:268` | CONFIRMED (comment) |

This is the one place in the current code that writes raw instruction words to a published report. See section 7.

### 1.7 BIOS and ROM guards

| Element | Location | Label |
|---|---|---|
| `BiosAddressGuard.IsBiosAddress`, `OverlapsBios`, `RequireNoBiosImage` (refuses BIOS code in AOT generation) | `src/PSXRecomp.Infrastructure/Standalone/BiosAddressGuard.cs:100`, `:113`, `:120`, `:146` | CONFIRMED |
| `BiosContentDiagnostic`: counts and provenance text, no bytes | `BiosAddressGuard.cs:81` | CONFIRMED |
| Standalone report "never names an input" (issue #786 B1) | `src/PSXRecomp.RuntimeHost/Program.cs:197` comment | OBSERVED |

### 1.8 Screenshots and manifest

| Element | Location | Label |
|---|---|---|
| Screenshot result and execution JSON records | `src/PSXRecomp.Cli/ScreenshotCommand.cs:353`, `:368` | CONFIRMED |
| Guest PC source and timing (`capture-instant`) | `src/PSXRecomp.Infrastructure/Runtime/Gpu/ScreenshotCapture.cs:46-48` | CONFIRMED |

### 1.9 Identity hashes already in use

| Element | Location | Label |
|---|---|---|
| Input identity: SHA-256 of EXE and disc, provenance only for disc | `docs/runtime/loaded-code-identity.md:14`, `:45` | OBSERVED |
| `GuardStatsScope` run ID, random 64-bit hex per child process | `src/PSXRecomp.Infrastructure/GuardStatsScope.cs:18`; `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:1345` | CONFIRMED |
| `ObservedRunIdentity` (firmware SHA, manifest, disc SHA, probe options SHA) | `src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:46`, `:262` | CONFIRMED |

### 1.10 Gaps in the inventory

- **No shared session ID.** The unit trace has a session ID. The `#457` report has none; the standalone host has a separate `runId` (`Program.cs:198`); the guard-stats scope has another. **CONFIRMED** (by the listed locations).
- **No MMIO trace.** `HostMmioAccess` and the `LastMmio*` metrics record only the last access. No stream of MMIO events exists. **CONFIRMED** (by inspection of listed types; a full search was not done).
- **No GPU, SPU, CD or DMA event stream.** Only the device interfaces (`IGuestDeviceAccess.cs`, `ITimer.cs`, `IGpu.cs`, `ISpu.cs`, `ICdRom.cs`) exist. **OBSERVED** (file list only).
- **No timestamps.** The deterministic JSON forms exclude time by design. A time axis for trace records does not exist. **CONFIRMED** (`diagnostics.md` section 2).
- **Studio GUI and CLI consumption of a trace format**: none found in this inventory. **UNVERIFIED** (a full GUI search was not done).

## 2. Common model

Four record families share one envelope. The envelope is the same as the existing `v:1` JSON Lines style of the unit trace, so the parser patterns can be reused. **PREDICTED** (design).

```
Envelope (every record):
  v        int      schema version, starts at 1
  seq      uint     strictly increasing per stream, gap = reject (matches unit-trace rule)
  stream   string   "native" | "fallback" | "host" | "gui" | "cli" (writer identity)
  kind     string   one of the record kinds below
  session  string   session ID (section 4)
  t        uint64?  optional monotonic ns from the writer; null when not sampled
```

Record families:

| Family | `kind` values | Core fields | Source of truth |
|---|---|---|---|
| PC / control | `unit`, `handoff`, `fallback`, `transfer`, `stop` | `pc`, `next`, `retired`, `termination`, `status` | Extends existing unit-trace kinds (`unit-trace.md`) |
| Register state | `checkpoint` | `pc`, `gpr[32]`, `hi`, `lo`, `sr`, `cause`, `epc`, `globalRetired` | `CpuCheckpoint` (`CpuCheckpoint.cs:17`) |
| MMIO | `mmio` | `physAddr`, `width`, `isWrite`, `value`?, `pc`? | `HostMmioAccess` (`MixedFallback.cs:161`) |
| Runtime event | `event` | `device` (`gpu`, `spu`, `cdrom`, `dma`, `timer`, `irq`, `bios`), `name`, `code`?, `data`? | New; BIOS call identity exists (`BiosCallIdentity`, `BiosServiceResult.cs:152`) |

Diagnostic records (`Diagnostic`, section 1.1) are not trace records. They are linked into a trace by reference only (`DiagnosticEvidenceReference`). **CONFIRMED** (current contract) / **PREDICTED** (use).

Field rules:

- Addresses are `uint` and written as hex strings in JSON, matching the existing unit trace.
- No record carries a host path, user name, or wall-clock time by default. Time is opt-in (section 5).
- `value` on an MMIO record is off by default (section 7).

## 3. Trace event schema

Recommended schema v1 is an extension of the unit-trace file, not a new format. **PREDICTED**.

- Native stream: unchanged `unit`, `handoff`, `truncated`, `end` records (`unit-trace.md`).
- Fallback stream: unchanged `fallback`, `transfer`, `truncated`, `end` records.
- New `mmio` and `event` kinds go in a third stream, written by the host (not the generated C), so the native trace build stays byte-identical in the non-trace case. **PREDICTED** (the byte-identical property is **OBSERVED**, `unit-trace.md` "Enabling").
- The joiner rule for sequence gaps and session match applies per stream. A new joiner pass merges the third stream by `(session, globalRetired)`. **PREDICTED**.

Example (illustrative, not implemented):

```json
{"v":1,"seq":12,"stream":"host","kind":"mmio","session":"3f9c0a...","globalRetired":4821,"physAddr":"0x1F801070","width":4,"isWrite":true,"pc":"0x80012A44"}
```

`pc` on an MMIO record is optional because the native side does not report the access PC today. **OBSERVED** (`RuntimeHostRunMetrics` carries only the last address, not the PC; `RuntimeHostDiagnostic.cs:146-160`).

Known semantic limits that the schema must carry, not hide: serviced-SYSCALL counting, PC after interrupt, termination 5 with fallback stop, SYSCALL in delay slot. **OBSERVED** (`unit-trace.md` "Known semantic differences").

## 4. Linking to session ID and artifact ID

Proposed identities:

| ID | Format | Scope | Source |
|---|---|---|---|
| `sessionId` | 32-hex GUID (`N` format), generated once per run by the parent | One run, shared by every stream | Unit trace already does this (`UnitTraceRun.cs:88`) |
| `artifactId` | Input-identity SHA-256 of the EXE, `sha256:` prefix | One build input | `ArtifactIdentity` (`ExecutionDiagnosticReport.cs:19`); `RunCommand.cs:191` |
| `runId` | Existing `RuntimeHostDiagnostic` GUID | Retire in favour of `sessionId` | `RuntimeHost/Program.cs:198` |

Linking rules (PREDICTED):

1. The parent generates `sessionId` once. It goes into the environment (`PSX_TRACE_SESSION`), into every trace header, into `report.json` as `sessionId` (new optional field), and into `diagnostics.log` lines as a context key.
2. `artifactId` is never generated per run. It is the input hash, so the same EXE gives the same `artifactId` across runs. This keeps "same input, different session" comparable.
3. A `Diagnostic` links to a trace by `DiagnosticEvidenceReference(kind=trace, identifier=sessionId)`. A trace links back to its artifact by the header field `artifactId`.
4. A join is rejected if `sessionId` differs between streams (already true for the unit trace, `unit-trace.md` "Identity").
5. The existing `ProductRevision` and `InputSha256` fields are kept. **CONFIRMED** (fields exist).

Open: whether the `artifactId` for a standalone packaged artifact should be the EXE hash, the artifact manifest hash, or both. See section 9.

## 5. Performance cost and sampling

What exists:

- Unit trace is opt-in, costs nothing when off (byte-identical generated source). Default output cap 64 MiB. **OBSERVED** (`unit-trace.md`).
- Interpreter fetch ring is fixed-size (64 by default) and always on. **CONFIRMED** (`ExecutionTraceRing.cs:19`); its per-fetch cost was not measured. **UNVERIFIED**.
- Loaded-code mismatch comparison is bounded by `DefaultMaxComparedWords = 1<<24` and a record cap. **CONFIRMED** (`LoadedCodeMismatchDiagnostics.cs:25`).
- Fallback attribution is per-transition and only with an observer. Full-RAM compare on every fallback return is the largest known avoidable cost. **OBSERVED** (`docs/runtime/loaded-code-identity.md:83`, labelled INFERRED there; not measured).

Recommended policy (PREDICTED):

| Level | Trigger | What is recorded | Expected cost |
|---|---|---|---|
| L0 Off | default | Nothing new | 0 (must stay byte-identical) |
| L1 Boundary | `--trace` | Unit trace plus `handoff`, `transfer`, `stop`, `checkpoint` at existing boundaries | Per boundary; UNVERIFIED |
| L2 Event | `--trace=events` | Adds `event` and `mmio` records | Per MMIO access; UNVERIFIED, likely the dominant cost |
| L3 Sampled | `--trace=events --sample N` | Every N-th `mmio` or `event`, with a counter so drops are visible | Scales with 1/N; UNVERIFIED |

Sampling rules (PREDICTED):

- Sampling is deterministic (every N-th by `seq` within a stream), never random, so two runs of the same input are comparable.
- Every sampled-out record is counted in the next kept record (`skipped` field) so consumers know the trace is incomplete. Incomplete traces join as `Partial` (existing status, `unit-trace.md` "Joining").
- Caps are per stream, using the existing `truncated` record pattern.
- Diagnostics and `stop` records are never sampled out.

Measurements required before the default levels are chosen: wall time and output bytes for L0 vs L1 vs L2 on one standalone packaged run. **UNVERIFIED** (no measurement in this work).

## 6. Crashes, sessions, artifacts

- A crash produces a `stop` record with the `Diagnostic` code and `sessionId`, then the existing bundle is written with the trace files beside `report.json`. **PREDICTED**.
- A killed run leaves an unterminated trace. The existing rule (last partial line discarded, join `Partial`) already covers this. **OBSERVED** (`unit-trace.md` "No end line").
- The `#457` bundle is the shareable unit. Trace files join it only after the redaction in section 7. **PREDICTED**.

## 7. Safety boundary: BIOS and ROM raw data

Current state:

- Unit trace records contain CPU registers and PCs, not memory bytes. **OBSERVED** (`unit-trace.md` "Record schema"; `CpuCheckpoint` has no memory field, `CpuCheckpoint.cs:17-25`). **CONFIRMED** for `CpuCheckpoint`.
- `ExecutionDiagnosticReport` has no field for input payloads, RAM, save data, or paths. **CONFIRMED** (`ExecutionDiagnosticReport.cs:8-10`).
- BIOS content diagnostic reports counts only. **CONFIRMED** (`BiosAddressGuard.cs:81`).
- **Exception found:** `LoadedCodeMismatchDiagnostics` writes `windowAot` and `windowRam` (5 words each, around the first mismatch) into JSON. These are raw instruction words from the game's executable and from guest RAM. The comment calls it a bounded sample. Being bounded is not the same as being redacted. **CONFIRMED** (`LoadedCodeMismatchDiagnostics.cs:296-297`, `:269-273`). Whether this output is ever published is **UNVERIFIED**.

Proposed boundary (PREDICTED):

1. Trace schema v1 has no field for memory bytes. A `value` field on `mmio` is allowed only in a local-only trace level, never in the share-ready bundle.
2. The share-ready bundle (`#457`) includes only trace records that have no `value` and no word arrays. `windowAot` and `windowRam` are removed from the share-ready form or hashed (SHA-256 of the window) instead of printed.
3. BIOS bytes never enter any trace or report. Only the existing `BiosContentDiagnostic` counts and hashes may appear. Refer to `BiosAddressGuard` as the enforcement point.
4. ROM and disc bytes never enter a trace. Identity is by hash only (`loaded-code-identity.md`, "The hash is not runtime proof" and the hash-for-identity rule).
5. A writer-side test enforces (1) to (4): a trace written at L2 must contain no field outside the allow-list, and no hex run of 8 or more words that matches a loaded image window. **PREDICTED** (test does not exist).

## 8. Recommendation

1. Adopt the unit-trace envelope (`v`, `seq`, `kind`, session) as trace schema v1, and add `stream`, `mmio`, `event` kinds. Do not invent a second format. **PREDICTED** (rationale: existing joiner, tests, and parsers already implement the rules).
2. Make `sessionId` the single run identity. Retire `runId` in the standalone host and the separate guard-stats run ID for trace purposes, or keep them only as aliases. **PREDICTED**.
3. Keep `artifactId` as the input hash. Do not generate it per run. **PREDICTED**.
4. Ship L1 (boundary) first. Its cost is near-zero and reuses existing records. Add L2 and sampling only after the measurement in section 5 exists. **PREDICTED**.
5. Fix the share-ready redaction gap (section 7, exception) before any trace or bundle is shared. **CONFIRMED** that the gap exists in code; the fix is **PREDICTED**.

## 9. Open questions and the decision each needs

| # | Question | Decision needed from | Label of current evidence |
|---|---|---|---|
| Q1 | Is `sessionId` a new field in `report.json` (schema bump to `v2`) or an optional field in `v1`? | Owner of `#457` schema | CONFIRMED that v1 has no session field |
| Q2 | Retire the standalone `runId` and the guard-stats run ID in favour of `sessionId`, or keep both? | Owner of `#763` and `#773` runtime host | CONFIRMED both exist |
| Q3 | For standalone artifacts, is `artifactId` the EXE hash, the manifest hash, or both? | Owner of ADR 027 (`docs/adr/027-standalone-artifact-runtime-integration.md`) | UNVERIFIED (ADR not read in this work) |
| Q4 | Is `windowAot`/`windowRam` in the loaded-code report removed, hashed, or kept only in a local-only level? | Owner of `#786` loaded-code diagnostics | CONFIRMED the words are written |
| Q5 | Which trace level is the default for `PSXRecomp.RuntimeHost --diagnostics`? | Owner of `#763` | OBSERVED that the flag is off by default |
| Q6 | Is MMIO `value` ever allowed outside a local-only trace? | Security/owner review of `#457` privacy rules | PREDICTED position: no |
| Q7 | Is GPU/SPU/CD/DMA event coverage in scope for v1, or only BIOS-call and IRQ events? | Issue #45 owner (scope read via summary only) | UNVERIFIED (full issue body not read) |
| Q8 | What is the acceptable overhead budget for L2, for example percent wall time on one standard run? | Performance owner | UNVERIFIED (no budget in repo found in this work) |
| Q9 | Should the Studio GUI (#40) and CLI read the same JSON Lines files, or a joined summary only? | Owner of #40 and #42 | UNVERIFIED |
| Q10 | Are `t` timestamps allowed in share-ready bundles at all, given the determinism rule in `diagnostics.md` section 5? | Owner of `#49` diagnostics contract | CONFIRMED that the current contract excludes time |

## 10. Limits of this document

- This is a design only. No code, test, or schema file was changed.
- Overhead numbers are not measured.
- The full text of Issue #45 was not read; its scope comes from a summary of the public API. **OBSERVED**.
- ADR and GUI code were not searched exhaustively.
