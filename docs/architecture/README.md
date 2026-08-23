# Architecture

**Status:** Stable

**Authority:** SSOT

**Document Type:** Architecture Index

This section describes the intended architecture of PSXRecompStudio. Subsystem pages are authoritative for their respective responsibilities and constraints.

## System direction

PSXRecompStudio is intended to provide an integrated environment for PlayStation software analysis, understanding, recompilation, AI-assisted investigation, harnessing, and validation.

The architecture should preserve clear separation between:

```text
Presentation / Avalonia UI
        ↓
Application Layer
        ↓
Domain / Analysis / Test Models
        ↓
Infrastructure
```

The GUI must not become the source of truth for architecture or analysis semantics.

## Core architectural areas

| Area | Responsibility | Status |
|---|---|---|
| Architecture SSOT | Cross-cutting architectural rules | Stable |
| CPU / R3000A | CPU domain model and architectural semantics | Established / evolving |
| Decoder | Instruction decoding | Established / evolving |
| Analyzer | Static and architectural analysis | In development |
| Diagnostics | Structured findings and severity | Planned / evolving |
| AI Analysis | Evidence-driven AI investigation | Planned / evolving |
| Harness | Reproducible function/instruction validation | Planned / evolving |
| Testing | Automated and compatibility validation | Planned / evolving |
| GUI / UX | Modern developer-tool workspace | Defined by Issue #75 |
| Runtime | Execution and runtime inspection | Planned |

## Authority rule

When documentation conflicts, the more specific authoritative subsystem SSOT should be consulted together with the top-level Architecture SSOT. Issues and PRs describe changes; once a decision becomes current architecture, the appropriate SSOT should be updated.

## Related work

- Issue #75 — GUI/UX Architecture & Design SSOT
- Issue #76 — Documentation architecture and AI-readable SSOT
