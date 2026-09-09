# ADR-014: BIOS HLE Calls Cross a Shared Runtime Contract

- **Status**: Accepted (amended 2026-09-09 — see below)
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

`Supported` may be returned only when a service's guest-observable semantics are
actually satisfied — not merely when its ABI's return register is echoed back.
A service whose documented behavior requires reading guest memory, producing
host-visible output, or mutating state must implement that behavior before it
is registered. A service that would only reproduce its return-value contract
while skipping the effect a caller actually depends on must not be marked
Supported; it stays absent from the registry (falling through to
BIOS_HLE_UNSUPPORTED_CALL) instead. Marking an unimplemented side effect
Supported would let a differential/compatibility test report a false green and
would understate the Runtime's real compatibility gap. A service invoked with
an argument shape its ABI does not accept returns BIOS_HLE_INVALID_ARGUMENTS
through the shared BiosServiceResult factory, so argument rejection is never
re-implemented per service.

The registry currently holds one service: A(3Ch)/B(3Dh) std_out_putchar. Its
full documented behavior is writing the character to the TTY *and* returning
it; the current implementation models only the register-visible return-value
contract (the low byte of the argument) and does **not** yet emit TTY output.
This is accepted as a **Phase-1-scoped** `Supported` — not a claim that
putchar is fully implemented — because putchar's argument is a plain scalar:
no guest-memory access is skipped to compute the return value, so nothing
about the call's CPU/register-observable outcome can silently diverge from
real hardware. The missing TTY side effect is an explicitly tracked
limitation (Issue #279: "putchar TTY side effect implementation"), not a
hidden correctness gap.

This differs from A(3Eh)/B(3Fh) std_out_puts, whose family/function/ABI has
been verified against the same documentation
([docs/REFERENCES.md](../REFERENCES.md)) for future use but which is
deliberately **not** registered at all. Its documented behavior is reading a
NUL-terminated string from guest memory and writing it to the TTY; unlike
putchar's scalar argument, skipping the guest-memory read there would let an
invalid or unmapped pointer silently return success where real hardware would
fault — hiding a genuine correctness gap, not merely omitting a host-visible
side channel. That distinction, not just "no output sink yet," is why `puts`
stays unregistered while putchar's narrower contract is accepted as
`Supported`. The B0-table aliases of both functions (B(3Dh), B(3Fh)) are
likewise not registered: no evidence selects them, and an unregistered call
fails loudly through BIOS_HLE_UNSUPPORTED_CALL rather than diverging silently.

**On the meaning of `Supported` (open item):** as used in this ADR, `Supported`
currently means "the documented ABI/return-register contract is modeled, and
no guest-memory access needed to compute that contract is skipped" — it does
**not** mean "every documented effect, including host-visible output, is
implemented." This is a deliberate but narrow Phase-1 reading, and it needs to
be revisited once a Runtime output sink exists: at that point every
currently-registered service (today, only putchar) must be re-audited against
the stricter reading (full documented behavior, including host-visible side
effects) before Issue #279 can consider TTY-class services complete. Recorded
here explicitly so `Supported` does not silently drift into meaning "fully
compatible with real hardware."

## Consequences

- Recompiler, interpreter, differential tests, and future Studio diagnostics can
  share one service result and diagnostic model.
- Unsupported BIOS dependencies stop at an inspectable boundary instead of
  silently succeeding.
- The current slice is not a full BIOS implementation: stateful services,
  kernel RAM, boot sequence, and hardware services remain follow-up work.
- Real-ROM evidence can later select the next service without changing the
  identity or dispatch contract.
- `puts`'s identity is pre-verified and cited, so implementing it later is a
  Runtime-capability change (guest-memory read, output sink), not a fresh
  identity-research task.
- Because no service emits host output yet — including the registered
  putchar, whose TTY side effect is explicitly deferred — recompiled code
  that depends on observable console text is not satisfied by the current
  registry; the Runtime output sink and guest-memory access remain open work
  under Issue #279.
- `Supported`'s current meaning (ABI/return-register contract modeled, no
  guest-memory access skipped) is narrower than "fully compatible with real
  hardware" and must be re-audited once TTY output exists (see above); this is
  tracked as an explicit open item rather than left implicit.

## Alternatives Considered

- **Embed BIOS behavior in generated C or Recompiler code** — rejected because
  it would duplicate Runtime semantics and make the boundary unavailable to the
  interpreter.
- **Return a dummy success for unknown calls** — rejected because it hides
  compatibility gaps and cannot support deterministic differential diagnostics.
- **Implement a complete BIOS routine table first** — rejected because Phase 1
  needs a generic contract before evidence-driven service expansion.
- **Register `puts` as `Supported` with only its return value modeled** — this
  ADR originally did exactly this; rejected on review. Echoing the string
  pointer through R2 without reading guest memory or producing output
  satisfies the ABI's return convention but none of `puts`'s actual observable
  behavior, which would let a differential test pass without exercising
  anything the service is meant to do. Withdrawn in favor of leaving `puts`
  unregistered until the underlying Runtime capability exists.
- **Add a `Partial`/`UnsupportedState` status for an ABI-correct but
  effect-incomplete service** — considered, since that describes exactly the
  withdrawn `puts` registration, but rejected for now: no currently registered
  service needs it, and simply not registering an incomplete service resolves
  the concern without growing the status vocabulary ahead of a concrete need.
  The `BIOS_HLE_UNSUPPORTED_STATE` / `BIOS_HLE_SEMANTIC_MISMATCH` diagnostics
  named in Issue #279 remain available for a future case where a *registered*
  service reaches a state its HLE implementation cannot represent — a
  different situation from a service that was never registered.

## Related ADRs

- [ADR-013](013-real-rom-candidate-selection.md) — preserves the shared
  Recompiler contract when selecting real-ROM candidates.
