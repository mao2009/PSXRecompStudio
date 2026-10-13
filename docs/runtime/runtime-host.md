# Runtime Host

**Status:** Implemented for Phase 2 Step C (Issue #739, ADR-027 Option B)

The shared managed Runtime host bundles with a packaged game distribution so
the end user can run the artifact without Studio, no CLI, and no .NET SDK at
runtime. The host reads `runtime/runtime-host.json`, locates the bundled
`artifact-image.bin`, and runs the guest program through the existing
`ExecutionOrchestrator`/`BiosHleRuntime` shared Runtime (device graph, scheduler,
and HLE bridge). By default, the host ships as a framework-dependent deploy
(require matching .NET runtime on the host), or fully self-contained with
`--self-contained=true` (single-file host executable).

## Implementation

### Runtime Host Entry Point

**Location:** `src/PSXRecomp.RuntimeHost/Program.cs`

The host is a standalone console application (`net10.0` target, `OutputType=Exe`)
that:

1. Accepts a single command-line argument: the path to the `runtime-host.json`
   manifest.
2. Parses the manifest (schema version 1), verifying artifact and image paths,
   entry PC, program end, and block entry PCs.
3. Loads the guest program image from `artifact-image.bin` (sidecar used for
   the fallback interpreter).
4. Uses `RecompiledHostExecutionEngine.ForPrebuiltArtifact` to execute the
   guest over the shared Runtime:
   - **Guest firmware mode:** If `manifest.GuestFirmware` is true (detected by
     load address 0x1FC00000), the bridge is not attached; the artifact runs
     natively with no HLE service.
   - **HLE mode:** If `manifest.GuestFirmware` is false, the `BiosHleRuntime`
     bridge is attached; BIOS service calls and mixed fallback transfers are
     serviced through the device graph.
   - **Mixed fallback** (`manifest.MixedFallback`) is honored in both modes.
5. Passes `ProgramEndHandoff(manifest.ProgramEnd)` to the execution orchestrator
   for natural program end detection.
6. Collects console output and forwards it to stdout (for native runs) or TTY
   through the sink (for HLE-routed runs).
7. Returns an exit code:
   - **0:** Successful completion
   - **1:** Tooling failure (no arguments or `--help`)
   - **2:** Explicit runtime stop (guest-initiated termination during the run)
   - **78:** The host could not be set up: wrong argument count, missing
     manifest or manifest directory, invalid manifest, or a failure while
     building the runtime. The native launcher also returns 78 when it cannot
     locate or start the host binary (see [Exit Code 78 Contract](#exit-code-78-contract)).

### Manifest Contract

**Location:** `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostManifest.cs`

The on-disk manifest is written by the `psxrecomp package` command and has
the following required fields (schema version 1):

| Field | Type | Meaning |
|:--- |:--- |:--- |
| `schemaVersion` | `int` | Must equal `RuntimeHostManifest.CurrentSchemaVersion` (1) |
| `artifact` | `string` | Relative path from manifest directory to the native game executable (`../game` on Linux, `../game.exe` on Windows) |
| `guestFirmware` | `bool` | True when the image is guest firmware (OpenBIOS) that owns its kernel; false for HLE-bridged games |
| `mixedFallback` | `bool` | True when the artifact uses the mixed execution fallback (requires bridge for HLE games; for guest firmware, mixed fallback runs without a bridge) |
| `fallbackSegmentInstructionBudget` | `uint` | Per-segment instruction cap for interpreter fallback (used only when `mixedFallback=true`) |
| `fallbackMaxTransitions` | `uint` | Maximum number of handoffs from artifact to interpreter (used only when `mixedFallback=true`) |
| `imageLoadAddress` | `uint` | Guest address where the program image is loaded (e.g., 0x80010000 for games, 0x1FC00000 for OpenBIOS) |
| `image` | `string` | Relative path to the guest program image sidecar (`artifact-image.bin`) |
| `blockEntryPcs` | `uint[]` | Sorted, unique list of PCs that have compiled blocks (required for bridge/HLE mode; optional for guest firmware without mixed fallback) |
| `programEnd` | `uint` | Guest PC one past the image (natural program end boundary) |
| `resultRegister` | `int?` | GPR index used for result abstraction (0 for V0, null for none) |
| `runTimeoutMs` | `int` | Bounded per-run timeout in milliseconds (default 60000) |
| `entryPc` | `uint` | Guest PC at which the program begins (PS-X EXE entry point or firmware load address) |

The manifest carries **no absolute paths** (all paths are relative to the
distribution root). Schema version mismatches fail closed with an exception.

### Provisioning Service

**Location:** `src/PSXRecomp.Infrastructure/RuntimeHostBuildService.cs`

The provisioning service builds and publishes the RuntimeHost project via
`dotnet publish`:

- **Target directory:** `<distributionRoot>/runtime/`
- **Default RID:** Host's own runtime identifier (`RuntimeInformation.RuntimeIdentifier`).
  Passing a foreign RID enables cross-compile packaging (e.g., Linux host →
  Windows artifact).
- **Publish mode:**
  - `selfContained=false` (default): Framework-dependent deploy with apphost +
    host + runtime files. Requires matching .NET runtime on the host.
  - `selfContained=true`: Fully contained single-file host (`-p:PublishSingleFile=true`).
    No .NET installation is required at runtime.
- **Publish flags:** `-c Release -r <rid> -o <outputDir> --no-restore -p:SelfContained=<true/false>`
- **Output validation:** The provisioned directory must contain the host binary
  at `PSXRecomp.RuntimeHost[.exe]` relative to the output directory.

The service throws `RuntimeHostProvisionFailed` on any publish failure, with
`DiagnosticCode` identifying the failure reason (DOTNET_TOOLCHAIN_NOT_AVAILABLE,
DOTNET_PUBLISH_TIMEOUT, DOTNET_PUBLISH_FAILED).

### Layout Contract

**Location:** `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostLayout.cs`

The distribution layout is the single source of truth (SSOT) for accessing the
host and its data relative to the distribution root:

```
<game>/
  <game>[.exe]           native AOT artifact (bridge-capable, direct-launch)
  README.txt             end-user launch instructions and exit codes
  runtime/
    PSXRecomp.RuntimeHost[.exe]   shared managed Runtime host
    runtime-host.json               guest run configuration (manifest)
    artifact-image.bin              guest program image (sidecar)
```

Key constants:

| Constant | Value | Meaning |
|:--- |:--- |:--- |
| `RuntimeDirectoryName` | `"runtime"` | Subdirectory holding the host and data |
| `HostBinaryName` | `"PSXRecomp.RuntimeHost"` | Host executable base name (no platform suffix) |
| `ManifestFileName` | `"runtime-host.json"` | Manifest filename (must be under `RuntimeDirectoryName`) |
| `ImageFileName` | `"artifact-image.bin"` | Image sidecar filename |
| `HostRelativePath` | `"runtime/PSXRecomp.RuntimeHost"` | Host path relative to game artifact (uses forward slashes) |
| `ManifestRelativePath` | `"runtime/runtime-host.json"` | Manifest path relative to game artifact |
| `ManifestRelativeArtifactPath(artifact, windows)` | `"../artifact"` | Artifact path relative to manifest directory |
| `MissingRuntimeHostExitCode` | `78` | Exit code returned when host cannot be located by launcher |

The launcher in the generated artifact uses `HostRelativePath` to locate the
host (appending `.exe` on Windows via generated C macros). The manifest is placed
one level above the host and uses relative paths (`../game` on Linux, `../game.exe`
on Windows) to reference the artifact. The `ManifestRelativeArtifactPath` helper
adds the platform-appropriate `.exe` suffix on Windows.

### CLI Integration

**Location:** `src/PSXRecomp.Cli/PackageCommand.cs`

The `psxrecomp package` command orchestrates the provisioning and layout:

1. Loads input (PS-X EXE or CHD) with `CliInput.LoadWithIdentity`.
2. Generates the direct-launch artifact with `RecompiledArtifactCodeGen.GenerateStandalone(...,
   hostBridge: true, directLaunchHostRelativePath: RuntimeHostLayout.HostRelativePath)`.
3. Builds the native artifact via `GeneratedHostBuildService`.
4. Provisions the Runtime host into `<outputDir>/runtime/` via
   `RuntimeHostBuildService.ProvisionRuntimeHost`.
5. Writes `runtime/artifact-image.bin` sidecar (guest program image bytes).
6. Writes `runtime/runtime-host.json` (manifest) using `RuntimeHostManifestJson.Serialize`.
7. Writes `README.txt` with cross-platform instructions and exit codes.
8. Returns exit code 0 on success, 1 on any failure.

**Usage:**

```
psxrecomp package <input.exe|input.chd> --output <dir> ... [--self-contained] [--json]
```

**Arguments:**

- `--entry-root <0xPC>`: Optional entry PC override for indeterminate loads.
- `--mixed-fallback`: Enable mixed execution fallback (default: on for games,
  off for guest firmware).
- `--fallback-segment-budget <n>`: Per-segment instruction cap (default: 1,000,000).
- `--fallback-max-transitions <n>`: Maximum handoffs (default: 100,000).
- `--self-contained`: Publish fully contained host (single-file executable).
  By default, framework-dependent deploy is used.
- `--json`: Emit machine-readable JSON envelope (see `CliJson.PackageResult`).

**Exit codes:**

| Code | Meaning |
|:--- |:--- |
| 0 | Distribution successfully packaged |
| 1 | Build, provision, or package error |

### Exit Code 78 Contract

Exit code 78 means "the packaged runtime cannot be set up". The **native
launcher** returns it when it cannot locate or start the bundled Runtime host;
the **Runtime host** returns it when its own manifest, image or runtime setup
fails. Once a run starts, the host returns 0, 1, or 2 for the classified result.

The launcher performs these checks before returning 78:

1. Exists `<distributionRoot>/runtime/` directory?
2. Host binary at `<distributionRoot>/runtime/PSXRecomp.RuntimeHost[.exe]` exists?
3. Host binary is executable (has executable permission on POSIX, appropriate
   bits on Windows)?

If any check fails, the launcher exits 78 with a diagnostic message like
"missing runtime host". The diagnostic is emitted to stderr, and the exit
code is propagated as `RuntimeHostLayout.MissingRuntimeHostExitCode`.

## Testing

### E2E Tests

**Location:** `src/PSXRecomp.Tests/E2E/RuntimeHostPublishE2E.cs`

The E2E test suite verifies:

- **Package provisioning:** Host is successfully provisioned into `runtime/`
  directory with correct layout (host binary, manifest, image).
- **Manifest validation:** Manifest has correct schema, paths, and entry data.
- **Host launch:** Runtime host can launch bundled artifact and terminates
  with exit code 0.
- **Missing host:** Launcher returns exit 78 when Runtime host is absent.
- **Self-contained publishing:** Fully contained single-file host is produced
  (non-empty binary).
- **Cross-platform layout:** Relative paths use forward slashes, manifest uses
  platform-appropriate binary suffix.

### Fixtures

**Location:** `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs`

Step C fixtures added:

- **NaturalEndWithV0:** Natural-end program with V0=0x5678 (used for Runtime
  host launch verification).
- **NaturalEndWithTransitions:** Natural-end program with regular JAL transitions
  before exit (cross-platform sanity).
- **GuestFirmwareOpenbios:** Guest firmware (OpenBIOS at 0x1FC00000) with
  native execution, no HLE bridge.

## Future Work

- **NativeAOT compilation:** Compile the Runtime host to native AOT (Option C from
  ADR-027), reducing the host's runtime overhead and eliminating the need for
  .NET installation completely.
- **Optimized host spawning:** When `GuestFirmware` is true and the artifact does
  not use mixed fallback, eliminate unnecessary host spawns and run natively.
  The `--host-transfer` flag can be used to control this path.
- **Runtime bridge consolidation:** Consolidate the Phase 1 path (no host) and the
  Phase 2 path (host-provisioned) into a unified launcher code path, with
  flag-based behavior selection.
- **Universal packaging:** Support packaging to multiple target platforms
  in a single invocation (e.g., `--target platform1,platform2`).

## References

- [ADR-027: Standalone artifact — shared Runtime integration](../adr/027-standalone-artifact-runtime-integration.md)
- [ADR-014: BIOS HLE runtime contract](../adr/014-bios-hle-runtime-contract.md)
- [ADR-020: IR observable effect semantics](../adr/020-ir-observable-effect-semantics.md)
- [Issue #739: Standalone artifact generation and distributed Runtime provision](https://github.com/mao2009/PSXRecompStudio/issues/739)