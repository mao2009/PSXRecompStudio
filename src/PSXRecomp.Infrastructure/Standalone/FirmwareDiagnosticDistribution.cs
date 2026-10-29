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
    IReadOnlyDictionary<string, string> InputDigests,
    IReadOnlyList<string>? Residuals = null);

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

    /// <summary>The longest the launched host may take to exit after it has been killed before the measurement reports it still running.</summary>
    private static readonly TimeSpan KillAwaitBound = TimeSpan.FromSeconds(10);

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
    /// name, and nothing deletes it automatically. The runtime-host publish writes into a private directory and each entry is moved into
    /// <c>runtime/</c> with a refusing move; an entry a failed publish leaves in its private directory is kept, not removed. Remove a kept
    /// staging directory by hand after checking it. Paths longer than about 247 characters are refused before any write, with the limit named.
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
    /// <param name="publisher">
    /// Publishes the Runtime host into the directory it is given (the publish step). Null runs <c>dotnet publish</c>. Tests pass a stub that
    /// writes the files the publish would write, so the publish path is exercised without a toolchain.
    /// </param>
    internal static FirmwareDiagnosticBuild Build(
        FirmwareDiagnosticRequest request, bool windows, Action<string, string>? observe, string? compiler, Action<string>? publisher = null)
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

        // The CLI passes the host OS, so windows: false is reachable only by a caller that asks for an extensionless artifact on Windows.
        // The measurement names its artifact with .exe (the manifest and the identity depend on it), so that request is refused.
        if (!windows)
        {
            throw new NotSupportedException(
                "The measurement names its artifact with the Windows .exe suffix; windows: false (an extensionless artifact) is not supported.");
        }

        // A host directory is checked for links in full before anything is written, so a link refuses the build before the AOT copy,
        // the disc link or the artifact copy can run. The copy checks each entry again for links, and it reads each file's link count again
        // immediately before that file is copied, so a link created after this pre-check is refused as well.
        if (request.HostSourceDirectory is not null)
        {
            RequireNoLinksInHostDirectory(Path.GetFullPath(request.HostSourceDirectory));
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

            var hostSource = ProvisionOrCopyHost(area, runtimeDir, request, windows, observe, publisher ?? PublishRuntimeHost);

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

            // Only a completely written measurement is published. The commit checks that every entry in the staging area is one this build created,
            // then renames the directory; the rename refuses a name that is taken by now (nothing is replaced).
            observe?.Invoke("commit", staging); // SEAM: another process acts here, between the last write and the rename
            area.Commit();
            // Residuals are reported on the result, so an entry that cleanup left in place (for example, a record step that failed) is visible to the operator
            // even when the measurement itself succeeded.
            return new FirmwareDiagnosticBuild(
                outputDir, area.Rebase(artifactPath), area.Rebase(hostPath), area.Rebase(manifestPath), digests, area.Residuals.ToArray());
        }
        catch (Exception exception)
        {
            var kept = area.Discard();
            if (kept is null && area.Residuals.Count == 0)
            {
                throw;
            }

            var keptText = kept is null ? string.Empty : $" Its staging directory was kept at {kept} (remove it by hand after checking it).";
            var residualText = area.Residuals.Count == 0
                ? string.Empty
                : " Cleanup left these entries in place, because this build could not show they are its own: " + string.Join("; ", area.Residuals) + ".";
            throw new IOException($"The measurement failed.{keptText}{residualText} {exception.Message}", exception);
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
    public static JsonObject Measure(FirmwareDiagnosticBuild build, FirmwareDiagnosticRequest request, TimeSpan? exitGrace = null) =>
        Measure(build, request, exitGrace, afterLaunch: null, beforeRelease: null);

    /// <summary>
    /// The measurement with observation points for tests. <paramref name="afterLaunch"/> receives the launched host's Process object, inside
    /// the guarded wait, so a test can make the wait fail. <paramref name="beforeRelease"/> receives whether the launched host is still running,
    /// read after the finally block has killed it and before the Process object is released; the test never touches the process itself.
    /// </summary>
    internal static JsonObject Measure(
        FirmwareDiagnosticBuild build, FirmwareDiagnosticRequest request, TimeSpan? exitGrace, Action<Process>? afterLaunch,
        Action<bool>? beforeRelease = null)
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
        var clock = Stopwatch.StartNew();
        var timedOut = false;
        string? killFailure = null;
        try
        {
            afterLaunch?.Invoke(process);
            stop.Attach(process, cancel.Token);
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
        }
        finally
        {
            // A fault that leaves the wait (in the sampler, or in a wait call) must not leave the host running. The host this method launched is
            // killed through its own Process object and awaited here. A kill that fails is reported after the wait, and it never replaces the
            // exception already in flight, because nothing is thrown from this block.
            killFailure = KillLaunchedTreeIfRunning(process);
            beforeRelease?.Invoke(!process.HasExited);
        }

        if (killFailure is not null)
        {
            throw new InvalidOperationException(killFailure);
        }

        stop.Seal();
        var (confirmed, refused) = stop.Outcome;
        var residuals = stop.Residuals;
        var exitCode = process.HasExited ? process.ExitCode : (int?)null;

        process.StandardInput.Dispose();
        var stdoutCapture = stdout.Finish(PipeDrainBound);
        var stderrCapture = stderr.Finish(PipeDrainBound);
        WriteNewMeasurementFile(Path.Combine(build.Root, HostStdoutFileName), stdoutCapture.Text);
        WriteNewMeasurementFile(Path.Combine(build.Root, HostStderrFileName), stderrCapture.Text);

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

        // Every malformed shape (invalid JSON, a non-object, a field that is not a string) is refused with the same type as the other checks.
        string markerText;
        try
        {
            markerText = File.ReadAllText(marker);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The diagnostic disc marker could not be read (it may be locked or unreadable).", exception);
        }

        string? schema;
        string? path;
        try
        {
            var node = JsonNode.Parse(markerText)?.AsObject()
                ?? throw new InvalidDataException("The diagnostic disc marker is not a JSON object.");
            schema = node["schema"]?.GetValue<string>();
            path = node["path"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The diagnostic disc marker is malformed.", exception);
        }

        if (schema != DiscMarkerSchema)
        {
            throw new InvalidDataException("The diagnostic disc marker has an unknown schema.");
        }

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
        return HardLinkCountOf(stream.SafeFileHandle);
    }

    /// <summary>The number of directory entries of the file that an open handle refers to, read from that handle.</summary>
    internal static uint HardLinkCountOf(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!WindowsHardLinkInterop.GetFileInformationByHandle(handle.DangerousGetHandle(), out WindowsHardLinkInterop.ByHandleFileInformation info))
        {
            throw new IOException($"Could not read the file information (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        return info.NumberOfLinks;
    }

    /// <summary>
    /// Writes a measurement output file, which must not exist yet. The file is created exclusively: an existing file, a link at that name, or a
    /// file another process created is refused with an exception, and is never overwritten. A rerun writes into a new output directory.
    /// </summary>
    public static void WriteNewMeasurementFile(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    /// <summary>
    /// Kills the host's process tree when it is still running, and waits for it. It acts only on the Process object that <see cref="Measure"/>
    /// launched. Returns null when the host is no longer running afterwards; otherwise the reason it still runs. A kill that fails, or that
    /// races a natural exit, is recorded in that reason and never thrown from a finally block.
    /// </summary>
    private static string? KillLaunchedTreeIfRunning(Process process)
    {
        if (process.HasExited)
        {
            return null;
        }

        string? killError = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            killError = exception.Message;
        }

        if (process.WaitForExit((int)KillAwaitBound.TotalMilliseconds))
        {
            return null;
        }

        return killError is null
            ? "The runtime host did not exit after it was killed; it may still be running."
            : $"The runtime host could not be killed ({killError}) and did not exit; it may still be running.";
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

    /// <summary>
    /// The files the framework-dependent Runtime host publish writes, by exact name (the allow-list). A publish output entry that is not on
    /// this list is refused, and a listed name that the publish did not write is refused as an incomplete publish. The native library name is
    /// the NativeLibName of PSXRecomp.Core.csproj; a real publish of the host produces it (checked by a test that reads the csproj files and by
    /// the end-to-end publish test). A dependency change needs this list updated by hand (matrix rule 6, amendment (c)).
    /// </summary>
    internal static string[] RuntimePublishAllowList(bool windows) =>
    [
        RuntimeHostLayout.HostFileName(windows),
        RuntimeHostLayout.HostBinaryName + ".dll",
        RuntimeHostLayout.HostBinaryName + ".deps.json",
        RuntimeHostLayout.HostBinaryName + ".runtimeconfig.json",
        "PSXRecomp.Core.dll",
        "PSXRecomp.Infrastructure.dll",
        RuntimeNativeLibraryName,
        "SharpCompress.dll",
    ];

    /// <summary>The native library the Runtime host loads, by platform (the NativeLibName values of PSXRecomp.Core.csproj).</summary>
    /// <remarks>
    /// The name is hard-coded: it is not derived from the csproj at run time. The drift test
    /// (RuntimePublishAllowList_MatchesTheSetTheProjectFilesDeclareForThePublish) is the only check against NativeLibName in PSXRecomp.Core.csproj.
    /// Windows is the only supported host: Build refuses every other platform before any output is created, so no other name is used here.
    /// </remarks>
    internal const string RuntimeNativeLibraryName = "PSXRecomp.Native.dll";

    /// <summary>The publish step: the Runtime host is published into the directory given, framework-dependent, without debug symbols.</summary>
    private static void PublishRuntimeHost(string publishDir)
    {
        try
        {
            RuntimeHostBuildService.ProvisionRuntimeHost(publishDir, selfContained: false, firmwareDistribution: true);
        }
        catch (RuntimeHostBuildService.RuntimeHostProvisionFailed exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private static string ProvisionOrCopyHost(
        StagingArea area, string runtimeDir, FirmwareDiagnosticRequest request, bool windows, Action<string, string>? observe,
        Action<string> publisher)
    {
        observe?.Invoke("provision", runtimeDir);
        if (request.HostSourceDirectory is null)
        {
            // The publish writes into a private directory this build creates ("publish", the same length as "runtime", so an entry's path
            // inside it is as long as its destination). Nothing is moved out of it and nothing in it is adopted: the allow-listed files are
            // copied into runtime/ with exclusive creation, so each copy is one this build created and tracks from its creation. The
            // directory is then emptied (the copies' sources are removed only while they are still the files that were checked) and removed.
            var publishDir = area.CreateDirectory(Path.Combine(area.Root, "publish"));
            try
            {
                publisher(publishDir);
            }
            catch
            {
                // A failed publish records the allow-listed files it wrote as this run's, so cleanup removes them. The publisher's exception stays the
                // primary one and is rethrown unchanged; a failure to record is a residual, never a replacement for that exception.
                area.TryRecordPublishOutput(publishDir, RuntimePublishAllowList(windows));
                throw;
            }

            // A refusal of the output after a successful publish (an unlisted entry, a second link) also leaves the allow-listed files recorded, so the
            // cleanup that the refusal triggers removes them. The record is repeated by the copy step, which keeps each source's checked identity.
            area.TryRecordPublishOutput(publishDir, RuntimePublishAllowList(windows));
            area.CopyPublishedHost(publishDir, runtimeDir, RuntimePublishAllowList(windows));
            return "provisioned";
        }

        // A previously published host is copied as published (its files, unchanged); the run then uses those bytes.
        var source = Path.GetFullPath(request.HostSourceDirectory);
        if (IsReparsePoint(source))
        {
            // The entries below are checked for links, but a junction at the host directory itself would be followed before any entry is seen.
            throw new InvalidDataException($"The host directory is a link, which the copy refuses: {source}");
        }

        if (!File.Exists(Path.Combine(source, RuntimeHostLayout.HostFileName(windows))))
        {
            throw new InvalidDataException($"The host directory does not hold {RuntimeHostLayout.HostFileName(windows)}: {source}");
        }

        area.CopyTree(source, runtimeDir);

        return "copied:" + Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>Refuses a host directory that is a link, or that holds a link anywhere below it, by name. Nothing is written when it refuses.</summary>
    private static void RequireNoLinksInHostDirectory(string source)
    {
        if (IsReparsePoint(source))
        {
            throw new InvalidDataException($"The host directory is a link, which the copy refuses: {source}");
        }

        RequireNoLinksBelow(source);
    }

    private static void RequireNoLinksBelow(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsReparsePoint(entry))
            {
                throw new InvalidDataException($"The host directory holds a link, which the copy refuses: {entry}");
            }

            if (Directory.Exists(entry))
            {
                RequireNoLinksBelow(entry);
            }
            else
            {
                RequireSingleHardLink(entry);
            }
        }
    }

    /// <summary>
    /// Refuses a host file with more than one hard link. Its bytes are the bytes of another directory entry, which may lie outside the host
    /// directory, so copying it would copy content the host directory does not own. A count that cannot be read is refused too: the copy
    /// fails closed rather than copy a file whose link count is unknown. This applies only to the host copy, not to the disc link.
    /// </summary>
    private static void RequireSingleHardLink(string file)
    {
        uint links;
        try
        {
            links = HardLinkCount(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The hard-link count of the host file {file} could not be read, so the copy refuses it. Guarantee: no host file whose link count is unknown is copied.",
                exception);
        }

        if (links > 1)
        {
            throw new InvalidDataException(
                $"The host file {file} has {links} hard links, and the copy refuses a file with more than one link, because its bytes may come from another entry outside the host directory. Guarantee: no host file with more than one link is copied.");
        }
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
            bool finished;
            try
            {
                finished = _pump.Wait(drain);
            }
            catch (AggregateException exception)
            {
                // The pump faulted with an exception it does not handle; it is reported as a read error, not thrown to the caller.
                lock (_gate)
                {
                    _readError = exception.Flatten().InnerException?.Message ?? exception.Message;
                }

                finished = true;
            }

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
    internal sealed class StagingArea
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _outputDir;
        private readonly Action<string, string>? _observe;
        private readonly List<(string Path, EntryIdentity? Identity)> _ancestors = []; // created by Open, shallowest first
        // Created by this build: each entry with the identity (volume and file index) read right after its create or move. A null identity
        // means it could not be read; such an entry is never removed. Reserved names are the ones a toolchain writes later; they have no identity.
        private readonly Dictionary<string, EntryIdentity?> _owned = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _residuals = [];   // entries cleanup found and left in place, each with its reason
        // The allow-listed files the publish step wrote into its publish directory, each with the identity checked before it was copied.
        // They are removed by cleanup only while they are still that file (see RemovePublishSources). They are not owned entries: nothing
        // is moved from them and nothing is adopted into the owned set.
        private readonly Dictionary<string, EntryIdentity> _publishSources = new(StringComparer.OrdinalIgnoreCase);
        private EntryIdentity? _rootIdentity;
        private string? _root;

        /// <summary>
        /// The identity of a file or directory: its volume serial number, its file index (the same for every name of one file) and its creation
        /// time. NTFS can reuse a file index after a delete, so the creation time is compared too. Equal values for all three are taken as the
        /// same entry; that is a residual, since a reuse of all three cannot be excluded from these values alone.
        /// </summary>
        private readonly record struct EntryIdentity(uint Volume, uint IndexHigh, uint IndexLow, long CreationTime);

        /// <summary>
        /// Reads the identity of the entry at <paramref name="path"/>, opened as itself (a link is not followed). Null when the entry is missing
        /// or its identity cannot be read.
        /// </summary>
        private static EntryIdentity? IdentityOfPath(string path)
        {
            using var handle = WindowsHardLinkInterop.OpenForIdentity(path);
            if (handle.IsInvalid)
            {
                return null;
            }

            if (!WindowsHardLinkInterop.GetFileInformationByHandle(handle.DangerousGetHandle(), out var information))
            {
                return null;
            }

            return new EntryIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow, information.CreationTime);
        }

        /// <summary>Records an entry this build has just created or moved, with the identity read right after it.</summary>
        private void Own(string path) => _owned[path] = IdentityOfPath(path);

        /// <summary>Entries cleanup found and left in place, each with its path and the reason. Empty when cleanup removed everything it owned.</summary>
        public IReadOnlyList<string> Residuals => _residuals;

        public StagingArea(string outputDir, Action<string, string>? observe)
        {
            _outputDir = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _observe = observe;
            _fileName = Path.GetFileName(_outputDir);
        }

        private readonly string _fileName;

        /// <summary>The staging directory, once <see cref="Open"/> has been called.</summary>
        public string Root => _root ?? throw new InvalidOperationException("The staging area is not open.");

        /// <summary>Creates the missing parent directories and a new staging directory with a unique name beside the output, all exclusively.</summary>
        public void Open(string artifactName)
        {
            var parent = Path.GetDirectoryName(_outputDir)
                ?? throw new InvalidDataException($"The output directory has no parent directory: {_outputDir}");
            // The staging root is the longest path this build creates, and it is checked before any ancestor is created, so a refused
            // output path creates nothing at all.
            _root = Path.Combine(parent, $".{artifactName}.staging-{Guid.NewGuid():N}");
            RequireWithinPathLimit(_root);

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
                _ancestors.Add((path, IdentityOfPath(path)));
            }

            ExclusiveCreateDirectory(_root);
            _rootIdentity = IdentityOfPath(_root);
        }

        /// <summary>
        /// Creates <paramref name="path"/> inside the staging directory. Each missing directory on the way is created exclusively and
        /// recorded only after the create succeeded. A directory that exists is never adopted: the exclusive create refuses it.
        /// </summary>
        public string CreateDirectory(string path)
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
                Own(directory);
            }

            return path;
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

        /// <summary>
        /// Copies one host file. The source is opened once, its link count is read from that same handle, and the copy reads from that handle.
        /// A count above one is refused before the destination is created. Reading the count and copying use one open file, so a link added to the
        /// file before the open is seen, and the copy never re-opens the file by path.
        /// </summary>
        public void CopyHostFile(string source, string destination)
        {
            _observe?.Invoke("file", destination); // SEAM: another process acts here, before the open and the count
            RequireRealChain(destination);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint links;
            try
            {
                links = HardLinkCountOf(input.SafeFileHandle);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"The hard-link count of the host file {source} could not be read, so the copy refuses it. Guarantee: no host file whose link count is unknown is copied.",
                    exception);
            }

            if (links > 1)
            {
                throw new InvalidDataException(
                    $"The host file {source} has {links} hard links, and the copy refuses a file with more than one link, because its bytes may come from another entry outside the host directory. Guarantee: no host file with more than one link is copied.");
            }

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

        /// <summary>
        /// Copies a directory tree into <paramref name="destination"/>: every file is created exclusively and every subdirectory is created
        /// exclusively. A link anywhere in the source tree is refused by name, because a copy would follow it outside the source.
        /// </summary>
        public void CopyTree(string source, string destination)
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                if (IsReparsePoint(file))
                {
                    throw new InvalidDataException($"The host directory holds a link, which the copy refuses: {file}");
                }

                // Each file is copied through CopyHostFile: the link count is read from the same open handle that the copy reads from, so a hard link
                // added after the path-based pre-check, at any point before this file is opened, is refused, and the build publishes nothing.
                CopyHostFile(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                if (IsReparsePoint(directory))
                {
                    throw new InvalidDataException($"The host directory holds a link, which the copy refuses: {directory}");
                }

                CopyTree(directory, CreateDirectory(Path.Combine(destination, Path.GetFileName(directory))));
            }
        }

        /// <summary>Creates a hard link at <paramref name="link"/> to <paramref name="original"/> (CreateHardLink refuses an existing name).</summary>
        public void LinkNew(string original, string link)
        {
            _observe?.Invoke("file", link); // SEAM: another process acts here, before the check made immediately before the write
            RequireRealChain(link);
            RequireWithinPathLimit(link);
            LinkDiscInPlace(original, link);
            Own(link);
        }

        /// <summary>
        /// Reserves a name that a toolchain will write, when that name must not exist beforehand. Used only for names this build chose at random.
        /// </summary>
        public void Reserve(string path)
        {
            RequireRealChain(path);
            RequireWithinPathLimit(path);
            if (Path.Exists(path) || IsReparsePoint(path))
            {
                throw new IOException($"The staging area already holds an entry the measurement did not create: {path}");
            }

            // The toolchain creates this name after the reserve, so no identity is read here. Cleanup removes a reserved name by name
            // only, and only while it is a file that is not a link (see RemoveOwned). The window in which another process could replace it
            // before cleanup is a residual of the same-user threat model.
            _owned[path] = null;
            _reserved.Add(path);
        }

        /// <summary>
        /// Moves a file this build created to a name this build chooses. The move refuses an existing destination (File.Move does not
        /// replace), so a file another process placed at that name is never overwritten.
        /// </summary>
        public string MoveNew(string source, string destination)
        {
            RequireRealChain(destination);
            RequireWithinPathLimit(destination);
            RefuseExisting(destination);
            var recorded = RequireMovableSource(source);

            // MoveNew is for the toolchain's reserved names only. A publish output, or any other owned file, is copied, never moved (owner ruling 1-3).
            if (!_reserved.Contains(source))
            {
                throw new IOException($"MoveNew moves reserved toolchain names only; this owned file is not moved into the measurement: {source}");
            }

            // The source's identity is read immediately before the move and the destination's immediately after it. The destination is adopted
            // only when the two reads match (and, for a recorded entry, the source still matches its record). A reserved name has no record, so
            // the read before the move is the only identity available for it; that is why the post-move read is compared.
            var before = IdentityOfPath(source) ?? throw new IOException(
                $"The identity of a toolchain output cannot be read, so it is not moved into the measurement: {source}");
            if (recorded is { } expected && before != expected)
            {
                Residual(source, "it is not the entry recorded for it (it was replaced), so it is not moved");
                throw new IOException($"A file this build created was replaced, so it is not moved into the measurement: {source}");
            }

            File.Move(source, destination);
            _observe?.Invoke("moved", destination); // SEAM: another process acts here, between the move and the identity read

            if (IdentityOfPath(destination) != before)
            {
                Residual(destination, "it is not the entry that was moved (it was replaced between the move and its check), so it is not adopted");
                throw new IOException($"A file changed between its move and its check, so the measurement is not written: {destination}");
            }

            // The destination is adopted only because the source was owned, and only with the identity both reads agree on.
            _owned.Remove(source);
            _reserved.Remove(source);
            _owned[destination] = before;
            return destination;
        }

        /// <summary>
        /// Checks a source before the toolchain's reserved names are moved (the only remaining use of a move: the publish output is copied, never
        /// moved). Its parent chain must be real directories up to the staging root (a link anywhere on the way fails closed). It must be an entry
        /// this build created (a source that is not owned is never moved, and its destination is never recorded as owned). Its identity must still
        /// match the one recorded for it. A reserved name has no recorded identity; it is moved only while it is not a link. Returns the recorded
        /// identity, or null for a reserved name. A refusal is recorded as a residual.
        /// </summary>
        private EntryIdentity? RequireMovableSource(string source)
        {
            RequireRealChain(source);
            if (!_owned.TryGetValue(source, out var recorded))
            {
                Residual(source, "it was not created or adopted by this build, so it is not moved into the measurement");
                throw new IOException($"A publish entry that this build did not create is not moved into the measurement: {source}");
            }

            if (_reserved.Contains(source))
            {
                if (IsReparsePoint(source))
                {
                    Residual(source, "a link was found where a reserved name should be, so it is not moved");
                    throw new IOException($"A link was found where a reserved name should be; it is not moved into the measurement: {source}");
                }

                return null;
            }

            if (recorded is null)
            {
                Residual(source, "its identity was not recorded when it was created or adopted, so it is not moved");
                throw new IOException($"The identity of a publish entry cannot be shown, so it is not moved into the measurement: {source}");
            }

            if (IdentityOfPath(source) != recorded)
            {
                Residual(source, "it is not the entry recorded for it (it was replaced), so it is not moved");
                throw new IOException($"A publish entry was replaced, so it is not moved into the measurement: {source}");
            }

            return recorded;
        }


        /// <summary>
        /// Removes a directory this build created (the publish directory, once the allow-listed copies are done) when it is empty. It is deleted
        /// only while its identity recorded at creation still matches. Anything still in it is not removed with it: the call fails instead.
        /// </summary>
        public void ReleaseEmptyDirectory(string path)
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new IOException($"The published directory still holds entries that were not moved into the measurement: {path}");
            }

            // The identity read when this build created the directory must still match, immediately before the delete. Otherwise the directory
            // is not this build's (it was replaced, or its identity cannot be read): it is not deleted, and it is recorded as a residual.
            if (!_owned.TryGetValue(path, out var recorded) || recorded is null)
            {
                Residual(path, "its identity was not recorded when it was created, so it is not deleted");
                throw new IOException($"The published directory cannot be shown to be this build's, so it is not deleted: {path}");
            }

            if (IdentityOfPath(path) != recorded)
            {
                Residual(path, "it is not the directory this build created (it was replaced, or it cannot be identified now), so it is not deleted");
                throw new IOException($"The published directory was replaced, so it is not deleted: {path}");
            }

            Directory.Delete(path);
            _owned.Remove(path);
        }

        private static void RefuseExisting(string path)
        {
            if (Path.Exists(path) || IsReparsePoint(path))
            {
                throw new IOException($"A file, directory or link already exists at {path}; this build did not create it and does not use it.");
            }
        }

        /// <summary>
        /// Refuses a path longer than <see cref="MaxPathCharacters"/> before a write, so the refusal names the limit rather than a raw Win32 code.
        /// One bound is applied to every write. The code does not claim it is the platform's exact limit for each call type, and it is not
        /// verified on other machines or configurations. The message states the limit in terms of the output path the user sets.
        /// </summary>
        private void RequireWithinPathLimit(string path)
        {
            if (path.Length > MaxPathCharacters)
            {
                throw new IOException(
                    $"The path is {path.Length} characters long, over the {MaxPathCharacters}-character limit this build uses without long-path support: {path}. " +
                    $"The output path is {_outputDir.Length} characters and its file name is {_fileName.Length} characters. " +
                    "A generated artifact needs the output path at most 161 characters (its reserved toolchain name is the output path plus 86 characters), " +
                    "and the output path plus the file-name length at most 198 characters (the artifact's .c name is that sum plus 49). " +
                    "Checked before each write: the paths this build names itself, and each copied host or artifact file name. " +
                    "Not checked before dotnet publish writes them: the names the runtime-host publish chooses. Each allow-listed name is checked when it is " +
                    "copied into runtime/, and its path in the publish directory has the same length as that destination.");
            }
        }

        private const int MaxPathCharacters = 247;

        private FileStream CreateNewFile(string path)
        {
            RequireWithinPathLimit(path);
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Own(path); // recorded only after the exclusive create succeeded
            return stream;
        }

        /// <summary>Creates one directory with the Win32 call that fails when the name exists (Directory.CreateDirectory would adopt it).</summary>
        private void ExclusiveCreateDirectory(string path)
        {
            RequireWithinPathLimit(path);
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
        /// The failure-path form of <see cref="RecordPublishOutput"/>: it never throws (an I/O failure while recording is recorded as a residual),
        /// so it cannot replace the publish exception that is being rethrown.
        /// </summary>
        public void TryRecordPublishOutput(string publishDir, IReadOnlyList<string> allowList)
        {
            try
            {
                RecordPublishOutput(publishDir, allowList);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Residual(publishDir, $"the publish output could not be recorded for cleanup, so it is left in place ({exception.Message})");
            }
        }

        /// <summary>
        /// Records the allow-listed files a publish wrote into <paramref name="publishDir"/> as this run's, each with its identity, so that a
        /// failed publish (or a refused step) removes them on cleanup. An entry that is not on the list is never recorded, so it is never removed.
        /// </summary>
        public void RecordPublishOutput(string publishDir, IReadOnlyList<string> allowList)
        {
            _observe?.Invoke("record", publishDir); // SEAM: a failure here (the record or its enumeration) is the one the record path must survive
            var publishFull = Path.GetFullPath(publishDir);
            if (!Directory.Exists(publishFull) || IsReparsePoint(publishFull))
            {
                return;
            }

            var allowed = new HashSet<string>(allowList, StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(publishFull))
            {
                if (!allowed.Contains(Path.GetFileName(entry)) || IsReparsePoint(entry) || Directory.Exists(entry))
                {
                    continue;
                }

                if (IdentityOfPath(entry) is { } identity)
                {
                    _publishSources[entry] = identity;
                }
            }
        }

        /// <summary>
        /// Copies one publish output file with exclusive creation. The link count is read from the same open handle the copy reads from, and a
        /// count above one refuses the file before the destination is created (the same guarantee as <see cref="CopyHostFile"/>).
        /// </summary>
        private void CopyPublishedFile(string source, string destination)
        {
            _observe?.Invoke("file", destination); // SEAM: another process acts here, before the open and the count
            RequireRealChain(destination);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint links;
            try
            {
                links = HardLinkCountOf(input.SafeFileHandle);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"The hard-link count of the publish output {source} could not be read, so it is not copied. Guarantee: no publish output whose link count is unknown is copied.",
                    exception);
            }

            if (links > 1)
            {
                throw new InvalidDataException(
                    $"The publish output {source} has {links} hard links, and it is not copied, because its bytes may come from another entry. Guarantee: no publish output with more than one link is copied.");
            }

            using var output = CreateNewFile(destination);
            input.CopyTo(output);
        }

        /// <summary>
        /// Copies the Runtime host's publish output into <paramref name="runtimeDir"/>, and nothing else. The publish output must hold exactly
        /// the allow-listed files: a link, a directory, an entry that is not on the list, or an entry whose path is not the canonical path of
        /// its name under the publish directory refuses the step, and so does a listed file the publish did not write. This is checked before
        /// anything is copied. Each listed file is copied with exclusive creation (<see cref="CopyPublishedFile"/>), so each copy is tracked from its
        /// creation. Nothing is moved, and nothing is adopted. Afterwards the listed source files are removed, each only while it is still the
        /// file that was checked, and the publish directory is removed only when it is empty.
        /// </summary>
        public void CopyPublishedHost(string publishDir, string runtimeDir, IReadOnlyList<string> allowList)
        {
            RequireRealChain(publishDir);
            var publishFull = Path.GetFullPath(publishDir);
            var allowed = new HashSet<string>(allowList, StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(publishFull))
            {
                var name = Path.GetFileName(entry);
                if (IsReparsePoint(entry))
                {
                    throw new IOException($"A link was found in the publish output, so the measurement is not written: {entry}");
                }

                if (Directory.Exists(entry) || !allowed.Contains(name)
                    || !string.Equals(Path.GetFullPath(entry), Path.Combine(publishFull, name), StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"The publish output holds an entry that is not on the allow-list, so the measurement is not written: {entry}");
                }

                // Every publish entry is checked for a second hard link before anything is copied: a link would make its bytes come from another name.
                RequireSingleHardLink(entry);
            }

            var sources = new List<string>(allowList.Count);
            foreach (var name in allowList)
            {
                var source = Path.Combine(publishFull, name);
                if (!File.Exists(source))
                {
                    throw new IOException($"The publish output is incomplete ({name} is missing), so the measurement is not written.");
                }

                _publishSources[source] = IdentityOfPath(source)
                    ?? throw new IOException($"The identity of publish output {name} cannot be read, so the measurement is not written.");
                sources.Add(source);
            }

            foreach (var source in sources)
            {
                CopyPublishedFile(source, Path.Combine(runtimeDir, Path.GetFileName(source)));
            }

            _observe?.Invoke("publish-copied", publishFull); // SEAM: the copies exist and the publish output is still in place
            RemovePublishSources();
            ReleaseEmptyDirectory(publishDir);
        }

        /// <summary>
        /// Removes the publish output files this build checked, each only while it is still the file that was checked. A file that cannot be
        /// removed, or whose parent chain holds a link, is recorded as a residual and left in place. The publish directory is then not empty,
        /// so its release refuses.
        /// </summary>
        private void RemovePublishSources()
        {
            foreach (var (path, identity) in _publishSources.ToList())
            {
                if (LinkBelowRoot(path))
                {
                    Residual(path, "a link is on its way to it, so it is not followed and not removed");
                }
                else
                {
                    RemoveOwned(path, identity, reserved: false);
                }
            }

            _publishSources.Clear();
        }

        /// <summary>
        /// Refuses to publish a staging area that holds an entry this build did not create. An entry is one this build created when it is
        /// recorded as owned and, where an identity was recorded, it is still that entry and not a link. Every entry under the staging root is
        /// checked, so an unowned file that another process placed in the staging area is never published, and is recorded as a residual.
        /// </summary>
        private void RequireOnlyOwnedEntries()
        {
            var unowned = new List<string>();
            CollectUnowned(Root, unowned);
            if (unowned.Count == 0)
            {
                return;
            }

            foreach (var entry in unowned)
            {
                Residual(entry, "it was not created by this build, so it is not published");
            }

            throw new IOException("The staging area holds entries this build did not create, so the measurement is not published: " + string.Join("; ", unowned));
        }

        private void CollectUnowned(string directory, List<string> unowned)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var owned = _owned.TryGetValue(entry, out var recorded) && !IsReparsePoint(entry)
                    && (_reserved.Contains(entry) || (recorded is { } expected && IdentityOfPath(entry) == expected));
                if (!owned)
                {
                    unowned.Add(entry);
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    CollectUnowned(entry, unowned);
                }
            }
        }

        /// <summary>
        /// Renames the finished staging directory to the output name, after <see cref="RequireOnlyOwnedEntries"/>. The output is claimed by
        /// that rename: Directory.Move refuses a name that is taken, so an existing output directory, or a file or link at the output
        /// path, is never replaced, and no file in an existing output is overwritten. The rename is a single directory rename within one
        /// parent directory. Residual gap: the ownership check is not atomic with the rename; a same-user process can swap an entry for a
        /// link in the interval between them (matrix rule 6, residual (i)).
        /// </summary>
        public void Commit()
        {
            RequireRealDirectory();
            RequireOnlyOwnedEntries();
            Directory.Move(Root, _outputDir);
        }

        /// <summary>
        /// Removes what this build created, deepest first: the recorded entries, then the staging directory and the ancestors it created,
        /// each only when empty. Nothing is removed through a link. Returns the staging path when it is still present (it holds content
        /// this build did not create, or it is no longer a real directory), so the caller can name it; otherwise null.
        /// </summary>
        public string? Discard()
        {
            if (_root is not null)
            {
                if (IsRealDirectory(_root))
                {
                    RemovePublishSources();
                    foreach (var path in _owned.Keys.OrderByDescending(p => p.Length).ToList())
                    {
                        if (LinkBelowRoot(path))
                        {
                            Residual(path, "a link is on its way to it, so it is not followed and not removed");
                        }
                        else
                        {
                            RemoveOwned(path, _owned[path], _reserved.Contains(path));
                        }
                    }

                    RemoveOwned(_root, _rootIdentity, reserved: false);
                }
                else if (Path.Exists(_root) || IsReparsePoint(_root))
                {
                    Residual(_root, "it is no longer a real directory, so nothing under it is removed");
                }
            }

            for (var index = _ancestors.Count - 1; index >= 0; index--)
            {
                RemoveOwned(_ancestors[index].Path, _ancestors[index].Identity, reserved: false);
            }

            return _root is not null && Directory.Exists(_root) ? _root : null;
        }

        /// <summary>
        /// Removes one entry this build created, and only while it is still that entry: its identity must match the one read when it was
        /// created. A reserved name has no identity, so it is removed only while it is a file that is not a link. Anything else is left in
        /// place and recorded as a residual. Nothing is removed through a link, and a directory is removed only when it is empty.
        /// </summary>
        private void RemoveOwned(string path, EntryIdentity? recorded, bool reserved)
        {
            if (!Path.Exists(path) && !IsReparsePoint(path))
            {
                return; // already gone
            }

            if (reserved)
            {
                if (IsReparsePoint(path) || Directory.Exists(path))
                {
                    Residual(path, "a reserved name is removed only while it is a file that is not a link");
                    return;
                }
            }
            else if (recorded is null)
            {
                Residual(path, "its identity was not read when it was created, so it is not removed");
                return;
            }
            else if (IdentityOfPath(path) != recorded)
            {
                Residual(path, "it is not the entry this build created (it was replaced, or it cannot be identified now), so it is not removed");
                return;
            }

            try
            {
                if (Directory.Exists(path) && !IsReparsePoint(path))
                {
                    Directory.Delete(path); // not recursive: a non-empty directory (someone else's content) is kept
                }
                else
                {
                    File.Delete(path); // a hard link is removed by name only; the other name keeps the bytes
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Residual(path, $"it could not be removed ({exception.Message})");
            }
        }

        private void Residual(string path, string reason) => _residuals.Add($"{path}: {reason}");

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

    }
}
