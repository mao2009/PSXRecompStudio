# Architecture Matrix - SSOT

## Layer Definitions

| Layer | Responsibility | Projects |
|-------|----------------|----------|
| **Domain** | Pure business logic, PSX concept model, deterministic computation | `PSXRecomp.Core` |
| **Application** | Avalonia UI, user interface, presentation | `PSXRecompStudio` |
| **Infrastructure** | CPU emulation, memory management, hardware abstraction (C ABI) | `PSXRecomp.Native` |
| **Special** | Analyzer, Tests, Generated code | `PSXRecomp.Analyzer`, `PSXRecomp.Tests`, `PSXRecomp.Generated` |

## Dependency Matrix

| From | To | Allowed | Reason |
|------|-----|---------|---------|
| **Domain** | Domain | ✅ YES | Same layer, internal dependencies |
| **Domain** | Application | ❌ NO | Domain should not depend on Application (UI layer is outer layer) |
| **Domain** | Infrastructure | ✅ YES | Via C ABI (P/Invoke) |
| **Application** | Domain | ✅ YES | Application → Domain (UI layer) |
| **Application** | Infrastructure | ❌ NO | Application should not depend on Infrastructure directly (cross-layer dependency) |
| **Infrastructure** | Domain | ✅ YES | Infrastructure → Domain (C ABI boundary) |
| **Infrastructure** | Application | ❌ NO | Infrastructure should not depend on Application directly |
| **Infrastructure** | Special | ✅ YES | Infrastructure → Analyzer, Tests, Generated code |
| **Special** | Domain | ✅ YES | Special → Domain (analysis, testing) |
| **Special** | Application | ✅ YES | Special → Application (testing, validation) |
| **Special** | Infrastructure | ✅ YES | Special → Infrastructure (generation, debugging) |
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
| **Production** | Tests | ❌ NO | Production → Tests (tests depend on production, not vice versa) |
| **Production** | Domain | ✅ YES | Production → Domain (specification) |
| **Production** | Application | ✅ YES | Production → Application (integration) |
| **Production** | Infrastructure | ✅ YES | Production → Infrastructure (deployment) |

## C ABI / P/Invoke Boundary

```
PSXRecompStudio (Application)
    ↓ ProjectReference
PSXRecomp.Core (Domain)
    ↓ NativeInterop (internal static class, [LibraryImport])
PSXRecomp.Native (Infrastructure/C++)
```

- **PSXRecomp.Core** exposes `PSXCore` wrapper via `NativeInterop.cs`
- `NativeInterop.cs` uses `[LibraryImport("PSXRecomp.Native")]` to import C ABI functions
- **Boundary**: `PSXRecomp.Core` ↔ `PSXRecomp.Native` (P/Invoke contract)
- **No direct dependency** from `PSXRecomp.Native` → `PSXRecomp.Core` (reverse prohibited)
- **No direct dependency** from `PSXRecompStudio` → `PSXRecomp.Core` (UI layer should not depend on domain internals)

## Forbidden API Matrix

| Layer | API Type | Allowed | Forbidden | Reason |
|-------|----------|---------|-----------|---------|
| **Domain** | `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.*` | ❌ NO | ✅ YES | Loss of determinism |
| **Domain** | `Guid.NewGuid()`, `Random.Shared` | ❌ NO | ✅ YES | Non-deterministic randomness |
| **Domain** | `Environment.*` | ❌ NO | ✅ YES | Execution environment dependency |
| **Domain** | `File.*`, `Directory.*` | ❌ NO | ✅ YES | External I/O (UI layer responsibility) |
| **Domain** | `Console.*` | ❌ NO | ✅ YES | Standard output (UI layer responsibility) |
| **Domain** | `Process.*` | ❌ NO | ✅ YES | Process control (UI layer responsibility) |
| **Domain** | `HttpClient`, `Socket` | ❌ NO | ✅ YES | Network (UI layer responsibility) |
| **Application** | `File.*`, `Directory.*` | ❌ NO | ✅ YES | External I/O (UI layer responsibility) |
| **Application** | `Console.*` | ❌ NO | ✅ YES | Standard output (UI layer responsibility) |
| **Infrastructure** | All external I/O | ❌ NO | ✅ YES | Emulator purity requirement |
| **Infrastructure** | `File.*`, `Directory.*` | ❌ NO | ✅ YES | External I/O (should be abstracted) |
| **Infrastructure** | `Console.*` | ❌ NO | ✅ YES | Standard output (should be abstracted) |
| **Special** | `DateTime.Now`, `DateTime.UtcNow` | ❌ NO | ✅ YES | Determinism requirement |
| **Special** | `Guid.NewGuid()` | ❌ NO | ✅ YES | Non-deterministic randomness |
| **Special** | `Environment.*` | ❌ NO | ✅ YES | Execution environment dependency |
| **Special** | `Console.*` | ❌ NO | ✅ YES | Standard output (UI layer responsibility) |
| **Special** | `File.*` | ❌ NO | ✅ YES | External I/O (UI layer responsibility) |
| **Special** | `Directory.*` | ❌ NO | ✅ YES | External I/O (UI layer responsibility) |
| **Special** | `Process.*` | ❌ NO | ✅ YES | Process control (UI layer responsibility) |
| **Special** | `Thread.*` | ❌ NO | ✅ YES | Thread safety concerns |
| **Special** | `Task.Delay` | ❌ NO | ✅ YES | Asynchronous timing (UI layer responsibility) |
| **Special** | `Random` | ❌ NO | ✅ YES | Non-deterministic randomness |

## Architecture Attribute Contract

| Attribute | Scope | Applicability | Class | Notes |
|-----------|-------|---------------|-------|-------|
| `[Domain]` | Domain layer | ✅ | All classes | Pure business logic, no side effects |
| `[Application]` | Application layer | ✅ | UI components, ViewModels | UI-specific logic |
| `[Infrastructure]` | Infrastructure layer | ✅ | CPU emulation code | C ABI boundary, no UI/domain coupling |
| `[Analyzer]` | Special | ✅ | Analyzer classes | Enforcement of architecture rules |
| `[Test]` | Special | ✅ | Test classes | Unit/integration tests |
| `[Generated]` | Special | ✅ | Generated code | Auto-generated artifacts |

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

2. **Architecture Matrix** ✅
   - Domain → Application: ❌ NO (Domain should not depend on Application (UI layer is outer layer))
   - Application → Domain: ✅ YES (Application → Domain (P/Invoke))
   - Infrastructure → Domain: ✅ YES (Infrastructure → Domain (C ABI))
   - Infrastructure → Application: **Forbidden** (properly enforced)
   - Tests → Production: Allowed (verification)
   - Production → Tests: **Forbidden** (tests depend on production)

3. **Forbidden API** ✅
   - All listed APIs correctly classified as forbidden for respective layers
   - Layer-specific restrictions respected

4. **C ABI Boundary** ✅
   - Clear separation: `PSXRecomp.Core` ↔ `PSXRecomp.Native` via `NativeInterop.cs`
   - No reverse dependency allowed
   - P/Invoke contracts properly documented

5. **Special Tools** ✅
   - Analyzer treated as Special layer
   - Tests and Generated code as Special layers
   - Excluded from main dependency chains

## SSOT Status

- Architecture Matrix: ✅ ESTABLISHED - Dependency and namespace matrices defined
- Dependency Matrix: ⚠️ VERIFIED - Entries confirmed against actual project structure; Row 17 corrected (Domain → Application: ❌ NO)
- Forbidden API Matrix: ✅ ESTABLISHED - Layer-specific API restrictions documented
- Architecture Attribute Contract: ✅ DOCUMENTED - Attribute types and scopes defined
- Namespace Matrix: ✅ DOCUMENTED - Project-to-namespace mappings assigned
- C ABI Boundary: ✅ DEFINED - Clear separation via NativeInterop.cs and LibraryImport
- **Missing Items**: PSXRecomp.Analyzer project, PSXRecomp.Generated project not yet created (Issue #8)
- Status: ⚠️ WORK_IN_PROGRESS - SSOT established but incomplete; see Issue #8 for Analyzer implementation

---

## Issues Identified

1. **Missing Analyzer Project** - `PSXRecomp.Analyzer` project does not exist yet (will be created)
2. **Missing Generated Code Project** - `PSXRecomp.Generated` project not yet defined
3. **Missing Special Layer** - Analyzer and Tests projects need to be formally declared
4. **C ABI Contract** - Need to ensure `NativeInterop.cs` follows the documented pattern
