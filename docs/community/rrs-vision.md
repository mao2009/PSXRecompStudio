# RetroRecompStudio (RRS) — long-term vision

Status: **Proposed future direction; not an implemented multi-platform product.**

PSXRecompStudio starts with the original PlayStation. RetroRecompStudio (RRS) is the proposed evolution toward a platform-neutral, open-source recompilation ecosystem, not merely a renamed PS1 emulator.

## Design principles

1. **Shared host and public plugin specification.** Console-specific support can be built and evolved independently of the host.
2. **Interoperability rather than lock-in.** Competing plugins for the same platform, multi-platform plugins, alternate hosts and forks should be possible under a common specification.
3. **No permanent single-maintainer dependency.** Documentation, conformance tests, compatibility and governance should make long-term community stewardship feasible.
4. **Source-level maintenance by authorized developers.** Long-term generated outputs should be editable, traceable and rebuildable without the missing original toolchain or an AI service.
5. **Correctness before compatibility claims.** Differential tests, reproducible builds, documented limitations and explicit failure behavior are fundamental.
6. **Respect preservation and rights.** Do not distribute proprietary game data or BIOS; rights holders must independently clear commercial redistribution.

## Potential ecosystem

```text
      Independent game input (legally supplied)
                        |
                Shared host / pipeline
                  /          \
       Platform plugins   Verification / tooling
        (PS1, future…)     (contracts, fixtures)
                  \          /
          Rebuildable, inspectable outputs
                 for authorized use
```

This is an architectural aspiration; no cross-host plugin compatibility or commercial PS1 source recovery is claimed today.

## What is needed before this is credible?

- A validated bounded PS1 recompilation/run loop with clear interpreter-versus-AOT evidence.
- Defined plugin lifecycle, memory and execution contracts plus ABI/versioning strategy.
- Host-independent conformance fixtures and test suites.
- Developer-editable source generation, traceability, rebuilding, and title-specific patch isolation.
- Documented commercial use constraints, dependency provenance and governance.

## How to influence the design

We particularly welcome critiques from compiler engineers, emulator developers, tooling authors, archivists and rights-holder engineering teams. Start with a **small concrete design issue** or independent review instead of an unscoped request to implement RRS.

[Get involved](get-involved.md) · [Current project mission](../project-mission.md) · [Architecture](../../ARCHITECTURE.md)
