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
| BIOS HLE (subset) | ⚠ Partial | `BiosHleRuntime` — 14 registered identities; current inventory is maintained in `docs/runtime/bios-hle-evidence.md`, and the measured Persona path now passes A0:13, B0:19, B0:5B, C0:0A, A0:72 and A0:3F plus SYS(02h) and B0:17 |
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

**Classification:** `BIOS_HLE_UNSUPPORTED_CALL` `B0:4A` (InitCARD2) after `B0:08` OpenEvent and `B0:0C` EnableEvent (exit 1, `state=5`; item 34; tracked by #708 under the gate #351).

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
`CD_init:addr=800500e4`. The generated host now crosses the VBlank exception chain,
the guest's B0:19 hook, the runtime-discovered VBlank callback through the default mixed-execution
path, B0:17 ReturnFromException, and the CD-ROM IRQ2 path through DefInt and the guest callback.
The `CdlDemute` / `CD_init` retry blocker (item 29: `CdlDemute` (0Ch) answered with INT5,
libcd `DiskError` / `CdInit: Init failed`) is now historical. With item 30 (#699) `CD_init`
completes and execution continues into `ResetGraph`; item 31 (#701) registers `A0:49` (GPU_cw) and item 32 (#703) registers
`B0:15` (OutdatedPadInitAndStart) and item 33 (#705) implements `SYS(01h)` (EnterCriticalSection). Item 34 (#687) implements `B0:08` (OpenEvent) and `B0:0C` (EnableEvent); the current
measured stop is `BIOS_HLE_UNSUPPORTED_CALL` `B0:4A` (InitCARD2), tracked by #708. The earlier `OUTER_BUDGET_EXHAUSTED` stop at
`0x80025CCC` in the `CD_sync` VSync loop (items 26-27) is now historical, as is the
`UNRESOLVED_TRANSFER_IN_IMAGE` stop at `0x80025BC8`, which is reproduced only with
`--no-mixed-fallback`; the measurement-only extra root is no longer required. Likewise,
the still earlier `OUTER_BUDGET_EXHAUSTED` wait at `0x800278A8` (#675, items 16-19)
is no longer where the run ends.

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
    (flag 0 here is **not** an unset default: the guest does call `C0:0A(3,0)`, after `B0:19`
    and `B0:5B(0)`; measured on `main` 2c312e6 with a local-only, uncommitted trace of
    `BiosHleRuntime.Invoke`, see the corrected note below). Frame evidence stays
    `unavailable` / `no-frame-activity`. **Next blocker: #660 root-counter event
    delivery**, then #661 (Pad/Card, priority 2) once VBlank continues past priority 1.
    **Correction (measured on `main` 2c312e6).** The Persona guest calls `C0:0A(3,0)`
    itself: `A0:39`, `A0:13(8004EC90)`, `B0:19(8004EC90)`, `B0:5B(0)`, `C0:0A(3,0)`, `A0:72`,
    `B0:3F`, `A0:3F`, then the VBlank INT (item 12's "passes C0:0A" is the accurate
    statement). So `0x130[3] = 0` is a value the guest set, not a boot default of this
    Runtime, and it is not evidence for or against a Pad/Card owner: StartPAD/StartCARD
    were not called before the stop, so #661's ownership (a handler enqueued in priority 2)
    does not apply to this stop.

23. **#660 root-counter event delivery; VBlank continues past priority 1.** The element
    performs the currently modelled delivery of `F2000000h + t, 2` (B0:07 DeliverEvent contract, ADR-014 amendment): with
    no kernel EvCB table (`[0x120]`/`[0x124]` = 0, B0:08 unregistered) nothing can match, so
    delivery succeeds with no effect; an existing or unreadable table fails closed (#687).
    Measured on `main` b584899 + #660, with only `--entry-root 0x80025350 --entry-root
    0x80025614` (`run rom/PERSONA.chd --json --report --frame-evidence`): output unchanged
    (`CD_init:addr=800500e4`); `RuntimeFailure` / `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`
    (exit 1), `guestPc` `0x80000080`; EPC `0x80025CBC`, CAUSE `0x00000400`, SR `0x00000404`;
    message `I_STAT=0x0001, I_MASK=0x000D, pendingEnabled=0x0001|a pending enabled IRQ needs
    a kernel priority-chain element the Runtime does not model`. So the modelled VBlank delivery step was a successful no-op with
    flag 0 (no acknowledge); the chain continued past priority 1 and IRQ0 is still pending. Frame evidence stays
    `unavailable` / `no-frame-activity`. **Next blocker: an unmodelled chain element for the
    still-pending IRQ0 past priority 1.** The measurement does not identify which element
    (the diagnostic is source-neutral by design); psx-spx's priority-2 Pad/Card handler (#661)
    is the documented candidate, not yet confirmed by measurement.
    Scope: this only lets the chain get past priority 1 when no EvCB table exists. No EvCB is matched and no guest callback
    (mode 0x1000 included) runs; Pad/Card ownership, the EvCB allocator, B0:17/B0:18, DMA2 and GTE are not addressed (#661, #687, #664, #665, #440, #447).
24. **#690 priority-3 DefInt; the chain completes into the B0:19 hook.** The stop in item 23 was not
    Pad/Card: the guest itself calls `B0:19(8004EC90)`, `B0:5B(0)` and `C0:0A(3,0)` (the call order
    is `A0:39`, `A0:13`, `B0:19`, `B0:5B`, `C0:0A`, `A0:72`, `B0:3F`, `A0:3F`, then the VBlank INT),
    so priority 1 does not acknowledge and its own hook is meant to. CONFIRMED (psx-spx): the hook
    runs only when the exception handler ran to the end, DefInt (priority 3) does not acknowledge
    unless `C0:0D` enabled it, and `PadCardIrq` (priority 2) is enqueued only by StartPAD2/StartCARD.
    `DefaultChain` now skips the empty priority 2 and runs `BiosDefaultInterruptHandler`: nothing
    pending, or only IRQ0 with no EvCB table, completes the chain (existing
    `BiosExceptionCompletion`: the B0:19 hook, else the default Exit); any other pending enabled IRQ,
    several, or an existing EvCB table still stops as `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`.
    Measured on `main` 2c312e6 + #690 (`run rom/PERSONA.chd --json --report --frame-evidence`):
    with only the two entry roots the artifact enters the hook (`$v0 = 1`, PC = the saved `$ra`
    `0x800253B8`), the guest's own interrupt dispatcher acknowledges IRQ0 (`I_STAT` written
    `0xFFFE`, from `0x800254F8`), and the run stops at `UNRESOLVED_TRANSFER_IN_IMAGE` (exit 2,
    `0x80025BC8`, the callback the dispatcher calls; a caller-supplied root, as for items 7-9). With
    `--entry-root 0x80025BC8` added the guest calls `B0:17` and the run stops at
    `BIOS_HLE_UNSUPPORTED_CALL` `B0:17` (exit 1); the interpreter path (frame evidence) stops at the
    same `B0:17`. **Next boundary: with the established two roots, the manual-root / callback
coverage gap (#693); past it (measurement root), B0:17 ReturnFromException (#664).** The IRQ0 ack, the hook-entry
    registers and the ack PC were observed with a local-only, uncommitted trace before this change.
    UNKNOWN: retail DefInt event delivery, `$k0/$k1` and SR at hook entry, priority 0, `C0:0D`.
    Scope: no Pad/Card (#661), no EvCB matching (#687), no B0:17/B0:18 (#664, #665), no `C0:0D`.

25. **#693 opt-in mixed execution; the artifact crosses the callback.** The manual-root gap of item 24
    (`UNRESOLVED_TRANSFER_IN_IMAGE` at `0x80025BC8`) is closed by an **opt-in** mechanism, not by a root. The target
    is a runtime-discovered, in-image, register-indirect target: CONFIRMED provenance (investigation in #693) is
    that the guest library routine at `0x80025614` (the established root) stores the callback pointer with an
    ordinary store (`sw` at `0x8002568C`) into a RAM table at `0x8004EC5C + 4*IRQ`, and the guest dispatcher
    reads it (`lw` at `0x800254FC`) and calls it with `jalr` at `0x8002550C`. With `--mixed-fallback` the host runs
    the callback on the interpreter over the host-owned device graph and returns to the artifact at the compiled
    block `0x80025514`. Measured on `main` d4d729c + #693 with only
    `--entry-root 0x80025350 --entry-root 0x80025614 --mixed-fallback` (`run rom/PERSONA.chd --json`): 1 handoff,
    84 interpreter instructions, 74 RAM pages in (303,104 bytes; 606,208 hex characters on the pipe), 1 page back;
    the run then continues in the artifact and stops at `BIOS_HLE_UNSUPPORTED_CALL` `B0:17` (exit 1; #664), the
    same next blocker as with the measurement-only `--entry-root 0x80025BC8`. Timings (measurement only, three runs):
    sync 10.2-12.3 ms in total for the Persona handoff, interpreter execution 2.3-2.4 ms; a synthetic full-RAM
    entry (all 512 pages dirty, 2,097,152 bytes, 4,194,304 hex characters) costs 36-38 ms, against 0.265 ms for the
    bare in-process memory copy. Without the flag the run is byte-for-byte what item 24 recorded
    (`UNRESOLVED_TRANSFER_IN_IMAGE`, exit 2, guest PC `0x80025BC8`).
    Scope: no Persona address enters `src/`; no Pad/Card (#661), no EvCB matching (#687), no B0:17/B0:18
    (#664, #665); the mechanism is generic (ADR-012 amendment) and was opt-in at measurement time; the CLI gate
    became the default in item 27.

26. **#664 B0:17 ReturnFromException registered; the stop moves off the BIOS call and onto the
    budget.** The remaining B0:17 stop of items 24-25 was a real BIOS-service gap: `B0(17h)`
    ReturnFromException was unregistered. It is now registered (arity 0) as the kernel's
    exception-return operation, and it restores through the *same* `BiosExceptionCompletion`
    source of truth the #662 completion uses: read the current TCB through
    `[0x108]`→PCB→TCB, produce the restored register file/HI/LO/SR/PC as one CPU-state
    replacement (`BiosCpuStateMutation`), and every execution form (interpreter, generated host,
    mixed fallback) applies that replacement through one shared implementation each — the
    continuation is the saved EPC, not `$ra`, and RFE stays the CPU's own pop
    (ADR-014 amendment). Fail closed: wrong arity = `BIOS_HLE_INVALID_ARGUMENTS`; no live register
    file or an unreadable TCB = `BIOS_HLE_UNSUPPORTED_STATE`; B0:18 ResetEntryInt stays
    unregistered (#665). Measured on `main` HEAD (B0:17 branch) with only
    `--entry-root 0x80025350 --entry-root 0x80025614 --mixed-fallback`
    (`run rom/PERSONA.chd --json`): the B0:17 stop is gone. The run now crosses the callback
    (2 handoffs, 168 interpreter instructions at `0x80025BC8`, 75 RAM pages in, 2 pages back;
    last return PC `0x80025514`) and ReturnFromException, and then stops at
    `OUTER_BUDGET_EXHAUSTED` (exit 2, `state=3`, `recompiled-host-artifact`) with guest PC
    `0x80025CCC`, output unchanged (`CD_init:addr=800500e4`). **The B0:17 blocker (#664) is
    resolved.** Vcount now advances (each VBlank callback runs), so the run is no longer the
    item-17 permanent wait; it is the CD_sync VSync loop consuming the outer execution budget.
    **Next boundary: whether `CD_sync` completes.** Not yet measured (budget): whether the
    VBlank deadline path (`VSync(-1) + 0x3C0` from item 17) times out and retries CD, and whether
    the CD-ROM IRQ2 callback `0x800281F8` is needed to end the wait (#444). Frame evidence stays
    `unavailable` / `no-frame-activity`.

27. **#693 closed: the callback needs no flag and no root.** The mechanism of item 25 is now the
    `run` default: `psxrecomp run` enables mixed execution unless `--no-mixed-fallback` is given,
    while the engine/launcher keep their explicit `MixedFallbackOptions` and every eligibility
    rule is untouched (ADR-012 amendment). Measured on this branch with the item-24 command exactly
    as recorded (`run rom/PERSONA.chd --entry-root 0x80025350 --entry-root 0x80025614 --json
    --report --frame-evidence`): exit 2, `OUTER_BUDGET_EXHAUSTED` (`state=3`,
    `recompiled-host-artifact`) at guest PC `0x80025CCC` — the same stop item 26 recorded with the
    flag — output unchanged (`CD_init:addr=800500e4`); `mixedFallback` reports 2 handoffs, 2
    returns, 168 interpreter instructions, 75 RAM pages in, 2 back, `0x80025BC8` entered twice,
    last return PC `0x80025514`, and `--report` wrote its bundle. `--no-mixed-fallback` on the same
    input reproduces item 24 byte-for-byte (`UNRESOLVED_TRANSFER_IN_IMAGE`, exit 2, `state=4`,
    `0x80025BC8`), so the fail-closed stop is one flag away and stays tested. The `run --json`
    envelope now always carries `mixedFallback` while mixed execution is on (zero counts when
    nothing handed off) and omits it under `--no-mixed-fallback`. **Next boundary: unchanged —
    whether `CD_sync` completes (item 26), tracked by the gate #351; no new stop appeared.**
    Scope: no Persona address enters `src/`; `psxrecomp recompile` and embedders unchanged.

28. **CD_sync root cause (#351): the CD-ROM interrupt-enable reset value was 0; the stop moves to the IRQ2 chain.**
    Measured on `main` `f1c445a` (item-27 command): `OUTER_BUDGET_EXHAUSTED` at `0x80025CCC` is only where the
    budget ended (inside libgpu `VSync`, called from `CD_sync`). Retired instructions 1,230,672; two VBlank exceptions
    (I_STAT=0x0001, I_MASK=0x000D); the guest issued exactly one CD command, `CdlNop` (`0x01` to `0x1F801801`, never
    a write to `0x1F801802`), then polled `VSync(-1)` (2 MMIO reads per pass, 19,517 passes) with no further CD
    register access: `CD_sync` waits for the IRQ2 callback. Raising the budget does not help (no CD state change).
    ROOT CAUSE: `CdRomDevice` reset its interrupt-enable register to 0, so `HasInterrupt` stayed false and the
    scheduler never raised IRQ2. libcd never writes the register (the boot BIOS leaves it as reset); DuckStation's
    `CDROM::Reset`/`SoftReset` set it to `0x1F`. Fix: the reset value is `0x1F` (constructor and `Reset()`); a guest
    write of 0 still masks the line. After the fix the same command stops at `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`
    (I_STAT=0x0004, I_MASK=0x000D, EPC `0x80027848`): IRQ2 is pending but the kernel priority chain has no CD-ROM
    element (#697). Not measured: whether the chain lets `CD_sync` complete; real disc data is not reached.

29. **#697: CD-ROM IRQ2 is DefInt's (priority 3); the guest callback runs and `CD_sync` completes.**
    Reproduced on `main` `d9782b6` (item-28 command): exit 1, `BIOS_EXCEPTION_CHAIN_UNSUPPORTED`, I_STAT=0x0004,
    I_MASK=0x000D, EPC `0x80027848`, CAUSE=0x400, SR=0x404. CONFIRMED (PCSX-Redux OpenBIOS `IRQVerifier`, `EVENT_CDROM =
    0xF0000003` in `common/kernel/events.h`; psx-spx priority list): the kernel delivers IRQ2's event `F0000003h,1000h`
    from `DefInt` (priority 3) after priorities 0-2, acknowledging I_STAT only when `C0:0D` auto-ack is on (default off); the
    retail priority-0 `CdromDmaIrq`/`CdromIoIrq` are enqueued by the BIOS's own CD init, which a BIOS-less run never
    executes (C0:02 is unregistered). Fix: `BiosDefaultInterruptHandler` now models exactly one of IRQ0 or IRQ2 pending
    and enabled (no EvCB table: no-op delivery); anything else (several, other IRQs, an existing EvCB table, #687)
    still fails closed. Neither the CD controller flag nor I_STAT is touched by the kernel: the guest's B0:19 hook
    dispatches `InterruptCallback(2)` itself. Measured with the same two roots: the hook is entered, the IRQ2 callback
    (guest `0x800281F8`, runtime-discovered through mixed execution, 1706 entries) acknowledges the controller (I_STAT write `0xFFFB` by the guest dispatcher, then CD
    interrupt flag `0x1F801803` and the response read), and the guest issues CD commands `0x01` (CdlNop), `0x0A` (CdlInit)
    and `0x0C` (CdlDemute) in turn: `CD_sync` completed for Nop and Init (the next command is issued only afterwards),
    while `CdlDemute` is unimplemented in `CdRomDevice` and answered with INT5. libcd then prints `DiskError: ...` and
    `CdInit: Init failed` and retries `CD_init` indefinitely. Frame evidence stays `no-frame-activity`; no real sector
    data is read. Next blocker: #699.

30. **#699: CdlMute/CdlDemute implemented; `CD_init` completes; the stop moves to A0:49.**
    Reproduced on `main` `102a0cc`: the CD commands written to `0x1F801801` repeat `0x01`, `0x0A`, `0x0C`, then libcd prints
    `DiskError` / `CdInit: Init failed` and retries (`ARTIFACT_TIMEOUT`). CONFIRMED (psx-spx: `0Bh Mute` and `0Ch Demute`
    answer `INT3(stat)`, `0Ah Init` answers `INT3(late-stat), INT2(stat)`; DuckStation: Mute/Demute set a `muted` flag
    and send ACK + stat, `SoftReset` (Init) and `Reset` clear it). Fix: `CdRomDevice` supports Mute/Demute with the existing
    response/IRQ path (INT3 + stat, no parameters else INT5, `IsMuted` cleared by Init/Reset; no audio consumer exists yet);
    every other unknown command still answers INT5. Measured with the same two roots: the commands are now `0x01`, `0x0A`,
    `0x0C` once each, then the guest writes the CD volume registers (indexes 2/3, accepted and ignored by the device) and
    `CD_init` returns: no `DiskError`, no retry. Output continues with `ResetGraph:jtb=80054dbc,env=80054e04`, then the
    run stops with `BIOS_HLE_UNSUPPORTED_CALL` `A0:49` (exit 1, `state=5`, #701). No sector read command was issued:
    real disc data is not reached (#14). Frame evidence stays `no-frame-activity`.

31. **#701: A0:49 GPU_cw registered; the stop moves to B0:15.**
    Reproduced on `main` `40648a8`: `ResetGraph` then `BIOS_HLE_UNSUPPORTED_CALL` `A0:49`. CONFIRMED (PCSX-Redux OpenBIOS
    `openbios/gpu/gpu.c`): `GPU_cw(cmd)` is `GPU_sync()` then `GPU_DATA = cmd` (GP0, `0x1F801810`), returning `GPU_sync`'s
    result (0). `GPU_sync` waits for GPUSTAT bit 28 when the DMA direction (bits 29-30) is 0, otherwise for DMA2 CHCR busy
    to clear and GPUSTAT bit 26, then writes GP1(04h). Fix: `BiosGpuCommandService` (arity 1, returns 0) reaches the
    existing Runtime device graph through a new `IGuestDeviceAccess` boundary that the interpreter engine and the artifact
    bridge attach to the BIOS runtime; a wait that is not already satisfied, an unmodelled register or a runtime with no
    devices attached fails closed (`BIOS_HLE_UNSUPPORTED_STATE`, nothing written to GP0). The retail spin/timeout/abort is
    not modelled (a BIOS service cannot advance device time). Measured with the same two roots: the call is accepted and
    the run continues past it, then stops with `BIOS_HLE_UNSUPPORTED_CALL` `B0:15` (exit 1, `state=5`, #703). Output
    `CD_init:addr=800500e4`, `ResetGraph:jtb=80054dbc,env=80054e04`; frame evidence `no-frame-activity`; no sector read
    (#14).

32. **#703: B0:15 OutdatedPadInitAndStart registered; the stop moves to SYS(01h).**
    Reproduced on `main` `aedd759`: `BIOS_HLE_UNSUPPORTED_CALL` `B0:15` after `GPU_cw`. CONFIRMED (psx-spx kernelbios;
    PCSX-Redux OpenBIOS `initPadHighLevel` agrees): the call fails (returns 0) unless `type` is `20000000h`/`20000001h`;
    otherwise it FFh-fills the hidden buf1/buf2, calls `InitPad(buf1,22h,buf2,22h)` (zero-fills), calls `StartPad()`
    (enqueues `PadCardIrq`, "initializes some flags"), memorizes `button_dest` and returns 2. Measured: Persona calls it
    twice with `type=0x20000001, button_dest=0x800563F0, 0x14, 0` (identical). Fix: `BiosPadState` (arity 4) records "PadCardIrq
    enqueued" and `button_dest` in a guest-RAM kernel variable and returns 2 (0 for any other type, with no state change; that
    is the documented return, not a failure). `button_dest` is only memorized, never dereferenced. Not modelled:
    the hidden buffers (their only reader B0:16 is unregistered), StartPad's flags including auto-ack (left to #661; the
    B0:5B setting is untouched), the stores of the unused parameters to the caller's stack. Because an element now exists at
    priority 2, `DefaultChain` stops closed (`BIOS_EXCEPTION_CHAIN_UNSUPPORTED`, "PadCardIrq ... #661") when it would claim an
    exception (IRQ0 pending and enabled) instead of silently skipping it. Measured with the same two roots: B0:15 is accepted,
    the run continues and stops with `BIOS_SYSCALL_UNSUPPORTED` `SYS(01h)` (#705). No Pad IRQ was taken and no SIO0 access
    occurred, so #661 is not yet required. Frame evidence stays `no-frame-activity`; no sector read (#14).

33. **#705: SYS(01h) EnterCriticalSection implemented; the stop moves to B0:08.**
    Reproduced on `main` `f0beb42`: `BIOS_SYSCALL_UNSUPPORTED` `SYS(01h)` at guest PC `0x8004143C` after B0:15. CONFIRMED
    (psx-spx; PCSX-Redux OpenBIOS `syscallVerifier` agrees): SYS(01h) clears SR bits 2 and 10 of the exception frame
    (bit 2 reaches IEc through the RFE on return) and returns 1 if both were set, else 0; there is no nesting count or
    saved state, so Enter, Enter, Exit leaves interrupts enabled. Fix: `BiosKernelSyscallDispatch` handles number 1 as the
    mirror of the existing SYS(02h) on the same SR the exception entry pushed; the outcome gains an optional `$v0` that
    the interpreter engine and the generated-host bridge apply (SYS(02h) leaves `$v0` unchanged). No new state. Measured
    with the same two roots: one SYS(01h), `SR 0x404 -> 0`, `$v0 = 1` (an earlier SYS(02h) at `0x80041714` is unrelated);
    SYS(02h) is not reached again before the stop. The run then initialises the SPU registers (`0x1F801Dxx` writes) and
    stops with `BIOS_HLE_UNSUPPORTED_CALL` `B0:08` OpenEvent (exit 1, `state=5`; #687). No pending enabled IRQ was taken, no
    Pad IRQ/SIO0 access (#661 not required), no sector read, no DMA, no GPU frame activity (`no-frame-activity`).

34. **#687: B0:08 OpenEvent and B0:0C EnableEvent implemented over one guest-RAM EvCB table; the stop moves to B0:4A.**
    Reproduced on `main` `965c581`: `BIOS_HLE_UNSUPPORTED_CALL` `B0:08` (exit 1, `state=5`, guest PC `0xB0`). Measured
    arguments of the single call (caller `0x800186A0`, inside the libspu-style init after the SPU register writes):
    `OpenEvent(class=F0000009h, spec=0020h, mode=2000h, func=0)`, i.e. SPU IRQ9 "command completed", ready mode, no
    callback. The guest stores the returned handle at `0x8004E3E4`, then calls `B0:0C EnableEvent(handle)` and `SYS(02h)`.
    CONFIRMED (psx-spx event-functions/control-blocks; PCSX-Redux OpenBIOS `events.c`): first free EvCB slot, handle
    `F1000000h + slot`, `FFFFFFFFh` when full, initially disabled (status 1000h), EnableEvent always returns 1. The table
    (`[0x120]` address, `[0x124]` size in bytes, 1Ch bytes per EvCB) is seeded on first OpenEvent at `0xE400` with 16
    entries; it is the only event state, so the root-counter and DefInt delivery (`BiosEventControlBlocks.Deliver`)
    now match against it instead of failing closed whenever a table exists. An enabled mode-1000h event with a callback
    still fails closed on delivery. With the same two roots: OpenEvent x1 (handle `F1000000h`), EnableEvent reached, the
    run passes both and stops with `BIOS_HLE_UNSUPPORTED_CALL` `B0:4A` (exit 1, `state=5`; #708). TestEvent, CloseEvent,
    callbacks: not reached. No Pad IRQ/SIO0 access (#661 not required), no sector read, no DMA, no GPU frame activity
    (`no-frame-activity`).

The build stage now passes and the run reaches `RUNTIME_EXECUTION`. The kernel exception handler's priority chain (`BIOS_EXCEPTION_CHAIN_UNSUPPORTED`, item 21, #662) was the previous stop: CPU INT delivery (#680) and the
C0:06 entry work; since #660 the VBlank IRQ0 element's modelled delivery step is a successful no-op (no EvCB table) and the chain continues, and the stop was IRQ0 still pending past priority 1 with no modelled element to claim it (item 23). Since #690 priority-3 DefInt completes that chain into the guest's B0:19 hook (item 24). With the established two entry roots the artifact boundary was `UNRESOLVED_TRANSFER_IN_IMAGE` at the guest's VBlank callback `0x80025BC8` (exit 2; a manual-root / callback coverage gap, #693, closed by the default in item 27). The mechanism first measured with the opt-in `--mixed-fallback` (item 25) crosses that callback and stopped at `B0:17` (#664). Since #664 (item 26) B0:17 ReturnFromException is registered and the run crosses it too: the VBlank callback runs (Vcount advances), RFE resumes at the saved EPC, and item 28 reaches `BIOS_EXCEPTION_CHAIN_UNSUPPORTED` for the pending CD-ROM IRQ2 (`I_STAT=0x0004`, #697); item 29 (#697) resolves that stop: DefInt delivers the IRQ2 event, the guest's IRQ2 callback runs and `CD_sync` completes for CdlNop and CdlInit. CdlDemute (`0x0C`) was then the blocker (libcd `DiskError` / `CdInit: Init failed`, #699); item 30 resolves `CdlDemute`, item 31 resolves `A0:49`, item 32 resolves `B0:15`, item 33 resolves `SYS(01h)`, item 34 resolves `B0:08` OpenEvent and `B0:0C` EnableEvent (#687), and the current first blocker is `B0:4A` InitCARD2 (#708). Before it the stop was `OUTER_BUDGET_EXHAUSTED` at `0x800278A8` (after
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

1. **BIOS HLE coverage (#279).** The current Persona path has exercised the
   startup services through A0:49 `GPU_cw` and B0:15, and unsupported calls
   still fail explicitly with `BIOS_HLE_UNSUPPORTED_CALL`. The current first
   blocker is the unsupported `B0:08` OpenEvent (#687); add further services
   only when a measured execution path requires them.

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
