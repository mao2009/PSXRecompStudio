# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

An integrated development environment for analyzing, understanding, and recompiling PlayStation 1 (PSX) titles into natively executable programs.

*[日本語版 README はこちら / Japanese README](README.ja.md)*

## What is PSXRecompStudio?

PSXRecompStudio is a from-scratch development environment for working with PS1 software: disassembling and analyzing titles, modeling the R3000A CPU with byte-for-byte fidelity, and — eventually — statically recompiling title code into native programs that run directly on modern Windows, Linux, and macOS without emulation.

It combines an Avalonia-based desktop UI, a C# domain/application core, and a C++ native core connected through a stable C ABI, with AI development agents as an optional, evidence-first assistance layer rather than the product itself.

## Why PSXRecompStudio?

- **SSOT-driven architecture.** Architecture, CPU semantics, and development process are documented as living Single Sources of Truth in [`docs/`](docs/) and [Architecture Decision Records](docs/adr/), not left to tribal knowledge.
- **Mechanically enforced boundaries.** A Roslyn analyzer ([`PSXRecomp.Analyzer`](src/PSXRecomp.Analyzer)) fails the build on layering, dependency-direction, and forbidden-API violations — the architecture matrix is a compiler-checked contract, not just a diagram.
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
| Recompiler (IR contract, state model & host codegen) | Implemented (in PSXRecomp.Core; Phase 3A: GPR host codegen) |
| Recompiler (full) | Planned |
| Debugger | Planned |
| MCP / AI integration | Planned |
| Ghidra integration | Planned |

**CPU execution foundation.** The CPU execution foundation is now functional: instruction decoding, memory-path execution (including KSEG translation), branch/load delay-slot behavior, COP0 and exception handling, hardware interrupt sampling, and deterministic execution tracing all work together to execute a minimal MIPS program end to end. This is a vertical slice through the CPU, not a complete emulator — see [`docs/cpu/`](docs/cpu/) for the detailed specification.

**Recompiler.** PSXRecompStudio's ultimate goal is static recompilation. The backend-agnostic IR model, shared state contract, and deterministic host C source generation from GPR IR (Phase 3A) are now implemented in `PSXRecomp.Core.Recompiler`, but there is no standalone `PSXRecomp.Recompiler` project and no full recompilation yet — memory access, branch/control-flow generation, and the executable vertical slice are separate milestones. The CPU/decoder work above is foundational to it, not a substitute for it.

## Core Capabilities

- Architecture rules (layering, dependency direction, forbidden APIs, P/Invoke location) enforced at compile time, not just documented.
- R3000A/MIPS I instruction decoding and domain modeling, independently testable from the execution engine.
- A native CPU + memory bus that executes real instruction sequences with correct delay-slot and exception semantics.
- Deterministic, replayable execution traces (Golden Trace) intended to validate future recompiler backends against the interpreter.
- A C# ⇄ C++ interop boundary (C ABI + P/Invoke) that keeps native implementation details out of the managed layer.

## Architecture

```text
PSXRecompStudio
├── PSXRecompStudio        # Avalonia UI (Application layer)
├── PSXRecomp.Core         # C# Domain model + C ABI interop wrappers
├── PSXRecomp.Native       # C++ native core (CPU, memory, DMA, timers, interrupts)
├── PSXRecomp.Analyzer     # Roslyn architecture-enforcement analyzer
├── PSXRecomp.Analyzer.Tests
├── PSXRecomp.Tests
├── PSXRecompStudio.Tests  # Headless GUI tests
├── PSXRecomp.Runtime      # Planned
├── PSXRecomp.Recompiler   # Planned
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

The intended end-to-end pipeline — analysis and disassembly today, static recompilation ahead:

```text
PSX title (ROM/EXE, user-supplied)
        ↓  disassembly / analysis (Ghidra integration: planned)
Function/instruction boundaries, MMIO findings
        ↓  R3000A domain model + decoder (implemented)
Typed instruction representation
        ↓  static recompilation (planned — PSXRecomp.Recompiler)
Native code (x86-64 / ARM64)
        ↓
Native executable, validated against the interpreter via Golden Trace
```

Only the analysis/decoding stages are implemented today; recompilation itself is planned. Do not read "CPU execution foundation implemented" as "recompiler implemented" — they are separate milestones.

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
│   ├── PSXRecomp.Analyzer/            # Roslyn architecture analyzer
│   ├── PSXRecomp.Analyzer.Tests/
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
dotnet test src/PSXRecomp.Analyzer.Tests/PSXRecomp.Analyzer.Tests.csproj --configuration Release

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

## License / Legal

PSXRecompStudio is released under the [MIT License](LICENSE).

This repository does not contain and will never contain copyrighted ROM, ISO, BIOS, CHD, or other PlayStation disc/firmware images. Obtain any such files legally through your own means and do not add them to version control. Build artifacts and other generated files are likewise excluded. This is enforced, not just documented: the CI **Artifact Contamination Gate** job checks every pull request against [`config/artifact-policy.json`](config/artifact-policy.json) (forbidden extensions, path segments, file-size limits, and binary content signatures) — see [`docs/development/artifact-policy.md`](docs/development/artifact-policy.md).
