# ADR-014: BIOS HLE Calls Cross a Shared Runtime Contract

- **Status**: Accepted
- **Date**: 2026-09-08
- **Issue**: #279

## Context

Issue #279 requires the first BIOS HLE vertical slice to establish a
machine-readable call identity, a common Runtime boundary, and explicit
unsupported-call diagnostics after #225/#302. The existing Runtime interfaces
describe hardware and a future BIOS image, but do not provide a service-call
contract usable by both reference and recompiled execution.

## Decision

Guest BIOS calls are represented by BiosCallIdentity, which carries the A0/B0/C0
family, function number, optional guest PC, ABI argument words, and optional
name. Execution paths invoke IBiosRuntime and consume BiosServiceResult.
The result is explicitly supported or unsupported and may carry a stable
BiosDiagnostic; an unregistered call must produce
BIOS_HLE_UNSUPPORTED_CALL.

HLE implementations are registered by family and function number. They must not
select behavior from title identity, guest address hacks, generated C, or
duplicated CPU semantics. The Phase 1 registry implements only the deterministic
A0:3C putchar contract. The real BIOS image remains neither distributed nor a
normal-runtime prerequisite.

## Consequences

- Recompiler, interpreter, differential tests, and future Studio diagnostics can
  share one service result and diagnostic model.
- Unsupported BIOS dependencies stop at an inspectable boundary instead of
  silently succeeding.
- The current slice is not a full BIOS implementation: stateful services,
  kernel RAM, boot sequence, and hardware services remain follow-up work.
- Real-ROM evidence can later select the next service without changing the
  identity or dispatch contract.

## Alternatives Considered

- **Embed BIOS behavior in generated C or Recompiler code** — rejected because
  it would duplicate Runtime semantics and make the boundary unavailable to the
  interpreter.
- **Return a dummy success for unknown calls** — rejected because it hides
  compatibility gaps and cannot support deterministic differential diagnostics.
- **Implement a complete BIOS routine table first** — rejected because Phase 1
  needs a generic contract before evidence-driven service expansion.

## Related ADRs

- [ADR-013](013-real-rom-candidate-selection.md) — preserves the shared
  Recompiler contract when selecting real-ROM candidates.
