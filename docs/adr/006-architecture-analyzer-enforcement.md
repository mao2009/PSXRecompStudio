# ADR-006: Architecture Attributes and Dependency Direction Enforced via Build Errors

- **Status**: Accepted (amended 2026-09-08 by Issue #295)
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

## Amendment (Issue #295): migrate enforcement to loach.ArchitectureAnalyzer

### Context

`PSXRecomp.Analyzer`, decided above, hardcoded PSXR001–006 as C# rule classes
wired directly to Roslyn's `SymbolAnalysisContext`, duplicated in-repo and
maintained alongside their tests. Issue #290 tracked replacing it with
[`mao2009/ArchitectureAnalyzer`](https://github.com/mao2009/ArchitectureAnalyzer)
(packaged as `loach.ArchitectureAnalyzer`), a generic analyzer that expresses
the same class of rules — layering, dependency direction, forbidden APIs, and
interop boundaries — via a consumer-supplied JSON contract instead of
PSX-specific analyzer code. #291 authored `src/architecture.contract.json`
encoding every PSXR001–006 rule; #292 migrated the `.editorconfig` gate
contract from `PSXR*` to `AARC*` pins; #293 adopted
`loach.ArchitectureAnalyzer` 0.1.0 in shadow mode and verified parity; #294
removed `PSXRecomp.Analyzer` and `PSXRecomp.Analyzer.Tests` once parity held.

### Decision

Compile-time enforcement of this ADR's rules is now provided exclusively by
the `loach.ArchitectureAnalyzer` NuGet package, configured by:

- **`src/architecture.contract.json`** — the SSOT for rule *data*: layers,
  forbidden dependencies, forbidden APIs, marker-attribute mapping, and
  interop-boundary rules. This replaces the rule tables previously hardcoded
  in `PSXRecompArchitectureAnalyzer.cs`.
- **`.editorconfig`** — the operational severity gate contract
  (`dotnet_diagnostic.AARCxxx.severity = error`), pinned explicitly for the
  same reason the original PSXR pins were: the gate must not rely on
  descriptor defaults and must not be silently weakened.
- **`Directory.Build.props`** — wires the NuGet analyzer and the contract
  (as an `AdditionalFiles` item) into every project that opts in via
  `CompileArchitectureAttributes`, exactly as it wired the old analyzer.

Rule identity mapping (binding for anyone tracing a `PSXR`-era finding,
suppression, or test forward):

| Old (PSXR) | New (AARC) |
|---|---|
| `PSXR001` | `AARC004` |
| `PSXR002` | `AARC005` |
| `PSXR003` | `AARC006` |
| `PSXR004` | `AARC002` |
| `PSXR005` | `AARC003` |
| `PSXR006` | `AARC007` |

The marker attributes (`[Domain]`, `[Application]`, `[Infrastructure]`,
`[Test]`, `[Generated]`) remain **consumer-owned** — now at
`src/Architecture/PSXRecompArchitectureAttributes.cs` — matched by fully
qualified name from the contract. `loach.ArchitectureAnalyzer` carries no
PSX-specific name; the `[Analyzer]` marker attribute and the "Analyzer"
architecture layer, which existed only to classify the now-removed analyzer
project, were retired with it.

`src/PSXRecomp.Analyzer` and `src/PSXRecomp.Analyzer.Tests` are removed
(#294). There is no PSX-specific analyzer implementation left in this
repository.

### Consequences

Positive: the enforcement engine is maintained upstream instead of
duplicated in-repo; the SSOT is a portable JSON contract instead of C# rule
classes bound to Roslyn APIs; adding or changing a rule no longer requires
touching analyzer implementation code and its tests in this repository.

Negative / follow-on: the repository now tracks correctness against an
external package version (`loach.ArchitectureAnalyzer 0.1.0`). The
synchronization order in the original Consequences section above no longer
applies as written — rule changes now flow `architecture.contract.json`
(SSOT) → `.editorconfig` (gate) → upstream analyzer capability, since the
analyzer implementation is no longer this repository's to change directly.

### Alternatives Considered

- **Keep `PSXRecomp.Analyzer` indefinitely.** Rejected: running two
  analyzers enforcing overlapping rules is unsustainable, and the rules
  themselves are not PSX-specific — a generic, externally maintained
  analyzer removes the in-repo maintenance cost without losing anything the
  original decision required.
- **Vendor the rule logic from the NuGet package into the repository instead
  of consuming it as a dependency.** Rejected: it reintroduces exactly the
  duplication and maintenance burden this migration exists to remove.

### Related ADRs

None — this is the first amendment to ADR-006.
