# Persona E2E Pipeline Status (Issue #351)

**Status:** In Progress (Pre-Alpha)

**Authority:** Reference

**Related Issues:** #351 (verification gate), #9 (v0.1.0 milestone), #279 (BIOS-less execution), #205 (Recompiler roadmap), #593 (REGIMM/zero-comparison branch lowering, resolved), #596 (register-shift-amount opcode lowering, resolved), #597 (MULT/DIV/HI-LO lowering, resolved), #599 (LWL/LWR/SWL/SWR lowering, resolved), #628 (Syscall lowering, resolved), #635 (register-indirect JR/JALR relay, resolved), #440 (GPU remaining integration), #444 (CD-ROM), #447 (GTE), #441 (rasterization/frame snapshot, completed), #442 (device scheduling, completed), #445 (SPU register/MMIO, completed), #443 (SIO0 scoped model, completed), #601 (this status synchronization), #676 (generated-host device integration), #680 (generated-host hardware INT delivery)

## Purpose

Documents the current state of the Persona title-screen end-to-end verification path,
the first blocker that prevents the milestone from being reached, and the reproduction
route for anyone working on #351.

This document is not a requirements spec. Requirements and acceptance criteria for #351
live in the Issue itself.

---

## E2E Pipeline Stage Map

The v0.1.0 milestone targets this path for Persona (女神異聞録ペルソナ / Revelations: Persona):

```text
[1] FIXTURE_DISCOVERY  — legal user-owned disc image in rom/*.chd
[2] BUILD              — dotnet build (Release)
[3] ANALYSIS           — CHD → ISO → SYSTEM.CNF → PS-X EXE → decode → CFG → COMPLETE
[4] RECOMPILER_SLICE   — candidate function selection → recompilation → differential validation
[5] RUNTIME_EXECUTION  — full-title execution loop (`ExecutionOrchestrator` + host engine, Issue #366)
        ↓
    BIOS HLE dispatch  — A0/B0/C0 jump-table service calls (in-band `BiosVectorDispatch`)
        ↓
    GPU / SPU / SIO0 / CD-ROM / GTE — partial hardware models; production integration remains incomplete
        ↓
[6] TITLE_SCREEN       — Persona title screen (v0.1.0 Release Gate)
```

## Implemented Stages

| Stage | Status | Entry point |
|---|---|---|
| FIXTURE_DISCOVERY | ✅ Implemented | `RealRomFixtures.Discover()` — scans `rom/*.chd` |
| BUILD | ✅ Implemented | `dotnet build` |
| ANALYSIS | ✅ Implemented | `RealRomAnalysisSkillTests` / `RealRomAnalyzer.RunAll()` |
| RECOMPILER_SLICE | ✅ Implemented | `RealRomRecompilerVerticalSliceTests` / `RealRomCandidateSelector.SelectBest()` |
| RUNTIME_EXECUTION | ✅ Implemented | `ExecutionOrchestrator` over `HostTitleExecutionEngine` (Test) / `RealRomTitleExecutionTests`; production PS-X EXE path via `TitleExecutionService.Run(PsxExe, ...)` (#409) |
| BIOS HLE (subset) | ⚠ Partial | `BiosHleRuntime` — 13 registered identities; current inventory is maintained in `docs/runtime/bios-hle-evidence.md`, and the measured Persona path now passes A0:13, B0:19, B0:5B, C0:0A, A0:72 and A0:3F plus SYS(02h) |
| GPU | ⚠ Partial | GP0/GP1/GPUSTAT + VRAM/MMIO (#440), minimal rasterization + deterministic `FrameSnapshot` (#441/#500), VBlank IRQ0 scheduling (#442/#493), production interpreter 32-bit guest MMIO reachability (#572), GPU command IRQ1 delivery (#574), and production `FrameSnapshot` headless evidence (#575); DMA2 remains |
| SPU | ⚠ Partial | Rust-owned register/MMIO model at 0x1F801C00-0x1F801DFF (#445/#551); no ADPCM/ADSR/mixing/reverb/sound-RAM/audio-output model |
| SIO0 | ⚠ Partial | Production-reachable register model + deterministic disconnected-pad transaction path + IRQ7 (#443 via #548/#549); no real host controller or memory-card wire protocol |
| CD-ROM | ⚠ Partial | Register/FIFO substrate, minimum command protocol, DMA3 and IRQ2 are implemented and production-interpreter reachable (#585/#586/#587); sector bytes are test-supplied only — no real disc source, streaming, seek timing or CD audio (#14) |
| GTE | ⚠ Partial | COP2 data/control register bank (#581 / PR #592), RTPS (#582 / PR #590), NCLIP (#583 / PR #591), and AVSZ3/AVSZ4 (#584 / PR #589) are implemented; native COP2 dispatch/integration remains #447 |
| TITLE_SCREEN | ❌ Not reached | — |

The RUNTIME_EXECUTION row above is this gate's own real-ROM, fixture-gated test
path (generated-host `HostTitleExecutionEngine`, `[Test]`-only). It is separate
from the Studio's own production execution entry point described below
(ADR-015, interpreter-backed) — the two are not the same engine and should not
be conflated.

## First Blocker toward TITLE_SCREEN (as of HEAD)

**Stage:** Runtime execution (generated host, kernel exception path)

**Classification:** `kernel exception handler entered; priority chain reaches an unmodelled element (VBlank IRQ0 pending)` (measured in #662; next: #658/#660, then #661).

**Description:**

A fresh local run against a legally owned Persona fixture (`rom/PERSONA.chd`)
now completes the production CLI's whole-program build and enters generated-host
runtime execution. With only the two caller-supplied dynamic entry roots already
identified by the preceding measured path:

```powershell
dotnet run --project src/PSXRecomp.Cli -- run rom/PERSONA.chd --output out/persona \
  --entry-root 0x80025350 --entry-root 0x80025614 \
  --json --report --frame-evidence
```

the run passes SYS(02h) `ExitCriticalSection`, discovers the SYSCALL fall-through
without an extra root, passes A0:3F `printf`, and emits
`CD_init:addr=800500e4`. It then takes the VBlank interrupt as a hardware INT
exception and stops, fail-closed, at the general exception vector
`0x80000080` (item 20, #680). Since #662 (item 21) that vector is the Runtime's
kernel exception handler (C0:06): the run enters it, saves the context, and stops,
fail-closed, at the priority chain with `BIOS_EXCEPTION_CHAIN_UNSUPPORTED` (exit 1,
state 5 `RuntimeFailure`; EPC `0x80025CBC`, CAUSE `0x400`, SR `0x404`, I_STAT
`0x0001`, I_MASK `0x000D`) because the pending enabled VBlank IRQ0 needs a kernel
chain element the Runtime does not model. The earlier `OUTER_BUDGET_EXHAUSTED` wait at
`0x800278A8` (#675, items 16-19) is no longer where the run ends.

**Historical structural gap.** Earlier production runs failed before runtime
because `ReachableProgramBuilder` lowers the entire statically reachable graph
while the bounded real-ROM gates do not. The blocker history below records how
that gap was exposed and resolved; it is no longer the current first blocker.

**Blocker history, in the order actually measured (not guessed):**

1. **Resolved (#593).** The initial run failed at PC `0x80012170` (`Bgez`,
   `[InvalidFlow] Control-transfer opcode 'Bgez' is not supported by this
   lowering stage.`) — a few KB past the EXE load base, inside Persona's own
   early startup code, not BIOS/GPU/CD-ROM. `MipsToIrLowerer` lowered only
   `Beq`/`Bne`/`J`/`Jal`/`Jr`/`Jalr`; the six compare-with-zero branch opcodes
   (`Blez`/`Bgtz`/`Bltz`/`Bgez`/`Bltzal`/`Bgezal`) were decoded and natively
   executable but never lowered to IR. #593 added that lowering.
2. **Resolved (#596).** Re-running the identical CLI command after #593
   failed at PC `0x80018354` (`Srlv`, `[InvalidOperationShape] Opcode 'Srlv'
   is not supported by this lowering stage.`) — `Srlv` (shift-right-logical by
   a register amount) is a plain ALU opcode, not a control-transfer one; only
   the shift-*by-immediate* forms (`Sll`/`Srl`/`Sra`) were lowered, not the
   register-shift-amount forms (`Sllv`/`Srlv`/`Srav`). The existing IR shift
   representation could not even hold a runtime shift amount (`ShiftAmount`
   was a compile-time byte field); #596 added three variable-shift IR kinds
   (`ShiftLeftLogicalVariable`/`ShiftRightLogicalVariable`/
   `ShiftRightArithmeticVariable`) and lowering for all three opcodes.
3. **Resolved (#597).** Re-running the identical CLI command after #596
   failed at PC `0x8001CE34` (`Mult`, `[InvalidOperationShape] Opcode 'Mult'
   is not supported by this lowering stage.`). #597 added IR representation
   and lowering for `Mult`/`Multu`/`Div`/`Divu` plus the HI/LO moves
   (`Mfhi`/`Mflo`/`Mthi`/`Mtlo`), including host-side divide edge semantics.
4. **Resolved (#599, PR #627).** Re-running the identical CLI command after
   #597 advanced to PC `0x800287A4` (`Lwl`, `[InvalidOperationShape] Opcode
   'Lwl' is not supported by this lowering stage.`). `Lwl`/`Lwr`/`Swl`/`Swr`
   require unaligned byte-merge semantics rather than ordinary load/store
   overwrite semantics, including the architecturally correct old `rt` value
   and pending load-delay interaction. #599 added IR lowering and
   differential coverage against the native interpreter; `Lwl @ 0x800287A4`
   is resolved by PR #627.
5. **Resolved (#628).** Re-running the identical CLI command after #599 /
   PR #627 stopped at PC `0x8004143C` (`Syscall`). #628 lowers `Syscall` as
   the same architectural synchronous exception exit BREAK uses (Excode
   `0x08`, EPC/BD carried on the exit, delay-slot form reports the owning
   branch), validated by `RecompilerIrValidator` and differential-tested
   against the native interpreter. The build stage now succeeds.
6. **Resolved (#635).** Re-running the identical CLI command after #628
   built the artifact and stopped at runtime: `Blocked` / `RuntimeHandoff`,
   guest PC `0x80041694`, null result diagnostic. That block is the A0 stub
   `addiu $t2,$zero,0xA0; jr $t2; addiu $t1,$zero,0x39`; register-indirect
   `JR`/`JALR` ended the artifact run without relaying the target. #635
   carries the target on the IR exit (`TargetValueId`) into the dispatch
   loop; a block trace of the same artifact built with
   `RECOMPILER_CHECKPOINTS` (local investigation, not committed) shows `0x80041694` → host-claimed
   `0x000000A0` with `$t1 = 0x39` (A0:39 InitHeap, Supported) → return to
   `0x800119C4`.
7. **Current first blocker (measured after #635, runtime stage).** The same
   CLI command now stops at `Blocked` / `UnsupportedTransfer`, exit 2,
   guest PC `0x00000000`, `UNRESOLVED_TRANSFER`; frame evidence (the
   separate interpreter run) is `unavailable` / `RuntimeFailure` /
   `BIOS_HLE_UNSUPPORTED_CALL` (`no-frame-activity`), at A0:13. The artifact
   reaches `0x00000000` through `jalr $v0` at `0x800251D0`, whose target is
   loaded through the pointer at `0x8004FCE0`. The artifact's guest RAM is
   not seeded with the PS-X EXE image (its input carries zero initial-memory
   entries), so that load returns 0; in the EXE image the pointer chain names
   `0x80025350`, which the interpreter calls. The same missing image data
   already changes `$sp` at startup (the word at `0x800119E0`). The generated
   program also has no block at `0x80025350` or at the `jalr` return address
   `0x800251D8`, because reachable-program discovery stops at `JR`/`JALR`.
   These are artifact memory-image and static-discovery gaps, not BIOS HLE
   coverage.
8. **Measured after #637/#638/#644 (runtime stage).** With the EXE image in
   artifact RAM (#637) the same `jalr $v0` at `0x800251D0` targets `0x80025350`
   (runtime fact; `$ra = 0x800251D8`, now a block entry after #638). The run
   stopped at `Blocked` / `UnsupportedTransfer`, exit 2, guest PC `0x80025350`,
   reported by #644 as `UNRESOLVED_TRANSFER_IN_IMAGE` (an in-image PC with no
   compiled block). Supplying it as an explicit root,
   `psxrecomp run rom/PERSONA.chd --entry-root 0x80025350 --json`, compiles the
   block and the run no longer stops there; the next blocker is
   `BIOS_HLE_UNSUPPORTED_CALL` `A0:13` (exit 1, `RuntimeFailure`), a BIOS HLE gap
   and not a coverage gap. Roots are caller input only; nothing in production code
   names a title address (ADR-012 amendment).
9. **Measured after #648 (A0:13 setjmp HLE, runtime stage).** A0:13 is now
   registered (`SetJmpService`, 0x30-byte `jmp_buf`; `$v0 = 0`). Measured on
   `main` 79a4859 plus the #648 change, same command with
   `--entry-root 0x80025350`: the run passes `0x80025350` and A0:13 and stops at
   `BIOS_HLE_UNSUPPORTED_CALL` `B0:19` (exit 1, `RuntimeFailure`, result guest PC
   `176` / `0xB0`, the B0 vector); frame evidence reports the same code, `unavailable` /
   `no-frame-activity`. B0:19 is the next BIOS HLE gap, tracked separately;
   the A0:14 longjmp companion is not implemented.
10. **Measured after #650 (B0:19 HookEntryInt HLE, runtime stage).** B0:19 is now
    registered: the address in `$a0` is stored *by pointer* in a guest-RAM kernel
    variable (`0x00000118`, this Runtime's own choice; spec CONFIRMED: "addr points
    to a structure"), and firing the hook reads the buffer's current contents and
    restores only `ra/sp/fp/s0-s7/gp` with `$v0 = 1` (`BiosExceptionHook.TryComplete`).
    **The hook cannot fire in a real run yet**: its consumer is the kernel's
    exception-handler completion (C0:06). #651 defined the completion boundary
    (`BiosExceptionCompletion`) as a Runtime contract; #662 (item 21) now reaches it
    from both execution paths through the shared C0:06 entry, but only once the
    priority chains ran to the end, which the production run does not reach yet
    (B0:17 #664, B0:18 #665).
    Measured on `main` 79a4859 + #648 + #650 (PERSONA.chd):
    - `--entry-root 0x80025350` alone: A0:13 and B0:19 pass; stops at
      `UNRESOLVED_TRANSFER_IN_IMAGE` `0x80025614` (exit 2, `UnsupportedTransfer`).
      This matches the #639 stub-probe observation, now confirmed on the production run.
    - `--entry-root 0x80025350 --entry-root 0x80025614` (caller input only): the run
      proceeds past `0x80025614` and stops at `BIOS_HLE_UNSUPPORTED_CALL` `B0:5B`
      (exit 1, `RuntimeFailure`); frame evidence stays `unavailable` /
      `no-frame-activity`. B0:5B is the next BIOS HLE gap and is not implemented here.
11. **Measured after #652 (B0:5B ChangeClearPAD HLE, runtime stage).** B0:5B is
    registered as *configuration only*: the raw argument is stored in a guest-RAM
    kernel variable (`0x00000128`, this Runtime's own choice; survives the
    per-segment Runtime rebuild). CONFIRMED (psx-spx): it controls the Pad/Card IRQ
    handler's automatic IRQ0 (VBlank) acknowledge, for pad and card alike.
    NOT documented, so not assumed: which value enables it, any return value
    (none is reported, `$v0` untouched), and any relation to C0:0D. The call never
    touches I_STAT. **Nothing consumes the setting yet** (no BIOS Pad/Card IRQ
    handler exists in the Runtime; #654). Measured on `main` 79a4859 + #648 + #650 +
    #652, `--entry-root 0x80025350 --entry-root 0x80025614` (caller input only):
    the run passes `0x80025350`, A0:13, B0:19, `0x80025614` and B0:5B, and stops at
    `BIOS_HLE_UNSUPPORTED_CALL` `C0:0A` (exit 1, `RuntimeFailure`); frame evidence
    stays `unavailable` / `no-frame-activity`. C0:0A is the next gap (#655).
12. **Measured after #655 (C0:0A ChangeClearRCnt HLE, runtime stage).** C0:0A is
    registered as *configuration only*. CONFIRMED (psx-spx timer-functions): `t` is
    0..2 for timer 0..2 or 3 for vblank; `flag` 0 = kernel IRQ handler does nothing
    after an IRQ, 1 = automatically acknowledge and immediately return from
    exception; the call returns the previous flag. The four flags live in a
    guest-RAM kernel variable (`0x00000130`, this Runtime's own choice; survives the
    per-segment Runtime rebuild). INFERRED: the initial flag is 0 (zero-initialised
    memory). Undocumented, so rejected with `BIOS_HLE_INVALID_ARGUMENTS` instead of
    guessed: `t > 3` and `flag` other than 0/1. The call never touches I_STAT or a
    timer. **Nothing consumes the flags yet** (no kernel timer/vblank IRQ handler in
    the Runtime; #658). Measured on `main` 79a4859 + #648 + #650 + #652 + #655,
    `--entry-root 0x80025350 --entry-root 0x80025614` (caller input only): the run
    passes C0:0A and stops at `BIOS_HLE_UNSUPPORTED_CALL` `A0:72` (exit 1,
    `RuntimeFailure`); frame evidence stays `unavailable` / `no-frame-activity`.
    A0:72 is the next gap (#657), in the order the #639 probe predicted.
13. **Measured after #657 (A0:72 CdRemove HLE, runtime stage).** A0:72 is
    registered with its documented contract only. CONFIRMED (psx-spx):
    `A(72h) or A(56h)` is `_96_remove` / `CdRemove`, `void _96_remove(void)`,
    intended to remove the kernel's priority-0 CD-ROM IRQ handlers. CONFIRMED
    (psx-spx.github.io): it "does NOT work due to SysDeqIntRP bug"; the current
    no$psx text omits that note, so the retail effect on the chain is UNKNOWN.
    The Runtime therefore records **no** removal and writes no guest state:
    arity 0, `$v0` untouched, `$ra` applied by dispatch. INFERRED only from
    PCSX-Redux OpenBIOS and not modelled: entering a critical section and
    closing the five CD-ROM events. Future CD-ROM IRQ work (#444) must not
    assume A0:72 removed the handler. The A0:56 alias is not registered.
    Measured on `main` d112677 + #657,
    `--entry-root 0x80025350 --entry-root 0x80025614` (caller input only): the
    run passes A0:72 and stops at `CPU_EXCEPTION` at guest PC `0x80041714`
    (exit 1, `RuntimeFailure`): a `syscall` with `$a0 = 2`, i.e. SYS(02h)
    ExitCriticalSection, called from `0x8002540C`; frame evidence stays
    `unavailable` / `no-frame-activity`. This matches libetc `startIntr`'s
    `_96_remove(); ExitCriticalSection();` order and is the next gap (#663).
14. **Measured after #663 (SYS(02h) ExitCriticalSection, runtime stage).**
    A SYSCALL with a Runtime attached is completed through the shared kernel
    contract (ADR-014 amendment): SR bits 2 and 10 are set in the exception
    frame, the RFE pop moves bit 2 to IEc, and execution resumes after the
    `syscall` (net `SR | 0x401`; measured `cop0.sr=0x00000401` in the artifact
    snapshot). Unknown SYS numbers fail closed. Measured on `main` f6383e8 +
    #663, `--entry-root 0x80025350 --entry-root 0x80025614`: the run passes
    `0x80041714` and stops at `UNRESOLVED_TRANSFER_IN_IMAGE` at `0x80041718`
    (exit 2, `UnsupportedTransfer`), the instruction after the `syscall`, which
    reachable-program discovery does not compile (#669). Adding
    `--entry-root 0x80041718` (measurement only) reaches
    `BIOS_HLE_UNSUPPORTED_CALL` `A0:3F` printf (exit 1, `RuntimeFailure`, #670);
    the production interpreter reaches the same `A0:3F` without it. Frame
    evidence stays `unavailable` / `no-frame-activity`.
15. **Measured after #669 (SYSCALL fall-through discovery).** Reachable-program
    discovery now treats the `PC + 4` of a SYSCALL outside a delay slot as a
    reachable successor (discovery only; whether the Runtime completes the
    SYSCALL stays the #663 contract, unknown SYS still fails closed). Measured
    with only `--entry-root 0x80025350 --entry-root 0x80025614`: the artifact
    contains `recompiler_block_0x80041718` and the run stops at
    `BIOS_HLE_UNSUPPORTED_CALL` `A0:3F` printf (exit 1, `RuntimeFailure`), i.e.
    #670 is the next blocker. Frame evidence stays `unavailable` /
    `no-frame-activity`.

16. **Measured after #670 (A0:3F printf).** A0:3F is registered (`PrintfService`,
    arity 1; variadic words from `$a1-$a3`, then the guest stack at `$sp+16`).
    Measured with only `--entry-root 0x80025350 --entry-root 0x80025614`: the
    call is `printf("addr=%08x", 0x800500E4)`; the run passes it, emits
    `CD_init:addr=800500e4`, and now stops at `OUTER_BUDGET_EXHAUSTED`
    (exit 2, `state=3`) with guest PC `0x800278A8`, the PC where the budget
    expired. The generated blocks around it compare a RAM counter against
    `0x3C0000`, but what the guest is waiting for is **not identified**
    (classified in item 17). Frame evidence stays
    `unavailable` / `no-frame-activity`.

17. **Measured in #675 (classification, no code change).** `0x800278A8` is the
    return address of `jal 0x80025C98` (`VSync(-1)`) inside libcd `CD_sync`
    (strings `CD_sync`, `CD timeout: `). The loop sets `deadline = VSync(-1) + 0x3C0`
    (`[0x8005640C]`) and `iter = 0` (`[0x80056410]`), then per pass calls
    `VSync(-1)`, takes the timeout path if `deadline < VSync(-1)` or
    `iter++ > 0x3C0000`, else polls the RAM interrupt flag `[0x8004EC5A]` /
    state byte `[0x800500E0]`. `VSync(-1)` returns `Vcount` (`[0x8004FD3C]`),
    incremented only by the VBlank callback `0x80025BC8`. CONFIRMED from guest RAM
    at the stop: the guest registered its callbacks (IRQ0 `0x80025BC8`, IRQ2
    `0x800281F8`, IRQ3 `0x80025918`, I_MASK shadow `0x000D`) and Vcount is 0.
    CONFIRMED: the generated artifact maps only the 2 MiB RAM; reads of
    `0x1F801814`/`0x1F801110` return 0 and device writes are dropped, and
    nothing delivers an interrupt. Diagnostic budget control (local, not
    committed; wall-clock timeout lifted): the loop counter scales linearly with
    budget (1M blocks: 0x4C3C, 5M: 0x191C2, 20M: 0x65676) with Vcount unchanged;
    at 400M blocks the guest leaves the loop only via its own `0x3C0000` cap and
    prints `CD timeout: CD_cw:(CdlNop) Sync=NoIntr, Ready=NoIntr`, retries
    `CdlReset` (same), re-prints `CD_init:addr=800500e4` and cycles with Vcount
    still 0. Hence a permanent wait, not budget shortage. INFERRED: delivering
    VBlank IRQ0 alone would end the loop via the deadline path (a timeout, not
    CD success); a successful CD_sync needs the CD-ROM IRQ2 callback. UNKNOWN: the
    interpreter path's behaviour at this point (its frame-evidence run also
    reports `OUTER_BUDGET_EXHAUSTED` at 400M). The device models exist and are
    wired for the interpreter only; the generated-host gap is tracked in #676.

18. **#678 MMIO bridge landed; the blocker is unchanged.** The generated-host
    artifact now relays guest accesses outside RAM to the Runtime device graph, so
    `0x1F801814` / `0x1F801110` reads reach the existing GPU/Timer state instead of
    returning 0 and device writes are no longer dropped. Nothing advances those
    devices or delivers an interrupt yet (#679, #680), so the same run still ends
    `OUTER_BUDGET_EXHAUSTED` at `0x800278A8` with Vcount unchanged.

19. **#679 guest time reaches the `DeviceScheduler`; the stop is unchanged.** The
    artifact now reports retired guest instructions and the host advances the
    existing scheduler (ADR-025), so device time passes on the generated-host path.
    Measured with the same production command (exit 2, `state=3`,
    `OUTER_BUDGET_EXHAUSTED`, guest PC `0x800278A8`, BIOS output
    `CD_init:addr=800500e4`, frame evidence `unavailable` / `no-frame-activity`),
    plus a local, uncommitted diagnostic dump of the device graph and guest RAM
    at the end of the run: 39,164 reports totalling 1,230,686 retired instructions
    (about 2.2 VBlank intervals); I_STAT `0x00000001` (IRQ0 pending, never
    acknowledged) with I_MASK `0x0000000D`; Timer 0 and Timer 2 (mode `0x1800`)
    counters `0xC75E`, which is exactly 1,230,686 mod 65,536; Timer 1 (mode
    `0x507`) `0`; DICR `0`, DMA3 CHCR `0`; CD-ROM `InterruptGeneration` 1 with
    `HasInterrupt` false and `DataReady` false; guest RAM `Vcount`
    (`[0x8004FD3C]`) `0`, `[0x800500E0]` `0`, `iter` (`[0x80056410]`) `0x4C3C`.
    IRQ0 is pending in the controller but nothing delivers it to the
    artifact CPU, so no callback runs and `CD_sync` keeps waiting: #680 is the next
    step. Not measured: whether delivering IRQ0/IRQ2 ends the wait.

20. **#680 hardware INT delivery; the stop moves to the exception vector.** The
    artifact takes the Interrupt Controller's line as an R3000A INT at a dispatch
    boundary when `SR.IEc` and `SR.IM2` are set (ADR-025 addendum). Measured on
    `main` d3c4ef6 + #680, with only
    `--entry-root 0x80025350 --entry-root 0x80025614`
    (`run rom/PERSONA.chd --json --report --frame-evidence`): the run passes
    SYS(02h) (SR `0x401`), A0:3F, `CD_init:addr=800500e4` (the only guest output),
    and stops with `RuntimeFailure` / `ARTIFACT_EXCEPTION_VECTOR_UNHANDLED`,
    `guestPc` `0x80000080` (exit 1). CONFIRMED from the diagnostic: EPC
    `0x80025CBC` (inside libetc `VSync`, the polling code from item 17), CAUSE
    `0x00000400` (Excode INT, BD 0, IP2), SR `0x00000404` (the SR `0x401` stack-pushed,
    IEc cleared), I_STAT `0x0001` (VBlank IRQ0) with I_MASK `0x000D`. So the
    previous permanent wait is left: the VBlank IRQ reached the CPU. It produced no
    guest-visible progress yet: no guest callback ran (`Vcount` is still produced
    only by the VBlank callback `0x80025BC8`), because the artifact has no kernel
    code at `0x80000080` (the real BIOS places a stub that enters C0:06
    ExceptionHandler; BIOS-less runs place nothing). **First blocker: #662**
    (C0:06 ExceptionHandler entry). #658/#660 (timer/VBlank handlers, root-counter
    events), #661 (Pad/Card IRQ), #664/#665 (B0:17 / B0:18) sit behind it and were
    not reached. Frame evidence stays `unavailable` / `no-frame-activity`
    (the frame-evidence run is the interpreter's and still reports
    `OUTER_BUDGET_EXHAUSTED`). Not reached: the title screen; not measured:
    whether the handler chain then lets `CD_sync` complete (IRQ2).
    Recorded, not changed: the interpreter's INT check treats SR bit 1 as IEc while the
    kernel contract and generated host use bit 0 (ADR-025 addendum), so a guest enabling
    interrupts via SYS(02h) is interrupted on the generated host and not on the interpreter
    (fixed by #684: the interpreter now reads IEc from SR bit 0).

21. **#662 C0:06 ExceptionHandler entry; the stop moves to the priority chain.** The
    unpopulated RAM vector `0x80000080` now enters the shared BIOS-less kernel exception
    handler (`BiosExceptionHandler`, ADR-014 amendment): it saves the interrupted
    context into the current TCB (seeding a PCB/TCB because `[0x108]` is 0 in a BIOS-less
    run), walks the priority chains, and only after they ran to the end performs
    the completion step (B0:19 hook, else ReturnFromException). Measured on `main`
    35cc954 + #662, with only `--entry-root 0x80025350 --entry-root 0x80025614`
    (`run rom/PERSONA.chd --json --report --frame-evidence`): output unchanged
    (`CD_init:addr=800500e4`); `RuntimeFailure` / `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`,
    `guestPc` `0x80000080` (exit 1); EPC `0x80025CBC`, CAUSE `0x00000400`, SR
    `0x00000404`, I_STAT `0x0001`, I_MASK `0x000D` (unchanged: the entry only reads
    them). So the entry is crossed: the bare `ARTIFACT_EXCEPTION_VECTOR_UNHANDLED` is
    gone. **First blocker: the VBlank IRQ0 chain element.** The default chain
    stops at the first pending enabled IRQ (IRQ0), whose kernel handlers are priority 1
    (timer/VBlank: #658 clear flags, #660 root-counter events) and priority 2 (Pad/Card:
    #661; its B0:5B setting is modelled by #654 but not invoked). Which of them is first
    needed is decided when #658/#661 are implemented; #664/#665 sit behind that. Frame
    evidence stays `unavailable` / `no-frame-activity`. Not reached: the title screen;
    not measured: whether the chain lets `CD_sync` complete (IRQ2).

22. **#658 priority-1 timer/VBlank chain element; the stop moves to root-counter event
    delivery.** The default chain now runs `BiosTimerVblankIrqHandler` (VBlank/IRQ0,
    Timer2..0/IRQ6..4; claim = pending in I_STAT and enabled in I_MASK) and consumes the
    existing C0:0A `BiosRootCounterClearPolicy` flags: 0 = no acknowledge/return, the chain
    continues; 1 = acknowledge only that IRQ and return from the exception (hook and
    priority 2 skipped); other values fail closed. A claimed source first needs its
    root-counter events delivered, which is #660 and not implemented, so it fails closed
    rather than acknowledging an IRQ nothing serviced. Measured on `main` 24b7bdb + #658,
    with only `--entry-root 0x80025350 --entry-root 0x80025614`
    (`run rom/PERSONA.chd --json --report --frame-evidence`): output unchanged
    (`CD_init:addr=800500e4`); `RuntimeFailure` / `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`
    (exit 1), `guestPc` `0x80000080`; EPC `0x80025CBC`, CAUSE `0x00000400`, SR
    `0x00000404`, I_STAT `0x0001`, I_MASK `0x000D`; message
    `VBlank IRQ0 (C0:0A t=3) flag=0|root-counter event delivery (#660) is not modelled`
    (flag 0 is the unset default; the guest never called C0:0A). Frame evidence stays
    `unavailable` / `no-frame-activity`. **Next blocker: #660 root-counter event
    delivery**, then #661 (Pad/Card, priority 2) once VBlank continues past priority 1.

The build stage now passes and the run reaches `RUNTIME_EXECUTION`, where the
first measured stop is now the kernel exception handler's priority chain
(`BIOS_EXCEPTION_CHAIN_UNSUPPORTED`, item 21, #662): CPU INT delivery (#680) and the
C0:06 entry work; since #658 the VBlank IRQ0 element is reached and the missing piece is root-counter event delivery (#660, item 22), then Pad/Card (#661). Before it the stop was `OUTER_BUDGET_EXHAUSTED` at `0x800278A8` (after
#670 A0:3F printf, item 16), classified in item 17 as a wait for interrupts the generated host could not deliver (#676); **GPU DMA2 / remaining GPU integration (#440) and real CD-ROM data (#14) remain unreached and
unranked**. The generic sub-blocker ordering below remains background context,
not a priority order; the next implementation target is the first boundary
actually measured by the production run.

The Studio itself is **not** blocked on having no execution entry point. As of
ADR-015, `PSXRecompStudio.Services.TitleExecutionService` is the production
composition root: it assembles the production, Domain-layer
`InterpreterTitleExecutionEngine`, a `BiosHleRuntime`, and
`ExecutionOrchestrator`. The product reaches classified execution through two
distinct Studio actions:

- `MainWindowViewModel.RunDiagnosticTitleCommand` runs the built-in diagnostic
  program (zeroed register state) — `request → engine load → bounded run →
  BIOS handoff → classified result`.
- `MainWindowViewModel.RunRealTitleCommand` runs the real-ROM production flow
  (Issue #409): the loaded disc image is analyzed by
  `RealRomTitleExecutionService` through `RomAnalysisPipeline`, the analyzed
  PS-X EXE is retained from `RomAnalysisOutcome.Executable`, and that same
  executable object is fed into `TitleExecutionService.Run(PsxExe, ...)` →
  `ExecutionOrchestrator` → `InterpreterTitleExecutionEngine`, producing a
  classified outcome. The flow deliberately routes through the outcome-
  preserving pipeline rather than the report-only `DiscImageAnalyzer` façade,
  which returns only a `DiscImageAnalysisReport` and drops the executable.

Both run through the **interpreter** backend, not the generated-host
(recompiled) one (`HostTitleExecutionEngine` remains `[Test]`-only).

The real-ROM product flow is proven end to end by the Studio's product-flow
tests (`RealRomProductionFlowTests`): a synthetic disc input → Studio service
layer → production execution → classified result, including an assertion that
the exact executable held by the analysis outcome is the object handed to
`TitleExecutionService.Run(PsxExe, ...)`.

What still does not exist:

- **A production generated-host (recompiled) execution backend for the Studio.**
  The Studio's production composition remains interpreter-backed (ADR-015).
  The headless CLI can build and launch runnable generated-host artifacts, but
  that is not the same as replacing the Studio production engine.
- **Production GPU/frame completion.** Guest 32-bit GP0/GP1/GPUSTAT traffic from
  the production interpreter now reaches the existing managed `GpuDevice`/VRAM
  state through #572. GPU command IRQ1 is delivered through the production
  scheduler in #574. Production `FrameSnapshot` evidence is exposed headlessly through #575. DMA
  channel 2 data movement remains open under #440.
- **SPU audio behavior.** SPU register/MMIO storage is production-reachable
  (#445/#551), but ADPCM decoding, ADSR, mixing, reverb, sound RAM, audio output,
  CD-audio input, and IRQ9 are not implemented.
- **CD-ROM real disc data.** The register/FIFO substrate, minimum command
  protocol, DMA3 and IRQ2 are implemented and production-interpreter reachable
  (#585/#586/#587), but sector bytes are test-supplied only: there is no real
  disc source, streaming, seek timing or CD audio (#14).
- **GTE production integration.** The COP2 register bank (#581 / PR #592) and
  isolated RTPS/NCLIP/AVSZ3/AVSZ4 kernels (#582/#583/#584 via PRs #590/#591/#589)
  now exist. Native COP2/LWC2/SWC2 dispatch is still not wired to the implemented
  register/command semantics (#447).
- **An actual Persona title-screen proof.** No fake frame, hard-coded shortcut,
  or test-only presentation satisfies #351.

**Background runtime sub-blockers (not a priority order).** The production CLI
now reaches runtime execution, so these remain relevant only when the measured
path actually reaches them:

1. **BIOS HLE coverage (#279).** Thirteen identities are registered. The current
   Persona path has exercised the startup services through A0:3F `printf`, and
   unsupported calls still fail explicitly with `BIOS_HLE_UNSUPPORTED_CALL`.
   No unsupported BIOS identity is the current first blocker; add further
   services only when a measured execution path requires them.

2. **Production GPU/frame integration (#440 / #351).** Production interpreter
   32-bit GPU MMIO reaches the existing managed GPU state (#572) and GPU
   command IRQ1 is scheduler-delivered (#574). `FrameSnapshot` is exposed through the headless #351 evidence path (#575).
   The remaining concrete GPU integration gap is DMA2 data movement.

3. **Evidence-gated hardware after the next real boundary.** SPU register/MMIO
   exists while audio behavior is still absent. CD-ROM has its register/FIFO
   substrate, minimum command protocol, DMA3 and IRQ2, but sector data is still
   test-supplied only (#14). GTE now has a
   register bank plus isolated RTPS/NCLIP kernels, but COP2 dispatch/integration
   is still open. MDEC/GTE/CD-ROM/SPU work should be promoted only when the real
   execution path demonstrates that it is the next blocker rather than by issue
   number order.

## Reproduction Route

### Prerequisites

- A legally-owned Persona disc image (CHD format), placed at `rom/<name>.chd`
- .NET 10 SDK
- PowerShell 7+
- **Never** add the disc image to git or any artifact

### Run the gate

```powershell
# From the repository root:
pwsh scripts/e2e/persona-e2e-gate.ps1
```

Exit codes:
- `0` — reserved for a future run that reaches the `TITLE_SCREEN` release gate; the current implementation does not return PASS
- `1` — a stage failed
- `2` — no fixture is present, or the currently implemented stages completed without reaching `TITLE_SCREEN` (SKIP)

Output: machine-readable JSON in `reports/e2e/persona-e2e-gate-result.json` (git-ignored).

### Run individual stages

```powershell
# Stage 3 — Analysis only:
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj `
  --filter 'FullyQualifiedName~RealRomAnalysisSkillTests' -c Release

# Stage 4 — Recompiler vertical slice:
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj `
  --filter 'FullyQualifiedName~RealRomRecompilerVerticalSliceTests' -c Release

# Stage 5 — Full-title execution (orchestrator over generated host):
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj `
  --filter 'FullyQualifiedName~RealRomTitleExecutionTests' -c Release
```

With no fixture present, all three skip explicitly with reason:
`skipped: no real-ROM fixture found under rom/*.chd (disc images are never committed)`

The repository CI does not provide commercial ROM fixtures. Therefore these
real-ROM-gated tests **skip in CI by design**; CI still validates the synthetic
and fixture-independent paths. A local user-supplied legal fixture is required
to exercise the real-ROM stages.

## Output Artifacts

All paths are git-ignored. Do not commit any of these.

| Path | Content | Shareable |
|---|---|---|
| `reports/e2e/persona-e2e-gate-result.json` | Gate result JSON | Yes — metadata only |
| `reports/real-rom/<fixture>/manifest.json` | Analysis identity + counts | Yes |
| `reports/real-rom/<fixture>/report.json` | CHD/ISO/decode summary | Yes |
| `logs/real-rom/<fixture>/analysis.log.jsonl` | Per-stage detail (may contain local paths) | Local only |
| `rom/` | Disc images | Never — copyrighted |

When quoting results in Issues or PRs: PASS/FAIL/SKIP, stage name, counts,
SHA-256, and the diagnostic code only. Never paste ROM content, executable bytes,
or local paths.

## What Has Not Been Done (and Why)

| Not done | Why |
|---|---|
| Fake/hard-coded title screen | Issue #351 non-goal; artifact-policy gate would reject |
| Title-specific Core/Recompiler hack | Issue #351 explicit non-goal |
| "Screenshot exists = PASS" | Issue #351 explicit non-goal |
| DLSS/FSR/GPU modernization | Issue #351 non-goal |
| Completing the game | Issue #351 non-goal |

## Tracking

- [Issue #351](https://github.com/mao2009/PSXRecompStudio/issues/351) — this gate
- [Issue #9](https://github.com/mao2009/PSXRecompStudio/issues/9) — v0.1.0 milestone
- [Issue #593](https://github.com/mao2009/PSXRecompStudio/issues/593) — REGIMM/zero-comparison branch IR lowering (resolved the `Bgez` blocker)
- [Issue #596](https://github.com/mao2009/PSXRecompStudio/issues/596) — register-shift-amount opcode IR lowering (resolved the `Srlv` blocker)
- [Issue #597](https://github.com/mao2009/PSXRecompStudio/issues/597) — MULT/DIV/HI-LO IR lowering (resolved the `Mult` blocker)
- [Issue #599](https://github.com/mao2009/PSXRecompStudio/issues/599) — LWL/LWR/SWL/SWR IR lowering (resolved by PR #627; the `Lwl` blocker at PC `0x800287A4`)
- [Issue #628](https://github.com/mao2009/PSXRecompStudio/issues/628) — Syscall / exception-transfer IR lowering (resolved; `Syscall` at PC `0x8004143C`)
- [Issue #279](https://github.com/mao2009/PSXRecompStudio/issues/279) — BIOS-less execution / remaining HLE coverage (not yet reached by a production CLI run)
- [Issue #440](https://github.com/mao2009/PSXRecompStudio/issues/440) — remaining GPU production integration; DMA2 remains (IRQ1 #574 and headless FrameSnapshot #575 are complete)
- [Issue #444](https://github.com/mao2009/PSXRecompStudio/issues/444) — CD-ROM runtime model; register/FIFO substrate, command protocol, DMA3 and IRQ2 implemented (#585/#586/#587); real disc data remains #14
- [Issue #447](https://github.com/mao2009/PSXRecompStudio/issues/447) — GTE/COP2 execution integration; register bank and initial arithmetic kernels are partially implemented
- [Issue #445](https://github.com/mao2009/PSXRecompStudio/issues/445) — SPU register/MMIO substrate (completed via #551)
- [Issue #443](https://github.com/mao2009/PSXRecompStudio/issues/443) — scoped SIO0 model (completed via #548/#549)
- [Issue #601](https://github.com/mao2009/PSXRecompStudio/issues/601) — current synchronization of this status document
- [ADR-015](../adr/015-production-execution-engine-ownership.md) — production execution engine ownership
