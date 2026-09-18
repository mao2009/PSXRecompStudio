# Architecture Matrix - SSOT

**Status:** Stable

**Authority:** Subsystem SSOT

**Document Type:** Managed Architecture Matrix

This document is authoritative for the managed C# architecture layer model, dependency and forbidden-API rationale, analyzer mapping, managed host-I/O boundary, and the C ABI/P/Invoke boundary documentation. [`ARCHITECTURE.md`](../ARCHITECTURE.md) remains the Top-level Architecture SSOT for repository-wide system direction. For machine-enforced rule data within this subsystem, `src/architecture.contract.json` governs exactly as described below.

## SSOT split

Two documents together form the managed architecture SSOT, and they own different things:

- **[`src/architecture.contract.json`](../src/architecture.contract.json)** — the machine-enforced SSOT. Every layer, forbidden dependency, forbidden API, marker attribute, and interop-boundary rule is authored there exactly once, and `loach.ArchitectureAnalyzer` (AARC diagnostics) reads it directly at compile time. This document does not duplicate its row-by-row content.
- **This document** — rationale, layer intent, a high-level view of the rules, and the mapping from contract entries to diagnostics. When the two disagree, the contract governs; treat a mismatch here as a doc-drift finding to fix.

## Layer Definitions

| Layer | Responsibility | Namespace root | Projects |
|-------|----------------|----------------|----------|
| **Domain** | Pure business logic, PSX concept model, deterministic computation, Domain-owned ports, and the C ABI/P/Invoke boundary | `PSXRecomp.Core` | `PSXRecomp.Core` |
| **Application** | Avalonia UI, use-case orchestration, user interface, presentation | `PSXRecompStudio` | `PSXRecompStudio` |
| **Infrastructure** | Concrete **managed host adapters**: filesystem/disc acquisition, process/toolchain execution, network integrations, logging/output sinks, and other host side effects | `PSXRecomp.Infrastructure` | `PSXRecomp.Infrastructure` |
| **Test** | Unit/integration tests and test-only host tooling | `PSXRecomp.Tests`, `PSXRecompStudio.Tests` | test projects |
| **Generated** | Auto-generated code | `PSXRecomp.Generated` | *(planned)* |

`PSXRecomp.Native` is the C++ emulation core. It is **not** the managed Infrastructure layer: it has no managed namespace, is outside Roslyn/AARC enforcement, and is reached only through the Domain layer's C ABI/P/Invoke boundary.

Namespace resolution matches by **root prefix**: a namespace equal to a root or any descendant (`Root` / `Root.*`) maps to that root's layer — e.g. `PSXRecomp.Core.Interop` resolves to Domain.

## Dependency Matrix (high-level)

The exhaustive edge list is `architecture.contract.json`'s `forbiddenDependencies` array. Read at a glance:

| From | To | Allowed | Reason |
|------|-----|---------|--------|
| Domain | Application | ❌ NO | Domain must not depend on the outer Application layer |
| Domain | Infrastructure | ❌ NO | Domain owns ports; it must not depend on concrete managed adapters |
| Application | Infrastructure | ❌ NO | Application consumes host services through Domain-owned ports, not concrete adapters |
| Infrastructure | Application | ❌ NO | Infrastructure must not depend on Application |
| Infrastructure | Domain | ✅ YES | Concrete adapters implement/use Domain-owned ports and contracts |
| Domain / Application / Infrastructure | Test | ❌ NO | Production code must not depend on test code |
| Application | Domain | ✅ YES | Application orchestrates Domain services and ports |
| Test | Domain / Application / Infrastructure | ✅ YES | Tests may depend on production code, not vice versa |

The `Application → Infrastructure` prohibition deliberately stays global. When a future executable first needs to instantiate a concrete adapter, one **composition-root site** may use a narrowly scoped `AARC002` suppression with an explicit rationale. Business/use-case code must remain port-only. If concrete-adapter wiring spreads beyond one obvious bootstrap site, that is evidence for a dedicated Host/Bootstrap layer and requires a separate architecture change rather than wider suppressions.

## Managed Host I/O / Port-Adapter Boundary

The owner of managed host side effects is now explicit:

| Capability | Port/contract owner | Concrete implementation owner |
|------------|---------------------|-------------------------------|
| File / directory / disc-image acquisition | Domain (`PSXRecomp.Core`) abstraction when one is required | Managed Infrastructure |
| External process / compiler / toolchain execution | Domain abstraction | Managed Infrastructure |
| Network access | Domain abstraction | Managed Infrastructure |
| Console / structured-log / file output sinks | Domain abstraction such as `IRuntimeOutputSink` | Managed Infrastructure |
| Pure CHD / ISO / PS-X EXE parsing from already-supplied bytes, streams, or sector delegates | Domain | Domain; no host acquisition side effect occurs |
| C ABI/P/Invoke into `PSXRecomp.Native` | Domain interop boundary | Domain interop wrapper + native C++ core; **not** managed Infrastructure |

Rules that follow from this split:

1. **Domain owns ports, never adapters.** Interfaces and deterministic request/result contracts used by Domain/Application live in `PSXRecomp.Core`. Domain must not reference `PSXRecomp.Infrastructure`.
2. **Infrastructure executes host APIs.** `System.IO.File` / `Directory`, `Process`, network clients/sockets, and concrete output/logging mechanisms are legitimate inside an `[Infrastructure]` adapter. Infrastructure may depend inward on Domain contracts.
3. **Application does not acquire host resources directly.** Studio/CLI use cases receive or invoke Domain ports/services. They do not call `File.*`, `Directory.*`, or concrete Infrastructure types as ordinary application logic.
4. **Parsing is distinct from acquisition.** A CHD/ISO parser that consumes an already-supplied `Stream`, byte sequence, or sector callback may remain Domain code. Opening a user-selected path, locating a compiler executable, making a network request, or selecting a concrete sink is host I/O and belongs to Infrastructure.
5. **Runtime host output follows the same rule.** For example, `BiosHleRuntime` depends on the Domain-owned `IRuntimeOutputSink`; a console/file/log sink implementation belongs to Infrastructure.
6. **Tests remain a controlled exception environment.** Test-only fixture reads, temporary files, and compiler process launches may continue to use narrowly scoped `AARC003` suppressions. A suppression must cover only the justified site and must not become a production architecture substitute.
7. **No premature Infrastructure project.** `PSXRecomp.Infrastructure` is created when the first production path actually needs a concrete managed host adapter (for example, opening a disc path, invoking a production generated-host compiler, making a network call, or installing a concrete output/logging sink). A reserved namespace alone is not sufficient reason to create an empty project.

This resolves the former contradiction where the rationale called external I/O an Infrastructure responsibility while `File.*` / `Directory.*` / `Console.*` were mechanically forbidden inside Infrastructure itself.

## C ABI / P/Invoke Boundary

```text
PSXRecompStudio (Application)
    ↓ ProjectReference (allowed)
PSXRecomp.Core (Domain/Interop)
    ↓ NativeInterop (internal static partial class, [LibraryImport])
PSXRecomp.Native (native C++ core; outside managed AARC layers)
```

- **PSXRecompStudio → PSXRecomp.Core** ProjectReference is **allowed**.
- **PSXRecompStudio → PSXRecomp.Native** direct access or dependency is prohibited; calls go through Core interop.
- `NativeInterop.cs` declares the P/Invoke bindings: `internal static partial class NativeInterop` with `[LibraryImport("PSXRecomp.Native")]`.
- `PSXCoreWrapper.cs` exposes the public `PSXCoreWrapper` wrapper (`IDisposable`, native handle owner) over those bindings.
- **Boundary**: `PSXRecomp.Core` ↔ `PSXRecomp.Native` (P/Invoke/C ABI contract).
- `PSXRecomp.Native` is not assigned the managed Infrastructure layer and is not analyzed by Roslyn/AARC.
- Mechanically enforced on the managed side by `interopBoundaryRules` in the contract (`DllImport`/`LibraryImport` must be declared inside Domain).

## Forbidden API Matrix (high-level)

The exhaustive per-layer list is `architecture.contract.json`'s `forbiddenApis` array. A blank cell means the contract does not forbid that API for that layer; it does not grant permission outside the responsibilities defined above.

| Category | Domain | Application | Infrastructure | Test | Generated | Reason |
|----------|:---:|:---:|:---:|:---:|:---:|---|
| `Console.*` | ❌ | ❌ |  | ❌ | ❌ | host output is implemented by a concrete Infrastructure adapter |
| `File.*` / `Directory.*` | ❌ | ❌ |  | ❌ | ❌ | host filesystem acquisition belongs to Infrastructure |
| `Environment.*` | ❌ | | | ❌ | ❌ | execution-environment dependencies break deterministic layers |
| `Process.*` | ❌ | | | ❌ | ❌ | process/toolchain control belongs to Infrastructure |
| `DateTime.Now` / `.UtcNow` | ❌ | | | ❌ | ❌ | non-deterministic time sources break determinism |
| `Guid.NewGuid()` | ❌ | | | ❌ | ❌ | non-deterministic randomness is forbidden |
| `Random` (whole type) | ❌ | | | ❌ | ❌ | non-deterministic randomness is forbidden |
| `Thread.*` | | | | ❌ | ❌ | manual thread management breaks deterministic execution |
| `Task.Delay` | | | | ❌ | ❌ | asynchronous timing must use controlled schedulers |
| `HttpClient` / `Socket` | ❌ | | | | | concrete network access belongs to Infrastructure |

Infrastructure's blank cells for host APIs are intentional after Issue #38. The layer is the implementation boundary for those effects; forbidding the APIs there would make the adapter layer impossible to implement. Dependency direction and port ownership, not an Infrastructure-side API ban, keep those effects from leaking inward.

## Architecture Attribute Contract

| Attribute | Layer | Notes |
|-----------|-------|-------|
| `[Domain]` | Domain | Pure/deterministic logic and ports; no concrete host side effects |
| `[Application]` | Application | UI/use-case orchestration |
| `[Infrastructure]` | Infrastructure | Concrete managed host adapters |
| `[Test]` | Test | Test classes |
| `[Generated]` | Generated | Auto-generated artifacts |

All five are `internal` types in the `PSXRecomp.Architecture` namespace (`src/Architecture/PSXRecompArchitectureAttributes.cs`), distributed to consumers according to `Directory.Build.props`. Attributes are matched by fully qualified name in the contract's `layerDeclaration.markerAttributes`.

An `[Analyzer]` attribute and an "Analyzer" layer existed while `PSXRecomp.Analyzer` was itself a project the contract had to classify; both were retired in #294. The generic `loach.ArchitectureAnalyzer` package carries no PSX-specific layer.

### Applicability by Type

- **class**: All attribute types applicable
- **record**: `[Domain]`, `[Application]`, `[Infrastructure]` applicable
- **struct**: `[Domain]`, `[Application]` applicable
- **interface**: `[Domain]` applicable
- **enum**: `[Domain]` applicable
- **delegate**: `[Domain]` applicable
- **partial type**: Attributes split across partial parts

## Namespace Matrix

| Project | Namespace | Responsibility |
|---------|-----------|----------------|
| `PSXRecompStudio` | `PSXRecompStudio` | Application (UI, ViewModels, orchestration) |
| `PSXRecomp.Core` | `PSXRecomp.Core` | Domain logic + ports + C ABI interop (`NativeInterop`, `PSXCoreWrapper`) |
| `PSXRecomp.Native` | *(C++; no managed namespace)* | Native emulation core; outside managed AARC layers |
| `PSXRecomp.Infrastructure` | `PSXRecomp.Infrastructure` | Managed host adapters (activated by Issue #458: `GeneratedHostBuildService`; Issue #42: `FileProjectMetadataStore`) |
| `PSXRecomp.Tests` / `PSXRecompStudio.Tests` | test roots | Test infrastructure |

## Mechanical Enforcement

Compile-time enforcement is provided by the [`loach.ArchitectureAnalyzer`](https://github.com/mao2009/ArchitectureAnalyzer) NuGet package (`AARC` diagnostics), configured by `src/architecture.contract.json` and `.editorconfig`.

| ID | Rule | Severity |
|----|------|----------|
| `AARC002` | Forbidden dependency edge | error |
| `AARC003` | Forbidden API usage per layer | error |
| `AARC004` | Missing architecture layer declaration | error |
| `AARC005` | Multiple layer declarations on one type | error |
| `AARC006` | Attribute layer does not match namespace mapping | error |
| `AARC007` | P/Invoke (`DllImport` / `LibraryImport`) outside the Domain interop boundary | error |

Severity is pinned explicitly in `.editorconfig` so the gate contract cannot be weakened silently.

### Migration history (PSXR → AARC)

Through #294 these rules were enforced by the former in-repo analyzer. Historical identity mapping:

| Old (PSXR) | New (AARC) |
|---|---|
| `PSXR001` | `AARC004` |
| `PSXR002` | `AARC005` |
| `PSXR003` | `AARC006` |
| `PSXR004` | `AARC002` |
| `PSXR005` | `AARC003` |
| `PSXR006` | `AARC007` |

Enforcement notes:

- Enforcement scope is classes (including records); structs, interfaces, enums, and delegates are recognized but not required to be annotated.
- A partial type is satisfied by any attributed part; a nested class inherits the layer of its enclosing attributed type.
- The `PSXRecomp.Architecture.*` marker namespace and generated code are exempt from the missing-declaration rule.
- Escape hatches for legitimate per-site usage use the relevant `#pragma warning disable AARCxxx` with a reviewable rationale. Suppression scope must never be widened beyond the exact wiring/test site.
- CI fails on any unsuppressed AARC error.

### Quality Gate Verification Record

- **2026-08-24 (Issue #105, PR #107)**: the former PSXR gate was verified end-to-end with a temporary forbidden dependency fixture.
- **2026-09-08 (Issue #294, PR #299)**: `loach.ArchitectureAnalyzer` verified as the sole gate; temporary fixtures proved `AARC002`, `AARC003`, `AARC004`, and `AARC007` fail builds as intended.
- **2026-09-17 (Issue #38)**: managed host-I/O policy was made internally consistent. Infrastructure is allowed to execute concrete host APIs, Domain → Infrastructure is mechanically forbidden, Application → Infrastructure remains forbidden except a narrowly scoped composition-root suppression, and Native remains outside managed AARC layers. Contract fixtures in this change verify the positive Infrastructure host-I/O case, the negative Domain → Infrastructure edge, and the explicit composition-root exception.

## Consistency Checks

1. **Repository Structure** ✅
   - `PSXRecompStudio` → `PSXRecomp.Core` is the normal managed production dependency.
   - `PSXRecomp.Core` → `PSXRecomp.Native` is only the documented P/Invoke/C ABI boundary.
   - `PSXRecomp.Infrastructure` was activated by Issue #458 with its first concrete adapter, `GeneratedHostBuildService` (implements the Domain-owned `IGeneratedHostBuildService` port). Issue #42 added a second adapter, `FileProjectMetadataStore` (implements the Domain-owned `IProjectMetadataStore` port).

2. **Dependency Matrix** ENFORCED — `AARC002` mechanically forbids Domain/Application from depending on concrete Infrastructure and forbids production → Test edges.

3. **Forbidden API** ENFORCED — `AARC003` keeps host APIs out of Domain/Application/Test/Generated where declared; Infrastructure intentionally owns concrete host-side API usage.

4. **C ABI Boundary** — `AARC007` keeps P/Invoke declarations in Domain. The native C++ implementation remains outside Roslyn/AARC.

5. **Layer Declaration** ENFORCED — `AARC004`–`AARC006` enforce declaration/uniqueness/namespace mapping.

## Issues Identified

1. **Missing Generated Code Project** — `PSXRecomp.Generated` is reserved but not yet defined.
2. **Production generated-host execution engine** — Issue #458 activated `PSXRecomp.Infrastructure` with the compile/link build substrate (`GeneratedHostBuildService`), but a production `IRecompiledExecutionEngine` backed by that substrate, and its wiring to the Studio, remain future work (tracked from #380/ADR-015's deferred Option B).

## Recommendations

- Do not create `PSXRecomp.Infrastructure` merely to occupy the layer. Create it together with the first production host adapter and its Domain port.
- Keep composition-root adapter construction isolated; repeated `AARC002` exceptions are evidence to introduce a dedicated Host/Bootstrap layer rather than weakening the dependency rules.
- Keep native C ABI integration and managed host adapters as separate architecture concepts.

---

**SSOT Status**

- Architecture Matrix: ✅ ESTABLISHED — subsystem SSOT for managed architecture rationale and high-level tables; executable rule data is `src/architecture.contract.json`.
- Managed host-I/O boundary: ✅ DEFINED — Domain ports inward, managed Infrastructure adapters outward.
- Top-level architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md).
- Mechanical enforcement: ✅ ACTIVE — `loach.ArchitectureAnalyzer` (`AARC002`–`AARC007`) via `src/architecture.contract.json` + `.editorconfig`.
- `PSXRecomp.Infrastructure` activated (Issue #458). Reserved but not yet activated: `PSXRecomp.Generated`.
