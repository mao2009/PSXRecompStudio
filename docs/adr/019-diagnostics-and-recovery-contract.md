# ADR-019: Diagnostics & Recovery contract

- Status: Accepted
- Date: 2026-09-17
- Issue: #49

## Context

Analysis, Recompiler, Build, and Runtime each already produce
machine-readable failures in their own shape (`RomAnalysisOutcome`'s
`FailedStage`/`FailureKind`, `TitleExecutionResult`'s `DiagnosticCode`,
`RecompilerIrTerminationReason`, ...). Nothing ties "what happened" to "what
subsystem", "how severe", "what evidence", and "what to do next" in one
shared shape usable identically by the GUI, the CLI, JSON output, and a
future AI-assisted diagnosis consumer (#41). Without a shared contract, each
future consumer would either re-derive that mapping per subsystem or the
subsystems would grow parallel, incompatible "diagnostic" concepts over time.

This decision is project-wide and long-lived by construction: `Diagnostic` is
meant to be produced by every subsystem `DiagnosticCategory` names, and
consumed by every future presentation surface #49 lists (GUI, CLI, JSON,
AI diagnosis). It therefore needs an ADR rather than living as an
undocumented type addition.

## Decision

Introduce `PSXRecomp.Core.Diagnostics` (Domain layer) as the shared,
cross-subsystem diagnostics contract. Full model description:
[`docs/architecture/diagnostics.md`](../architecture/diagnostics.md). Binding
decisions this ADR records:

1. **`Code` (a validated `SCREAMING_SNAKE` string), not a closed enum, is the
   machine identity.** A code does not need to be registered
   (`DiagnosticCodes`) to be valid; an unknown-but-well-formed code from a
   newer contract version stays first-class. This lets existing ad-hoc string
   codes (`BIOS_HLE_UNSUPPORTED_CALL`, `OUTER_BUDGET_EXHAUSTED`, ...) elevate
   directly instead of forcing a big-bang enum migration.
2. **`Message` is never the machine identity.** `Code` (+ `MessageKey` +
   `Context`) is the boundary Issue #21's localization work resolves against;
   `Message` is a secondary, human-readable, non-authoritative string.
3. **Retry semantics is a closed four-value set
   (`DiagnosticRetrySemantics`), not a `bool Recoverable`.** `NotRetryable`,
   `RetrySameRequest` (the only case eligible for automatic retry),
   `RetryAfterUserChange`, and `RetryAfterExternalChange` are distinguished
   because a GUI/CLI/automation consumer needs to know *which* of those four
   applies, not just whether "recovery" exists in the abstract.
4. **Evidence is referenced, never embedded.**
   `DiagnosticEvidenceReference` carries a kind + stable identifier; the
   evidence store itself (Issue #132) is out of scope here.
5. **The exception boundary is explicit and fixed.**
   `DiagnosticExceptionPolicy.IsProcessFatal` names
   `OutOfMemoryException`/`StackOverflowException`/`AccessViolationException`
   as never convertible to a `Diagnostic`; every other exception type is a
   normal candidate for classification into a `Diagnostic` at a catch
   boundary, but a `Diagnostic` never represents a process-fatal condition.
6. **Integration into existing result types is additive and optional.**
   `RomAnalysisOutcome.Diagnostic` and `TitleExecutionResult.Diagnostic` are
   `init`-only, default-`null` properties populated only by
   `DiagnosticAdapter`, an explicit adapter a caller opts into. Existing
   construction, equality, and consumer code for both types is unchanged.
7. **No timestamp, host name, username, or absolute path enters the model.**
   `Diagnostic` and its JSON encoding (`DiagnosticJson`) stay deterministic:
   the same problem always serializes to the same document
   (`DiagnosticComparer` gives multi-diagnostic sets a stable order on top of
   that).

## Consequences

- Any subsystem that wants a diagnostics story reuses this contract instead
  of inventing a parallel one; a new `DiagnosticCategory`/`DiagnosticStage`
  value requires an existing or accepted architectural role rather than being
  added speculatively.
- `DiagnosticCode` shape validation (not a closed enum) means typo-shaped
  strings fail fast (`ArgumentException`/`JsonException`), while a
  forward-compatible unknown code from a newer binary does not hard-fail an
  older consumer.
- Because integration is additive/optional, this ADR does not by itself wire
  `DiagnosticAdapter` into every production call site; a concrete consumer
  (GUI diagnostics screen #40, CLI, AI diagnosis #41) does that wiring when it
  exists, against this same contract.
- Full logging/trace infrastructure (#16, #45), an evidence store (#132),
  localization resources (#21), a crash/report artifact uploader, and a retry
  scheduler remain separate, future work; this ADR only fixes the shape they
  will plug into.

## Alternatives considered

### A closed `enum DiagnosticCode`

Rejected. The codebase already has ad-hoc stable string codes in production
(`BIOS_HLE_UNSUPPORTED_CALL`, `OUTER_BUDGET_EXHAUSTED`); a closed enum would
force re-encoding all of them (and every future one) into this assembly's
enum, breaking forward compatibility with a newer contract version's codes
and coupling every subsystem's release cadence to this one's.

### A single `bool Recoverable` flag

Rejected. It cannot express the difference between "retry automatically",
"retry once the user changes something", and "retry once external state
changes" — distinctions the GUI/CLI/automation surfaces this contract feeds
actually need to act correctly (e.g. only `RetrySameRequest` is safe to retry
without a human in the loop).

### Rewriting `RomAnalysisOutcome` / `TitleExecutionResult` to carry a
### required `Diagnostic` instead of their existing fields

Rejected for #49's scope. It would be a compatibility-breaking change to two
already-consumed result types for no behavioral gain; the additive optional
property plus adapter achieves the same diagnosability without touching
existing semantics.
