# PSXRecompStudio Documentation

## Purpose

This directory is the project-level architecture and development knowledge base.
It is intended to be readable by both humans and AI development agents.

## Authority

- **SSOT**: current authoritative architectural/design information.
- **Reference**: supporting documentation that does not override an SSOT.
- **Draft**: proposed or experimental information.
- **Deprecated**: retained for historical context only.

## Documentation hierarchy

```text
Architecture
├── SSOT
├── CPU / R3000A
├── Decoder
├── Analyzer
├── Diagnostics
├── AI Analysis
├── Harness
├── Testing
├── GUI / UX
└── Runtime

Development
├── Agent Guide
├── Repository Artifact Policy
├── API Documentation & Docstring Policy
├── README Auto-Update
├── Real-ROM Analysis Flow
└── Terminology

Decisions
└── Architecture Decision Records
```

## Issue vs Documentation

| Location | Purpose |
|---|---|
| GitHub Issue | Work tracking, implementation tasks, acceptance criteria, dependencies |
| Pull Request | Concrete code/documentation change and review |
| `docs/` | Current architecture, constraints, terminology, and stable project knowledge |
| `README_AI` | Concise bootstrap path for AI development agents |
| Code comments | Local implementation context |

Closed Issues are historical records. They are not the primary source of current architecture.

## AI bootstrap path

A development agent should normally follow this order:

1. Read the repository README and `README_AI` when present.
2. Read the relevant architecture/SSOT documentation.
3. Identify the applicable subsystem SSOT.
4. Inspect related open Issues and PRs.
5. Inspect the implementation code.
6. Verify that the proposed change does not violate documented constraints.
7. Update documentation when an architectural decision changes.

## Page metadata

Subsystem SSOT documents should use this metadata when applicable:

```text
Status: Stable | Draft | Deprecated
Authority: SSOT | Reference
Related Issues:
Related Components:
Dependencies:
Constraints:
```

## Current subsystem documentation

Detailed subsystem pages will be added as the corresponding architecture becomes established.

- [Architecture SSOT](architecture/README.md)
- [GUI / UX](architecture/gui-ux.md)
- [Development Agent Guide](development/agent-guide.md)
- [Repository Artifact Policy](development/artifact-policy.md)
- [Real-ROM Analysis Artifact Format](development/real-rom-analysis-artifacts.md)
- [API Documentation & Docstring Policy](development/documentation-policy.md)
- [README Auto-Update](development/readme-autoupdate.md)
- [Native Library Build and Test Execution](development/native-library-build.md)
- [Real-ROM Analysis Flow](development/real-rom-analysis.md)
- CPU / R3000A — planned
- Decoder — planned
- Analyzer — planned
- Diagnostics — planned
- AI Analysis — planned
- Harness — planned
- Testing — planned
- Runtime — planned

## Maintenance rule

When implementation changes the intended architecture, update the authoritative documentation in the same change or in the directly associated follow-up change. Documentation should describe the current intended system, not merely reproduce historical implementation details.
