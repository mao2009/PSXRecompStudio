# BIOS HLE Evidence and Next-Service Inventory

**Status:** Draft

**Authority:** Reference

**Related Issues:** #279, #225, #11

## 1. Purpose

Issue #279 makes BIOS-less execution the default user path: recompiled software
must reach BIOS services through a shared Runtime/HLE boundary instead of a
required Sony BIOS image. The Runtime abstraction already exists
(`IBiosRuntime`, `BiosCallIdentity`, `BiosServiceResult`) and holds one narrow
Phase-1 registration (ADR-014). This document is the **evidence-driven inventory
for the next BIOS HLE services** to add after the parallel Runtime capabilities
— the **guest-memory read boundary** and the **deterministic output sink** —
land.

It records three things and registers nothing:

1. the **actual** current support state of the HLE registry (section 2);
2. the **gap** between what real-ROM analysis can prove today and what it must
   record to prioritize HLE services from evidence (section 3);
3. a **candidate ranking** for the next services, grounded in the identities
   recorded in [docs/REFERENCES.md](../REFERENCES.md) (section 4).

This document implements no service and changes no contract. It exists so the
next implementation task picks its service from evidence rather than from
convenience, exactly as Issue #279 requires ("Actual priority must be
evidence-driven from #225 and later full-title bring-up").

## 2. Current support matrix

State of `PSXRecomp.Core.Runtime.BiosHleRuntime` as of this document. The
registry is a dictionary keyed by `(BiosCallFamily, byte)` holding **exactly one
entry**: `(A0, 0x3C)` → `InvokePutChar`. Every other call — including the
deliberately unregistered neighbours and aliases below — falls through to
`BiosServiceResult.Unsupported`, producing the stable diagnostic
`BIOS_HLE_UNSUPPORTED_CALL` (verified in `BiosHleRuntime.cs:30-45` and
`BiosServiceResult.cs:34-44`).

| Service | A0/B0 identity | Current state | Missing semantics | Blocked on (Runtime capability) |
|---|---|---|---|---|
| getchar | A0:3B | **Unregistered** — falls through to `BIOS_HLE_UNSUPPORTED_CALL` | TTY input read/consume; blocking wait for input | Input sink (no design yet) |
| putchar | A0:3C | **Registered `Supported` (Phase-1 limited)** — `InvokePutChar` models only the register-visible return-value contract (`arg & 0xFF`) | Documented TTY output side effect not implemented (Issue #279 "putchar TTY side effect implementation") | Output sink |
| gets | A0:3D | **Unregistered** — `BIOS_HLE_UNSUPPORTED_CALL` | TTY line input into a guest-memory buffer | Input sink; guest-memory **write** |
| puts | A0:3E | **Unregistered** — identity verified and cited (ADR-014, REFERENCES.md) but deliberately not registered | Reading a NUL-terminated string from guest memory; writing it to the TTY; returning the incoming pointer | Guest-memory **read**; output sink |
| putchar alias | B0:3D | **Unregistered** — no evidence selects the B0 entry | Same as A0:3C | Output sink; plus evidence that B0 is actually used |
| puts alias | B0:3F | **Unregistered** — no evidence selects the B0 entry | Same as A0:3E | Guest-memory read; output sink; plus B0 usage evidence |
| all other A0/B0/C0 | — | **Unregistered** — any call not in the registry fails loudly via `BIOS_HLE_UNSUPPORTED_CALL`; no dummy success is returned | Per-service | Per-service |

Notes verified against the code:

- `BiosHleRuntime.PutCharFunction == 0x3C`; the only registration is
  `(BiosCallFamily.A0, PutCharFunction)` (`BiosHleRuntime.cs:13-35`).
- `InvokePutChar` rejects any argument count ≠ 1 with
  `BIOS_HLE_INVALID_ARGUMENTS` and otherwise returns
  `Supported(identity, identity.Arguments[0] & 0xFFu)` (`BiosHleRuntime.cs:47-60`).
  No TTY output is emitted.
- `BiosHleContractTests` pins A0:3B / A0:3D / A0:3E / A0:3F / B0:3D / B0:3F and a
  non-contiguous A0:09 as `BIOS_HLE_UNSUPPORTED_CALL`, guarding against any
  accidental widening of the registry
  (`BiosHleContractTests.cs:36-110`).
- ADR-014 explains the *why*: a service whose documented effect depends on guest
  memory the Runtime cannot yet read (or output it cannot yet emit) must stay
  **absent** from the registry rather than be registered with only its return
  value modeled, because a differential test would then report a false green.

## 3. Evidence gap (real-ROM → BIOS usage)

### 3.1 What the analysis pipeline records today (verified, not assumed)

The real-ROM pipeline produces a `DiscImageAnalysisReport`
(`DiscImageAnalysisReport.cs`) whose decoded-instruction stream is the only
place a `syscall` can surface. The relevant records carry:

- `DecodedInstruction` — `Address`, `RawWord`, `Mnemonic`, `Operands`,
  `Format`, `ControlFlow` (`DecodedInstruction.cs:9-17`). A decoded syscall gets
  `Mnemonic = "syscall"`, `Operands = ""`, `ControlFlow = "Trap"`.
- `BasicBlock` / `CfgEdge` — structural flow only (`BasicBlock.cs`,
  `Analysis/Contracts/CfgEdge.cs`).
- `FunctionDiscoveryArtifact` — `DiscoveredFunction` records
  `DirectCallTargets`, `ReturnAddresses`, `UnresolvedIndirectSources`
  (`FunctionDiscovery.cs:20-30`).
- Persisted artifacts (#215): `manifest.json` counts, `report.json` histogram
  mixes (`mnemonicMix`, `formatMix`, `controlFlowMix`), `instructions.json`
  per-instruction records, `cfg.json` blocks/edges
  (`AnalysisArtifacts/AnalysisReportDocument.cs`,
  `AnalysisArtifacts/InstructionListDocument.cs`).

**Does any stage detect syscall instructions or record guest call-sites into
the BIOS jump tables? No.** The decoder classifies `syscall`/`break` as
`R3000aControlFlowKind.Trap` (`R3000aDecoder.DecodeTrap`, `R3000aDecoder.cs:369-380`)
but **drops the trap code field entirely** — the constructed instruction has
`operandCount: 0` and all-default operands. Consequence, each verified in code:

- **No call-site PC list.** No stage emits the guest PC of a `syscall` as a
  first-class record. `instructions.json` happens to carry `address` + `mnemonic`,
  so sites are *recoverable* by re-scanning, but nothing aggregates or names them.
- **No identity frequency.** `DecodeTrap` discards the 20-bit code that the PS1
  ABI uses to select the A0/B0/C0 entry, so **no A0/B0/C0 + function number is
  ever derived**. `CountCallReturnCandidates` counts only `LinkInfo.WritesLink`
  and `JR $ra` (`RomAnalysisPipeline.cs:536-558`); a syscall writes no link, so
  it is counted as neither a call nor a return. `FunctionDiscovery.IsCall` is
  likewise `LinkInfo.WritesLink` (`FunctionDiscovery.cs:179`), so a syscall is
  not a `DirectCallTarget`, not a `ReturnAddress`, and not an
  `UnresolvedIndirectSource` (only `JumpRegister` qualifies,
  `FunctionDiscovery.cs:139`). The `report.json`/`manifest.json` schemas have no
  syscall count field at all.
- **No lowerer handling.** `MipsToIrLowerer` has no `Syscall`/`Break` case in
  either `TryEmitInstruction` or `TryGetSourceRegisters`; both fall to their
  `default` and return unsupported ("Opcode 'Syscall' is not supported by this
  lowering stage.", `MipsToIrLowerer.cs:715-793`).
- **No jump-table tracking.** Nothing resolves the A0/B0/C0 table base or maps a
  trap code to a table entry; `cfg.json` records an unresolved indirect edge
  target as `0x00000000` and nothing more.

`R3000aOpcode.Syscall = 58` and `R3000aOpcode.Break = 59` do exist
(`R3000aOpcode.cs:66-67`), and the candidate survey already treats them as
control-flow opcodes (`RealRomCandidateSurvey.cs:163-171`), but that survey is a
git-ignored local diagnostic, not the artifact schema and not a source of
BIOS-call identity.

### 3.2 What information is missing

1. **Call-site PC list** — the guest PCs of every decoded `syscall`, as a
   structured artifact.
2. **Identity frequency** — the A0/B0/C0 function number derived from each trap
   code, aggregated per function (what #279 calls "syscall/function identity").
3. **Lowerer visibility** — `syscall`/`break` are currently opaque-to-unsupported
   in `MipsToIrLowerer`; a future HLE interception point needs them either lowered
   to a Runtime call or explicitly classified as external (BIOS) dependencies.
4. **Jump-table target tracking** — for real code that dispatches through the
   A0/B0/C0 tables *indirectly*, which call-site analysis cannot see at all
   without runtime/emulation data.

### 3.3 Minimal addition that would collect it

The smallest evidence-collecting change is inside the existing decode stage:
when the decoder classifies `R3000aControlFlowKind.Trap`, extract the trap code
from the raw word (bits `[25:6]`, i.e. `(raw >> 6) & 0xFFFFF`) and record
`(GuestPc, TrapCode)` per site. Concretely:

- add a `MIPS_DECODE` / `BASIC_BLOCK` extension that emits a small
  `DecodedSyscall { Address, TrapCode }` list into `DiscImageAnalysisReport`;
- first-class it in the #215 artifact schema (`report.json` count + a
  `syscallSites.json`-style document), which is a **schema version bump** per the
  artifact-policy rules in `docs/development/real-rom-analysis-artifacts.md`;
- optionally aggregate it in `RealRomCandidateSurvey` (which already lists
  `Syscall`/`Break` as control-flow opcodes and could report
  syscall-frequency stop-details without any selector change).

Because the PS1 ABI maps a `syscall` trap code to an A0/B0/C0 table entry, this
one field turns the existing decode stream into a **bios-call candidate list**
without running anything: every site's identity becomes knowable at analysis
time. That directly serves the lowered Runtime boundary: an identified call site
is either (a) satisfied by a registered HLE service, or (b) asserted to be
BIOS-independent and safe to recompile — per #225 the candidate must have **no
BIOS dependency**, and today exclusion is the only option.

### 3.4 Connection to #225 and future bring-up

ADR-013 defines the current contract: candidate selection accepts only
instruction windows the existing `MipsToIrLowerer` lowers, and *explicitly*
excludes unresolved indirect flow and anything that does not lower
(`RealRomCandidateSelector`, `RealRomCandidateStopReason`). A real-ROM function
containing a `syscall` therefore stops at
`UnsupportedInstruction`/an indirect-external boundary today, so:

- **BIOS-independent candidates succeed today** — which is exactly what #225's
  first real-ROM function is: a bounded window that never touches a syscall.
- **BIOS-touching code is excluded until HLE exists** — no silent workaround, no
  title-specific carve-out, per the #225 policy ("BIOS/syscall… 対象関数が以下へ
  依存する場合は、黙ってworkaroundを埋め込まない").
- The moment call-site recording (3.3) lands, #225 gains a *machine-readable
  reason* for every rejection and future bring-up can prioritise HLE services by
  counting which trap codes actually appear — the evidence loop Issue #279 asks
  for ("Initial BIOS HLE subset is selected from real usage evidence").

## 4. Next-service recommendation (evidence-based)

Rating scale for this table: **External dependency** is `host` (a Runtime
capability or host I/O is required to honour the documented effect), `guest`
(the service only touches guest registers/state), or both. **Testability** is
the ease of building a deterministic, fixture-free differential/contract test.
**Real-ROM evidence availability** is whether the site can be proven from
analysis artifacts today (before 3.3 lands, no syscall identity is recorded at
all, so everything is currently inferred). Identities for putchar/puts are
verified and cited in `docs/REFERENCES.md`; getchar/gets are used in
`BiosHleContractTests` with A0:3B/A0:3D but are **not yet re-verified** against
the documentation, so they carry a "verify before register" marker.

| Service | Documented identity | Arguments | Return | Side effects | External dependency (host/guest) | Testability | Real-ROM evidence availability | Implementation difficulty |
|---|---|---|---|---|---|---|---|---|
| putchar TTY completion (completes A0:3C) | A0:3C `std_out_putchar(char)` (B0:3D alias) | 1 char word (`arg & 0xFF`) | the character | write char to TTY output sink | **host** — output sink only | High (deterministic sink assertable) | High — scalar call, static-site detectable once trap codes are recorded | Low |
| puts | A0:3E `std_out_puts(src)` (B0:3F alias) | 1 pointer to NUL-terminated guest string | the incoming string-pointer | read guest string; write to TTY; return pointer | host + **guest read** — guest-memory read boundary + output sink | High once read+sink exist (differential: stub reads, compare output + R2) | High — identity already verified (ADR-014) | Low–medium |
| getchar | A0:3B (needs doc verification) | none | the character (with wait) | read/consume TTY input; **blocking** wait when empty | host — input sink | Low (blocking; determinism needs a designed input sink) | Medium — call-site detectable, **but** real titles rarely use TTY input | Medium–high |
| gets | A0:3D (needs doc verification) | 1 pointer to guest buffer | to be verified before registration | read a TTY input line into guest memory (NUL-terminated) | host + **guest write** — input sink + guest-memory write | Low (no input sink design; needs guest-memory write too) | Medium | High |
| B0 putchar/puts aliases | B0:3D / B0:3F | same as A0 counterparts | same | same, through the B0 table | host (+ guest read for puts) | High (once the capability exists) | **Low** — no evidence selects the B0 entries; keep unregistered | Low (capability-gated) |

### Recommendation

After **Track A (guest-memory read boundary)** and **Track B (deterministic
output sink)** land, implement in this order:

1. **putchar TTY completion (A0:3C)** — needs *only* the output sink. It is the
   smallest possible completion of the one registered service, it stays on a
   scalar contract (no guest read), and it upgrades `Supported` to the stricter
   ADR-014 reading (full documented behavior including host-visible output).
2. **puts (A0:3E)** — needs guest-memory **read** + output sink; its identity is
   already verified, so registration becomes a Runtime-capability change rather
   than fresh identity research (ADR-014's stated intent). Registering it also
   retires the one documented "deliberately unregistered" neighbour, shrinking
   the loud-but-incomplete surface for TTY output.
3. **getchar/gets** — deferred until the **input sink design is decided**.
   getchar adds blocking semantics; gets additionally needs guest-memory write.
   Neither is on the critical path for a first BIOS-touching real-ROM function
   (games overwhelmingly *write* to TTY via putchar/puts; TTY input is rare).
4. **B0:3D/B0:3F** — register only when real-ROM evidence shows a B0-table call
   site; no evidence selects them today (ADR-014).

Rationale in one line: putchar completion costs only the output sink, puts costs
one read boundary plus that sink, and everything else costs an input sink whose
design does not exist yet — so the evidence- and cost-minimal next services are
putchar completion, then puts.

## 5. Dependency map

```text
BIOS HLE service (BiosHleRuntime.Invoke)
        │
        ├── Track A: guest-memory reader (guest-memory read boundary)
        └── Track B: deterministic output sink
                │
                └── future Studio/CLI host adapters (consume the sink, supply input)
```

The boundary relationship is directional: a BIOS HLE service may consume the
guest-memory reader (to honour reader semantics such as `puts`) and/or the
output sink (to honour host-visible effects such as putchar's TTY write). The
services never reach into host I/O or file/console APIs directly — the Domain
layer stays pure (`docs/runtime/architecture.md`, `docs/architecture/README.md`),
and the adapters live on the host side the Domain layer cannot see.

Two explicit guarantees:

- **`puts` must NOT be registered as `Supported` until both boundaries exist**
  (guest-memory read **and** output sink). Registering it earlier with only its
  return value would let an invalid/unmapped pointer silently succeed where real
  hardware faults, hiding a correctness gap — the exact rejection rationale
  recorded in ADR-014.
- **This document registers nothing.** It is an inventory; the registry in
  `BiosHleRuntime` remains `(A0, 0x3C)` only, and any service added later is a
  separate implementation task that adds code and updates ADR-014/ARCHITECTURE.md
  as appropriate.

## 6. Open questions / next steps

1. **Add syscall recording to the analysis artifacts** — implement 3.3 (trap-code
   extraction + a first-class artifact field), schema-bumping per the #215 rules.
2. **Pick the first real-ROM function that exercises an A0 call** — once call
   sites are recorded, select a title-local function for puts/putchar to drive
   the next HLE registration from evidence rather than from the synthetic
   fixtures alone.
3. **Decide the input sink design for getchar/gets** — blocking semantics,
   host-provided input source, and how the Domain layer receives it without I/O.
4. **Re-verify getchar/gets identity** (A0:3B / A0:3D) against the documented
   std_io behavior and record it in `docs/REFERENCES.md` before any register work,
   per the no-guessing rule of ADR-014.
5. **Re-audit `Supported` against the strict reading once the output sink lands**
   — ADR-014's open item: every registered service must satisfy full documented
   behavior (including host-visible output), not just the ABI return contract.