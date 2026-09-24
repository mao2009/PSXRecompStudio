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
├── PSXRecomp.Native/          # Native Core: PSX emulation core (C ABI); C++ plus the Rust substrate in rust/
├── architecture.contract.json # SSOT for enforced architecture rules (read by loach.ArchitectureAnalyzer)
├── PSXRecomp.Runtime/         # Future: PSX runtime management
├── PSXRecomp.Recompiler/      # Future: recompiler
├── PSXRecomp.Debugger/        # Future: debugger
├── PSXRecomp.Infrastructure/  # Managed host adapters (compiler/process/file I/O; #458)
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
C++ (PSXRecomp.Native) ── linked with ──> Rust (PSXRecomp.Native/rust, staticlib)
```

C++ classes are not exposed directly to C#.

Since Issue #473 the native library also links a Rust `staticlib`, so both
languages ship inside the one artifact managed code already loads. The
boundary itself is unchanged: Rust reaches C# through the same C ABI header and
the same `[LibraryImport("PSXRecomp.Native")]` bindings, and no Rust type is
visible above it. The interrupt controller (I_STAT/I_MASK, Issue #484), the
timer controller (Root Counters, Issue #486) and the DMA controller registers
(Issue #488) are implemented in Rust behind the unchanged `PSXCore_*Interrupt*`
/ `PSXCore_*Timer*` / `PSXCore_*Dma*` functions; all remaining subsystems are
C++. When those devices advance during execution is decided on the managed side
by `DeviceScheduler` (Issue #442, see
[Device Scheduling](docs/runtime/architecture.md#device-scheduling-issue-442)). See [ADR-023](docs/adr/023-rust-native-coexistence-substrate.md) and the

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
| GPU | Partially implemented (register/VRAM model + flat/Gouraud rectangle/triangle rasterization + deterministic frame snapshot, C# only; texture mapping and quads are not implemented; not wired into `DeviceScheduler` or DMA2) |
| SPU | Planned (interface contract only) |
| CD-ROM | Planned (interface contract only) |
| MDEC | Planned (interface contract only) |
| GTE | Planned (interface contract only) |

The Interrupt Controller has its register model, C ABI, C# adapter, and native tests implemented, as well as CPU interrupt integration that reflects the aggregate pending state into CAUSE.IP2 on every CPU Step/Run; see [docs/cpu/exceptions.md](docs/cpu/exceptions.md). DMA / Timers are implemented through their register-level models, with C# `MemoryBus` MMIO routing adapters and native tests, while complete wiring from the native execution path (`PSXMemory`'s `hw_regs` region) to each controller remains in progress. GPU has a managed C# register/VRAM/rasterization/frame-snapshot model, but is not yet wired into `DeviceScheduler` or DMA2 and has no native implementation. SPU / CD-ROM / MDEC / GTE have interface contracts in `PSXRecomp.Core/Runtime` only; no native implementations exist.

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
wires A0:39 `InitHeap` (the identity real-ROM analysis observed most broadly —
5 of 5 locally available executables), A0:3C `putchar`, A0:3E `puts`, the B0:3F
alias of `puts` (selected by real-ROM evidence — a title observed calling it
through the B0 jump table), and B0:56 `GetC0Table` / B0:57 `GetB0Table`, and
implements the full documented behavior of all six. `InitHeap` takes two scalar
arguments (addr, size), has no documented return value, and this Runtime
registers no malloc/realloc/calloc/free/qsort service to consume its heap
bookkeeping, so its complete guest-observable contract is argument-shape
validation; `$v0` is left untouched (ADR-014). A0:3E and B0:3F dispatch
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

Both execution paths reach the `IBiosRuntime` boundary through one shared
statement of what a BIOS call means, `BiosVectorDispatch` (Issue #362): a guest
transfer to an A0/B0/C0 trampoline vector builds the identity from the PS1 ABI
(`$t1` selects the function, `$a0`–`$a3` carry up to the registered service's own
argument count), and the Runtime's answer either moves the PC to a patched
jump-table target, returns to `$ra` — writing `$v0` only when the service
produced a return value, leaving it untouched otherwise — or stops the run with
an explicit diagnostic; never a silent success. The interpreter applies that
outcome to a live core; the generated host is offered its unresolved PCs through
a generic control-transfer hook in the emitted state struct, so no BIOS address,
function number, or service name ever enters the Recompiler IR or the generated
C (ADR-014). Because the generated host can only enter a block it already
compiled, a patched target outside its static block table is reported rather
than jumped to; compiling a target discovered at run time is dynamic overlay
recompilation (Issue #249) and is out of scope.

### Input

Input follows a host-independent contract split into a console-agnostic host
layer and per-console modules (Issue #47). The host layer
(`PSXRecomp.Core.Runtime.Input`) maps abstract `PhysicalInputId`s to a console's
logical buttons through `InputBindingMap<TButton>` and captures the result as an
immutable `ControllerInputSnapshot<TButton>`, carrying each port's attached
device kind in `PhysicalControllerState<TDeviceKind>`; it contains no console
button, device-kind, or protocol semantics. The PS1 console module
(`PSXRecomp.Core.Runtime.Input.Ps1`) owns `Ps1Button`, `Ps1ControllerDeviceKind`,
`Ps1ControllerState`, `Ps1ControllerPortState`, and `Ps1ControllerSnapshot`,
whose `Resolve` applies PS1 device policy: the standard digital pad is the only
supported kind today, and recognized-but-unimplemented devices (DualShock,
analog, NeGcon, mouse, light guns, and other dedicated controllers) are carried
through explicitly as unsupported rather than treated as digital pads. Keyboard /
gamepad / touch acquisition and the PS1 SIO/controller protocol remain out of
scope and connect to this contract later; other consoles add their own module
without changing the host layer. See [docs/runtime/input.md](docs/runtime/input.md).

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
- A Load/Store IR operation carries an explicit `RecompilerIrMemoryEffectKind`
  (`Unknown`/`Ordinary`/`Device`), fail-closed to `Unknown` unless the address
  is provably RAM/scratchpad/BIOS (`Ordinary`) or a hardware register
  (`Device`); indirect control flow, the BIOS/runtime transfer boundary, and
  unsupported/exception-producing opcodes were already explicit before this
  addition (ADR-020, Issue #411). The six observable-effect categories, their
  termination/diagnostic identifiers, and the optimization contract are
  tabulated in `docs/development/recompiler-ir-contract.md`.

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

That choice is being revisited incrementally rather than reversed (Issue #471). A Rust `staticlib` now links into the same native library (Issue #473, [ADR-023](docs/adr/023-rust-native-coexistence-substrate.md)), so individual subsystems migrate one at a time behind the unchanged C ABI: the interrupt controller (Issue #484), the timer controller (Issue #486) and the DMA controller registers (Issue #488) are now implemented in Rust, and removing C++ is not a goal.

## Build Structure

```text
Native Core:    CMake + Ninja (+ cargo, linked in) → .so / .dll / .dylib
C# Core:        dotnet build → .dll
UI:             dotnet build → executable
Tests:          dotnet test (C#) + ctest (C++) + cargo test (Rust)
```

## Future Components

```text
PSXRecompStudio       → UI (Avalonia)
PSXRecomp.Core        → P/Invoke + wrappers
PSXRecomp.Native      → PSX emulation core
PSXRecomp.Runtime     → runtime management
PSXRecomp.Recompiler  → recompiler
PSXRecomp.Debugger    → debugger
mcp/                  → MCP Server
```
