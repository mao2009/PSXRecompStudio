# Persona E2E Pipeline Status (Issue #351)

**Status:** In Progress (Pre-Alpha)

**Authority:** Reference

**Related Issues:** #351 (verification gate), #9 (v0.1.0 milestone), #279 (BIOS-less execution), #205 (Recompiler roadmap), #440 (GPU remaining integration), #441 (rasterization/frame snapshot, completed), #442 (device scheduling, completed), #445 (SPU register/MMIO, completed), #443 (SIO0 scoped model, completed), #552 (this status synchronization)

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
    GPU / SPU / SIO0 / CD-ROM — partial hardware models; production integration remains incomplete
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
| CD-ROM | ❌ Not implemented | Register/command/DMA3/IRQ2 implementation remains #444 |
| TITLE_SCREEN | ❌ Not reached | — |

The RUNTIME_EXECUTION row above is this gate's own real-ROM, fixture-gated test
path (generated-host `HostTitleExecutionEngine`, `[Test]`-only). It is separate
from the Studio's own production execution entry point described below
(ADR-015, interpreter-backed) — the two are not the same engine and should not
be conflated.

## First Blocker toward TITLE_SCREEN (as of HEAD)

**Stage:** BIOS HLE coverage

**Classification:** `runtime/BIOS coverage` — the bounded execution loop exists,
but the BIOS service surface needed by a real title is still incomplete.

**Description:**

The full-title execution orchestrator (Issue #366) now runs recompiled guest code
through a bounded CPU loop: `ExecutionOrchestrator` in `PSXRecomp.Core.Execution`
drives an `IRecompiledExecutionEngine` (the Test assembly's IR, interpreter, and
generated-host `HostTitleExecutionEngine`) across multiple segments with outer and
per-segment budgets. Guest segments end on unresolved control transfers at the
Runtime/BIOS boundary; the shared `BiosVectorDispatch` contract (PR #364,
interpreter + recompiled paths) services A0/B0/C0 jump-table calls in-band, and a
handoff decides continue/exit/return/pause. A segment that stops at an unresolved
PC with no continuation rule is classified (UnsupportedTransfer), never a hang.

It runs on real-ROM functions through `RealRomTitleExecutionTests`
(real-fixture-gated; skips with no `rom/*.chd`).

Seven BIOS identities are currently registered: A0:39 InitHeap, A0:3C putchar,
B0:3D putchar alias, A0:3E puts, B0:3F puts alias, B0:56 GetC0Table, and
B0:57 GetB0Table. An unregistered call still fails closed with
`BIOS_HLE_UNSUPPORTED_CALL`.

No newer legal-Persona fixture result is recorded in this repository after the
recent Runtime work, so this document does not invent a new first failing BIOS
identity. Based on the current production contract and recorded #351 evidence,
**BIOS HLE coverage remains the first documented generic code-level blocker**.
A fresh local run with a legally owned Persona fixture is required to identify
the next concrete unsupported boundary. After that boundary is cleared, the
remaining production GPU/frame integration and any actually exercised
SPU/CD-ROM support must be resolved before the title screen can be claimed.

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
- **CD-ROM runtime hardware.** The command/status/FIFO model, DMA3 data path and
  IRQ2 remain unimplemented (#444).
- **An actual Persona title-screen proof.** No fake frame, hard-coded shortcut,
  or test-only presentation satisfies #351.

**Remaining generic runtime sub-blockers in order:**

1. **BIOS HLE coverage (#279).** Seven identities are registered; unsupported
   calls still stop explicitly with `BIOS_HLE_UNSUPPORTED_CALL`. A fresh legal
   Persona fixture run should identify the next concrete missing identity/state.

2. **Production GPU/frame integration (#440 / #351).** Production interpreter
   32-bit GPU MMIO reaches the existing managed GPU state (#572) and GPU
   command IRQ1 is scheduler-delivered (#574). `FrameSnapshot` is exposed through the headless #351 evidence path (#575).
   The remaining concrete GPU integration gap is DMA2 data movement.

3. **Evidence-gated hardware after the next real boundary.** SPU register/MMIO
   exists, while audio behavior is still absent; CD-ROM remains unimplemented.
   MDEC/GTE/CD-ROM/SPU work should be promoted only when the real execution path
   demonstrates that it is the next blocker rather than implemented by issue
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
- [Issue #279](https://github.com/mao2009/PSXRecompStudio/issues/279) — BIOS-less execution / remaining HLE coverage
- [Issue #440](https://github.com/mao2009/PSXRecompStudio/issues/440) — remaining GPU production integration, DMA2 and IRQ1
- [Issue #444](https://github.com/mao2009/PSXRecompStudio/issues/444) — CD-ROM register/DMA3/IRQ2 model (evidence-gated)
- [Issue #445](https://github.com/mao2009/PSXRecompStudio/issues/445) — SPU register/MMIO substrate (completed via #551)
- [Issue #443](https://github.com/mao2009/PSXRecompStudio/issues/443) — scoped SIO0 model (completed via #548/#549)
- [Issue #552](https://github.com/mao2009/PSXRecompStudio/issues/552) — synchronization of this status document
- [ADR-015](../adr/015-production-execution-engine-ownership.md) — production execution engine ownership
