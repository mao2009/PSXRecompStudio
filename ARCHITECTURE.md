# Architecture

**Status:** Stable

**Authority:** Top-level Architecture SSOT

**Document Type:** Architecture Overview

This document defines the repository-wide system architecture for PSXRecompStudio. More-specific subsystem SSOTs are authoritative within their own scope. The managed layer/dependency model is defined by [docs/architecture-matrix.md](docs/architecture-matrix.md), while [docs/architecture/README.md](docs/architecture/README.md) serves as the architecture index.

## Project Goals

An integrated development environment for analyzing and recompiling PlayStation 1 (PSX) titles so they can run natively on Windows, Linux, and macOS.

Future goals:

- Recompile PSX titles
- Define title-specific differences in YAML
- Disassembly, analysis, and debugging
- Retrieve and manipulate game state from external AI through an MCP server
- Provide a general-purpose foundation that rights holders can use for official ports and preservation

## Component Structure

```text
src/
├── PSXRecompStudio/           # Avalonia UI application
├── PSXRecomp.Core/            # C# Core: P/Invoke bindings + wrappers
├── PSXRecomp.Native/          # C++ Core: PSX emulation core (C ABI)
├── architecture.contract.json # SSOT for enforced architecture rules (read by loach.ArchitectureAnalyzer)
├── PSXRecomp.Runtime/         # Future: PSX runtime management
├── PSXRecomp.Recompiler/      # Future: recompiler
├── PSXRecomp.Debugger/        # Future: debugger
├── PSXRecomp.Infrastructure/  # Future: shared infrastructure
└── PSXRecomp.Tests/           # xUnit tests
mcp/                           # MCP Server (Node.js / TypeScript)
```

## Architecture Enforcement

Layer attributes (`[Domain]`, `[Application]`, `[Infrastructure]`, etc.) and dependency directions are enforced at compile time by the `loach.ArchitectureAnalyzer` NuGet package (AARC002-007, all Errors). Rule data lives in `src/architecture.contract.json`, while `.editorconfig` owns gate severity. The former in-repository `PSXRecomp.Analyzer` (PSXR001-006) was removed in #294.

- Managed architecture subsystem SSOT: [docs/architecture-matrix.md](docs/architecture-matrix.md)
- Design decision: [docs/adr/006-architecture-analyzer-enforcement.md](docs/adr/006-architecture-analyzer-enforcement.md) (migration added in #295)

Every class requires a layer attribute (marker namespace `PSXRecomp.Architecture.*`), except generated code and nested classes whose enclosing type is already attributed. Violations are build errors and also fail CI.

## C# / Native Responsibility Split

### C# side (PSXRecompStudio, PSXRecomp.Core)

- Avalonia UI / MVVM pattern
- Project management (YAML)
- Logging
- Debugger UI
- P/Invoke integration with the Native Core
- Resource management (`IDisposable`)

### Native side (PSXRecomp.Native)

- PSX CPU (R3000A) emulation
- PSX Memory (RAM, BIOS, Hardware Registers)
- PSX Hardware (GPU, SPU, DMA, CD-ROM, Timers, Interrupt Controller — see the Hardware section below for implementation status)
- Performance-critical computation in general

### Boundary

Calls from C# to Native go through the **C ABI only**.

```text
C# (PSXRecomp.Core)
  ↓ P/Invoke
C ABI (psx_core.h)
  ↓
C++ (PSXRecomp.Native)
```

C++ classes are not exposed directly to C#.

## C ABI

### Design Policy

- Hide state behind an opaque pointer (`PSXCore*`)
- Export with C linkage
- Return state through return values rather than error codes
- Make memory ownership explicit through Create/Destroy

### Minimal API

```c
// Lifecycle
PSXCore* PSXCore_Create(void);
void     PSXCore_Destroy(PSXCore* core);
void     PSXCore_Reset(PSXCore* core);

// CPU Registers
uint32_t PSXCore_GetGPR(PSXCore* core, int index);
void     PSXCore_SetGPR(PSXCore* core, int index, uint32_t value);
uint32_t PSXCore_GetPC(PSXCore* core);
void     PSXCore_SetPC(PSXCore* core, uint32_t value);
uint32_t PSXCore_GetHI(PSXCore* core);
void     PSXCore_SetHI(PSXCore* core, uint32_t value);
uint32_t PSXCore_GetLO(PSXCore* core);
void     PSXCore_SetLO(PSXCore* core, uint32_t value);

// Memory
uint8_t* PSXCore_GetRAM(PSXCore* core);
uint32_t PSXCore_GetRAMSize(void);
```

## PSX Core

### CPU

- R3000A-compatible (MIPS I)
- 32 GPRs (General Purpose Registers)
- PC (Program Counter)
- HI / LO (multiply/divide result registers)
- Basic abstraction of CP0 (System Control Coprocessor)
- Hardware interrupt sampling: on each Step/Run, reflect the Interrupt Controller's aggregate pending state into CAUSE.IP2 (bit 10); see [docs/cpu/exceptions.md](docs/cpu/exceptions.md)

### Memory

- PSX RAM: 2 MB
- BIOS: 512 KB
- Hardware Register Space

### Hardware (Implementation Status)

Status reflects the current repository state (implementation, tests, and CI), not open Issues or planned/design intent. This uses the same basis as README Current Status.

| Component | Status |
|---------------|------|
| Interrupt Controller | Implemented |
| CPU interrupt integration | Implemented |
| DMA | Partially implemented (register-level model + IRQ + C# MMIO adapter + tests; transfer engine / MemoryBus wiring on the native execution path is not implemented) |
| Timers | Partially implemented (register-level model + tick + IRQ + C# MMIO adapter + tests; GPU-derived dotclock / HBlank signal wiring is not implemented) |
| GPU | Planned (interface contract only) |
| SPU | Planned (interface contract only) |
| CD-ROM | Planned (interface contract only) |
| MDEC | Planned (interface contract only) |
| GTE | Planned (interface contract only) |

The Interrupt Controller has its register model, C ABI, C# adapter, and native tests implemented, as well as CPU interrupt integration that reflects the aggregate pending state into CAUSE.IP2 on every CPU Step/Run; see [docs/cpu/exceptions.md](docs/cpu/exceptions.md). DMA / Timers are implemented through their register-level models, with C# `MemoryBus` MMIO routing adapters and native tests, while complete wiring from the native execution path (`PSXMemory`'s `hw_regs` region) to each controller remains in progress. GPU / SPU / CD-ROM / MDEC / GTE have interface contracts in `PSXRecomp.Core/Runtime` only; no native implementations exist.

## Runtime

The PSX runtime manages BIOS loading, EXE loading, memory mapping, and the I/O loop.
Normal execution targets BIOS-less operation, and BIOS calls pass through the
`IBiosRuntime` boundary in `PSXRecomp.Core.Runtime`. `BiosCallIdentity` carries
the A0/B0/C0 family, function number, guest PC, and arguments, while
`BiosServiceResult` represents supported / unsupported outcomes structurally.
Unimplemented calls return explicit diagnostics such as
`BIOS_HLE_UNSUPPORTED_CALL` rather than silently succeeding. Title-specific BIOS
workarounds must not be added to the Recompiler or CPU core, and a real BIOS
image must not be distributed or made mandatory. The HLE registry currently
wires A0:3C `putchar`, A0:3E `puts`, and the B0:3F alias of `puts` (selected by
real-ROM evidence — a title observed calling it through the B0 jump table), and
implements the full documented behavior of all three. A0:3E and B0:3F dispatch
to the same `PutsService` implementation rather than a per-family copy of it;
each diagnostic still names the identity that was actually invoked. `putchar`
writes the low byte of the character argument to the `IRuntimeOutputSink`
injected into `BiosHleRuntime` and returns that same byte, so `Supported` here
means the host-visible TTY effect too, not just the return-register contract.
The sink is a required constructor dependency — a missing sink is a
construction error, never a silently discarded side effect — and the Domain
layer reaches host output only through that boundary, never `System.Console`
(ADR-014). An argument shape the ABI does not accept is still rejected with
`BIOS_HLE_INVALID_ARGUMENTS` and writes nothing to the sink. A0:3E `puts`
reads its NUL-terminated string argument through the `IGuestMemoryReader` also
injected into `BiosHleRuntime`, writes those bytes to the same sink, and
returns the incoming string pointer; its B0:3F alias behaves identically.
Because its argument is a guest-memory
pointer rather than a scalar, the read is what makes `Supported` honest here:
the string is collected into a bounded buffer first and emitted only once it is
proven fully readable, so an invalid or unmapped pointer reports
`BIOS_HLE_UNSUPPORTED_STATE` and writes nothing rather than producing partial
output. Every other function number reports `BIOS_HLE_UNSUPPORTED_CALL`
(ADR-014).

Analysis and Runtime hold **two different facts about the same call**, and the
separation is deliberate. `BiosCallRecognizer`
(`PSXRecomp.Core.DiscImage`) recognizes A0/B0/C0 jump-table call sites in an
analyzed executable and records what the ROM *requests*; the HLE registry records
what the Runtime can *provide*. Analysis therefore never filters its evidence to
the currently registered services — doing so would make a title's BIOS surface
appear to shrink and grow with implementation progress, and would destroy the
evidence loop Issue #279 requires. The two share only `BiosCallFamily` and the
verified identity table `BiosCallNames`, never the registry: a function number
whose identity this repository has not verified is recorded and counted without a
name rather than with a guessed one. Recognized sites, including those whose
function number is not statically resolvable, are persisted in the deterministic
analysis artifact (`report.json`, `biosCalls`; see
[docs/development/real-rom-analysis-artifacts.md](docs/development/real-rom-analysis-artifacts.md)).
The evidence covers whatever instruction window the analysis decoded, so a report
produced with the pipeline's default entry-point window records only the sites
within it, not the executable's whole BIOS surface.

## Recompiler

- `PSXRecomp.Core.Recompiler` owns the backend-agnostic IR and shared
  interpreter/recompiled state contract (Issue #206).
- IR values are fixed-width 32-bit guest values; blocks have explicit exits and
  deterministic canonical serialization.
- Lowering, host generation, differential comparison, and the executable
  vertical slice remain separate responsibilities of Issues #207, #208, #211,
  and #209. Memory and control-flow expansion is deferred until the GPR-only
  gate is green.
- Real-ROM function candidates are a projection over the existing disc/EXE
  analysis output, selected only when they already lower under the contract
  above — no second, real-ROM-specific semantics implementation exists
  (ADR-013, Issue #225).

## Debugger (Future)

- Breakpoints
- Step execution
- Register / memory views
- Disassembly view
- GPU rendering view

## MCP (Future)

- Model Context Protocol server
- Retrieve and manipulate game state
- AI-driven play and automated testing
- Node.js / TypeScript implementation

## YAML

YAML is used to define title-specific differences:

- Memory mapping differences
- Instruction-specific special handling
- GPU register differences
- Region definitions

## Ghidra (Future)

- Import disassembly results
- Use function-analysis results
- Ghidra script integration

## Host Platform

### First-Class Support

| OS | Architecture | Status |
|----|---------------|------|
| Windows 10/11 | x64 | Future support |
| Linux | x64 | Development environment |
| macOS 12+ | x64 | Future support |
| macOS 12+ | ARM64 | Future support |

### Multi-OS Policy

- Separate PSX-specific processing from Host OS-specific processing
- Do not assume Wine / Proton
- Cross-build the Native Core with CMake
- Use .NET cross-platform support for C#

## Why C++

| Requirement | Decision |
|------|------|
| Performance | PSX CPU emulation is a tight loop; C++ optimizes well |
| Control | Direct control over memory layout and instruction execution is required |
| Existing knowledge | Many PSX emulators are written in C/C++ (DuckStation, PCSX-Redux, etc.) |
| C ABI | C++ can provide a C ABI with `extern "C"` |
| Future potential | Suitable for future JIT recompiler implementation |
| Clang | The current environment uses GCC only, but C++ itself is standard |

Rust was also considered, but C++ was chosen as the primary option because of existing PSX-emulator knowledge and its compatibility with C# P/Invoke.

## Build Structure

```text
Native Core:    CMake + Ninja → .so / .dll / .dylib
C# Core:        dotnet build → .dll
UI:             dotnet build → executable
Tests:          dotnet test (C#) + ctest (C++)
```

## Future Components

```text
PSXRecompStudio       → UI (Avalonia)
PSXRecomp.Core        → P/Invoke + wrappers
PSXRecomp.Native      → PSX emulation core
PSXRecomp.Runtime     → runtime management
PSXRecomp.Recompiler  → recompiler
PSXRecomp.Debugger    → debugger
PSXRecomp.Infrastructure → shared infrastructure
mcp/                  → MCP Server
```
