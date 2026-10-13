using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The ahead-of-time plan of a firmware-booted run (Issue #732): the ROM's compiled blocks and the guarded
/// versions of the code the firmware places in RAM (kernel, shell, the disc's boot executable). Packaging and
/// the Runtime host both derive it from the same inputs, so the host's loaded-code table is exactly the one the
/// native artifact was generated from. Nothing here generates or compiles code at run time.
/// </summary>
[Infrastructure]
public sealed record FirmwareAotPlan(
    RecompilerIrProgram Program,
    LoadedCodeTable LoadedCode,
    IReadOnlySet<uint> ObservationPcs)
{
    /// <summary>
    /// Entries kept interpreted so the milestones and exception path stay observable. They match the
    /// <c>openbios-probe</c> run the generated-host parity was measured with.
    /// </summary>
    private static readonly uint[] FixedObservationPcs = [OpenBiosBootMonitor.ShellLoadAddress, 0x80010000u, 0x80000080u, 0x000026A4u];

    public static FirmwareAotPlan Build(
        OpenBiosFirmware firmware, string rootsFile, uint codeBytes, string loadImagesFile, ICdSectorSource disc)
    {
        var roots = File.ReadAllLines(rootsFile)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .Select(static line => uint.Parse(
                line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? line[2..] : line,
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        var code = firmware.Words.Take((int)Math.Min(codeBytes / 4, (uint)firmware.Words.Count)).ToArray();
        var image = ReachableProgramBuilder.BuildFirmwareImage(OpenBiosFirmware.ResetVector, code, OpenBiosFirmware.ResetVector, roots);
        var manifest = LoadImageManifest.Read(loadImagesFile, firmware, disc);
        var observed = new HashSet<uint>(manifest.Interpreted);
        observed.UnionWith(FixedObservationPcs);
        var loaded = new LoadedCodeTable(manifest.Images.Select(
            i => ReachableProgramBuilder.BuildLoadedImage(i.LoadAddress, i.Words, i.Roots, observed)));
        return new FirmwareAotPlan(image.Program, loaded, observed);
    }
}

/// <summary>Reads a local OpenBIOS ROM as a fail-closed input: exact size, hashed, never padded or downloaded.</summary>
[Infrastructure]
public static class OpenBiosRomInput
{
    public static (byte[] Bytes, string Sha256) Read(string path, string? expectedSha256 = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The OpenBIOS ROM was not found: {path}");
        }

        var size = new FileInfo(path).Length;
        if (size != OpenBiosFirmware.ImageSize)
        {
            throw new InvalidDataException($"Expected a {OpenBiosFirmware.ImageSize}-byte OpenBIOS ROM; '{Path.GetFileName(path)}' is {size} bytes.");
        }

        var bytes = File.ReadAllBytes(path);
        var hash = Sha256Hex(bytes);
        if (expectedSha256 is not null && !string.Equals(hash, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The OpenBIOS ROM SHA-256 {hash} does not match the expected {expectedSha256}.");
        }

        return (bytes, hash);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Packaging inputs for a firmware-booted distribution.</summary>
[Infrastructure]
public sealed record FirmwareDistributionRequest(
    string RomPath,
    string DiscPath,
    string AotDirectory,
    string OutputDirectory,
    uint CodeBytes,
    int RunTimeoutMs,
    bool SelfContained,
    string? ExpectedRomSha256 = null,
    uint InstructionBudget = uint.MaxValue);

/// <summary>
/// <c>psxrecomp package --bios openbios</c>: builds a standalone distribution whose native artifact IS the BIOS
/// ROM (guest firmware) and whose game is loaded from the disc by the firmware's own CD driver, in one guest
/// state. The Runtime host (<see cref="FirmwareRuntimeLauncher"/>) supplies the same disc to the device graph.
/// The ROM and disc are the user's local inputs: they are copied into <em>this local</em> distribution only and
/// redistribution is not implied (Issue #730).
/// </summary>
[Infrastructure]
public static class FirmwareDistributionBuilder
{
    public static string Build(FirmwareDistributionRequest request, bool windows)
    {
        var (rom, romHash) = OpenBiosRomInput.Read(request.RomPath, request.ExpectedRomSha256);
        var firmware = OpenBiosFirmware.FromBytes(rom);
        var outputDir = Path.GetFullPath(request.OutputDirectory);
        var aotDir = Path.GetFullPath(request.AotDirectory);
        var rootsSource = Path.Combine(aotDir, "rom.roots");
        var imagesSource = Path.Combine(aotDir, "images.txt");
        if (!File.Exists(rootsSource) || !File.Exists(imagesSource))
        {
            throw new FileNotFoundException($"The AOT directory must hold rom.roots and images.txt (see scripts/openbios/prepare-aot.py): {aotDir}");
        }

        using var disc = DiscSectorSourceOpener.Open(request.DiscPath);
        var plan = FirmwareAotPlan.Build(firmware, rootsSource, request.CodeBytes, imagesSource, disc.Source);

        var dispatch = RecompilerHostCodeGen.Generate(plan.Program, plan.LoadedCode);
        if (!dispatch.Success)
        {
            throw new InvalidOperationException(dispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        var artifactName = Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var binaryName = artifactName + (windows ? ".exe" : "");
        var source = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, new uint[TitleExecutionRequest.GprCount], 0, 0, OpenBiosFirmware.ResetVector, request.InstructionBudget, [],
            OpenBiosFirmware.ResetVector, rom, hostBridge: true,
            directLaunchHostRelativePath: RuntimeHostLayout.HostRelativePath);
        if (!source.Success)
        {
            throw new InvalidOperationException(source.DiagnosticMessage ?? "Standalone source generation failed.");
        }

        // The ROM + RAM-image dispatch is large; the probe allows the same bounded three minutes per compiler step.
        var build = new GeneratedHostBuildService(TimeSpan.FromMinutes(3)).Build(
            new GeneratedHostBuildRequest(source.Source!, outputDir, binaryName));
        if (build.Status != GeneratedHostBuildStatus.Succeeded)
        {
            throw new InvalidOperationException($"Artifact build failed ({build.Status}). {build.DiagnosticMessage}");
        }

        var runtimeDir = Path.Combine(outputDir, RuntimeHostLayout.RuntimeDirectoryName);
        try
        {
            RuntimeHostBuildService.ProvisionRuntimeHost(runtimeDir, request.SelfContained);
        }
        catch (RuntimeHostBuildService.RuntimeHostProvisionFailed exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        // Sidecars. The ROM is the program image the host's interpreter fallback and device RAM read.
        File.WriteAllBytes(Path.Combine(runtimeDir, RuntimeHostLayout.ImageFileName), rom);
        var firmwareDir = Directory.CreateDirectory(Path.Combine(runtimeDir, FirmwareBootManifest.FirmwareDirectory));
        foreach (var file in Directory.EnumerateFiles(aotDir).Where(static f => new FileInfo(f).LinkTarget is null))
        {
            File.Copy(file, Path.Combine(firmwareDir.FullName, Path.GetFileName(file)), overwrite: true);
        }

        var discLeaf = Path.GetExtension(request.DiscPath).Equals(".chd", StringComparison.OrdinalIgnoreCase) ? "disc.chd" : "disc.bin";
        var discDir = Directory.CreateDirectory(Path.Combine(runtimeDir, FirmwareBootManifest.DiscDirectory));
        File.Copy(request.DiscPath, Path.Combine(discDir.FullName, discLeaf), overwrite: true);

        var boot = new FirmwareBootManifest(
            FirmwareBootManifest.CurrentSchemaVersion,
            BiosBackendSelection.NameOf(BiosBackendKind.OpenBios),
            romHash,
            request.CodeBytes,
            $"{FirmwareBootManifest.FirmwareDirectory}/rom.roots",
            $"{FirmwareBootManifest.FirmwareDirectory}/images.txt",
            $"{FirmwareBootManifest.DiscDirectory}/{discLeaf}",
            OpenBiosRomInput.Sha256HexOfFile(request.DiscPath));
        var bootText = FirmwareBootManifestJson.Serialize(boot);
        _ = FirmwareBootManifestJson.Parse(bootText);
        File.WriteAllText(Path.Combine(runtimeDir, FirmwareBootManifest.FileName), bootText);

        var blockEntryPcs = plan.Program.Blocks.Select(static b => b.EntryPc).Order().Distinct().ToArray();
        var host = new RuntimeHostManifest(
            RuntimeHostManifest.CurrentSchemaVersion,
            RuntimeHostLayout.ManifestRelativeArtifactPath(artifactName, windows),
            GuestFirmware: true,
            MixedFallback: true,
            FallbackSegmentInstructionBudget: uint.MaxValue,
            FallbackMaxTransitions: uint.MaxValue,
            ImageLoadAddress: OpenBiosFirmware.ResetVector,
            Image: RuntimeHostLayout.ImageFileName,
            BlockEntryPcs: blockEntryPcs,
            ProgramEnd: OpenBiosFirmware.ResetVector + (uint)OpenBiosFirmware.ImageSize,
            ResultRegister: null,
            RunTimeoutMs: request.RunTimeoutMs,
            EntryPc: OpenBiosFirmware.ResetVector);
        var hostText = RuntimeHostManifestJson.Serialize(host);
        _ = RuntimeHostManifestJson.Parse(hostText);
        File.WriteAllText(Path.Combine(runtimeDir, RuntimeHostLayout.ManifestFileName), hostText);

        File.WriteAllText(Path.Combine(outputDir, RuntimeHostLayout.ReadmeFileName), Readme(binaryName));
        return build.Artifact!.BinaryPath;
    }

    private static string Readme(string binaryName) => string.Join('\n',
        "# Standalone OpenBIOS game distribution",
        "",
        $"Launch: {binaryName}",
        "",
        "The native artifact is the OpenBIOS ROM compiled ahead of time. OpenBIOS runs its real MIPS code and loads the",
        "game from runtime/disc/ through its own CD driver; BIOS calls are never answered by the host.",
        "",
        "- runtime/artifact-image.bin is the OpenBIOS ROM you supplied (verified by SHA-256 on every run).",
        "- runtime/disc/ holds the disc image you supplied (verified by SHA-256 on every run).",
        "- This directory is a LOCAL build. OpenBIOS redistribution is pending the audit in Issue #730, and the disc",
        "  is your own copyrighted media: do not redistribute this directory.",
        "- Exit codes: 0 completed, 2 blocked (unsupported transfer or hardware), 1 tooling failure, 78 setup failure.",
        "");
}

/// <summary>
/// The Runtime-host side of a firmware-booted distribution: verifies the ROM and disc identities, supplies the
/// disc to the shared device graph, and drives the prebuilt artifact in guest-firmware mode. No BIOS HLE Runtime
/// is ever attached, and an unverified ROM, disc or backend stops the run before anything executes.
/// </summary>
[Infrastructure]
public static class FirmwareRuntimeLauncher
{
    public const int SetupFailureExitCode = 78;

    public static bool IsFirmwareDistribution(string runtimeDirectory) =>
        File.Exists(Path.Combine(runtimeDirectory, FirmwareBootManifest.FileName));

    public static int Run(
        RuntimeHostManifest host, string runtimeDirectory, string artifactPath, string imagePath, TextWriter error)
    {
        try
        {
            var boot = FirmwareBootManifestJson.Parse(File.ReadAllText(Path.Combine(runtimeDirectory, FirmwareBootManifest.FileName)));
            if (!host.GuestFirmware)
            {
                throw new InvalidDataException("A firmware distribution's runtime manifest must declare guest firmware.");
            }

            var (rom, _) = OpenBiosRomInput.Read(imagePath, boot.RomSha256);
            var firmware = OpenBiosFirmware.FromBytes(rom);
            var discPath = Path.Combine(runtimeDirectory, boot.DiscFile);
            if (new FileInfo(discPath).LinkTarget is not null)
            {
                throw new InvalidDataException("The disc image must not be a symbolic link.");
            }

            if (OpenBiosRomInput.Sha256HexOfFile(discPath) != boot.DiscSha256)
            {
                throw new InvalidDataException("The disc image does not match the one this distribution was built for (SHA-256).");
            }

            using var disc = DiscSectorSourceOpener.Open(discPath);
            var plan = FirmwareAotPlan.Build(
                firmware,
                Path.Combine(runtimeDirectory, boot.RootsFile),
                boot.CodeBytes,
                Path.Combine(runtimeDirectory, boot.LoadImagesFile),
                disc.Source);

            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
                artifactPath,
                host.BlockEntryPcs.ToHashSet(),
                guestFirmware: true,
                mixedFallback: new MixedFallbackOptions(host.FallbackSegmentInstructionBudget, host.FallbackMaxTransitions),
                runTimeout: TimeSpan.FromMilliseconds(host.RunTimeoutMs),
                loadedCode: plan.LoadedCode,
                imageWords: firmware.Words,
                imageLoadAddress: host.ImageLoadAddress,
                launchMode: RecompiledHostExecutionEngine.PrebuiltArtifactLaunchMode.RuntimeHost,
                disc: disc.Source);

            var result = new ExecutionOrchestrator().Execute(
                engine,
                handoff: null,
                new TitleExecutionRequest(
                    host.EntryPc, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: uint.MaxValue));

            var evidence = engine.FallbackEvidence;
            error.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "firmware-run",
                bios = boot.BiosBackend,
                romSha256 = boot.RomSha256,
                state = result.State.ToString(),
                guestPc = result.FinalSnapshot is { } s ? $"0x{s.PC:X8}" : null,
                diagnosticCode = result.DiagnosticCode,
                nativeInstructions = engine.NativeRetiredInstructions,
                fallbackInstructions = evidence?.FallbackInstructions,
                fallbackTransitions = evidence?.Transitions,
            }));
            return RecompiledArtifactExitCode.ExitCodeFor(result.State);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or FormatException)
        {
            error.WriteLine($"runtime host: firmware distribution: {exception.GetType().Name}: {exception.Message}");
            return SetupFailureExitCode;
        }
    }
}
