# ADR-027: Standalone artifact — shared Runtime integration (Phase 2)

- Status: Accepted
- Date: 2026-10-12
- Issue: #739
- Parent implementation: Phase 2 Step A (Runtime Bridge), PR #758

## Context

Phase 1 (PR #757, ADR-014's relay boundary) generates a parentless standalone
artifact: the PS-X EXE program image and its initial state are embedded in the
C source and the `main()` runs recompiled code with no host attached. Without a
host, an unresolved control transfer terminates the artifact (termination 5,
exit 96) and MMIO access exits with the unsupported-intent code, so the
Phase 1 artifact cannot run real programs that touch devices or the BIOS HLE
boundary.

Phase 2 (Issue #739) requires the generated artifact to run against the
existing shared Runtime with no Studio at runtime. The shared Runtime must be
reused — it must not be reimplemented, and no new JIT is introduced: AOT stays
the primary path (#739 explicitly allows a bounded interpreter fallback and
runtime dependencies).

## Options compared

- **A — Static-link the native Runtime into the artifact.** No; the shared
  Runtime is a managed (C#) host that owns the device graph, scheduler and BIOS
  HLE, so "static linking" into a native C artifact is not composable with the
  existing architecture without a large runtime reimplementation.
- **B — Bundle the shared managed Runtime host with the artifact (chosen).**
  The artifact's embedded image attaches the existing `--host-transfer` bridge
  to the shared `BiosHleRuntime`/device graph in the bundled host, exactly the
  way the file-driven artifact does today. Studio is not needed at runtime and
  no input files are read; .NET on the host is allowed for now, preferring
  self-contained distribution (no user-side .NET install). The generated
  product is a Persona-specific executable.
- **C — NativeAOT compilation of the host.** The future native-ization path:
  the structure chosen in B must not preclude it (same managed host, compiled
  ahead of time instead of run under a JIT). Not the Phase 2 delivery itself.
- **D — Other (separate/interpreter-wrapped product).** Rejected: would not
  reuse the shared Runtime or would duplicate an interpreter.

## Decision

Extend the standalone artifact so its embedded-image `main()` can attach the
shared Runtime host bridge over the existing `--host-transfer` path (opt-in via
argv, keeping Phase 1's parentless behavior when the bridge is not requested).
The artifact is generated from exactly the state the run call names (PC, GPRs,
HI/LO, budget, init memory, image), so it reads no input files. The CLI exposes
this as `run --standalone`. The default (non-embedded) file-driven artifact and
the `recompile --standalone` product are unchanged.

## Relation to existing ADRs

- **ADR-014** stays in force: the native artifact still reaches the managed
  Runtime only by relaying over the RHOST protocol to a host; this change
  bundles that host with the artifact instead of requiring the Studio process.
- **ADR-015**: the composition root is unchanged — the same shared
  `ExecutionOrchestrator`/`BiosHleRuntime` types, reached through
  `RecompiledArtifactLauncher` with no Studio/Avalonia dependency.
- **ADR-016**: the per-segment instruction budget contract and termination
  classification are unchanged; the embedded image's budget is the
  `RunSegment` budget.
- **ADR-025**: guest time, device scheduling and device RAM remain host-owned;
  the bridge and protocol are reused, not extended.
- **ADR-023 / ADR-026**: no new runtime compilation is introduced; the embedded
  image is compiled AOT exactly like the file-driven artifact. RAM-placed code
  (ADR-026) keeps its existing semantics.
- NativeAOT (Option C) remains the documented future path (ADR-027
  Alternatives/B), and the host structure deliberately keeps it composable.

## Consequences and verification

- `RecompiledArtifactCodeGen.GenerateStandalone(..., hostBridge: true)` emits
  the bridge-capable `main()`; the generated source is still self-contained
  (no `fopen`/`fscanf`/`argv[1]`/`argv[2]`) and the file-IO leak gate applies.
- `RecompiledHostExecutionEngine` gains `embedImage`: it defers the embedded
  generation+build to the first `RunSegment` (the state is only known there) and
  launches with flags only, writing no `artifact-input.txt`/`artifact-image.bin`.
- `RecompiledArtifactLauncher.Launch(..., standalone:)` and the CLI
  `run --standalone` wire the option through to the shared launcher.
- Verified by codegen unit tests (bridge-capable main, determinism), a
  production-path E2E launch proving the embedded artifact attaches the shared
  `BiosHleRuntime` (putchar through the A0 vector completes with the TTY byte
  and no input files), and a CLI E2E run of a synthetic PS-X EXE in both
  plain and `--standalone` modes.