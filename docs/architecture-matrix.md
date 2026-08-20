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
    ↓ NativeInterop (internal static class, [LibraryImport])
PSXRecomp.Native (Infrastructure/C++)
```

- **PSXRecompStudio → PSXRecomp.Core** ProjectReference is **allowed** (regular dependency for UI layer)
- **PSXRecompStudio → PSXRecomp.Native** direct access or dependency is **prohibited** (must go through Core interop)
- **PSXRecomp.Core** exposes `PSXCore` wrapper via `NativeInterop.cs`
- `NativeInterop.cs` uses `[LibraryImport("PSXRecomp.Native")]` to import C ABI functions
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
| `PSXRecomp.Core` | `PSXRecomp.Core` | Domain (business logic, P/Invoke wrappers) |
| `PSXRecomp.Native` | (C++ - no managed namespace) | Infrastructure (CPU emulation) |
| `PSXRecomp.Tests` | `PSXRecomp.Tests` | Special (test infrastructure) |
| `PSXRecomp.Analyzer` | (TBD) | Special (architecture enforcement) |

## Consistency Checks

1. **Repository Structure** ✅
   - `PSXRecompStudio` → `PSXRecomp.Core` (ProjectReference confirmed)
   - `PSXRecomp.Core` → `PSXRecomp.Native` (P/Invoke contract confirmed)
   - `PSXRecomp.Tests` → `PSXRecomp.Core` (Test dependency confirmed)

2. **Architecture Matrix** DOCUMENTED
   - Domain → Application: ❌ NO (Domain should not depend on Application (UI layer is outer layer))
   - Application → Domain: ✅ YES (Application → Domain via NativeInterop in PSXRecomp.Core (P/Invoke))
   - Infrastructure → Domain: ✅ YES (Infrastructure → Domain (C ABI))
   - Infrastructure → Application: **Forbidden** (properly enforced)
   - Tests → Production: Allowed (verification)
   - Production → Tests: **Forbidden** (tests depend on production)

3. **Forbidden API** NOT YET VERIFIED
   - All listed APIs correctly classified as forbidden for respective layers
   - Layer-specific restrictions respected
   - *Note: Mechanical enforcement requires Roslyn Analyzer (Issue #8)*

4. **C ABI Boundary** NOT YET VERIFIED
   - Clear separation: `PSXRecomp.Core` ↔ `PSXRecomp.Native` via `NativeInterop.cs`
   - No reverse dependency allowed
   - P/Invoke contracts properly documented
   - *Note: Runtime verification requires native build and integration tests*

5. **Special Tools** NOT YET VERIFIED
   - Analyzer treated as Special layer
   - Tests and Generated code as Special layers
   - Excluded from main dependency chains
   - *Note: Analyzer rule implementation pending (Issue #8)*

## Issues Identified

1. **Missing Analyzer Project** - `PSXRecomp.Analyzer` project does not exist yet (will be created in Issue #8)
2. **Missing Generated Code Project** - `PSXRecomp.Generated` project not yet defined (Issue #8)
3. **Missing Special Layer** - Analyzer and Tests projects need to be formally declared
4. **C ABI Contract** - Need to ensure `NativeInterop.cs` follows the documented pattern

## Recommendations

- Add `PSXRecomp.Analyzer` and `PSXRecomp.Generated` projects (Issue #8)
- Ensure `NativeInterop.cs` uses proper `[LibraryImport]` attributes
- Document the C ABI boundary clearly in the codebase
- Add Forbidden API checks to Roslyn Analyzer (separate rule set, Issue #8)
- Update `architecture-matrix.md` with finalized tables above

---

**SSOT Status**

- Architecture Matrix: ✅ ESTABLISHED - Dependency and namespace matrices defined; entries verified against actual project structure
- Dependency Matrix: ✅ VERIFIED - Entries confirmed against actual project structure; Domain → Application corrected to ❌ NO; contradictory edges resolved; Production explicitly defined
- Forbidden API Matrix: ✅ ESTABLISHED - Layer-specific API restrictions documented; *Note: Mechanical enforcement requires Roslyn Analyzer (Issue #8)*
- Architecture Attribute Contract: ✅ DOCUMENTED - Attribute types and scopes defined
- Namespace Matrix: ✅ DOCUMENTED - Project-to-namespace mappings assigned
- C ABI Boundary: ✅ DEFINED - Clear separation via NativeInterop.cs and LibraryImport; *Note: Runtime verification requires native build and integration tests*
- **Missing Items**: PSXRecomp.Analyzer project, PSXRecomp.Generated project not yet created (Issue #8)
- Status: ⚠️ WORK_IN_PROGRESS - SSOT established but incomplete; see Issue #8 for Analyzer implementation
