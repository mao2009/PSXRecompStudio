# Persona E2E Pipeline Status (Issue #351)

**Status:** In Progress (Pre-Alpha)

**Authority:** Reference

**Related Issues:** #351 (verification gate), #9 (v0.1.0 milestone), #279 (BIOS-less execution), #205 (Recompiler), #366 (full-title execution orchestrator), #380 (production execution engine ownership, ADR-015), #409 (real PS-X EXE → production execution path bridge)

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
    GPU / SPU / CD-ROM — hardware rendering + audio + disc (⚠ NOT YET IMPLEMENTED)
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
| BIOS HLE (subset) | ⚠ Partial | `BiosHleRuntime` — 5 of 256+ services |
| GPU | ⚠ Partial | Managed register/VRAM model (#440) — register/MMIO semantics + VRAM transfers; not wired into title execution, no rasterization |
| SPU / CD-ROM | ❌ Not implemented | Interface-only |
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

Only five BIOS services are currently registered: A0:3C putchar, A0:3E puts,
B0:3F puts alias, B0:56 GetC0Table, and B0:57 GetB0Table. The first unregistered
call fails closed with `BIOS_HLE_UNSUPPORTED_CALL`. Therefore broader BIOS HLE
coverage is the next generic runtime blocker after the implemented bounded
execution stages. Once the required BIOS calls are covered, GPU/SPU/CD-ROM
producers remain necessary before an actual title screen can be reached.

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

- ~~**Real disc/EXE → production execution wiring.**~~ Wired in this PR: the
  Studio's `RealRomTitleExecutionService`/`RunRealTitleCommand` retain the
  analyzed PS-X EXE and run it through the production execution path. This is
  distinct from the E2E gate's fixture-gated Test execution path described
  above.
- **Disc-image acquisition (file I/O) in the product.** The Application layer
  is forbidden `System.IO.File`/`Directory` by the architecture contract
  (`src/architecture.contract.json`), so the product action consumes pre-read
  disc bytes (`MainWindowViewModel.DiscImageBytes`); reading a disc image from
  disk stays behind the Infrastructure seam, deferred to Issue #38.
- **A production generated-host (recompiled) execution backend.** The Studio's
  production path is interpreter-backed only (ADR-015); compiling recompiled
  guest code and running it as the product's execution backend is deferred
  (Option B in ADR-015), not implemented.
- Runs the entire Persona executable to an **actual title screen** — even once
  a real title is loaded, the boot path needs BIOS HLE beyond the five services
  above and GPU/SPU/CD-ROM MMIO.
- GPU / SPU / CD-ROM hardware: a managed GPU register/VRAM model now exists
  (#440) but is not wired into the title execution path and performs no
  rasterization; the SPU/CD-ROM interfaces still have no production
  implementation. Hardware communication beyond the modeled GPU registers would
  hit MMIO open-bus (reads return 0, writes ignored).

**Remaining generic runtime sub-blockers in order:**

1. **BIOS HLE coverage**: Only 5 services implemented (A0:3C putchar, A0:3E puts,
   B0:3F puts alias, B0:56 GetC0Table, B0:57 GetB0Table). First unregistered call
   produces diagnostic code `BIOS_HLE_UNSUPPORTED_CALL`.

2. **GPU rendering**: the managed GPU model (#440) covers register semantics and
   VRAM transfers only; rasterization and display output are not implemented, so
   no rendering occurs.

3. **SPU / CD-ROM**: Same situation — interface-only.

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
- [Issue #279](https://github.com/mao2009/PSXRecompStudio/issues/279) — BIOS-less execution policy
- [Issue #362](https://github.com/mao2009/PSXRecompStudio/issues/362) — recompiled-path BIOS dispatch (Worker A)
- [Issue #366](https://github.com/mao2009/PSXRecompStudio/issues/366) — full-title execution orchestrator (this work)
- [Issue #380](https://github.com/mao2009/PSXRecompStudio/issues/380) / [ADR-015](../adr/015-production-execution-engine-ownership.md) — production execution engine ownership; the Studio's diagnostic execution entry point described above
