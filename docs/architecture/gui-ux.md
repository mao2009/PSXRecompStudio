# GUI / UX Architecture

**Status:** Stable

**Authority:** SSOT

**Related Issues:** #75

## Purpose

PSXRecompStudio GUI is a modern developer-tool workspace for PlayStation analysis, understanding, recompilation, AI-assisted investigation, harnessing, and validation.

The goal is not cosmetic polish alone. The GUI must make the capabilities of the underlying architecture understandable and usable without coupling domain semantics to Avalonia.

## Workspace model

```text
┌─────────────────────────────────────────────────────────────┐
│ Project / Runtime / Build / Debug status                    │
├────────────┬────────────────────────────────┬───────────────┤
│ Workspace  │ Main Work Surface              │ Inspector     │
│ Navigator  │                                │ / Details     │
├────────────┴────────────────────────────────┴───────────────┤
│ Problems | Diagnostics | Output | AI Activity | Test Results │
└─────────────────────────────────────────────────────────────┘
```

### Primary areas

- Project Dashboard
- Analysis
- CPU / Architecture
- Analyzer
- AI Analysis
- Harness
- Testing
- Runtime / Game Preview

## UX principles

- Modern developer-tool appearance rather than a launcher-only interface.
- High information density with strong hierarchy.
- Low visual noise and restrained use of color.
- Error / Warning / Info severity must communicate meaningful importance.
- Overview first; evidence and details available through drill-down.
- Consistent terminology and navigation across analysis features.
- Dark and light themes should be supported by the architecture.
- UI is not the source of truth for CPU, analyzer, recompilation, or test semantics.

## Diagnostic presentation

Analyzer and related systems should expose structured findings instead of UI-formatted strings.

A finding may contain:

- Rule ID
- Severity
- Category
- Address/location
- Message
- Evidence
- Suggested action/fix
- Confidence
- Related symbol/function/module
- Suppression or acknowledgement state

The same structured result should be usable by GUI, CLI, automation, and AI workflows.

## AI interaction model

AI is primarily an analysis/investigation capability rather than an unconstrained chatbot.

Preferred flow:

```text
Evidence → AI interpretation → Hypothesis → Confidence → Human confirmation
```

AI context may include architecture SSOT, CPU information, symbols, analyzer findings, call relationships, and relevant project state.

## Harness and testing interaction

Findings should naturally lead into reproducible validation.

The future UI should support selecting a target, inspecting inputs/state, running a harness, comparing expected/actual state, and linking failures back to findings. Original-versus-recompiled comparison belongs in the testing workflow.

## Runtime interaction

A future runtime surface may expose frame/FPS, CPU/runtime state, active function/context, analyzer monitoring, and test/debug controls. Runtime integration is a later implementation target.

## Architectural boundary

```text
Avalonia UI
    ↓
Application Layer (Commands / Queries / orchestration)
    ↓
Domain / Analysis / Test Models
    ↓
Infrastructure
```

- UI-specific ViewModels and adapters stay above the application/domain layers.
- CPU rules, analyzer rules, and recompilation semantics must not be encoded in views.
- Application contracts should remain suitable for future CLI, automation, and AI integration.
- Domain results should be testable and serializable where practical.

## Runtime frame presentation (Issue #455, post-v0.1)

The Studio shows the Runtime's existing `FrameSnapshot` (#441/#575) and nothing else: Runtime/GPU -> `FrameSnapshot` -> `PresentationFrame` (Application-layer BGRA32 copy) -> Avalonia `Image`. Studio owns no GPU, VRAM or rasterizer.

- The presentation copy is built off the UI thread for real-title runs; the source snapshot is never mutated, and window resize only rescales at draw time (nearest-neighbor, `BitmapInterpolationMode.None`).
- Explicit states: `NoFrame` (no Runtime frame evidence; nothing is drawn, no placeholder), `Unsupported` (empty display region), `Ready`.
- The frame is captured after a run completes at the caller's chosen boundary; live/continuous presentation and VBlank pacing are not part of this slice.
- Runtime and headless CLI do not reference Avalonia (guarded by `HeadlessAvaloniaIndependenceTests`); closing the window has no Runtime effect.

## Dashboard direction

The dashboard should communicate project health at a glance, including architecture, analysis progress, function/symbol counts, diagnostics, AI state, harness/test status, recompilation readiness, and runtime state.

The dashboard should not require users to inspect raw logs to understand project health.
