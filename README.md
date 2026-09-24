# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

PSXRecompStudio is a research-oriented PlayStation 1 (PS1 / PSX) static-recompilation and reverse-engineering environment.

Its core differentiator is a **differentially validated recompiler path**: MIPS → IR/lowering → deterministic host C → bounded execution → interpreter state comparison. That path is proven on a synthetic fixture and on a first bounded real-ROM function. Complete commercial PS1 game recompilation is **not yet implemented**.

*[日本語版 README はこちら / Japanese README](README.ja.md)* · [Project website](https://mao2009.github.io/PSXRecompStudio/)

## Current scope

**Implemented and validated**

- Synthetic MIPS fixture → Recompiler IR/lowering → deterministic host C → gcc build → bounded execution → interpreter-state comparison, proven end-to-end (re-run below as evidence): [`RecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs).
- The same pipeline on a first conservatively selected, bounded real-ROM function, gated on a user-supplied ROM: [`RealRomCandidateSelector`](src/PSXRecomp.Core/Recompiler/RealRomRecompilerBridge.cs), [`RealRomRecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs) (#225).
- A synthetic, always-run correctness oracle for the full-title execution loop's own test harness: it proves the classified-end assertions the real-ROM execution test relies on actually reject a wrong run, rather than passing everything: [`ObservedTitleExecutionTests.cs`](src/PSXRecomp.Tests/Execution/ObservedTitleExecutionTests.cs) (#378).

**Implemented foundations / partial**

- R3000A / MIPS I decode/execute, branch and load delay slots, COP0/exceptions, interrupt sampling, and KSEG0/KSEG1 translation: [`src/PSXRecomp.Core/Cpu/`](src/PSXRecomp.Core/Cpu/). A native, per-instruction Golden Trace captures retirement-order register writes for future backend comparison: [`golden_trace.h`](src/PSXRecomp.Native/tests/golden_trace.h).
- CHD → ISO 9660 → PS-X EXE → MIPS analysis → basic blocks/CFG, exercised end-to-end by [`DiscImageAnalyzerIntegrationTests.cs`](src/PSXRecomp.Tests/DiscImage/DiscImageAnalyzerIntegrationTests.cs).
- A bounded, title-agnostic full-title execution loop, [`ExecutionOrchestrator`](src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs), driven by a production, Domain-layer interpreter engine reachable from the Studio UI ([ADR-015](docs/adr/015-production-execution-engine-ownership.md)). The Studio reaches classified execution through two distinct actions: `RunDiagnosticTitleCommand` (built-in diagnostic program) and, per Issue #409, `RunRealTitleCommand` — the real-ROM product flow in which the loaded disc image is analyzed, the analyzed PS-X EXE (entry PC, SP, GP, text segment from the EXE header) is retained from `RomAnalysisOutcome.Executable`, and that same executable is run via `TitleExecutionService.Run(PsxExe, ...)` → `ExecutionOrchestrator` → `InterpreterTitleExecutionEngine`, proven end to end by `RealRomProductionFlowTests`. The generated-C (recompiled) engine remains test-only.
- Shared BIOS A0/B0/C0 vector dispatch on both the interpreter and recompiled paths, currently covering 5 registered services (putchar, puts and its B0 alias, `GetB0Table`, `GetC0Table`) — not broad BIOS HLE coverage: [`BiosHleRuntime.cs`](src/PSXRecomp.Core/Runtime/BiosHleRuntime.cs).
- Register-level DMA/interrupt/timer MMIO adapters and a memory bus with dedicated tests: [`src/PSXRecomp.Core/Dma/`](src/PSXRecomp.Core/Dma/), reachable from the production interpreter engine (#386). A deterministic, CPU-cycle-driven [`DeviceScheduler`](src/PSXRecomp.Core/Runtime/DeviceScheduler.cs) advances the Rust-backed timers and DMA channels and raises Timer (IRQ4-6), DMA (IRQ3) and a fixed-interval VBlank (IRQ0) during a real run (#442); not cycle-exact, and DMA completion moves no data.
- A pure managed GPU model — GP0/GP1/GPUSTAT registers, a 1024x512x16b VRAM buffer, and flat/Gouraud-shaded rectangle and triangle rasterization into VRAM — plus a scheduler-independent, deterministic `FrameSnapshot` capture of the configured display region (raw pixels + a stable SHA-256 hash): [`src/PSXRecomp.Core/Runtime/Gpu/`](src/PSXRecomp.Core/Runtime/Gpu/) (#440, #441). Texture mapping, quads, and semi-transparency are not implemented; the GPU device itself is not yet wired into `DeviceScheduler` or any execution engine, so `IGpu.HasVblank` stays false and DMA2 is unconnected.
- Standard raw 128 KiB PlayStation memory-card images, read and written without conversion so a card can be shared with other emulators, with slot 1 / slot 2 configuration, atomic saves, and external-modification detection: [`src/PSXRecomp.Core/MemoryCard/`](src/PSXRecomp.Core/MemoryCard/), [`FileMemoryCardStorage.cs`](src/PSXRecompStudio/Services/FileMemoryCardStorage.cs), [`docs/runtime/memory-card.md`](docs/runtime/memory-card.md) (#22). The memory-card SIO/IRQ7 protocol and any card UI are not implemented.
- The first runnable-artifact proof (Issue #461): a synthetic PS-X EXE run through the entire production pipeline — `PsxExe.Load` → `PsxExeTitleInput.Build` → decode/lowering → host + artifact code generation → gcc build (#458) → launcher execution (#459) — asserting the generated/recompiled code really executed, deterministically (down to the generated source), and stopped only at classified boundaries; the legal real-input half runs the identical path against user-supplied `rom/*.exe` fixtures and skips explicitly when none is present: [`RecompiledArtifactE2ETests.cs`](src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs), [`RealExeE2ETests.cs`](src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs).
- A minimal headless CLI, `psxrecomp`, exposing the recompiled-artifact build (#458) and runnable-artifact execution (#459) contracts: `psxrecomp recompile <input.exe|input.chd> --output <dir>` and `psxrecomp run <input.exe|input.chd>`, with deterministic JSON output and 0/1/2 exit codes. Since #457 a `.chd` input is dispatched via extension and resolved through the production `RomAnalysisPipeline` (CHD → ISO 9660 → SYSTEM.CNF → boot PS-X EXE → `PsxExeTitleInput`), so a fully qualified CHD runs the identical downstream pipeline as the EXE; any other extension keeps the original EXE-only path. CHD failures are classified by the pipeline, never reported as an invalid PS-X EXE: [`src/PSXRecomp.Cli/`](src/PSXRecomp.Cli/), [`docs/development/headless-cli.md`](docs/development/headless-cli.md) (#460). Not the general-purpose command framework (Issue #15).
- Architecture layering mechanically enforced by `loach.ArchitectureAnalyzer` against [`architecture.contract.json`](src/architecture.contract.json).
- An end-to-end reproduction workflow, the Persona E2E gate, chains disc discovery → analysis → recompiler slice → orchestrated execution against a legally user-supplied fixture and reports PASS/FAIL/SKIP per stage: [`scripts/e2e/persona-e2e-gate.ps1`](scripts/e2e/persona-e2e-gate.ps1), tracked in [`docs/v0.1.0/persona-e2e-status.md`](docs/v0.1.0/persona-e2e-status.md). Its next generic runtime blocker toward an actual title screen is broader BIOS HLE coverage; GPU/SPU/CD-ROM producers remain required after the needed BIOS calls are supported.

**Not implemented**

- General-purpose real-ROM function recompilation — candidate selection is deliberately conservative (see [Recompilation Workflow](#recompilation-workflow)).
- End-to-end static recompilation and execution of a complete commercial PS1 title.
- SPU, CD-ROM, MDEC, and GTE — interface contracts only; nothing implements or consumes them yet (e.g. [`IGte.cs`](src/PSXRecomp.Core/Runtime/IGte.cs)). GPU has a register/VRAM/rasterization model (see above) but texture mapping, quads, and display-frame presentation are not implemented, and it is not wired into any execution engine.
- Broad BIOS HLE service coverage.
- A general-purpose product CLI.

### Evidence: a real differential-validation run

```text
$ dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release \
    --filter "FullyQualifiedName~PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests"

Passed PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests.VerticalSlice_Matches_Interpreter_On_One_Plus_Two_Equals_Three [312 ms]
Passed PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests.VerticalSlice_RecompiledSnapshots_Are_Deterministic_Across_Independent_Runs [623 ms]
Passed PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests.VerticalSlice_Produced_Test_Binary_Is_Identical_Across_Runs [320 ms]

Total tests: 3
     Passed: 3
```

This is the synthetic MIPS → IR → generated host C → gcc build → bounded execution → interpreter-state comparison path, run directly from this repository (Issue #209). The real-ROM counterpart runs the identical pipeline against a user-supplied ROM under `rom/` and skips explicitly when none is present — see [`RealRomFixtures.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomFixtures.cs).

### Evidence: the first runnable recompiled artifact (Issue #461)

```text
$ dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release \
    --filter "FullyQualifiedName~PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests"

Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_FullProductionChain_ExecutesGeneratedCodeToCompletion
Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_RepeatedChain_IsDeterministicSourceAndStableBuildInput
Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_UnresolvedJumpBoundary_IsClassifiedBlockedNotCrash
Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_UnsupportedBiosBoundary_IsClassifiedFailureWithDiagnostic

Total tests: 4
     Passed: 4
```

This is the same production pipeline's vertical proof (Issue #461): one synthetic
PS-X EXE walks parse → input bridge → lowering → host/artifact code generation →
gcc build (#458) → launcher execution (#459), asserting the generated/recompiled
code really executed, deterministically (repeated runs are identical down to the
generated source), and stopped only at classified boundaries. The legal real-input
half runs the identical path against a user-supplied PS-X EXE under `rom/*.exe`
and skips explicitly when none is present — see [`RealExeE2ETests.cs`](src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs).

## Quick start

### Verify the current implementation

```bash
dotnet build src/PSXRecompStudio.slnx --configuration Release
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release
```

A successful run verifies the current CPU/runtime/recompiler contracts, including the synthetic recompiler vertical slice above. The real-ROM differential tests require a legally obtained, user-supplied image and skip automatically without one. For the native core and headless GUI suites, see [Build](#build) and [Test](#test).

### Explore the recompiler

- Recompiler implementation: [`src/PSXRecomp.Core/Recompiler/`](src/PSXRecomp.Core/Recompiler/) (IR/lowering `MipsToIrLowerer.cs`, host codegen `RecompilerHostCodeGen.cs`, differential comparison `RecompilerDifferentialResult.cs`).
- Synthetic differential validation: [`RecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs).
- Bounded real-ROM validation: [`RealRomRecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs) and [`RealRomTitleExecutionTests.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomTitleExecutionTests.cs).
- Native per-instruction Golden Trace: [`src/PSXRecomp.Native/tests/golden_trace.h`](src/PSXRecomp.Native/tests/golden_trace.h).
- BIOS vector dispatch: [`BiosVectorDispatch.cs`](src/PSXRecomp.Core/Runtime/BiosVectorDispatch.cs).

A general-purpose recompilation CLI is not available yet; the minimal headless surface ([`docs/development/headless-cli.md`](docs/development/headless-cli.md)) covers the #458/#459 contracts only.

## Next milestones

1. **Expand BIOS HLE coverage (#279, #365):** implement the services required by the next real-title execution path while preserving explicit failure for unsupported calls.
2. **Broaden real-ROM recompilation coverage:** expand supported instructions/control flow only with differential validation retained as the correctness gate.
3. **A production generated-host (recompiled) execution backend:** compile recompiled guest code and run it as the product's execution backend, deferred from ADR-015 Option B.

> **Asset policy:** ROM, ISO, CHD, BIOS, firmware images, and commercial game assets are not included in this repository. Any user-supplied files must be obtained and used legally.

## What is PSXRecompStudio?

PSXRecompStudio is a from-scratch development environment for analyzing and reverse engineering PlayStation 1 software: disassembling PS-X executables, analyzing R3000A / MIPS I code, modeling CPU behavior with byte-for-byte fidelity, and researching how title code can eventually be statically recompiled into native programs for modern systems.

It combines an Avalonia-based desktop UI, a C# domain/application core, and a C++ native core connected through a stable C ABI, with AI development agents as an optional, evidence-first assistance layer rather than the product itself.

For preservation and reverse-engineering work, the project favors reproducible analysis and deterministic, re-verifiable execution evidence over opaque compatibility heuristics or title-specific hacks.

## Why PSXRecompStudio?

- **SSOT-driven architecture.** Architecture, CPU semantics, and development process are documented as living Single Sources of Truth in [`docs/`](docs/) and [Architecture Decision Records](docs/adr/), not left to tribal knowledge.
- **Mechanically enforced boundaries.** `loach.ArchitectureAnalyzer`, configured by [`src/architecture.contract.json`](src/architecture.contract.json), fails the build on layering, dependency-direction, and forbidden-API violations.
- **A stable C#/Native boundary.** All communication with the native core crosses a single C ABI (`psx_core.h`) via P/Invoke — no C++ types leak into C#.
- **Evidence-first, human-in-the-loop AI collaboration.** AI development agents are a replaceable tool, not the product's identity; the workflow is agent-agnostic (Claude Code, OpenCode, Codex, or others).

## Current Status

Status reflects the current repository state (implementation, tests, and CI), not open issues or design intent. See [Current scope](#current-scope) above for the evidence behind each row.

| Area | Status |
|---|---|
| CPU execution (decode/execute, delay slots, COP0, interrupts, KSEG, Golden Trace) | Implemented — [`docs/cpu/`](docs/cpu/) |
| Recompiler (synthetic + first real-ROM function, differential validation) | Validated, bounded — general real-ROM coverage not implemented |
| Runnable recompiled-artifact E2E (synthetic + legal real EXE) | Implemented — Issue #461 |
| Disc / executable analysis (CHD → ISO 9660 → PS-X EXE → CFG) | Implemented |
| Runtime / BIOS execution boundary (A0/B0/C0 dispatch, production interpreter engine wired into Studio) | Partial — real PS-X EXE loading and interpreter execution supported (#409); not broad BIOS HLE |
| Hardware — DMA / interrupts / timers (MMIO adapters, memory bus) | Partial — advanced by `DeviceScheduler` in the production interpreter (#442): IRQs latch in I_STAT and, with SR IEc/IM2 set, are taken as CPU interrupts whose guest handler runs and returns via RFE (#499); not cycle-exact, DMA completion moves no data |
| Hardware — GPU | Partial — register/VRAM model, flat/Gouraud rectangle/triangle rasterization, deterministic frame snapshot (C#, not wired into `DeviceScheduler` or any execution engine) |
| Hardware — SPU / CD-ROM / MDEC / GTE | Planned — interface contracts only |
| Memory cards (standard raw 128 KiB image, slot 1/2 configuration, safe saves) | Partial — storage and format implemented ([`docs/runtime/memory-card.md`](docs/runtime/memory-card.md)); SIO/IRQ7 protocol and card UI not implemented |
| Full-title static recompilation | Not implemented |
| Architecture enforcement (Roslyn analyzer, Artifact Contamination Gate) | Implemented — enforced in CI |
| Avalonia UI application shell | Implemented (minimal — one diagnostic execution action; no feature UI yet) |
| Debugger / MCP / Ghidra integration | Planned |

## Architecture

```text
PSXRecompStudio
├── PSXRecompStudio        # Avalonia UI (Application layer)
├── PSXRecomp.Core         # C# Domain model + C ABI interop wrappers
├── PSXRecomp.Native       # C++ native core (CPU, memory, DMA, timers, interrupts)
├── architecture.contract.json  # Architecture SSOT, enforced by loach.ArchitectureAnalyzer (NuGet)
├── PSXRecomp.Tests
├── PSXRecompStudio.Tests  # Headless GUI tests
├── PSXRecomp.Runtime      # Planned
├── PSXRecomp.Recompiler   # Planned standalone project — IR/lowering/codegen currently live in PSXRecomp.Core/Recompiler
├── PSXRecomp.Debugger     # Planned
└── mcp/                   # Planned (MCP server)
```

The C#/Native boundary is a single C ABI — no native C++ types are exposed to C#:

```text
C# (PSXRecomp.Core, NativeInterop)
        │  P/Invoke ([LibraryImport])
        ▼
C ABI (include/psx_core.h)
        │
        ▼
C++ native core (PSXRecomp.Native)
```

Layering and dependency direction (Domain / Application / Infrastructure / Interop / Special) are the compiler-enforced Single Source of Truth in [`docs/architecture-matrix.md`](docs/architecture-matrix.md); rationale for individual decisions lives in [`docs/adr/`](docs/adr/). See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the full system design.

## Recompilation Workflow

Two paths exist today, both implemented end to end and differentially validated against the interpreter through the same, unmodified Recompiler contract.

The **synthetic path**:

```text
MIPS fixture → Recompiler IR (lowering + validation) → deterministic host C
        → host compile → bounded execution → interpreter reference execution
        → state-snapshot comparison → Differential validation → MATCH
```

The **real-ROM path** reuses the same disc/EXE analysis and the same Recompiler IR/lowering/codegen/differential stages — only the input differs. `RealRomCandidateSelector` picks a real-ROM window; the fact that it lowers cleanly and excludes every indirect jump is itself the selection criterion, so no separate real-ROM semantics implementation exists (#225):

```text
PSX title (ROM/EXE, user-supplied) → disassembly/analysis (Ghidra integration: planned)
        → function/instruction boundaries, MMIO findings, CFG/basic blocks
        → RealRomCandidateSelector: bounded, indirect-jump-free candidate window
        → ... same Recompiler IR/codegen/differential pipeline as above ...
        → Differential validation → MATCH (first function proven; #225)
```

Candidate selection is deliberately conservative: a window is accepted only when `MipsToIrLowerer` actually lowers it and no JR/JALR appears in it. General real-ROM function coverage and full-title static recompilation (every function of a real title, plus runtime and hardware integration) remain unimplemented — do not read "a first real-ROM function proven" as "general real-ROM or full-title recompilation implemented."

## Technology Stack

- **UI**: Avalonia UI / C#, MVVM
- **Runtime**: .NET 10+
- **Native Core**: C++17 / CMake / Ninja, C ABI boundary, plus a Rust coexistence substrate (Cargo, `staticlib`) linked into the same library
- **Architecture enforcement**: Roslyn Analyzer
- **Testing**: xUnit (C#), CTest (C++), `cargo test` (Rust), Avalonia headless UI tests
- **Configuration**: YAML (planned: per-title difference definitions)
- **AI integration**: MCP (planned)
- **Reverse engineering**: Ghidra (planned)
- **Version control**: Git / GitHub, with a CI-gated `main`

## Repository Structure

```text
PSXRecompStudio/
├── ARCHITECTURE.md                    # System architecture (SSOT)
├── docs/                              # Architecture / development SSOT and ADRs
├── src/
│   ├── PSXRecompStudio.slnx
│   ├── PSXRecompStudio/               # Avalonia UI
│   ├── PSXRecompStudio.Tests/         # Headless GUI tests
│   ├── PSXRecomp.Core/                # C# Domain model + P/Invoke interop
│   ├── PSXRecomp.Native/              # C++ native core (CMake project)
│   ├── architecture.contract.json     # Architecture SSOT (loach.ArchitectureAnalyzer)
│   └── PSXRecomp.Tests/               # xUnit tests (Core + Native via P/Invoke)
├── config/                            # SSOT configuration (artifact policy, CPU instruction data, README automation)
├── scripts/                           # CI and development scripts
└── skills/                            # AI development-agent skill definitions
```

`rom/` (ROM/ISO/BIOS) and build output directories (`bin/`, `obj/`, `build/`, `native/`) are excluded from version control; see [License / Legal](#license--legal) below.

## Build

### .NET (UI + C# Core)

```bash
dotnet build src/PSXRecompStudio.slnx --configuration Release
```

### Native Core (C++ and Rust)

```bash
cd src/PSXRecomp.Native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

A **Rust toolchain (`cargo`) is required**: the native library links a Rust `staticlib` built from `src/PSXRecomp.Native/rust/`, and CMake runs `cargo build` for you — there is no manual copy step. On Windows with a MinGW/GCC front-end, CMake also installs the `x86_64-pc-windows-gnu` Rust target via `rustup` at configure time.

`PSXRecomp.Core` triggers the native build and copies the resulting shared library into its own output directory as part of a normal `dotnet build`; see [`docs/development/native-library-build.md`](docs/development/native-library-build.md) for the exact artifact-naming and resolution rules per OS, and [`docs/development/rust-ffi-contract.md`](docs/development/rust-ffi-contract.md) for the FFI rules the Rust side follows.

## Test

```bash
# Rust substrate unit tests (run from the crate so the toolchain pin applies)
(cd src/PSXRecomp.Native/rust && cargo test)

# Native Core unit tests (CMake/CTest)
ctest --test-dir src/PSXRecomp.Native/build --output-on-failure

# C# test suites
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release

# Headless GUI tests (Avalonia, no display server required)
dotnet test src/PSXRecompStudio.Tests/PSXRecompStudio.Tests.csproj --configuration Release
```

CI (`.github/workflows/ci.yml`) runs an Artifact Contamination Gate, the native build/test, the .NET build/test, and the headless GUI tests as independent required jobs before a PR can merge. CI intentionally does **not** provide commercial ROM fixtures, so the real-ROM-gated analysis/recompiler/title-execution tests skip there by design; their real-ROM path is exercised only with a legally user-supplied local fixture.

## Documentation

Start with [`docs/README.md`](docs/README.md) for the full documentation index. Key entry points:

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — system architecture
- [`docs/architecture-matrix.md`](docs/architecture-matrix.md) — layering and dependency-direction SSOT, mechanically enforced by the analyzer
- [`docs/adr/`](docs/adr/) — Architecture Decision Records, including [ADR-015](docs/adr/015-production-execution-engine-ownership.md) on the production execution engine
- [`docs/cpu/`](docs/cpu/) — R3000A instruction set, pipeline, COP0, exceptions, memory model
- [`docs/architecture/gui-ux.md`](docs/architecture/gui-ux.md) — GUI/UX design
- [`docs/development/agent-guide.md`](docs/development/agent-guide.md) — bootstrap guide for AI development agents
- [`docs/development/documentation-policy.md`](docs/development/documentation-policy.md) — API documentation / docstring policy
- [`docs/development/native-library-build.md`](docs/development/native-library-build.md) — native library build/artifact rules
- [`docs/development/artifact-policy.md`](docs/development/artifact-policy.md) — repository artifact policy
- [`docs/development/readme-autoupdate.md`](docs/development/readme-autoupdate.md) — README automation design
- [`SECURITY.md`](SECURITY.md) — vulnerability reporting

## Development Workflow

`main` is protected by GitHub repository rules; direct pushes are disabled.

```text
feature branch
      ↓  commit, push
Pull Request
      ↓  CI (artifact policy, native, .NET, GUI tests)
Human review
      ↓
Merge to main
```

A CI-driven bot may also propose a minimal `README.md` update on a pull request when the PR materially changes what the README documents; see [`docs/development/readme-autoupdate.md`](docs/development/readme-autoupdate.md). It currently manages `README.md` only — `README.ja.md` is maintained manually until that automation is extended to multiple languages.

## Support

If you find this project useful, you are welcome to support its development via [GitHub Sponsors](https://github.com/sponsors/mao2009).

There are no obligations and no special perks. Sponsorship does not include ROM files, game data, or BIOS images — those are not part of this project. No promises are made about how contributions are allocated.

## License / Legal

PSXRecompStudio is released under the [MIT License](LICENSE).

This repository does not contain and will never contain copyrighted ROM, ISO, BIOS, CHD, or other PlayStation disc/firmware images. Obtain any such files legally through your own means and do not add them to version control. Build artifacts and other generated files are likewise excluded. This is enforced, not just documented: the CI **Artifact Contamination Gate** job checks every pull request against [`config/artifact-policy.json`](config/artifact-policy.json) (forbidden extensions, path segments, file-size limits, and binary content signatures) — see [`docs/development/artifact-policy.md`](docs/development/artifact-policy.md).
