# PSXRecompStudio Documentation

## Purpose

This directory is the project-level architecture and development knowledge base.
It is intended to be readable by both humans and AI development agents.

## Authority

- **SSOT**: current authoritative architectural/design information.
- **Reference**: supporting documentation that does not override an SSOT.
- **Draft**: proposed or experimental information.
- **Deprecated**: retained for historical context only.

English is the Canonical language for maintained SSOT documentation. When a
translated convenience copy exists, it must remain subordinate to its Canonical
source. See [API Documentation & Docstring Policy](development/documentation-policy.md)
for the Canonical/translation maintenance and review policy.

## Documentation hierarchy

```text
Architecture
├── Top-level Architecture SSOT
├── Architecture Index
├── Managed Architecture Matrix
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

Reference
└── Prior Art / Reference Implementations

Decisions
└── Architecture Decision Records
```

## Issue vs Documentation

| Location | Purpose |
|---|---|
| GitHub Issue | Work tracking, implementation tasks, acceptance criteria, dependencies |
| Pull Request | Concrete code/documentation change and review |
| `docs/` | Current architecture, constraints, terminology, and stable project knowledge |
| Code comments | Local implementation context |

Closed Issues are historical records. They are not the primary source of current architecture.

## AI bootstrap path

A development agent should normally follow this order:

1. Read the repository README.
2. Read the [Top-level Architecture SSOT](../ARCHITECTURE.md).
3. Read the [Architecture Index](architecture/README.md) and identify the applicable subsystem SSOT.
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

- [Top-level Architecture SSOT](../ARCHITECTURE.md)
- [Architecture Index](architecture/README.md)
- [Managed Architecture Matrix](architecture-matrix.md)
- [GUI / UX](architecture/gui-ux.md)
- [Development Agent Guide](development/agent-guide.md)
- [Repository Artifact Policy](development/artifact-policy.md)
- [Real-ROM Analysis Artifact Format](development/real-rom-analysis-artifacts.md)
- [API Documentation & Docstring Policy](development/documentation-policy.md)
- [README Auto-Update](development/readme-autoupdate.md)
- [Native Library Build and Test Execution](development/native-library-build.md)
- [Real-ROM Analysis Flow](development/real-rom-analysis.md)
- [Recompiler IR / CPU Semantic Contract](development/recompiler-ir-contract.md)
- [MIPS-to-IR Lowering](development/recompiler-ir-lowering.md)
- [Recompiler Host Code Generation](development/recompiler-host-codegen.md)
- [References and Prior Art](REFERENCES.md)
- [Architecture Decision Records](adr/)
- CPU / R3000A
  - [R3000A Overview](cpu/r3000a.md)
  - [Registers](cpu/registers.md)
  - [Instruction Set](cpu/instruction-set.md)
  - [Instruction Format](cpu/instruction-format.md)
  - [Pipeline](cpu/pipeline.md)
  - [COP0](cpu/cop0.md)
  - [Exceptions](cpu/exceptions.md)
  - [Memory](cpu/memory.md)
  - [Test Specification](cpu/test-specification.md)
- Decoder — planned
- Analyzer — planned
- Diagnostics — planned
- AI Analysis — planned
- Harness — planned
- Testing — planned
- [Runtime](runtime/architecture.md)

## Translation relationship checks

Registered translation pairs are declared in
`config/docs/translations.json`. The documentation translation workflow checks
that registered Canonical and translated files exist, that the required
Canonical marker is present in the translation, and whether the Canonical file
has changed more recently than its translation.

Freshness findings are warnings for human review; they do not claim semantic
divergence or authorize automatic translation/merge.

## Maintenance rule

When implementation changes the intended architecture, update the authoritative documentation in the same change or in the directly associated follow-up change. Documentation should describe the current intended system, not merely reproduce historical implementation details.
