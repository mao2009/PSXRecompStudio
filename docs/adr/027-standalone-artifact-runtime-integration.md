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

**Step C (PR #761) supersedes the above**: it implements host bundling, direct
launch, and self-contained distribution as described in the Step C scope below.

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

## Step C scope: distributed Runtime host provisioning (ADR-027 Option B)

Step C implements the distributed Runtime host provisioning path, enabling a
packaged game distribution to launch directly on the end user's machine with no
Studio, no CLI, and no .NET SDK at runtime.

### Implementation (Phase 2 Step C, 2026-10-12)

**New files:**
- `src/PSXRecomp.RuntimeHost/Program.cs` - Host entry point that reads
  `runtime/runtime-host.json`, locates the bundled `artifact-image.bin`, and
  runs the guest artifact through the shared Runtime.
- `src/PSXRecomp.Infrastructure/RuntimeHostBuildService.cs` - Provisioning
  service that runs `dotnet publish -c Release -r <rid>` of the RuntimeHost
  project into `<outputDir>/runtime/`.
- `src/PSXRecomp.Cli/PackageCommand.cs` - E2E packaging command that generates
  the host and emits `runtime/runtime-host.json` manifest.
- `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs` - On-disk
  manifest describing the guest artifact (image, block entry PCs, entry PC, program end).
- `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostLayout.cs` - SSOT for the
  distribution layout:
  ```
  <game>/
    <game>[.exe]          native AOT artifact (bridge-capable, direct-launch)
    runtime/
      PSXRecomp.RuntimeHost[.exe]   shared managed Runtime host
      runtime-host.json               guest run configuration (every package)
      build-manifest.json             integrity carrier for the H2/H3 SHA-256 checks; mandatory for newly produced packages (see the 2026-10-30 amendment)
      artifact-image.bin              embedded program image sidecar (HLE packages only)
      firmware-boot.json              firmware packages only: firmware boot manifest (H4)
      firmware/                       firmware packages only: AOT inputs (H5), roots and load-images files (H10)
      disc/                           firmware packages only: disc image named by the boot manifest (H9)
      bios-content-diagnostic.txt     firmware packages only: counts-only report, written and never read by the host
    README.txt
  ```
- `src/PSXRecomp.Core/Execution/ProgramEndHandoff.cs` - Natural program end
  signal for the host-driven run (`TitleExecutionService.Execute` returns the
  completion state).

**Key behavior:**
- Guest firmware (OpenBIOS) is auto-detected by load address 0x1FC00000 (or size 0x80000).
  When `GuestFirmware` is true, the host attaches no BIOS HLE Runtime and launches the
  artifact in `--runtime-host` mode; the device-graph MMIO bridge is still attached
  (`--host-transfer`, `--exact-device-time`, `--guest-exceptions`) because it is the
  artifact's only MMIO path (without it a guest MMIO access exits 96).
- Mixed fallback requires a bridge; the manifest carries `BlockEntryPcs` and
  `EntryPc` for a direct-launch engine that does not emit code.
- The launcher (`GenerateStandalone(..., directLaunchHostRelativePath)`) generates
  a C prologue that checks for the host (`--host-transfer` flag) and either:
  - Launches `runtime/PSXRecomp.RuntimeHost[.exe]` with the manifest path when
    `--host-transfer` is not present (direct-launch mode), or
  - Bypasses the host and executes the embedded artifact directly when
    `--host-transfer` is present (Phase 1 path).
- Missing host → exit code 78 (defined in `RuntimeHostLayout.MissingRuntimeHostExitCode`).
- Exit codes: 0 = success, 2 = blocked transfer, 1 = tooling failure, 78 = missing host.

**Exit code 78 contract:**
- Exit 78 means "the packaged runtime cannot be set up" and is returned by two
  different programs for their own setup failures:
  - the generated artifact's **launcher**, when it cannot locate or start the
    bundled Runtime host;
  - the **Runtime host** (`PSXRecomp.RuntimeHost`), for a wrong argument count, a
    missing manifest or manifest directory, or an invalid / out-of-distribution /
    linked manifest path (fail closed, before anything is launched).
- Once the Runtime host has started a run it returns 0 (completed) or 2 (blocked /
  unsupported transfer, explicit runtime stop); 1 is a tooling failure. The
  generated artifact's own termination codes (0..7, 90..98) are distinct from
  these, and `psxrecomp package` uses only 0 (packaged) and 1 (failure).
- The launcher checks for `RuntimeHostLayout.HostRelativePathFor(platform)`;
  if the directory or binary is missing, it exits 78 and emits a missing-host
  diagnostic.

**Commands:**
- `psxrecomp package <input.exe|input.chd> --output <dir> ...`:
  - Generates the host directly with `RuntimeHostBuildService.ProvisionRuntimeHost`.
  - By default (`--self-contained=false`), publishes a framework-dependent
    deploy (requires .NET runtime on the host).
  - With `--self-contained=true`, publishes a fully contained single-file host
    (no end-user .NET needed).
  - Emits `runtime/runtime-host.json` with schema version 1.
  - Emits `runtime/artifact-image.bin` sidecar for the host's fallback.
  - Emits `README.txt` with cross-platform launch instructions.

**Testing:**
E2E tests in `src/PSXRecomp.Tests/E2E/RuntimeHostPublishE2E.cs` verify:
- Package command provisions host into `runtime/` directory.
- Generated layout contains all required files (host binary, manifest, image).
- Runtime host can launch bundled artifact and terminates with correct exit code.
- Missing runtime → launcher exits 78.
- Self-contained publishing produces a non-empty single-file host.

**Remaining work:**
- No HLE BIOS deployment (OpenBIOS-only for now, BIOS service calls are not
  served in this release).
- No absolute paths in the manifest (all paths are relative to the distribution root).
- Runtime bridge optimization (eliminate unnecessary host spawns when no bridge is needed)
  and migration of the "Phase 1 path" (direct-launch no-host) to the launcher's
  `--host-transfer` flag for parity with the host-provisioned flow.

**Verification status:**
- ✅ ADR-027 Option B decision implemented.
- ✅ Runtime host entry point with manifest parsing and execution flow.
- ✅ Provisioning service with `dotnet publish` integration.
- ✅ Package command with host generation and manifest emission.
- ✅ E2E tests for layout, host launch, and exit code 78.
- ✅ Fixed C verbatim delimiter issues in launcher prologue (Phase 2 Step C prerequisite).
- ✅ Windows launcher fixes (PATH_MAX, command-line quoting, handle cleanup).
- ✅ Mixed fallback support for prebuilt artifacts.
- ✅ ArgumentList-based process invocation for paths with spaces.
- ✅ Guest firmware + mixed fallback logic corrected.
- ✅ Documentation updates (headless-cli.md, runtime-host.md, README.md).
- ✅ Full build and test suite verification (Linux CI passing).
- ✅ PR creation and CodeRabbit review.

## Amendment (2026-10-30): Step C layout and integrity carrier

- Scope: only the Step C layout block changes. The decision text, the Option
  B and C choices, the Step A scope and the status are unchanged.
- Adds `runtime/build-manifest.json` as the integrity carrier (H16). The
  Runtime host and the game window must check H2 (`binary.sha256`), and for
  HLE packages H3 (`packaged.imageSidecar.sha256`), against it before use. This
  check is not yet implemented. The
  carrier is mandatory for every newly produced package, HLE and firmware. Its
  writer is Issue #864 (Artifact Provenance, #46). Its reader is Issue #865
  (Runtime Configuration, #48). The carrier is not a runtime configuration
  input, and `runtime-host.json` stays schema 1.
- Existing firmware inputs, listed explicitly. They already ship and this
  amendment does not add them: `runtime/firmware-boot.json` (H4);
  `runtime/firmware/` AOT inputs (H5), including the roots and load-images
  files (H10); `runtime/disc/<leaf>` (H9); `runtime/bios-content-diagnostic.txt`
  (counts only, written and never read by the host); and the environment
  variable `PSXRECOMP_BIOS_ROM` at run time (H6, not a file).
- Migration and legacy packages: a package produced before this amendment has
  no `runtime/build-manifest.json`. The amended reader is required to refuse it (requirement; not implemented; fail closed,
  a classified setup failure; no guest code runs). The host never generates,
  writes or repairs the carrier, so such a package must be repackaged.
- No claim is made that a production carrier is enabled, that any acceptance
  criterion has passed, or that Windows or POSIX protection is verified.
- Source: `docs/design/issue-48-runtime-config.md` sections 2.2, 3.4 and 11
  (Q13, Q16, Q17, Q19).
