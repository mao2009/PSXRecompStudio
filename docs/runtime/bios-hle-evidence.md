# BIOS HLE Evidence and Next-Service Inventory

**Status:** Draft

**Authority:** Reference

**Related Issues:** #279, #225, #11

## 1. Purpose

Issue #279 makes BIOS-less execution the default user path: recompiled software
must reach BIOS services through a shared Runtime/HLE boundary instead of a
required Sony BIOS image. The Runtime abstraction already exists
(`IBiosRuntime`, `BiosCallIdentity`, `BiosServiceResult`) and holds three
registrations — A0:3C `putchar`, A0:3E `puts`, and its real-ROM-evidence-selected
B0:3F alias — each implementing its full documented behavior, host-visible
output included (ADR-014). Both parallel
Runtime capabilities this
document originally awaited have since landed: the **guest-memory read
boundary** (`IGuestMemoryReader`, PR #350) and the **deterministic output
sink** (`IRuntimeOutputSink`, PR #349, ADR-014 amendment 2026-09-10). This
document is the **evidence-driven inventory for the next BIOS HLE services**
that can now be built on top of them.

It records three things and registers nothing:

1. the **actual** current support state of the HLE registry (section 2);
2. what real-ROM analysis can prove about BIOS usage — originally a **gap**, now
   closed on the Analysis side, together with the **evidence actually obtained**
   (section 3);
3. a **candidate ranking** for the next services, grounded in the identities
   recorded in [docs/REFERENCES.md](../REFERENCES.md) (section 4).

The evidence loop Issue #279 asks for is now closed end to end: analysis recognizes
A0/B0/C0 call sites, records them deterministically in `report.json`, and the resulting
call frequencies — not a static candidate table — are what section 4 reasons from.

This document implements no service and changes no contract. It exists so the
next implementation task picks its service from evidence rather than from
convenience, exactly as Issue #279 requires ("Actual priority must be
evidence-driven from #225 and later full-title bring-up").

## 2. Current support matrix

State of `PSXRecomp.Core.Runtime.BiosHleRuntime` as of this document. The
registry is a dictionary keyed by `(BiosCallFamily, byte)` holding **exactly
five entries**: `(A0, 0x3C)` → `InvokePutChar`, `(A0, 0x3E)` → `InvokePuts`,
`(B0, 0x3F)` → `InvokePuts`, `(B0, 0x56)` → `InvokeGetC0Table`, and
`(B0, 0x57)` → `InvokeGetB0Table`. The first three are a thin delegation to the same
`PutsService.Invoke`, registered under distinct identities rather than a
per-family copy of the service. Every other call — including the deliberately
unregistered neighbours and aliases below — falls through to
`BiosServiceResult.Unsupported`, producing the stable diagnostic
`BIOS_HLE_UNSUPPORTED_CALL` (verified in `BiosHleRuntime.Invoke` and
`BiosServiceResult.CreateUnsupported`).

Additionally, `BiosHleRuntime.Invoke` now consults guest-visible jump-table
state before reaching the registry. If the 4-byte slot at
`BiosJumpTables.EntryAddress(family, functionNumber)` holds a non-zero value
that is not that physical slot's own HLE sentinel (`BiosJumpTables.HleSentinelTarget`,
computed from the slot's canonical identity — see below), `BiosServiceResult.PatchedTarget`
is returned instead — carrying the raw patched guest address in `ReturnValue`
(ADR-014 amendment 2026-09-11 for #360). Each of the five registered slots is
seeded with that sentinel at construction time (a required `IGuestMemoryWriter`
constructor dependency) **only when that slot currently reads as zero** — a
pre-existing guest patch or a save-state's restored content is never
overwritten (ADR-014's 2026-09-11 CodeRabbit follow-up amendment). A slot that
does read as zero observes a real, non-zero, deterministic sentinel rather
than 0, and can save/patch/restore it with dispatch correctly following each
state. Slots with no registered service are unaffected and remain zero by
default.

Registry lookup and sentinel recognition both key off a call's **canonical
physical-slot identity**, not its logical `(family, function)` pair:
`BiosJumpTables.CanonicalizeIdentity` maps a C0 call with `functionNumber >= 0x80`
to its mirrored B0 identity (`functionNumber - 0x80`; psx-spx documents
`C(80h.....) N/A ;mirrors to B(00h.....)`, real hardware dispatching C-function
numbers 0x80+ through the same jump-list memory as B-function numbers 0x00+),
and every other call to itself. A registered B0 service is therefore reachable
through its C0 high-range alias exactly as through the direct B0 call — the
`BiosCallIdentity` passed to that service, and reported in the result and
diagnostic, is always the identity the guest actually invoked, never the
canonical one (ADR-014's 2026-09-11 CodeRabbit follow-up amendment).

**Observed / Verified / Implemented, kept distinct:**

```text
Observed (Analysis, §3.4):
  B0:3F puts — real-ROM call site, guest PC 0x800D0FF8, SLPM_869.24

Verified (docs/REFERENCES.md, BiosCallNames):
  A0:3C/B0:3D putchar, A0:3E/B0:3F puts,
  A0:39 InitHeap, A0:AB _card_info, A0:AC _card_load,
  B0:4E _card_write, B0:50 _new_card, B0:56 GetC0Table, B0:57 GetB0Table

Implemented (BiosHleRuntime registry):
  A0:3C putchar, A0:3E puts, B0:3F puts, B0:56 GetC0Table, B0:57 GetB0Table

Previously Blocked, now Implemented (ADR-014 amendment 2026-09-11 for #360):
  B0:56 GetC0Table — now registered; returns BiosJumpTables.C0TableAddress,
    backed by guest-visible RAM connected to dispatch
  B0:57 GetB0Table — now registered; returns BiosJumpTables.B0TableAddress,
    backed by guest-visible RAM connected to dispatch
```

The four lists answer different questions and do not imply each other: an
identity can be Verified without ever being Observed in a local fixture (the
seven newly verified identities above), Observed without being Verified until
checked, or Verified and Observed without being Implemented (the four
remaining card identities above have no registered service — identifying them is
not a decision to build them, per ADR-014). `B0:56`/`B0:57` were previously
Blocked (evaluated and found to need a Runtime capability that did not exist);
that capability now exists — the guest-visible, dispatch-connected jump-table
state — so both are now Implemented (ADR-014 amendment 2026-09-11 for #360).

| Service | A0/B0 identity | Current state | Missing semantics | Blocked on (Runtime capability) |
|---|---|---|---|---|
| getchar | A0:3B | **Unregistered** — falls through to `BIOS_HLE_UNSUPPORTED_CALL` | TTY input read/consume; blocking wait for input | Input sink (no design yet) |
| putchar | A0:3C | **Registered `Supported` (full documented behavior)** — `InvokePutChar` writes `arg & 0xFF` to the injected `IRuntimeOutputSink` *and* returns it | None — TTY output is now emitted (ADR-014 amendment 2026-09-10) | None; `BiosHleRuntime` takes the sink by constructor injection |
| gets | A0:3D | **Unregistered** — `BIOS_HLE_UNSUPPORTED_CALL` | TTY line input into a guest-memory buffer | Input sink (no design yet) — guest-memory **write** now exists (`IGuestMemoryWriter`, ADR-014 amendment 2026-09-11 "Runtime guest-memory write boundary") but is unwired into `BiosHleRuntime` pending a consumer |
| puts | A0:3E | **Registered `Supported` (full documented behavior)** — `InvokePuts` delegates to `PutsService`, which reads the NUL-terminated string through `IGuestMemoryReader`, writes it to the injected `IRuntimeOutputSink`, and returns the incoming pointer | None — all three documented effects are implemented (ADR-014 amendment "A0:3E puts registered") | None; `BiosHleRuntime` takes both the sink and the reader by constructor injection |
| putchar alias | B0:3D | **Unregistered** — no evidence selects the B0 entry | Same as A0:3C | Evidence that B0 is actually used (output sink already exists) |
| puts alias | B0:3F | **Registered `Supported` (full documented behavior)** — dispatches to the same `PutsService` as A0:3E, per the ADR-014 amendment *B0:3F registered* (2026-09-11) | None — selected by real-ROM evidence ([3.4](#34-real-rom-evidence-obtained)) and now implemented | None; registered |
| GetC0Table | B0:56 | **Registered `Supported`** — returns `BiosJumpTables.C0TableAddress` (`0x674`); that address is backed by guest-visible RAM connected to dispatch (ADR-014 amendment 2026-09-11 for #360) | None for the documented behavior (return table base; base is now real and patchable) | None; registered. Note: `0x674` is this Runtime's design choice, not a primary-source-confirmed real-hardware fact — see ADR-014 amendment for #360 |
| GetB0Table | B0:57 | **Registered `Supported`** — returns `BiosJumpTables.B0TableAddress` (`0x874`); same backing guarantee as GetC0Table | None | None; registered. Same caveat on `0x874` |
| all other A0/B0/C0 | — | **Unregistered** — any call not in the registry fails loudly via `BIOS_HLE_UNSUPPORTED_CALL`; no dummy success is returned | Per-service | Per-service |

Notes verified against the code:

- `BiosHleRuntime.PutCharFunction == 0x3C`, `PutsFunction == 0x3E`,
  `PutsAliasFunction == 0x3F`, `GetC0TableFunction == 0x56`, and
  `GetB0TableFunction == 0x57`; the five registrations are
  `(BiosCallFamily.A0, PutCharFunction)`, `(BiosCallFamily.A0, PutsFunction)`,
  `(BiosCallFamily.B0, PutsAliasFunction)`, `(BiosCallFamily.B0, GetC0TableFunction)`,
  and `(BiosCallFamily.B0, GetB0TableFunction)`; the first three bind to
  `InvokePuts`/`PutsService.Invoke`; the last two bind to `InvokeGetC0Table`/
  `InvokeGetB0Table` respectively.
- `InvokePutChar` rejects any argument count ≠ 1 with
  `BIOS_HLE_INVALID_ARGUMENTS`; otherwise it writes the low byte
  (`identity.Arguments[0] & 0xFFu`) to the injected `IRuntimeOutputSink` and
  returns that same byte via
  `Supported(identity, identity.Arguments[0] & 0xFFu)` (`BiosHleRuntime.InvokePutChar`).
  The TTY output side effect is emitted; the rejection path writes nothing to
  the sink. The sink is a required constructor dependency
  (`ArgumentNullException` on null), so it can never be a silent no-op.
- `BiosHleContractTests` pins A0:3B / A0:3D / A0:3F / B0:3D / B0:3E / B0:40 and a
  non-contiguous A0:09 as `BIOS_HLE_UNSUPPORTED_CALL`, guarding against any
  accidental widening of the registry; it pins putchar's emitted byte, its call
  ordering, its determinism across fresh runtimes, all three required constructor
  dependencies (sink, reader, writer), and the untouched sink on the invalid-argument path; and it pins
  puts's dispatch end to end for both A0:3E and its B0:3F alias — the guest
  string reaching the sink, the returned pointer, an unmapped pointer failing
  loudly with zero bytes written, the two identities producing observably
  equivalent output while a failure diagnostic still names the identity that
  was actually invoked (not a hard-coded one), and all three services sharing
  one sink in call order. Neither A0:3E nor B0:3F is in the unsupported-neighbour
  set because both are now registered. It additionally pins (#360's Blocker 1
  fix): every registered slot reads as its own HLE sentinel — not zero —
  immediately after construction; an unregistered slot still reads as zero; the
  full read/save/patch/restore round trip re-enables the original handler; and
  (#360's Blocker 2 fix) the C0-high/B0 alias is live through dispatch in both
  directions, not just in address arithmetic. Following the PR #363 CodeRabbit
  follow-up (ADR-014's 2026-09-11 amendment), it further pins: seeding a
  registered slot never overwrites a pre-existing non-zero entry (guest patch
  or restored save-state), a reconstructed Runtime over already-patched memory
  observes that patch unchanged, and a registered B0 service (puts,
  GetC0Table, GetB0Table) is reachable through its C0 high-range alias with
  the same sentinel, the same patch behavior in both directions, and the
  original C0 identity preserved in the result/diagnostic.
- `PutsServiceTests` covers `PutsService` itself exhaustively (bounded scan,
  atomicity on every failure path, KUSEG/KSEG0/KSEG1 aliasing, uint-overflow
  rejection, determinism); the contract tests deliberately prove only the wiring
  rather than duplicating that coverage.
- ADR-014 explains the *why*: a service whose documented effect depends on guest
  memory the Runtime cannot yet read (or output it cannot yet emit) must stay
  **absent** from the registry rather than be registered with only its return
  value modeled, because a differential test would then report a false green.
  Both currently registered services clear that bar; the services still absent
  (getchar, gets, the B0 aliases) are absent for exactly that reason.

## 3. Evidence (real-ROM → BIOS usage)

> **Status of this section (updated).** The gap described below has been closed on the
> Analysis side. `BiosCallRecognizer`
> (`src/PSXRecomp.Core/DiscImage/BiosCallRecognition.cs`) now recognizes A0/B0/C0 call
> sites and resolves the R9/`$t1` function number, and the result is persisted in
> `report.json`'s `biosCalls` section (`report.json` schema version 2). Real-ROM evidence
> has been obtained — see [3.4](#34-real-rom-evidence-obtained). The text of 3.1–3.3 is
> kept as the record of what was missing and why, with the resolved items marked.

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

**Did any stage detect guest call-sites into the BIOS jump tables? Not when this
section was written — it does now** (`BiosCallRecognizer`, 3.4). PS1 BIOS A0/B0/C0
dispatch is an indirect jump through the jump table at `0xA0`/`0xB0`/`0xC0` — real code
materializes the vector into a register (typically `$t2`/R10) and uses `jr`/`jalr`
(a direct `j`/`jal` can reach only the KSEG0 alias `0x800000A0`, since J preserves the
top four PC bits; the recognizer accepts both forms) — with the function
number loaded into `$t1`/R9 via `li`/`addiu`/`ori`/`lui`, most often **in the jump's
delay slot**, which executes before control reaches the vector. The
MIPS `syscall` instruction is a separate kernel-trap interface, unrelated to
A0/B0/C0 dispatch, and its trap code bits `[25:6]` do **not** map to A0/B0/C0
function numbers. So trap-code extraction cannot yield BIOS call identity.

What the decoder and CFG builder actually record, each verified in code:

- `DecodeTrap` classifies `syscall`/`break` as `R3000aControlFlowKind.Trap`
  (`R3000aDecoder.DecodeTrap`, `R3000aDecoder.cs:369-380`), constructs the
  instruction with `operandCount: 0` and all-default operands, and drops the trap
  code field — but the encoded word is preserved in `R3000aInstruction`. This is
  factually true yet irrelevant to A0/B0/C0 identity, because BIOS dispatch does
  not go through `syscall`.
- **Direct jump resolution exists.** The CFG builder (`BasicBlockBuilder`)
  resolves direct jump/branch targets, so a direct `j 0xA00000B0` *would* be
  resolved; an indirect `jr $t9` creates an unresolved edge (`cfg.json` records
  the target as `0x00000000`).
- **No BIOS call is a `call`.** `FunctionDiscovery.IsCall` is
  `LinkInfo.WritesLink` (`FunctionDiscovery.cs:179`), i.e. JAL/JALR only; a
  `jr $t9` writes no link, so A0/B0/C0 sites are neither `DirectCallTarget`,
  `ReturnAddress`, nor `UnresolvedIndirectSource` (only `JumpRegister` qualifies,
  `FunctionDiscovery.cs:139`). `CountCallReturnCandidates` counts only
  `LinkInfo.WritesLink` and `JR $ra` (`RomAnalysisPipeline.cs:536-558`).
- ~~**No constant propagation / register tracking.**~~ — **resolved.**
  `BiosCallRecognizer` carries block-local, forward-only known-constant GPR values, which
  is what resolves both the vector register and the R9/`$t1` function number. It is not a
  general constant propagator and is not offered as one: it models only the immediate
  materialization forms a BIOS stub uses, and any other possible write to a tracked
  register makes it unknown again.
- **No lowerer handling.** `MipsToIrLowerer` has no `Syscall`/`Break` case in
  either `TryEmitInstruction` or `TryGetSourceRegisters`; both fall to their
  `default` and return unsupported ("Opcode 'Syscall' is not supported by this
  lowering stage.", `MipsToIrLowerer.cs:715-793`). The candidate selector treats
  syscall/break as `UnsupportedInstruction`.

`R3000aOpcode.Syscall = 58` and `R3000aOpcode.Break = 59` do exist
(`R3000aOpcode.cs:66-67`), and the candidate survey already treats them as
control-flow opcodes (`RealRomCandidateSurvey.cs:163-171`), but that survey is a
git-ignored local diagnostic, not the artifact schema and not a source of
BIOS-call identity.

### 3.2 What information is missing

1. **A0/B0/C0 target recognition** — recognizing direct jumps/calls in the CFG
   whose target is the BIOS jump-table base `0xA0`/`0xB0`/`0xC0`. The
   `BasicBlockBuilder` already resolves direct jump targets, so this is reachable.
2. **R9/t1 function-number resolution** — tracking what constant was loaded into
   `$t1`/R9 before a BIOS-table jump, i.e. the function number for the A0/B0/C0
   entry being dispatched. No constant propagation or register tracking exists
   anywhere today.
3. **Call-site aggregation** — linking a BIOS-table jump (direct or via `$t9`)
   with its function number from R9/t1, so a site becomes a
   `(Family, FunctionNumber)` identity.
4. **Indirect dispatch tracking** — for real code that loads the A0/B0/C0 table
   entry into a register and jumps indirectly (`jr $t9`); this needs resolution
   through the preceding load, not just recognition of the jump itself.
5. **Lowerer visibility** — `syscall`/`break` are currently opaque-to-unsupported
   in `MipsToIrLowerer`; BIOS-touching code stops at the lowerer boundary, and a
   future HLE interception point needs A0/B0/C0 sites either lowered to a Runtime
   call or explicitly classified as external (BIOS) dependencies.

### 3.3 Minimal addition that would collect it

The first-priority evidence is **A0/B0/C0 target recognition**: direct jumps in
the CFG whose target is `0xA0`/`0xB0`/`0xC0`. The `BasicBlockBuilder` already
resolves direct jump targets, so recognizing these jumps is a small, pure-CFG
addition. For indirect dispatch through `$t9`, a lightweight constant-propagation
or pattern-match on the preceding `li`/`addiu`/`ori`/`lui` into `$t1`/R9 resolves
the function number locally.

Proposed evidence model:

```
BiosCallSiteEvidence
- GuestPc
- Family: A0 / B0 / C0
- FunctionNumber (resolved or unresolved)
- Confidence / ResolutionKind
```

ResolutionKind values:

- `DirectJump` — direct `j`/`jal` to a known A0/B0/C0 address
- `LocalConstant` — function number resolved from an adjacent load into R9/t1
- `Unresolved` — indirect jump to A0/B0/C0 but function number not statically
  resolvable

This turns the existing CFG + decoded-instruction stream into a **bios-call
candidate list** without running anything: each site's family is knowable from
its target, and its function number becomes knowable at analysis time when the
preceding constant load is resolved. That directly serves the lowered Runtime
boundary: an identified call site is either (a) satisfied by a registered HLE
service, or (b) asserted to be BIOS-independent and safe to recompile — per #225
the candidate must have **no BIOS dependency**, and today exclusion is the only
option.

### 3.4 Real-ROM evidence obtained

This section records **observed** results, not projections. They come from running the
analysis pipeline over the disc images present on one developer machine under the
git-ignored `rom/` directory, with a decode window wide enough to cover the text segment
(the pipeline's own default window of 128 instructions is an entry-point probe and reaches
only the earliest stubs). No ROM, ISO or executable content is reproduced here; only the
metadata `docs/development/real-rom-analysis-artifacts.md` marks as safe to quote.

**A0/B0/C0 call sites are recognized in every locally available title.** Across five
distinct executables: 158 sites in total, 8 to 61 per executable, spanning all three
families. Every one resolved to a function number — 138 through the canonical stub shape
(`resolution: "DelaySlotConstant"`) and 20 from an earlier constant in the same basic
block (`BlockConstant`), with **zero** `Unresolved`. The dispatch idiom is evidently
uniform enough that block-local constant tracking is sufficient in practice; that is an
observation about these five executables, not a guarantee, which is why unresolved sites
remain a first-class part of the schema.

**The one verified identity observed, in full:**

| Field | Value |
|---|---|
| Identity | `B0:3F` — `std_out_puts(src)`, the B0-table alias of `puts` |
| Guest PC | `0x800D0FF8` |
| Resolution | `DelaySlotConstant` |
| Executable serial | `SLPM_869.24` |
| Executable SHA-256 | `831a6cceb94c88c9736f6df88a7fd9e08ff1261ca781b40b7e6ff449cf0fd24e` |

The recognized stub is the textbook shape, and is quoted here because it is the pattern
the recognizer models:

```text
0x800D0FF4  240A00B0  addiu $t2, $zero, 0x00B0   # the B0 vector
0x800D0FF8  01400008  jr    $t2                  # the call site
0x800D0FFC  2409003F  addiu $t1, $zero, 0x003F   # delay slot: function 0x3F
```

**Two findings that matter for registration, stated plainly:**

1. **No A0:3C and no A0:3E call site was observed in any locally available title.** The
   two services the registry implements today are not requested by any of them. This does
   not make the registrations wrong — they are correct, verified and tested — but it does
   mean the local fixture set does not exercise them.
2. **A B0-table call site does exist.** ADR-014 asserted in its base Decision and both
   registration amendments that the B0 aliases "stay unselected by evidence". That fact
   no longer holds for `B0:3F`, so it was corrected where it was decided, not here: see
   the ADR-014 amendment
   [*B0:3F selected by real-ROM evidence*](../adr/014-bios-hle-runtime-contract.md).
   `B0:3D` remains unselected. Registering the alias is still gated by ADR-014's bar (a
   service is registered only when it satisfies its full documented behavior); this
   document registers nothing, and the amendment registers nothing either — only the
   evidence precondition has changed.

**Update (2026-09-11): seven of these identities are now verified.** Cross-referenced
against the same source `docs/REFERENCES.md` already cites (psx-spx / Nocash), and now
recorded in `BiosCallNames`: `A0:39` `InitHeap(addr,size)`, `A0:AB` `_card_info(port)`,
`A0:AC` `_card_load(port)`, `B0:4E` `_card_write(port,sector,src)`, `B0:50` `_new_card()`,
`B0:56` `GetC0Table`, `B0:57` `GetB0Table`. `B0:0A` and `B0:4F` were **not** part of that
verification pass and remain unverified below. Verifying an identity is identification
only — it is not a decision to implement any of them as an HLE service; that remains
ADR-014's separate, evidence-and-prerequisite-gated decision (see §4 "Next candidates").

| Identity | Observed in (of the 5 distinct executables) | Sites per executable | Verified? |
|---|---|---|---|
| `A0:39` | 5 | 1 | ✅ `InitHeap(addr,size)` |
| `A0:AB` | 4 | 1 | ✅ `_card_info(port)` |
| `A0:AC` | 4 | 1 | ✅ `_card_load(port)` |
| `B0:0A` | 4 | 1 | Not verified |
| `B0:4E` | 4 | 1 | ✅ `_card_write(port,sector,src)` |
| `B0:4F` | 4 | 1 | Not verified |
| `B0:50` | 4 | 1 | ✅ `_new_card()` |
| `B0:57` | 3 | 4 | ✅ `GetB0Table()` |
| `B0:56` | 3 | 2–3 | ✅ `GetC0Table()` |

`B0:56` and `B0:57` are the only identities observed with more than one call site in a
single executable.

These remain call-frequency observations. A verified identity is not yet proposed as a
service — see §4's next-candidate ranking, which weighs frequency against criticality,
ABI complexity, and missing prerequisites rather than frequency alone.

### 3.5 Connection to #225 and future bring-up

ADR-013 defines the current contract: candidate selection accepts only
instruction windows the existing `MipsToIrLowerer` lowers, and *explicitly*
excludes unresolved indirect flow and anything that does not lower
(`RealRomCandidateSelector`, `RealRomCandidateStopReason`). A real-ROM function
that touches BIOS — via `jr $t9` to `0xA0`/`0xB0`/`0xC0`, an indirect edge the
CFG leaves unresolved — stops at an indirect-external boundary today, and a
`syscall`/`break` likewise stops at `UnsupportedInstruction`. So:

- **BIOS-independent candidates succeed today** — which is exactly what #225's
  first real-ROM function is: a bounded window that never touches a BIOS A0/B0/C0
  site.
- **BIOS-touching code is excluded until HLE exists** — no silent workaround, no
  title-specific carve-out, per the #225 policy ("BIOS/syscall… 対象関数が以下へ
  依存する場合は、黙ってworkaroundを埋め込まない").
- The real blocker for #225 is not "recording syscalls" but the inability to
  distinguish **"this function touches BIOS"** from **"this function is
  self-contained"**. Once A0/B0/C0 recognition (3.3) lands, the candidate
  selector can know which BIOS services a function uses, giving #225 a
  *machine-readable reason* for every rejection and letting future bring-up
  prioritise HLE services by counting which function numbers actually appear —
  the evidence loop Issue #279 asks for ("Initial BIOS HLE subset is selected
  from real usage evidence").

## 4. Next-service recommendation (evidence-based)

Rating scale for this table: **External dependency** is `host` (a Runtime
capability or host I/O is required to honour the documented effect), `guest`
(the service only touches guest registers/state), or both. **Testability** is
the ease of building a deterministic, fixture-free differential/contract test.
**Real-ROM evidence availability** is whether the site can be proven from
analysis artifacts. This column is no longer a projection: A0/B0/C0 recognition and
R9/`$t1` resolution have landed, so it now reports what was actually observed across the
locally available fixtures ([3.4](#34-real-rom-evidence-obtained)). The
**function numbers themselves come from verified
documentation**: they are known from published PS1 BIOS documentation and
recorded in `docs/REFERENCES.md`. Real-ROM evidence means recognizing A0/B0/C0
call sites in the decoded instruction stream and correlating them with those
documented function numbers. Identities for putchar/puts are verified and cited
in `docs/REFERENCES.md`; getchar/gets are used in `BiosHleContractTests` with
A0:3B/A0:3D but are **not yet re-verified** against the documentation, so they
carry a "verify before register" marker.

| Service | Documented identity | Arguments | Return | Side effects | External dependency (host/guest) | Testability | Real-ROM evidence availability | Implementation difficulty |
|---|---|---|---|---|---|---|---|---|
| ~~putchar TTY completion (completes A0:3C)~~ — **done**, registered with full documented behavior | A0:3C `std_out_putchar(char)` (B0:3D alias) | 1 char word (`arg & 0xFF`) | the character | write char to TTY output sink | **host** — output sink (`IRuntimeOutputSink`) | High (deterministic sink assertable) | **None observed** — no locally available title calls A0:3C (3.4) | Low |
| ~~puts~~ — **done**, registered with full documented behavior | A0:3E `std_out_puts(src)` (B0:3F alias) | 1 pointer to NUL-terminated guest string | the incoming string-pointer | read guest string; write to TTY; return pointer | host + **guest read** — `IGuestMemoryReader` and `IRuntimeOutputSink` | High (differential: stub reads, compare output + R2) | **None observed for A0:3E**; the B0:3F alias *is* called (3.4). Identity verified (ADR-014) | Low–medium |
| getchar | A0:3B (needs doc verification) | none | the character (with wait) | read/consume TTY input; **blocking** wait when empty | host — input sink | Low (blocking; determinism needs a designed input sink) | **None observed** — consistent with the expectation that real titles rarely use TTY input | Medium–high |
| gets | A0:3D (needs doc verification) | 1 pointer to guest buffer | to be verified before registration | read a TTY input line into guest memory (NUL-terminated) | host + **guest write** — input sink + guest-memory write | Low (no input sink design; needs guest-memory write too) | Medium | High |
| ~~B0:3F puts alias~~ — **done**, registered with full documented behavior | B0:3F `std_out_puts(src)` (alias of A0:3E) | same as A0:3E | same | same, through the B0 table, same `PutsService` | host + guest read (already exists) | High (differential: stub reads, compare output + R2) | **Confirmed.** A real title calls it at guest PC `0x800D0FF8` ([3.4](#34-real-rom-evidence-obtained)) | Low — reuse, no new implementation |
| B0 putchar alias | B0:3D | same as A0:3C | same | same, through the B0 table | host (output sink already exists) | High (deterministic sink assertable) | **None observed.** | Low (capability-gated) |
| GetC0Table | B0:56 `GetC0Table()` | none | address of the C0 jump-table list | none directly observable, but the documented purpose is to let callers **patch** that list — the return value is only honest if it points at real, guest-visible, dispatch-connected table content | **guest state** — a guest-visible, dispatch-connected jump-table representation this Runtime does not model at all; a generic write primitive now exists (`IGuestMemoryWriter`) but the table's own canonical address/format/initial contents remain unconfirmed (#360) | Low today for a bare return value, but that alone would not honestly test the documented behavior; the modeled table this ABI depends on does not exist to test against | **Confirmed** — the most frequently observed verified identity (3 of 5 executables, 2–3 sites in one) | **Blocked** — needs a new Runtime capability (guest-visible kernel jump-table state), not a wiring task (ADR-014 amendment 2026-09-11; tracked in #360) |
| GetB0Table | B0:57 `GetB0Table()` | none | address of the B0 jump-table list | same as GetC0Table, for the B0 list | same as GetC0Table | same as GetC0Table | **Confirmed** — tied for most frequently observed, with 4 sites in one executable | **Blocked** — same capability gap (ADR-014 amendment 2026-09-11; tracked in #360) |

### Recommendation

**Track A (guest-memory read boundary)** and **Track B (deterministic output
sink)** have both landed (PR #350, PR #349), and the two TTY-output services
they unblocked have since been implemented:

1. **putchar TTY completion (A0:3C)** — ✅ **done.** It needed *only* the output
   sink. It stays on a scalar contract (no guest read) and upgraded `Supported`
   to the stricter ADR-014 reading (full documented behavior including
   host-visible output).
2. **puts (A0:3E)** — ✅ **done.** It needed guest-memory **read** + output sink;
   with its identity already verified, registration was a pure
   implementation/wiring task rather than a Runtime-capability change or fresh
   identity research (ADR-014's stated intent). It retired the one documented
   "deliberately unregistered" neighbour, shrinking the loud-but-incomplete
   surface for TTY output.
3. **getchar/gets** — the remaining candidates, deferred until the **input sink
   design is decided**. getchar adds blocking semantics; gets additionally needs
   guest-memory write. Neither is on the critical path for a first
   BIOS-touching real-ROM function (games overwhelmingly *write* to TTY via
   putchar/puts; TTY input is rare).
4. **B0:3F** — ✅ **done.** Registered 2026-09-11 (ADR-014 amendment *B0:3F registered*),
   reusing the existing `PutsService` under a distinct identity rather than a new
   implementation. **B0:3D** stays unregistered: no call site selects it. Note that no
   locally available title calls A0:3C or A0:3E directly, so the B0 alias was, until this
   registration, the *only* observed call to a service this repository had verified.
5. **B0:56 GetC0Table / B0:57 GetB0Table** — ✅ **done (#360).** Both registered
   2026-09-11 (ADR-014 amendment for #360). The jump-table state abstraction
   (`BiosJumpTables`, guest-RAM-backed dispatch in `BiosHleRuntime.Invoke`) was
   the required capability; once it existed, registration was equivalent to
   `puts`'s wiring task — the effect is now genuine, not a bare constant return.
   See open question 9 (resolved) and the §4 next-service table above for the
   updated status. The remaining open item — executing patched targets — is tracked
   as follow-up Issue #362 (item 11).

Rationale in one line: both output-side boundaries existed, so putchar
completion and puts were wiring/registration tasks rather than capability work
and are now complete; only input (getchar/gets) still needs a Runtime
capability (an input sink) that does not exist yet.

## 5. Dependency map

```text
BIOS HLE service (BiosHleRuntime.Invoke)
        │
        ├── Track A: guest-memory reader (guest-memory read boundary) — landed, PR #350
        └── Track B: deterministic output sink — landed, PR #349 / ADR-014 amendment 2026-09-10
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

- **`puts` was not registered as `Supported` until it actually read guest memory
  and wrote to the sink** — the condition this section previously stated as a
  prohibition, now satisfied rather than waived. Registering it with only its
  return value modeled would have let an invalid/unmapped pointer silently
  succeed where real hardware faults, hiding a correctness gap (the rejection
  rationale in ADR-014). Boundary existence was a precondition, not a
  substitute, for exercising both effects; the registered service exercises
  both, and an unreadable pointer reports `BIOS_HLE_UNSUPPORTED_STATE` with zero
  bytes written. The same bar applies unchanged to every service registered from
  here on.
- **This document registers nothing.** It is an inventory; the registry in
  `BiosHleRuntime` is `(A0, 0x3C)`, `(A0, 0x3E)`, and `(B0, 0x3F)`, changed by the
  implementation tasks that added those services, not by this document. Any
  service added later is likewise a separate implementation task that adds code
  and updates ADR-014/ARCHITECTURE.md as appropriate.

## 6. Open questions / next steps

1. ~~**Add A0/B0/C0 target recognition to the analysis pipeline**~~ — ✅ **done.**
   `BiosCallRecognizer` recognizes both direct jumps to a vector and the indirect
   `jr`/`jalr` dispatch real code actually uses.
2. ~~**Add R9/t1 function-number resolution for indirect BIOS dispatch**~~ — ✅ **done.**
   Resolved from a block-local constant, including the delay-slot case that the canonical
   stub relies on. Sites whose number cannot be resolved are recorded as
   `Unresolved` rather than dropped or guessed.
3. **Widen the evidence decode window.** The pipeline's default window
   (`RomAnalysisPipeline.DefaultInstructionCount`, 128 instructions from the entry point)
   is an entry-point probe: it reaches only the earliest stubs, so the `biosCalls` section
   of a default-window `report.json` understates a title's BIOS surface. The real-ROM
   evidence test asks for a text-wide window explicitly. Deciding the persisted artifact's
   own window is a separate change with an artifact-churn cost, and is deliberately not
   made here.
4. ~~**Verify the identities the evidence surfaces most often**~~ — ✅ **done for seven of
   nine** (2026-09-11, Issue #11): `A0:39`, `A0:AB`, `A0:AC`, `B0:4E`, `B0:50`, `B0:56`,
   `B0:57` are verified against `docs/REFERENCES.md` and recorded in `BiosCallNames`.
   `B0:0A` and `B0:4F` were not part of that pass and remain unverified — they are
   call-frequency observations, not service candidates, until checked.
5. ~~**Decide whether to register `B0:3F`**~~ — ✅ **done.** Registered 2026-09-11 (ADR-014
   amendment *B0:3F registered*), reusing `PutsService`.
6. **Decide the input sink design for getchar/gets** — blocking semantics,
   host-provided input source, and how the Domain layer receives it without I/O.
7. **Re-verify getchar/gets identity** (A0:3B / A0:3D) against the documented
   std_io behavior and record it in `docs/REFERENCES.md` before any register work,
   per the no-guessing rule of ADR-014.
8. ~~**Re-audit `Supported` against the strict reading once a service is
   actually wired to the output sink**~~ — ✅ **resolved.** Both registered
   services now consume the sink: putchar writes its character (ADR-014
   amendment "A0:3C putchar wired to the output sink") and puts writes its
   string (amendment "A0:3E puts registered"), the latter also exercising the
   guest-memory read its return value depends on. ADR-014's open item is
   discharged for the whole registry, and the strict reading — full documented
   behavior including host-visible output, not just the ABI return contract —
   is the bar every future registration must meet.
9. ~~**Decide whether to build a guest-visible BIOS kernel jump-table (A0/B0/C0) state
   abstraction.**~~ ✅ **Done (#360, ADR-014 amendment 2026-09-11 for #360).**
   `BiosJumpTables` provides the table base addresses and entry-slot arithmetic.
   `BiosHleRuntime.Invoke` consults guest-visible RAM before the registry; a non-zero
   slot value returns `BiosServiceResult.PatchedTarget`. `B0:56 GetC0Table` and
   `B0:57 GetB0Table` are now registered and backed by guest-visible RAM connected to
   dispatch. The patched-target *execution* gap (jumping to arbitrary guest code) is
   tracked as a new follow-up Issue (see §6 item 11 below).
10. ~~**Add a generic Runtime guest-memory write boundary**~~ — ✅ **done** (#359,
    ADR-014 amendment 2026-09-11 "Runtime guest-memory write boundary").
    `IGuestMemoryWriter`/`GuestMemoryWriter` mirror the read boundary (translation,
    RAM bound, Try-style contract). The consumer that was pending has now landed:
    `BiosHleContractTests` patches jump-table slots through `GuestMemoryWriter`
    end-to-end, and `IGuestMemoryWriter.TryWrite` was promoted with the writer
    widening that accompanied #360.
11. **Execute patched BIOS jump-table targets (guest-code / interpreter dispatch
    trap).** `BiosHleRuntime.Invoke` now returns `BiosServiceResult.PatchedTarget`
    carrying the raw guest target address when a slot has been patched, but this
    Runtime has no capability to actually execute that target. Requires (a) a
    guest-jump-to-`0xA0`/`0xB0`/`0xC0` recognition/trap mechanism in the
    interpreter and/or recompiled-code path, and (b) a decision for how a
    `PatchedTarget` result falls back to raw guest-code execution. Filed as
    follow-up Issue #362 (filed alongside PR for #360).