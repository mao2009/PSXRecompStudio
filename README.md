# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

An open-source PlayStation 1 (PS1 / PSX) development environment for static recompilation, binary analysis, reverse engineering, MIPS code analysis, and native porting.

*[日本語版 README はこちら / Japanese README](README.ja.md)*

## What is PSXRecompStudio?

PSXRecompStudio is a from-scratch development environment for analyzing and reverse engineering PlayStation 1 software: disassembling PS-X executables, analyzing R3000A / MIPS I code, modeling CPU behavior with byte-for-byte fidelity, and — eventually — statically recompiling title code into native programs that run directly on modern Windows, Linux, and macOS without emulation.

It combines an Avalonia-based desktop UI, a C# domain/application core, and a C++ native core connected through a stable C ABI, with AI development agents as an optional, evidence-first assistance layer rather than the product itself.

## Why PSXRecompStudio?

- **SSOT-driven architecture.** Architecture, CPU semantics, and development process are documented as living Single Sources of Truth in [`docs/`](docs/) and [Architecture Decision Records](docs/adr/), not left to tribal knowledge.
- **Mechanically enforced boundaries.** `loach.ArchitectureAnalyzer`, configured by [`src/architecture.contract.json`](src/architecture.contract.json), fails the build on layering, dependency-direction, and forbidden-API violations — the architecture matrix is a compiler-checked contract, not just a diagram.
- **Deterministic CPU foundation.** The R3000A model is validated with a per-instruction Golden Trace: every register write is captured in retirement order and replayed to catch divergence, laying the groundwork for comparing future recompiler backends against the interpreter.
- **A stable C#/Native boundary.** All communication with the native core crosses a single C ABI (`psx_core.h`) via P/Invoke — no C++ types leak into C#.
- **Evidence-first, human-in-the-loop AI collaboration.** AI development agents are a replaceable tool, not the product's identity: user-driven analysis, verifiable evidence, and human review remain central, and the workflow is agent-agnostic (Claude Code, OpenCode, Codex, or others).

## Current Status

Status reflects the current repository state (implementation, tests, and CI), not open issues or design intent.

| Area | Status |
|---|---|
| Architecture foundation (layers, C ABI boundary, ADRs) | Implemented |
| Avalonia UI application shell | Implemented (minimal — no feature UI yet) |
| C# Core / Native Core boundary | Implemented |
| C ABI / P/Invoke | Implemented |
| Architecture Analyzer (Roslyn) | Implemented — enforced in CI |
| Analyzer test suite | Implemented |
| R3000A instruction domain model | Implemented |
| R3000A decoder | Implemented |
| MemoryBus / KSEG0 / KSEG1 translation | Implemented |
| Branch and load delay-slot modeling | Implemented |
| COP0 / exception handling | Implemented |
| Interrupt Controller | Implemented |
| CPU interrupt integration | Implemented |
| Timers / DMA controller | Partially implemented (register-level native models exist; full memory-bus wiring in progress) |
| Minimal MIPS program execution path | Implemented |
| Golden Trace (deterministic execution tracing) | Implemented |
| Disc image analysis (CHD → ISO 9660 → PS-X EXE → MIPS analysis, basic blocks / CFG) | Implemented |
| GPU / SPU / CD-ROM / MDEC / GTE | Planned (interface contracts only) |
| Runtime (BIOS/EXE loading, I/O loop) | Planned |
| Synthetic MIPS recompiler vertical slice (IR/lowering, memory, control flow, host codegen, differential validation) | Implemented and differentially validated |
| Real-ROM function recompilation | Next milestone — not yet complete (#225) |
| Full-title static recompilation | Not implemented |
| Debugger | Planned |
| MCP / AI integration | Planned |
| Ghidra integration | Planned |

**CPU execution foundation.** The CPU execution foundation is now functional: instruction decoding, memory-path execution (including KSEG translation), branch/load delay-slot behavior, COP0 and exception handling, hardware interrupt sampling, and deterministic execution tracing all work together to execute a minimal MIPS program end to end. This is a vertical slice through the CPU, not a complete emulator — see [`docs/cpu/`](docs/cpu/) for the detailed specification.

**Recompiler.** PSXRecompStudio's ultimate goal is static recompilation. A backend-agnostic Recompiler IR and shared state contract, MIPS→IR lowering, deterministic host C generation, a memory backend (load/store at every width, unaligned access, load-delay semantics), a control-flow backend (branches, jumps, links, delay slots, bounded/budgeted loops), and an interpreter-vs-recompiled differential validator are all implemented in `PSXRecomp.Core.Recompiler` (there is no standalone `PSXRecomp.Recompiler` project yet — see [Repository Structure](#repository-structure)). Together these prove an executable, differentially-validated **synthetic MIPS fixture** vertical slice: MIPS → IR → generated host C → build → bounded execution → interpreter diff → match (#207, #208, #209, #211; re-verified end-to-end by the integration smoke test, #266).

This is not yet static recompilation of real game code: real-ROM-derived functions are analyzed (see [Disc image analysis](#current-status) above) but not yet wired into the Recompiler execution/differential path — connecting the first real-ROM function is the next milestone (#225). Full-title recompilation, complete runtime integration, and complete hardware support remain unimplemented. The CPU/decoder work above is foundational to it, not a substitute for it.

## Core Capabilities

- Architecture rules (layering, dependency direction, forbidden APIs, P/Invoke location) enforced at compile time, not just documented.
- R3000A/MIPS I instruction decoding and domain modeling, independently testable from the execution engine.
- A native CPU + memory bus that executes real instruction sequences with correct delay-slot and exception semantics.
- Deterministic, replayable execution traces (Golden Trace) intended to validate future recompiler backends against the interpreter.
- A deterministic MIPS→IR→host-C Recompiler pipeline with bounded execution and interpreter differential validation, proven end-to-end on a synthetic fixture (not yet on real-ROM code).
- A C# ⇄ C++ interop boundary (C ABI + P/Invoke) that keeps native implementation details out of the managed layer.

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

Two paths exist today. The **synthetic path** is implemented end to end and
differentially validated against the interpreter:

```text
MIPS fixture
        ↓  decode / analysis
Recompiler IR (lowering + validation)
        ↓  deterministic host C generation
Generated host C
        ↓  host compile
Bounded execution
        ↓
Interpreter reference execution
        ↓  State Snapshot / checkpoint comparison
Differential validation → MATCH
```

The **real-ROM path** reuses the same disc/EXE analysis (function/instruction
boundaries, CFG/basic blocks — implemented) but is not yet connected to the
Recompiler execution/differential path above; connecting the first real-ROM
function is the next milestone, tracked in #225:

```text
PSX title (ROM/EXE, user-supplied)
        ↓  disassembly / analysis (implemented; Ghidra integration: planned)
Function/instruction boundaries, MMIO findings, CFG/basic blocks
        ↓  candidate real-ROM function
Recompiler execution / differential validation   ← next milestone (#225)
        ↓
Native executable, validated against the interpreter
```

Full-title static recompilation (every function of a real title, plus runtime
and hardware integration) is not implemented. Do not read "synthetic
Recompiler vertical slice implemented" as "real-ROM or full-title
recompilation implemented" — they are separate milestones.

## Technology Stack

- **UI**: Avalonia UI / C#, MVVM
- **Runtime**: .NET 10+
- **Native Core**: C++17 / CMake / Ninja, C ABI boundary
- **Architecture enforcement**: Roslyn Analyzer
- **Testing**: xUnit (C#), CTest (C++), Avalonia headless UI tests
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

### Native Core (C++)

```bash
cd src/PSXRecomp.Native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

`PSXRecomp.Core` triggers the native build and copies the resulting shared library into its own output directory as part of a normal `dotnet build`; see [`docs/development/native-library-build.md`](docs/development/native-library-build.md) for the exact artifact-naming and resolution rules per OS.

## Test

```bash
# Native Core unit tests (CMake/CTest)
ctest --test-dir src/PSXRecomp.Native/build --output-on-failure

# C# test suites
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release

# Headless GUI tests (Avalonia, no display server required)
dotnet test src/PSXRecompStudio.Tests/PSXRecompStudio.Tests.csproj --configuration Release
```

CI (`.github/workflows/ci.yml`) runs an Artifact Contamination Gate, the native build/test, the .NET build/test, and the headless GUI tests as independent required jobs before a PR can merge.

## Documentation

Start with [`docs/README.md`](docs/README.md) for the full documentation index. Key entry points:

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — system architecture
- [`docs/architecture-matrix.md`](docs/architecture-matrix.md) — layering and dependency-direction SSOT, mechanically enforced by the analyzer
- [`docs/adr/`](docs/adr/) — Architecture Decision Records
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
