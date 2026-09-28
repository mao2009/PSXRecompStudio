# Contributor entry-task review

This page applies the contributor-ready Issue standard from [CONTRIBUTING.md](../../CONTRIBUTING.md) to a small sample of current open work. It is an onboarding aid, not a replacement for the live Issue bodies.

## #579 — Persona E2E failure classification

**Boundary:** `scripts/e2e/persona-e2e-gate.ps1` and focused helper/test files under `scripts/e2e/`.

**Relevant contracts:** the Persona E2E gate result schema and the diagnostics emitted by the tests it invokes.

**Validation:** synthetic parser/classification cases are sufficient; a commercial ROM is not required to prove failure-category parsing.

**Non-goals:** changing runtime/recompiler semantics, changing the title-screen gate itself, or weakening fail-closed behavior.

**Complexity:** small. PowerShell familiarity and care around diagnostic parsing are sufficient. This is a plausible entry task once its expected category vocabulary is explicit.

## #599 — LWL/LWR/SWL/SWR IR lowering

**Boundary:** MIPS-to-IR lowering and focused differential/semantic tests. Existing native execution and Rust merge arithmetic are reference evidence rather than a reason to broaden the change.

**Relevant contracts:** IR side-effect semantics, load-delay behavior, unaligned-word merge semantics, and current lowering patterns.

**Validation:** targeted synthetic/differential tests are required; legal real-input evidence may confirm that the production blocker moved, but it is not a substitute for semantic tests.

**Non-goals:** unrelated CPU opcodes, native interpreter rewrites, or runtime hardware work.

**Complexity:** medium/advanced. The file surface is bounded, but correctness requires MIPS I and existing IR-contract knowledge. This should not be labeled `good first issue`.

## #606 — reusable legal PS-X EXE fixture suite

**Boundary:** source-generated repository-owned fixtures plus the smallest production-path tests and contributor documentation needed to prove the mechanism.

**Relevant contracts:** executable parsing, artifact policy, production analysis/recompile/runtime entry points, and the distinction between synthetic fixtures and user-supplied real input.

**Validation:** deterministic regeneration and at least two machine-verifiable fixtures running through production paths.

**Non-goals:** a homebrew SDK, bundled BIOS/ROM content, commercial-title compatibility claims, or implementing every proposed fixture family.

**Complexity:** medium. The first slice is intentionally bounded, but it crosses test infrastructure and production-path composition. It is contributor-friendly for someone already comfortable with the repository, not a first task.

## Label guidance from this review

None of these examples should be mass-labeled automatically. `good first issue` is appropriate only after the live task has enough explicit paths, contracts, expected outputs, and validation steps that a new contributor does not need hidden project knowledge to discover the boundary.
