# ADR-014: BIOS HLE Calls Cross a Shared Runtime Contract

- **Status**: Accepted (amended 2026-09-09, 2026-09-10, 2026-09-11 (x5) — see below)
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
