# ADR-015: The Production Execution Engine Is the Interpreter Backend, and It Lives in the Domain Layer

- **Status**: Accepted (amended 2026-09-16 by Issue #409; amended 2026-09-18 by Issue #458)
- **Date**: 2026-09-16
- **Issue**: #380

## Context

`ExecutionOrchestrator` (#366/PR #373) landed the full-title execution loop, but
every `IRecompiledExecutionEngine` implementation lived in `PSXRecomp.Tests`
(`RecompiledIrTitleExecutionEngine`, `InterpreterTitleExecutionEngine`,
`HostTitleExecutionEngine`), all `[Test]`-attributed. `PSXRecompStudio` had zero
references to any execution type. The capability was 100% test-only: there was
no path, from the shipped product, to execute a title at all. PR #373 disclosed
this itself and proposed this ADR.

`IRecompiledExecutionEngine`'s own doc comment already states the intended
split: implementations that need *a compiler, temporary files, or process
control* live outside the Domain layer, "while pure-CPU backends may live
anywhere below." Issue #380 asks where a production implementation belongs
(Infrastructure vs. an Application-owned adapter) and how it reaches a toolchain
without that knowledge entering the Domain layer.

Two facts about this repository's declared layering (`src/architecture.contract.json`,
enforced as build errors by the AARC analyzer per [ADR-006](006-architecture-analyzer-enforcement.md))
constrain the answer, and both were checked against the contract rather than
assumed:

- **`Application → Infrastructure` is a forbidden dependency.** The contract's
  stated reason is that "the Application layer must reach Infrastructure only
  through the Domain interop boundary." `PSXRecompStudio` is the Application
  layer. A production engine placed in a new `PSXRecomp.Infrastructure` project
  is therefore *not directly referenceable from the Studio at all*: consuming it
  would additionally require a Domain-declared abstraction plus a composition
  root that resolves it — real work that buys nothing for a backend with no
  Infrastructure-shaped dependency.
- **P/Invoke is pinned to the Domain layer** (`interopBoundaryRules`, AARC007).
  The native R3000A interpreter is reached through `PSXCoreWrapper`, which is
  already `[Domain]` production code today.

## Decision

**Option A (adopted): the production `IRecompiledExecutionEngine` is the
interpreter-backed one, and it lives in the Domain layer.**

`InterpreterTitleExecutionEngine` moves from `src/PSXRecomp.Tests/Execution/` to
`src/PSXRecomp.Core/Execution/`, becoming `[Domain] public sealed` production
code. It is not reimplemented or forked: the test copy is deleted and the
existing `ExecutionOrchestratorTests` now exercise the production type, so there
is exactly one interpreter engine in the repository.

Domain residency is justified against the contract, not by convenience:

- It needs **no compiler, no temporary files, no process control** — the three
  reasons `IRecompiledExecutionEngine` itself gives for living outside the
  Domain layer. It is precisely the "pure-CPU backend" that doc comment permits
  "anywhere below."
- Its dependencies are all already Domain: `PSXCoreWrapper` (the Domain interop
  boundary), `BiosVectorDispatch`/`BiosJumpTables`, `IGuestMemoryReader`/`Writer`,
  `Ps1AddressTranslation`.
- It touches none of the Domain layer's forbidden APIs (`System.Console`,
  `System.IO.File`/`Directory`, `System.Environment`,
  `System.Diagnostics.Process`, clocks, randomness). The analyzer confirms this:
  `dotnet build src/PSXRecompStudio.slnx -c Release` passes with AARC002/003/004/
  005/006/007 pinned to `error`.

**The Studio's wiring lives in the Application layer.**
`PSXRecompStudio.Services.TitleExecutionService` (`[Application]`) is the
composition root: it assembles the engine, a `BiosHleRuntime` over the engine's
own guest memory, and `ExecutionOrchestrator`, then returns the classified
`TitleExecutionResult` plus the guest's raw TTY bytes. It owns *only* wiring —
no CPU semantics, no BIOS semantics, no outcome classification, all of which
stay in the Domain layer. `MainWindowViewModel.RunDiagnosticTitleCommand` is the
Studio action that calls it, so the product now genuinely reaches
`request → engine load → bounded run → BIOS handoff → classified result`.

The output sink and the handoff policy are Application-owned, consistent with
[ADR-014](014-bios-hle-runtime-contract.md)'s rule that the Domain layer fixes no
encoding: the service's private `CollectedOutput` collects raw bytes and the
view model decides how to render them, and its private `ProgramEndHandoff`
reports only "the guest ran off the end of its own program image" as an exit,
declining every other unresolved transfer so it is classified as
`UnsupportedTransfer` rather than silently treated as success.

**Nothing about the toolchain enters the Domain layer, because this backend has
no toolchain.** That is the actual resolution of Issue #380's "how does it
invoke a toolchain without Core knowing about compilers/processes" question for
this slice: the question is deferred along with the backend that raises it.

## Consequences

- **Positive**: the Studio can execute a title through production code. Issue
  #380's stated completion criterion — "a minimal production implementation
  exists outside the Test assembly, wired to at least a CLI entry point or
  Studio action" — is met by an actual Studio action, not only by a service
  class.
- **Positive**: no duplicate engine. The interpreter engine has one definition;
  tests consume production code, never the reverse. No production assembly
  references `PSXRecomp.Tests.*`.
- **Positive**: no new project, no new dependency, no speculative abstraction.
  `PSXRecomp.Infrastructure` stays empty and reserved (Issue #38) until a
  component actually needs it.
- **Negative / known limitation**: the production path executes through the
  **interpreter**, not through recompiled/generated host code. This is a real
  capability gap and is named as such, not papered over — the engine's
  `Name` is `interpreter-native-full-title`, and it is reported in every
  `TitleExecutionResult.EngineName`.
- **Negative / known limitation**: `TitleExecutionService.Run` took an
  in-memory program image and started from a zeroed register file with no
  initial-memory seed. Loading a real title (disc/EXE image → program image +
  initial state) was a separate concern that was not wired here; the analysis
  side of it already exists (`RealRomAnalysis`), the joining of the two did
  not. **Amended by #409**: `TitleExecutionService.Run(PsxExe, ...)` now loads
  an analyzed PS-X EXE image with its header-derived initial state (entry PC,
  SP, GP, text segment) into the same production composition root; the
  interpreter-backend limitation below is unchanged. **Amended further by this
  PR**: the Studio product flow actually reaches it — `MainWindowViewModel.
  RunRealTitleCommand` delegates to `RealRomTitleExecutionService`, which runs
  the disc analysis through `RomAnalysisPipeline`, retains
  `RomAnalysisOutcome.Executable`, and hands that same executable to
  `TitleExecutionService.Run(PsxExe, ...)`. The flow deliberately avoids the
  report-only `DiscImageAnalyzer` façade, which drops the executable; and no
  PS1 semantics live in the view model. Disc-image acquisition (file I/O) is
  left to the Infrastructure seam (Issue #38), so the action consumes pre-read
  disc bytes.
- **Negative**: `ExecutionOrchestratorTests` now depends on a production type.
  That is the intended direction of the dependency, but it does mean a change
  to the engine's public shape is now an API change rather than a test-fixture
  change.

## Deferred: the generated-host / compiler-backed production adapter (Option B)

Deliberately **not** attempted here, and explicitly not rejected on merit — only
on scope. `HostTitleExecutionEngine`/`RecompilerHostExecutor` (still `[Test]`,
still in `PSXRecomp.Tests`) run recompiled C through gcc, and a production
equivalent is what would make the product actually *recompile* rather than
interpret. It is a genuinely larger change than Issue #380's own "minimal"
criterion and non-goals allow, and folding it in would produce exactly the
mega-PR the #374 audit batch forbids.

**Amended by Issue #458**: sketch point 1 below is now landed. `PSXRecomp.Infrastructure`
exists, with `GeneratedHostBuildService` (`[Infrastructure]`) implementing the
Domain-owned `IGeneratedHostBuildService` port (`PSXRecomp.Core.Recompiler`) to
compile and link generated host C source into a native artifact at a
caller-selected output location, with structured (non-exception) failure
classification. `RecompilerHostExecutor.CompileRecompiledBinary` (test-only)
now calls this production service instead of invoking gcc itself, so the
differential harness and `HostTitleExecutionEngine` exercise the same compile/link
path a production caller would. Sketch points 2–4 remain open: no
`IExecutionEngineProvider`-shaped selector exists yet, no engine implementation
consumes this service, and no Studio/CLI composition root resolves one. This
Issue's explicit non-goal was the runtime entrypoint itself (#459) and CLI (#460).

A sketch for whoever picks up the remaining points, so the analysis is not redone from scratch:

1. **It cannot live in the Domain layer.** It needs `System.Diagnostics.Process`
   and `System.IO.File`/`Directory`, all three of which are on the Domain
   layer's forbidden-API list — the exact case
   `IRecompiledExecutionEngine`'s doc comment reserves for "outside the Domain
   layer." This is the concrete component that finally justifies creating
   `PSXRecomp.Infrastructure` (Issue #38).
2. **The `Application → Infrastructure` forbidden edge must be solved, not
   worked around.** The contract says Application reaches Infrastructure "only
   through the Domain interop boundary," so the Studio cannot reference the
   Infrastructure project directly. The expected shape is a Domain-declared
   factory/selector abstraction (e.g. an `IExecutionEngineProvider` beside
   `IRecompiledExecutionEngine`) that the Infrastructure adapter implements and
   a composition root supplies. Note that the contract also forbids
   `System.IO.File`/`Directory` *in Infrastructure itself* ("external I/O must
   be abstracted behind an adapter interface"), so temp-file lifecycle needs its
   own adapter seam, not raw `File` calls.
3. **The hard parts are packaging, not architecture.** Shipping or locating a
   C toolchain for an end user, cross-platform process invocation, temp-file
   lifetime and cleanup on abnormal termination, and a sane failure mode when no
   compiler is present — these, not the layer question, are what make this a
   separate piece of work.
4. **Nothing above needs a contract change.** `IRecompiledExecutionEngine`,
   `ExecutionOrchestrator`, and `TitleExecutionService`'s shape are all
   engine-agnostic today; a host-backed engine drops into the same seam. The
   Studio's `TitleExecutionService` would gain an engine choice, not a rewrite.

## Alternatives Considered

- **Option B — put the production engine in a new `PSXRecomp.Infrastructure`
  project, host/compiler-backed from the start** — rejected for this PR on
  scope, and deferred above with a sketch rather than dismissed. It would also
  have required solving the `Application → Infrastructure` forbidden edge before
  any execution capability shipped at all, so the product would have gained
  nothing until the whole packaging problem was solved.
- **Put the interpreter engine in `PSXRecomp.Infrastructure` anyway, for
  symmetry with a future host engine** — rejected. It has no Infrastructure-
  shaped dependency (no I/O, no process, no compiler), so the only thing the
  move would add is the forbidden `Application → Infrastructure` edge and the
  indirection needed to route around it. "It is called an engine" is not a
  layering reason; the contract's reasons are dependency-shaped, and this
  engine's dependencies are Domain dependencies. Creating a project to hold one
  class that does not need it is the speculative structure the analyzer contract
  exists to keep honest, not to manufacture.
- **Keep the engine in `PSXRecomp.Tests` and have the Studio reference the test
  assembly** — rejected outright: `Application → Test` and `Domain → Test` are
  forbidden dependencies in the contract, enforced as build errors.
- **Ship only an Application-layer service with no Studio UI action** — allowed
  by Issue #380's non-goals, but rejected as unnecessarily thin: a service with
  no caller does not demonstrate that the product can reach execution. A single
  button and a status line cost ~20 lines and remove the ambiguity, without
  becoming the "polished product feature" the non-goals exclude.
- **Give `TitleExecutionService` a general "run any title" API with disc/EXE
  loading** — rejected as out of scope; see the known limitation above. The
  service exposes the execution contract it actually implements and no more.

## Related ADRs

- [ADR-006](006-architecture-analyzer-enforcement.md) — the analyzer and
  contract this decision is argued against, and which mechanically verifies it.
- [ADR-014](014-bios-hle-runtime-contract.md) — the shared BIOS dispatch the
  production engine applies in-band, and the encoding-free output-sink boundary
  the Studio's sink implements.
