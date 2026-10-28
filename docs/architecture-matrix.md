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
| **Infrastructure** | Concrete **managed host adapters**: filesystem/disc acquisition, process/toolchain execution, network integrations, logging/output sinks, and other host side effects | `PSXRecomp.Infrastructure` | `PSXRecomp.Infrastructure`, `PSXRecomp.Cli` |
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

   *Amendment (PR #846, owner ruling): `Process.GetProcessById` exception.* A test may call `Process.GetProcessById(pid)` only for a child process that the test itself launched and still holds a `Process` object for. The PID is taken from that owned object and is a lookup aid, not proof of ownership. Before the looked-up process is used, the caller must confirm that its start time equals the `StartTime` recorded from the owned `Process` object when the test launched it, which guards against PID reuse. If the start times differ, or the process has exited, the caller treats the process as not owned and does nothing to it. Every operation that changes the process (wait, kill) goes through the owned `Process` object, never through the looked-up one. The caller must never operate on a process it did not start. The `AARC003` suppression applies only to that call site. No call site in PR #846 uses this exception at the time of this amendment.

   *Amendment (PR #846, owner ruling): junction-fixture launch exception.* This is distinct from the `Process.GetProcessById` exception above. A test may launch `cmd.exe` once, solely to run `mklink /J` and create a junction fixture, under these conditions: (1) it is test-only, and no production code path may use it; (2) the link is created only inside the test's own temporary directory; (3) the command line is built only from paths that pass a check refusing a double quote, a percent sign, a caret, a control character, or a trailing backslash or slash (a path that fails is refused with a reason and no process is started). Each accepted path is wrapped in double quotes, and the command runs as `cmd.exe /d /v:off /c mklink /J "<link>" "<target>"`: `/d` skips AutoRun commands and `/v:off` turns delayed expansion off. Inside the quotes the characters `&`, `(`, `)` and `!` are passed literally; the tests check this with junctions created for paths containing them and with Japanese text, and they check that injection-shaped names are refused before any launch. No wider claim is made than this set of refused characters and these switches; (4) the test holds and waits on only the `Process` object that its own launch returned, with a bound; a launch still running after the bound, or after an abnormal exit, is killed through that object's process tree and awaited, and no other process is ever inspected, stopped, or found by PID; (5) the fixture verifies that the junction exists and resolves before a test relies on it, and a test whose junction cannot be created is skipped with its reason; (6) disposal removes only the link and never the link's target, and no test recursively deletes through a junction. The exception covers this one launch shape and nothing else: it does not permit compiler launches beyond the existing rule, host-mutating P/Invoke, or any production path. Each `AARC003` suppression for the launch states this exception.

   *Amendment (PR #846, same-user residuals).* The measurement's staging, cleanup and publish code runs as the same user as any other process that could write the output directory, and that is the threat model. Within it: (a) an existing output path is refused and left as it is, and a publish never replaces an existing directory, because `Directory.Move` refuses an existing name; (b) cleanup removes only an entry this build created, and only while its identity (volume serial number, file index and creation time, read right after the create, the adoption or the move) still matches; an entry whose identity is missing, changed or unreadable is left in place and listed as a residual in the failure message. An entry the publish step writes is adopted as this build's own right after that step (`StagingArea.AdoptPublished`), with its identity read then. `MoveNew` and `MoveNewDirectory` move a source only when the source is owned (created, adopted, or a reserved name), its parent chain is real directories up to the staging root, and, for a recorded entry, its identity still matches the record; the destination is then checked against the same identity. A source that is not owned is never moved and never adopted; a destination is adopted only when its source was owned; each refusal is listed as a residual. A directory move re-records the owned entries under the moved tree before its checks, so cleanup still owns them if a check fails. A reserved name that a toolchain writes has no identity and is moved or removed by name only while it is a file that is not a link; (c) a link found under the staging area fails closed, nothing is removed through a link, and a link's target is never deleted recursively. The following windows remain open and are not closed: (i) between the check that a staging entry is real and the rename or delete that follows it, a same-user process can replace the entry with a link (`StagingArea.Commit` and `RemoveOwned`); (ii) between a toolchain's write of a reserved name and its move or removal, the name can be replaced, since no identity is recorded for it; (iii) a later file can reuse the volume serial number, file index and creation time of a removed entry only if the platform reuses all three, which the check does not exclude; (iv) a same-user process can swap a directory on the path for a link between the chain check and a write beneath it (`RequireRealChain` before each write; not tested); (v) in `StagingArea.ReleaseEmptyDirectory`, the empty publish directory can be replaced between its identity check and `Directory.Delete` (the swap before the check is tested by `ReleaseEmptyDirectory_SwappedBeforeTheDelete_IsNotDeletedAndIsReported`; the swap between check and delete is not tested); (vi) in `StagingArea.MoveNew` and `MoveNewDirectory`, a same-user process can replace an owned entry, or a directory on its path, between the chain and identity checks and the move. Tested: a link at the source is refused (`MoveNew_SourceIsALink_IsRefusedBeforeTheMove`, `MoveNewDirectory_SourceIsALink_IsRefusedAndNothingIsMoved`); a parent replaced by a junction to a third-party directory is refused and the third-party file is neither moved nor adopted (`MoveNew_PublishReplacedByAJunctionToAThirdPartyDirectory_MovesNothingAndTheThirdPartyFileSurvives`, `MoveNew_ThirdPartyNameBoundToTheSameFile_IsRefusedByTheChainCheck`, `MoveNewDirectory_PublishReplacedByAJunctionToAThirdPartyDirectory_MovesNothing`); an unowned source is refused (`MoveNew_SourceNotOwned_IsRefusedAndNeverAdopted`). The first and third of the junction cases are refused by the identity check; only the hard-link case isolates the chain check for `MoveNew`, and no test isolates it for `MoveNewDirectory`. Not tested and not closed: a swap between the checks and the move itself; the destination check after the move, which refuses and records a residual, is not exercised by any test. (vii) an entry's identity is read after its creation or adoption, so a replacement in the window between the create or publish and that read is taken as the identity (not tested). (viii) a publish-step output can be replaced between the publish and `AdoptPublished` (not tested). (ix) a link created inside a publish subtree after the pre-move scan and before the directory move is moved with the tree; the scan after the move finds it and refuses with a residual (not tested; the pre-move scan is tested only for a link that is the source itself, not for a nested link, and the post-move scan is not tested). These are residuals of the same-user threat model. They are not guarantees, and no claim is made that they are closed.

   *Record: analyzer behaviour and unreported call sites (PR #846, owner ruling A).* Behaviour: AARC003 reports, for each method and forbidden class, only the last forbidden call in that method. Earlier calls of the same class in that method are neither reported nor suppressed. Minimal reproduction (a temporary probe in the Test layer, deleted, not in the repository): method `SameApiTwiceInOneMethod` calls `File.ReadAllText` twice and `Directory.CreateDirectory` twice. Expected: four diagnostics, one per call. Actual: two, on the second `ReadAllText` and on the second `CreateDirectory`. Method `TwoClassesOneMethod` calls `File.Delete` and then `Directory.Delete`. Expected: two. Actual: two. Count, with the exact method: in `FirmwareDiagnosticDistributionTests.cs` and `RealRomAnalysis/TempDirectory.cs`, every AARC003 pragma line was removed in a scratch copy and the projects were built with the analyzer. That build reports 92 unique lines. The repository keeps 91 pragma pairs, each covering at least one reported line. A call-site line is a line that is neither a comment nor a pragma and that matches `\b(File|Directory|Process|Environment|Thread)\.` or `DateTime\.UtcNow` (static), or `\.(HasExited|WaitForExit|Kill|ExitCode|StandardInput|Handle)\b` (instance). 149 call-site lines match; 92 are reported; 57 are not reported (55 match a static pattern, 2 only an instance pattern). Classification of the 57: test-only compiler launch (the `Process.Start` of a `dotnet build` stub, its `WaitForExit` and `Kill`): 3 lines; not executed (a `File.WriteAllText` inside the raw-string source of a generated stub project): 1 line; legitimate test-only operations on the test's own temporary directory (fixture reads and writes, moves and deletions of fixture entries, removal of a link by name): 53 lines. No design violation was found. Under owner ruling A the 57 lines carry no suppression; they are recorded here as analyzer misses.
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
