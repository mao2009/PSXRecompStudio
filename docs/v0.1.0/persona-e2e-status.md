# Persona E2E Pipeline Status (Issue #351)

**Status:** In Progress (Pre-Alpha)

**Authority:** Reference

**Related Issues:** #351 (verification gate), #9 (v0.1.0 milestone), #279 (BIOS-less execution), #205 (Recompiler)

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
[3] ANALYSIS           — CHD → ISO → SYSTEM.CNF → PSX EXE → decode → CFG → COMPLETE
[4] RECOMPILER_SLICE   — candidate function selection → recompilation → differential validation
[5] RUNTIME_EXECUTION  — full-title execution loop (⚠ NOT YET IMPLEMENTED)
        ↓
    BIOS HLE dispatch  — A0/B0/C0 jump-table service calls
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
| RUNTIME_EXECUTION | ❌ Not implemented | — |
| BIOS HLE (subset) | ⚠ Partial | `BiosHleRuntime` — 5 of 256+ services |
| GPU / SPU / CD-ROM | ❌ Not implemented | Interface-only |
| TITLE_SCREEN | ❌ Not reached | — |

## First Blocker (as of HEAD 7799a9b)

**Stage:** RUNTIME_EXECUTION

**Classification:** `artifact-build-runtime wiring` — missing execution orchestrator

**Description:**

No full-title execution orchestrator exists. After analysis and single-function
recompilation (stages 3–4), there is no component that:

- Runs the entire Persona executable through a CPU execution loop
- Services BIOS calls from running recompiled code at runtime
- Connects the recompiled guest to GPU/SPU/CD-ROM hardware

The PSXRecompStudio application is a GUI (Avalonia) with no CLI execution entry point.
The existing test infrastructure only covers individual-function differential validation,
not full-title execution.

**Sub-blockers in order** (after a full execution loop exists):

1. **BIOS HLE coverage**: Only 5 services implemented (A0:3C putchar, A0:3E puts,
   B0:3F puts alias, B0:56 GetC0Table, B0:57 GetB0Table). First unregistered call
   produces diagnostic code `BIOS_HLE_UNSUPPORTED_CALL`.

2. **Recompiled-path BIOS vector dispatch**: The generated-host path cannot yet
   dispatch BIOS trampoline vectors (interpreter-path dispatch landed in PR #364;
   recompiled-path equivalent tracked in Issue #362 / Worker A).
   Parity marker: `BiosPatchedTargetExecutionTests.RecompiledPath_CannotYetDispatchABiosVector_AndSaysSoInTheLowering`.

3. **GPU rendering**: `IGpu` is an interface with no production implementation.
   Hardware communication would hit MMIO open-bus (reads return 0, writes ignored).

4. **SPU / CD-ROM**: Same situation — interface-only.

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
- `0` — all implemented stages passed
- `1` — a stage failed  
- `2` — no fixture present (SKIP)

Output: machine-readable JSON in `reports/e2e/persona-e2e-gate-result.json` (git-ignored).

### Run individual stages

```powershell
# Stage 3 — Analysis only:
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj `
  --filter 'FullyQualifiedName~RealRomAnalysisSkillTests' -c Release

# Stage 4 — Recompiler vertical slice:
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj `
  --filter 'FullyQualifiedName~RealRomRecompilerVerticalSliceTests' -c Release
```

With no fixture present, both skip explicitly with reason:
`skipped: no real-ROM fixture found under rom/*.chd (disc images are never committed)`

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
