# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

PSXRecompStudio is a research-oriented PlayStation 1 (PS1 / PSX) static-recompilation and reverse-engineering environment.

It can already analyze PS-X EXE / CHD inputs, lower MIPS code into a deterministic recompilation pipeline, build runnable host artifacts, and compare bounded results against an interpreter. It is still research-stage software: complete commercial PS1 title recompilation is **not yet implemented**.

[日本語 README](README.ja.md) · [Project website](https://mao2009.github.io/PSXRecompStudio/) · [Latest release](https://github.com/mao2009/PSXRecompStudio/releases/latest)

## What works today

- **Runnable recompilation pipeline:** MIPS → IR/lowering → deterministic host code → native artifact → bounded execution.
- **Differential validation:** synthetic and bounded real-input paths can be compared against interpreter execution.
- **PS-X EXE and CHD input:** CHD input is resolved through the production disc-analysis pipeline before entering the same downstream recompilation path.
- **Headless CLI:** `psxrecomp recompile` and `psxrecomp run` expose the current build/run flow.
- **Diagnostic bundles:** `psxrecomp run ... --report` can write a privacy-safe `diagnostic-report.zip`; nothing is uploaded automatically.
- **Runtime foundations:** R3000A/MIPS I execution, delay slots, COP0/exceptions, interrupt handling, memory-card storage, partial BIOS/HW models, and deterministic GPU frame snapshots are covered by tests.
- **Native coexistence:** the native core uses C++ and incrementally migrated Rust components behind the same stable C ABI.
- **Cross-platform validation:** CI and release workflows cover Linux, Windows, and macOS.

The detailed implementation status and evidence live in the maintained documentation and tests rather than in this front page.

## Current limitations

- Full commercial-title static recompilation is not implemented.
- Real-title execution remains bounded and research-oriented rather than a compatibility claim.
- BIOS HLE coverage is partial.
- GPU integration is incomplete at the product level, and SPU/CD-ROM/MDEC/GTE support remains incomplete.
- The generated-host path is not yet the general production execution backend for complete titles.

## Quick start

### Build and test

```bash
dotnet build src/PSXRecompStudio.slnx --configuration Release
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release
```

Native/Rust build details are documented in [Native library build](docs/development/native-library-build.md).

### Try the CLI

Using a legally obtained PS-X EXE or CHD:

```bash
psxrecomp recompile <input.exe|input.chd> --output out
psxrecomp run <input.exe|input.chd> --output out
```

Optional diagnostic bundle:

```bash
psxrecomp run <input.exe|input.chd> --output out --report
```

See the [headless CLI reference](docs/development/headless-cli.md) for JSON output, exit codes, report contents, and input behavior.

## Architecture

```text
PS-X EXE / CHD
      ↓
disc / executable analysis
      ↓
R3000A / MIPS I
      ↓
IR + lowering
      ↓
deterministic generated host artifact
      ↓
bounded execution + differential validation
```

The application core is C#/.NET. Native functionality crosses a stable C ABI into a mixed C++/Rust native library; Rust migration is incremental and does not change the public native boundary.

For the full design, see [ARCHITECTURE.md](ARCHITECTURE.md), the [architecture matrix](docs/architecture-matrix.md), the [ADRs](docs/adr/), and the [Rust FFI contract](docs/development/rust-ffi-contract.md).

## Evidence

The repository keeps executable evidence close to the implementation:

- [Synthetic differential recompiler tests](src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs)
- [Bounded real-ROM recompiler tests](src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs)
- [Runnable recompiled-artifact E2E tests](src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs)
- [Real-input E2E tests](src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs)
- [Persona E2E status](docs/v0.1.0/persona-e2e-status.md)

Real-ROM tests require legally obtained user-supplied input and skip explicitly when no fixture is available.

## Roadmap

Near-term work is focused on:

1. expanding BIOS/runtime coverage required by real-title execution;
2. broadening recompilation coverage while keeping differential validation as the correctness gate;
3. moving more native implementation behind the existing Rust/C++ ABI boundary;
4. progressing from bounded proofs toward a production generated-host execution backend.

The issue tracker remains the source for task-level planning; the README intentionally does not mirror the backlog.

## Documentation

Start with [docs/README.md](docs/README.md). Useful entry points include:

- [System architecture](ARCHITECTURE.md)
- [Architecture/dependency SSOT](docs/architecture-matrix.md)
- [CPU/R3000A documentation](docs/cpu/)
- [Headless CLI](docs/development/headless-cli.md)
- [Native library build](docs/development/native-library-build.md)
- [Rust FFI contract](docs/development/rust-ffi-contract.md)
- [Real-ROM analysis](docs/development/real-rom-analysis.md)
- [Diagnostics and recovery](docs/architecture/diagnostics.md)
- [Memory-card runtime](docs/runtime/memory-card.md)
- [Repository artifact policy](docs/development/artifact-policy.md)

## Development

`main` is CI-gated. Changes are developed on branches, validated by the repository test/build checks, reviewed, and then merged through pull requests.

Architecture and dependency rules are enforced at build time by `loach.ArchitectureAnalyzer`; repository artifact policy is also enforced in CI.

For project-specific development guidance, see [docs/development/agent-guide.md](docs/development/agent-guide.md).

## Support

If you find the project useful, you can support it through [GitHub Sponsors](https://github.com/sponsors/mao2009). Sponsorship does not include ROMs, BIOS images, game data, compatibility promises, or other special project assets.

## License / legal

PSXRecompStudio is released under the [MIT License](LICENSE).

This repository does not include copyrighted ROM, ISO, CHD, BIOS, firmware, or commercial game assets. User-supplied inputs must be obtained and used legally and must not be committed to the repository. Generated/build artifacts are also governed by the repository's [artifact policy](docs/development/artifact-policy.md).
