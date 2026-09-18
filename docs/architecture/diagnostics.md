# Diagnostics & Recovery

**Status:** Stable

**Authority:** Subsystem SSOT

**Related Issues:** #49

## 1. Purpose

Issue #49 asks for a shared contract so failures from Analysis, Recompiler,
Build, and Runtime can be traced from "what happened" to "what to do next" in
one machine-readable shape, usable identically from the GUI, the CLI, JSON
output, and (eventually) AI-assisted diagnosis. `PSXRecomp.Core.Diagnostics`
(Domain layer) is that contract.

This document defines the model as it exists today. It does not define a
logging system, a trace/evidence-capture pipeline, a crash uploader, an AI
diagnosis implementation, a GUI diagnostics screen, a full CLI presentation,
or a retry scheduler — those remain out of scope for #49 and are tracked by
their own issues (see [Relationship to adjacent concerns](#7-relationship-to-adjacent-concerns)).

## 2. The `Diagnostic` model

A `Diagnostic` (`Diagnostic.cs`) is an immutable record describing one
expected/domain failure or notable outcome:

| Member | Type | Meaning |
|---|---|---|
| `Code` | `DiagnosticCode` | Stable machine-readable identity (SSOT for GUI/CLI/JSON/automation). |
| `Category` | `DiagnosticCategory` | The subsystem that owns the problem surface. |
| `Severity` | `DiagnosticSeverity` | How bad it is and whether the operation could continue. Defaults to `Error`. |
| `Stage` | `DiagnosticStage` | The coarse pipeline phase, independent of any subsystem's own stage enum. |
| `Message` | `string?` | Human-readable secondary text; never required to identify the failure. |
| `MessageKey` | `string?` | Locale-neutral key Issue #21 localization resolves against `Code`'s structured context; `null` until that work lands. |
| `Context` | `IReadOnlyList<DiagnosticContextEntry>` | Structured key/value metadata (guest PC, opcode, file identity, ...). |
| `Evidence` | `IReadOnlyList<DiagnosticEvidenceReference>` | References to the evidence backing the diagnostic. |
| `Recovery` | `DiagnosticRecovery` | What to do next and under what retry semantics. |

`Diagnostic.IsValid()` checks that `Code` is non-empty, every enum member is
defined, and every `Context`/`Evidence` entry and `Recovery` is itself valid.
It is the validation entry point for externally supplied documents, so it
returns `false` for malformed-but-parseable data and never throws: a JSON
payload can write `null` into `Context`/`Evidence`, or `null` elements into
them, despite their non-nullable annotations, and such a document is reported
invalid rather than raising.

The model carries no timestamp, host name, username, or absolute path, so the
same problem always serializes to the same document (see
[§5 Determinism and JSON](#5-determinism-and-json)).

## 3. Code, severity, category, stage

- **`DiagnosticCode`** (`DiagnosticCode.cs`) is a validated `SCREAMING_SNAKE`
  string (`^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*$`). It does not need to be
  registered to be valid — an unknown-but-well-formed code from a newer
  contract version is still first-class (fail-safe against skew), which is
  why the shape check, not a closed enum, is the identity boundary. Existing
  stable string codes already in the codebase (e.g. `BIOS_HLE_UNSUPPORTED_CALL`,
  `OUTER_BUDGET_EXHAUSTED`) satisfy the shape and elevate directly.
- **`DiagnosticCodes`** (`DiagnosticCodes.cs`) registers the well-known codes
  production paths currently emit, each `<SUBSYSTEM_PREFIX>_<CONDITION>`. A
  code does not have to be listed there to be valid; registration exists for
  discoverability only.
- **`DiagnosticSeverity`**: `Info` / `Warning` / `Error` / `Fatal`. `Fatal`
  means the process/session cannot continue, not just the operation.
- **`DiagnosticCategory`**: the owning subsystem (`Analysis`, `Disc`,
  `Recompiler`, `Build`, `Runtime`, `Configuration`, `Input`, `MemoryCard`,
  `Infrastructure`). Deliberately not grown speculatively — a category needs
  an existing or accepted architectural role.
- **`DiagnosticStage`**: the coarse pipeline phase (`Unknown`, `Input`,
  `Analysis`, `Recompile`, `Build`, `Execute`, `Report`), independent of and
  coarser than any subsystem's own stage enum (e.g. `RomAnalysisStage`, which
  stays in `Context`/mapping logic, not in the shared contract).

## 4. Context, evidence, recovery

- **Context** (`DiagnosticContextEntry` + `DiagnosticContextKeys`): a typed
  key/value slot (`uint` for addresses/opcodes, `string` for identities/hashes/
  names), following the existing `RecompilerIrMetadataEntry` pattern rather
  than a free-form dictionary. Keys are a shared, stable vocabulary
  (`guestPc`, `guestAddress`, `opcode`, `biosCall`, `file`, `discHash`, `tool`,
  `exceptionType`, `failureKind`, `count`).
- **Evidence** (`DiagnosticEvidenceReference` + `DiagnosticEvidenceKind`): a
  lightweight *reference* to evidence (trace, log, artifact) — kind plus a
  stable identifier — never an embedding of the evidence itself. The full
  evidence/trace capture pipeline is Issues #16/#45/#132; this contract only
  defines how a diagnostic points at evidence once it exists.
- **Recovery** (`DiagnosticRecovery`, `DiagnosticRecoveryAction`,
  `DiagnosticRetrySemantics`): a single `Recoverable: bool` cannot distinguish
  the cases that actually matter, so retry semantics is a closed four-value
  set:
  - `NotRetryable` — retrying will not help.
  - `RetrySameRequest` — a transient tool/engine failure; the only case
    eligible for automatic retry (`AutomaticRetryAllowed`).
  - `RetryAfterUserChange` — the user/caller must change an input or
    configuration first (`RequiresUserAction`).
  - `RetryAfterExternalChange` — an external state (device, memory-card slot,
    file on disk) must change first; the same logical request then becomes
    valid.

  `DiagnosticRecoveryAction` is a closed, documented action set (`Retry`,
  `RetryAfterChange`, `ProvideInput`, `ChangeConfiguration`, `UseFallback`,
  `Reanalyze`, `Rebuild`, `ReportBug`, `None`) so a GUI can render a concrete
  action button and automation can execute a recovery step, rather than
  parsing a free-form instruction string. `DiagnosticRecovery.IsValid()`
  enforces the internal consistency between `Action`, `Retry`, and
  `RequiresUserAction`: the admissible retry semantics are derived from each
  action's own meaning rather than from a list of known-bad pairs. `None` and
  `ReportBug` declare that no local recovery path exists, so they admit only
  `NotRetryable` and no `RequiresUserAction`; `Retry` means "the same request,
  unchanged", so it admits only `RetrySameRequest` and
  `RetryAfterExternalChange`. Every other action names a change to make and
  therefore rejects `RetrySameRequest`; it remains compatible with
  `NotRetryable`, `RetryAfterUserChange`, and
  `RetryAfterExternalChange`, so an action added later is valid by default
  except for the unchanged-request semantic.

## 5. Determinism and JSON

`DiagnosticJson` (`DiagnosticJson.cs`) follows the same determinism contract
as the analysis artifact JSON (`ArtifactJson`): camelCase keys, two-space
indentation, LF line endings, a single trailing LF, and nulls written rather
than omitted (the key set depends only on the schema). Enums and
`DiagnosticCode` serialize as their stable string values.

`DiagnosticComparer` (`DiagnosticComparer.cs`) gives a stable total order over
a set of diagnostics (`Category` → `Stage` → `Code` → context-entry count) so
any code path that emits more than one diagnostic produces the same order
regardless of collection or dictionary iteration order.

## 6. Exception boundary

`DiagnosticExceptionPolicy.IsProcessFatal(Exception)` names the boundary
between an expected/domain failure (→ a `Diagnostic`) and a programming
bug/invariant violation/process-fatal condition (→ an exception that must
propagate):

- Malformed input, unsupported coverage, missing files, and tool failures are
  classified as `Diagnostic` values and flow through results.
- `OutOfMemoryException`, `StackOverflowException`, and
  `AccessViolationException` (and their subtypes) are process-fatal and must
  never be caught and converted into a `Diagnostic`; recovering from them
  in-process is not defined. Production catches should read
  `catch (Exception e) when (!DiagnosticExceptionPolicy.IsProcessFatal(e))`.
- **Cancellation is not a failure.** `IsProcessFatal` answers only whether an
  exception is process-fatal, and reports `false` for
  `OperationCanceledException`, because cancellation is a control-flow signal
  that the caller asked to stop — not a problem to diagnose. The guard above is
  therefore insufficient on a boundary that can be cancelled: such a boundary
  must exclude cancellation as well, as `RomAnalysisPipeline`'s
  `IsClassifiableFailure` already does, so a cancelled run never surfaces as a
  `Diagnostic`. `IsProcessFatal` itself stays a process-fatality predicate and
  is not widened into a cancellation check.

## 7. Relationship to adjacent concerns

This contract deliberately does not overlap with two adjacent, separately
tracked concerns:

```text
Diagnostic  = an actionable operation outcome/problem (this document)
Log         = execution history (Issue #16)
Trace       = detailed execution evidence (Issue #45)
```

- **Localization (Issue #21):** `Message` is a secondary human-readable
  string and is never the machine identity. `Code` plus `MessageKey` plus
  `Context` is the boundary #21's localization work resolves against; this PR
  does not implement `ja-JP`/`en-US` resources.
- **Evidence (Issue #132):** `DiagnosticEvidenceReference` only references
  evidence by kind + identifier; the evidence store/artifact format itself is
  not implemented here.
- **AI diagnosis (Issue #41):** the `Diagnostic` shape (`Code`/`Context`/
  `Evidence`/`Recovery`) is the intended input boundary for AI-assisted
  diagnosis, but no AI diagnosis logic exists in this change.

## 8. Existing result integration

`DiagnosticAdapter` (`DiagnosticAdapter.cs`) derives a `Diagnostic?` from an
already-produced, already-machine-readable subsystem outcome — it does not
change how those outcomes are produced:

- `DiagnosticAdapter.From(RomAnalysisOutcome)` — `null` on `Pass`; an `Info`
  diagnostic on `Skip`; an `Error` diagnostic mapped from the failed stage and
  failure kind otherwise.
- `DiagnosticAdapter.From(TitleExecutionResult)` — `null` on a clean end
  (`Completed`, `Returned`, or a `RuntimeHandoff` without a diagnostic code);
  otherwise a diagnostic classified from the raw diagnostic code (when the
  engine already reports a stable string code) or the recompiler IR
  termination reason.

`RomAnalysisOutcome.Diagnostic` and `TitleExecutionResult.Diagnostic` are
additive, optional (`init`-only, default `null`) properties: existing
construction, equality, and consumer code are unchanged unless a caller
opts in by assigning the adapter's result. Wiring the adapter into every
production call site is out of scope for #49 — that is a per-subsystem
follow-up once a concrete consumer (GUI, CLI, AI diagnosis) needs it.

## 9. Non-goals for this document

Tracked separately, not defined here: full logging/trace infrastructure
(#16, #45), a crash/report artifact uploader, telemetry, an AI diagnosis
implementation (#41), a GUI diagnostics screen (#40), a full CLI
presentation, complete `ja-JP`/`en-US` localization resources (#21), and a
retry scheduler.
