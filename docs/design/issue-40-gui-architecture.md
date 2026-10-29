# Issue #40: Studio GUI Architecture and Application Boundary (Design)

**Status:** Proposed (design only; no implementation in this change)

**Related Issues:** #40 (this design), #38 (host I/O ports and adapters), #75 (GUI/UX SSOT), #21 (localization), #851 (egui PoC, owned by W17)

**Base:** `2e3ecad` on `main`

**Related SSOTs:** [`docs/architecture/gui-ux.md`](../architecture/gui-ux.md), [`docs/architecture-matrix.md`](../architecture-matrix.md), [`src/architecture.contract.json`](../../src/architecture.contract.json), [`docs/adr/015-production-execution-engine-ownership.md`](../adr/015-production-execution-engine-ownership.md), [`docs/adr/017-managed-host-io-port-adapter-boundary.md`](../adr/017-managed-host-io-port-adapter-boundary.md), [`docs/development/headless-cli.md`](../development/headless-cli.md)

## 0. How to read this document

Every factual claim carries one label and a `path:line` reference (paths are relative to the repository root):

| Label | Meaning |
|---|---|
| **CONFIRMED** | Read directly in the source, contract or document at the cited line in this base commit. |
| **OBSERVED** | Seen in a directory listing, a grep result or a test file, but not traced through execution. |
| **PREDICTED** | A consequence expected from the design, not yet built or measured. |
| **UNVERIFIED** | Not checked in this session. No build, test or analyzer run was performed for this document. |

Design statements (recommendations) are not labelled; they are proposals for the owners in section 9.

Issue #40 asks for a Studio GUI design, not an implementation. Nothing in this change alters source, the architecture contract, or existing docs.

## 1. Problem statement

Issue #40 requires that the GUI, CLI and MCP share Application-layer use cases, that the GUI adds no business logic, and that the Analysis -> Recomp -> Build -> Run flow has async work, progress, cancellation and error display. The question this document answers is what the current code can already support, where the boundary is broken, and what the smallest coherent next step is.

Key finding in one paragraph: the Domain layer already owns most of the pipeline logic (analysis, execution orchestration, code generation, a build port), and the Studio already calls some of it. What is missing is a shared use-case layer that both the Studio and the CLI can call. The architecture contract forbids the CLI's layer (Infrastructure) from depending on the Studio's layer (Application), so the shared layer cannot simply be "the Application project" without an owner decision. The CLI also currently holds use-case orchestration itself. Cancellation and progress are only partly available, and the Studio offers no way to load a disc.

## 2. Current state survey

### 2.1 Layers, projects and allowed edges

| Project | Layer (declared) | Evidence |
|---|---|---|
| `PSXRecomp.Core` | Domain (`PSXRecomp.Core`) | **CONFIRMED** `src/architecture.contract.json:3`; `docs/architecture-matrix.md` Layer Definitions table |
| `PSXRecompStudio` | Application (`PSXRecompStudio`) | **CONFIRMED** `src/architecture.contract.json:4`; `src/PSXRecompStudio/Services/TitleExecutionService.cs:17` (`[Application]`) |
| `PSXRecomp.Infrastructure` | Infrastructure | **CONFIRMED** `src/architecture.contract.json:5`; `src/PSXRecomp.Infrastructure/PSXRecomp.Infrastructure.csproj:10` (references Core only) |
| `PSXRecomp.Cli` | Infrastructure (per matrix) | **CONFIRMED** `docs/architecture-matrix.md` Layer Definitions table lists `PSXRecomp.Cli` under Infrastructure; `src/PSXRecomp.Cli/CliInput.cs:34` and `:149` are `[Infrastructure]` |
| `PSXRecomp.GameWindow` | none declared | **OBSERVED**: no `[Application]` / `[Infrastructure]` attribute found under `src/PSXRecomp.GameWindow`; **UNVERIFIED** whether AARC004 applies there (its csproj does not set `CompileArchitectureAttributes`, which the Studio csproj does at `src/PSXRecompStudio/PSXRecompStudio.csproj:9`) |
| `PSXRecomp.Native` | outside managed layers | **CONFIRMED** `docs/architecture-matrix.md` (Layer Definitions, paragraph after the table) |

Edges that matter for this design:

- `Application -> Infrastructure` is forbidden, with a composition-root exception only. **CONFIRMED** `src/architecture.contract.json:12`.
- `Infrastructure -> Application` is forbidden. **CONFIRMED** `src/architecture.contract.json:13`.
- `Domain -> Infrastructure` is forbidden. **CONFIRMED** `src/architecture.contract.json:11`.
- The Studio references Core only. **CONFIRMED** `src/PSXRecompStudio/PSXRecompStudio.csproj:36`.
- The CLI references Core and Infrastructure. **CONFIRMED** `src/PSXRecomp.Cli/PSXRecomp.Cli.csproj:23-24`.
- The Studio's `[Application]` composition root uses the Domain engine directly, which is allowed because Application -> Domain is allowed. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:78`.

Consequence for the design: the CLI is in the Infrastructure layer, so under the current contract it cannot depend on any Application-layer use-case type. A "shared Application use-case layer" that the CLI also calls therefore needs an owner decision (section 9, D1).

### 2.2 The Studio today (Application layer)

- The window has two command buttons and a frame image: "Run diagnostic title" and "Run analyzed title". **CONFIRMED** `src/PSXRecompStudio/Views/MainWindow.axaml:47-78`.
- The window has no control that selects or opens a disc. **CONFIRMED** `src/PSXRecompStudio/Views/MainWindow.axaml` (no file picker control or binding; the only disc input is the `DiscImageBytes` property, `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:61`).
- Production code never assigns `DiscImageBytes`; only tests do. **OBSERVED** by grep: assignments appear only in `src/PSXRecompStudio.Tests/GUIWorkflowTests.cs:44,60,81` and `src/PSXRecompStudio.Tests/RealRomProductionFlowTests.cs:106,121`. Consequence: the "Run analyzed title" action is reachable only with a disc supplied programmatically. **PREDICTED** from this: a user of the shipped window cannot run a real title.
- The diagnostic run is synchronous on the UI thread. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:68-72` (`private void RunDiagnosticTitle()` calls `_execution.RunDiagnostic()` directly). Its budget is small (outer 4, segment 64), so the cost is bounded. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:159-160`.
- The real-title action is `async` with `AllowConcurrentExecutions = false`, runs analysis and interpretation on `Task.Run`, and marshals only the final status back to the UI. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:95-113`.
- The real-title action hashes the disc (SHA-256) inside the view model. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:107-109`. The CLI computes the same identity separately from a file stream. **CONFIRMED** `src/PSXRecomp.Cli/CliInput.cs:106`. Two front ends now own two copies of an identity rule.
- The real-title service is I/O-free by design and documents that disc acquisition is deferred to Issue #38. **CONFIRMED** `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:59-62`.
- The real-title service calls the Domain analysis pipeline directly and keeps the analyzed executable. **CONFIRMED** `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:97`, `:137-161`.
- Layout rejections are caught as `ArgumentException` and classified into the result. **CONFIRMED** `src/PSXRecompStudio/Services/RealRomTitleExecutionService.cs:142-153`.
- The Studio runs titles only with the interpreter. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:78` and `:121`. This is the decision recorded in ADR-015. **CONFIRMED** `docs/adr/015-production-execution-engine-ownership.md:67-70`.
- The Studio's execution calls do not pass a cancellation token to the orchestrator. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:92-93` and `:126-129` (the `Execute` overload with `cancellationToken` is not used). The orchestrator supports it. **CONFIRMED** `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:61-67`, `:73-75`.
- The status strings are composed in the view model from state, engine name, segment count, TTY text and diagnostic code. **CONFIRMED** `src/PSXRecompStudio/ViewModels/MainWindowViewModel.cs:74-77`, `:117-137`. These are presentation strings, but they are unlocalized and they are the only error surface.
- The Studio composes its own copies of the guest-end handoff. **CONFIRMED** `src/PSXRecompStudio/Services/TitleExecutionService.cs:183-192` (private `ProgramEndHandoff`), while Core already has a public one. **CONFIRMED** `src/PSXRecomp.Core/Execution/ProgramEndHandoff.cs:16-27`. Behavior is the same (`PC == loadAddress + count * 4`), so the copy is duplication, not a semantic fork.
- The Studio contains file I/O for memory cards, under suppressions. **CONFIRMED** `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs:166-167`, `:176-177`, `:212-213`, `:277-280`, `:304-324`, each with `#pragma warning disable AARC003`. The class comment calls this a composition-root I/O site until Infrastructure exists. **CONFIRMED** `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs:14-22`. Production code does not use this class yet; only tests do. **OBSERVED** by grep (`src/PSXRecompStudio.Tests/FileMemoryCardStorageTests.cs`). ADR-017 states that the Infrastructure project is activated, so this I/O is now in the wrong layer. **CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:35` (activation rule, item 7), `src/PSXRecomp.Infrastructure/PSXRecomp.Infrastructure.csproj` (exists).

### 2.3 The CLI today (Infrastructure layer)

- The CLI is documented as a "thin composition root" with no compiler, build or execution-loop semantics. **CONFIRMED** `docs/development/headless-cli.md:19-21`.
- The `recompile` command in practice runs the whole pipeline: load input, lower, codegen, artifact codegen, standalone image packing, and the build. **CONFIRMED** `src/PSXRecomp.Cli/RecompileCommand.cs:32-115`, with byte packing at `:80-83`. Packing little-endian instruction words is a rule that Studio would also need.
- The `run` command builds and launches through the generated-host launcher, not through the interpreter. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:68`, `src/PSXRecomp.Infrastructure/RecompiledArtifactLauncher.cs:73-80`.
- The Studio runs the interpreter, the CLI runs a generated host. **CONFIRMED** (`TitleExecutionService.cs:78` vs `RunCommand.cs:68`). The headless doc nevertheless says the CLI composes "exactly the same pipelines the Studio uses." **CONFIRMED** `docs/development/headless-cli.md:16-17`. This is drift (see section 2.5).
- The CLI's `run`, `screenshot` and frame-evidence code each have their own guest-end handoff class. **CONFIRMED** `src/PSXRecomp.Cli/RunCommand.cs:302`, `src/PSXRecomp.Cli/ScreenshotCommand.cs:629`, `src/PSXRecomp.Cli/ProductionFrameEvidenceCollector.cs:121`.
- The CLI reads disc files and runs analysis itself. **CONFIRMED** `src/PSXRecomp.Cli/CliInput.cs:74`, `:84` (`File.ReadAllBytes`), `:106-115` (`File.OpenRead` then `RomAnalysisPipeline.RunFromChd`).
- The CLI's error surface is an exit code plus a JSON envelope; the table is in the doc. **CONFIRMED** `docs/development/headless-cli.md:219-227` (exit codes section and JSON envelopes).

### 2.4 The second Avalonia window (PSXRecomp.GameWindow)

- This project is a second Avalonia desktop app that references Infrastructure directly. **CONFIRMED** `src/PSXRecomp.GameWindow/PSXRecomp.GameWindow.csproj:20`, `src/PSXRecomp.GameWindow/MainWindow.axaml.cs:14`.
- It already has the async/cancel/UI-marshal pattern the Studio needs: it starts a run with a `CancellationToken` (`:69`), cancels through a `CancellationTokenSource` (`:37`, `:109`), and posts the result back to the UI thread (`:79`). **CONFIRMED**.
- It is not in the Studio's architecture story. The solution lists it separately. **CONFIRMED** `src/PSXRecompStudio.slnx:5`.

### 2.5 Drift and duplication found

| # | Finding | Evidence | Label |
|---|---|---|---|
| F1 | No shared use-case layer exists; CLI holds recompile/build orchestration and Studio holds execution wiring. | Sections 2.2, 2.3 | CONFIRMED |
| F2 | Headless doc says CLI and Studio compose "exactly the same pipelines"; they run different engines (interpreter vs generated host). | `docs/development/headless-cli.md:16-17`, `TitleExecutionService.cs:78`, `RunCommand.cs:68` | CONFIRMED (drift) |
| F3 | Contract reason for `Application -> Infrastructure` differs from the reason ADR-015 quotes. The contract says "composition-root exception"; ADR-015 quotes "only through the Domain interop boundary." | `src/architecture.contract.json:12`, `docs/adr/015-production-execution-engine-ownership.md:29-31` | CONFIRMED (drift) |
| F4 | Guest-end handoff is duplicated in at least four production sites even though Core has the public type. | `TitleExecutionService.cs:183`, `RunCommand.cs:302`, `ScreenshotCommand.cs:629`, `ProductionFrameEvidenceCollector.cs:121`, `Core/Execution/ProgramEndHandoff.cs:16` | CONFIRMED |
| F5 | Disc identity (SHA-256) is computed in two front ends. | `MainWindowViewModel.cs:107-109`, `CliInput.cs:106` | CONFIRMED |
| F6 | Studio performs memory-card file I/O inside Application, under AARC003 suppressions, though ADR-017 says Infrastructure now exists. | `FileMemoryCardStorage.cs:14-22,166-324`, `ADR-017:35` | CONFIRMED |
| F7 | Studio cannot open a disc; the real-title action is only reachable programmatically. | `MainWindow.axaml:63-78`, `MainWindowViewModel.cs:61` | CONFIRMED (by grep and XAML) |
| F8 | Analysis, build and launch have no cancellation token. Only the orchestrator (`ExecutionOrchestrator.Execute`, `Resume`) and the CLI-side `LiveGameRunner` accept one. | `ExecutionOrchestrator.cs:61-67`; `RecompiledArtifactLauncher.cs:73-80` (no token); `GeneratedHostBuildService.cs:42` (timeout only; the `Build` method takes no token: `GeneratedHostBuild.cs:89`); `LiveGameRunner.cs:26-33` | CONFIRMED |
| F9 | No progress reporting exists in Domain, Infrastructure or CLI. Analysis exposes stage results after completion, and the orchestrator exposes a per-segment callback. | `rg`/grep for `IProgress` returned none outside tests; `RomAnalysisOutcome.cs:69` (`RomAnalysisStageRecorder`); `ExecutionOrchestrator.cs:46` (`segmentCallback` documented; parameter at `:65`) | OBSERVED |
| F10 | Studio errors are raw composed strings; the CLI uses stable codes (e.g. `EXECUTION_CANCELLED`) and exit codes. | `MainWindowViewModel.cs:74-77,117-137`; `ExecutionOrchestrator.cs:90`; `RomAnalysisOutcome.cs:32-33` (`FailureKind`) | CONFIRMED |
| F11 | `ARCHITECTURE.md` names `PSXRecomp.Runtime`, `PSXRecomp.Recompiler`, `PSXRecomp.Debugger` projects that do not exist in the solution. | `ARCHITECTURE.md:355-362`; `src/PSXRecompStudio.slnx` | CONFIRMED (drift, out of scope to fix here) |
| F12 | No MCP project exists in `src`. | **OBSERVED** directory listing of `src/` | OBSERVED |

## 3. Target boundary

### 3.1 Responsibilities

| Layer / unit | Owns | Must not own |
|---|---|---|
| **GUI** (Studio views, `PSXRecompStudio` window code) | Layout, bindings, input, presentation copies (`PresentationFrame`), localized labels, progress and cancel controls, dispatching to UI thread | Any analysis, codegen, execution, hash, budget, verdict or file/process operation |
| **Application use cases** (see 3.2 for placement) | Use-case orchestration, request/result contracts, single-flight policy, progress and cancel plumbing, mapping stage results to one shared result shape | Engine semantics, parsing, lowering, host I/O |
| **Domain** (`PSXRecomp.Core`) | Analysis pipeline, orchestrator, codegen, execution semantics, ports (`IGeneratedHostBuildService`, `IProjectMetadataStore`, `IMemoryCardStorage`, `IRuntimeOutputSink`, `ICancellableExecutionEngine`) | Host I/O, process control, UI types |
| **Infrastructure** (`PSXRecomp.Infrastructure`) | Concrete adapters: disc file reading, toolchain process, generated-host launcher, metadata/file stores, memory-card file store | Business rules; any rule not already in a Domain contract |
| **Interop** (`PSXRecomp.Core` `NativeInterop`, `PSXCoreWrapper`) | C ABI boundary to `PSXRecomp.Native` | Anything above |
| **CLI** (`PSXRecomp.Cli`) | Argument parsing, exit codes, JSON envelope, stdout/stderr | Use-case orchestration (target state; see F1) |
| **Tests** | Drive GUI via simulated controls; drive use cases directly | Production dependencies on tests |

### 3.2 Where the shared use cases live (requires owner decision D1)

The contract forbids the CLI (Infrastructure) and the Studio (Application) from depending on each other's layer, and it forbids Application from using Infrastructure except at a composition root. That leaves two candidate placements:

| Option | Shape | Pros | Cons |
|---|---|---|---|
| **A. Use cases in Domain** (recommended) | Pure orchestration over Domain ports, in `PSXRecomp.Core` (e.g. a `Usecases` namespace under `PSXRecomp.Core`). Studio and CLI both call it. | Fits the existing precedent (`ExecutionOrchestrator`, `RomAnalysisPipeline`, `RecompilerHostCodeGen` are already Domain orchestration). Needs no new contract edge. CLI and Studio stay at layer edges that are already allowed. Testable without Avalonia. | The use-case layer must not get UI or host dependencies (enforced by review and AARC003 for Domain: `System.IO.File`, `Process`, `Console` are already forbidden in Domain). Some orchestration would move out of the CLI into Core, which needs review. |
| **B. Use cases in an Application project** | New project with namespace root `PSXRecompStudio.*` (Application by root prefix). Studio references it. | Keeps UI-adjacent policy (single-flight, progress marshaling hooks) close to the GUI. | The CLI (Infrastructure) cannot reference it without a contract change, so the CLI would keep its own orchestration (F1 persists). Contract change needed. |

Option A is recommended. The CLI and Studio become two composition roots, each wiring Domain use cases to Infrastructure adapters.

### 3.3 Composition roots and the Application -> Infrastructure edge

Both executables need concrete adapters: Studio needs a disc reader, the toolchain, and possibly the launcher; the CLI already has them.

- ADR-017 permits one composition-root site per executable, with a narrowly scoped `AARC002` suppression and rationale. **CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:31`.
- ADR-017 says that if the suppression spreads, a dedicated Host/Bootstrap layer should be introduced. **CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:31`, `docs/architecture-matrix.md` (Application -> Infrastructure paragraph).
- The matrix already says a second Application-to-Infrastructure site is evidence for that layer. **CONFIRMED** `docs/architecture-matrix.md` ("Keep composition-root adapter construction isolated", Recommendations).

Recommendation: the Studio's `App` (composition root) is the first such site. Defer the Host/Bootstrap layer until a second executable needs the same wiring, and record that trigger in the ADR (D2).

### 3.4 Flow: Analysis -> Recomp -> Build -> Run

| Stage | Today: owner and entry | Shared use case (target) | Adapter needed | GUI state | Cancellation point |
|---|---|---|---|---|---|
| Input / disc load | Studio: none (F7). CLI: `CliInput.Load`, `CliInput.cs:41`, reads files at `:74,84,106` | `LoadDisc(path or bytes)` returns identity (SHA-256) and format | Disc reader (Infrastructure, path -> bytes/stream) | Choose, Loading, Loaded (hash, size) | Before read, between sectors if the reader supports it |
| Analysis | Studio: `RealRomTitleExecutionService.cs:97`. CLI: `CliInput.cs:115` | `AnalyzeDisc` returns `RomAnalysisOutcome` with stage results | None beyond bytes/stream | Running (stage name), Pass, Fail with `FailureKind` | Token checked between stages (**gap**: analysis does not accept a token today; PREDICTED to need a Domain change) |
| Recomp (codegen) | CLI only: `RecompileCommand.cs:45-63`, `RecompilerHostCodeGen.Generate` | `RecompileTitle` returns the generated host source and state | None | Running, Done | Token before codegen (**gap**) |
| Build | CLI only: `RecompileCommand.cs:115`, `GeneratedHostBuildService.Build` | `BuildArtifact` through `IGeneratedHostBuildService` | Toolchain (Infrastructure) | Building (toolchain step), Built, ToolingError | Token to kill toolchain (**gap**: only a timeout exists, `GeneratedHostBuildService.cs:42`; the `Build` method takes no token, `GeneratedHostBuild.cs:89`) |
| Run | Studio: interpreter, `TitleExecutionService.cs:92-93` (no token). CLI: generated-host launcher, `RunCommand.cs:68` | `RunTitle` with engine selection (D3) | Launcher port (**gap**: `RecompiledArtifactLauncher` is concrete Infrastructure, `:21`, `:73-80`) | Running (segment count), Success, Blocked, Failed, Cancelled | Already supported by the orchestrator via `ICancellableExecutionEngine` (`ExecutionOrchestrator.cs:61-67`, `ICancellableExecutionEngine.cs:11-25`) |

Notes on the table:

- Build and Run for the generated host are one operation today: `Launch` rebuilds and then runs (`RecompiledArtifactLauncher.cs:73-80`). **CONFIRMED** by the doc comment in `src/PSXRecomp.Cli/RunCommand.cs:20-22`. The GUI should expose them as separate steps only when the launcher is split (PREDICTED change; it is a Domain-port decision, D5).
- Analysis results are the input the GUI must show for review (issue #40, "Analysis Result Viewer"). The result type already carries stage status and `FailureKind`. **CONFIRMED** `src/PSXRecomp.Core/DiscImage/RomAnalysisOutcome.cs:16`, `:30-33`.

### 3.5 Async, progress and cancellation

**Async.** Each use case is a `Task<TResult>` with a `CancellationToken` parameter. CPU-bound work runs off the UI thread. This is the existing rule in `MainWindowViewModel.cs:105` (`Task.Run`) and in the GameWindow (`MainWindow.axaml.cs:69`), made uniform.

**Single-flight.** Each command rejects a second concurrent start for the same use case, as `AllowConcurrentExecutions = false` does today (`MainWindowViewModel.cs:95`). The rule belongs in the use-case or GUI command layer, not in the views.

**Progress.** Use-case progress is a small Domain-owned value type (stage name, step index, step total, optional message), delivered through `IProgress<T>` (BCL) to the caller. The Studio marshals it to the UI thread with the Avalonia dispatcher, as the GameWindow does (`MainWindow.axaml.cs:79`). Granularity:

- Analysis: per stage (available after completion through `RomAnalysisStageRecorder`, `RomAnalysisOutcome.cs:69`; live per-stage delivery is a gap, PREDICTED Domain change).
- Run: per segment (available through `ExecutionOrchestrator.Execute` `segmentCallback`, `ExecutionOrchestrator.cs:65`).
- Build: toolchain step (compile, link) is not reported today; PREDICTED Infrastructure change.

**Cancellation.** One token per user-initiated operation. The GUI's Cancel button cancels that token. Each stage checks it between units of work. Orchestrator semantics already exist: a pre-cancelled run ends as `Cancelled` without loading (`ExecutionOrchestrator.cs:73-75`), and a running segment stops through `ICancellableExecutionEngine.RequestCancel` (`ICancellableExecutionEngine.cs:17`). Gaps are listed in section 3.4.

**Stop semantics for the UI.** A cancelled operation must end as a classified `Cancelled` outcome, not as an exception and not as a hung spinner. The GUI shows the outcome, and the last progress value stays visible.

### 3.6 Error display

**Result shape.** Every use case returns one shared result type:

- `Outcome`: `Succeeded`, `Blocked` (explicit unsupported boundary), `Failed` (classified failure), `Cancelled`, `ToolingError` (missing toolchain or I/O), `InvalidInput`. The mapping to CLI exit codes (0 / 2 / 1) belongs only in the CLI. **CONFIRMED** the existing exit-code rules in `src/PSXRecomp.Cli/RunCommand.cs:29-30` (Success 0, Blocked 2, Failure and tooling 1).
- `Code`: a stable machine string (for example `EXECUTION_CANCELLED`, `ExecutionOrchestrator.cs:90`; or `FailureKind` from `RomAnalysisOutcome.cs:32-33`).
- `MessageKey` and `Arguments`: a localization key (issue #21) instead of an English sentence. Until #21 lands, the English text is a fallback.
- `Details`: optional path to a diagnostic report, reusing the existing bundle contract (`docs/development/headless-cli.md` diagnostic report section).

**GUI rule.** The GUI renders `Outcome`, `Code`, the localized message, and a "Details" disclosure. It must not parse CLI text or stderr. This is the issue's requirement ("GUIはCLI固有の内部実装やテキストログの解析に依存しない") and it is the reason for the shared result type.

**Migration.** Today's composed strings (`MainWindowViewModel.cs:74-77`, `:117-137`) become one `Outcome` plus `Code` plus formatted detail; the composed text moves to a presentation mapper that is pure and testable.

### 3.7 The "GUI adds no business logic" rule

The rule is stated as a checklist that a review can apply to any GUI change.

**The GUI may:**

- bind, lay out, select, and show progress and state;
- convert a Domain result to a presentation model with a pure, lossless transform, as `PresentationFrame.From` does for frame snapshots (`MainWindowViewModel.cs:72`, `:111`; `docs/architecture/gui-ux.md:110-113`);
- localize labels and format numbers and dates;
- decide what to show first, what to collapse, and how to order rows.

**The GUI must not:**

- compute hashes, identities, checksums or file formats (today's violation: `MainWindowViewModel.cs:107-109`, to move into the use case, F5);
- choose or change execution budgets, engines or fallbacks (defaults live in shared request types; today `RealRomTitleExecutionService.cs:68-71`, which the CLI does not share);
- classify outcomes or decide pass/fail from raw fields (today the view model maps states to text, `MainWindowViewModel.cs:117-137`; the mapping is presentation only and must not grow rules);
- read or write files, start processes, or touch Infrastructure types (today's violation: none in the view model, but the memory card store sits in Application, F6);
- parse CLI output or log text.

**Enforcement.** Proposed, not yet built:

1. A review checklist item in the GUI PR template (process only).
2. A test that fails if the Studio project references Infrastructure outside the composition-root file (PREDICTED test; UNVERIFIED feasibility in the current test project).
3. The existing AARC rules remain in force: `System.IO.File`, `Directory`, `Process`, `Console` are forbidden in Application (`src/architecture.contract.json`, `forbiddenApis` Application entries). **CONFIRMED** the Application entries in the contract.

### 3.8 CLI, MCP and Tests

- **CLI.** Target: the CLI is a composition root and presentation layer only. Its `recompile`, `run` and frame-evidence paths call the same use cases as the Studio. Its duplicate handoffs (F4) are replaced by `PSXRecomp.Core.Execution.ProgramEndHandoff`. The headless doc's parity sentence (F2) becomes accurate only after the engine question (D3) is answered.
- **GUI-to-CLI process.** Issue #40 allows the GUI to launch the CLI as an implementation choice but does not fix it. Recommendation: in-process use cases. Process launch is a fallback only if a toolchain must be isolated (D6).
- **MCP.** There is no MCP project today (F12). MCP should be a third thin adapter over the same use cases, added later. This document does not design the MCP surface.
- **Tests.** GUI tests keep driving real controls (`GUIWorkflowTests.cs` header comment, lines 11-21, states that the Project/Analysis/Recompile/Approval screens do not exist yet). Use-case tests run without Avalonia. **CONFIRMED** `src/PSXRecompStudio.Tests/GUIWorkflowTests.cs:11-21`.

### 3.9 Headless independence

The Runtime and the headless CLI must not reference Avalonia; a test guards this (`HeadlessAvaloniaIndependenceTests`). **CONFIRMED** `docs/architecture/gui-ux.md:115`. The use-case layer (Option A) must keep that property: no Avalonia type in Core use cases.

## 4. Recommended approach

The recommended approach is incremental, with each phase a separate, behavior-preserving change where possible.

| Phase | Change | Behavior change | Gate |
|---|---|---|---|
| 0 | This design, plus owner decisions D1-D8 recorded in an ADR (the issue's "ADR/SSOT へ反映" criterion) | None | Owner sign-off |
| 1 | Remove the duplicate guest-end handoffs (F4) in favor of `ProgramEndHandoff` | None expected; behavior is identical (verify in CLI and Studio tests) | Existing tests |
| 2 | Add the shared result type, `CancellationToken` parameters and `IProgress` plumbing to use cases in Core (Option A); move the CLI's recompile orchestration into them | None for CLI output; new APIs only | CLI JSON and exit-code tests unchanged |
| 3 | Studio: disc load through a Domain port with an Infrastructure reader (F7), Cancel and progress UI, shared error panel | New UI | GUI tests drive real controls |
| 4 | Move `FileMemoryCardStorage` to Infrastructure (F6); Studio keeps only the port | None | Existing memory-card tests |
| 5 | Decide the Run engine question (D3) and, if approved, add the generated-host run to the Studio | New UI and new composition | ADR amendment |
| 6 | MCP adapter over the same use cases | New surface | Separate issue |

Why this order: Phase 1 removes duplication with no risk. Phase 2 creates the shared surface both front ends need, before any GUI change. Phase 3 is the first user-visible change and depends on the surface being fixed. Phases 4 to 6 each depend on an owner decision.

## 5. Alignment with ADR-015 and ADR-017

- ADR-015's decision (interpreter as the Studio's production engine, in Domain) is preserved. **CONFIRMED** `docs/adr/015-production-execution-engine-ownership.md:43-47`, `:67-70`.
- ADR-017's rule that Application does not acquire host resources is violated by F6 and is the reason Phase 4 exists. **CONFIRMED** `docs/adr/017-managed-host-io-port-adapter-boundary.md:31`, `docs/architecture-matrix.md` item 3 ("Application does not acquire host resources directly", line 66).
- The contract's Application -> Infrastructure reason text (F3) should be corrected in the same ADR that records D1 and D2, so the rationale and the contract agree. Contract change itself is out of scope here.

## 6. Coordination with W17 (egui PoC, issue #851)

- The egui comparison is an experiment. It must not replace, modify or be merged into the Avalonia GUI. Nothing in this document depends on its outcome.
- The use-case boundary in section 3 is toolkit-neutral by design: no Avalonia type in Core use cases. If the egui PoC is pursued, it would consume the same Domain use cases through its own composition root. That is a second composition root in a second UI, which is exactly the situation in section 3.3.
- Owner decision D8 covers whether the comparison result changes the Avalonia decision. This document assumes the Avalonia GUI continues.
- **UNVERIFIED:** the content of issue #851, which this session did not read. The coordination above is based on the brief for this worker only.

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Use-case code drifts into the view model again | PREDICTED high without a checklist | Section 3.7 checklist; Phase 3 tests drive use cases directly |
| Core use cases pick up host I/O or UI types | PREDICTED medium | AARC003 already forbids host APIs in Domain; add an Avalonia-free rule test (PREDICTED) |
| Composition-root suppressions spread | PREDICTED medium (two executables) | ADR-017 rule; D2 trigger |
| Cancellation gaps leave work running after the user cancels | CONFIRMED gap (F8) | Phase 2 tokens; toolchain kill is the hardest item and may stay a timeout-only behavior until D5 |
| Studio runs a different engine from the CLI and users compare the wrong results | CONFIRMED today (F2) | D3 decision; label the engine in every result |
| Localization retrofit cost | PREDICTED medium | Message keys from Phase 2 even before #21 translations exist |
| Disc acquisition in the GUI blocks on large CHD files | PREDICTED medium | Run disc load and analysis off the UI thread (section 3.5); progress per stage |

## 8. Out of scope

- Implementing any GUI screen, use case, port or adapter.
- The Project, Compatibility Registry, AI review and Approval screens (issue #40 lists them; they need their own design).
- The MCP surface.
- Changing `src/architecture.contract.json`, `ARCHITECTURE.md` or existing ADRs (only proposed in section 5).

## 9. Owner decisions

Each decision lists options and the recommendation. The owner decides; this document does not.

**D1. Where the shared use cases live.**
- Option A (recommended): Domain (`PSXRecomp.Core`), used by Studio and CLI.
- Option B: a new Application project; requires reclassifying the CLI or a contract change, and leaves CLI orchestration duplicated.

**D2. Composition root and Application -> Infrastructure wiring.**
- Option A (recommended for now): one composition-root site per executable with a narrow `AARC002` suppression each, as ADR-017 permits; trigger for a Host/Bootstrap layer: a third site or a second executable needing the same wiring.
- Option B: introduce a Host/Bootstrap layer now (a separate architecture change).

**D3. Which engine the Studio's "Run" uses.**
- Option A (recommended): interpreter as the Studio default (as ADR-015 decided); add the generated-host run as an explicit, labelled second action later (Phase 5).
- Option B: switch the Studio to the generated host now (requires D2 and an ADR-015 amendment).
- Either way, correct the headless doc's parity claim (F2).

**D4. Cancellation scope for analysis and build.**
- Option A (recommended): add tokens to Domain analysis and codegen entry points in Phase 2; for the toolchain, a token that kills the process, with timeout kept as the backstop.
- Option B: keep timeout-only cancellation for build and add a "cannot cancel during build" state to the GUI.

**D5. Split of Build and Run in the generated-host path.**
- Option A (recommended): expose a Domain launcher port with separate build and run steps (matches the GUI's stages).
- Option B: keep `Launch` as one operation and show it as one "Build and run" action.

**D6. GUI-to-CLI process launch.**
- Option A (recommended): no; in-process use cases only. Process launch only for toolchain isolation if D4 cannot be met in-process.
- Option B: GUI launches the CLI for long jobs with JSON output. This keeps the CLI as the single pipeline but adds an output-parsing contract to maintain.

**D7. Disc acquisition and the Studio's file access.**
- Option A (recommended): a Domain port for disc input; Infrastructure reader; file picker in the GUI passes a path only. Move `FileMemoryCardStorage` to Infrastructure in Phase 4 (F6).
- Option B: keep disc bytes supplied by the caller and defer file selection, as today. This leaves the shipped Studio unable to load a disc (F7).

**D8. Relationship to the egui PoC (W17, #851).**
- Option A (recommended): the comparison is a time-boxed experiment with its result recorded in an ADR; Avalonia stays the Studio UI unless the owner decides otherwise.
- Option B: decide the UI toolkit before the use-case boundary; this would change Phase 3 scope.

Additional decisions the owner should also note, without a recommendation here:

- **D9.** Whether `PSXRecomp.GameWindow` stays a separate app or is folded into the Studio; it currently references Infrastructure directly (F-section 2.4) and has no layer attribute (UNVERIFIED enforcement).
- **D10.** Localization timing for the error codes (issue #21).
- **D11.** Where Session/Trace records are stored (required by issue #40, "Session/Trace 情報を残せるように"); not designed here.

## 10. Verification performed for this document

- Read-only survey of the files cited above at base `2e3ecad`. **CONFIRMED** for every citation labelled CONFIRMED.
- No build, no test run, no analyzer run. **UNVERIFIED:** that the contract, the AARC analyzer, or the existing GUI tests still pass at this base, and that the proposed rule tests are feasible.
- No BIOS, ROM, disc or copyrighted content was read or included. Paths are repository-relative.
