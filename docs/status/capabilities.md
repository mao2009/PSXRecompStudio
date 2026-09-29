# Capability and Verification Matrix

**Document type:** Maintained status document

**Authority:** Derived public capability summary

**Last reviewed:** 2026-09-28

**Repository baseline:** `main@8df3139` (PR #609 merge commit)

**Synthetic evidence basis:** linked repository-owned executable tests, maintained E2E gates, and required CI checks at the review baseline

**Real-input evidence basis:** legal user-supplied local fixtures recorded through maintained status documents such as [Persona E2E Pipeline Status](../v0.1.0/persona-e2e-status.md)

**Related Issues:** #205 (Recompiler / Runtime roadmap), #351 (Persona title-screen gate), #605 (initial matrix), #610 (provenance/evidence vocabulary)

## Purpose

This page is the human-readable evidence map for what PSXRecompStudio can currently demonstrate.

It is deliberately narrower than a compatibility list and deliberately less detailed than the task backlog:

- **GitHub Issues** track work, dependencies, and acceptance criteria.
- **Tests and E2E gates** are the executable evidence.
- **Subsystem SSOT documents** define intended architecture and contracts.
- **This matrix** projects those sources into a compact view of implemented and verified capability.

A row marked **Verified** proves only the stated scope. It does **not** imply complete PlayStation title compatibility.

Evidence links identify the preferred current proof for each row. Historical tests, superseded experiments, or older implementation paths may still exist elsewhere in the repository, but they do not expand the public claim unless this matrix is updated.

## State vocabulary

| State | Meaning |
|---|---|
| **Verified** | Implemented and covered by executable evidence for the stated scope. |
| **Partial** | Implemented for a bounded subset with explicit missing behavior. |
| **Blocked** | The path exists, but a known unsupported boundary prevents further progress. |
| **Planned** | The capability is tracked but not yet implemented at the stated scope. |
| **Not claimed** | The project intentionally makes no broader compatibility claim from the available evidence. |

Evidence is also separated into **synthetic / repository-owned** proof and **legal user-supplied real-input** proof. Real-input tests skip explicitly when the required local fixture is absent.

### Real-input evidence vocabulary

| Value | Meaning |
|---|---|
| **Verified (bounded)** | A legal local input has exercised the stated capability for a bounded scope. |
| **Measured blocker** | A legal local input reached this path and established a concrete stopping boundary. |
| **Not reached** | The current production path stops earlier, so this capability has not yet been evaluated by that run. |
| **Not applicable** | The capability is a format, contract, tooling, or local-diagnostic property for which title input is not the relevant proof dimension. |
| **Not claimed** | Real-input behavior is intentionally excluded from the public claim for this row. |
| **No evidence yet** | No maintained real-input evidence currently supports this row. |

### Verification threshold

A capability may be marked **Verified** only when all of the following are true for the stated scope:

1. A repository-owned executable test or maintained E2E gate covers the capability.
2. The evidence asserts an observable contract, not merely absence of a crash.
3. Any real-input statement links to a reproducible local command or a maintained status record.
4. When the implementation is intentionally bounded, the row names the known boundary or points to the maintained blocker/status source.

## Current capability matrix

| Area | Capability | State | Executable / maintained evidence | Real-input evidence | Known blocker / next step |
|---|---|---|---|---|---|
| Input | Bounded PS-X EXE analysis path for exercised inputs | **Verified** | [Real-input E2E tests](../../src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs), [Persona E2E status](../v0.1.0/persona-e2e-status.md) | **Verified (bounded)** | Continue to extend only when a real input exposes a new parser/analysis gap. |
| Input | Bounded CHD-to-executable extraction and analysis path for exercised disc layouts | **Verified** | [Headless CLI](../development/headless-cli.md), [Persona E2E status](../v0.1.0/persona-e2e-status.md) | **Verified (bounded)** | Broader disc/runtime behavior remains separate from successful executable extraction. |
| Recompiler | MIPS → IR/lowering → deterministic generated host code | **Verified** | [Synthetic vertical-slice tests](../../src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs), [IR contract](../development/recompiler-ir-contract.md), [host-codegen contract](../development/recompiler-host-codegen.md) | **Verified (bounded)** | Whole-program lowering coverage still advances through measured unsupported opcodes. |
| Recompiler | Interpreter differential validation | **Verified** | [Synthetic vertical-slice tests](../../src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs), [bounded real-ROM differential tests](../../src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs) | **Verified (bounded)** | Preserve the interpreter/differential path as the correctness oracle while coverage expands. |
| Recompiler | Whole-program Persona lowering through the production CLI | **Blocked** | [Persona E2E status](../v0.1.0/persona-e2e-status.md), #599 | **Measured blocker** | #599: lower LWL/LWR/SWL/SWR with correct merge and load-delay semantics, then rerun the identical production command to discover the next boundary. |
| Generated host | Build and launch a runnable recompiled artifact | **Verified** | [Runnable-artifact E2E tests](../../src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs), [Headless CLI](../development/headless-cli.md) | **Verified (bounded)** | This is not yet a complete-title production backend. |
| Runtime | Production title-execution composition / classified result | **Verified** | [Studio production-flow tests](../../src/PSXRecompStudio.Tests/RealRomProductionFlowTests.cs), [Persona E2E status](../v0.1.0/persona-e2e-status.md) | **Verified (bounded)** | Studio production execution remains interpreter-backed; complete generated-host title execution is not claimed. |
| CPU | R3000A/MIPS I execution substrate, delay-slot and exception foundations | **Partial** | CPU docs and tests under [docs/cpu](../cpu/), [Persona E2E status](../v0.1.0/persona-e2e-status.md) | **Verified (bounded)** | Continue evidence-driven semantic fixes rather than claiming instruction-set completeness from aggregate coverage. |
| BIOS | BIOS HLE dispatch | **Partial** | [Persona E2E status](../v0.1.0/persona-e2e-status.md), #279 | **Not reached** | Current Persona production CLI build stops in recompiler lowering before BIOS execution; identify the next concrete BIOS gap only after runtime is actually reached. |
| GPU | GP0/GP1/GPUSTAT, VRAM substrate, minimal rasterization and deterministic frame snapshots | **Partial** | [FrameSnapshot tests](../../src/PSXRecomp.Tests/FrameSnapshotTests.cs), [runtime architecture](../runtime/architecture.md), [Persona E2E status](../v0.1.0/persona-e2e-status.md) | **Verified (bounded)** | Production frame evidence exists; DMA2 data movement and broader GPU behavior remain incomplete under #440. |
| SPU | Register/MMIO model | **Partial** | [Runtime architecture](../runtime/architecture.md), [Persona E2E status](../v0.1.0/persona-e2e-status.md), #445 | **Not claimed** | ADPCM/ADSR, mixing, reverb, sound RAM behavior, audio output and IRQ behavior are outside the completed register/MMIO slice. |
| SIO0 | Register model, disconnected-pad transaction path and IRQ7 | **Partial** | [Runtime architecture](../runtime/architecture.md), [Persona E2E status](../v0.1.0/persona-e2e-status.md) | **Not claimed** | Real host-controller integration and memory-card wire protocol remain outside the bounded model. |
| Memory card | Runtime storage/file-format support | **Verified** | [Memory-card runtime policy](../runtime/memory-card.md) | **Not applicable** | Broader controller/SIO interaction is tracked separately from storage-format support. |
| CD-ROM | Register/FIFO substrate, minimum command protocol, DMA3 and IRQ2 in the production interpreter | **Partial** | [Runtime architecture](../runtime/architecture.md), [Persona E2E status](../v0.1.0/persona-e2e-status.md), #444 | **Not reached** | Tests prove guest MMIO → IRQ2 (INT3 and INT1) → DMA3 into guest RAM with channel-3-only completion, but only with test-supplied data: no production code supplies sector bytes (`CdRomDevice.LoadData` has no production caller), so real disc/sector reads are not implemented. Streaming reads, seek timing and CD audio are also absent. |
| GTE | COP2 register substrate and initial isolated command kernels | **Partial** | [Persona E2E status](../v0.1.0/persona-e2e-status.md), #447 | **Not reached** | Complete native COP2/LWC2/SWC2 dispatch/integration and additional commands remain evidence-gated. |
| MDEC | Runtime command/FIFO/DMA integration | **Planned** | #446 | **No evidence yet** | Implement only when evidence or the roadmap promotes it to an active runtime blocker. |
| CLI | `recompile` / `run`, structured results, diagnostics/reporting | **Verified** | [Headless CLI](../development/headless-cli.md), [real-input E2E tests](../../src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs) | **Verified (bounded)** | Build-stage failures that occur before a runtime result exists may still terminate before structured runtime evidence is produced; see the current Persona blocker report. |
| Diagnostics | Privacy-safe local diagnostic bundle; no automatic upload | **Verified** | [Headless CLI](../development/headless-cli.md), [diagnostics architecture](../architecture/diagnostics.md) | **Not applicable** | Keep diagnostic data deterministic/redacted as additional runtime evidence is added. |
| Native boundary | C# ↔ C ABI ↔ mixed C++/Rust coexistence | **Verified** | [Rust FFI safety contract](../development/rust-ffi-contract.md), [architecture matrix](../architecture-matrix.md) | **Not applicable** | #607 tracks stronger automated ABI compatibility/layout checks as Rust migration continues. |
| Persona v0.1 gate | Reach an actual title screen without title-specific Core/Recompiler shortcuts | **Blocked** | [Persona E2E status](../v0.1.0/persona-e2e-status.md), #351 | **Measured blocker** | Resolve the currently measured first blocker, rerun, and continue evidence-first until the genuine title-screen gate is reached. |

## What the matrix does not claim

This matrix intentionally does not provide a percentage-complete score.

In particular:

- a passing bounded real-input test is not a compatibility claim for that title;
- a supported instruction or device register subset is not a claim of complete subsystem emulation;
- a deterministic frame snapshot is not proof that a complete commercial title renders correctly;
- a runnable generated-host artifact is not yet the same thing as complete-title native execution;
- anticipated BIOS/GPU/CD-ROM/GTE blockers are not promoted to "current blocker" until an actual production execution run reaches them.

The current measured Persona blocker and its history live in [Persona E2E Pipeline Status](../v0.1.0/persona-e2e-status.md).

## Maintenance

Update this page when one of the following changes materially:

1. a capability moves between Planned / Partial / Verified / Blocked;
2. a new executable evidence path becomes the preferred proof for a capability;
3. a real-input run establishes a different first blocker;
4. a subsystem begins making a broader or narrower public claim;
5. the review baseline becomes stale enough that the linked preferred evidence no longer describes current `main`.

Keep this top-level matrix at subsystem/capability granularity. Instruction-level, command-level, or register-level support tables belong in subsystem documentation and should be linked from here rather than expanding this page indefinitely.

Do not mirror the full Issue backlog here. Link to the focused Issue or maintained status document instead.
