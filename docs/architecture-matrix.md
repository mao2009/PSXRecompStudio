# Architecture Matrix - SSOT

**Status:** Stable

**Authority:** Subsystem SSOT

**Document Type:** Managed Architecture Matrix

This document is authoritative for the managed C# architecture layer model, dependency and forbidden-API rationale, analyzer mapping, and the C ABI/P/Invoke boundary documentation. [`ARCHITECTURE.md`](../ARCHITECTURE.md) remains the Top-level Architecture SSOT for repository-wide system direction. For machine-enforced rule data within this subsystem, `src/architecture.contract.json` governs exactly as described below.

## SSOT split

Two documents together form the managed architecture SSOT, and they own different things:

- **[`src/architecture.contract.json`](../src/architecture.contract.json)** — the
  machine-enforced SSOT. Every layer, forbidden dependency, forbidden API, marker
  attribute, and interop-boundary rule is authored there exactly once, and
  `loach.ArchitectureAnalyzer` (AARC diagnostics) reads it directly at compile
  time. This document does not duplicate its row-by-row content.
- **This document** — rationale, layer intent, a high-level view of the rules,
  and the mapping from contract entries to diagnostics. When the two disagree,
  the contract governs; treat a mismatch here as a doc-drift finding to fix.

## Layer Definitions

| Layer | Responsibility | Namespace root | Projects |
|-------|----------------|-----------------|----------|
| **Domain** | Pure business logic, PSX concept model, deterministic computation, and the C ABI interop boundary | `PSXRecomp.Core` | `PSXRecomp.Core` |
| **Application** | Avalonia UI, user interface, presentation | `PSXRecompStudio` | `PSXRecompStudio` |
| **Infrastructure** | Managed hardware-abstraction / adapter code (reserved; no project occupies this layer yet) | `PSXRecomp.Infrastructure` | *(planned)* |
| **Test** | Unit/integration tests | `PSXRecomp.Tests` | `PSXRecomp.Tests` |
| **Generated** | Auto-generated code | `PSXRecomp.Generated` | *(planned)* |

`PSXRecomp.Native` (the C++ emulation core) has no managed namespace and sits
outside the analyzer's reach entirely; it is reached only through the Domain
layer's C ABI boundary (see below).

Namespace resolution matches by **root prefix**: a namespace equal to a root or
any descendant (`Root` / `Root.*`) maps to that root's layer — e.g.
`PSXRecomp.Core.Interop` resolves to Domain.

## Dependency Matrix (high-level)

The exhaustive edge list is `architecture.contract.json`'s `forbiddenDependencies`
array. Read at a glance:

| From | To | Allowed | Reason |
|------|-----|---------|---------|
| Domain | Application | ❌ NO | Domain must not depend on the outer Application layer |
| Application | Infrastructure | ❌ NO | Application reaches Infrastructure only through the Domain interop boundary |
| Infrastructure | Application | ❌ NO | Infrastructure must not depend on Application |
| Domain / Application / Infrastructure | Test | ❌ NO | Production code must not depend on test code |
| Application | Domain | ✅ YES | Application → Domain via `PSXRecomp.Core` (P/Invoke wrappers); not a forbidden edge |
| Test | Domain / Application / Infrastructure | ✅ YES | Tests depend on production code, not vice versa |

## C ABI / P/Invoke Boundary

```text
PSXRecompStudio (Application)
    ↓ ProjectReference (allowed)
PSXRecomp.Core (Domain/Interop)
    ↓ NativeInterop (internal static partial class, [LibraryImport])
PSXRecomp.Native (Infrastructure/C++)
```

- **PSXRecompStudio → PSXRecomp.Core** ProjectReference is **allowed** (regular dependency for UI layer)
- **PSXRecompStudio → PSXRecomp.Native** direct access or dependency is **prohibited** (must go through Core interop)
- `NativeInterop.cs` declares the P/Invoke bindings: `internal static partial class NativeInterop` with `[LibraryImport("PSXRecomp.Native")]`
- `PSXCoreWrapper.cs` exposes the public `PSXCoreWrapper` wrapper (`IDisposable`, native handle owner) over those bindings
- **Boundary**: `PSXRecomp.Core` ↔ `PSXRecomp.Native` (P/Invoke contract)
- **No direct dependency** from `PSXRecomp.Native` → `PSXRecomp.Core` (reverse prohibited)
- **No direct dependency** from `PSXRecompStudio` → `PSXRecomp.Native` (UI layer must not bypass Core interop)
- Mechanically enforced by `interopBoundaryRules` in the contract (`DllImport`/`LibraryImport` must be declared inside the Domain layer) — see [Mechanical Enforcement](#mechanical-enforcement).

## Forbidden API Matrix (high-level)

The exhaustive per-layer list (including specific members like `DateTime.Now`
vs `DateTime.UtcNow`) is `architecture.contract.json`'s `forbiddenApis` array.
Every layer forbids the same non-determinism / external-I/O categories, with
the underlying reason varying by layer intent:

| Category | Domain | Application | Infrastructure | Test | Generated | Reason |
|----------|:---:|:---:|:---:|:---:|:---:|---|
| `Console.*` | ❌ | ❌ | ❌ | ❌ | ❌ | standard output must be abstracted behind an adapter |
| `File.*` / `Directory.*` | ❌ | ❌ | ❌ | ❌ | ❌ | external I/O is an Infrastructure responsibility |
| `Environment.*` | ❌ | | | ❌ | ❌ | execution environment dependencies break determinism |
| `Process.*` | ❌ | | | ❌ | ❌ | process control is an Infrastructure responsibility |
| `DateTime.Now` / `.UtcNow` | ❌ | | | ❌ | ❌ | non-deterministic time sources break determinism |
| `Guid.NewGuid()` | ❌ | | | ❌ | ❌ | non-deterministic randomness is forbidden |
| `Random` (whole type) | ❌ | | | ❌ | ❌ | non-deterministic randomness is forbidden |
| `Thread.*` | | | | ❌ | ❌ | manual thread management breaks deterministic execution |
| `Task.Delay` | | | | ❌ | ❌ | asynchronous timing must use controlled schedulers |
| `HttpClient` / `Socket` | ❌ | | | | | network access is an Infrastructure responsibility |

Blank cells are not currently declared forbidden for that layer in the
contract (this table only records what the contract actually declares — it is
not a design claim that the blank combination is safe).

## Architecture Attribute Contract

| Attribute | Layer | Notes |
|-----------|-------|-------|
| `[Domain]` | Domain | Pure business logic, no side effects |
| `[Application]` | Application | UI components, ViewModels |
| `[Infrastructure]` | Infrastructure | Reserved for the future Infrastructure project |
| `[Test]` | Test | Test classes |
| `[Generated]` | Generated | Auto-generated artifacts |

All five are `internal` types in the `PSXRecomp.Architecture` namespace
(`src/Architecture/PSXRecompArchitectureAttributes.cs`), distributed to each
consumer project as a linked source file from `Directory.Build.props`.
Consuming projects opt in with
`<CompileArchitectureAttributes>true</CompileArchitectureAttributes>`.
Attributes are matched by fully qualified name in the contract's
`layerDeclaration.markerAttributes`.

An `[Analyzer]` attribute and an "Analyzer" layer existed while
`PSXRecomp.Analyzer` was itself a project the contract had to classify; both
were retired in #294 along with that project — the generic
`loach.ArchitectureAnalyzer` package carries no PSX-specific layer and needs
none, since no project occupies it.

### Applicability by Type

- **class**: All attribute types applicable
- **record**: `[Domain]`, `[Application]`, `[Infrastructure]` applicable
- **struct**: `[Domain]`, `[Application]` applicable (no side effects)
- **interface**: `[Domain]` applicable (contract, no implementation)
- **enum**: `[Domain]` applicable (pure values)
- **delegate**: `[Domain]` applicable (pure function pointers)
- **partial type**: Attributes split across partial parts

## Namespace Matrix

| Project | Namespace | Responsibility |
|---------|-----------|----------------|
| `PSXRecompStudio` | `PSXRecompStudio` | Application (UI, ViewModels) |
| `PSXRecompStudio` | `PSXRecompStudio.ViewModels` | Application (UI models) |
| `PSXRecomp.Core` | `PSXRecomp.Core` | Domain (business logic) + Interop (`NativeInterop`, `PSXCoreWrapper`) |
| `PSXRecomp.Native` | (C++ - no managed namespace) | Infrastructure (CPU emulation) |
| `PSXRecomp.Infrastructure` | `PSXRecomp.Infrastructure` | Infrastructure (reserved; managed adapters, project planned) |
| `PSXRecomp.Tests` | `PSXRecomp.Tests` | Test infrastructure |

## Mechanical Enforcement

Compile-time enforcement of this matrix is provided by the
[`loach.ArchitectureAnalyzer`](https://github.com/mao2009/ArchitectureAnalyzer)
NuGet package (`AARC` diagnostics), configured entirely by
`src/architecture.contract.json` (rule data) and `.editorconfig` (severity /
gate contract — see ADR-006). There is no PSX-specific analyzer implementation
in this repository; the enforcement engine is externally maintained.

| ID | Rule | Severity |
|----|------|----------|
| `AARC002` | Forbidden dependency edge | error |
| `AARC003` | Forbidden API usage per layer | error |
| `AARC004` | Missing architecture layer declaration | error |
| `AARC005` | Multiple layer declarations on one type | error |
| `AARC006` | Attribute layer does not match namespace mapping | error |
| `AARC007` | P/Invoke (`DllImport` / `LibraryImport`) outside the Domain interop boundary | error |

Severity is pinned explicitly in `.editorconfig` so the gate contract does not
rely on descriptor defaults and cannot be weakened silently.

### Migration history (PSXR → AARC)

Through #294, these rules were enforced by an in-repo hardcoded analyzer
(`PSXRecomp.Analyzer`, `PSXR001`–`PSXR006`, see ADR-006). The rule identity
mapping, kept for anyone tracing an old `PSXR`-era finding, suppression, or
test forward:

| Old (PSXR) | New (AARC) |
|---|---|
| `PSXR001` (missing declaration) | `AARC004` |
| `PSXR002` (multiple attributes) | `AARC005` |
| `PSXR003` (namespace/layer mismatch) | `AARC006` |
| `PSXR004` (forbidden dependency) | `AARC002` |
| `PSXR005` (forbidden API) | `AARC003` |
| `PSXR006` (interop boundary) | `AARC007` |

Enforcement notes (unchanged in substance from the PSXR era):

- Enforcement scope is **classes** (including records); structs, interfaces,
  enums, and delegates are recognized but not required to be annotated.
- A partial type is satisfied by any attributed part; a nested class inherits
  the layer of its enclosing attributed type.
- The `PSXRecomp.Architecture.*` marker namespace and generated code are
  exempt from the missing-declaration rule.
- Escape hatch for legitimate per-site usage (e.g., temp files in tests):
  suppress with `#pragma warning disable AARC003` or via `.editorconfig` /
  `NoWarn`, justified in review. Suppression scope must never be widened
  beyond the violating site to make a gate pass.
- CI fails on any violation because every AARC diagnostic listed above is
  pinned to error.

### Quality Gate Verification Record

- **2026-08-24 (Issue #105, PR #107)**: The `PSXR`-era gate was verified
  end-to-end with a temporary fixture project. A deliberate Domain →
  Application reference produced exactly one `error PSXR004` at the reference
  site, failed `dotnet build` (exit code 1), and failed CI. Removing only the
  violating files returned CI to fully green. Superseded by the entry below —
  kept here as history, not re-run.
- **2026-09-08 (Issue #294, PR #299)**: With `PSXRecomp.Analyzer` fully
  removed, `loach.ArchitectureAnalyzer` verified as the sole gate. `dotnet
  build` / `dotnet test` passed clean (0 errors, 1264 tests). A temporary,
  uncommitted fixture then forced one violation per surviving rule family —
  `AARC002` (Domain → Application dependency), `AARC003` (`Console.WriteLine`
  in Domain), `AARC004` (undeclared type), and `AARC007` (`DllImport` outside
  Domain) — and each surfaced as a build error with no legacy PSXR analyzer
  present. The fixture was reverted; the tree returned to a clean, fully
  green state.

## Consistency Checks

1. **Repository Structure** ✅
   - `PSXRecompStudio` → `PSXRecomp.Core` (ProjectReference confirmed)
   - `PSXRecomp.Core` → `PSXRecomp.Native` (P/Invoke contract confirmed)
   - `PSXRecomp.Tests` → `PSXRecomp.Core` (Test dependency confirmed)

2. **Dependency Matrix** ENFORCED — mechanically enforced by `AARC002`; see contract for the full edge list.

3. **Forbidden API** ENFORCED — mechanically enforced by `AARC003`; see contract for the full per-layer list.

4. **C ABI Boundary** — clear separation via `NativeInterop.cs` and `LibraryImport`; P/Invoke location enforced (`AARC007`). Runtime verification requires native build and integration tests (tracked separately from this matrix).

5. **Layer Declaration** ENFORCED — presence, uniqueness, and namespace mapping enforced (`AARC004`–`AARC006`).

## Issues Identified

1. **Missing Generated Code Project** - `PSXRecomp.Generated` project not yet defined (generated code is exempted by path convention until then).
2. **Missing Infrastructure Project** - `PSXRecomp.Infrastructure` namespace root is reserved in the contract; no project occupies it yet.
3. **C ABI Contract** - Runtime integration verification remains pending (native build passes locally; CI-level integration tests pending).

## Recommendations

- Define the `PSXRecomp.Generated` and `PSXRecomp.Infrastructure` projects and decide their dependency policy once they exist.
- Verify the native build and integration tests for the documented `NativeInterop` boundary.

---

**SSOT Status**

- Architecture Matrix: ✅ ESTABLISHED - subsystem SSOT for managed architecture rationale and high-level tables; the executable rule set is `src/architecture.contract.json`.
- Top-level architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) — repository-wide system direction and cross-cutting architecture.
- Mechanical enforcement: ✅ ACTIVE - `loach.ArchitectureAnalyzer` (AARC002–AARC007) via `src/architecture.contract.json` + `.editorconfig`; see ADR-006 (amended).
- **Missing Items**: `PSXRecomp.Generated` and `PSXRecomp.Infrastructure` projects not yet created.
