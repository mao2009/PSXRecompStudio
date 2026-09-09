# Architecture

**Status:** Stable

**Authority:** Reference

**Document Type:** Architecture Index

This section routes readers to the intended architecture of PSXRecompStudio. [`ARCHITECTURE.md`](../../ARCHITECTURE.md) is the Top-level Architecture SSOT. More-specific subsystem pages are authoritative for their respective responsibilities and constraints.

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
| Top-level Architecture SSOT | Repository-wide system direction and cross-cutting architecture | Stable |
| Managed Architecture Matrix | Managed layer/dependency rules and analyzer mapping | Stable |
| CPU / R3000A | CPU domain model and architectural semantics | Established / evolving |
| Decoder | Instruction decoding | Established / evolving |
| Analyzer | Static and architectural analysis | In development |
| Diagnostics | Structured findings and severity | Planned / evolving |
| AI Analysis | Evidence-driven AI investigation | Planned / evolving |
| Harness | Reproducible function/instruction validation | Planned / evolving |
| Testing | Automated and compatibility validation | Planned / evolving |
| GUI / UX | Modern developer-tool workspace | Defined by Issue #75 |
| Runtime | BIOS-less execution boundary and runtime inspection | Phase 1 contract |

## Authority rule

Use the following hierarchy when documentation overlaps:

1. A more-specific authoritative subsystem SSOT governs within its declared scope.
2. [`ARCHITECTURE.md`](../../ARCHITECTURE.md) governs repository-wide system direction and cross-cutting architecture.
3. This file is a routing index/reference and does not override either of the above.

For managed C# layer/dependency rules, [`docs/architecture-matrix.md`](../architecture-matrix.md) is the subsystem SSOT; for its machine-enforced rule data, `src/architecture.contract.json` governs exactly as documented there.

Issues and PRs describe changes; once a decision becomes current architecture, the appropriate SSOT should be updated.

## Related work

- Issue #75 — GUI/UX Architecture & Design SSOT
- Issue #76 — Documentation architecture and AI-readable SSOT
