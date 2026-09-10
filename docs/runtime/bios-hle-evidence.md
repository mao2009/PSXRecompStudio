# BIOS HLE Evidence and Next-Service Inventory

**Status:** Draft

**Authority:** Reference

**Related Issues:** #279, #225, #11

## 1. Purpose

Issue #279 makes BIOS-less execution the default user path: recompiled software
must reach BIOS services through a shared Runtime/HLE boundary instead of a
required Sony BIOS image. The Runtime abstraction already exists
(`IBiosRuntime`, `BiosCallIdentity`, `BiosServiceResult`) and holds two
registrations — A0:3C `putchar` and A0:3E `puts` — each implementing its full
documented behavior, host-visible output included (ADR-014). Both parallel
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
registry is a dictionary keyed by `(BiosCallFamily, byte)` holding **exactly two
entries**: `(A0, 0x3C)` → `InvokePutChar` and `(A0, 0x3E)` → `InvokePuts`, the
latter a thin delegation to `PutsService.Invoke`. Every other call — including the
deliberately unregistered neighbours and aliases below — falls through to
`BiosServiceResult.Unsupported`, producing the stable diagnostic
`BIOS_HLE_UNSUPPORTED_CALL` (verified in `BiosHleRuntime.Invoke` and
`BiosServiceResult.CreateUnsupported`).

| Service | A0/B0 identity | Current state | Missing semantics | Blocked on (Runtime capability) |
|---|---|---|---|---|
| getchar | A0:3B | **Unregistered** — falls through to `BIOS_HLE_UNSUPPORTED_CALL` | TTY input read/consume; blocking wait for input | Input sink (no design yet) |
| putchar | A0:3C | **Registered `Supported` (full documented behavior)** — `InvokePutChar` writes `arg & 0xFF` to the injected `IRuntimeOutputSink` *and* returns it | None — TTY output is now emitted (ADR-014 amendment 2026-09-10) | None; `BiosHleRuntime` takes the sink by constructor injection |
| gets | A0:3D | **Unregistered** — `BIOS_HLE_UNSUPPORTED_CALL` | TTY line input into a guest-memory buffer | Input sink; guest-memory **write** |
| puts | A0:3E | **Registered `Supported` (full documented behavior)** — `InvokePuts` delegates to `PutsService`, which reads the NUL-terminated string through `IGuestMemoryReader`, writes it to the injected `IRuntimeOutputSink`, and returns the incoming pointer | None — all three documented effects are implemented (ADR-014 amendment "A0:3E puts registered") | None; `BiosHleRuntime` takes both the sink and the reader by constructor injection |
| putchar alias | B0:3D | **Unregistered** — no evidence selects the B0 entry | Same as A0:3C | Evidence that B0 is actually used (output sink already exists) |
| puts alias | B0:3F | **Unregistered** — but a real-ROM call site now selects it ([3.4](#34-real-rom-evidence-obtained)) | Same as A0:3E | Nothing — evidence, identity and both Runtime capabilities are now in place; only the registration work remains |
| all other A0/B0/C0 | — | **Unregistered** — any call not in the registry fails loudly via `BIOS_HLE_UNSUPPORTED_CALL`; no dummy success is returned | Per-service | Per-service |

Notes verified against the code:

- `BiosHleRuntime.PutCharFunction == 0x3C` and `PutsFunction == 0x3E`; the
  registrations are `(BiosCallFamily.A0, PutCharFunction)` and
  `(BiosCallFamily.A0, PutsFunction)`.
- `InvokePutChar` rejects any argument count ≠ 1 with
  `BIOS_HLE_INVALID_ARGUMENTS`; otherwise it writes the low byte
  (`identity.Arguments[0] & 0xFFu`) to the injected `IRuntimeOutputSink` and
  returns that same byte via
  `Supported(identity, identity.Arguments[0] & 0xFFu)` (`BiosHleRuntime.InvokePutChar`).
  The TTY output side effect is emitted; the rejection path writes nothing to
  the sink. The sink is a required constructor dependency
  (`ArgumentNullException` on null), so it can never be a silent no-op.
- `BiosHleContractTests` pins A0:3B / A0:3D / A0:3F / B0:3D / B0:3F and a
  non-contiguous A0:09 as `BIOS_HLE_UNSUPPORTED_CALL`, guarding against any
  accidental widening of the registry; it pins putchar's emitted byte, its call
  ordering, its determinism across fresh runtimes, both required constructor
  dependencies, and the untouched sink on the invalid-argument path; and it pins
  puts's dispatch end to end — the guest string reaching the sink, the returned
  pointer, an unmapped pointer failing loudly with zero bytes written, and both
  services sharing one sink in call order. A0:3E is no longer in the
  unsupported-neighbour set because it is now registered.
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
materializes the vector into a register (typically `$t2`/R10) and uses `jr`/`jalr`,
because `j` cannot reach those addresses from kernel-segment code — with the function
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

**A0/B0/C0 call sites are recognized in every locally available title.** Recognized site
counts ranged from 8 to 61 per executable across five distinct executables, spanning all
three families. Almost every site resolved through the canonical stub shape
(`resolution: "DelaySlotConstant"`), with a small number resolving from an earlier
constant in the same block (`BlockConstant`).

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
2. **A B0-table call site does exist.** Section 4 previously recorded "no evidence
   selects the B0 entries" as the reason to keep `B0:3D`/`B0:3F` unregistered. For
   `B0:3F` that statement is now **superseded by the evidence above**: one real title
   calls it. Whether to register the alias remains an implementation decision under
   ADR-014's bar (a service is registered only when it satisfies its full documented
   behavior), and this document still registers nothing — but the decision is no longer
   blocked on missing evidence.

**What was *not* established.** The frequently observed function numbers below are raw
identities from the recognizer; this repository has **not** verified what they are.
Verifying an identity against the documentation cited in `docs/REFERENCES.md` is a
prerequisite for any registration work, per ADR-014's no-guessing rule.

| Identity | Observed in |
|---|---|
| `A0:39` | every distinct executable examined |
| `B0:57` | every executable that reached the relevant text region, 4 sites each |
| `B0:56` | same, 2–3 sites each |
| `A0:AB`, `A0:AC`, `B0:4E`, `B0:50` | most executables examined |

These are reported as call-frequency observations only. None is proposed as a service
until its identity is verified.

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
| B0 putchar/puts aliases | B0:3D / B0:3F | same as A0 counterparts | same | same, through the B0 table | host (+ guest read for puts) | High (once the capability exists) | **B0:3F — confirmed.** A real title calls it at guest PC `0x800D0FF8` (see [3.4](#34-real-rom-evidence-obtained)); the earlier "no evidence selects the B0 entries" reading is superseded for `B0:3F`. **B0:3D — still none.** | Low (capability-gated) |

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
4. **B0:3F** — the condition this item set has now been met: real-ROM evidence shows a
   B0-table `puts` call site ([3.4](#34-real-rom-evidence-obtained)). Both Runtime
   capabilities it needs already exist, and its identity is verified in
   `docs/REFERENCES.md`, so registering it is an implementation task rather than a
   capability or research one. **B0:3D** stays unregistered: no call site selects it.
   Note that no locally available title calls A0:3C or A0:3E, so the B0 alias is at
   present the *only* observed call to a service this repository has verified.

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
  `BiosHleRuntime` is `(A0, 0x3C)` and `(A0, 0x3E)`, changed by the
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
4. **Verify the identities the evidence surfaces most often** (`A0:39`, `B0:56`,
   `B0:57`, `A0:AB`, `A0:AC`, `B0:4E`, `B0:50`) against the documentation cited in
   `docs/REFERENCES.md`, and record them there. Until that is done they are call-frequency
   observations, not service candidates — ADR-014 forbids registering a guessed identity.
5. **Decide whether to register `B0:3F`** now that a real call site selects it
   ([3.4](#34-real-rom-evidence-obtained)) and both required Runtime capabilities exist.
4. **Decide the input sink design for getchar/gets** — blocking semantics,
   host-provided input source, and how the Domain layer receives it without I/O.
5. **Re-verify getchar/gets identity** (A0:3B / A0:3D) against the documented
   std_io behavior and record it in `docs/REFERENCES.md` before any register work,
   per the no-guessing rule of ADR-014.
6. ~~**Re-audit `Supported` against the strict reading once a service is
   actually wired to the output sink**~~ — ✅ **resolved.** Both registered
   services now consume the sink: putchar writes its character (ADR-014
   amendment "A0:3C putchar wired to the output sink") and puts writes its
   string (amendment "A0:3E puts registered"), the latter also exercising the
   guest-memory read its return value depends on. ADR-014's open item is
   discharged for the whole registry, and the strict reading — full documented
   behavior including host-visible output, not just the ABI return contract —
   is the bar every future registration must meet.