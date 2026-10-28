using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// only when complete; a failure removes the entries this call created and never a directory it did not create.
    /// </summary>
    public static FirmwareDiagnosticBuild Build(FirmwareDiagnosticRequest request, bool windows)
        => Build(request, windows, observe: null);

    /// <summary>
    /// The build with an observation seam for tests: <paramref name="observe"/> is called with a phase name and a path at each point
    /// where another process could take the output name. Production callers use the overload without it.
    /// </summary>
    internal static FirmwareDiagnosticBuild Build(FirmwareDiagnosticRequest request, bool windows, Action<string, string>? observe)
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
        var area = new StagingArea(outputDir);
        try
        {
            area.Open(artifactName);
            var staging = area.Root;
            observe?.Invoke("staged", staging);

            var runtimeDir = Path.Combine(staging, RuntimeHostLayout.RuntimeDirectoryName);
            var firmwareDir = Path.Combine(runtimeDir, FirmwareBootManifest.FirmwareDirectory);
            area.Track(() =>
            {
                Directory.CreateDirectory(firmwareDir);
                FirmwareAotBundle.CopyInto(aotFiles, firmwareDir);
            });
            var (stagedAotInputs, _) = FirmwareAotBundle.Collect(firmwareDir);

            // The disc is linked into the runtime directory (a second name for the same file), never copied. The plan reads
            // the original path directly, so the disc the plan and the host read is the one the user named.
            var discLeaf = Path.GetExtension(discPath).Equals(".chd", StringComparison.OrdinalIgnoreCase) ? "disc.chd" : "disc.bin";
            var discDir = Path.Combine(runtimeDir, FirmwareBootManifest.DiscDirectory);
            area.Track(() =>
            {
                Directory.CreateDirectory(discDir);
                if (request.DirectDiscReference)
                {
                    // Nothing is linked or copied: the marker names the original, which the host opens read-only.
                    WriteDiscMarker(runtimeDir, discPath, request.DiscSha256);
                }
                else
                {
                    LinkDiscInPlace(discPath, Path.Combine(discDir, discLeaf));
                }
            });

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
                artifactPath = area.Produce(() => GenerateArtifact(plan, request, staging, artifactName, artifactFile, windows));
                artifactSource = "generated";
            }
            else
            {
                // The earlier artifact is copied under this measurement's name; its bytes are not rebuilt.
                artifactPath = area.Claim(Path.Combine(staging, artifactFile));
                File.Copy(Path.GetFullPath(request.ArtifactSourcePath), artifactPath, overwrite: false);
                artifactSource = "reused:" + Path.GetFileName(request.ArtifactSourcePath);
            }

            var hostSource = area.Produce(() => ProvisionOrCopyHost(runtimeDir, request, windows));

            var bootPath = area.Claim(Path.Combine(runtimeDir, FirmwareBootManifest.FileName));
            var boot = new FirmwareBootManifest(
                FirmwareBootManifest.CurrentSchemaVersion,
                BiosBackendSelection.NameOf(BiosBackendKind.OpenBios),
                request.CodeBytes,
                $"{FirmwareBootManifest.FirmwareDirectory}/{FirmwareAotBundle.RootsFileName}",
                $"{FirmwareBootManifest.FirmwareDirectory}/{FirmwareAotBundle.ImagesFileName}",
                $"{FirmwareBootManifest.DiscDirectory}/{discLeaf}",
                request.DiscSha256,
                stagedAotInputs);
            File.WriteAllText(bootPath, FirmwareBootManifestJson.Serialize(boot));

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
            var manifestPath = area.Claim(Path.Combine(runtimeDir, RuntimeHostLayout.ManifestFileName));
            File.WriteAllText(manifestPath, RuntimeHostManifestJson.Serialize(host));

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
            var identityPath = area.Claim(Path.Combine(staging, IdentityFileName));
            File.WriteAllText(identityPath, identity.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

            // Only a completely written measurement is published; the rename refuses a name that is taken by now.
            area.Commit();
            return new FirmwareDiagnosticBuild(
                outputDir, area.Rebase(artifactPath), area.Rebase(hostPath), area.Rebase(manifestPath), digests);
        }
        catch
        {
            area.Discard();
            throw;
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

    private static void WriteDiscMarker(string runtimeDir, string discPath, string sha256)
    {
        var marker = new JsonObject { ["schema"] = DiscMarkerSchema, ["path"] = discPath, ["sha256"] = sha256, ["access"] = "read-only" };
        File.WriteAllText(Path.Combine(runtimeDir, DiscMarkerFileName), marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
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
        FirmwareAotPlan plan, FirmwareDiagnosticRequest request, string outputDir, string artifactName, string artifactFile, bool windows)
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
        var build = new GeneratedHostBuildService(request.BuildTimeout ?? FirmwareDistributionBuilder.DefaultBuildTimeout).Build(
            new GeneratedHostBuildRequest(source.Source!, outputDir, artifactFile, FirmwareDistribution: true));
        if (build.Status != GeneratedHostBuildStatus.Succeeded)
        {
            throw new InvalidOperationException($"Artifact build failed ({build.Status}). {build.DiagnosticMessage}");
        }

        return Path.Combine(outputDir, Path.GetFileName(build.Artifact!.BinaryPath));
    }

    private static string ProvisionOrCopyHost(string runtimeDir, FirmwareDiagnosticRequest request, bool windows)
    {
        if (request.HostSourceDirectory is null)
        {
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
            File.Copy(file, Path.Combine(runtimeDir, Path.GetFileName(file)), overwrite: false);
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

    /// <summary>True when <paramref name="path"/> is a symbolic link or junction (or a dangling one). A missing path is not a link.</summary>
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
    /// The private staging directory one measurement is written into, and the entries that build itself created in it. The output name
    /// is claimed only by <see cref="Commit"/> (a rename that refuses an existing name). Cleanup removes only what this build created:
    /// a directory is removed only when it is empty, so content another process placed in the staging area is never deleted, and
    /// links are removed by name without following them.
    /// </summary>
    private sealed class StagingArea
    {
        private readonly string _outputDir;
        private readonly List<string> _ancestors = [];
        private readonly List<string> _owned = [];
        private string? _root;

        public StagingArea(string outputDir) => _outputDir = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>The staging directory, once <see cref="Open"/> has created it.</summary>
        public string Root => _root ?? throw new InvalidOperationException("The staging area is not open.");

        /// <summary>Creates the missing parent directories (recorded for cleanup) and a new staging directory with a unique name beside the output.</summary>
        public void Open(string artifactName)
        {
            var parent = Path.GetDirectoryName(_outputDir)
                ?? throw new InvalidDataException($"The output directory has no parent directory: {_outputDir}");
            for (var path = parent; path is not null && !Directory.Exists(path); path = Path.GetDirectoryName(path))
            {
                _ancestors.Add(path); // deepest first
            }

            Directory.CreateDirectory(parent);
            _root = Path.Combine(parent, $".{artifactName}.staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        /// <summary>Records one path this build writes. Recorded before the write, so a partial write is cleaned up too.</summary>
        public string Claim(string path)
        {
            _owned.Add(path);
            return path;
        }

        /// <summary>Runs a step that produces files this build does not name one by one; whatever it adds to the staging area is owned.</summary>
        public void Track(Action step) => Produce(() => { step(); return 0; });

        /// <summary>As <see cref="Track"/>, returning the step's result.</summary>
        public T Produce<T>(Func<T> step)
        {
            var before = Snapshot(Root);
            try
            {
                return step();
            }
            finally
            {
                _owned.AddRange(Snapshot(Root).Where(path => !before.Contains(path)));
            }
        }

        /// <summary>The path <paramref name="path"/> has inside the published output.</summary>
        public string Rebase(string path) => Path.Combine(_outputDir, Path.GetRelativePath(Root, path));

        /// <summary>Renames the finished staging directory to the output name. Directory.Move refuses a name that is taken.</summary>
        public void Commit() => Directory.Move(Root, _outputDir);

        /// <summary>Removes what this build created: the owned entries deepest first, then the staging and parent directories if they are empty.</summary>
        public void Discard()
        {
            foreach (var path in _owned.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length))
            {
                RemoveOwnEntry(path);
            }

            if (_root is not null)
            {
                RemoveIfEmpty(_root);
            }

            foreach (var ancestor in _ancestors)
            {
                RemoveIfEmpty(ancestor);
            }
        }

        /// <summary>Every entry under <paramref name="root"/>, not following links into their targets.</summary>
        private static HashSet<string> Snapshot(string root)
        {
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    entries.Add(entry);
                    if (Directory.Exists(entry) && !IsReparsePoint(entry))
                    {
                        pending.Push(entry);
                    }
                }
            }

            return entries;
        }

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
