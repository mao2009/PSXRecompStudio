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
process exit 5) and a guest access outside RAM with no host MMIO bridge exits
96 (`MmioUnavailable`, ADR-025's device-graph boundary), so the Phase 1
artifact cannot run real programs that touch devices or the BIOS HLE boundary.

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
- **B — Bundle the shared managed Runtime host with the artifact (chosen as
  the distribution direction).** The artifact's embedded image attaches the
  existing `--host-transfer` bridge to the shared
  `BiosHleRuntime`/device graph supplied by a managed host, exactly the way
  the file-driven artifact does today. Studio is not needed at runtime and no
  input files are read; .NET on the host is allowed for now, preferring
  self-contained distribution (no user-side .NET install). Step A implements
  only the Runtime Bridge half of B — bundling the host into the distributed
  executable and directly launching that executable are future completion
  conditions of #739 (see "Step A scope" below).
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

## Step A scope: the Runtime Bridge only

This ADR records the Phase 2 integration decision; **Step A (PR #758)
implements only the Runtime Bridge** for the embedded-image artifact.
Explicitly **not** implemented in Step A:

- **Host bundling** — the shared managed Runtime host is not bundled into the
  distributed executable; `run --standalone` supplies it from the CLI process.
- **Direct launch** — the distributed game executable cannot be started without
  a host process (no double-click / bare-runtime launch); the CLI run path is
  required.
- **Self-contained distribution** — a package with no user-side .NET install
  is a future completion condition of #739, not a Step A property.

Option B's policy decision is adopted and maintained (the host structure keeps
the NativeAOT path of Option C composable), and **Phase 1's parentless
execution contract is preserved**: an embedded artifact generated without the
bridge (`hostBridge: false`), or launched without `--host-transfer`, keeps
exactly the Phase 1 no-host behavior (unresolved transfer: termination 5 /
exit 5; out-of-RAM access with no bridge: exit 96).

## Relation to existing ADRs

- **ADR-014** stays in force: the native artifact still reaches the managed
  Runtime only by relaying over the RHOST protocol to a host; this change
  supplies that host from the `run --standalone` CLI process instead of the
  Studio process. Bundling the host into the distributed executable is the
  future completion condition listed under "Step A scope", not a Step A
  behavior.
- **ADR-015**: the composition root is unchanged — the same shared
  `ExecutionOrchestrator`/`BiosHleRuntime` types, reached through
  `RecompiledArtifactLauncher` with no Studio/Avalonia dependency.
- **ADR-016**: the generated-host budget contract and termination
  classification are unchanged. The budget unit is a **generated-host dispatch
  unit** (retired generated blocks, plus claimed host-transfer dispatch units)
  — not a MIPS instruction count, and distinct from the mixed-fallback
  instruction budget (Issue #693) and guest time's retired-instruction count
  (Issue #732). The embedded image's budget is exactly the `RunSegment`
  budget.
- **ADR-025**: guest time, device scheduling and device RAM remain host-owned;
  the bridge and protocol are reused, not extended.
- **ADR-023 / ADR-026**: no new runtime compilation is introduced; the embedded
  image is compiled AOT exactly like the file-driven artifact. RAM-placed code
  (ADR-026) keeps its existing semantics.
- NativeAOT (Option C) remains the documented future path (ADR-027
  Alternatives/B), and the host structure deliberately keeps it composable.

## Consequences and verification

- `RecompiledArtifactCodeGen.GenerateStandalone(..., hostBridge: true)` emits
  the bridge-capable `main()`; the generated source stays self-contained with
  respect to input files (no `fopen`/`fscanf`/`argv[1]`/`argv[2]` — it embeds
  its image and initial state as data) and the file-IO leak gate applies.
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
- Phase 2 Step B (Issue #739) reuses this same `embedImage` mode from
  `openbios-probe --engine generated-host --standalone` to run the OpenBIOS
  firmware through the standalone artifact; it adds no new execution path and
  is documented in
  [`docs/runtime/openbios-firmware-backend.md`](../runtime/openbios-firmware-backend.md).
