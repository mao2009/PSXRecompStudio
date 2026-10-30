# Issue #40: Studio GUI Architecture and Application Boundary (Design)

**Status:** Proposed (design only; no implementation in this change). D2-A adopted provisionally on 2026-10-30 (section 9; revision note in section 11).

**Related Issues:** #40 (this design), #38 (host I/O ports and adapters), #75 (GUI/UX SSOT), #21 (localization), #851 (Rust/egui GameWindow PoC, scope limited to `PSXRecomp.GameWindow`)

**Base:** `2e3ecad` on `main` (branch point; the single verification base for this document). The `src/` tree is identical between `2e3ecad` and the document's HEAD (`git diff --stat 2e3ecad HEAD -- src` is empty), so `src/` citations hold at HEAD. Commits between the base and HEAD only change this document.

**Related SSOTs:** [`docs/architecture/gui-ux.md`](../architecture/gui-ux.md), [`docs/architecture-matrix.md`](../architecture-matrix.md), [`src/architecture.contract.json`](../../src/architecture.contract.json), [`docs/adr/015-production-execution-engine-ownership.md`](../adr/015-production-execution-engine-ownership.md), [`docs/adr/017-managed-host-io-port-adapter-boundary.md`](../adr/017-managed-host-io-port-adapter-boundary.md), [`docs/development/headless-cli.md`](../development/headless-cli.md)

## 0. How to read this document

Every factual claim carries one label and a `path:line` reference (paths are repository-relative):

| Label | Meaning |
|---|---|
| **CONFIRMED** | Read directly in the repository source, contract or document at the cited line at the verification base (`2e3ecad`, section header). Source verification only. |
| **READ** | A claim taken from an issue body fetched in this session via the GitHub public API (`https://api.github.com/repos/mao2009/PSXRecompStudio/issues/<N>`). It is **not** source verification: it cannot be reproduced from the repository, and the issue text may change. |
| **OBSERVED** | Seen in a directory listing or a grep result, but not traced through execution. |
| **PREDICTED** | A consequence expected from a proposed change; not built or measured. |
| **UNVERIFIED** | Not checked in this session. No build, test or analyzer run was performed. |

Recommendations are not labelled; they are proposals for the owners in section 9.

Issue #40 asks for a design, not an implementation. Nothing in this change alters source, the architecture contract, or existing docs. The architecture matrix and ADR revisions proposed below are owner decisions, not edits made here.

## 1. Problem statement

Issue #40 requires that GUI, CLI and MCP share Application-layer use cases, that the GUI add no business logic, and that Analysis -> Recomp -> Build -> Run have async work, progress, cancellation and error display (**READ** issue #40 body, read via the GitHub public API in this session: GUI, CLI and MCP use the same Application-layer use cases where possible; GUI-specific domain logic is not created; long operations provide progress, cancellation and logs). This document records what the current code supports, where the boundary is broken, and the smallest coherent next step.

Summary: **CONFIRMED** the Domain layer already owns most pipeline logic (analysis `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:142`, execution orchestration `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:61`, code generation `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:15`, a build port `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:86`), and the Studio already calls part of it (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`, `src/PSXRecompStudio/Services/TitleExecutionService.cs:78`). **CONFIRMED** (sections 2.2 and 2.3): what is missing is a use-case layer that both the Studio and the CLI can call. **CONFIRMED** (`src/architecture.contract.json:12-13`, `docs/architecture-matrix.md:23`): the contract and matrix do not allow that layer to be placed freely (sections 2.1 and 3.2). **CONFIRMED** (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:49`, `:291`; `src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:31`; `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:133-135`): cancellation is complete only for the generated-host engine; the Studio has no stop mechanism at all, because it passes no token. **CONFIRMED** (`src/PSXRecompStudio/Views/MainWindow.axaml:63-78`, `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:61`): the shipped Studio cannot load a disc.

## 2. Current state survey

### 2.1 Layers, projects and allowed edges

| Project | Layer (declared) | Evidence |
|---|---|---|
| `PSXRecomp.Core` | Domain (`PSXRecomp.Core`) | **CONFIRMED** `src/architecture.contract.json:3`; `docs/architecture-matrix.md:22` (Layer Definitions) |
| `PSXRecompStudio` | Application (`PSXRecompStudio`) | **CONFIRMED** `src/architecture.contract.json:4`; `docs/architecture-matrix.md:23` |
| `PSXRecomp.Infrastructure` | Infrastructure | **CONFIRMED** `src/architecture.contract.json:5`; `docs/architecture-matrix.md:24` (Layer Definitions, Infrastructure row) |
| `PSXRecomp.Cli` | Infrastructure (per matrix) | **CONFIRMED** `docs/architecture-matrix.md:24` lists `PSXRecomp.Cli` as an Infrastructure project; `src/PSXRecomp.Cli/CliInput.cs:34` and `:149` are `[Infrastructure]` |
| `PSXRecomp.RuntimeHost` | Infrastructure by attribute; not in the matrix Projects column | **CONFIRMED** `src/PSXRecomp.RuntimeHost/Program.cs:23` and `src/PSXRecomp.RuntimeHost/UnverifiedIdentityProbe.cs:12` are `[Infrastructure]`; **CONFIRMED** the matrix Infrastructure row lists only `PSXRecomp.Infrastructure` and `PSXRecomp.Cli` (`docs/architecture-matrix.md:24`); **CONFIRMED** listed in `src/PSXRecompStudio.slnx:7`; **OBSERVED** it is packaged by the `package` command (`docs/development/headless-cli.md:153-158`, `:170-174`) |
| `PSXRecomp.GameWindow` | not declared | **CONFIRMED** listed in `src/PSXRecompStudio.slnx:5`; **OBSERVED** no `[Application]` or `[Infrastructure]` attribute under `src/PSXRecomp.GameWindow`; **CONFIRMED** (props text) AARC is not enabled for it (`Directory.Build.props:41-47`; `src/PSXRecomp.GameWindow/PSXRecomp.GameWindow.csproj:1-22` sets neither property), so AARC004 is not expected to apply; **UNVERIFIED** (build) |
| `PSXRecomp.Tests`, `PSXRecompStudio.Tests` | Test | **CONFIRMED** `src/architecture.contract.json:6`; `src/PSXRecompStudio.slnx:8-9` |
| `src/Architecture` (`PSXRecomp.Architecture` marker attributes) | attribute source, not a layer | **CONFIRMED** `src/Architecture/PSXRecompArchitectureAttributes.cs` exists; `docs/architecture-matrix.md` Architecture Attribute Contract section |
| `PSXRecomp.Native` | outside managed layers | **CONFIRMED** `docs/architecture-matrix.md` (paragraph after the Layer Definitions table) |
| `Generated` (reserved; no project) | Generated | **CONFIRMED** namespace root `PSXRecomp.Generated` (`src/architecture.contract.json:7`); the matrix lists it as planned (`docs/architecture-matrix.md`, Layer Definitions, Generated row); the contract forbids `System.Diagnostics.Process` for this layer (**CONFIRMED** `src/architecture.contract.json:49`); **OBSERVED** no such project in `src/PSXRecompStudio.slnx`. It does not change the conclusions: no Application-to-Infrastructure edge involves it. |

Edges that matter here:

- `Application -> Infrastructure` is forbidden, with a composition-root exception. **CONFIRMED** `src/architecture.contract.json:12`.
- `Infrastructure -> Application` is forbidden. **CONFIRMED** `src/architecture.contract.json:13`.
- `Domain -> Infrastructure` is forbidden. **CONFIRMED** `src/architecture.contract.json:11`.
- `Application -> Domain` is allowed (matrix dependency table). **CONFIRMED** `docs/architecture-matrix.md` (Dependency Matrix, Application to Domain row).
- The Studio references Core only. **CONFIRMED** `src/PSXRecompStudio/PSXRecompStudio.csproj:36`.
- The CLI references Core and Infrastructure. **CONFIRMED** `src/PSXRecomp.Cli/PSXRecomp.Cli.csproj:23-24`.
- The Studio's `[Application]` composition root uses the Domain interpreter directly. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:78`.

Consequence: the CLI is in the Infrastructure layer, so under the current contract it cannot depend on Application-layer use-case types (**CONFIRMED** `docs/architecture-matrix.md:24`, `src/architecture.contract.json:13`). Section 3.2 gives the placement options and their contract cost.

### 2.2 The Studio today (Application layer)

- The window has two command buttons and a frame image: "Run diagnostic title" and "Run analyzed title". **CONFIRMED** `src/PSXRecompStudio/Views/MainWindow.axaml:47-78`.
- The window has no control that selects or opens a disc. **CONFIRMED** by reading `src/PSXRecompStudio/Views/MainWindow.axaml` in full (no file-picker control or binding).
- The disc input is a settable property backed by a field. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:61` (field `_discImageBytes`, which the toolkit exposes as `DiscImageBytes`).
- Production code never assigns that property; only tests do. **CONFIRMED** by grep over `src`: assignments exist only in `src/PSXRecompStudio.Tests/GUIWorkflowTests.cs:44,60,81` and `src/PSXRecompStudio.Tests/RealRomProductionFlowTests.cs:106,121,144`. Consequence: the shipped window cannot run a real title; the action is reachable only when a test supplies the bytes. **CONFIRMED** by the two facts above.
- The diagnostic run is synchronous on the UI thread. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:68-72`. Its budget is small (outer 4, segment 64). **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:159-160`.
- The real-title action is `async` with `AllowConcurrentExecutions = false`, runs analysis and interpretation on `Task.Run`, and marshals only the final status back to the UI. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:95-113`.
- The real-title action computes a SHA-256 of the disc inside the view model. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:107-109`. The CLI computes the same identity from a file stream. **CONFIRMED** `src/PSXRecomp.Cli/CliInput.cs:106-109`.
- The real-title service is I/O-free by design and documents that disc acquisition is deferred to Issue #38. **CONFIRMED** `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:59-62`.
- The real-title service calls the Domain analysis pipeline and keeps the analyzed executable. **CONFIRMED** `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`, `:137-161`.
- Layout rejections are caught as `ArgumentException` and classified into the result. **CONFIRMED** `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:142-153`.
- The Studio runs titles only with the interpreter. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:78` and `:121`. This is the decision recorded in ADR-015. **CONFIRMED** `docs/adr/015-production-execution-engine-ownership.md:43` (Option A adopted) and `:104` (interpreter as the production engine, not generated host code).
- The Studio's execution calls do not pass a cancellation token to the orchestrator. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:92-93` and `:126-129` (the overload with `cancellationToken` is not used).
- The Studio has no stop mechanism at all. **CONFIRMED** it passes no token (`src/PSXRecompStudio/Services/TitleExecutionService.cs:92-93`, `:126-129`); the orchestrator's token parameter is declared `CancellationToken cancellationToken = default` (**CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:67`; no cancellation is requested unless a caller passes a token, BCL default-token semantics, **UNVERIFIED** in this session), so the per-segment check (`ExecutionOrchestrator.cs:133-135`) never fires for Studio runs; and the window has no Cancel control (`src/PSXRecompStudio/Views/MainWindow.axaml:47-78`). The interpreter engine has no stop support: it does not implement `ICancellableExecutionEngine` and has no `RequestCancel`, `AttachCancellation` or token parameter. **CONFIRMED** `src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:31` (class declaration, interface list: `IRecompiledExecutionEngine` only) and a grep of that file for `Cancel` returned no matches.
- The status strings are composed in the view model from state, engine name, segment count, TTY text and diagnostic code. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:74-77`, `:117-137`. These are presentation strings and the only error surface; they are unlocalized.
- The Studio composes its own copy of the guest-end handoff. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:183-192`, while Core has a public one. **CONFIRMED** `src/PSXRecomp.Core/Execution/ProgramEndHandoff.cs:16-27`. The behavior is the same, so the copy is duplication.
- The Studio contains memory-card file I/O under suppressions. **CONFIRMED** `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs:166-167`, `:176-177`, `:212-213`, `:277-280`, `:304-324`, each with `#pragma warning disable AARC003`. The class comment calls this a composition-root I/O site until Infrastructure exists. **CONFIRMED** `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs:14-22`. Production code does not use the class; only tests do. **OBSERVED** by grep (`src/PSXRecompStudio.Tests/FileMemoryCardStorageTests.cs`). The Infrastructure project now exists. **CONFIRMED** `src/PSXRecomp.Infrastructure/PSXRecomp.Infrastructure.csproj`.

### 2.3 The CLI today (Infrastructure layer)

- The CLI is documented as a "thin composition root" with no compiler, build or execution-loop semantics. **CONFIRMED** `docs/development/headless-cli.md:19-21`.
- The `recompile` command runs the whole pipeline in one method: load input, lower, codegen, artifact codegen, standalone image packing, and build. **CONFIRMED** `src/PSXRecomp.Cli/RecompileCommand.cs:32` (`Run`), `:80-83` (little-endian word packing), `:115` (`new GeneratedHostBuildService().Build(...)`).
- The `run` command builds and launches through the generated-host launcher. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:68`; `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:73-80`. The launcher is single-shot, so `run` rebuilds from input. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:20-22`.
- `run` enables mixed execution by default. An in-image, aligned PC without a compiled block, reached through a register-indirect jump, is executed by the interpreter over the same device graph and control returns to the artifact. **CONFIRMED** `docs/development/headless-cli.md:74-79`; ADR-015 amendment #693 states the same for the production statement. **CONFIRMED** `docs/adr/015-production-execution-engine-ownership.md:218`.
- The Studio runs the interpreter; the CLI `run` runs a generated host, with interpreter fallback for some indirect transfers. The headless doc says the CLI composes "exactly the same pipelines the Studio uses." **CONFIRMED** `docs/development/headless-cli.md:16-17`. This is drift (F2).
- The CLI's `run`, `screenshot` and frame-evidence code each have their own guest-end handoff class. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:302`, `src/PSXRecomp.Cli/ScreenshotCommand.cs:629`, `src/PSXRecomp.Cli/ProductionFrameEvidenceCollector.cs:121`.
- The CLI reads disc files and runs analysis itself. **CONFIRMED** `src/PSXRecomp.Cli/CliInput.cs:41` (`Load`), `:74`, `:84` (`File.ReadAllBytes`), `:106-115` (`File.OpenRead`, then `RomAnalysisPipeline.RunFromChd`).
- The CLI's error surface is an exit code plus a JSON envelope. **CONFIRMED** `docs/development/headless-cli.md:219-227`.

### 2.4 The second Avalonia window (PSXRecomp.GameWindow)

- `PSXRecomp.GameWindow` is a second Avalonia desktop app that references Infrastructure directly. **CONFIRMED** `src/PSXRecomp.GameWindow/PSXRecomp.GameWindow.csproj:20`; `src/PSXRecomp.GameWindow/MainWindow.axaml.cs:14`.
- It already has the async, cancel and UI-marshal pattern the Studio needs. It starts a run with a token (`src/PSXRecomp.GameWindow/MainWindow.axaml.cs:69`, `PackagedArtifactLiveRunner.RunAsync`), cancels through a source (`:37`, `:109`), and posts the result back to the UI thread (`:79`). **CONFIRMED**.
- Issue #851 targets this window only (section 6). **READ** issue #851 body: "対象は既存の Avalonia / C# `PSXRecomp.GameWindow` ... Studio IDE自体のAvalonia移行は対象外".

### 2.5 Cancellation and progress, by engine and entry point

| Entry point | Token accepted | Stop inside a segment | Source |
|---|---|---|---|
| `ExecutionOrchestrator.Execute` / `Resume` | yes (`CancellationToken`, defaulted) | forwards to `ICancellableExecutionEngine` if the engine implements it | **CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:61-67`, `:73-75`; `ICancellableExecutionEngine.cs:11-25` |
| `RecompiledHostExecutionEngine` | yes, via `AttachCancellation` and `RequestCancel` | yes | **CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:49`, `:291`, `:306` |
| `LiveGameRunner.RunAsync` | yes | forwards to the generated host | **CONFIRMED** `src/PSXRecomp.Infrastructure/Standalone/LiveGameRunner.cs:32` |
| `PackagedArtifactLiveRunner.RunAsync` | yes | forwards to the generated host | **CONFIRMED** `src/PSXRecomp.Infrastructure/Standalone/PackagedArtifactLiveRunner.cs:30` |
| `InterpreterTitleExecutionEngine` (Studio path) | no | no; the Studio passes no token, so no stop mechanism exists (`src/PSXRecompStudio/Services/TitleExecutionService.cs:92-93`) | **CONFIRMED** `src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:31` |
| `RecompiledArtifactLauncher.Launch` | no | no; runs build then launch | **CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:73-80` |
| `IGeneratedHostBuildService.Build` (port) | no | no; toolchain steps have a timeout only | **CONFIRMED** `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:89`; `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:42` (30 s default) |
| Analysis (`RomAnalysisPipeline`) | no | no | **CONFIRMED** `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:142` (`RunFromIsoImage`, no token parameter; the CHD overloads at `:42`, `:78` likewise take none) |

Progress: no progress-reporting type exists in Domain, Infrastructure or CLI. **OBSERVED** by grep for `IProgress` across `src` (no production matches). Available hooks are the per-segment callback (`src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:65`) and the stage recorder that reports after analysis completes (`src/PSXRecomp.Core/DiscImage/RomAnalysisOutcome.cs:69`).

### 2.6 Drift and duplication found

| # | Finding | Evidence | Label |
|---|---|---|---|
| F1 | No shared use-case layer exists; the CLI holds recompile and build orchestration, the Studio holds execution wiring. | sections 2.2, 2.3 | CONFIRMED |
| F2 | The headless doc says the CLI and Studio compose "exactly the same pipelines"; they use different engines (interpreter in the Studio, generated host with mixed fallback in `run`). | `docs/development/headless-cli.md:16-17`, `TitleExecutionService.cs:78`, `RunCommand.cs:68`, `headless-cli.md:74-79` | CONFIRMED (drift) |
| F3 | The contract's reason for `Application -> Infrastructure` ("composition-root exception") differs from the reason ADR-015 quotes ("only through the Domain interop boundary"). | `src/architecture.contract.json:12`, `docs/adr/015-production-execution-engine-ownership.md:29-31` | CONFIRMED (drift) |
| F4 | The guest-end handoff is duplicated in at least four production sites although Core has the public type. | `TitleExecutionService.cs:183`, `RunCommand.cs:302`, `ScreenshotCommand.cs:629`, `ProductionFrameEvidenceCollector.cs:121`, `ProgramEndHandoff.cs:16` | CONFIRMED |
| F5 | Disc identity (SHA-256) is computed in two front ends. | `MainWindowViewModel.cs:107-109`, `CliInput.cs:106-109` | CONFIRMED |
| F6 | The Studio performs memory-card file I/O in Application under AARC003 suppressions, though the Infrastructure project now exists and ADR-017 says it is activated for the first production adapter. | `FileMemoryCardStorage.cs:14-22,166-324`; `docs/adr/017-managed-host-io-port-adapter-boundary.md:35` (item 7) | CONFIRMED |
| F7 | The shipped Studio cannot load a disc; the real-title action is reachable only with bytes supplied by a test. | `MainWindow.axaml:63-78`, `MainWindowViewModel.cs:61`, `RealRomProductionFlowTests.cs:144` | CONFIRMED (code reading) |
| F8 | The Studio has no stop path: it passes no token (`TitleExecutionService.cs:92-93`, `:126-129`) and the window has no Cancel control (`MainWindow.axaml:47-78`). The generated host can stop; the interpreter cannot stop mid-segment; analysis, codegen, build and the launcher take no token. | section 2.5 table | CONFIRMED |
| F9 | No progress reporting exists in Domain, Infrastructure or CLI. | section 2.5 | OBSERVED (grep) |
| F10 | Studio errors are raw composed strings; the CLI uses stable codes (for example `EXECUTION_CANCELLED`) and exit codes. | `MainWindowViewModel.cs:74-77,117-137`; `ExecutionOrchestrator.cs:90`; `RomAnalysisOutcome.cs:32-33` (`FailureKind`) | CONFIRMED |
| F11 | `ARCHITECTURE.md` names `PSXRecomp.Runtime`, `PSXRecomp.Recompiler`, `PSXRecomp.Debugger` projects that do not exist in the solution, and a `mcp/` directory that is not in `src`. | `ARCHITECTURE.md:31-33` (tree) and `:360-363` (diagram); `src/PSXRecompStudio.slnx` | CONFIRMED (drift; out of scope to fix here) |
| F12 | No MCP project exists in `src`. | **OBSERVED** directory listing of `src/` | OBSERVED |

## 3. Target boundary

### 3.1 Responsibilities

| Layer / unit | Owns | Must not own |
|---|---|---|
| **GUI** (Studio views and window code) | Layout, bindings, input, presentation copies (`PresentationFrame`), localized labels, progress and cancel controls, dispatch to the UI thread | Analysis, codegen, execution, hashing, budgets, verdicts, file or process operations |
| **Use cases** (placement in 3.2) | Orchestration, request and result contracts, single-flight policy, progress and cancel plumbing, one shared result shape | Engine semantics, parsing, lowering, host I/O |
| **Domain** (`PSXRecomp.Core`) | Analysis pipeline, orchestrator, codegen, execution semantics, ports (**CONFIRMED** declarations: `IGeneratedHostBuildService` `src/PSXRecomp.Core/Recompiler/GeneratedHostBuild.cs:86`, `IProjectMetadataStore` `src/PSXRecomp.Core/Project/IProjectMetadataStore.cs:18`, `IMemoryCardStorage` `src/PSXRecomp.Core/MemoryCard/IMemoryCardStorage.cs:130`, `IRuntimeOutputSink` `src/PSXRecomp.Core/Runtime/IRuntimeOutputSink.cs:11`, `ICancellableExecutionEngine` `src/PSXRecomp.Core/Execution/ICancellableExecutionEngine.cs:11`) | Host I/O, process control, UI types |
| **Infrastructure** (`PSXRecomp.Infrastructure`) | **CONFIRMED** (`docs/architecture-matrix.md:24`) concrete adapters for filesystem and disc acquisition and process and toolchain execution; the generated-host launcher (**CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:21`); stores | Business rules; any rule not already in a Domain contract |
| **Interop** (`NativeInterop`, `PSXCoreWrapper` in Core) | **CONFIRMED** (`src/PSXRecomp.Core/NativeInterop.cs`, `src/PSXRecomp.Core/PSXCoreWrapper.cs`; `docs/architecture-matrix.md` C ABI section): C ABI boundary to `PSXRecomp.Native` | Anything above |
| **CLI** (`PSXRecomp.Cli`) | Argument parsing, exit codes, JSON envelope, stdout and stderr | Use-case orchestration (target state; F1) |
| **Tests** | Drive the GUI through simulated controls; drive use cases directly | Production dependencies on tests |

### 3.2 Where the shared use cases live (owner decision D1)

Three placements are possible. Each has a documented contract or ADR cost.

| Option | Shape | Contract and doc cost | Consequence |
|---|---|---|---|
| **A. Use cases in Domain** (`PSXRecomp.Core`) | Pure orchestration over Domain ports; Studio and CLI both call it. | **Revision required.** The matrix places "use-case orchestration" in the Application layer: **CONFIRMED** `docs/architecture-matrix.md:23` (Application row). Option A moves that responsibility to Domain, so the matrix row must be revised to say orchestration is Domain-owned and Application composes it. **CONFIRMED** `docs/architecture-matrix.md:66` ("Studio/CLI use cases") must be revised the same way. No contract edge changes. | CLI and Studio stay on edges already allowed. **CONFIRMED** existing precedent: `ExecutionOrchestrator` (`src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:61`) and `RomAnalysisPipeline` (`src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:142`) are Domain orchestration, and `RecompilerHostCodeGen` (`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:15`) is Domain code generation. |
| **B. Use cases in an Application project** (namespace root `PSXRecompStudio.*`) | Studio references it. | **Contract change required** for the CLI. **CONFIRMED** the CLI (Infrastructure) cannot reference Application today (`src/architecture.contract.json:13`). Without that change, the CLI keeps its own orchestration (F1 persists). | UI-adjacent policy stays with the GUI. |
| **C. Domain engine-provider port** (ADR-015 sketch point 2) | Domain declares a factory or selector, for example `IExecutionEngineProvider` beside `IRecompiledExecutionEngine`; Infrastructure implements it; a composition root supplies it. | **ADR revision required.** ADR-015 records this shape as still open: sketch point 2 says "no `IExecutionEngineProvider`-shaped selector exists yet" (paraphrase of `docs/adr/015-production-execution-engine-ownership.md:152-153`, where the text is **CONFIRMED**; the expected shape is at `:194-200`). It addresses engine selection, not the use-case layer, so it complements A or B. | **Gate (D2):** D2-A is adopted provisionally (section 3.3). Under that adoption the selector is not required, because the single Studio composition-root site can construct engines directly; if engine selection needs a seam, it sits at that site, not in Domain or in views. D2-B (selector as the mechanism; registration path **UNVERIFIED**) and D2-C (Host layer; classification **UNVERIFIED**) are alternatives or fallback only. Whether a selector is needed remains open under D3. Benefit: **UNVERIFIED**. |

Recommendation: D1 Option A for use-case orchestration (D1 remains open; this is a recommendation, not a settlement). The engine selector (row C) is not recommended as settled. Under the provisional D2-A it is optional and, if needed, sits at the single Studio composition-root site; D2-C (Host layer; classification **UNVERIFIED**) is fallback only (section 3.3). **Departure from issue #40:** the issue's background says the common use-case execution base is the Application layer (`Application層を共通のユースケース実行基盤とし`), and its diagram places the Application Layer between the GUI and the CLI, MCP and Tests, above Core/Domain (**READ** issue #40 body, GitHub public API, read this session). Option A instead places orchestration in Domain, outside the Application layer. **Competing decisions (D1):** Option A (Domain placement) and Option B (an Application project) are alternatives. The current contract (**CONFIRMED** `src/architecture.contract.json:13`) forbids Infrastructure (the CLI) from depending on Application. Option A works without changing that edge but departs from issue #40's background; Option B matches the background but requires changing that edge. The choice is open and belongs to the owner (D1). D1 Option A requires revising `docs/architecture-matrix.md:23` and `:66`. Row C requires completing ADR-015's sketch point 2.

### 3.3 Composition roots and the Application -> Infrastructure edge (owner decision D2)

**D2 status: adopted provisionally as D2-A on 2026-10-30** (**READ** Issue #40 owner comment, 2026-10-30, fetched in this session). Conditions, as ruled:

1. a single, narrow Studio composition-root site may construct Infrastructure adapters;
2. that site carries a locally scoped and justified `AARC002` suppression under ADR-017;
3. no general exemption for the Application layer;
4. no composition logic in views or viewmodels;
5. the architecture analyzer or a test must verify that the exception stays single-site;
6. if the single-site rule cannot be enforced cleanly, return to the owner with Host/Bootstrap (D2-C) rather than spreading suppressions.

Condition 5 does not exist yet: no analyzer rule or test for it is in the repository (**UNVERIFIED** feasibility). The existing architecture test `src/PSXRecomp.Tests/ArchitectureHostIoContractTests.cs:42` (`Application_CannotDependOnConcreteInfrastructureOutsideCompositionRootException`) checks only that the contract lists the Application-to-Infrastructure edge; it does not test the exception, any suppression, or the number of sites.

**Single-site identification (open owner item).** The ruling requires one narrow Studio composition-root site, but the repository does not yet name one, and this document does not designate one. Candidates, none of which constructs an Infrastructure adapter today: (a) `TitleExecutionService` (`src/PSXRecompStudio/Services/TitleExecutionService.cs`), which ADR-015 calls "the composition root" (`docs/adr/015-production-execution-engine-ownership.md:67-69`) and which constructs `InterpreterTitleExecutionEngine` at `:78` (`Run` over a program) and `:121` (`Run` over a PS-X EXE); (b) `RealRomTitleExecutionService` (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:47` self-describes as "The Studio's composition root"); (c) Avalonia startup, `src/PSXRecompStudio/Program.cs` (`BuildAvaloniaApp`). Two Studio classes already describe themselves as a composition root, so the term alone does not identify the site. Separately, `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:16-17` constructs both services in field initializers; whether that is composition logic under condition 4 is an owner judgement. Until the owner names one file and one construction point, condition 5's analyzer rule or test cannot be specified. The analysis below is this document's own analysis, including its fallback reasoning. The owner ruling adopts D2-A provisionally and states the conditions above; the analysis is not the owner's rationale. D2-B and D2-C are alternatives or fallback only.

Premise, corrected: the contract's `forbiddenDependencies` (**CONFIRMED** `src/architecture.contract.json:10-16`) has seven entries: Domain to Application (`:10`), Domain to Infrastructure (`:11`), Application to Infrastructure (`:12`), Infrastructure to Application (`:13`), and Domain, Application and Infrastructure each to Test (`:14-16`). The edges that bear on the Studio and the CLI are the Application-to-Infrastructure and Infrastructure-to-Application ones; there is no Infrastructure-to-Infrastructure entry. So under the current contract, a composition-root suppression is needed only where an Application-classified project references Infrastructure. The only such project is the Studio (namespace root `PSXRecompStudio`, **CONFIRMED** `src/architecture.contract.json:4`), and it references Core only (**CONFIRMED** `src/PSXRecompStudio/PSXRecompStudio.csproj:36`). The CLI (**CONFIRMED** `src/PSXRecomp.Cli/PSXRecomp.Cli.csproj:24` references Infrastructure; namespace `PSXRecomp.Infrastructure.Cli`, **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:12`) and PSXRecomp.RuntimeHost (**CONFIRMED** `src/PSXRecomp.RuntimeHost/PSXRecomp.RuntimeHost.csproj:25`) are Infrastructure-classified and need no suppression.

- The contract's entry for `Application -> Infrastructure` (**CONFIRMED** `src/architecture.contract.json:12`) gives "composition-root exception" as its reason; the exception itself is a per-site `AARC002` suppression (matrix:47, below).
- ADR-017 describes the exception as a single site: "Concrete adapter construction is limited to one explicit composition-root site with a narrowly scoped `AARC002` suppression and rationale until evidence justifies a dedicated Host/Bootstrap layer." **CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:31`. The matrix uses the same singular wording: "one **composition-root site** may use a narrowly scoped `AARC002` suppression" (`docs/architecture-matrix.md:47`), and "Keep composition-root adapter construction isolated" (`docs/architecture-matrix.md:210`).
- The matrix criteria for a Host/Bootstrap layer, quoted exactly: "repeated `AARC002` exceptions" (**CONFIRMED** `docs/architecture-matrix.md:210`) and "spreads beyond one obvious bootstrap site" (**CONFIRMED** `docs/architecture-matrix.md:47`). ADR-017 describes the same concern as "starts spreading" (**CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:64`). Whether any state meets these criteria is an **owner judgement**, not a settled fact; the provisional D2 adoption does not decide it.
- **Current state (CONFIRMED):** `git grep AARC002 2e3ecad -- src` returns no match (run at the base; the same grep at `60da892` also returns no match), so no composition-root suppression exists in the repository. The Studio is the only Application-classified executable, so it is the only project whose future composition root would need one. The singular wording of ADR-017:31 and matrix:47 fits one such site without revision; whether a second Application-classified site meets the matrix criteria quoted above is an **owner judgement**, not authorized by the provisional D2 adoption.
- **GameWindow (AARC not enabled, CONFIRMED):** its namespace `PSXRecomp.GameWindow` (**CONFIRMED** `src/PSXRecomp.GameWindow/MainWindow.axaml.cs:16`) matches no contract root (**CONFIRMED** `src/architecture.contract.json:3-7`). `Directory.Build.props` defaults `EnableArchitectureAnalyzer` to `CompileArchitectureAttributes` (**CONFIRMED** `Directory.Build.props:41-42`), and adds the analyzer package and contract only when it is `'true'` (**CONFIRMED** `Directory.Build.props:44-47`). `src/PSXRecomp.GameWindow/PSXRecomp.GameWindow.csproj:1-22` sets neither property (**CONFIRMED**), so AARC does not apply to GameWindow in this repository; its build behaviour is **UNVERIFIED** (no build run). By contrast, `src/PSXRecomp.Cli/PSXRecomp.Cli.csproj:19` and `src/PSXRecomp.RuntimeHost/PSXRecomp.RuntimeHost.csproj:20` set `EnableArchitectureAnalyzer=true` (**CONFIRMED**), so AARC applies to the CLI and RuntimeHost.

Options for D2:

- **D2-A (Studio composition root), adopted provisionally 2026-10-30:** the Studio's own composition code is the single Application-to-Infrastructure site, with a narrow `AARC002` suppression under ADR-017. The suppression does not exist in `src` (grep at the document HEAD). **CONFIRMED** ADR-017:31 describes one such site, so one site needs no ADR revision. **UNVERIFIED:** whether a pragma-scoped suppression works at the eventual site without spreading (startup candidates `src/PSXRecompStudio/Program.cs` and `src/PSXRecompStudio/App.axaml.cs` are not designations); and whether the single-site verification (condition 5) can be built. Gate: the selector (3.2 row C) is not required under this option, because the suppressed site can construct engines directly. Trade-off: whichever site is named owns adapter choice; the site is not yet identified (above). Whether a second Application-classified site meets the matrix criteria is an owner judgement and is not authorized.
- **D2-B (Domain-owned factory, no Application reference to Infrastructure), not adopted:** the Studio references only Core and builds adapters through a Domain factory or provider, which is the ADR-015 sketch point 2 selector (section 3.2 row C). Alternative only. Trade-off: no suppression, but some Infrastructure-classified code must register adapters at process start, and the Studio cannot call that registration without a reference. **UNVERIFIED:** whether a registration path exists that avoids the reference.
- **D2-C (Host/Bootstrap layer), not adopted; fallback only:** a dedicated composition layer, to be proposed if the single-site rule cannot be enforced cleanly (condition 6). The matrix states that a dedicated layer "requires a separate architecture change" (**CONFIRMED** `docs/architecture-matrix.md:47`). Its contract classification is **UNVERIFIED**.

### 3.4 Flow: Analysis -> Recomp -> Build -> Run

| Stage | Today: owner and entry | Shared use case (target) | Adapter needed | GUI state | Cancellation today and needed |
|---|---|---|---|---|---|
| Input / disc load | **CONFIRMED** Studio: none (F7). CLI: `CliInput.Load` (`CliInput.cs:41`) reads files at `:74,84,106` | `LoadDisc` returns identity (SHA-256) and format | Disc reader (Infrastructure: path to bytes or stream) | Choose, Loading, Loaded (hash, size) | Needed: token checked before read |
| Analysis | **CONFIRMED** Studio: `RealRomTitleExecutionService.cs:97`. CLI: `CliInput.cs:115` | `AnalyzeDisc` returns `RomAnalysisOutcome` | None beyond bytes or stream | Running (stage), Pass, Fail with `FailureKind` | Needed: no token today (section 2.5); Domain change |
| Recomp (codegen) | **CONFIRMED** CLI only: `RecompileCommand.cs:32-63`, `RecompilerHostCodeGen.Generate` | `RecompileTitle` returns generated source and state | None | Running, Done | Needed: token before codegen |
| Build | **CONFIRMED** CLI only: `RecompileCommand.cs:115` (`GeneratedHostBuildService.Build`) | `BuildArtifact` through `IGeneratedHostBuildService` | Toolchain (Infrastructure) | Building (compile, link), Built, ToolingError | **CONFIRMED** Today: timeout only (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:42`); the port takes no token (`GeneratedHostBuild.cs:89`). Needed: a token that kills the toolchain (D4) |
| Run | **CONFIRMED** Studio: interpreter, `TitleExecutionService.cs:92-93` (no token). CLI: generated host with mixed fallback, `RunCommand.cs:68` | `RunTitle` with engine selection (D3) | Launcher port (no port today; **CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:21` is the concrete class declaration, `:73-80` the `Launch` method) | Running (segment count), Success, Blocked, Failed, Cancelled | Generated host: stop supported (section 2.5). Studio interpreter: no stop mechanism (no token passed, `TitleExecutionService.cs:92-93`); needed: a token path and interpreter stop support (D4) |

Notes:

- `Launch` builds and then runs as one operation. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:20-22` (single-shot; rebuilds from input). Exposing build and run as separate GUI steps requires a launcher port (D5).
- Analysis results are the input the GUI must show for review (**READ** issue #40 body via GitHub API, "Analysis Result Viewer"). The result type carries stage status and `FailureKind`. **CONFIRMED** `src/PSXRecomp.Core/DiscImage/RomAnalysisOutcome.cs:16`, `:30-33`.

### 3.5 Async, progress and cancellation

**Async.** Each use case is a `Task<TResult>` with a `CancellationToken` parameter, and CPU-bound work runs off the UI thread. **CONFIRMED** The Studio already does this for the real-title action (`src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:105`), and the GameWindow does it for runs (`src/PSXRecomp.GameWindow/MainWindow.axaml.cs:69`).

**Single-flight.** A second concurrent start of the same use case is rejected, as `AllowConcurrentExecutions = false` does today (**CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:95`). The rule belongs in the use-case or command layer, not in views.

**Progress.** Use-case progress is a small Domain-owned value (stage name, step index, step total, optional message) delivered through `IProgress<T>` (BCL). The Studio would marshal it to the UI thread with the Avalonia dispatcher, as the GameWindow does (**CONFIRMED** `src/PSXRecomp.GameWindow/MainWindow.axaml.cs:79`; **OBSERVED** the Studio has no progress path today, and `src` contains no `IProgress` use outside tests). Granularity:

- Analysis: per stage. The outcome carrying stage results is built by `RomAnalysisOutcome.From` (**CONFIRMED** `src/PSXRecomp.Core/DiscImage/RomAnalysisOutcome.cs:68`, `Stages = recorder.Results` at `:83`), and the pipeline calls it at its return points (**CONFIRMED** `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:54`, `:59`, `:90`, `:97`). So a caller sees stage results when the pipeline returns. Live per-stage delivery would need a new hook (PREDICTED).
- Run: per segment, via the orchestrator callback (**CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:65`).
- Build: toolchain steps (compile, link) are not reported (**OBSERVED**: `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs` has no progress hook). Infrastructure change (PREDICTED).

**Cancellation.** One token per user-initiated operation; the Cancel button cancels it; each stage checks it between units of work.

- Pre-cancelled runs end as `Cancelled` without loading. **CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:73-75`.
- A running generated-host segment stops through `RequestCancel`. **CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:291`, `:306` (`AttachCancellation`).
- Studio runs have no stop mechanism: no token is passed (`src/PSXRecompStudio/Services/TitleExecutionService.cs:92-93`, `:126-129`), the orchestrator's token parameter is declared `CancellationToken cancellationToken = default` (**CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:67`), and the window has no Cancel control (`src/PSXRecompStudio/Views/MainWindow.axaml:47-78`). **CONFIRMED.** Once a token is passed, a non-cancellable engine such as the interpreter is checked only before each segment (`ExecutionOrchestrator.cs:133-135`) and does not stop mid-segment. A cancellable engine is registered for `RequestCancel` (**CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:79-80`), which is how the generated host stops mid-segment (**CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs:291`, `:306`). The interpreter's segment is bounded by the segment budget. The real-title path uses 4096 steps per segment (`src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:71`, `DefaultSegmentBudget`; outer budget 64 at `:68`). The built-in diagnostic path uses 64 steps per segment and 4 segments (`src/PSXRecompStudio/Services/TitleExecutionService.cs:159-160`). The latency estimate uses the real-title figure only, so the worst-case latency would be one 4096-step segment. **PREDICTED** from these budgets; not measured.
- A cancelled operation must end as a classified `Cancelled` outcome, not as an exception and not as a hung spinner.

### 3.6 Error display

**Result shape.** Every use case returns one shared result type:

- `Outcome`: `Succeeded`, `Blocked` (explicit unsupported boundary), `Failed`, `Cancelled`, `ToolingError`, `InvalidInput`. The CLI exit-code mapping (0, 2, 1) lives only in the CLI. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:29-30` (Success 0, Blocked 2, Failure and tooling 1).
- `Code`: a stable machine string. **CONFIRMED** examples: `EXECUTION_CANCELLED` (`src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:90`) and `FailureKind` (`src/PSXRecomp.Core/DiscImage/RomAnalysisOutcome.cs:32-33`).
- `MessageKey` and arguments: a localization key (issue #21). English text is a fallback until #21 lands.
- `Details`: optional path to a diagnostic report, reusing the existing bundle contract (**CONFIRMED** `docs/development/headless-cli.md:328`, Diagnostic report bundle section).

**GUI rule.** The GUI renders `Outcome`, `Code`, the localized message, and a details disclosure. It never parses CLI text or stderr. This follows the issue's requirement that the GUI not depend on CLI-specific internals or text logs (**READ** issue #40 body, GitHub public API, read this session: GUIはCLI固有の内部実装やテキストログの解析に依存しない).

**Migration.** Today's composed strings (**CONFIRMED** `MainWindowViewModel.cs:74-77`, `:117-137`) become one `Outcome`, a `Code`, and formatted detail. The text mapping moves to a pure presentation mapper.

### 3.7 The "GUI adds no business logic" rule

**The GUI may:**

- bind, lay out, select, and show progress and state;
- convert a Domain result to a presentation model with a pure, lossless transform, as `PresentationFrame.From` does (**CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:72`, `:111`; `docs/architecture/gui-ux.md:110-113`);
- localize labels and format numbers and dates;
- choose what to show first, what to collapse, and how to order rows.

**The GUI must not:**

- compute hashes, identities, checksums or file formats. Current violation (**CONFIRMED** `MainWindowViewModel.cs:107-109`, F5); move to the use case;
- choose or change execution budgets, engines or fallbacks. Defaults live in `RealRomTitleExecutionService.cs:68-71` (**CONFIRMED**), and the CLI has its own constant (**CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:41`, `DefaultSegmentBudget = 1_000_000u`), so the defaults are not shared;
- classify outcomes or decide pass or fail from raw fields. The view model maps states to text (**CONFIRMED** `MainWindowViewModel.cs:117-137`); that is presentation only and must not grow rules;
- read or write files, start processes, or touch Infrastructure types. **CONFIRMED** (code reading of `MainWindowViewModel.cs`) the view model currently has none of these; the memory-card store in Application is the exception (F6);
- parse CLI output or log text;
- construct Infrastructure adapters or hold composition logic (D2 ruling: only the single Studio composition-root site may construct them).

**Enforcement.** Proposed, not built:

1. A review checklist item for GUI changes (process only).
2. Required by the D2 ruling, not built: an architecture analyzer rule or test that fails if the Application-to-Infrastructure exception is not single-site, that is, if Studio code outside the composition-root site references Infrastructure or carries an `AARC002` suppression. It does not exist yet; feasibility **UNVERIFIED**. It cannot be specified until the site is named (3.3). If it cannot be enforced cleanly, the owner is returned to D2-C (section 3.3, condition 6).
3. The existing AARC rules, which the contract applies to Application: `System.Console`, `System.IO.File` and `System.IO.Directory`. **CONFIRMED** `src/architecture.contract.json:31-33`.
4. Proposed new AARC003 rule for Application: `System.Diagnostics.Process`. **PREDICTED** only: the contract does not forbid `Process` in Application today (no Application entry for `Process` in `src/architecture.contract.json:31-33`), and the matrix leaves the Application cell blank for `Process.*` (`docs/architecture-matrix.md:101`). Adding it is an owner decision and a contract change (D10). The rule text in section 3.7 describes the intent, not the current contract.

### 3.8 CLI, MCP and Tests

- **CLI.** Target: a composition root and presentation layer only. Its `recompile`, `run` and frame-evidence paths call the same use cases as the Studio, and its duplicate handoffs (F4) are replaced by `PSXRecomp.Core.Execution.ProgramEndHandoff` (**CONFIRMED** `src/PSXRecomp.Core/Execution/ProgramEndHandoff.cs:16`). The headless doc's parity sentence (F2) becomes accurate only after the engine decision (D3).
- **GUI-to-CLI process.** **READ** (**READ** issue #40 body, GitHub public API, read this session): the GUI may launch the CLI process as an implementation option; the issue does not require it. Recommendation: in-process use cases (D6).
- **MCP.** No MCP project exists (F12). MCP should be a third thin adapter over the same use cases, designed in a separate issue.
- **Tests.** GUI tests keep driving real controls (**CONFIRMED** `src/PSXRecompStudio.Tests/GUIWorkflowTests.cs:14-15` states that the Project, Analysis, Recompile and Approval screens do not exist yet). Use-case tests run without Avalonia. The guard test is `src/PSXRecomp.Tests/HeadlessAvaloniaIndependenceTests.cs:10` (**CONFIRMED**); the rule it guards is stated in `docs/architecture/gui-ux.md:115` (**CONFIRMED**).

### 3.9 Headless independence

**CONFIRMED** (rule stated in `docs/architecture/gui-ux.md:115`): the Runtime and the headless CLI must not reference Avalonia, and a test guards this. Option A (3.2) must keep that property: no Avalonia type in Core use cases.

### 3.10 GUI and the analysis cache (#43)

Owner ruling (**READ** Issue #40 owner comment, 2026-10-30, fetched in this session). It is recorded here, not decided by this document:

- The GUI may use the same cache service and results as the CLI, through shared use cases. There is no GUI-only cache, no GUI-only key derivation, and no alternate correctness policy.
- Cache hits are disabled for every image-dependent stage until #43 Rule C passes. The ruling names the U2, V-ID, R-G1 same-buffer consumption and T-IDENT tests as part of that gate. The Rule C conditions are defined in #43 and are not restated here (**UNVERIFIED** in this document; #43 was not read in this session).
- The GUI must display uncached or reanalysis status for those stages and must never manufacture a hit.
- If the shared cache store is an Infrastructure adapter, its construction falls under the same single-site rule as 3.3. That site is not yet named, so where the store is constructed is an open item.

## 4. Recommended approach

Incremental, each phase separately reviewable.

| Phase | Change | Behavior change | Gate |
|---|---|---|---|
| 0 | This design; owner decisions D1-D11 recorded in ADR revisions (ADR-015 sketch point 2; ADR-017 needs no wording change for the one site of provisional D2-A) and the matrix revisions in 3.2 | None | Owner sign-off |
| 1 | Replace duplicate guest-end handoffs (F4) with `ProgramEndHandoff` | None expected | Existing tests |
| 2 | Add the shared result type, tokens, and `IProgress` plumbing to Core use cases (D1 Option A); add stop support to `InterpreterTitleExecutionEngine` (`ICancellableExecutionEngine`, `AttachCancellation`, `RequestCancel`, modeled on `RecompiledHostExecutionEngine.cs:291-306` (**CONFIRMED** `:291` `RequestCancel`, `:306` `AttachCancellation`)); move the CLI's recompile orchestration into use cases | None for CLI output; the Studio gains mid-segment stop only if D4 Option A is chosen | CLI JSON and exit-code tests unchanged; new interpreter stop tests |
| 2b | Token wiring: pass a `CancellationToken` from the Studio's two execution entry points into `ExecutionOrchestrator.Execute` (**CONFIRMED** the current calls pass no token at `src/PSXRecompStudio/Services/TitleExecutionService.cs:92-93` and `:126-129`; the parameter is at `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:61-67`). Precondition for Phase 3's Cancel UI. | No change until a token is cancelled (default-token semantics, **UNVERIFIED** in this session) | Test: a cancelled token ends the run as `Cancelled` (`ExecutionOrchestrator.cs:73-75`) |
| 3 | Studio: disc load through a Domain port with an Infrastructure reader (F7); Cancel and progress UI (precondition: Phase 2b); shared error panel | New UI; the first Studio construction of an Infrastructure adapter, at the single composition-root site (D2-A; not yet named, 3.3) | GUI tests drive real controls; single-site verification (D2 condition 5) in place before that construction |
| 4 | Move `FileMemoryCardStorage` to Infrastructure (F6); Studio keeps only the port | None | Existing memory-card tests |
| 5 | Engine selection (D3): if a selector is needed, the ADR-015 sketch point 2 selector (3.2 row C) sits at the single Studio composition-root site under provisional D2-A (not required under that option; section 3.3). D2-B (**UNVERIFIED** registration path) and D2-C (Host layer, fallback only) apply only if D2 is revisited; generated-host run as an explicit, labelled second action | New UI and composition | ADR amendment (D3 still open) |
| 6 | MCP adapter over the same use cases | New surface | Separate issue |

Why this order: Phase 1 removes duplication without risk. Phase 2 creates the shared surface and the interpreter stop support that the Studio's cancel requires. Phase 3 is the first user-visible change and depends on that surface. Phases 4 to 6 depend on owner decisions.

## 5. Alignment with ADR-015 and ADR-017

- ADR-015's decision (interpreter as the Studio's production engine, in Domain) is preserved. **CONFIRMED** `docs/adr/015-production-execution-engine-ownership.md:43`, `:67-70`.
- The rule that Application does not acquire host resources (**CONFIRMED** `docs/architecture-matrix.md:66`, item 3) is violated by F6 and motivates Phase 4. The composition-root sentence is at `docs/adr/017-managed-host-io-port-adapter-boundary.md:31` (**CONFIRMED**).
- The contract reason text (F3) should be corrected in the same ADR revision that records D1 and D2, so that rationale and contract agree. The contract itself is not changed by this document.
- ADR-017's singular "one composition-root site" wording (**CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:31`) fits the provisional D2-A for one Studio site; it needs revision only if a second Application-classified site is wanted (3.3), which the ruling does not authorize.

## 6. Coordination with W17 (Rust/egui PoC, issue #851)

Issue #851 is scoped to the GameWindow. **READ** issue #851 body: target is `PSXRecomp.GameWindow` ("Standalone実行時のゲーム表示用GUIに限り"); "Studio IDE自体のAvalonia移行は対象外"; the issue does not approve replacing the default host ("既定ホストの置換 ... を承認するものではない").

Consequences for this design:

- The Studio IDE stays on Avalonia (**READ** issue #851 body: IDE migration is out of scope; **READ** Issue #40 owner comment, 2026-10-30, confirms it). The Issue #40 ruling also keeps #851 Rust/egui+wgpu as a post-v0.1 isolated GameWindow research/PoC, not a default migration. The egui comparison does not affect the use-case boundary in section 3, the Studio's disc and analysis flow, or the Studio's error model.
- The egui PoC is in Rust (**READ** issue #851 body, GitHub public API: Rust + egui + wgpu). **UNVERIFIED** that no direct call path exists; this design assumes the C# Domain use cases cannot be called directly and need IPC or FFI, as #851 items 1-2 describe (same-process FFI or local IPC with frame transport). That is a runtime-boundary decision, not a use-case decision.
- The GameWindow path that #851 would replace uses `PackagedArtifactLiveRunner` (**CONFIRMED** `src/PSXRecomp.GameWindow/MainWindow.axaml.cs:69`; cancel at `:109`) and the run, stop and timeout contracts (**UNVERIFIED** as described: **READ** issue #851 body cites issues #827 and #829 for them, and those contracts were not read in this session). Any PoC must keep those contracts, which the Domain stop semantics in section 3.5 also depend on.
- The use-case boundary in section 3 is toolkit-neutral: no Avalonia type in Core use cases. This is what keeps a future non-Avalonia host possible without rework.
- Owner decision D9 covers the GameWindow's future; it is the only place #851 bears on this design.

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Use-case code drifts back into the view model | PREDICTED high without a checklist | 3.7 checklist; Phase 3 tests drive use cases directly |
| Core use cases pick up host I/O or UI types | PREDICTED medium | AARC003 already forbids host APIs in Domain (**CONFIRMED** `src/architecture.contract.json:19-30`); add a no-Avalonia rule test (PREDICTED) |
| Composition-root suppressions spread across executables | PREDICTED medium | Provisional D2-A: single-site analyzer/test (required; not yet built); fallback to D2-C if it cannot be enforced cleanly |
| Cancellation gaps leave work running after Cancel | CONFIRMED gap (F8) | Phase 2 interpreter stop; toolchain kill is the hardest item and may stay timeout-only until D4 |
| Studio and CLI run different engines, and results are compared as if equal | CONFIRMED today (F2) | D3; label the engine in every result |
| Localization retrofit cost | PREDICTED medium | Message keys from Phase 2 even before #21 translations exist |
| Disc acquisition blocks the UI on large images | PREDICTED medium | Run disc load and analysis off the UI thread (3.5) with per-stage progress |

## 8. Out of scope

- Implementing any GUI screen, use case, port or adapter.
- The Project, Compatibility Registry, AI review and Approval screens listed in issue #40 (**READ** issue #40 body, GitHub public API; `src/PSXRecompStudio.Tests/GUIWorkflowTests.cs:14-15` states the Project, Analysis, Recompile and Approval screens do not exist yet). Each needs its own design.
- The MCP surface.
- Changing `src/architecture.contract.json`, `ARCHITECTURE.md`, `docs/architecture-matrix.md`, or any ADR. Revisions are listed as owner decisions.
- The Studio IDE's UI toolkit. Issue #851 excludes it, and this document does not reopen it.
- Naming the single Studio composition-root site (open owner item, 3.3). No file is designated by this document.

## 9. Owner decisions

Each decision lists options and a recommendation. The owner decides; this document does not.

**D1. Where the shared use cases live.**
- Option A (recommended; competes with Option B): Domain (`PSXRecomp.Core`). **This departs from issue #40's background**, which names the Application layer as the common use-case execution base (`Application層を共通のユースケース実行基盤とし`; **READ** via GitHub API, this session). **Revision required:** **CONFIRMED** `docs/architecture-matrix.md:23` (Application row says "use-case orchestration") and `:66` (Studio/CLI use cases) must say orchestration is Domain-owned and Application composes it.
- Option B: a new Application project (competes with Option A). This matches issue #40's background (`Application層を共通のユースケース実行基盤とし`). **CONFIRMED** the current contract forbids the edge that would let the CLI reference it (`src/architecture.contract.json:13`), so Option B requires changing that edge. If the edge is not changed, the CLI keeps duplicate orchestration.
- Engine-selection placement follows the provisional D2-A, not D1: if a selector is needed, it sits at the single Studio composition-root site (3.2 row C; 3.3). Its benefit is **UNVERIFIED**. D1 itself remains open.

**D2. Composition root and Application -> Infrastructure wiring.** **Status: adopted provisionally as D2-A on 2026-10-30** (**READ** Issue #40 owner comment; conditions in 3.3).
- D2-A (adopted provisionally): one narrow Studio composition-root site (not yet named; see 3.3) may construct Infrastructure adapters, with a locally scoped and justified `AARC002` suppression under ADR-017. No general exemption; no composition logic in views or viewmodels. The analyzer or a test must verify that the exception stays single-site; this verification does not exist yet (feasibility **UNVERIFIED**). If the single-site rule cannot be enforced cleanly, return to the owner with D2-C rather than spreading suppressions. **CONFIRMED** ADR-017:31 describes one site, so one site needs no ADR revision. A second Application-classified site would need the ADR-017 and matrix wording revised (**CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:31`, `docs/architecture-matrix.md:47`, `:210`); it is not authorized.
- D2-B (Domain-owned factory): not adopted; alternative only (3.3). Registration path **UNVERIFIED**.
- D2-C (Host/Bootstrap layer): not adopted; fallback only, if the single-site rule cannot be enforced cleanly. Requires a separate architecture change (**CONFIRMED** matrix `:47`).

**D3. Which engine the Studio's Run uses, and the CLI's mixed fallback.**
- Option A (recommended): the Studio keeps the interpreter as its default (ADR-015). Add the generated-host run as an explicit, labelled second action (Phase 5). Any engine selector sits at the single Studio composition-root site under the provisional D2-A (3.2 row C; 3.3; benefit **UNVERIFIED**). D3 itself remains open.
- Option B: switch the Studio to the generated host now. Requires an ADR-015 amendment; any composition it needs must stay inside the D2 single-site rule.
- Either way: the CLI `run` is generated-host by default with mixed fallback enabled (**CONFIRMED** `docs/development/headless-cli.md:74-79`), so the Studio and `run` do not execute the same way for indirect transfers. Correct the headless doc's parity claim (F2) and state the engine in every result.

**D4. Stop support for the interpreter and for build.**
- Option A (recommended): implement `ICancellableExecutionEngine` on `InterpreterTitleExecutionEngine` (Phase 2) so a running segment can stop, and accept that the one-segment stop-latency bound applies only once a token is wired (3.5, PREDICTED). Until a token is wired, the Studio has no stop mechanism at all (section 2.2). For the toolchain, add a token that kills the process, with the timeout kept as the backstop (**CONFIRMED** `src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs:42`).
- Option B: no stop for Studio interpreter runs (as today, since no token is passed), timeout-only for build, and show "cannot cancel" for those operations in the GUI.

**D5. Split of Build and Run in the generated-host path.**
- Option A (recommended): a Domain launcher port with separate build and run steps, matching the GUI's stages. Today's `Launch` is one operation (**CONFIRMED** `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:73-80`).
- Option B: keep `Launch` as one "Build and run" action.

**D6. GUI-to-CLI process launch.**
- Option A (recommended): no; in-process use cases only. Process launch only for toolchain isolation if D4 cannot be met in-process.
- Option B: the GUI launches the CLI with JSON output for long jobs. This keeps the CLI as the single pipeline but adds an output-parsing contract.

**D7. Disc acquisition and file access in the Studio.**
- Option A (recommended): a Domain port for disc input with an Infrastructure reader; the GUI's file picker passes a path only. The reader is constructed only at the single Studio composition-root site (D2). Move `FileMemoryCardStorage` to Infrastructure in Phase 4 (F6).
- Option B: keep disc bytes supplied by the caller. The shipped Studio stays unable to load a disc (F7).

**D8. Error codes and localization timing.**
- Option A (recommended): message keys and stable `Code` values from Phase 2, before #21 translations exist.
- Option B: English text only until #21 lands; retrofit later.

**D9. GameWindow's future and the #851 outcome.**
- **CONFIRMED** the GameWindow is a separate Avalonia app that references Infrastructure directly (`src/PSXRecomp.GameWindow/PSXRecomp.GameWindow.csproj:20`) and is listed separately in the solution (`src/PSXRecompStudio.slnx:5`). **OBSERVED** it declares no layer attribute; **CONFIRMED** (props text) the analyzer is not enabled for it (`Directory.Build.props:41-47`; build behaviour **UNVERIFIED**, section 3.3).
- Owner decides whether it stays a separate host, is folded into the Studio, or is replaced after #851. Per #851, replacing the default host is not approved by that issue (**READ** issue #851 body, GitHub public API: 既定ホストの置換 ... を承認するものではない); that needs its own decision.
- Owner ruling (**READ** Issue #40 owner comment, 2026-10-30): #851 Rust/egui+wgpu remains a post-v0.1 isolated GameWindow research/PoC, not a default migration. D9 otherwise stays open.

**D10. Application-layer `Process` prohibition.**
- Option A (recommended): propose adding `System.Diagnostics.Process` as an Application `forbiddenApis` entry in the contract, with a matrix row. **PREDICTED:** this would produce AARC003 errors for any current Application code that uses `Process`; none was checked (UNVERIFIED).
- Option B: leave `Process` unforbidden in Application and rely on review and section 3.7.

**D11. Session and trace storage.** Issue #40 requires that important operations leave reproducible Session and Trace records (**READ** issue #40 body, GitHub public API, read this session: すべての重要操作で再現可能なSession/Trace情報を残せるようにする). Where they are stored is not designed here; the owner should assign it.

**Open items not settled by the 2026-10-30 ruling.** D1, D3, D4, D5, D6, D7, D8, D9 (other than the #851 note above), D10 and D11 remain open. The ruling's GUI/cache rule (3.10) is recorded, but the cache design itself is #43's.

## 10. Verification performed for this document

- Read-only survey of the files cited above at base `2e3ecad` (`src/` unchanged to HEAD; see the header). Labels CONFIRMED mean the cited line was read in this session.
- No build, test or analyzer run. **UNVERIFIED:** that the contract, the AARC analyzer, or existing GUI tests pass at this base; feasibility of the proposed rule tests; the analyzer behaviour for `PSXRecomp.GameWindow`, which is not enabled (section 3.3; build behaviour **UNVERIFIED**) (**CONFIRMED** `PSXRecomp.RuntimeHost` is attributed `[Infrastructure]` at `src/PSXRecomp.RuntimeHost/Program.cs:23` and `src/PSXRecomp.RuntimeHost/UnverifiedIdentityProbe.cs:12`; it is not in the matrix Projects column).
- No BIOS, ROM, disc or copyrighted content was read or included. Paths are repository-relative.
- Re-read at HEAD `fd4b738` for the D2 fix: `TitleExecutionService.cs:78,121,144,159-160`; `RealRomTitleExecutionService.cs:47,68,71`; `MainWindowViewModel.cs:16-17`; `docs/adr/015-production-execution-engine-ownership.md:67-69`; `src/PSXRecomp.Tests/ArchitectureHostIoContractTests.cs:42`; `Program.cs` and `App.axaml.cs` exist (candidates only). No build or test run.

## 11. Revision note and traceability

**Revision (2026-10-30, D2 ruling).** D2-A is adopted provisionally (**READ** Issue #40 owner comment, 2026-10-30). Sections 3.2, 3.3, 3.5, 3.7, 3.10 (new), 4, 5, 6, 7, 8, 9 and 10 (D1, D2, D3, D7, D9, open items) are updated for consistency. The single Studio composition-root site is not yet named and is an open owner item (3.3). Citations touched in this revision were re-read at the document HEAD (`git show HEAD:<path>`). `git diff --stat origin/batch2/w16-gui-architecture-design -- src` is empty, and `git grep -n AARC002 HEAD -- src` has no match: no suppression exists yet. Nothing is built or run. The single-site analyzer/test is required but does not exist (**UNVERIFIED**).

| Ruling element (Issue #40 comment, 2026-10-30) | Where reflected | Status |
|---|---|---|
| D2-A provisional: single narrow Studio composition-root site; locally scoped, justified `AARC002` suppression under ADR-017; no general exemption; no composition logic in views or viewmodels; analyzer/test verifies single-site; fallback to D2-C | 3.2 row C, 3.3, 3.7, 4 (Phases 3, 5), 7, 9 (D2, D3, D7) | Provisional; verification not built |
| GUI and #43 cache: same cache service and results as CLI via shared use cases; no GUI-only cache, key derivation or correctness policy; hits disabled for image-dependent stages until #43 Rule C; GUI shows uncached or reanalysis status | 3.10 | Ruling recorded; #43 Rule C not restated (**UNVERIFIED** here) |
| #851 Rust/egui+wgpu: post-v0.1 isolated GameWindow research/PoC, not a default migration; Studio IDE stays on Avalonia | 6, 9 (D9) | Ruling recorded |
| Other decisions (D1, D3-D11) | 9 | Open; not settled by this revision |
