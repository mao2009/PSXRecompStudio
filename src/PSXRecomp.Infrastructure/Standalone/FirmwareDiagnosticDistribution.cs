using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PSXRecomp.Architecture;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The inputs of one developer-only measurement (Issue #846). Every path names a local file that is read in place: the ROM
/// and the disc are never copied, and the AOT inputs are the only bytes copied (into the measurement's own runtime directory).
/// </summary>
[Infrastructure]
public sealed record FirmwareDiagnosticRequest(
    string RomPath,
    string RomSha256,
    string DiscPath,
    string DiscSha256,
    string AotDirectory,
    string OutputDirectory,
    uint CodeBytes,
    uint InstructionBudget,
    int RunTimeoutMs,
    string? HostSourceDirectory = null,
    string? ArtifactSourcePath = null,
    TimeSpan? BuildTimeout = null,
    bool DirectDiscReference = false);

/// <summary>A built measurement layout: the game executable, the <c>runtime/</c> directory and the manifest the host reads.</summary>
[Infrastructure]
public sealed record FirmwareDiagnosticBuild(
    string Root,
    string ArtifactPath,
    string HostPath,
    string ManifestPath,
    IReadOnlyDictionary<string, string> InputDigests);

/// <summary>
/// Developer-only measurement path for the firmware-booted run (Issue #846). It reuses the product's AOT plan, host dispatch
/// generation and standalone generation with the same arguments the firmware package uses (<c>hostBridge: true</c>,
/// <c>externalBios: true</c>), and runs the Runtime host from the same <c>runtime/</c> contract. It differs from
/// <see cref="FirmwareDistributionBuilder"/> in exactly these wiring points: the disc is opened in place and linked into the
/// runtime directory (never copied), the ROM is only read (it is named by <c>PSXRECOMP_BIOS_ROM</c> at run time), the host may
/// be a previously published directory instead of a fresh publish, and the artifact may be an earlier one. Nothing here is
/// shipped; the output directory is local and is never committed.
/// </summary>
[Infrastructure]
public static class FirmwareDiagnosticDistribution
{
    public const string IdentityFileName = "diagnostic-identity.json";
    public const string MeasurementFileName = "measurement.json";
    public const string HostStderrFileName = "host-stderr.txt";
    public const string HostStdoutFileName = "host-stdout.txt";
    public const string DiagnosticFileName = "host-diagnostic.json";

    /// <summary>The developer-only marker that makes the host open the original disc read-only (absent in every product package).</summary>
    public const string DiscMarkerFileName = "diagnostic-disc.json";

    private const string DiscMarkerSchema = "psxrecomp.diagnostic-disc.v1";

    private const string Schema = "psxrecomp.firmware-diagnostic.v1";
    private const string FirmwareRunPrefix = "{\"kind\":\"firmware-run\"";

    /// <summary>The longest the host may run past its own run timeout before its tree is stopped.</summary>
    private static readonly TimeSpan HostExitGrace = TimeSpan.FromMinutes(2);

    /// <summary>The longest a pipe may stay open after the host has ended (a grandchild holding it) before its output is taken as is.</summary>
    private static readonly TimeSpan PipeDrainBound = TimeSpan.FromSeconds(10);

    /// <summary>The most characters kept of each output stream; the rest is drained and the stream is marked truncated.</summary>
    private const int CaptureLimitChars = 4 * 1024 * 1024;

    /// <summary>
    /// Writes the measurement layout under <see cref="FirmwareDiagnosticRequest.OutputDirectory"/> (which must not exist yet). The
    /// inputs are verified before anything is created. The layout is written into a private staging directory and renamed into place
    /// only when complete; a failure removes the entries this call named before writing them, and never a directory it did not create
    /// or content another process placed in the staging area.
    /// <para>
    /// Ownership: every file is created with an exclusive create (it fails when the name exists) and recorded only after that create
    /// succeeds; every directory is created exclusively (Win32 CreateDirectoryW, which fails on an existing name) and recorded only then.
    /// Nothing is adopted, so a failure never removes a directory or file this call did not create.
    /// </para>
    /// <para>
    /// Operator notes: a failed build that cannot remove everything it created reports the staging directory it kept in the exception
    /// message. A process that is killed mid-build leaves its staging directory <c>.&lt;name&gt;.staging-&lt;guid&gt;</c> beside the output
    /// name, and nothing deletes it automatically. Files the runtime-host publish writes (names not known in advance) are not recorded,
    /// so a failure after that step leaves them in <c>runtime/</c>. Remove a kept staging directory by hand after checking it.
    /// The build runs on Windows only; it is refused on other platforms before anything is created.
    /// </para>
    /// </summary>
    public static FirmwareDiagnosticBuild Build(FirmwareDiagnosticRequest request, bool windows)
        => Build(request, windows, observe: null, compiler: null);

    /// <summary>
    /// The build with an observation seam for tests: <paramref name="observe"/> is called with a phase name and a path at each point
    /// where another process could take the output name. Production callers use the overload without it.
    /// </summary>
    internal static FirmwareDiagnosticBuild Build(FirmwareDiagnosticRequest request, bool windows, Action<string, string>? observe)
        => Build(request, windows, observe, compiler: null);

    /// <param name="compiler">The toolchain executable for the generated artifact; null uses the default compiler. Tests pass a stub.</param>
    internal static FirmwareDiagnosticBuild Build(
        FirmwareDiagnosticRequest request, bool windows, Action<string, string>? observe, string? compiler)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSha256(request.RomSha256, nameof(request.RomSha256));
        RequireSha256(request.DiscSha256, nameof(request.DiscSha256));

        var outputDir = Path.GetFullPath(request.OutputDirectory);
        // A dangling link or junction is not "existing" to Path.Exists, but a build must not be redirected through one either.
        if (Path.Exists(outputDir) || IsReparsePoint(outputDir))
        {
            throw new IOException($"The measurement directory already exists and is never replaced: {outputDir}");
        }

        observe?.Invoke("claim", outputDir);

        var artifactName = Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(artifactName))
        {
            throw new InvalidDataException($"The output directory has no name to derive the artifact from: {outputDir}");
        }

        // Inputs first: a wrong digest or a linked disc refuses before any output exists.
        var (rom, romSha) = OpenBiosRomInput.Read(Path.GetFullPath(request.RomPath), request.RomSha256);
        var firmware = OpenBiosFirmware.FromBytes(rom);
        var discPath = Path.GetFullPath(request.DiscPath);
        RequireRegularDisc(discPath);
        if (OpenBiosRomInput.Sha256HexOfFile(discPath) != request.DiscSha256)
        {
            throw new InvalidDataException("The disc does not match the expected SHA-256; the measurement is not started.");
        }

        var (aotInputs, aotFiles) = FirmwareAotBundle.Collect(Path.GetFullPath(request.AotDirectory));
        var digests = new Dictionary<string, string>(StringComparer.Ordinal) { ["rom"] = romSha, ["disc"] = request.DiscSha256 };
        foreach (var input in aotInputs)
        {
            digests["aot:" + input.Path] = input.Sha256;
        }

        // Everything is written into a private sibling staging directory. The output name is claimed only by the final rename,
        // which refuses an existing name, so a directory another process creates meanwhile is never entered, replaced or deleted.
        // The no-replace commit is a Windows rename (MoveFileEx without replace). On POSIX, rename(2) silently replaces an empty
        // directory that appears between the check and the rename, so the measurement is refused there instead of relying on it.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The measurement is built on Windows only: its no-replace publish of the output directory is not available elsewhere.");
        }

        var area = new StagingArea(outputDir, observe);
        try
        {
            area.Open(artifactName);
            var staging = area.Root;
            observe?.Invoke("staged", staging);

            var runtimeDir = Path.Combine(staging, RuntimeHostLayout.RuntimeDirectoryName);
            var firmwareDir = Path.Combine(runtimeDir, FirmwareBootManifest.FirmwareDirectory);
            area.CreateDirectory(firmwareDir);
            foreach (var (relative, source) in aotFiles)
            {
                var destination = Path.Combine(firmwareDir, relative.Replace('/', Path.DirectorySeparatorChar));
                area.CreateDirectory(Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException($"The AOT input '{relative}' has no file name."));
                area.CopyNewFile(source, destination);
            }

            var (stagedAotInputs, _) = FirmwareAotBundle.Collect(firmwareDir);

            // The disc is linked into the runtime directory (a second name for the same file), never copied. The plan reads
            // the original path directly, so the disc the plan and the host read is the one the user named.
            var discLeaf = Path.GetExtension(discPath).Equals(".chd", StringComparison.OrdinalIgnoreCase) ? "disc.chd" : "disc.bin";
            var discDir = Path.Combine(runtimeDir, FirmwareBootManifest.DiscDirectory);
            area.CreateDirectory(discDir);
            if (request.DirectDiscReference)
            {
                // Nothing is linked or copied: the marker names the original, which the host opens read-only.
                area.WriteNewFile(Path.Combine(runtimeDir, DiscMarkerFileName), DiscMarkerText(discPath, request.DiscSha256));
            }
            else
            {
                area.LinkNew(discPath, Path.Combine(discDir, discLeaf));
            }

            FirmwareAotPlan plan;
            using (var disc = DiscSectorSourceOpener.Open(discPath))
            {
                plan = FirmwareAotPlan.Build(
                    firmware,
                    Path.Combine(firmwareDir, FirmwareAotBundle.RootsFileName),
                    request.CodeBytes,
                    Path.Combine(firmwareDir, FirmwareAotBundle.ImagesFileName),
                    disc.Source);
            }

            var artifactFile = artifactName + (windows ? ".exe" : string.Empty);
            string artifactPath;
            string artifactSource;
            if (request.ArtifactSourcePath is null)
            {
                artifactPath = GenerateArtifact(area, plan, request, artifactName, artifactFile, windows, compiler);
                artifactSource = "generated";
            }
            else
            {
                // The earlier artifact is copied under this measurement's name; its bytes are not rebuilt.
                artifactPath = Path.Combine(staging, artifactFile);
                area.CopyNewFile(Path.GetFullPath(request.ArtifactSourcePath), artifactPath);
                artifactSource = "reused:" + Path.GetFileName(request.ArtifactSourcePath);
            }

            var hostSource = ProvisionOrCopyHost(area, runtimeDir, request, windows, observe);

            var boot = new FirmwareBootManifest(
                FirmwareBootManifest.CurrentSchemaVersion,
                BiosBackendSelection.NameOf(BiosBackendKind.OpenBios),
                request.CodeBytes,
                $"{FirmwareBootManifest.FirmwareDirectory}/{FirmwareAotBundle.RootsFileName}",
                $"{FirmwareBootManifest.FirmwareDirectory}/{FirmwareAotBundle.ImagesFileName}",
                $"{FirmwareBootManifest.DiscDirectory}/{discLeaf}",
                request.DiscSha256,
                stagedAotInputs);
            area.WriteNewFile(Path.Combine(runtimeDir, FirmwareBootManifest.FileName), FirmwareBootManifestJson.Serialize(boot));

            var blockEntryPcs = plan.Program.Blocks.Select(static b => b.EntryPc).Order().Distinct().ToArray();
            var host = new RuntimeHostManifest(
                RuntimeHostManifest.CurrentSchemaVersion,
                RuntimeHostLayout.ManifestRelativeArtifactPath(artifactName, windows),
                GuestFirmware: true,
                MixedFallback: true,
                FallbackSegmentInstructionBudget: uint.MaxValue,
                FallbackMaxTransitions: uint.MaxValue,
                ImageLoadAddress: OpenBiosFirmware.ResetVector,
                Image: string.Empty,
                BlockEntryPcs: blockEntryPcs,
                ProgramEnd: OpenBiosFirmware.ResetVector + (uint)OpenBiosFirmware.ImageSize,
                ResultRegister: null,
                RunTimeoutMs: request.RunTimeoutMs,
                EntryPc: OpenBiosFirmware.ResetVector,
                BiosProvenance: RuntimeHostManifest.BiosProvenanceSynthetic);
            var manifestPath = Path.Combine(runtimeDir, RuntimeHostLayout.ManifestFileName);
            area.WriteNewFile(manifestPath, RuntimeHostManifestJson.Serialize(host));

            var hostPath = Path.Combine(runtimeDir, RuntimeHostLayout.HostFileName(windows));
            var identity = new JsonObject
            {
                ["schema"] = Schema,
                ["romSha256"] = romSha,
                ["discSha256"] = request.DiscSha256,
                ["aotInputs"] = new JsonArray(stagedAotInputs.Select(i => (JsonNode)new JsonObject { ["path"] = i.Path, ["sha256"] = i.Sha256 }).ToArray()),
                ["artifact"] = new JsonObject { ["file"] = artifactFile, ["sha256"] = Sha(artifactPath), ["source"] = artifactSource },
                ["host"] = new JsonObject { ["file"] = Path.GetFileName(hostPath), ["sha256"] = Sha(hostPath), ["source"] = hostSource },
                ["codegen"] = new JsonObject
                {
                    ["protocolFallbackVersion"] = RecompiledArtifactCodeGen.ProtocolFallbackVersion,
                    ["romReference"] = "PSXRECOMP_BIOS_ROM at run time; the ROM is read, never copied",
                    ["discReference"] = "original path opened by DiscSectorSourceOpener; runtime/disc is a hard link to it",
                    ["wiring"] = "FirmwareAotPlan.Build + RecompilerHostCodeGen.Generate + RecompiledArtifactCodeGen.GenerateStandalone(hostBridge: true, externalBios: true)",
                },
            };
            area.WriteNewFile(Path.Combine(staging, IdentityFileName),
                identity.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

            // Only a completely written measurement is published; the rename refuses a name that is taken by now.
            area.Commit();
            return new FirmwareDiagnosticBuild(
                outputDir, area.Rebase(artifactPath), area.Rebase(hostPath), area.Rebase(manifestPath), digests);
        }
        catch (Exception exception)
        {
            var kept = area.Discard();
            if (kept is null)
            {
                throw;
            }

            throw new IOException(
                $"The measurement failed and its staging directory was kept at {kept} (remove it by hand after checking it): {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Runs the measurement's Runtime host once, the way a packaged distribution runs it (<c>PSXRECOMP_BIOS_ROM</c> names the ROM;
    /// the host launches the artifact as its child). The wait is bounded: past the bound the child's tracked process tree is stopped
    /// through <see cref="StopRecord"/> (only descendants claimed by parent identity; other processes are never named), and the
    /// outcome, the residual processes and the state of each output are recorded in the result instead of thrown. The inputs are
    /// digested again afterwards; a change makes the result invalid.
    /// </summary>
    /// <param name="exitGrace">The time past the run timeout before the tree is stopped; null uses <see cref="HostExitGrace"/> (tests shorten it).</param>
    public static JsonObject Measure(FirmwareDiagnosticBuild build, FirmwareDiagnosticRequest request, TimeSpan? exitGrace = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(request);
        var romPath = Path.GetFullPath(request.RomPath);
        var diagnosticPath = Path.Combine(build.Root, DiagnosticFileName);

        var startInfo = new ProcessStartInfo(build.HostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(build.ManifestPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Stdin is a pipe that stays open until the host ends: a host that waits for input is then not ended by EOF.
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--diagnostics");
        startInfo.ArgumentList.Add(diagnosticPath);
        startInfo.ArgumentList.Add(build.ManifestPath);
        startInfo.Environment[RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = romPath;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The runtime host did not start.");
        var stdout = new BoundedCapture(process.StandardOutput, CaptureLimitChars);
        var stderr = new BoundedCapture(process.StandardError, CaptureLimitChars);

        // The host enforces its own run timeout; the bound here only catches a host that never exits.
        var bound = TimeSpan.FromMilliseconds(request.RunTimeoutMs) + (exitGrace ?? HostExitGrace);
        using var cancel = new CancellationTokenSource();
        using var stop = new StopRecord();
        stop.Attach(process, cancel.Token);
        var clock = Stopwatch.StartNew();
        var timedOut = false;
        while (!process.WaitForExit(StopRecord.PollMs))
        {
            stop.SampleTree();
            if (clock.Elapsed >= bound)
            {
                timedOut = true;
                cancel.Cancel(); // the stop runs synchronously: it kills the tracked tree and waits for each exit
                break;
            }
        }

        if (!timedOut)
        {
            process.WaitForExit();
        }

        stop.Seal();
        var (confirmed, refused) = stop.Outcome;
        var residuals = stop.Residuals;
        var exitCode = process.HasExited ? process.ExitCode : (int?)null;

        process.StandardInput.Dispose();
        var stdoutCapture = stdout.Finish(PipeDrainBound);
        var stderrCapture = stderr.Finish(PipeDrainBound);
        File.WriteAllText(Path.Combine(build.Root, HostStdoutFileName), stdoutCapture.Text);
        File.WriteAllText(Path.Combine(build.Root, HostStderrFileName), stderrCapture.Text);

        RequireInputsUnchanged(build.InputDigests, CurrentDigests(request));

        var firmwareRun = stderrCapture.Text.Split('\n')
            .Select(static l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(FirmwareRunPrefix, StringComparison.Ordinal));
        var diagnostic = File.Exists(diagnosticPath) ? JsonNode.Parse(File.ReadAllText(diagnosticPath)) : null;

        var status = !timedOut ? "completed" : confirmed && refused is null ? "timed-out-stopped" : "timed-out-not-confirmed";
        return new JsonObject
        {
            ["schema"] = Schema,
            ["status"] = status,
            ["valid"] = !timedOut && stdoutCapture.Status == "complete" && stderrCapture.Status == "complete" && residuals.Count == 0,
            ["hostExitCode"] = exitCode,
            ["inputsUnchanged"] = true,
            ["stop"] = new JsonObject
            {
                ["processId"] = stop.ProcessId,
                ["confirmed"] = confirmed,
                ["refused"] = refused,
                ["residuals"] = new JsonArray(residuals.Select(r => (JsonNode)new JsonObject
                {
                    ["pid"] = r.ProcessId,
                    ["state"] = r.State.ToString(),
                    ["reason"] = r.Reason,
                }).ToArray()),
            },
            ["capture"] = new JsonObject
            {
                ["stdout"] = stdoutCapture.ToJson(),
                ["stderr"] = stderrCapture.ToJson(),
            },
            ["summary"] = Summary(exitCode, firmwareRun, diagnostic),
            ["firmwareRun"] = firmwareRun is null ? null : JsonNode.Parse(firmwareRun),
            ["diagnostic"] = diagnostic,
        };
    }

    /// <summary>Fails when any digest recorded before the run differs after it, or a recorded input is missing.</summary>
    public static void RequireInputsUnchanged(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        foreach (var (name, digest) in before)
        {
            if (!after.TryGetValue(name, out var now) || now != digest)
            {
                throw new InvalidDataException(
                    $"The input '{name}' changed while the measurement was taken (SHA-256 differs); the result is invalid.");
            }
        }
    }

    /// <summary>
    /// The disc the host opens for a run. Without the developer marker in <paramref name="runtimeDirectory"/> it is the product path
    /// (<c>runtime/&lt;relativeDiscFile&gt;</c>), so the product contract is unchanged. With the marker it is the original disc the marker
    /// names, which the caller opens read-only and digests (the digest check is the caller's). A symbolic link is refused.
    /// </summary>
    public static string ResolveRuntimeDiscPath(string runtimeDirectory, string relativeDiscFile)
    {
        var marker = Path.Combine(runtimeDirectory, DiscMarkerFileName);
        if (!File.Exists(marker))
        {
            return Path.Combine(runtimeDirectory, relativeDiscFile);
        }

        var node = JsonNode.Parse(File.ReadAllText(marker))?.AsObject()
            ?? throw new InvalidDataException("The diagnostic disc marker is not a JSON object.");
        if (node["schema"]?.GetValue<string>() != DiscMarkerSchema)
        {
            throw new InvalidDataException("The diagnostic disc marker has an unknown schema.");
        }

        var path = node["path"]?.GetValue<string>();
        if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new InvalidDataException("The diagnostic disc marker does not name an existing absolute disc path.");
        }

        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new InvalidDataException("The diagnostic disc must be a regular file, not a symbolic link.");
        }

        return Path.GetFullPath(path);
    }

    private static string DiscMarkerText(string discPath, string sha256)
    {
        var marker = new JsonObject { ["schema"] = DiscMarkerSchema, ["path"] = discPath, ["sha256"] = sha256, ["access"] = "read-only" };
        return marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>Creates a hard link at <paramref name="linkPath"/> to <paramref name="originalPath"/>. Never copies the bytes.</summary>
    public static void LinkDiscInPlace(string originalPath, string linkPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The measurement disc link is implemented for Windows only (hard link, no copy).");
        }

        if (new FileInfo(originalPath).LinkTarget is not null)
        {
            throw new InvalidDataException("The disc must be a regular file, not a symbolic link.");
        }

        if (!WindowsHardLinkInterop.CreateHardLink(linkPath, originalPath, IntPtr.Zero))
        {
            throw new IOException($"Could not hard-link the disc into the measurement (Win32 error {Marshal.GetLastPInvokeError()}); it is never copied instead.");
        }
    }

    /// <summary>The number of directory entries of the file at <paramref name="path"/> (2 for a hard-linked pair).</summary>
    public static uint HardLinkCount(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!WindowsHardLinkInterop.GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out WindowsHardLinkInterop.ByHandleFileInformation info))
        {
            throw new IOException($"Could not read the file information (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        return info.NumberOfLinks;
    }

    private static string GenerateArtifact(
        StagingArea area, FirmwareAotPlan plan, FirmwareDiagnosticRequest request, string artifactName, string artifactFile, bool windows,
        string? compiler)
    {
        var dispatch = RecompilerHostCodeGen.Generate(
            plan.Program, plan.LoadedCode, guardStats: GuardStatsScope.Enabled, allowEmptyProgram: true);
        if (!dispatch.Success)
        {
            throw new InvalidOperationException(dispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        BiosGuardWiring.Run(BiosGuardWiring.Dispatch, () => BiosAddressGuard.RequireNoBiosCode(plan.Program, plan.LoadedCode, BiosGuardWiring.Dispatch));
        var source = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, new uint[TitleExecutionRequest.GprCount], 0, 0, OpenBiosFirmware.ResetVector, request.InstructionBudget, [],
            OpenBiosFirmware.ResetVector, [], hostBridge: true,
            directLaunchHostRelativePath: RuntimeHostLayout.HostRelativePath, externalBios: true);
        if (!source.Success)
        {
            throw new InvalidOperationException(source.DiagnosticMessage ?? "Standalone source generation failed.");
        }

        BiosGuardWiring.Run(BiosGuardWiring.Source, () => BiosAddressGuard.RequireNoBiosSource(source.Source!));
        // The toolchain writes its outputs under a name chosen here at random, so no other process can have placed a file there in
        // advance. Its three names are reserved (each must not exist) before the toolchain can write them. After a successful build
        // the source and the binary are moved to the measurement's names; a move refuses an existing name.
        var staging = area.Root;
        var toolName = "host-" + Guid.NewGuid().ToString("N") + (windows ? ".exe" : string.Empty);
        area.Reserve(Path.Combine(staging, toolName + ".c"));
        area.Reserve(Path.Combine(staging, toolName + ".o"));
        area.Reserve(Path.Combine(staging, toolName));
        var build = new GeneratedHostBuildService(request.BuildTimeout ?? FirmwareDistributionBuilder.DefaultBuildTimeout).Build(
            new GeneratedHostBuildRequest(source.Source!, staging, toolName, FirmwareDistribution: true, CompilerExecutable: compiler));
        if (build.Status != GeneratedHostBuildStatus.Succeeded)
        {
            throw new InvalidOperationException($"Artifact build failed ({build.Status}). {build.DiagnosticMessage}");
        }

        area.MoveNew(Path.Combine(staging, toolName + ".c"), Path.Combine(staging, artifactFile + ".c"));
        return area.MoveNew(Path.Combine(staging, Path.GetFileName(build.Artifact!.BinaryPath)), Path.Combine(staging, artifactFile));
    }

    private static string ProvisionOrCopyHost(
        StagingArea area, string runtimeDir, FirmwareDiagnosticRequest request, bool windows, Action<string, string>? observe)
    {
        observe?.Invoke("provision", runtimeDir);
        if (request.HostSourceDirectory is null)
        {
            // The publish writes a set of files whose names are not known in advance; only the host executable is claimed by name.
            // Those other published files are not claimed, so after a failure they remain in the staging directory.
            area.Reserve(Path.Combine(runtimeDir, RuntimeHostLayout.HostFileName(windows)));
            try
            {
                RuntimeHostBuildService.ProvisionRuntimeHost(runtimeDir, selfContained: false, firmwareDistribution: true);
            }
            catch (RuntimeHostBuildService.RuntimeHostProvisionFailed exception)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }

            return "provisioned";
        }

        // A previously published host is copied as published (its files, unchanged); the run then uses those bytes.
        var source = Path.GetFullPath(request.HostSourceDirectory);
        if (!File.Exists(Path.Combine(source, RuntimeHostLayout.HostFileName(windows))))
        {
            throw new InvalidDataException($"The host directory does not hold {RuntimeHostLayout.HostFileName(windows)}: {source}");
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            area.CopyNewFile(file, Path.Combine(runtimeDir, Path.GetFileName(file)));
        }

        return "copied:" + Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static Dictionary<string, string> CurrentDigests(FirmwareDiagnosticRequest request)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rom"] = OpenBiosRomInput.Sha256HexOfFile(Path.GetFullPath(request.RomPath)),
            ["disc"] = OpenBiosRomInput.Sha256HexOfFile(Path.GetFullPath(request.DiscPath)),
        };
        var (aotInputs, _) = FirmwareAotBundle.Collect(Path.GetFullPath(request.AotDirectory));
        foreach (var input in aotInputs)
        {
            digests["aot:" + input.Path] = input.Sha256;
        }

        return digests;
    }

    private static JsonObject Summary(int? hostExitCode, string? firmwareRun, JsonNode? diagnostic)
    {
        var run = firmwareRun is null ? null : JsonNode.Parse(firmwareRun)?.AsObject();
        return new JsonObject
        {
            ["hostExitCode"] = hostExitCode,
            ["state"] = run?["state"]?.DeepClone(),
            ["guestPc"] = run?["guestPc"]?.DeepClone(),
            ["diagnosticCode"] = run?["diagnosticCode"]?.DeepClone(),
            ["nativeInstructions"] = run?["nativeInstructions"]?.DeepClone(),
            ["fallbackInstructions"] = run?["fallbackInstructions"]?.DeepClone(),
            ["fallbackTransitions"] = run?["fallbackTransitions"]?.DeepClone(),
            ["finalPc"] = diagnostic?["finalPc"]?.DeepClone(),
            ["transferTargetPc"] = diagnostic?["transferTargetPc"]?.DeepClone(),
            ["orchestratorState"] = diagnostic?["orchestratorState"]?.DeepClone(),
            ["orchestratorDiagnosticCode"] = diagnostic?["orchestratorDiagnosticCode"]?.DeepClone(),
            ["classifiedExitCode"] = diagnostic?["classifiedExitCode"]?.DeepClone(),
        };
    }

    private static void RequireSha256(string value, string name)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"{name} must be 64 hexadecimal digits.", name);
        }
    }

    private static void RequireRegularDisc(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The disc was not found: {path}");
        }

        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new InvalidDataException("The disc must be a regular file, not a symbolic link.");
        }
    }

    private static string Sha(string path) => OpenBiosRomInput.Sha256HexOfFile(path);

    /// <summary>
    /// One output stream of the host, read on its own thread and kept up to a limit. Its status tells a complete stream, a
    /// truncated one (the limit was reached), a read that failed (with its message) and a pipe still held open at the deadline.
    /// Only a complete stream is a whole output.
    /// </summary>
    private sealed class BoundedCapture
    {
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _text = new();
        private readonly Task _pump;
        private readonly int _limit;
        private bool _truncated;
        private bool _ended;
        private string? _readError;

        public BoundedCapture(StreamReader reader, int limit)
        {
            _limit = limit;
            _pump = Task.Run(() => Pump(reader));
        }

        private void Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (_gate)
                    {
                        var room = _limit - _text.Length;
                        if (room > 0)
                        {
                            _text.Append(buffer, 0, Math.Min(room, read));
                        }

                        if (read > room)
                        {
                            _truncated = true;
                        }
                    }
                }

                lock (_gate)
                {
                    _ended = true;
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                lock (_gate)
                {
                    _readError = exception.Message;
                }
            }
        }

        /// <summary>Waits at most <paramref name="drain"/> for the end of the stream, then takes what was read so far.</summary>
        public CapturedOutput Finish(TimeSpan drain)
        {
            var finished = _pump.Wait(drain);
            lock (_gate)
            {
                var status = _readError is not null ? "read-error"
                    : !finished || !_ended ? "pipe-held"
                    : _truncated ? "truncated"
                    : "complete";
                return new CapturedOutput(_text.ToString(), status, _truncated, _readError);
            }
        }
    }

    /// <summary>A captured stream: the text kept, and its status (complete, truncated, read-error or pipe-held).</summary>
    private sealed record CapturedOutput(string Text, string Status, bool Truncated, string? ReadError)
    {
        public JsonObject ToJson() => new()
        {
            ["status"] = Status,
            ["truncated"] = Truncated,
            ["readError"] = ReadError,
            ["characters"] = Text.Length,
        };
    }

    /// <summary>
    /// True when <paramref name="path"/> is a symbolic link or junction, including a dangling one: on Windows the link itself reports the
    /// reparse attribute (Build_DanglingJunctionAtTheOutputPath_IsNotFollowed checks it). Only a path that does not exist at all gives false.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The private staging directory one measurement is written into. Every entry is created exclusively, and ownership is recorded only
    /// after the exclusive create succeeded, so nothing that already existed (or that another process creates in a race) is adopted or
    /// removed. Directories are created with the Win32 call that fails on an existing name. Every write first checks that the staging
    /// directory and the chain below it are not links, and the cleanup never deletes through a link. The output name is claimed only by
    /// <see cref="Commit"/>, a rename that refuses an existing name.
    /// <para>
    /// Residual gap (not closed): the link check runs immediately before each create, but a same-user process that swaps a directory
    /// of the staging area for a junction in the few system calls between that check and the create is not excluded. Closing it needs a
    /// handle held on each directory without delete sharing, which this class does not take. The check narrows the window; it does not close it.
    /// </para>
    /// </summary>
    private sealed class StagingArea
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _outputDir;
        private readonly Action<string, string>? _observe;
        private readonly List<string> _ancestors = [];   // created by Open, shallowest first
        private readonly List<string> _owned = [];       // created by this build, recorded after each successful create
        private string? _root;

        public StagingArea(string outputDir, Action<string, string>? observe)
        {
            _outputDir = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _observe = observe;
        }

        /// <summary>The staging directory, once <see cref="Open"/> has been called.</summary>
        public string Root => _root ?? throw new InvalidOperationException("The staging area is not open.");

        /// <summary>Creates the missing parent directories and a new staging directory with a unique name beside the output, all exclusively.</summary>
        public void Open(string artifactName)
        {
            var parent = Path.GetDirectoryName(_outputDir)
                ?? throw new InvalidDataException($"The output directory has no parent directory: {_outputDir}");
            var missing = new List<string>();
            for (var path = parent; path is not null && !Path.Exists(path); path = Path.GetDirectoryName(path))
            {
                missing.Add(path);
            }

            missing.Reverse(); // shallowest first
            foreach (var path in missing)
            {
                _observe?.Invoke("directory", path); // SEAM: another process acts here, between the existence check and the create
                ExclusiveCreateDirectory(path);
                _ancestors.Add(path);
            }

            _root = Path.Combine(parent, $".{artifactName}.staging-{Guid.NewGuid():N}");
            ExclusiveCreateDirectory(_root);
        }

        /// <summary>
        /// Creates <paramref name="path"/> inside the staging directory. Each missing directory on the way is created exclusively and
        /// recorded only after the create succeeded. A directory that exists is never adopted: the exclusive create refuses it.
        /// </summary>
        public void CreateDirectory(string path)
        {
            RequireRealChain(path);
            var missing = new List<string>();
            for (var current = path; current is not null && !SamePath(current, Root) && !Path.Exists(current); current = Path.GetDirectoryName(current))
            {
                missing.Add(current); // deepest first
            }

            missing.Reverse(); // shallowest first
            foreach (var directory in missing)
            {
                _observe?.Invoke("directory", directory); // SEAM: another process acts here, between the existence check and the create
                RequireRealChain(directory);
                ExclusiveCreateDirectory(directory);
                _owned.Add(directory);
            }
        }

        /// <summary>Creates a new file with <paramref name="destination"/>'s name and copies the bytes of <paramref name="source"/> into it.</summary>
        public void CopyNewFile(string source, string destination)
        {
            _observe?.Invoke("file", destination); // SEAM: another process acts here, before the check made immediately before the write
            RequireRealChain(destination);
            using var input = File.OpenRead(source);
            using var output = CreateNewFile(destination);
            input.CopyTo(output);
        }

        /// <summary>Creates a new UTF-8 (no byte-order mark) text file; it fails if the name exists.</summary>
        public void WriteNewFile(string path, string text)
        {
            _observe?.Invoke("file", path); // SEAM: another process acts here, before the check made immediately before the write
            RequireRealChain(path);
            using var output = CreateNewFile(path);
            using var writer = new StreamWriter(output, Utf8WithoutBom);
            writer.Write(text);
        }

        /// <summary>Creates a hard link at <paramref name="link"/> to <paramref name="original"/> (CreateHardLink refuses an existing name).</summary>
        public void LinkNew(string original, string link)
        {
            _observe?.Invoke("file", link); // SEAM: another process acts here, before the check made immediately before the write
            RequireRealChain(link);
            LinkDiscInPlace(original, link);
            _owned.Add(link);
        }

        /// <summary>
        /// Reserves a name that a toolchain will write, when that name must not exist beforehand. Used only for names this build chose at random.
        /// </summary>
        public void Reserve(string path)
        {
            RequireRealChain(path);
            if (Path.Exists(path) || IsReparsePoint(path))
            {
                throw new IOException($"The staging area already holds an entry the measurement did not create: {path}");
            }

            _owned.Add(path);
        }

        /// <summary>Moves a file this build created to a name this build chooses. Refuses an existing destination.</summary>
        public string MoveNew(string source, string destination)
        {
            RequireRealChain(destination);
            File.Move(source, destination);
            _owned.Add(destination);
            return destination;
        }

        private FileStream CreateNewFile(string path)
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _owned.Add(path); // recorded only after the exclusive create succeeded
            return stream;
        }

        /// <summary>Creates one directory with the Win32 call that fails when the name exists (Directory.CreateDirectory would adopt it).</summary>
        private static void ExclusiveCreateDirectory(string path)
        {
            if (!WindowsDirectoryInterop.CreateDirectoryW(path, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(error == WindowsDirectoryInterop.ErrorAlreadyExists
                    ? $"A file, directory or link already exists at {path}; this build did not create it and does not use it."
                    : $"Could not create the directory {path} (Win32 error {error}).");
            }
        }


        /// <summary>The path <paramref name="path"/> has inside the published output.</summary>
        public string Rebase(string path) => Path.Combine(_outputDir, Path.GetRelativePath(Root, path));

        /// <summary>
        /// Renames the finished staging directory to the output name. Directory.Move refuses a name that is taken. Residual gap: the check
        /// just before the rename is not atomic with it. A same-user process that swaps the staging directory for a link in that interval
        /// can make the rename move the link. This is narrowed to no I/O between the check and the rename, and it is not closed.
        /// </summary>
        public void Commit()
        {
            RequireRealDirectory();
            Directory.Move(Root, _outputDir);
        }

        /// <summary>
        /// Removes what this build created, deepest first: the recorded entries, then the staging directory and the ancestors it created,
        /// each only when empty. Nothing is removed through a link. Returns the staging path when it is still present (it holds content
        /// this build did not create, or it is no longer a real directory), so the caller can name it; otherwise null.
        /// </summary>
        public string? Discard()
        {
            if (_root is not null && IsRealDirectory(_root))
            {
                foreach (var path in _owned.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length))
                {
                    if (!LinkBelowRoot(path))
                    {
                        RemoveOwnEntry(path);
                    }
                }

                RemoveIfEmpty(_root);
            }

            for (var index = _ancestors.Count - 1; index >= 0; index--)
            {
                RemoveIfEmpty(_ancestors[index]);
            }

            return _root is not null && Directory.Exists(_root) ? _root : null;
        }

        /// <summary>Fails closed unless the staging directory is still a real directory (not a link) and no directory on the way to <paramref name="path"/> is one.</summary>
        private void RequireRealChain(string path)
        {
            RequireRealDirectory();
            for (var current = path; current is not null && !SamePath(current, Root); current = Path.GetDirectoryName(current))
            {
                if (IsReparsePoint(current))
                {
                    throw new IOException($"A link was found inside the staging area; the measurement is not written: {current}");
                }
            }
        }

        private void RequireRealDirectory()
        {
            if (!IsRealDirectory(Root))
            {
                throw new IOException(
                    $"The staging directory is no longer a real directory (it was replaced or removed); the measurement is not written or published: {Root}");
            }
        }

        private bool LinkBelowRoot(string path)
        {
            for (var current = path; current is not null && !SamePath(current, Root); current = Path.GetDirectoryName(current))
            {
                if (IsReparsePoint(current))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRealDirectory(string path) => Directory.Exists(path) && !IsReparsePoint(path);

        private static bool SamePath(string a, string b) =>
            string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        private static void RemoveOwnEntry(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path); // not recursive: a non-empty directory (someone else's content) is kept
                }
                else if (File.Exists(path) || IsReparsePoint(path))
                {
                    File.Delete(path); // a hard link is removed by name only; the other name keeps the bytes
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort: the original failure is what the caller sees; an entry that cannot be removed is left in place.
            }
        }

        private static void RemoveIfEmpty(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not empty (or not removable): it stays.
            }
        }
    }
}
