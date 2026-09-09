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

Issue #279 also forbids guessing BIOS function identity. Function numbers
registered under this ADR are therefore taken from published PlayStation kernel
documentation recorded in [docs/REFERENCES.md](../REFERENCES.md); the behavior
is implemented independently from that documented description, and no BIOS ROM
image is obtained or distributed.

## Decision

Guest BIOS calls are represented by BiosCallIdentity, which carries the A0/B0/C0
family, function number, optional guest PC, ABI argument words, and optional
name. Execution paths invoke IBiosRuntime and consume BiosServiceResult.
The result is explicitly supported or unsupported and may carry a stable
BiosDiagnostic; an unregistered call must produce
BIOS_HLE_UNSUPPORTED_CALL.

HLE implementations are registered by family and function number. They must not
select behavior from title identity, guest address hacks, generated C, or
duplicated CPU semantics. The real BIOS image remains neither distributed nor a
normal-runtime prerequisite.

Registered services are deterministic and side-effect free until a Runtime
output sink and guest-memory access exist. A service therefore models the
documented ABI (accepted argument shape and returned register value) and nothing
else; it never performs host I/O or reads guest memory to produce its result.
A service invoked with an argument shape its ABI does not accept returns
BIOS_HLE_INVALID_ARGUMENTS through the shared BiosServiceResult factory, so
argument rejection is never re-implemented per service.

The registry currently holds the TTY-output services documented as
A(3Ch) std_out_putchar (returns the low byte of the character argument) and
A(3Eh) std_out_puts (returns its incoming string-pointer argument). The B0-table
aliases of the same functions (B(3Dh), B(3Fh)) are deliberately not registered
yet: no evidence selects them, and an unregistered alias fails loudly through
BIOS_HLE_UNSUPPORTED_CALL rather than diverging silently.

## Consequences

- Recompiler, interpreter, differential tests, and future Studio diagnostics can
  share one service result and diagnostic model.
- Unsupported BIOS dependencies stop at an inspectable boundary instead of
  silently succeeding.
- The current slice is not a full BIOS implementation: stateful services,
  kernel RAM, boot sequence, and hardware services remain follow-up work.
- Real-ROM evidence can later select the next service without changing the
  identity or dispatch contract.
- Because the TTY services emit no host output, recompiled code that depends on
  observable console text is not yet satisfied by them; the Runtime output sink
  and guest-memory access remain open work under Issue #279.

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
