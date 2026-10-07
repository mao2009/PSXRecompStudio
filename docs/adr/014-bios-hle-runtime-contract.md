# ADR-014: BIOS HLE Calls Cross a Shared Runtime Contract

- **Status**: Accepted (amended 2026-09-09, 2026-09-10, 2026-09-11 (x5), 2026-09-15 (x3), 2026-09-17 — see below)
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

## Amendment (2026-09-09): Runtime guest-memory read boundary

Track A of Issue #279 adds a generic Runtime guest-memory read boundary —
`IGuestMemoryReader` with a default `GuestMemoryReader` and a bounded
`GuestMemoryStringReader` for NUL-terminated guest strings — so a future
service (notably A0:3E puts) can read guest memory safely. This amendment
records only that boundary and its contract; it does not register any service.

- (a) The boundary exists so a service can distinguish a stored zero byte from
  an invalid or unmapped address: reads are Try-style and never report silent
  success for an address that cannot be read, so a caller cannot confuse a
  syntactically successful zero byte with a real stored zero.
- (b) The reader delegates virtual-to-physical translation to a shared
  `Ps1AddressTranslation` helper (KUSEG/KSEG0/KSEG1, mirroring native
  `PSXCpu::TranslateAddress`) and reads physical bytes from the injected memory
  path via a delegate. The reader is bounded to RAM only: BIOS ROM, scratchpad,
  and hardware registers are not accessible through this reader. It is a bounded
  read boundary, not a second memory-semantics implementation: no RAM window,
  mirroring, or caching is built here.
- (c) Reads are always bounded: the string helper is capped at `maxLength`
  bytes, so no caller can trigger an unbounded scan.
- (d) A0:3E puts remains unregistered until BOTH this guest-memory read
  boundary and a Runtime output sink exist. The output sink is a separate
  concurrent boundary and is deliberately not part of this amendment or Track A.

## Amendment (2026-09-10): Runtime output sink boundary

Track B of Issue #279 adds the generic Runtime output boundary this ADR has
referred to since the guest-memory amendment above: `IRuntimeOutputSink`, with
a `CapturedOutputSink` test double. It was implemented directly against this
ADR's existing description (PR #349, merged 2026-09-09) without an ADR update
at the time; this amendment is that update, recording the boundary and its
contract now that a second dependent (this document) needs it stated
explicitly.

- (a) The boundary is deliberately separate from the guest-memory read
  boundary (previous amendment): reading guest memory and emitting host
  output are distinct responsibilities with distinct failure modes (an
  invalid address vs. nothing to observe the write). A0:3E puts needs both —
  neither boundary substitutes for the other.
- (b) `IRuntimeOutputSink` is a single-member, byte-level contract
  (`WriteByte(byte)`): the receiver owns buffering and encoding, and no
  Unicode/string decoding policy is fixed at this boundary. This is
  intentional: PS1 BIOS TTY output (putchar, and later puts) is a raw guest
  byte stream, and deciding its text encoding is a host/presentation concern,
  not a Runtime/Domain one. A future host adapter (GUI console, CLI stdout,
  headless capture) decodes or forwards bytes as it sees fit; the Domain layer
  never assumes an encoding.
- (c) The Domain layer (`PSXRecomp.Core`) does not depend on `System.Console`
  or any other I/O primitive to satisfy this boundary — enforced by the
  architecture analyzer (`src/architecture.contract.json`) rejecting
  `System.Console` in the Domain layer. `IRuntimeOutputSink` is the only
  output-side effect a BIOS HLE service may observe.
- (d) No dependency-injection point is added to `BiosHleRuntime` or
  `IBiosRuntime` by this amendment. Consistent with the guest-memory
  boundary's precedent (also unwired into any runtime host at this stage),
  wiring a sink parameter through to a registry with no service that writes
  to it would be speculative: the injection point belongs to whichever change
  actually completes putchar's TTY side effect or registers puts, where the
  concrete call site decides the shape (e.g. constructor injection into
  `BiosHleRuntime`, or a per-call parameter on `IBiosRuntime.Invoke`). The
  `CapturedOutputSink` test double already demonstrates the boundary is
  trivially substitutable once a consumer exists.
- (e) Introducing this sink does **not** by itself change any service's
  registration status. Putchar's TTY output and puts's write-to-TTY effect
  remain unimplemented; both are still tracked as separate follow-up work
  under Issue #279 (see `docs/runtime/bios-hle-evidence.md`). Re-auditing
  `Supported` against the stricter reading (noted as an open item in the base
  decision above) happens when a service is actually wired to this sink, not
  when the sink boundary alone lands.

## Amendment (2026-09-10): A0:3C putchar wired to the output sink

A0:3C putchar's documented TTY side effect is now implemented: `BiosHleRuntime`
takes an `IRuntimeOutputSink` by constructor injection and `InvokePutChar`
writes the low byte of its character argument to that sink before returning it.
This closes the "putchar TTY side effect implementation" item Issue #279
tracked, and supersedes the Phase-1-limited framing the base Decision section
and the preceding amendment's item (e) recorded for putchar.

- (a) **`Supported` now holds for putchar under the stricter reading.** The
  open item in the base Decision — that `Supported` meant only "the ABI/return
  register contract is modeled, and no guest-memory access needed to compute it
  is skipped" — is resolved for the sole registered service: putchar satisfies
  its **full** documented behavior (write the character to the TTY *and* return
  it). The re-audit that item required is therefore done for A0:3C. The stricter
  reading is what a newly registered service must meet from here on; the
  narrower Phase-1 reading is retired, not carried forward.
- (b) **The sink is a required constructor dependency, not an option.**
  `new BiosHleRuntime(outputSink)` throws `ArgumentNullException` on a null
  sink. A "no sink configured" mode would be a silent no-op that reintroduces
  exactly the false-green failure this ADR rejects — a host could run the
  registry while putchar's documented effect is quietly discarded, and a
  differential test would report success. Construction fails loudly instead. No
  parameterless overload exists.
- (c) **This settles the injection-point question left open** by the output-sink
  amendment's item (d): the shape is constructor injection into
  `BiosHleRuntime`, not a per-call parameter on `IBiosRuntime.Invoke`. The
  dispatch contract (`IBiosRuntime`, `BiosCallIdentity`, `BiosServiceResult`)
  is unchanged, so recompiled code, the interpreter, and diagnostics still see
  one identical boundary; only the registry's construction gained a dependency.
- (d) **Argument rejection stays side-effect-free.** An argument count ≠ 1 is
  still rejected with `BIOS_HLE_INVALID_ARGUMENTS` through the shared factory,
  and nothing is written to the sink on that path — a rejected call must not be
  observable as output. The return-value contract (`arg & 0xFF`) is unchanged.
- (e) **The byte is written raw.** Per the output-sink amendment's item (b), no
  encoding or Unicode conversion happens in the Domain layer; the low 8 bits of
  the argument reach the sink verbatim and the receiver decides how to interpret
  them. No `System.Console` or other I/O primitive is used, as the architecture
  analyzer enforces.
- (f) **No other service's status changes.** The registry still holds exactly
  `(A0, 0x3C)`. A0:3E puts remains unregistered, and the B0 aliases (B0:3D,
  B0:3F) remain unselected by evidence; Issue #279 stays open for those.

## Amendment (2026-09-10): A0:3E puts registered

Issue #279's puts track completes here. `PutsService` implements the service
and `BiosHleRuntime` registers it under `(A0, 0x3E)`, dispatching to it. This
amendment records the registration and what it settles; the base Decision and
the earlier amendments are unchanged.

- (a) **The "not until both effects are exercised" guarantee is now met, not
  waived.** The guest-memory amendment's item (d) and the evidence inventory
  both required that puts stay unregistered until it *actually* reads guest
  memory and writes to the sink. Both now happen: the registered service reads
  the NUL-terminated string through `IGuestMemoryReader` and writes every byte
  to `IRuntimeOutputSink`. The condition was satisfied before registration, not
  relaxed to permit it — which is the distinction the original rejection of a
  return-value-only puts turned on.
- (b) **`BiosHleRuntime` takes a second required dependency.**
  `new BiosHleRuntime(outputSink, guestMemoryReader)` throws
  `ArgumentNullException` on either null, following the sink's precedent from
  the previous amendment for the same reason: a missing reader would leave a
  registered service unable to perform the access its documented behavior
  depends on. Constructor injection remains the injection point settled by that
  amendment's item (c); `IBiosRuntime.Invoke`'s signature is untouched, so every
  execution path still sees one identical dispatch contract.
- (c) **Output is atomic with respect to the sink.** The string is collected
  into a bounded buffer and nothing is written until the whole string is proven
  readable, so a failure emits zero bytes. Partial TTY output would be a
  narrower form of the same false green this ADR rejects — a caller observing
  half a line while the call reports failure — so the all-or-nothing shape is
  part of the contract, not an implementation detail.
- (d) **Unreadable pointers and unterminated strings report
  `BIOS_HLE_UNSUPPORTED_STATE`.** This is the diagnostic the base Decision's
  Alternatives section reserved for "a *registered* service reaching a state its
  HLE implementation cannot represent", used here for exactly that case. It is
  a `Status = Unsupported` result carrying a distinct code, mirroring
  `BIOS_HLE_INVALID_ARGUMENTS`: the two-value `BiosServiceStatus` is unchanged,
  so the rejection of a third status stands. The scan is bounded (4096 bytes),
  honouring the guest-memory amendment's item (c).
- (e) **The stricter reading of `Supported` now has two data points.** Both
  registered services satisfy their full documented behavior including
  host-visible output, and puts additionally exercises the guest-memory access
  its return value depends on. The re-audit the base Decision's open item
  demanded is complete for the whole registry, not just for putchar.
- (f) **No other service's status changes.** The registry holds `(A0, 0x3C)` and
  `(A0, 0x3E)`. getchar (A0:3B) and gets (A0:3D) stay unregistered pending an
  input-sink design, and the B0 aliases (B0:3D, B0:3F) stay unselected by
  evidence. Issue #279 remains open for those. (Superseded for `B0:3F` by the
  2026-09-10 real-ROM evidence amendment below.)

## Amendment (2026-09-10): B0:3F selected by real-ROM evidence

Analysis can now recognize A0/B0/C0 call sites in a real executable
(`BiosCallRecognizer`, Issue #11), and the resulting evidence changes one fact
this ADR asserted in its base Decision and in both registration amendments.

- (a) **"The B0 aliases stay unselected by evidence" no longer holds for
  `B0:3F`.** A real title calls it: guest PC `0x800D0FF8`, executable serial
  `SLPM_869.24`, executable SHA-256
  `831a6cceb94c88c9736f6df88a7fd9e08ff1261ca781b40b7e6ff449cf0fd24e`. The
  evidence and its provenance are recorded in
  [`docs/runtime/bios-hle-evidence.md`](../runtime/bios-hle-evidence.md) §3.4.
  `B0:3D` remains unselected: no call site was observed for it.
- (b) **This amendment selects a service; it does not register one.** The bar the
  base Decision sets is unchanged — a service is registered only when it
  satisfies its full documented behavior, host-visible effects included. `B0:3F`
  now clears the *evidence* precondition, and it already has a verified identity
  and both required Runtime capabilities, which makes registering it an
  implementation task rather than a research or capability one. The registry is
  unchanged by this amendment and still holds `(A0, 0x3C)` and `(A0, 0x3E)`.
- (c) **Analysis evidence is not filtered by registration state.** The recognizer
  records what a ROM requests; the registry records what the Runtime provides.
  Filtering the former by the latter would make a title's BIOS surface shrink
  and grow with implementation progress, destroying the evidence loop Issue #279
  asks for. The two share only `BiosCallFamily` and the verified identity table
  `BiosCallNames`, never the registry.
- (d) **Identity verification remains a precondition for registration.** The
  recognizer reports every function number it resolves, including the many this
  repository has not verified. Those are call-frequency observations, not service
  candidates: the no-guessing rule in the base Decision applies unchanged, and an
  unverified number is recorded without a name rather than with a guessed one.
- (e) **Recognition prefers an unresolved record to a guessed one.** Where the
  vector is certain but the function number is not statically resolvable, the
  site is recorded as unresolved rather than omitted or inferred. An
  understated BIOS surface and a fabricated identity are both failures; the
  latter is worse, because it would be acted on.
- (f) **No other service's status changes.** getchar (A0:3B) and gets (A0:3D)
  stay unregistered pending an input-sink design. Issue #279 remains open.

## Amendment (2026-09-11): B0:3F registered

The registration the previous amendment said was now "an implementation task
rather than a research or capability one" is done. `BiosHleRuntime` registers
`(BiosCallFamily.B0, 0x3F)` under a distinct constant,
`BiosHleRuntime.PutsAliasFunction`, dispatched to the same `InvokePuts` private
method — and therefore the same `PutsService` — that `(A0, 0x3E)` already used.
No second service class was written.

- (a) **One implementation, two identities, not two implementations.** The
  registry now holds `(A0, 0x3C)`, `(A0, 0x3E)`, and `(B0, 0x3F)`. The last two
  entries both bind to `InvokePuts`/`PutsService.Invoke`; the family/function
  identity is what the registry keys on, not what runs. This is the reuse this
  ADR's base Decision requires ("must not select behavior from title identity,
  guest address hacks, generated C, or duplicated CPU semantics") applied to an
  alias: sharing behavior across two identities is not the same failure mode as
  hard-coding behavior by title.
- (b) **Identity preservation was a real gap, not a formality.** `PutsService`'s
  three diagnostic messages previously hard-coded the literal string `"A0:3E"`.
  Left unfixed, a `B0:3F` failure would have reported itself as an `A0:3E`
  failure in the human-readable message — correct in the structured
  `BiosDiagnostic.Identity` field (which was always the actual call's identity,
  never hard-coded), but wrong in the text a diagnostic consumer reads. The
  messages now interpolate `identity.StableKey`, so a `B0:3F` failure reports
  `"B0:3F puts: ..."` and an `A0:3E` failure still reports `"A0:3E puts: ..."`.
  `BiosHleContractTests.PutsDiagnostics_Name_The_Identity_That_Was_Actually_Called`
  pins both directions.
- (c) **`B0:3D` remains unselected and unregistered.** No real-ROM call site has
  been observed for it (`docs/runtime/bios-hle-evidence.md` §3.4); this
  amendment changes nothing about it. Registering it later, if evidence ever
  selects it, is expected to be the same kind of reuse this amendment
  demonstrates for `B0:3F` — binding an existing identity to the existing
  `InvokePutChar`, not a new putchar implementation.
- (d) **No other service's status changes.** getchar (A0:3B) and gets (A0:3D)
  stay unregistered pending an input-sink design. Issue #279's B0:3F item is
  now closed; the Issue itself remains open for the rest of its scope.

## Amendment (2026-09-11): B0:56 GetC0Table / B0:57 GetB0Table evaluated, not registered

Both identities are verified (`BiosCallNames`) and are the two most frequently
observed real-ROM identities recorded to date
([docs/runtime/bios-hle-evidence.md](../runtime/bios-hle-evidence.md) §3.4:
`B0:56` in 3 of 5 executables, `B0:57` in 3 of 5 with up to 4 sites in one).
This amendment records why they are not registered, rather than leaving that
decision implicit.

- (a) **Their documented behavior names a real, patchable table, not a bare
  number.** [docs/REFERENCES.md](../REFERENCES.md): "retrieve the address of
  the jump list for the `C(NNh)` and `B(NNh)` functions respectively,
  allowing entries in those lists to be patched." The returned address is
  documented as pointing at guest-visible, patchable function-pointer table
  content — not an opaque handle whose only observable use is being echoed
  back through R2.
- (b) **No such table exists in this Runtime.** `BiosHleRuntime` dispatches
  purely through a host-side `(BiosCallFamily, byte)` lookup (the `services`
  dictionary in `BiosHleRuntime.cs`); no guest memory address anywhere in the
  Runtime or Domain layer is backed by, or connected to, the actual A0/B0/C0
  dispatch tables, and no BIOS ROM image is loaded (base Decision). Returning
  a constant address — even the historically correct real-hardware one —
  would satisfy only the return-register contract while the guest-visible
  content at that address stays unmodeled: a read returns whatever happens to
  already be in guest RAM (never a real function pointer), and a write (the
  documented "patch" use) has no effect on this Runtime's actual dispatch.
  This is the same effect-incomplete pattern the base Decision's Alternatives
  section already rejected once for `puts` (an ABI-correct, return-value-only
  registration that skips the guest-observable effect a caller depends on),
  applied here to a table pointer instead of a guest string read.
- (c) **Whether real call sites actually use the pointer cannot be answered
  with existing tooling.** `BiosCallRecognizer` resolves call-site *identity*
  (family/function number) only; this repository has no register-liveness or
  return-value-use dataflow pass, so whether the observed `B0:56`/`B0:57`
  sites dereference or patch through the returned address, or discard it, is
  unverified. Per this ADR's no-guessing rule, an unverified fact resolves to
  the safe side — it does not narrow the documented ABI down to "harmless
  because it's probably unused" without evidence, and the current absence of
  a locally observed dereference is not evidence that one never occurs.
- (d) **Verdict: evaluated and left unregistered.** `BiosHleRuntime`'s
  registry is unchanged by this amendment; it still holds exactly `(A0,
  0x3C)`, `(A0, 0x3E)`, and `(B0, 0x3F)`. Registering `B0:56`/`B0:57` needs a
  Runtime capability this repository does not yet have — a guest-visible,
  dispatch-connected representation of the B0/C0 jump tables — which is a new
  capability decision, not a two-service wiring task like `puts`'s `B0:3F`
  alias was. A follow-up Issue proposing that capability is recommended (see
  [docs/runtime/bios-hle-evidence.md](../runtime/bios-hle-evidence.md)).
- (e) **No other service's status changes.** getchar (A0:3B) and gets (A0:3D)
  stay unregistered pending an input-sink design; `B0:3D` stays unregistered
  pending evidence. Issue #279 remains open for all of these, plus the new
  kernel jump-table state item this amendment surfaces.

## Amendment (2026-09-11): Runtime guest-memory write boundary

The follow-up this ADR's previous amendment recommended is filed as two
Issues: #359 (this amendment's subject) and #360 (the jump-table state
abstraction itself, still blocked — see below). This amendment adds
`IGuestMemoryWriter`, with a default `GuestMemoryWriter`, as the write-side
mirror of the existing guest-memory read boundary (amendment
2026-09-09). It records only that boundary and its contract; it does not
register any service and does not by itself unblock `GetC0Table`/`GetB0Table`.

- (a) **The boundary exists so a service can distinguish a rejected write from
  a successful one**: writes are Try-style, mirroring the reader, and a
  rejected write never partially mutates guest memory.
- (b) **Translation and RAM bound are shared with the reader, not
  reimplemented.** `GuestMemoryWriter` calls `Ps1AddressTranslation.TryTranslate`
  directly — the same shared KUSEG/KSEG0/KSEG1 helper `GuestMemoryReader` uses,
  so the writer depends on the shared translation rule rather than on the
  reader class — and rejects any physical address outside
  `Ps1MemoryMap.RamSize`, identically to `GuestMemoryReader`. BIOS ROM,
  scratchpad, and hardware registers are not accessible through this writer,
  matching the reader's stated scope.
- (c) **This boundary is deliberately unwired into `BiosHleRuntime`.** No
  currently registered service needs it. This mirrors the precedent set by
  the guest-memory read boundary itself (unwired at landing, wired only once
  `puts` needed it) and by the output-sink amendment's item (d): wiring a
  write parameter through to a registry with no service that uses it would be
  speculative. The injection point is left to whichever future change
  actually needs it.
- (d) **This does not, by itself, unblock `GetC0Table`/`GetB0Table`.** The
  previous amendment's verdict — that registering either needs a
  guest-visible, dispatch-connected representation of the actual B0/C0
  tables, not merely the ability to write a byte somewhere — is unchanged.
  A write boundary is a necessary building block for that representation
  (and independently for `gets`, A0:3D, which needs guest-memory write for
  its input buffer), but the table's canonical address, entry format, and
  initial contents remain unconfirmed and are tracked in #360, not resolved
  here.
- (e) **No other service's status changes.** The registry is unchanged:
  `(A0, 0x3C)`, `(A0, 0x3E)`, `(B0, 0x3F)`. Issue #279 remains open.

## Amendment (2026-09-11): Guest-visible BIOS kernel jump-table (A0/B0/C0) state abstraction (#360)

Issue #360 asked for a guest-visible, dispatch-connected representation of the
A0/B0/C0 kernel jump tables. This amendment records the design decisions made
and the Runtime changes delivered; it resolves the open item the previous
amendment identified.

### Confirmed facts (primary source: psx-spx, pinned commit ecd6f794f459ab5f72feb88d46df8d23b3c413e0)

- (a) **A0 table base address `0x00000200`, size `0x300` bytes (192 entries × 4 bytes).**
  Explicitly stated in the BIOS memory map ("00000200h 300h A(nnh) Jump Table").
  This is a primary-source-confirmed real-hardware fact. Real software has no
  `GetA0Table` equivalent and is known to reference `0x200` directly, so this
  Runtime must match it exactly as a compatibility requirement. Recorded as
  `BiosJumpTables.A0TableAddress`.
- (b) **Entry width: 4 bytes (32-bit raw guest address) for all three tables.**
  Confirmed from primary source (A-table size/count arithmetic; B/C stride
  cross-checked via `[r2 + n*4]` access pattern in secondary sources).
- (c) **Dispatch trampoline: guest code loads the function number into R9 and
  branches to physical `0xA0`/`0xB0`/`0xC0`; a RAM-resident stub computes
  `table_base + R9*4`, loads the 32-bit word there, and jumps to it.** Confirmed
  from the same primary source.
- (d) **Patch semantics: writing a new 32-bit address into `table_base + n*4`
  redirects all subsequent dispatch through that slot on the very next call.**
  Confirmed (same primary source + secondary load-then-jump pattern cross-check).
- (e) **`GetC0Table` (B0:56) / `GetB0Table` (B0:57):** no arguments; return value
  is the table base address; documented purpose is to let the caller patch
  entries. Confirmed; already cited in `docs/REFERENCES.md:128-134`.

### B0/C0 table addresses: Runtime design choice, not a confirmed real-hardware fact

- (f) **`BiosJumpTables.B0TableAddress` (`0x00000874`) and
  `BiosJumpTables.C0TableAddress` (`0x00000674`) are NOT primary-source-confirmed
  real-hardware facts in this repository.** Only secondary sources (an emudev.org
  walkthrough and the Nocash BIOS-patches page's `[r2+n*4]` offsets) place the
  real BIOS's tables at these values. The pinned primary source does not state
  them explicitly.
- (g) **This Runtime is free to choose its own B0/C0 addresses.** This Runtime
  never loads a real BIOS ROM (base Decision non-goal). `GetB0Table`/`GetC0Table`
  exist precisely so correct software discovers these addresses dynamically, never
  by hardcoding them. Any non-colliding in-RAM address would have been equally
  valid for a BIOS-less HLE Runtime; correct software always calls the Get-functions
  rather than hardcoding B0/C0 bases, unlike A0 (which has no Get-function
  equivalent and for which some real software is known to hardcode `0x200`).
- (h) **The values `0x874`/`0x674` were chosen as this Runtime's own design
  decision** — purely for plausibility and least-surprise if real-BIOS-ROM support
  is ever added later — not as a claim about real hardware. Any future reader must
  not mistake these constants for primary-source-confirmed facts.

### Dispatch design: guest RAM is the single source of truth

- (i) **`BiosHleRuntime.Invoke` now consults guest-visible table state before the
  registry.** For each call, it reads the 4 bytes at
  `BiosJumpTables.EntryAddress(family, functionNumber)` through
  `IGuestMemoryReader`. If the little-endian `uint32` value is non-zero, the entry
  has been patched by guest code: `BiosServiceResult.PatchedTarget` is returned —
  carrying the raw patched guest address in `ReturnValue` — regardless of whether
  a host-side handler is also registered for that slot. Zero falls through to the
  registry exactly as before.
- (j) **No explicit table initialisation exists.** Guest RAM is zero-initialised by
  construction (`RecompilerGuestMemory`'s backing is `new byte[RamSize]`, C# zero-
  initialises; production memory paths are assumed to behave equivalently — this
  assumption is noted in code comments, because production wiring does not exist
  yet). "Unpatched" is therefore `0x00000000`, consistent with the secondary-source
  convention that unused real-hardware slots trap to `0`. This is the deterministic
  initial table state without any explicit init step; fresh instances over fresh RAM
  dispatch identically.
- (k) **A `PatchedTarget` result carries the raw target address and nothing more.**
  This Runtime does not execute or validate a patched target — jumping to arbitrary
  guest code requires an interpreter or recompiled-code dispatch trap that does not
  exist in this repository (see follow-up Issue #362, filed alongside this PR).
  Execution of patched targets is intentionally out of scope here.
- (l) **The behavioral superset guarantee holds.** Every existing registered service
  (`A0:3C`, `A0:3E`, `B0:3F`) and every "stays Unsupported" case is unaffected as
  long as nothing has written a non-zero value at that service's own table slot.
  Verified: existing tests write only at `0x100`, `0x200`–`0x201`, `0x210`, `0x300`
  — none overlaps `A0:3C`→`0x2F0`–`0x2F3`, `A0:3E`→`0x2F8`–`0x2FB`,
  `B0:3F`→`0x970`–`0x973`.

### `IGuestMemoryReader.TryRead` / `IGuestMemoryWriter.TryWrite` interface widenings

- (m) **`IGuestMemoryReader.TryRead(uint, Span<byte>)` is now declared on the
  interface.** The concrete `GuestMemoryReader` already implemented this method;
  only the interface declaration was missing. This allows callers typed as
  `IGuestMemoryReader` — including `BiosHleRuntime.Invoke`'s stackalloc-based
  4-byte read — to use it without casting.
- (n) **`IGuestMemoryWriter.TryWrite(uint, ReadOnlySpan<byte>)` is a new method**
  on both the interface and `GuestMemoryWriter`. It is all-or-nothing: every address
  is validated before any byte is written, mirroring `GuestMemoryReader.TryRead`.
  It is non-speculative: `BiosHleContractTests` exercises it end-to-end to patch
  jump-table slots, so the boundary widening has a concrete consumer.

### `B0:56 GetC0Table` and `B0:57 GetB0Table` are now registered

- (o) **Both services are registered in `BiosHleRuntime`.** They return
  `BiosJumpTables.C0TableAddress` and `BiosJumpTables.B0TableAddress` respectively
  and reject any call with arguments (`BIOS_HLE_INVALID_ARGUMENTS`).
- (p) **This satisfies the "effect-incomplete" bar** the previous amendment invoked
  to withhold registration. The bar was: returning a constant address would be
  effect-incomplete if the content at that address were not backed by guest-visible
  content connected to dispatch (analogous to puts's return-value-only rejection).
  Now reads and writes at the returned address affect actual dispatch outcomes
  through `BiosHleRuntime.Invoke` — the returned address is not an inert constant.
- (q) **Reconciliation with Issue #279's "OpenBIOS-preference / demonstrated
  technical justification" comments.** `B0:56`/`B0:57` are the two most frequently
  observed real-ROM identities to date
  (`docs/runtime/bios-hle-evidence.md` §3.4: 3 of 5 executables each, up to 4
  sites in one). This change does not reimplement any BIOS kernel function body;
  it only makes the existing three registered services' dispatch hardware-faithful
  and adds two functions whose entire documented behavior is "return a table
  address" — which is now genuinely modeled rather than a bare constant, because
  the table is now backed by guest-visible RAM connected to dispatch.

### Executing patched targets: intentionally out of scope

- (r) **No interpreter or recompiled-code dispatch trap for patched targets exists.**
  `BiosHleRuntime.Invoke` returns `BiosServiceResult.PatchedTarget` with the raw
  guest address, but this Runtime cannot actually jump to / execute that target.
  This is a separately-scoped gap: (a) a guest-jump-to-`0xA0`/`0xB0`/`0xC0`
  recognition/trap mechanism in the interpreter and/or recompiled-code path is
  confirmed absent from `src/PSXRecomp.Native/src/psx_cpu.cpp` and
  `RecompilerInterpreterExecutor.cs` as of this amendment, and (b) how a
  `PatchedTarget` result falls back to raw guest-code execution versus staying
  diagnosable is an open design question. Both are tracked in follow-up
  Issue #362 (to be filled in once filed alongside this PR). This is a pre-existing
  gap, not something this amendment introduces or resolves.

### Registry after this amendment

- (s) **The registry now holds five entries:** `(A0, 0x3C)`, `(A0, 0x3E)`,
  `(B0, 0x3F)`, `(B0, 0x56)`, `(B0, 0x57)`. Issue #279 remains open for getchar,
  gets, B0:3D, and the patched-target execution gap above.

### Addendum: A0 upper-bound enforced; B0/C0 bounds intentionally left open

- (t) **A0's table size is primary-source-confirmed** (psx-spx, pinned commit
  `ecd6f794f459ab5f72feb88d46df8d23b3c413e0`: 0x300 bytes = 192 entries,
  function numbers 0x00–0xBF). `BiosJumpTables.A0MaxFunctionNumber = 0xBF` was
  added and `BiosHleRuntime.Invoke` now skips the patch-check for A0 function
  numbers > 0xBF. Without this guard, computing `EntryAddress(A0, fn)` for
  fn >= 0xC0 would land in the "relocated kernel code" region of the BIOS
  memory map; a non-zero byte pattern there would be falsely reported as
  `PatchedTarget`.
- (u) **No equivalent bound is enforced for B0 or C0.** No primary source in
  this repository confirms an upper function-number limit for either table. The
  no-guessing rule in this ADR (Appendix A) prohibits inventing a bound from
  secondary sources. The B0/C0 address-collision note in `BiosJumpTables`
  (C0 entries at fn >= 0x80 arithmetically alias B0 entries at fn 0x00) is a
  documented consequence of the chosen base addresses, not a confirmed
  hardware behavior, and does not establish a usable upper bound. This
  limitation is deliberate and documented; it may be revisited only when a
  primary source is located.

## Amendment (2026-09-11): #360 pre-merge fixes — guest-visible existing entries, and correcting the C0/B0 range comment against primary source

PR #363 (implementing this amendment's own predecessor above) carried two
pre-merge blockers. Both are resolved here, against the same pinned primary
source (psx-spx, commit `ecd6f794f459ab5f72feb88d46df8d23b3c413e0`,
`docs/kernelbios.md`) already cited throughout this ADR. This amendment does
not reopen #362 (executing a patched target); the two remain distinct, per
this amendment's own item (h) below.

### Blocker 1: a registered service's own slot read as zero, not as a real existing entry

**The problem, restated precisely.** The previous amendment made guest RAM the
dispatch source of truth, but every slot — registered or not — was left at
RAM's zero default. `GetB0Table`/`GetC0Table` return an address whose content
a guest is documented to read before conditionally patching it (see the next
finding), yet that content was always zero for a slot this Runtime already
implements. A guest reading an "existing" A0:3C/A0:3E/B0:3F/B0:56/B0:57 entry,
saving it, and later restoring it could not actually round-trip: the "saved"
value was always 0, indistinguishable from "never patched" and from "not a
real function".

**Primary-source evidence found (`docs/kernelbios.md`, §"BIOS Patches" and the
GetB0Table/GetC0Table entry):**

- `B(56h) - GetC0Table()` / `B(57h) - GetB0Table()`: "Retrieves the address of
  the jump lists for B(NNh) and C(NNh) functions, allowing to patch entries in
  that lists (however, the BIOS does often jump directly to the function
  addresses, rather than indirectly via the list, so patching may have little
  effect in such cases)." This confirms patching is a real, documented use of
  the returned address, while also confirming that on *real* hardware not
  every function is actually dispatched through the table (the BIOS's own
  internal calls often bypass it) — a fact about real hardware's internal
  call graph, not about this Runtime, which (per the previous amendment's item
  (i)) always dispatches A0/B0/C0 through the table.
- The "BIOS Patches" section is explicit that reading an *existing* entry is
  the normal, common case, not a hypothetical one: "all known patches are
  invoked by a B(56h) or B(57h) function call. In the nocash PSX bios, these
  two functions are examining the following opcodes, if the opcodes are a
  known patch, then the BIOS reproduces the desired behaviour... If the
  opcodes are unknown, then the BIOS simply locks up." The worked example
  (`patch_missing_cop0r13_in_exception_handler`, used by real commercial
  titles including Ridge Racer and Metal Gear Solid) calls `B(56h) GetC0Table`
  and then reads `[r2 + 06h*4]` — table entry `C(06h)`, annotated in the same
  source as `;=00000C80h = exception_handler = C(06h)` — before comparing
  bytes at that address. This is read-existing-entry, not merely
  read-then-immediately-overwrite.
- This confirms the *pattern* (read an existing, real, non-zero entry before
  acting on it) as common documented practice. It does **not** hand this
  Runtime a real numeric value to reproduce for any of its five *own*
  registered slots (A0:3C, A0:3E, B0:3F, B0:56, B0:57) — the quoted example is
  a different slot (C0:06, the exception handler), which this Runtime does not
  implement, and no BIOS ROM is loaded here to source real values from
  (base Decision, unchanged). Inventing a "real-looking" address for a slot
  this Runtime does not model would itself be a no-guessing violation.

**Design candidates evaluated** (as the investigation required, before any
code change):

- **Candidate A — guest RAM as a fully-populated SSOT for every known slot.**
  Rejected: this Runtime has no real BIOS ROM and does not know the real
  target address for any function it has not implemented; populating every
  documented slot would mean inventing addresses for functions with no HLE
  behavior behind them — a direct no-guessing violation, and worse than the
  current zero (a dereference would appear to succeed against content that
  does nothing).
- **Candidate B — a synthetic HLE-trampoline address space, initialised only
  for slots this Runtime actually registers; patched slots hold the raw guest
  target; dispatch tells the two apart by exact value.** **Adopted.** It
  requires no real BIOS ROM, invents nothing for slots this Runtime does not
  implement (they stay at the pre-existing, still-honest zero), and gives a
  real, non-zero, deterministic value specifically for the five slots this
  Runtime already claims to implement — the only slots where "what should a
  guest read here" has an actual, defensible answer.
- **Candidate C — keep host registry state authoritative; project a virtual
  value only at guest-read time, without writing real RAM content.** Rejected:
  a raw guest `lw` instruction never goes through this Runtime's dispatch
  logic — only `Invoke` does — so any value that exists only in `Invoke`'s
  reasoning is invisible to ordinary guest memory reads. This would not
  actually satisfy "a guest can read an existing entry"; it would only make
  `Invoke` itself more permissive while leaving the guest-observable RAM
  content unchanged (still zero). Candidate C does not solve the problem it
  was proposed for.

**Decision: Candidate B, implemented as follows.**

- (a) `BiosJumpTables.HleSentinelTarget(family, functionNumber)` computes a
  per-slot constant: `0xFFFF0000 | (family << 8) | functionNumber`. The high
  half-word `0xFFFF0000` falls inside the KSEG2 window (any address above
  `0xBFFFFFFF`), which `Ps1AddressTranslation.TryTranslate` already rejects
  unconditionally — this is an existing, code-verified guarantee, not a new
  assumption, so the sentinel can never collide with a real RAM, BIOS, or
  relocated-kernel-code address, and it does not resemble a genuine PS1 code
  pointer (`0x00xxxxxx`/`0x80xxxxxx`/`0xA0xxxxxx`) on inspection — it is never
  mistakable for a claim about real hardware.
- (b) `BiosHleRuntime`'s constructor now also takes an `IGuestMemoryWriter`
  (a required dependency, `ArgumentNullException` on null — the same pattern
  the sink and reader already established) and, immediately after building the
  registry, writes each registered `(family, function)`'s sentinel into its
  own guest RAM slot via `TryWrite`. This is the actual production-behavior
  fix: the value a guest reads via an ordinary load instruction, with no
  Runtime involvement, is now real and non-zero for every slot this Runtime
  implements.
- (c) `Invoke`'s patch-check is widened: a non-zero entry is treated as
  `PatchedTarget` only when it is *not* that exact slot's own sentinel. A
  freshly-constructed registered slot (sentinel present), and a slot restored
  to its saved sentinel value after a patch, both fall through to the
  registry exactly as before. An unregistered slot is untouched by
  construction and stays at zero, matching the pre-existing (and
  primary-source-consistent, see Blocker 2) convention unchanged.
- (d) This gives the full guest-visible lifecycle: **read** an existing,
  real, non-zero entry (the sentinel) → **save** it → **patch** (write any
  other value; observed on the very next call as `PatchedTarget`, exactly as
  the prior amendment already guaranteed) → **restore** the saved value →
  dispatch reaches the original HLE handler again. `BiosHleContractTests`
  exercises this end to end
  (`RegisteredServiceSlot_Read_Save_Patch_Restore_RoundTrips_To_OriginalHandler`).
- (e) **Deterministic and snapshot-safe by construction, without new
  bookkeeping.** The sentinel is a pure function of `(family, function)`, and
  it is written into ordinary guest RAM at construction — a Runtime lifecycle
  event that happens identically every time. No new host-side shadow state
  exists: everything a save-state needs is already inside guest RAM, exactly
  like a real patched target's raw address already was.
- (f) **No dual source of truth.** Guest RAM remains the single dispatch
  source of truth introduced by the previous amendment; this change only
  makes that RAM's *initial* content for this Runtime's own services
  meaningful instead of leaving it at RAM's incidental zero default. The host
  registry is still consulted only as the *behavior* a recognised value
  dispatches to, never as a second address authority.
- (g) **Interpreter/recompiled parity is unaffected.** Both consume the same
  guest RAM and the same `BiosHleRuntime.Invoke`; nothing in this change is
  specific to either execution path.
- (h) **This does not touch #362.** Executing an arbitrary patched guest
  target still requires a dispatch trap this repository does not have; that
  gap, and its own tracking Issue, are unchanged. This amendment only makes
  the guest-visible *state* — what a read returns before any patch trap would
  ever run — honest; it does not make a patched target executable.
- (i) **Registry membership is unchanged.** Still exactly `(A0, 0x3C)`,
  `(A0, 0x3E)`, `(B0, 0x3F)`, `(B0, 0x56)`, `(B0, 0x57)`.

### Blocker 2: the C0/B0 range comment contradicted primary source

The previous amendment's item (u) and `BiosJumpTables`' comments described
the B0/C0 address relationship (`EntryAddress(C0, fn>=0x80) ==
EntryAddress(B0, fn-0x80)`) as an unconfirmed Runtime-only quirk, and claimed
no primary source gives B0/C0 range information. Both claims are wrong; the
pinned primary source documents the exact ranges:

- `C(1Eh..7Fh) N/A ;jump_to_00000000h` — real, present C0 slots that dispatch
  to guest address 0 (consistent with this Runtime's existing all-zero
  convention for a slot with no registered service).
- `C(80h.....) N/A ;mirrors to B(00h.....)` — **confirmed real-hardware
  fact**: C-function numbers 0x80 and up dispatch through the very same
  jump-list memory as B-function numbers 0x00 and up. This is exactly the
  relationship `BiosJumpTables`' 0x200 base-address separation reproduces
  arithmetically; it is not a coincidental Runtime artifact to excuse as
  "harmless", it is the documented behavior, faithfully reproduced given the
  (still secondary-source-cited, not primary-source-confirmed as exact
  values) base addresses this Runtime chose.
- `B(5Eh..FFh) N/A ;jump_to_00000000h` and `B(100h....) N/A ;garbage` — B0's
  full byte domain (0x00-0xFF) is documented (0x00-0x5D real, 0x5E-0xFF
  jump-to-0); the undocumented "garbage" region starts at 0x100, already
  outside `byte FunctionNumber`'s representable range, so this Runtime cannot
  reach it without a type change nobody is proposing.

**Correction applied:** `BiosJumpTables.cs`'s class and `C0TableAddress`
remarks are rewritten to state the above as confirmed primary-source facts
with direct quotes, replacing "C0's real documented range never reaches
0x80" and "no confirmed upper bound for B0 or C0 exists" (both false) with
the actual documented domains. `Invoke`'s inline comment is corrected the
same way. No behavior changes: no additional upper-bound guard is added for
B0 or C0, because — unlike the situation the previous wording implied — there
is no undocumented range left inside the byte domain to guess a bound for;
A0 keeps its own primary-source-confirmed 192-entry bound
(`A0MaxFunctionNumber`) unchanged, for the different reason that A0's table
is smaller than its full byte domain.

**Regression tests added** (`BiosHleContractTests`):
`EntryAddress_C0HighFunctionNumbers_Alias_DocumentedB0Entries` (pure
arithmetic, C0:0x80↔B0:0x00, C0:0xFF↔B0:0x7F, C0:0xC0↔B0:0x40),
`PatchWrittenViaC0HighAlias_IsObservedThroughB0Dispatch` and
`PatchWrittenViaB0Slot_IsObservedThroughC0HighAliasDispatch` (the alias is
live through dispatch, both directions, not just address arithmetic),
`C0_DocumentedJumpToZeroRange_RemainsUnsupported_NotContradicted` (0x1E and
0x7F), and the existing A0 upper-bound regression
(`A0_FunctionNumber_AboveTableBound_SkipsPatchCheck_And_IsUnsupported`) is
unchanged and still passes.

### Registry and constructor after this amendment

- (j) The registry is still exactly `(A0, 0x3C)`, `(A0, 0x3E)`, `(B0, 0x3F)`,
  `(B0, 0x56)`, `(B0, 0x57)`.
- (k) `BiosHleRuntime`'s constructor now requires three dependencies:
  `IRuntimeOutputSink`, `IGuestMemoryReader`, `IGuestMemoryWriter` — all
  `ArgumentNullException`-guarded, none with a parameterless fallback,
  consistent with every prior boundary this ADR has added.

## Amendment (2026-09-11): PR #363 CodeRabbit follow-up — zero-only sentinel seeding, and C0/B0 canonical physical-slot identity

Two CodeRabbit Major findings against the previous amendment's implementation,
both confirmed valid against current code and primary source.

### Blocker A: sentinel seeding overwrote pre-existing guest state

The previous amendment's constructor seeding ((b) above) wrote every
registered slot's sentinel unconditionally. A `BiosHleRuntime` constructed
over guest memory that already holds a guest patch, or a save-state's
restored content, silently replaced that value with the sentinel — exactly
the "dual source of truth" and "silent state loss" failure modes this ADR's
base Decision and Blocker 1 fix were meant to rule out.

**Fix:** seeding is now zero-only. For each registered slot, the constructor
reads the existing 4-byte entry through `IGuestMemoryReader` first:

- Existing value is exactly zero → seed the sentinel (unchanged production
  behavior for a freshly-allocated guest memory).
- Existing value is non-zero → leave it untouched. A pre-existing guest patch
  or a restored save-state value is never overwritten.
- The read itself fails (an unmapped or rejected address) → treated the same
  as non-zero: do not seed. This is a deliberate, deterministic failure
  policy — a slot whose actual current content cannot be established is left
  exactly as it already stood, never speculatively or partially mutated. It
  is not a construction error: the same best-effort posture the previous
  amendment already established for a rejected *write* now applies
  symmetrically to a failed *read*.

No new host-side state is introduced; the check is a plain read-then-write
sequence over the same guest RAM this Runtime already treats as the single
dispatch source of truth. Reconstructing a `BiosHleRuntime` over memory a
save-state has already restored — patched or not — now reproduces the exact
same dispatch outcome the original Runtime instance would have given.

### Blocker B: C0:80+ mirror shared a physical slot but not a sentinel/registry identity

psx-spx (pinned commit ecd6f794f459ab5f72feb88d46df8d23b3c413e0,
`docs/kernelbios.md`) documents `C(80h.....) N/A ;mirrors to B(00h.....)` as
real-hardware behavior — already cited by this ADR's prior amendment for the
*address arithmetic* (`EntryAddress(C0, fn) == EntryAddress(B0, fn-0x80)` for
`fn >= 0x80`). What that amendment missed: `HleSentinelTarget` and the
registry lookup both keyed off the call's *logical* `(family, function)` pair,
not the physical slot. `EntryAddress(C0, 0xBF)` and `EntryAddress(B0, 0x3F)`
read/write the identical guest address, but `HleSentinelTarget(C0, 0xBF)` and
`HleSentinelTarget(B0, 0x3F)` computed two different values, and the registry
dictionary held only `(B0, 0x3F)` — so a guest calling the registered puts
alias through its C0 high-range mirror (`C0:BF`) would misread the slot's real
sentinel as an unrecognized non-zero value and get `PatchedTarget` instead of
reaching `PutsService`, even on a completely unpatched, freshly-constructed
Runtime.

**Fix — one canonical identity per physical slot**, added as a single new
`BiosJumpTables.CanonicalizeIdentity(family, functionNumber)` helper (the sole
place the `0x80` mirror threshold is written down): a C0 call with
`functionNumber >= 0x80` canonicalizes to `(B0, functionNumber - 0x80)`; every
other call canonicalizes to itself. Both existing call sites for
family/function identity now go through this canonicalization:

- `HleSentinelTarget(family, functionNumber)` canonicalizes before computing
  the sentinel, so a C0 alias and its mirrored B0 identity always produce the
  identical value — matching the one physical guest RAM slot they share.
- `BiosHleRuntime.Invoke`'s registry lookup canonicalizes
  `(identity.Family, identity.FunctionNumber)` before consulting `services`,
  so `C0:BF` now resolves the very same registered handler as a direct
  `B0:3F` call.

**Original vs. canonical identity — Option 1 chosen, consistent with the
existing A0:3E/B0:3F alias policy.** The canonical identity is used
*only* to select the sentinel value and the registry entry. The
`BiosCallIdentity` actually passed to the resolved handler, and therefore
`BiosServiceResult.Diagnostic.Identity` and every `StableKey`, is always the
identity the guest actually called — `C0:BF` stays `C0:BF` in every
diagnostic and result, never silently rewritten to `B0:3F`. This mirrors the
pattern the B0:3F puts-alias amendment already established: registering two
distinct call identities against one shared implementation, while the
identity in the result names whichever one the guest actually invoked.
`Invoke`'s existing non-A0 patch-check bound and untranslatable-target
reporting are unaffected — this fix only changes which identity computes the
sentinel and which registry key is looked up, not the patch-check control
flow itself.

**Snapshot/save-state consistency:** because canonicalization is a pure
function of `(family, functionNumber)` with no new host-side state, and the
underlying guest RAM slot is unchanged (still exactly the address
`EntryAddress` already computed), a save-state captured through either the C0
alias or the direct B0 identity restores to the same dispatch behavior from
both identities after Blocker A's zero-only seeding fix — reconstructing a
Runtime over already-mirrored, already-patched guest memory changes nothing
about which physical bytes are read.

### Scope confirmation

This amendment does not touch `#362` (executing a patched guest target):
`PatchedTarget` is still only reported, never executed, from either the C0
alias or the direct B0 identity. It does not change `A0MaxFunctionNumber` or
the A0 patch-check bound (Blocker 2 territory in the prior amendment). It
does not change registry membership: still exactly `(A0, 0x3C)`, `(A0, 0x3E)`,
`(B0, 0x3F)`, `(B0, 0x56)`, `(B0, 0x57)`.

### Regression tests added (`BiosHleContractTests`)

- Zero-only seeding: `Constructor_SeedsSlot_OnlyWhenExistingEntryIsZero`,
  `Constructor_SeedsAllRegisteredSlots_OverFreshZeroedMemory`,
  `Constructor_PreservesGuestPatch_AndInvokeStillReportsPatchedTarget`,
  `SecondRuntime_ConstructedOverSameMemory_PreservesFirstRuntimesPatch`,
  `Constructor_ReSeeding_RestoredSentinel_DoesNotChangeItsMeaning`,
  `Constructor_SlotReadFailure_DoesNotSeed_AndDoesNotThrow`.
- Canonical physical-slot identity: `RegisteredB0Service_IsReachable_ThroughItsC0HighRangeAlias`
  (`C0:BF`→`B0:3F` puts, `C0:D6`→`B0:56` GetC0Table, `C0:D7`→`B0:57`
  GetB0Table), `HleSentinelTarget_IsIdenticalForC0Alias_AndItsCanonicalB0Identity`,
  `RegisteredMirrorSlot_ReadsAsCanonicalSentinel_ThroughEitherIdentity`,
  `PatchAppliedViaB0Side_IsObservedIdentically_ThroughC0AliasDispatch`,
  `PatchAppliedViaC0Alias_IsObservedIdentically_ThroughB0DirectDispatch`,
  `RegisteredMirrorSlot_PreexistingPatch_SurvivesConstruction_ObservedFromBothIdentities`,
  `C0AliasDispatch_PreservesTheOriginalC0Identity_InTheResult`.

All prior regression tests from the base ADR and every previous amendment,
including the address-arithmetic alias tests
(`EntryAddress_C0HighFunctionNumbers_Alias_DocumentedB0Entries`), the
unregistered-slot alias patch tests
(`PatchWrittenViaC0HighAlias_IsObservedThroughB0Dispatch`,
`PatchWrittenViaB0Slot_IsObservedThroughC0HighAliasDispatch`), the A0
upper-bound guard, and the read/save/patch/restore round trip, remain
unchanged and still pass.

## Amendment (2026-09-15): executing patched jump-table targets in the interpreter path (#362)

The prior amendment recorded finding (r) — "no interpreter or recompiled-code
dispatch trap for patched targets exists" — and deferred it to Issue #362. This
amendment closes the interpreter half of that gap and states precisely what
remains open on the recompiled half.

### Investigation: the gap was wider than "cannot jump"

Before this amendment `IBiosRuntime` had **no production consumer at all**.
`BiosHleRuntime.Invoke` was reached only from `BiosHleContractTests`; neither
`RecompilerInterpreterExecutor` nor the generated host ever called it. So
`PatchedTarget` was not merely "reported but not executed" — no execution path
had a BIOS call boundary to report it *to*. The missing capability was therefore
a BIOS vector trap, of which the patched-target jump is one of three outcomes.

### Decision: the trap is a PC redirect, not a new dispatch abstraction

Three designs were considered:

- **A — redirect the PC and return to the existing execution loop.** The
  interpreter already executes arbitrary MIPS from guest RAM; a patched target is
  ordinary guest code, so "jump to it" is exactly "set the PC to it and keep
  stepping".
- **B — a new shared `GuestExecutionTarget` / `RuntimeDispatchResult`
  abstraction** consumed by both executors.
- **C — interpreter only, recompiled path declared explicitly unsupported.**

**A (with C's honesty about the recompiled path) is chosen.** B was rejected
under YAGNI: `BiosServiceResult`'s three existing statuses already encode the
three things a trampoline can do with an entry (jump to the guest target,
return an HLE result, or resolve nothing), and a second result vocabulary
mapping one-to-one onto the first would be duplication, not structure. There is
likewise no pre-existing indirect-dispatch abstraction to reuse: `JR`/`JALR`
lower to `RecompilerIrTerminationReason.UnresolvedIndirectFlow` precisely
because the IR carries no runtime target, so nothing in the recompiler models a
computed jump this could have been expressed through.

> **Amendment (2026-09-29, Issue #635):** the lowering fact above is historical.
> Since #635, register-indirect `JR`/`JALR` carry their runtime target on
> `RecompilerIrExit.TargetValueId` (see the ADR-020 amendment) and are relayed
> through the existing generated dispatch loop and `host_transfer`. The decision
> (option A) is unchanged. The other `UnresolvedIndirectFlow` mentions in this
> ADR describe runtime BIOS stop paths and remain correct.

No CPU semantics are reimplemented. Setting the PC hands the target back to the
same native R3000A interpreter that ran the caller, and the target returns
through the `$ra` the original call site linked — the trampoline never consumes
the link register, exactly as on hardware.

### Vector→family mapping is now stated once

`BiosJumpTables.TryResolveVectorFamily(address, out family)` is added as the
single definition of the three trampoline vector addresses (`0xA0`/`0xB0`/`0xC0`,
primary-source-confirmed) and their KUSEG/KSEG aliasing rule. The static
`BiosCallRecognizer` (which asks this question of a decoded jump target) now
delegates to it, so the static analysis and the runtime trap cannot disagree
about which vector an address is.

### Trap semantics

`RecompilerInterpreterExecutor` optionally takes a factory that builds an
`IBiosRuntime` over the running core's guest memory. When one is supplied, a PC
that resolves to a trampoline vector is dispatched instead of ending the run:

| `BiosServiceStatus` | Effect |
|---|---|
| `PatchedTarget` | PC ← the raw guest target; the interpreter executes it. |
| `Supported` | `$v0` ← the service's return value (when it has one); PC ← `$ra`. |
| `Unsupported` | The run stops with `UnresolvedIndirectFlow` and the Runtime's own diagnostic. |

The identity is built from the PS1 ABI: `$t1` selects the function number and
`$a0`–`$a3` carry up to the registered service's own argument count (see the
2026-09-15 "minimal live-trap arity SSOT" amendment below — the exact-count
version corrects what this paragraph originally read as "always carry all
four"). `GuestPc` is left `null`: `core.Pc` at this point is the trampoline
vector address, not a call-site PC this executor tracks separately (same
amendment).

**No new address policy is introduced.** A patched target is rejected only when
`Ps1AddressTranslation.TryTranslate` rejects it — the same boundary every guest
memory access in this executor already passes through, applied to an address
that is about to be fetched from. The PC does not move in that case, and the
diagnostic names the address. A target that *is* translatable is jumped to
verbatim: a KUSEG alias stays a KUSEG alias, never normalised.

Dispatching a vector spends one step from the same budget that bounds ordinary
instructions, so a guest that loops back into a BIOS call is cut short by
`ExecutionBudgetExceeded` exactly as any other unterminated loop is.

The trap is opt-in. An executor constructed without a factory behaves exactly as
before, which is what keeps every existing differential fixture comparing like
for like against the generated host.

### Remaining blockers (#362 stays open)

- (t) ~~**The recompiled path cannot dispatch a BIOS vector.**~~ **Resolved** by
  the 2026-09-15 "dispatching BIOS vectors from the recompiled path" amendment
  below; the paragraph is kept as written for the record. A `jal` to a
  trampoline lowers to an ordinary `RecompilerIrFlowKind.Call` naming an address
  the program has no block for; the generated host's dispatch stops at an unknown
  PC. Nothing in the IR marks that target as a BIOS vector, and the host has no
  Runtime hook to call if it did. This is a generic gap in the generated-host
  dispatch, not a patched-target-specific one, and it was pinned by a gap marker
  test (since replaced, per the resolving amendment's item (d)).
- (u) ~~**A live trap cannot reach a registered HLE service.**~~ **Resolved**
  by the 2026-09-15 "minimal live-trap arity SSOT" amendment below: the trap
  now queries each registered service's exact argument count from the
  Runtime itself before reading `$a0`–`$a3`, rather than always supplying all
  four. Richer, machine-readable service descriptors (return-type metadata, a
  general ABI type system) remain out of scope and are tracked by Issue #365.

Both are out of scope here: #362 is bounded to transferring control to a target
that already exists as guest code. Dynamic overlay compilation (#249),
self-modifying code, and a whole-program interpreter fallback remain out of
scope and unchanged.

### Regression tests added (`BiosPatchedTargetExecutionTests`)

A synthetic program patches a jump-table entry with its own `sw`, calls the
vector, and proves the patched routine ran and returned:
`PatchedEntry_TransfersControlToTheGuestTarget_WhichRunsAndReturns`,
`PatchedEntry_TransfersControl_ForRegisteredAndUnregisteredAndMirroredSlots`
(registered A0:3C, registered B0:3F, the C0:BF mirror of that same physical
slot, and an unregistered A0 slot),
`PatchedEntry_Dispatches_ThroughEveryAliasOfTheTrampolineVector` (KUSEG and
KSEG0), `PatchedTarget_IsJumpedTo_Verbatim_WithoutSegmentNormalization`,
`PatchedTarget_OutsideEveryTranslatableRegion_StopsTheRun_WithADiagnostic`,
`UnpatchedUnregisteredEntry_StopsTheRun_WithTheRuntimesOwnDiagnostic`,
`PatchedTarget_LoopingBackIntoTheCall_IsBoundedByTheExecutionBudget`,
`WithoutABiosRuntime_ACallToAVector_LeavesTheProgramExactlyAsBefore`, and a
parity marker asserting the then-open recompiled-path gap (since replaced by
`ABiosVectorCall_StaysAnOrdinaryCallFlow_WithNoBiosMarkerInTheIr` — see the
2026-09-15 recompiled-path amendment's item (d)).

## Amendment (2026-09-15): CodeRabbit fresh review on #364 — minimal live-trap arity SSOT, and correcting the reported GuestPc

CodeRabbit's fresh review of PR #364 raised two findings against
`RecompilerInterpreterExecutor.TryDispatchBiosVector` before merge. Both are
confirmed valid and fixed here.

### Finding A (Major): the live trap always passed all four ABI registers

This is exactly blocker (u) above, materialised: `TryDispatchBiosVector`
always built `BiosCallIdentity.Arguments` from all four of `$a0`–`$a3`, but
every registered service (A0:3C, A0:3E, B0:3F, B0:56, B0:57) rejects any
argument count but its own exact arity. A live call to an unpatched
registered service therefore always failed with `BIOS_HLE_INVALID_ARGUMENTS`
— the "Supported" path this very PR's own live-trap mechanism exists to
reach was unreachable for every registered service.

**Fix: arity is a Runtime query, not an executor-side table.** `IBiosRuntime`
gains one new member:

```csharp
bool TryGetServiceArgumentCount(BiosCallFamily family, byte functionNumber, out int argumentCount);
```

`BiosHleRuntime` is the sole implementation and the sole source of truth: its
service registry, previously `IReadOnlyDictionary<(Family, Function),
Func<BiosCallIdentity, BiosServiceResult>>`, now carries the argument count
alongside each handler — `IReadOnlyDictionary<(Family, Function), (int
ArgumentCount, Func<BiosCallIdentity, BiosServiceResult> Handler)>` — so a
service's arity can never drift out of sync with its own argument-count
check. `TryGetServiceArgumentCount` resolves through the very same
`BiosJumpTables.CanonicalizeIdentity` alias mapping `Invoke` already uses, so
a C0 high-range alias (e.g. `C0:BF`) reports its canonical B0 service's arity
(`B0:3F` puts, one argument) — never a second, independently-tracked number;
alias and canonical identity share exactly one arity, exactly as they already
share one sentinel and one handler.

`TryDispatchBiosVector` queries this before reading any argument register:

| Arity lookup | Behavior |
|---|---|
| Registered, `N` ≤ 4 | Read exactly `$a0`..`$a{N-1}`; the rest are never read. |
| Registered, `N` > 4 | Stop with a new `BIOS_ARITY_EXCEEDS_REGISTER_BOUNDARY` diagnostic — this register-only trap has no stack-argument model and must not guess one. No registered service needs this today; it exists so a future one fails loudly here instead of silently misreading registers. |
| Unregistered | Read all four `$a0`–`$a3` (arity is unknown, so nothing is fabricated as zero) and dispatch anyway — `Invoke` still reaches the registry and reports `Unsupported` with its own diagnostic, unaffected by which registers were carried. |

**Scope boundary with Issue #365.** #365 ("Model BIOS HLE service arity for
live vector dispatch") owns richer, machine-readable service descriptors —
return-type metadata, a general ABI type system, and future recompiled-path
consumers. This PR implements only the minimal query #364 needs to unblock
its own live-trap Supported path: an argument *count*, nothing more. #365
stays open and is not closed by this change; a comment on #365 records that
this minimal SSOT was implemented here and what remains for it.

### Finding B (Minor): `GuestPc` reported the trampoline vector, not the call site

`TryDispatchBiosVector` passed `core.Pc` as `BiosCallIdentity.GuestPc`. At
that point in execution `core.Pc` **is** the trampoline vector address
(`0x000000A0`/`0xB0`/`0xC0`) — the interpreter's dispatch loop traps *before*
stepping into the vector, so the PC never moves off it. `GuestPc` is
documented as the guest call-site PC; reporting the vector address under
that name would misattribute every live-trap diagnostic's `pc=` field to a
fixed low address that names no actual call site in the guest program.

**Fix: `GuestPc` is `null`.** This executor does not yet track the transfer
instruction's own PC separately from `core.Pc`, so there is no real call-site
value to report. `null` is the honest value — `BiosDiagnostic.ToStableString()`
already renders a `null` `GuestPc` as `pc=unknown` rather than a fabricated
address. Nothing else changes: `BiosCallIdentity.GuestPc` remains optional by
design (see its constructor), and every other producer of an identity
(`BiosHleContractTests`, `BiosCallRecognition`'s static site tracking) is
unaffected — this fix touches only the one live-trap call site that was
passing the wrong value.

### Registry shape after this amendment

Registry membership is unchanged (still exactly `(A0, 0x3C)`, `(A0, 0x3E)`,
`(B0, 0x3F)`, `(B0, 0x56)`, `(B0, 0x57)`); only the value type each entry
maps to gained its argument count.

### Regression tests added

`BiosHleContractTests`: `TryGetServiceArgumentCount_ReturnsTheRegisteredServicesArity`
(all five registered services), `TryGetServiceArgumentCount_UnregisteredService_ReturnsFalse_AndZero`,
`TryGetServiceArgumentCount_C0HighRangeAlias_ReportsItsCanonicalB0Arity`.

`BiosPatchedTargetExecutionTests`: `LiveTrap_GetC0Table_ZeroArgumentService_IsSupported_DespiteNonZeroAbiRegisters`,
`LiveTrap_GetB0Table_ZeroArgumentService_IsSupported_DespiteNonZeroAbiRegisters`,
`LiveTrap_PutChar_OneArgumentService_UsesOnlyA0_DespiteNonZeroExtraAbiRegisters`,
`LiveTrap_Puts_OneArgumentService_UsesOnlyA0_DespiteNonZeroExtraAbiRegisters` (A0:3E and B0:3F),
`LiveTrap_C0HighRangeAlias_UsesTheCanonicalArity_AndDispatchesNormally`; and
`UnpatchedUnregisteredEntry_StopsTheRun_WithTheRuntimesOwnDiagnostic` gained
assertions that its diagnostic reports `pc=unknown`, never the trampoline
vector address, covering Finding B end-to-end through the live trap.

### What remains open

(t) is unchanged and #362 stays open on that basis: the recompiled path still
cannot dispatch a BIOS vector at all. (Superseded by the next amendment, which
closes (t).) #365 stays open for richer service descriptor/signature work beyond
the minimal arity count this amendment adds.

All prior regression tests from the base ADR and every previous amendment remain
unchanged and still pass.

## Amendment (2026-09-15): dispatching BIOS vectors from the recompiled path (#362)

The preceding amendment's blocker (t) — "the recompiled path cannot dispatch a
BIOS vector" — is closed here. Both execution paths now reach the same Runtime
through the same semantics.

### Decision 1: the semantics are extracted, not duplicated

`BiosVectorDispatch` (new, `PSXRecomp.Core/Runtime/`) is the single statement of
what a guest transfer to an A0/B0/C0 trampoline vector means. Given the guest
register file and the resolved family it builds the identity from the PS1 ABI,
queries the arity SSOT, invokes `IBiosRuntime`, and returns a
`BiosVectorDispatchOutcome` saying where control continues, what (if anything)
goes into `$v0`, or which diagnostic stops the run. Applying that outcome to a
concrete machine is the only path-specific part.

`RecompilerInterpreterExecutor.TryDispatchBiosVector` is now a thin adapter over
it; the three diagnostic-code constants it previously declared are aliases of
`BiosVectorDispatch`'s, so existing consumers are unaffected and there is still
exactly one definition of each. Nothing about the interpreter's observable
behavior changes — this amendment does not revisit the previous two.

The alternative of writing a second, generated-path-only dispatch was rejected
for the reason the base Decision's Alternatives section already gives: two
implementations of one contract is exactly how the interpreter and the
recompiled path come to disagree about a BIOS call.

### Decision 2: the backend gets a generic host hook, not BIOS knowledge

Three designs were considered for reaching the Runtime from generated code:

- **(A) the generated dispatch loop recognises a BIOS vector itself** — rejected.
  It would put `BiosJumpTables`' addresses and the family mapping into emitted C,
  which is precisely the "embed BIOS behavior in generated C" alternative the
  base Decision rejected, and it would give the backend a second copy of a rule
  `TryResolveVectorFamily` already owns.
- **(B) a dedicated BIOS IR flow kind, decided at lowering time** — rejected. It
  changes the IR contract (`RecompilerIrFlowKind`, the validator, every
  consumer) to carry a fact the IR does not need: the lowering already produces
  a correct `Call` naming the vector address, and marking it would push PS1 BIOS
  specifics into Recompiler Core. It also cannot express a vector reached
  indirectly, so the unknown-PC boundary would still need the check.
- **(C) the generated dispatch offers an unresolved PC to a generic host
  control-transfer hook, and the host classifies it.** **Adopted.**

Under (C) the emitted `RecompilerState` gains one optional function pointer,
`host_transfer`, consulted at the dispatch loop's unknown-PC boundary before it
gives up. The hook returns non-zero when the host does not claim the PC — in
which case the pre-existing behavior (Success once at least one block retired,
otherwise `UNSUPPORTED_IR`) runs unchanged — and zero when it does, having set
`termination_reason` and `next_pc` itself. This mirrors the `recompiler_read_mem*`
extern-helper precedent the backend already uses for guest memory: a contract
the host fulfils, not a behavior the backend implements.

Consequences of (C) worth recording:

- (a) **The generated C contains no BIOS identifier at all** — no vector
  address, no function number, no service name — which is pinned by a codegen
  test asserting the emitted source never mentions one.
- (b) **A null hook is byte-for-byte the previous behavior.** Every existing
  host driver zero-initialises its state, so nothing that does not want the hook
  has to change, and every existing differential fixture keeps comparing like
  for like.
- (c) **A claimed transfer retires like a block.** It emits a checkpoint at the
  PC it was claimed for and spends one step from the same budget that bounds
  retired blocks, so a guest looping back into a BIOS call is cut short by
  `ExecutionBudgetExceeded` exactly as the interpreter's is.
- (d) **The IR contract is untouched.** `jal 0xA0` still lowers to an ordinary
  `RecompilerIrFlowKind.Call` whose target the program has no block for, and
  nothing marks it; that is now pinned as intended behavior by
  `ABiosVectorCall_StaysAnOrdinaryCallFlow_WithNoBiosMarkerInTheIr` (which
  replaces the gap marker the previous amendment introduced).

### Decision 3: a patched target must resolve to an existing generated block

> Superseded in part by the amendment "patched-target-with-no-block parity
> between the host and interpreter engines (#379)" below: the guard and its
> diagnostic remain, but the diagnostic is now an advisory carried alongside a
> clean segment end rather than a stop reason.

The interpreter can enter any translatable address because it fetches guest
MIPS at run time. The generated host can only enter a PC it already compiled a
block for. So the recompiled path adds one guard on top of the shared
semantics: a `PatchedTarget` whose address is not a static block entry stops
the run with `BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK` instead of redirecting
there and silently falling off the end of the program at the next unknown PC.
That silent-success outcome is the failure mode Issue #279 forbids, so the
guard is part of the contract rather than an implementation detail.

- (e) **Block matching is by exact PC**, the same comparison the generated
  dispatch itself makes. A patched target naming a different segment alias of a
  compiled block (KUSEG for a KSEG0 program) therefore does not resolve and is
  reported. This is a real, deliberate limitation of the recompiled path, not a
  claim about hardware; the interpreter jumps to such an alias verbatim.
- (f) **Compiling a target found only at runtime is out of scope.** That is
  dynamic overlay recompilation, tracked as Issue #249. This amendment adds no
  interpreter fallback for the generated path either: mixing execution engines
  mid-run is a separate design decision nobody has taken, and introducing it as
  a side effect of a dispatch hook would be exactly the kind of silent
  capability creep this ADR exists to prevent.
  *(Superseded for in-image, register-indirect targets by the 2026-10-06 amendment (#693) at the end of this ADR — mixed execution, the `run` default since 2026-10-07.)*

### Test binding: the boundary is crossed, not simulated

The generated program is a separate OS process, so its `host_transfer` hook is
relayed to the test process over a line protocol on stdout/stdin carrying only
registers, single guest-memory bytes, and a decision. The protocol holds no BIOS
knowledge; the Runtime on the other side is a real `BiosHleRuntime` reading and
writing the running program's own guest RAM through the ordinary
`GuestMemoryReader`/`GuestMemoryWriter` boundaries. It is constructed at the
handshake the driver performs after the fixture's initial memory has landed and
before the first block retires, so its zero-only sentinel seeding is observed by
the generated program exactly as it is by the interpreter.

This keeps the assertions honest: what the tests exercise is generated C
compiled by the real backend and run, not a simulation of it.

### Registry, contract and scope after this amendment

- (g) Registry membership is unchanged: `(A0, 0x3C)`, `(A0, 0x3E)`, `(B0, 0x3F)`,
  `(B0, 0x56)`, `(B0, 0x57)`. No service was added, removed, or re-audited.
- (h) `IBiosRuntime`, `BiosCallIdentity` and `BiosServiceResult` are unchanged.
  The recompiled path consumes the identical contract the interpreter does.
- (i) **#365 is not a dependency of this change.** The jump-table patch check
  runs before registry/arity dispatch, and the minimal arity query the previous
  amendment added is sufficient for every registered service; the generated path
  uses the same query through the same shared helper. Richer, machine-readable
  service descriptors remain #365's scope and remain open.

### Regression tests added (`RecompiledBiosVectorDispatchTests`)

Every fixture runs the real pipeline (decode → lower → validate → host codegen →
gcc → run): `GeneratedPath_ZeroArgumentService_IsSupported_DespiteGarbageInEveryAbiRegister`
(B0:56 and B0:57, with garbage in all four ABI argument registers),
`GeneratedPath_OneArgumentService_UsesOnlyA0_AndProducesTheServicesOutput`
(A0:3C), `GeneratedPath_Puts_ReachesOneImplementation_ThroughEveryRegisteredIdentity`
(A0:3E, B0:3F, and the C0:BF mirror),
`GeneratedPath_PatchedEntry_TransfersControlToTheGeneratedBlockAtTheGuestTarget`,
`GeneratedPath_PatchedEntry_Dispatches_ThroughEveryAliasOfTheTrampolineVector`
(KUSEG and KSEG0), `GeneratedPath_PatchedEntry_OverridesARegisteredServicesOwnSlot`,
`GeneratedPath_PatchedTarget_WithNoGeneratedBlock_StopsAtTheTarget_WithAnAdvisory`
(named `..._StopsTheRun_WithADiagnostic` when it was added; renamed by the #379
amendment below),
`GeneratedPath_PatchedTarget_OutsideEveryTranslatableRegion_StopsTheRun_WithADiagnostic`,
`GeneratedPath_UnregisteredService_StopsTheRun_WithTheRuntimesOwnDiagnostic`,
`GeneratedPath_PatchedTarget_LoopingBackIntoTheCall_IsBoundedByTheExecutionBudget`,
`WithoutABiosRuntime_ACallToAVector_LeavesTheGeneratedProgramExactlyAsBefore`,
and three interpreter/recompiled parity runs through
`RecompilerDifferentialRunner` covering the patched-target, Supported and
Unsupported outcomes (GPR, HI/LO, PC, memory, termination reason, checkpoint
trace, the diagnostic, and the service's captured output).

`HostCodeGenTests` pins the emitted contract: `Generation_HostTransfer_Hook_Is_In_State_Struct`
and `Generation_Dispatch_Offers_An_Unknown_Pc_To_The_Host_Before_Giving_Up`
(including that the generated source never names the BIOS).

All prior regression tests from the base ADR and every previous amendment remain
unchanged and still pass.

## Amendment (2026-09-16): patched-target-with-no-block parity between the host and interpreter engines (#379)

### Problem

Decision 3 above was written before the full-title execution orchestrator
(`ExecutionOrchestrator`, #366/#373) and its `ITitleExecutionHandoff` existed.
It made "a `PatchedTarget` this program has no block for" a **stop reason**:
`HostTransferSession.HandleTransfer` answered the generated dispatch with
`UnresolvedIndirectFlow` plus `BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK`.

Once the orchestrator existed, that turned one guest-level condition into two
different orchestrator-visible outcomes:

- **Host engine** — a diagnosed `UnresolvedIndirectFlow`, which
  `ExecutionOrchestrator` maps straight to `TitleExecutionState.RuntimeFailure`.
  The handoff is never consulted.
- **Interpreter engine** — the patched target is jumped to verbatim; the next
  loop iteration's program-range check ends the segment with `Success` at that
  PC, which the orchestrator routes into `handoff.Decide(...)`.

Both cite Issue #249 as the escape hatch for exactly this case, but only the
interpreter reached the thing that can take it.

### Decision

The guard stays; its verdict changes. A `PatchedTarget` with no generated block
now ends the host segment the same way the interpreter's does — `Success`, with
the PC left at the patched target — and
`BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK` is recorded as an **advisory** on the
`RecompilerExecutionResult` rather than as the segment's termination reason.

Consequences:

- (a) **Both engines hand the orchestrator the identical unresolved PC**, so the
  same handoff produces the same `TitleExecutionState` on either backend. That
  is what #249's escape hatch was for; deciding what such a target means is the
  caller's job, not the backend's.
- (b) **No information is lost.** The advisory still names the family, the
  function number, the unreachable address and #249, and still reaches a caller
  that has no handoff (the differential harness reads it off the result). What
  #279 forbids is a *silent* success; this outcome is neither silent nor a claim
  that the call succeeded.
- (c) **The orchestrator is unchanged.** No new state, no new contract member,
  and no backend-specific diagnostic code in `PSXRecomp.Core` — the parity is
  achieved entirely inside the host backend that diverged.
- (d) **Alternatives rejected.** Making the *interpreter* hard-fail instead
  (option b of #379) would delete a capability the interpreter legitimately has
  and contradict both the orchestrator's own doc comment and this ADR's #249
  framing. Special-casing the diagnostic code inside `ExecutionOrchestrator`
  would put a Test-assembly backend's diagnostic string into the Domain layer.
- (e) **Known limitation, unchanged.** A patched target *inside* the program
  image but not at a block entry (a mid-block address, or a different segment
  alias of a compiled block — point (e) of Decision 3) still diverges: the
  interpreter executes from it, the host stops at it. Closing that is dynamic
  overlay recompilation, Issue #249.

### Regression tests

`ExecutionOrchestratorTests.BiosJumpTableDispatch_ReachesTheSameOrchestratorOutcome_OnBothEngines`
is a single contract-driven `[Theory]`: one scenario table of guest programs and
expected outcomes, each scenario run through **both**
`InterpreterTitleExecutionEngine` and `HostTitleExecutionEngine` with the same
handoff, asserting both against the same expected row *and* against each other.
It covers an unpatched registered entry, a patch that overrides a registered
service's slot, an unregistered service, and the patched-target-with-no-block
case this amendment is about, including `$v0` return-value propagation and the
patched routine's guest-RAM side effect read back into a register.

`RecompiledBiosVectorDispatchTests.GeneratedPath_PatchedTarget_WithNoGeneratedBlock_StopsAtTheTarget_WithAnAdvisory`
(renamed from `..._StopsTheRun_WithADiagnostic`) pins the backend-level change:
`Completed` status, the advisory diagnostic, `Success` termination, and the PC
left at the address the recompiled path could not enter.

## Amendment (2026-09-17): A0:39 InitHeap registered

Issue #279's production-path evidence (`docs/runtime/bios-hle-evidence.md`
§3.4) records `A0:39 InitHeap(addr,size)` as the most broadly observed BIOS
identity across the locally available real-ROM fixtures — 5 of 5 distinct
executables, one site each, more than any other identity including `B0:3F`
(the alias selected by the previous evidence-driven registration). Its
identity is already verified (`docs/REFERENCES.md:114-116`,
`BiosCallNames.cs:48`); this amendment registers the service.

- (a) **Documented effect, and why nothing about it is skipped.**
  `docs/REFERENCES.md` describes `InitHeap(addr,size)` as: "initializes the
  address and size of the heap used by `malloc`/`realloc`/`calloc`/`free` and
  `qsort`; also deallocates all memory handles. The BIOS never calls it
  automatically, so software must." Unlike `puts` (a guest-memory pointer
  argument whose read this ADR's base Decision required before registration)
  or `GetC0Table`/`GetB0Table` (a returned address this Runtime had to back
  with guest-visible, dispatch-connected state before registration),
  `InitHeap`'s two arguments are plain scalar words — no guest-memory access
  is needed to honour its ABI — and this Runtime registers no
  malloc/realloc/calloc/free/qsort service that could ever observe the heap
  bookkeeping `InitHeap` is documented to establish. There is therefore no
  guest-observable consumer of that bookkeeping for this Runtime to under- or
  mis-model; validating the argument shape is InitHeap's complete
  guest-observable contract today, satisfying the same "no documented,
  observable effect is skipped" bar the base Decision set for `puts` and the
  jump-table amendment set for `GetC0Table`/`GetB0Table` — the bar differs in
  *what it requires*, not in being relaxed for this service.
- (b) **No documented return value.** Neither `docs/REFERENCES.md` nor any
  prior verification pass records a return value for `InitHeap`, unlike
  `puts` (returns its string-pointer argument) or `GetC0Table`/`GetB0Table`
  (return a table address). Per this ADR's no-guessing rule, no return value
  is invented: the service is registered as
  `BiosServiceResult.Supported(identity)` with a null `ReturnValue`, which
  `BiosVectorDispatch` already treats as "leave `$v0` untouched" — the same
  mechanism the base Decision documented but no previously registered service
  has exercised end to end. A new production-path test
  (`TitleExecutionServiceTests.RunDiagnostic_InitHeap_DrivesTheWholePipeline_ToAClassifiedCompletion_WithV0Untouched`)
  seeds `$v0` with a sentinel before the call and asserts it survives
  unchanged, proving this path is live through the interpreter's register
  trap, not merely asserted at the `BiosVectorDispatch` unit level.
- (c) **No new Runtime capability required.** `InitHeap` needs none of the
  three boundaries `BiosHleRuntime` already requires (`IRuntimeOutputSink`,
  `IGuestMemoryReader`, `IGuestMemoryWriter`); the generic per-slot HLE
  sentinel seeding in the constructor covers `(A0, 0x39)` automatically, the
  same as every other registered slot, because it iterates the registry
  rather than a hard-coded list of five. Registering it is therefore a
  wiring/argument-validation task, not a capability-research task — the same
  category the `B0:3F` amendment established for an alias, applied here to a
  fresh identity with a materially different (return-less) ABI.
- (d) **Not the card-family identities also verified in the same pass.**
  `A0:AB _card_info`, `A0:AC _card_load`, `B0:4E _card_write`, and `B0:50
  _new_card` remain unregistered. They are memory-card I/O and are left to
  the memory-card runtime work tracked separately; this amendment changes
  nothing about them.
- (e) **No other service's status changes.** The registry gains exactly one
  entry: `(A0, 0x39)` → `InvokeInitHeap`. getchar (A0:3B) and gets (A0:3D)
  stay unregistered pending an input-sink design; `B0:3D` stays unregistered
  pending evidence; the card identities stay unregistered per (d). Issue #279
  remains open.

### Registry after this amendment

- (f) The registry now holds six entries: `(A0, 0x39)`, `(A0, 0x3C)`,
  `(A0, 0x3E)`, `(B0, 0x3F)`, `(B0, 0x56)`, `(B0, 0x57)`.

### Regression tests

`BiosHleContractTests` adds: `InitHeap_Accepts_AddrAndSize_And_Reports_No_Return_Value`
(the registered dispatch, and that `ReturnValue` is null);
`InvalidInitHeapArgumentCounts_Are_Rejected_Explicitly` (0, 1, and 3 arguments,
mirroring the putchar arity-rejection pattern); the neighbouring-slots theory
gains `A0:38`/`A0:3A`; and the three "every registered service" theories
(fresh-instance determinism, sentinel-not-zero, and constructor seeding over
fresh memory) gain the `(A0, 0x39)` case each, so InitHeap gets the same
regression coverage every other registered slot already has. The production
path gains `TitleExecutionServiceTests.RunDiagnostic_InitHeap_DrivesTheWholePipeline_ToAClassifiedCompletion_WithV0Untouched`
(described in (b) above).

## Amendment (2026-09-30): A0:13 setjmp registered

Issue #648: after explicit entry roots (#644) the Persona production run stopped
at `BIOS_HLE_UNSUPPORTED_CALL` `A0:13`. `A(13h) setjmp(buf)` is registered
through `SetJmpService`.

- **Documented effect (PSX-SPX kernelbios, misc-functions).** Store the
  ABI-saved registers in the 0x30-byte guest buffer at `$a0`, little-endian:
  +00 `$ra`, +04 `$sp`, +08 `$fp`, +0C..+28 `$s0..$s7`, +2C `$gp`; return 0
  when called directly. Nothing else is saved. The effect is a guest-memory
  write, so the service is registered only with the write boundary attached.
- **Register file at the call.** The service reads registers beyond the ABI
  arguments, so `BiosCallIdentity` gains an optional `GuestRegisters` (the full
  GPR file). `BiosVectorDispatch.Dispatch` fills it for every call, so the
  interpreter and the generated host both supply it; no path-specific code.
- **Return and PC.** The service returns `Supported` with value 0; the caller
  applies `$v0 = 0` and continues at the call site's `$ra` exactly once,
  through the existing `BiosVectorDispatchOutcome`.
- **Fail closed.** The buffer is written through
  `IGuestMemoryWriter.TryWrite`, which is all-or-nothing and rejects wraparound,
  untranslatable and out-of-RAM ranges. A rejected write, or a call carrying no
  register file, is `BIOS_HLE_UNSUPPORTED_STATE`; a wrong argument count is
  `BIOS_HLE_INVALID_ARGUMENTS`.
- **Not in scope.** A0:14 `longjmp` (the "return again" half) stays
  unregistered and reports `BIOS_HLE_UNSUPPORTED_CALL`.

Tests: `SetJmpServiceTests`.

## Amendment (2026-09-30): B0:19 HookEntryInt registered

Issue #650: after A0:13 the Persona production run stopped at
`BIOS_HLE_UNSUPPORTED_CALL` `B0:19`. Registering it as a bare `Supported` would
discard the hook's guest-visible meaning, so it is registered with real state.

- **Pointer, not copy.** psx-spx: "addr points to a structure (with same format
  as for the setjmp function)". Registration stores only the address; the
  0x30-byte buffer is read when the hook fires, so later guest edits to it are
  observed. The A0:13 layout is now shared (`JmpBufLayout`).
- **State lives in guest RAM.** The address is held in a 4-byte kernel variable
  at `0x00000118` (`BiosExceptionHook.PointerAddress`; inside psx-spx's unused
  "table of tables" slot, a Runtime design choice like the B0/C0 table bases,
  since the real variable's location is undocumented). Some engines rebuild
  `BiosHleRuntime` per segment, so a Runtime field would lose the registration.
- **Firing.** `BiosExceptionHook.TryComplete` (reached only through
  `BiosExceptionCompletion.Complete`, see the #651 amendment) reads all 0x30 bytes first
  (all-or-nothing), then restores only `$ra/$sp/$fp/$s0-$s7/$gp`, sets `$v0 = 1`
  and reports the saved `$ra` as the PC. An unreadable buffer is
  `InvalidState` and changes nothing.
- **B0:19's own return.** No return value is documented, so `$v0` is untouched
  and the caller's `$ra` is applied once by the existing dispatch outcome.
- **Not yet connected.** The consumer is the kernel exception handler's
  completion step (C0:06), which this Runtime does not model; B0:17
  ReturnFromException and B0:18 ResetEntryInt stay unregistered.

Tests: `BiosExceptionHookTests`.

## Amendment (2026-09-30): B0:5B ChangeClearPAD registered

Issue #652: after B0:19 the Persona production run stopped at
`BIOS_HLE_UNSUPPORTED_CALL` `B0:5B`.

- **CONFIRMED (psx-spx).** `B(5Bh) ChangeClearPAD(int)` applies to pad and card
  and configures the Pad/Card IRQ handler's automatic IRQ0 (VBlank)
  acknowledge. **Not documented:** which argument value enables it, any return
  value, and any relation to `C0:0D SetIrqAutoAck` (the DefaultInterruptHandler's
  per-IRQ auto-ack), so none is assumed.
- **Configuration only.** The raw argument is stored as given
  (`BiosPadCardAutoAck`); no polarity is interpreted, no return value is
  reported (`$v0` untouched), and I_STAT and devices are never touched, so IRQ0
  stays pending until the guest acknowledges it.
- **State in guest RAM.** An 8-byte kernel variable at `0x00000128` (+0
  configured flag, +4 last argument; inside psx-spx's unused "table of tables"
  slot, a Runtime design choice) because some engines rebuild `BiosHleRuntime`
  per segment. `BiosPadCardAutoAck.TryGetSetting` is the read contract.
- **No consumer yet.** The Runtime has no BIOS Pad/Card IRQ handler, so the
  setting is recorded but not acted on; tracked in #654 (see the #654
  amendment below).

Tests: `BiosPadCardAutoAckTests`.

## Amendment (2026-09-30): C0:0A ChangeClearRCnt registered

Issue #655: after B0:5B the Persona production run stopped at
`BIOS_HLE_UNSUPPORTED_CALL` `C0:0A`.

- **CONFIRMED (psx-spx).** `C(0Ah) ChangeClearRCnt(t,flag)`: `t` 0..2 = timer
  0..2, 3 = vblank; `flag` 0 = the kernel's IRQ handler does nothing after
  processing, 1 = it acknowledges the IRQ and immediately returns from
  exception; returns the previous flag.
- **Configuration only, previous flag returned.** The new flag is stored and the
  old one is the return value (written to `$v0` by dispatch). I_STAT and the
  timers are never touched. `t > 3` and a `flag` other than 0/1 are undocumented
  and are rejected with `BIOS_HLE_INVALID_ARGUMENTS` rather than guessed. The
  initial flag is 0 (INFERRED: zero-initialised kernel memory).
- **State in guest RAM.** Four words at `0x00000130` (one per `t`; inside
  psx-spx's unused table-of-tables slots, a Runtime design choice) because some
  engines rebuild `BiosHleRuntime` per segment. Read contract:
  `BiosRootCounterClearPolicy.TryGetFlag`.
- **No consumer yet.** The Runtime has no kernel timer/vblank IRQ handler;
  tracked in #658.

Tests: `BiosRootCounterClearPolicyTests`.

## Amendment (2026-09-30): Pad/Card IRQ handler consumes the B0:5B setting

Issue #654: the B0:5B setting had no consumer.

- **Polarity CONFIRMED by two sources outside psx-spx.** OpenBIOS
  (`sio0Handler`; B0:5B is its `setSIO0AutoAck`) acknowledges only IRQ0 when
  the flag is set; PSn00bSDK calls `ChangeClearPAD(0)` to stop the kernel
  acknowledging and `ChangeClearPAD(1)` to restore it. So 0 = leave IRQ0
  pending, 1 = acknowledge IRQ0. **UNKNOWN:** other values (OpenBIOS treats any
  non-zero as enable; the retail BIOS is undocumented) and the value before the
  first B0:5B (OpenBIOS StartPAD sets 1; StartPAD is not modeled here). Both are
  reported (`UnknownSetting`, `NotConfigured`) and leave IRQ0 pending.
- **`BiosPadCardIrqHandler.Handle`.** Claims the exception only when IRQ0 is
  set in both I_STAT and I_MASK (OpenBIOS `sio0Verifier`; INFERRED for the
  retail BIOS). Acknowledges through the existing `IInterruptController` as an
  I_STAT write-0-to-clear of bit 0 only; no interrupt or SIO0 state is
  duplicated. B0:5B itself still never touches I_STAT.
- **Not modeled.** Pad reads and the card state machine (SIO0 stays in the
  native core), StartPAD/StartCARD, and the priority-chain enqueue. Calling
  `Handle` from the exception path belongs to the kernel ExceptionHandler
  boundary (#651); the wiring is tracked in #661, and until then the handler is
  exercised by tests only.
- **C0:0D SetIrqAutoAck** is the DefaultInterruptHandler's per-IRQ auto-ack, a
  separate handler and setting (OpenBIOS keeps them separate too); it is not
  consulted here.

Tests: `BiosPadCardIrqHandlerTests`.

## Amendment (2026-09-30): exception-completion boundary

Issue #651: B0:19 registered a hook that nothing fired. The completion step of
the kernel exception handler is now a Runtime contract, `BiosExceptionCompletion`.

- **CONFIRMED (psx-spx interrupt-exception-handling).** The hook runs "only if
  the ExceptionHandler has been fully executed"; a chain element that calls
  ReturnFromException skips it. The default Exit structure (B0:18) has
  ReturnFromException as its PC. B0:17 restores R1-R31 except k0, HI, LO, SR
  and PC from the current TCB, and its RFE re-enables interrupts.
- **Completion.** `Complete` enters the hook when one is registered (the #650
  semantics: `$v0 = 1`, PC = saved `$ra`); with `0x118 == 0` it reports
  `ReturnFromException` and changes nothing, because the default Exit's only
  effect is ReturnFromException; an unreadable buffer is `InvalidState`. It is
  the only way the hook fires (`BiosExceptionHook.TryComplete` is internal). It
  is called after the priority chains ran to the end — never at IRQ time and
  never after an early ReturnFromException.
- **Saved-state restore.** `TryReturnFromException` reads the current TCB
  through the documented table-of-tables entry `0x108` → PCB → `[PCB+0]`, using
  the documented TCB layout (08h r0..r31, 88h epc, 8Ch hi, 90h lo, 94h sr). It
  reads all of it before writing anything; a zero or unreadable pointer, or an
  unreadable TCB, changes nothing. SR is reported as saved: RFE is the CPU's to
  apply, so it is not re-implemented here. k0 and r0 are left untouched.
- **No second state.** The type holds no state: the hook pointer and the TCB
  are guest RAM, and EPC/CAUSE/SR stay the CPU's. The interpreter's
  `_inInterruptHandler` path for a guest-installed 0x80000080 handler is
  unchanged.
- **Not connected yet.** No execution path calls the boundary: the C0:06
  entry (vector stub, kernel PCB/TCB/ExCB init, chain walk) is #662. B0:17
  stays unregistered until the dispatch outcome can carry a full-state restore
  and the CPU applies RFE (#664); B0:18 needs Runtime choices for the Exit
  structure and exception stack (#665). A BIOS-less run has no PCB, so B0:17
  would fail closed today. *(The C0:06 entry and the PCB/TCB seeding are #662; see
  the 2026-10-01 amendment below.)*

Tests: `BiosExceptionCompletionTests`.

## Amendment (2026-09-30): A0:72 CdRemove registered

Issue #657: after C0:0A the Persona production run stopped at
`BIOS_HLE_UNSUPPORTED_CALL` `A0:72`.

- **CONFIRMED: identity and contract.** `A(72h) or A(56h)` is one routine,
  `_96_remove` / `CdRemove` (psx-spx). PSY-Q / PSn00bSDK prototype:
  `void _96_remove(void)` — no arguments, no return value.
- **CONFIRMED: documented intent.** psx-spx: "DequeueCdIntr and _96_remove try
  to remove priority 0 elements" — the kernel's CD-ROM IRQ handlers
  (CdromDmaIrq, CdromIoIrq; chain 0 also holds SyscallException), which the
  BIOS installs via `_96_init` before the boot executable starts.
- **CONFIRMED (psx-spx.github.io): retail BIOS bug.** `A(72h)` "does NOT work
  due to SysDeqIntRP bug" (same for `A(A3h) DequeueCdIntr`). `C(03h)
  SysDeqIntRP` "does only check the first element properly, and, thereafter it
  reads a garbage value from an uninitialized stack location, and acts more or
  less unpredictable". **Sources disagree:** the current no$psx text (v2.3)
  drops both notes, saying only that SysDeqIntRP "contains several nonsense
  opcodes that are never executed". Either way, what the retail routine
  actually leaves in chain 0 is **UNKNOWN**.
- **Runtime behavior: accepted, no modelled effect.** The Runtime must not
  claim a removal the retail BIOS does not reliably perform, and it models no
  priority chain, CD-ROM IRQ dispatch or kernel event table, so no Runtime
  state exists for the call to change. A0:72 is registered with arity 0 and a
  void return: `$v0` untouched, the caller's `$ra` applied once by dispatch, no
  guest memory written. Any argument is `BIOS_HLE_INVALID_ARGUMENTS`. This
  reflects the known-broken semantics, not a skipped effect.
- **INFERRED, not modelled.** PCSX-Redux OpenBIOS `deinitCDRom` (a
  reimplementation, not retail behavior) also enters a critical section and
  closes the five CD-ROM events. PSY-Q libetc `startIntr` calls
  ExitCriticalSection right after `_96_remove` and does not use a result.
- **Future CD-ROM IRQ work (#444)** must not assume that calling A0:72 removed
  the kernel CD-ROM handler.
- **Not registered.** The A0:56 alias (not observed on the production path) and
  A0:71 `_96_init` stay `BIOS_HLE_UNSUPPORTED_CALL`.

Tests: `BiosCdRemoveTests`.

## Amendment (2026-09-30): SYSCALL SYS(02h) ExitCriticalSection

Issue #663: after A0:72 the Persona production run stopped at `CPU_EXCEPTION`
on a `syscall` with `$a0 = 2`. A SYSCALL is the kernel's own boundary, not an
A0/B0/C0 vector call, so it does not go through `BiosVectorDispatch`.

- **CONFIRMED (psx-spx interrupt-exception-handling).** SYS(02h)
  ExitCriticalSection "enables interrupts by set SR (cop0r12) Bit 2 and 10 (of
  which, Bit2 gets copied to Bit0 once when returning from the syscall
  exception). There's no return value (all registers except SR and K0 are
  unchanged)." The Issue text matches; the bits are those of the SR *inside*
  the exception frame (bit 2 is IEp after the entry push), and the RFE on return
  moves bit 2 to bit 0. The net guest-visible effect of a call is
  `SR' = SR | 0x401` (IEc, IM2), which the tests assert.
- **Contract.** `BiosKernelSyscallDispatch.Dispatch(number, srAtEntry)` is the
  one definition, for every execution path: it returns the frame SR the kernel
  leaves (`srAtEntry | 0x404`) or an explicit unsupported outcome
  (`BIOS_SYSCALL_UNSUPPORTED`). It holds no state and no second SR.
- **The execution path owns the CPU side.** A SYSCALL exception (Excode 8) with
  a Runtime attached is completed by the path that owns the CPU: exception entry,
  RFE pop, resume at EPC+4. Anything else — other Excodes, a SYSCALL in a branch
  delay slot, or no Runtime — is unchanged (`CPU_EXCEPTION`).
  - **Interpreter** (`InterpreterTitleExecutionEngine`): the native CPU has
    already done the entry; the engine writes the frame SR (`SetCop0`) and calls
    the new native `PSXCore_PopExceptionSrStack`, which is RFE's SR transform
    without running an instruction. RFE is not re-implemented in managed code.
  - **Generated host**: the artifact is the CPU here and gains one COP0 word,
    `cop0_sr` (zero at start, like the native reset; no lowered instruction
    reads it). Its `host_syscall` hook, emitted only for programs with a SYSCALL
    exit, does the entry push, offers `RHOST_SYSCALL fault_pc a0 sr` to the
    parent, applies the parent's `C sr` reply, does the RFE pop and resumes at
    `fault_pc + 4`.
- **Fail closed.** SYS(00h), SYS(01h) EnterCriticalSection, SYS(03h) and SYS(04h+)
  are `BIOS_SYSCALL_UNSUPPORTED` and stop the run. (The real kernel also
  services these; they are not modelled.)
- **Not modelled:** the C0:06 exception handler and TCB save/restore (#662),
  EnterCriticalSection, a SYSCALL in a delay slot.

Tests: `BiosKernelSyscallTests`; native `PSXCore_PopExceptionSrStack`.

## Amendment (2026-10-01): generated-host MMIO bridge

Issue #678 (first slice of #676, evidence #675): a generated-host artifact owns
guest RAM but read 0 / dropped every access outside it, so GPUSTAT, timers and
every device write were invisible to the Runtime.

- **RAM stays local.** `artifact_ram` is the single guest RAM. Accesses inside the
  low-8-MiB RAM mirror never cross the protocol, and the byte-wise `R`/`W`
  requests that carry BIOS HLE's own RAM access stay RAM-only, so an MMIO request
  can never interleave with one.
- **Everything else is one width-aware request.** A guest load/store outside RAM
  is sent as `RHOST_MMIO_READ <width> <physical>` / `RHOST_MMIO_WRITE <width>
  <physical> <value>` (width 1, 2 or 4) and answered `V <value>` or `X`. It is
  never decomposed into bytes: MMIO has width-sensitive registers (the GPU window
  is a 32-bit device), FIFOs and read side effects. Constants live in
  `RecompiledArtifactCodeGen` and are emitted into the C driver.
- **One Runtime device graph.** The parent serves the requests from
  `PsxDeviceGraph` — the native core, `MemoryBus` and the DMA/timer/interrupt/GPU/
  CD-ROM adapters, assembled once and shared with `InterpreterTitleExecutionEngine`
  — entering through the same `PSXCoreWrapper` read/write path the interpreter's
  guest loads and stores use. The artifact carries no device model and no address
  list. The graph holds device state only: advancing it (#679) and delivering
  interrupts (#680) are separate slices.
- **Fail closed.** The Runtime answers for the scratchpad, the hardware-register
  window (a routed device, or the core's flat register store for an unclaimed
  address, as for the interpreter) and addresses outside every region (open bus:
  read 0, write ignored). Main RAM and the BIOS-ROM window (no image) are refused:
  `ARTIFACT_MMIO_UNSUPPORTED`. A device exception is `ARTIFACT_MMIO_DEVICE_FAILED`;
  a malformed request is `ARTIFACT_HOST_PROTOCOL_FAILED`. The child exits 97 on a
  refusal, 98 on a malformed or missing reply, and 96 when it has no host at all
  (no Runtime attached) — never a snapshot, never a silent 0.

Tests: `RecompiledArtifactMmioBridgeTests`.

## Amendment (2026-10-01): BIOS-less C0:06 ExceptionHandler entry

Issue #662: nothing called the #651 completion boundary, because a BIOS-less run has
no code at the general exception vector (the real BIOS places a stub that jumps to
C0:06). After #680 delivered the device IRQ to the generated-host CPU as an INT, the
Persona production run stopped there (`ARTIFACT_EXCEPTION_VECTOR_UNHANDLED`).

- **One shared entry.** `BiosExceptionHandler.Handle` is the kernel handler for every
  execution path, as `BiosKernelSyscallDispatch` is for SYSCALL: the interpreter and the
  generated host pass the CPU's exception state (`BiosExceptionContext`: EPC/CAUSE/SR read from
  the CPU, plus HI/LO) and apply the returned `BiosExceptionHandlerOutcome` to their own CPU.
  The type holds no CPU state, no interrupt state and no ExCB; interrupts are reached through
  the existing `IInterruptController`.
- **Vector ownership.** The vector is the kernel's only when the four words at `0x80`
  (the size of the real stub) are all zero (`IsKernelVector`) and a Runtime is attached. Any
  non-zero word keeps it guest-owned, so the `_inInterruptHandler` / `_rfePending` path (#499)
  is unchanged. SR.BEV = 1 (`0xBFC00180`) is not served. Only Excode INT is served; SYSCALL stays
  #663's and anything else is `BIOS_EXCEPTION_UNSUPPORTED`.
- **Order (PSX-SPX interrupt-exception-handling).** Save the interrupted context into the
  current TCB (r1-r31, EPC, HI, LO, SR at the TCB layout of the #651 amendment, plus CAUSE at
  `98h`); walk the priority chains; only if they ran to the end call
  `BiosExceptionCompletion.Complete` (hook, else the default Exit); the default Exit and a chain
  element's own ReturnFromException both restore through `TryReturnFromException`, the same
  read B0:17 will use (#664). The hook is never fired at IRQ time, nor after an early
  ReturnFromException. After a hook, SR is left as the CPU has it (no RFE ran); after
  ReturnFromException the saved SR is handed to the CPU, which applies its own RFE.
- **PCB/TCB in a BIOS-less run.** A real kernel builds them at boot; here `[0x108] == 0`.
  Only then does the first exception seed one PCB (`0xE000`, `[PCB+0]` -> TCB) and one zeroed
  TCB (`0xE100`) and point `[0x108]` at it. These addresses are this Runtime's own choice in
  the kernel-reserved page, like `0x118`; the real ones are not documented (UNKNOWN). Nothing
  else is invented: no thread status, no ExCB, no sizes. A non-zero `[0x108]` is guest state:
  an unusable PCB/TCB is `BIOS_EXCEPTION_KERNEL_STATE_INVALID` and is never replaced.
- **The chain is a seam.** `BiosExceptionChain` walks the priority chains with a `BiosExceptionChainContext`
  (guest memory reader/writer, the existing `IInterruptController`, the `BiosExceptionContext`; no CPU or
  engine object, so #658/#661 elements can read guest kernel state without a second CPU semantics). The seeded
  PCB/TCB (`0xE000`/`0xE100`) is Runtime-reserved kernel state: a future ExCB/EvCB/TCB allocator needs one SSOT
  for it. Both engines take
  it as an optional constructor argument (null = `DefaultChain`). `DefaultChain` runs priority 1 first
  (`BiosTimerVblankIrqHandler`, #658: VBlank/IRQ0, Timer2/IRQ6, Timer1/IRQ5, Timer0/IRQ4, in that INFERRED
  order; a source is claimed when its IRQ is pending in I_STAT and enabled in I_MASK). For a claimed source
  the existing `BiosRootCounterClearPolicy` flag (the only state) is read and validated **first**: an
  unreadable flag, or one other than 0/1, fails closed without delivering anything, and guest state is not
  corrected. Only for a valid flag are the root-counter events delivered through
  `BiosRootCounterEventDelivery`; the default delivers none (#660 is not modelled), and a failed delivery
  likewise fails closed without acknowledging the IRQ (`BIOS_EXCEPTION_CHAIN_UNSUPPORTED` naming the source,
  its flag and #660). After a successful delivery: flag 0 = no acknowledge, no return, the chain continues;
  flag 1 = acknowledge only that IRQ through `IInterruptController` (W0C) and return from the exception at
  once (`ReturnedFromException`: lower priorities and the hook are skipped). Priority 2 (Pad/Card, #661) is reached only when no priority-1 element returned;
  `DefaultChain` then ends when no enabled IRQ is pending, otherwise it fails closed with a source-neutral
  diagnostic (I_STAT, I_MASK, pending-enabled bits; the owner of the remaining IRQ is not identified). Other default
  elements plug in here: Pad/Card (#661, `BiosPadCardIrqHandler`), guest elements (C0:02). Per-IRQ handler
  ownership was not verified and is not encoded.
- **Generated host.** The artifact remains the CPU; the wire additions (read `E`; write `G`,
  `H`, `C`, `P`, `L`) are in the ADR-025 addendum.
- **Measured (Persona).** The entry is crossed; the run now stops at the chain with the
  VBlank IRQ0 pending (`docs/v0.1.0/persona-e2e-status.md`, item 21).
- **Not modelled:** any chain element, B0:17/B0:18 registration (#664/#665), BEV = 1, a
  delay-slot-specific EPC adjustment (EPC is restored verbatim, as the CPU stored it).

Tests: `BiosExceptionHandlerTests`, `KernelExceptionEntryTests` (interpreter),
`RecompiledArtifactInterruptTests` (generated host).

## Amendment (2026-10-02): root-counter event delivery (#660)

The priority-1 timer/VBlank element (#658) attempts the currently modelled root-counter event delivery by default
(`BiosTimerVblankIrqHandler.DeliverEvents`, the default of `BiosRootCounterEventDelivery`).

- **Mapping (CONFIRMED, psx-spx event-summary).** C0:0A `t` → event: t=0 Timer0/IRQ4 →
  `F2000000h,2`; t=1 Timer1/IRQ5 → `F2000001h,2`; t=2 Timer2/IRQ6 → `F2000002h,2`; t=3
  VBlank/IRQ0 → `F2000003h,2`. That these handlers are the ones delivering them is INFERRED (#660).
- **Contract.** Delivery is B0:07 `DeliverEvent(F2000000h + t, 2)`: it marks the EvCBs matching
  class and spec (psx-spx). An EvCB lives only in the kernel's EvCB table (psx-spx "table of tables":
  `[0x120]` address, `[0x124]` size). A BIOS-less run has no table (both words 0), and the Runtime
  registers no event opener (B0:08 OpenEvent is unregistered, so a call to it stops the run), so
  nothing can match: the delivery succeeds and has no effect (INFERRED from the two facts above).
  It writes nothing, seeds no table and creates no second event or interrupt state.
- **Fail closed.** An unreadable table, a table that exists (either word non-zero: guest state that
  would need EvCB matching and callbacks, #687) or `t > 3` is a failed delivery:
  `BIOS_EXCEPTION_CHAIN_UNSUPPORTED` naming the source, its flag and the event; the IRQ is not
  acknowledged and the table is not corrected.
- **#658 unchanged.** The C0:0A flag is still validated first; after a successful delivery flag 0 =
  no acknowledge, no return, the chain continues; flag 1 = W0C-acknowledge only that IRQ and
  `ReturnedFromException` (hook and lower priorities skipped).
- **UNKNOWN (not guessed).** Whether the real handler delivers only when an EvCB exists or checks
  conditions other than I_MASK; how a mode-1000h callback runs (execution context, re-entry);
  real-hardware side effects of delivering an event nobody opened; repeat-delivery semantics while an
  event is already ready. EvCB matching and the event functions are #687, taken up when measured.
- **Scope.** The step is a no-op when no EvCB table exists; it matches no EvCB and runs no callback (mode 1000h included). It does not resolve Pad/Card ownership (#661), the EvCB allocator or B0:17/B0:18 (#664, #665), DMA2 or GTE.
- **Measured (Persona).** The VBlank IRQ0 element's currently modelled delivery step is a successful no-op (no EvCB table, flag 0); the chain then continues past priority 1 with IRQ0 still pending, and the
  stop moves to the source-neutral `DefaultChain` diagnostic
  (`docs/v0.1.0/persona-e2e-status.md`, item 23).

Tests: `BiosTimerVblankIrqHandlerTests` (mapping, flag 0/1 after delivery, fail-closed table states),
plus the updated chain assertions in `BiosExceptionHandlerTests`, `KernelExceptionEntryTests` and
`RecompiledArtifactInterruptTests`.

## Amendment (2026-10-05): priority-3 DefInt completes the chain into the B0:19 hook

Issue #690: after #660 the Persona run stopped at `BIOS_EXCEPTION_CHAIN_UNSUPPORTED` with IRQ0 still
pending past priority 1, although the guest had registered a B0:19 hook and set `C0:0A(3,0)` itself.

- **CONFIRMED (psx-spx interrupt-exception-handling).** The hook runs only when the exception handler
  ran to the end (an element that calls ReturnFromException skips it). Priority 3 is `DefInt`, which
  delivers a default IRQ event (`F0000001h,1000h` for IRQ0) and does not acknowledge unless
  `C(0Dh) SetIrqAutoAck` enabled it ("By default, AutoAck is disabled for all IRQs"). `PadCardIrq`
  (priority 2) is enqueued by StartPAD2/StartCARD and not by InitPAD2.
- **Model.** `DefaultChain` runs priority 1, skips priority 2 (empty: the Runtime registers neither
  StartPAD/StartCARD nor C0:02), then runs `BiosDefaultInterruptHandler`. Nothing pending, or exactly
  IRQ0 pending and enabled with no EvCB table (`[0x120]`/`[0x124]` = 0, the #660 no-op delivery),
  completes the chain; the existing `BiosExceptionCompletion` then enters the B0:19 hook (`$v0 = 1`,
  PC = saved `$ra`) or takes the default Exit. DefInt does not acknowledge: the hook does.
- **Fail closed (unchanged diagnostic).** Any other pending enabled IRQ, several, or an existing EvCB
  table (matching and the 1000h callback mode are #687) stop as `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`.
- **C0:0D.** Not registered, so DefInt's per-IRQ auto-ack can only hold its documented default
  (disabled); `BiosDefaultInterruptHandler.DefaultAutoAck` is the seam a future C0:0D replaces.
- **UNKNOWN (not guessed).** Retail DefInt event behaviour without an EvCB, `$k0/$k1` and SR on hook
  entry, priority-0 owners, the full C0:0D.
- **Measured (Persona).** The artifact enters the hook and the guest's own dispatcher acknowledges IRQ0.
  With the established two entry roots the run then stops at `UNRESOLVED_TRANSFER_IN_IMAGE`
  `0x80025BC8` (the guest's callback; a manual-root / callback coverage gap, #693); with the
  measurement-only `--entry-root 0x80025BC8` it reaches `B0:17` (#664)
  (`docs/v0.1.0/persona-e2e-status.md`, item 24).

Tests: `BiosDefaultInterruptHandlerTests`, plus the updated chain assertions in
`BiosExceptionHandlerTests` and `BiosTimerVblankIrqHandlerTests`.

## Amendment (2026-10-06): mixed execution supersedes decision 3(f) for in-image indirect targets (#693)

Decision 3(f) above said that mixing execution engines mid-run "is a separate design decision nobody has taken". Issue #693 takes it, for one class only: an unresolved transfer to an in-image, 4-byte-aligned PC reached by a register-indirect jump (ADR-012, amendment #693). The gate is the engine's explicit `MixedFallbackOptions` (a null option means disabled); `psxrecomp run` supplies it by default since 2026-10-07 and `--no-mixed-fallback` withholds it. Decision 3's other statements stand.

- The host hook is still generic: `HostTransferBridge` consults the BIOS vector tables first, then the kernel exception vector (#662/#680), and only then, if mixed execution is enabled, offers the transfer to the fallback session. A BIOS vector or an exception vector is **never** a fallback case (the Runtime's own diagnostic stands).
- A patched jump-table target whose address has no generated block (`BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK`, #379) keeps its advisory: it is a *BIOS-vector* flow, not an indirect-jump target, and this amendment does not change it. Open design question (r) (how a `PatchedTarget` falls back to raw guest execution) stays open; a mixed-execution target is a different mechanism.
- The Runtime is unchanged: the interpreter loop calls the same `BiosVectorDispatch`, `BiosKernelSyscallDispatch` and `BiosExceptionHandler` over the host graph core's RAM (guest state in RAM, ADR-025 amendment). An unregistered BIOS call inside a fallback segment stops the run with the same `BIOS_HLE_UNSUPPORTED_CALL` as outside it.
- Registration is not a Runtime concept here: the guest's callback table is ordinary guest RAM and the Runtime has no registration event, which is why the target is discovered when it is *entered*, not when it is stored.

## Amendment (2026-10-06): B0:17 ReturnFromException registered

Issue #664: after #693 the Persona production run crossed the VBlank callback through the mixed
fallback and stopped at `BIOS_HLE_UNSUPPORTED_CALL` `B0:17`. B0:17 is not a call the guest returns
from — it *is* the kernel's exception-return operation — so registering it required a result shape
the contract did not have.

- **The restore is the existing source of truth.** `BiosExceptionCompletion.TryReturnFromException`
  (the #662 completion, already used by the exception handler's hook/exit path) reads the current
  TCB through `[0x108]`→PCB→TCB and produces the restored Hi/Lo/SR/PC. B0:17's handler only shapes
  that into a `BiosCpuStateMutation`; it cannot restore a different subset than the exception path.
- **One CPU-state replacement for every execution form.** `BiosCpuStateMutation` (Gpr, Hi, Lo,
  RestoredSr, NextPc) is the outcome of a service that replaces the machine instead of returning
  from the call: `$v0` comes from the restored register file, and continuation is the saved EPC,
  not `$ra`. `BiosServiceResult` carries it (exclusive of `ReturnValue`); interpreter, generated
  host and mixed fallback all apply it through one implementation each (`BiosCpuStateMutation.ApplyTo`
  on the core; `RecompiledHostExecutionEngine.ApplyCpuState` over the artifact's G/H/C/RFE wire
  commands, then the interrupt line as the restored SR judges it).
- **RFE stays the CPU's own.** `RestoredSr` is written and the native exception-stack pop
  (`PSXCore_PopExceptionSrStack` / the artifact's RFE) follows; EPC/CAUSE are deliberately not
  carried, because RFE does not rewrite them. Nothing re-implements RFE in managed code.
- **Fail closed.** A wrong argument count is `BIOS_HLE_INVALID_ARGUMENTS`; a call carrying no live
  register file, or a TCB that cannot be read in full through the table of tables, is
  `BIOS_HLE_UNSUPPORTED_STATE` and restores nothing.
- **B0:18 ResetEntryInt stays unregistered** (#665): a separate operation, not a case of this one.
- **Measured (Persona).** With the established entry roots
  (`--entry-root 0x80025350 --entry-root 0x80025614 --mixed-fallback`) the B0:17 stop is gone: the
  run crosses the callback and ReturnFromException, resumes at the EPC after RFE, and the new stop
  is `OUTER_BUDGET_EXHAUSTED` with Vcount now advancing (the VBlank callback runs) instead of a
  permanent wait (`docs/v0.1.0/persona-e2e-status.md`, item 26).

Tests: `BiosReturnFromExceptionTests`, `MixedFallbackTests` (production generated-host path),
`KernelExceptionEntryTests` (interpreter regression).

## Amendment (2026-10-07): DefInt delivers the CD-ROM IRQ2 event

Issue #697: after #698 the Persona run stopped at `BIOS_EXCEPTION_CHAIN_UNSUPPORTED` with IRQ2 pending
(I_STAT=0x0004, I_MASK=0x000D).

- **CONFIRMED (PCSX-Redux OpenBIOS `IRQVerifier`, `common/kernel/events.h`; psx-spx).** DefInt (priority 3)
  delivers `EVENT_CDROM = F0000003h` with spec `1000h` when `I_STAT & I_MASK` has IRQ2, and acknowledges only if
  auto-ack is enabled for it. The retail priority-0 CD handlers are enqueued by the BIOS CD driver init, not by DefInt.
- **Model.** `BiosDefaultInterruptHandler` accepts exactly one of IRQ0 or IRQ2 pending and enabled; with no EvCB table
  the delivery is the same no-op as for IRQ0, the chain completes into the B0:19 hook (or the default Exit). Nothing is
  acknowledged by the Runtime: neither I_STAT nor the CD controller's own interrupt flag; the guest's callback does both.
- **Priority 0 is empty in this kernel (INFERRED design position).** A BIOS-less run never executes the BIOS CD driver
  init and C0:02 SysEnqIntRP is unregistered (a guest enqueue stops the run), so no priority-0 element exists to claim IRQ2
  first. A0:72 CdRemove therefore still has nothing to change, consistent with the amendment above.
- **Fail closed.** Several pending IRQs (including IRQ0 + IRQ2), any other IRQ, or an existing EvCB table (#687) stop as
  `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`.
- **Measured (Persona).** The guest's IRQ2 callback runs and `CD_sync` completes for CdlNop and CdlInit; the next stop is
  CdlDemute being unimplemented in the CD-ROM device (#699; `docs/v0.1.0/persona-e2e-status.md`, item 29).

Tests: `BiosDefaultInterruptHandlerTests` (IRQ2 cases).

## Amendment (2026-10-07): BIOS services reach device registers through `IGuestDeviceAccess`

Issue #701: A0:49 `GPU_cw` is a register access (GP0 write after `GPU_sync`), which the memory reader/writer boundary
cannot express (it serves RAM only).

- **Boundary.** `IGuestDeviceAccess` (32-bit physical read/write) is implemented by `PsxDeviceGraph`, the one device graph
  every backend shares. `IDeviceBiosRuntime.AttachDevices` is called by the execution backend that owns the graph (the
  interpreter engine, which also serves mixed-execution fallback, and the generated-host bridge) right after the factory
  built the runtime; the factory signature is unchanged. It never advances device time.
- **Fail closed.** A service that needs devices and has none attached, a register the graph does not model, or a wait
  (`GPU_sync`) that is not already satisfied returns `BIOS_HLE_UNSUPPORTED_STATE`; the retail timeout/abort path is not modelled.
- **A0:49.** `GPU_cw(cmd)`: `GPU_sync`, then GP0 write, return 0 (OpenBIOS `gpu.c`).

Tests: `BiosGpuCommandServiceTests`.
