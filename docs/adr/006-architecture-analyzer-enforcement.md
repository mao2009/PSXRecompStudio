# ADR-006: Architecture Attributes and Dependency Direction Enforced via Build Errors

- **Status**: Accepted
- **Date**: 2026-08-21
- **Issue**: #8

## Context

`docs/architecture-matrix.md` (SSOT) documents layer attributes, dependency
direction, and the Forbidden API list, but manual review alone cannot
guarantee compliance. Drift between the documentation and the actual code is
inevitable over time. A mechanism to enforce the SSOT mechanically was
needed.

## Decision

Introduce a Roslyn Analyzer (`PSXRecomp.Analyzer`) that enforces the SSOT's
rules as compile-time errors.

### Diagnostic rules

| ID | Rule |
|----|------|
| `PSXR001` | Class has no architecture attribute |
| `PSXR002` | A type has more than one architecture attribute |
| `PSXR003` | Attribute layer does not match the namespace mapping |
| `PSXR004` | Forbidden dependency direction (inner → outer, Production → Test) |
| `PSXR005` | Forbidden API usage for the layer |
| `PSXR006` | P/Invoke (`DllImport` / `LibraryImport`) outside `PSXRecomp.Core` |

All are Error severity; CI fails on any violation.

### Attribute distribution

The six attributes (`[Domain]` `[Application]` `[Infrastructure]`
`[Analyzer]` `[Test]` `[Generated]`) are internal attributes in the
`PSXRecomp.Architecture` namespace, distributed to each project as a linked
source file (`<Compile Include="..." Link="..." />`) from the repository
root `Directory.Build.props`. Consuming projects opt in with
`<CompileArchitectureAttributes>true</CompileArchitectureAttributes>`. This
introduces no new assembly and no new project-reference graph. Attributes
are matched by fully qualified name.

### Enforcement scope and exclusions

- The enforced targets are **classes** (including records). struct /
  interface / enum / delegate are recognized but not required in this
  iteration (per Issue #8's wording).
- A partial type satisfies the rule if any one of its parts carries the
  attribute.
- A nested class inherits the layer of its enclosing attributed type (the
  `ContainingType` chain used by `ResolveLayer` is kept consistent with
  PSXR001).
- The `PSXRecomp.Architecture.*` namespace is exempt from PSXR001 (marker
  namespace).
- Generated code is excluded by path convention (`.g.cs` / `.designer.cs` /
  `obj/`, etc.) and by `IsImplicitlyDeclared`.
- Namespace-to-layer resolution matches on the root prefix (e.g.
  `PSXRecomp.Analyzer.Tests` → Analyzer). `PSXRecomp.Infrastructure` →
  Infrastructure is also reserved for a future project.

### Forbidden API notes

- In addition to the SSOT's row (`Random.Shared`), Domain forbids
  **`System.Random` entirely** (including `new Random()`) — the row's
  rationale (prohibiting non-deterministic randomness) applied to the whole
  type.
- Where the Test / Analyzer / Generated layers have a legitimate use (e.g. a
  test's temporary file), the violation can be suppressed with `#pragma
  warning disable PSXR005` or via `.editorconfig` / `NoWarn`, with the
  rationale shown in review.

### Unenforced dependency edges

Production → Analyzer / Generated is not enforced by this ADR because the
SSOT does not state it explicitly. This needs revisiting once the
`PSXRecomp.Generated` project is defined (tracked in architecture-matrix.md's
Missing Items).

## Consequences

- SSOT violations are caught immediately at build time, independent of
  review.
- Every new class now requires a layer attribute (migration complete: all
  12 existing classes are annotated).
- The analyzer itself is part of the solution and is subject to
  self-application.
- Rule changes are synchronized in this order: architecture-matrix.md
  (SSOT) → analyzer implementation → tests.
