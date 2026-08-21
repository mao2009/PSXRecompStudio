# Architecture Matrix - SSOT

## Layer Definitions

| Layer | Responsibility | Projects |
|-------|----------------|----------|
| **Domain** | Pure business logic, PSX concept model, deterministic computation | `PSXRecomp.Core` (Domain + C ABI interop wrappers) |
| **Application** | Avalonia UI, user interface, presentation | `PSXRecompStudio` |
| **Infrastructure** | CPU emulation, memory management, hardware abstraction (C ABI) | `PSXRecomp.Native` |
| **Interop** | C ABI boundary, native library loading, P/Invoke wrappers | `PSXRecomp.Core` (native-handle & NativeInterop residency) |
| **Special** | Analyzer, Tests, Generated code | `PSXRecomp.Analyzer`, `PSXRecomp.Tests`, `PSXRecomp.Generated` |

## Dependency Matrix

| From | To | Allowed | Reason |
|------|-----|---------|---------|
| **Domain** | Domain | ✅ YES | Same layer, internal dependencies |
| **Domain** | Application | ❌ NO | Domain should not depend on Application (UI layer is outer layer) |
| **Domain** | Infrastructure | ✅ YES | Via C ABI (P/Invoke); Domain defines wrapper contract |
| **Application** | Domain | ✅ YES | Application → Domain via NativeInterop in PSXRecomp.Core (P/Invoke wrappers) |
| **Application** | Infrastructure | ❌ NO | Application should not depend on Infrastructure directly (cross-layer dependency) |
| **Infrastructure** | Domain | ✅ YES | Infrastructure → Domain (C ABI boundary) |
| **Infrastructure** | Application | ❌ NO | Infrastructure should not depend on Application directly |
| **Infrastructure** | Special | ✅ YES | Infrastructure → Analyzer, Tests, Generated code |
| **Special** | Domain | ✅ YES | Special → Domain (analysis, testing) |
| **Special** | Application | ✅ YES | Special → Application (testing, validation) |
| **Special** | Infrastructure | ✅ YES | Special → Infrastructure (generation, debugging) |
| **Production** | Domain | ✅ YES | Production → Domain (specification) |
| **Production** | Application | ✅ YES | Production → Application (integration) |
| **Production** | Infrastructure | ✅ YES | Production → Infrastructure (deployment) |
| **Production** | Tests | ❌ NO | Production → Tests (tests depend on production, not vice versa) |
| **Tests** | Production | ✅ YES | Test → Production (verification) |
| **Tests** | Domain | ✅ YES | Test → Domain (validation) |
| **Tests** | Infrastructure | ✅ YES | Test → Infrastructure (fixtures) |
| **Tests** | Application | ✅ YES | Test → Application (UI testing) |
| **Analyzer** | Domain | ✅ YES | Analyzer → Domain (enforcement) |
| **Analyzer** | Application | ✅ YES | Analyzer → Application (enforcement) |
| **Analyzer** | Infrastructure | ✅ YES | Analyzer → Infrastructure (enforcement) |
| **Analyzer** | Special | ✅ YES | Analyzer → Special (self) |
| **Generated code** | Production | ✅ YES | Generated → Production (deployment) |
| **Generated code** | Domain | ✅ YES | Generated → Domain (usage) |
| **Generated code** | Application | ✅ YES | Generated → Application (usage) |

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

## Forbidden API Matrix

| Layer | API Type | Allowed | Forbidden | Reason |
|-------|----------|---------|-----------|---------|
| **Domain** | `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.*` | ❌ NO | ✅ YES | Loss of determinism |
| **Domain** | `Guid.NewGuid()`, `Random.Shared` | ❌ NO | ✅ YES | Non-deterministic randomness |
| **Domain** | `Environment.*` | ❌ NO | ✅ YES | Execution environment dependency |
| **Domain** | `File.*`, `Directory.*` | ❌ NO | ✅ YES | External I/O (Infrastructure/Adapter responsibility) |
| **Domain** | `Console.*` | ❌ NO | ✅ YES | Standard output (Infrastructure/Adapter responsibility) |
| **Domain** | `Process.*` | ❌ NO | ✅ YES | Process control (Infrastructure/Adapter responsibility) |
| **Domain** | `HttpClient`, `Socket` | ❌ NO | ✅ YES | Network (Infrastructure/Adapter responsibility) |
| **Application** | `File.*`, `Directory.*` | ❌ NO | ✅ YES | External I/O (UI layer orchestrates via Infrastructure adapters) |
| **Application** | `Console.*` | ❌ NO | ✅ YES | Standard output (UI layer orchestrates via Infrastructure adapters) |
| **Infrastructure** | All external I/O | ❌ NO | ✅ YES | Emulator purity requirement; must abstract for testability |
| **Infrastructure** | `File.*`, `Directory.*` | ❌ NO | ✅ YES | External I/O (should be abstracted behind adapter interface) |
| **Infrastructure** | `Console.*` | ❌ NO | ✅ YES | Standard output (should be abstracted behind adapter interface) |
| **Special** | `DateTime.Now`, `DateTime.UtcNow` | ❌ NO | ✅ YES | Determinism requirement (Tests must use frozen clocks) |
| **Special** | `Guid.NewGuid()` | ❌ NO | ✅ YES | Non-deterministic randomness (Tests should use deterministic IDs) |
| **Special** | `Environment.*` | ❌ NO | ✅ YES | Execution environment dependency (Tests mock/isolate) |
| **Special** | `Console.*` | ❌ NO | ✅ YES | Standard output (Tests capture / suppress output) |
| **Special** | `File.*` | ❌ NO | ✅ YES | External I/O (Tests use temporary isolated paths) |
| **Special** | `Directory.*` | ❌ NO | ✅ YES | External I/O (Tests use isolated temp directories) |
| **Special** | `Process.*` | ❌ NO | ✅ YES | Process control (Tests spawn subprocess with resource limits) |
| **Special** | `Thread.*` | ❌ NO | ✅ YES | Thread safety concerns (Tests run in isolated contexts) |
| **Special** | `Task.Delay` | ❌ NO | ✅ YES | Asynchronous timing (Tests use controlled schedulers) |
| **Special** | `Random` | ❌ NO | ✅ YES | Non-deterministic randomness (Tests use deterministic PRNGs) |

## Architecture Attribute Contract

| Attribute | Scope | Applicability | Class | Notes |
|-----------|-------|---------------|-------|-------|
| `[Domain]` | Domain layer | ✅ | All classes | Pure business logic, no side effects |
| `[Application]` | Application layer | ✅ | UI components, ViewModels | UI-specific logic |
| `[Infrastructure]` | Infrastructure layer | ✅ | CPU emulation code | C ABI boundary, no UI/domain coupling |
| `[Analyzer]` | Special | ✅ | Analyzer classes | Enforcement of architecture rules |
| `[Test]` | Special | ✅ | Test classes | Unit/integration tests |
| `[Generated]` | Special | ✅ | Generated code | Auto-generated artifacts |

### Applicability by Type

- **class**: All attribute types applicable
- **record**: `[Domain]`, `[Application]`, `[Infrastructure]` applicable
- **struct**: `[Domain]`, `[Application]` applicable (no side effects)
- **interface**: `[Domain]` applicable (contract, no implementation)
- **enum**: `[Domain]` applicable (pure values)
- **delegate**: `[Domain]` applicable (pure function pointers)
- **partial type**: Attributes split across partial parts

### Layer/Boundary Classification

- **Tests**: Attributes used for test categorization; Analyzer may exclude test projects
- **Analyzer**: `[Analyzer]` attribute used on enforcement classes; Analyzer itself may have exceptions
- **Generated Code**: `[Generated]` attribute applied; Namespace validation and Forbidden API checks may be relaxed

## Namespace Matrix

| Project | Namespace | Responsibility |
|---------|-----------|----------------|
| `PSXRecompStudio` | `PSXRecompStudio` | Application (UI, ViewModels) |
| `PSXRecompStudio` | `PSXRecompStudio.ViewModels` | Application (UI models) |
| `PSXRecomp.Core` | `PSXRecomp.Core` | Domain (business logic) + Interop (`NativeInterop`, `PSXCoreWrapper`) |
| `PSXRecomp.Native` | (C++ - no managed namespace) | Infrastructure (CPU emulation) |
| `PSXRecomp.Tests` | `PSXRecomp.Tests` | Special (test infrastructure) |
| `PSXRecomp.Analyzer` | `PSXRecomp.Analyzer` | Special (architecture enforcement) |

## Mechanical Enforcement (Roslyn Analyzer)

The rules in this matrix are enforced at compile time by `PSXRecomp.Analyzer` (see ADR-006). All diagnostics are reported as **errors** and fail the build.

| ID | Rule | Scope |
|----|------|-------|
| `PSXR001` | Missing architecture attribute on class | All classes (marker namespace and generated code exempt) |
| `PSXR002` | Multiple architecture attributes on one type | All classes |
| `PSXR003` | Attribute layer does not match namespace mapping | All classes |
| `PSXR004` | Forbidden dependency edge (inner → outer, Production → Test) | All classes |
| `PSXR005` | Forbidden API usage per layer (Forbidden API Matrix) | All classes |
| `PSXR006` | P/Invoke (`DllImport` / `LibraryImport`) outside `PSXRecomp.Core` | All classes |

Enforcement notes:

- Production → Analyzer / Generated dependencies are **not** enforced yet; the SSOT does not declare these edges explicitly (tracked for clarification).
- Enforcement scope is **classes** (including records); structs, interfaces, enums, and delegates are recognized but not required to be annotated in this iteration.
- CI fails on any violation because all diagnostics have error severity.

## Consistency Checks

1. **Repository Structure** ✅
   - `PSXRecompStudio` → `PSXRecomp.Core` (ProjectReference confirmed)
   - `PSXRecomp.Core` → `PSXRecomp.Native` (P/Invoke contract confirmed)
   - `PSXRecomp.Tests` → `PSXRecomp.Core` (Test dependency confirmed)

2. **Architecture Matrix** DOCUMENTED
   - Domain → Application: ❌ NO (Domain should not depend on Application (UI layer is outer layer))
   - Application → Domain: ✅ YES (Application → Domain via NativeInterop in PSXRecomp.Core (P/Invoke))
   - Infrastructure → Domain: ✅ YES (Infrastructure → Domain (C ABI))
   - Infrastructure → Application: **Forbidden** (no such ProjectReference exists; enforced by Roslyn Analyzer, PSXR004)
   - Tests → Production: Allowed (verification)
   - Production → Tests: **Forbidden** (tests depend on production)

3. **Forbidden API** ENFORCED
   - All listed APIs correctly classified as forbidden for respective layers
   - Layer-specific restrictions respected
   - *Mechanically enforced by Roslyn Analyzer (PSXR005)*

4. **C ABI Boundary** NOT YET VERIFIED
   - Clear separation: `PSXRecomp.Core` ↔ `PSXRecomp.Native` via `NativeInterop.cs`
   - No reverse dependency allowed
   - P/Invoke contracts properly documented
   - *Note: Runtime verification requires native build and integration tests*

5. **Special Tools** ENFORCED
   - Analyzer treated as Special layer
   - Tests and Generated code as Special layers
   - Excluded from main dependency chains
   - *Analyzer rules implemented (Issue #8); Production → Analyzer / Generated edges pending clarification*

## Issues Identified

1. **Missing Analyzer Project** - RESOLVED: `PSXRecomp.Analyzer` project exists and enforces this matrix (Issue #8)
2. **Missing Generated Code Project** - `PSXRecomp.Generated` project not yet defined (generated code is exempted by path convention until then)
3. **Incomplete Special Layer Declaration** - RESOLVED for `PSXRecomp.Analyzer`; `PSXRecomp.Generated` project remains absent
4. **C ABI Contract** - Runtime integration verification remains pending (native build passes locally; CI-level integration tests pending)

## Recommendations

- Define the `PSXRecomp.Generated` project and decide on Production → Generated dependency policy
- Verify the native build and integration tests for the documented `NativeInterop` boundary
- Document the C ABI boundary clearly in the codebase

---

**SSOT Status**

- Architecture Matrix: ✅ ESTABLISHED - Dependency and namespace matrices defined; entries cross-checked against actual project structure (manual)
- Dependency Matrix: ✅ ENFORCED - Entries cross-checked against actual `.csproj` ProjectReferences; Domain → Application corrected to ❌ NO; contradictory edges resolved; Production explicitly defined; *mechanically enforced by Roslyn Analyzer (PSXR004)*
- Forbidden API Matrix: ✅ ENFORCED - Layer-specific API restrictions documented; *mechanically enforced by Roslyn Analyzer (PSXR005)*
- Architecture Attribute Contract: ✅ ENFORCED - Attribute types and scopes defined; *presence, uniqueness, and namespace mapping enforced (PSXR001-003)*
- Namespace Matrix: ✅ DOCUMENTED - Project-to-namespace mappings assigned
- C ABI Boundary: ✅ DEFINED - Clear separation via NativeInterop.cs and LibraryImport; P/Invoke location enforced (PSXR006); *Note: Runtime verification requires native build and integration tests*
- **Missing Items**: PSXRecomp.Generated project not yet created; Production → Analyzer / Generated dependency policy pending clarification
- Status: ✅ ESTABLISHED AND MECHANICALLY ENFORCED - see "Mechanical Enforcement" section and ADR-006
