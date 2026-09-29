# Persona E2E Pipeline Status (Issue #351)

**Status:** In Progress (Pre-Alpha)

**Authority:** Reference

**Related Issues:** #351 (verification gate), #9 (v0.1.0 milestone), #279 (BIOS-less execution), #205 (Recompiler roadmap), #593 (REGIMM/zero-comparison branch lowering, resolved), #596 (register-shift-amount opcode lowering, resolved), #597 (MULT/DIV/HI-LO lowering, resolved), #599 (LWL/LWR/SWL/SWR lowering, resolved), #628 (Syscall lowering, resolved), #440 (GPU remaining integration), #444 (CD-ROM), #447 (GTE), #441 (rasterization/frame snapshot, completed), #442 (device scheduling, completed), #445 (SPU register/MMIO, completed), #443 (SIO0 scoped model, completed), #601 (this status synchronization)

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
| BIOS HLE (subset) | ⚠ Partial | `BiosHleRuntime` — 7 registered identities: A0:39, A0:3C, B0:3D, A0:3E, B0:3F, B0:56, B0:57 |
| GPU | ⚠ Partial | GP0/GP1/GPUSTAT + VRAM/MMIO (#440), minimal rasterization + deterministic `FrameSnapshot` (#441/#500), VBlank IRQ0 scheduling (#442/#493), production interpreter 32-bit guest MMIO reachability (#572), GPU command IRQ1 delivery (#574), and production `FrameSnapshot` headless evidence (#575); DMA2 remains |
| SPU | ⚠ Partial | Rust-owned register/MMIO model at 0x1F801C00-0x1F801DFF (#445/#551); no ADPCM/ADSR/mixing/reverb/sound-RAM/audio-output model |
| SIO0 | ⚠ Partial | Production-reachable register model + deterministic disconnected-pad transaction path + IRQ7 (#443 via #548/#549); no real host controller or memory-card wire protocol |
| CD-ROM | ⚠ Partial | Register/FIFO substrate, minimum command protocol, DMA3 and IRQ2 are implemented and production-interpreter reachable (#585/#586/#587); sector bytes are test-supplied only — no real disc source, streaming, seek timing or CD audio (#14) |
| GTE | ⚠ Partial | COP2 data/control register bank (#581 / PR #592), RTPS (#582 / PR #590), and NCLIP (#583 / PR #591) are implemented; AVSZ3/AVSZ4 (#584 / PR #589) is under review and native COP2 dispatch/integration remains #447 |
| TITLE_SCREEN | ❌ Not reached | — |

The RUNTIME_EXECUTION row above is this gate's own real-ROM, fixture-gated test
path (generated-host `HostTitleExecutionEngine`, `[Test]`-only). It is separate
from the Studio's own production execution entry point described below
(ADR-015, interpreter-backed) — the two are not the same engine and should not
be conflated.

## First Blocker toward TITLE_SCREEN (as of HEAD)

**Stage:** Recompiler IR lowering (build stage, before RUNTIME_EXECUTION)

**Classification:** `recompiler/lowering coverage` — the production CLI's
whole-program build fails on an unsupported opcode before any guest
instruction executes; BIOS HLE, GPU, CD-ROM and every other runtime boundary
below are unreached by definition until this stage succeeds.

**Description:**

A fresh local run against a legally owned Persona fixture (`rom/PERSONA.chd`),
using the production CLI:

```powershell
dotnet run --project src/PSXRecomp.Cli -- run rom/PERSONA.chd --output out/persona --json --report --frame-evidence
```

showed that **BIOS HLE coverage is not the first blocker.** `psxrecomp run`
fails during the build stage — `CliInput.Lower` → `ReachableProgramBuilder.Build`
(`src/PSXRecomp.Core/Recompiler/ReachableProgramBuilder.cs`) — with exit code 1
(tooling/build failure) and no JSON output (by design: no
`RecompiledArtifactResult` exists yet for this failure class, so the CLI's
`--json` diagnostic path is never reached; see `RunCommand.cs`). `--report` and
`--frame-evidence` are consequently also unreached.

**Why the existing real-ROM tests never exposed this.** The production `run`
path eagerly discovers and lowers the *entire* statically-reachable code graph
from the EXE entry point in one pass (`ReachableProgramBuilder`). The existing
real-ROM gates instead lower a bounded candidate window
(`RealRomCandidateSelector`, used by `RealRomRecompilerVerticalSliceTests`) or
execute segment-by-segment (`ExecutionOrchestrator`, used by
`RealRomTitleExecutionTests`) — neither ever needs the *whole* reachable graph
to be lowerable, so a PASS on either never guaranteed the production CLI's
whole-program build would succeed. This is a structural gap between what the
test suite validates and what the production CLI actually requires, not a
regression in either.

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
6. **Current first blocker (measured after #628, runtime stage).** The same
   CLI command now builds the artifact and fails at RUNTIME_EXECUTION:
   `Blocked` / `RuntimeHandoff`, guest PC `0x80041694`, frame evidence
   `BIOS_HLE_UNSUPPORTED_CALL` (`no-frame-activity`). The generated block at
   that PC is a `JR $t2` whose delay slot loads `$t1 = 0x39` after `$t2 = 0xA0`
   (read from the generated artifact source), i.e. a BIOS A0h call, function
   `0x39`, reaching an unimplemented HLE service (#279 territory). This
   is a runtime blocker, not a recompiler build blocker. Whether the
   `Syscall @ 0x8004143C` block itself executed before this point was not
   established by this measurement.

The build stage now passes and the run reaches `RUNTIME_EXECUTION`, where the
first measured stop is the unsupported BIOS HLE call above (#279); **GPU
integration (#440) and CD-ROM (#444) remain unreached and unranked**. The
generic sub-blocker ordering below is retained as the *anticipated* order once
the recompiler's IR lowering coverage stops rejecting the production CLI's
whole-program build; it is not itself measured evidence.

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
  isolated RTPS/NCLIP kernels (#582/#583 via PRs #590/#591) now exist; AVSZ3/4
  is being reviewed in PR #589. Native COP2/LWC2/SWC2 dispatch is still not
  wired to the implemented register/command semantics (#447).
- **An actual Persona title-screen proof.** No fake frame, hard-coded shortcut,
  or test-only presentation satisfies #351.

**Anticipated generic runtime sub-blockers, once recompiler lowering coverage
stops rejecting the production CLI's whole-program build (not yet reached, not
measured evidence — see the lowering blocker history above):**

1. **BIOS HLE coverage (#279).** Seven identities are registered; unsupported
   calls still stop explicitly with `BIOS_HLE_UNSUPPORTED_CALL`. A production
   CLI run that actually reaches RUNTIME_EXECUTION is required to identify the
   next concrete missing identity/state — none has been observed yet.

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
