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
    IReadOnlySet<uint> ObservationPcs,
    BiosContentDiagnostic? ContentDiagnostic = null)
{
    /// <summary>
    /// The only PCs a firmware distribution keeps interpreted: OpenBIOS's own exception entry points (<c>0x80000080</c>,
    /// <c>0x26A4</c>), which the firmware's exception path must stay able to observe. <c>scripts/openbios/prepare-aot.py</c> writes
    /// them as <c>interpret</c> lines; it writes no BIOS image, because the canonical pipeline never takes the BIOS as an AOT input (owner ruling R1, narrowed).
    /// </summary>
    /// <remarks>
    /// The milestone PCs the <c>openbios-probe</c> parity runs stop at (<c>0x80030000</c> shell load and
    /// <c>0x80010000</c> executable boundary) are not excluded here. A distribution compiles the disc's executables, so a game
    /// whose entry is exactly <c>0x80010000</c> runs from a guarded native block when that image is compiled (Issue #732 P1-2).
    /// The shell at <c>0x80030000</c> is BIOS code, not an AOT image (R1): it runs interpreted from the BIOS image.
    /// </remarks>
    private static readonly uint[] FixedExceptionObservationPcs = [0x80000080u, 0x000026A4u];

    public static FirmwareAotPlan Build(
        OpenBiosFirmware firmware, string rootsFile, uint codeBytes, string loadImagesFile, ICdSectorSource disc)
    {
        // The shared roots parser (Issue #732 review): comments/blank lines and hex interpretation match the
        // manifest's own roots files, and an invalid line is an InvalidDataException the CLI classifies.
        // Owner ruling (Issue #B2, option 1): the BIOS is never ahead-of-time translated. The roots file is still read,
        // so a malformed input fails closed, but no ROM block is compiled: every ROM PC runs in the interpreter fallback
        // over the BIOS image the run loads. Only the code the guest places in RAM (the disc's boot executable and the
        // images it names) is translated, exactly as before.
        _ = LoadImageManifest.ReadRootsFile(rootsFile);
        var manifest = LoadImageManifest.Read(loadImagesFile, firmware, disc);
        BiosGuardWiring.Run(BiosGuardWiring.Manifest, () => BiosAddressGuard.RequireNoBiosImage(manifest, firmware));
        var contentDiagnostic = BiosAddressGuard.DiagnoseContent(manifest, firmware);
        var observed = new HashSet<uint>(manifest.Interpreted);
        observed.UnionWith(FixedExceptionObservationPcs);
        var loaded = new LoadedCodeTable(manifest.Images.Select(
            i => ReachableProgramBuilder.BuildLoadedImage(i.LoadAddress, i.Words, i.Roots, observed) with { Origin = i.Origin }));
        var program = new RecompilerIrProgram(Array.Empty<RecompilerIrBlock>());
        BiosGuardWiring.Run(BiosGuardWiring.Plan, () => BiosAddressGuard.RequireNoBiosCode(program, loaded, BiosGuardWiring.Plan));
        return new FirmwareAotPlan(program, loaded, observed, contentDiagnostic);
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

/// <summary>
/// One AOT input file a firmware distribution ships and pins (Issue #732 P1-3): <see cref="Path"/> is the file's
/// location relative to <c>runtime/</c> (always under <c>firmware/</c>), <see cref="Sha256"/> its lowercase digest.
/// </summary>
[Infrastructure]
public sealed record FirmwareAotInput(string Path, string Sha256);

/// <summary>
/// Enumerates, contains and hashes the files a firmware distribution's AOT build depends on (Issue #732 P1-3):
/// <c>rom.roots</c>, <c>images.txt</c> and every <c>exe</c>/<c>image</c> file those directives reference. Packaging
/// copies them under <c>runtime/firmware/</c> preserving their manifest-relative layout and records each digest in
/// <see cref="FirmwareBootManifest.AotInputs"/>; the run re-verifies every digest (and the confined, link-free
/// layout) before rebuilding the loaded-code table, so a missing, escaping, linked or tampered input fails closed
/// instead of silently rebuilding a different table. Absolute references, <c>..</c> escapes, missing files and
/// symbolic links (the file or any directory below the AOT root) are rejected at packaging time.
/// </summary>
[Infrastructure]
public static class FirmwareAotBundle
{
    public const string RootsFileName = "rom.roots";
    public const string ImagesFileName = "images.txt";

    /// <summary>
    /// Collects the AOT inputs under <paramref name="aotDirectory"/>. Throws <see cref="InvalidDataException"/>
    /// (escaping/absolute/linked/malformed reference) or <see cref="FileNotFoundException"/> (missing file).
    /// </summary>
    public static (IReadOnlyList<FirmwareAotInput> Inputs, IReadOnlyList<(string Relative, string Source)> Files) Collect(string aotDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aotDirectory);
        var root = Path.GetFullPath(aotDirectory);
        RequireRealDirectory(root);
        var roots = Path.Combine(root, RootsFileName);
        var images = Path.Combine(root, ImagesFileName);
        if (!File.Exists(roots) || !File.Exists(images))
        {
            throw new FileNotFoundException(
                $"The AOT directory must hold {RootsFileName} and {ImagesFileName} (see scripts/openbios/prepare-aot.py): {root}");
        }

        // The two base files are inputs too (Issue #732 review P2): a linked <c>rom.roots</c> or <c>images.txt</c>
        // would otherwise be hashed and copied through its target while the AOT root's own reparse state is never
        // inspected.
        EnsureLinkFree(root, roots, RootsFileName);
        EnsureLinkFree(root, images, ImagesFileName);

        // A dependency is any manifest-referenced file plus the roots and images files themselves. Every one is
        // resolved against the AOT root (the images.txt directory), confined to it and checked for links.
        var sources = new SortedSet<string>(StringComparer.Ordinal) { roots, images };
        foreach (var reference in LoadImageManifest.ReadReferencedPaths(images))
        {
            sources.Add(ResolveContained(root, reference));
        }

        var inputs = new List<FirmwareAotInput>();
        var files = new List<(string Relative, string Source)>();
        foreach (var source in sources)
        {
            var relative = RelativeWithin(root, source);
            inputs.Add(new FirmwareAotInput($"{FirmwareBootManifest.FirmwareDirectory}/{relative}", OpenBiosRomInput.Sha256HexOfFile(source)));
            files.Add((relative, source));
        }

        return (inputs, files);
    }

    /// <summary>Copies the collected files under <paramref name="firmwareDirectory"/>, preserving the relative layout.</summary>
    public static void CopyInto(IReadOnlyList<(string Relative, string Source)> files, string firmwareDirectory)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwareDirectory);
        foreach (var (relative, source) in files)
        {
            var destination = Path.Combine(firmwareDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException($"The AOT input '{relative}' has no file name."));
            File.Copy(source, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Verifies every pinned input under a running distribution's <paramref name="runtimeDirectory"/>. Fails closed
    /// with <see cref="InvalidDataException"/> when an input is missing, outside <c>firmware/</c>, a symbolic link or
    /// does not match its recorded SHA-256.
    /// </summary>
    public static void Verify(string runtimeDirectory, IReadOnlyList<FirmwareAotInput> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        ArgumentNullException.ThrowIfNull(inputs);
        var firmwareDirectory = Path.GetFullPath(Path.Combine(runtimeDirectory, FirmwareBootManifest.FirmwareDirectory));
        foreach (var input in inputs)
        {
            var full = Path.GetFullPath(Path.Combine(runtimeDirectory, input.Path.Replace('/', Path.DirectorySeparatorChar)));
            RequireWithin(firmwareDirectory, full, input.Path);
            if (!File.Exists(full))
            {
                throw new InvalidDataException($"The firmware AOT input was not found: {input.Path}");
            }

            EnsureLinkFree(firmwareDirectory, full, input.Path);
            var actual = OpenBiosRomInput.Sha256HexOfFile(full);
            if (!string.Equals(actual, input.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The firmware AOT input '{input.Path}' does not match the one this distribution was built for (SHA-256).");
            }
        }
    }

    private static string ResolveContained(string root, string reference)
    {
        if (Path.IsPathRooted(reference))
        {
            throw new InvalidDataException($"The AOT manifest references the absolute path '{reference}'; AOT inputs must be relative to the AOT directory.");
        }

        var full = Path.GetFullPath(Path.Combine(root, reference));
        RequireWithin(root, full, reference);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"The AOT manifest references a file that does not exist: {reference}");
        }

        EnsureLinkFree(root, full, reference);
        return full;
    }

    private static string RelativeWithin(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return relative.Replace('\\', '/');
    }

    private static void RequireWithin(string container, string candidate, string what)
    {
        var relative = Path.GetRelativePath(container, candidate);
        if (relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"The AOT input '{what}' resolves outside the AOT directory.");
        }
    }

    // The AOT root itself must be a real directory: a linked root would resolve every relative reference outside
    // the directory the containment checks normalised against, and DirectoryInfo.LinkTarget on the root is not
    // reached by the per-file walk below.
    private static void RequireRealDirectory(string root)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"The AOT directory was not found: {root}");
        }

        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"The AOT directory must not be a symbolic link or reparse point: {root}");
        }
    }

    // The file and every directory between the AOT root and it must be real: a linked component could resolve the
    // name to a file outside the directory the containment check normalised against.
    private static void EnsureLinkFree(string root, string full, string what)
    {
        var directory = Path.GetDirectoryName(full);
        if (directory is not null)
        {
            var current = root;
            foreach (var part in Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    throw new InvalidDataException($"The AOT input '{what}' passes through the symbolic link or reparse point '{part}'.");
                }
            }
        }

        var file = new FileInfo(full);
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"The AOT input '{what}' must not be a symbolic link or reparse point.");
        }
    }
}

/// <summary>Packaging inputs for a firmware-booted distribution.</summary>
/// <param name="MixedFallback">
/// Whether an unresolved in-image indirect transfer may run on the interpreter fallback. Defaults to true (the
/// pre-existing distribution behavior); <c>--no-mixed-fallback</c> carries false through to the runtime manifest.
/// </param>
/// <param name="FallbackSegmentInstructionBudget">The per-segment interpreter fallback instruction cap (only meaningful while <paramref name="MixedFallback"/>).</param>
/// <param name="FallbackMaxTransitions">The artifact-to-interpreter handoff cap (only meaningful while <paramref name="MixedFallback"/>).</param>
/// <param name="BuildTimeout">
/// The bounded per-step host-toolchain (compiler/linker) timeout for this distribution's native artifact build,
/// or null to use <see cref="FirmwareDistributionBuilder.DefaultBuildTimeout"/>. It bounds only the compile/link
/// steps — never the guest run (that is <paramref name="RunTimeoutMs"/>) — so a slow build host can be given a
/// longer budget without changing the game's run time.
/// </param>
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
    uint InstructionBudget = uint.MaxValue,
    bool MixedFallback = true,
    uint FallbackSegmentInstructionBudget = FirmwareDistributionRequest.DefaultFallbackSegmentInstructionBudget,
    uint FallbackMaxTransitions = FirmwareDistributionRequest.DefaultFallbackMaxTransitions,
    TimeSpan? BuildTimeout = null,
    string BiosProvenance = RuntimeHostManifest.BiosProvenanceSynthetic)
{
    /// <summary>
    /// The finite default per-segment fallback cap for a standalone firmware package (#820 W09). The firmware's whole
    /// boot runs on the interpreter, so this is a safety cap, not a measured boot budget: the probe showed 2,000,000
    /// instructions do not reach the kernel, so the value is set far above that and must be re-measured on a real BIOS.
    /// </summary>
    public const uint DefaultFallbackSegmentInstructionBudget = 100_000_000u;

    /// <summary>The finite default artifact-to-interpreter handoff cap; the same value the run and package paths use.</summary>
    public const uint DefaultFallbackMaxTransitions = MixedFallbackOptions.DefaultMaxTransitions;
}

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
    /// <summary>The file in the distribution's runtime directory that holds the BIOS content diagnostic (Issue #B2).</summary>
    public const string BiosContentDiagnosticFileName = "bios-content-diagnostic.txt";

    /// <summary>
    /// The bounded per-step native-toolchain timeout a firmware distribution build uses unless the request
    /// overrides it (<c>--build-timeout-seconds</c>). The ROM + RAM-image dispatch is far larger than an
    /// ordinary generated artifact, so it gets three minutes per compiler step instead of the 30-second
    /// default; it stays a finite bound (never an unbounded wait).
    /// </summary>
    public static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(3);

    public static string Build(FirmwareDistributionRequest request, bool windows)
    {
        ArgumentNullException.ThrowIfNull(request);

        // P1 (Issue #732 review): validate and freeze every input before any code generation, compiler run or
        // output write. The ROM bytes are read and hashed; the AOT inputs are enumerated, confined, link-checked
        // and hashed; then the AOT inputs and the disc are copied into a staging directory and the plan is derived
        // from those frozen copies. The compiled code and the distributed data are therefore the same snapshot,
        // and a failure at any step leaves no partial distribution and never touches a pre-existing output.
        var (rom, _) = OpenBiosRomInput.Read(request.RomPath, request.ExpectedRomSha256);
        var firmware = OpenBiosFirmware.FromBytes(rom);
        var outputDir = Path.GetFullPath(request.OutputDirectory);
        var aotDir = Path.GetFullPath(request.AotDirectory);

        var artifactName = Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(artifactName))
        {
            throw new InvalidDataException($"The output directory has no name to derive the artifact from: {outputDir}");
        }

        var binaryName = artifactName + (windows ? ".exe" : "");

        // Collect (which enforces containment, existence and link-freedom) before anything is created.
        var (_, aotSourceFiles) = FirmwareAotBundle.Collect(aotDir);

        var staging = StagingDirectoryFor(outputDir, artifactName);
        try
        {
            Directory.CreateDirectory(staging);
            var stagingRuntimeDir = Path.Combine(staging, RuntimeHostLayout.RuntimeDirectoryName);

            // Freeze the AOT inputs under staging and pin the copies (Collect on the staged layout), so the
            // digests recorded in the boot manifest describe exactly the files this distribution ships.
            var stagingFirmwareDir = Directory.CreateDirectory(
                Path.Combine(stagingRuntimeDir, FirmwareBootManifest.FirmwareDirectory));
            FirmwareAotBundle.CopyInto(aotSourceFiles, stagingFirmwareDir.FullName);
            var (aotInputs, _) = FirmwareAotBundle.Collect(stagingFirmwareDir.FullName);

            // Freeze the disc the same way: build the plan against the staged copy, and hash that copy.
            var discLeaf = Path.GetExtension(request.DiscPath).Equals(".chd", StringComparison.OrdinalIgnoreCase) ? "disc.chd" : "disc.bin";
            var stagingDiscDir = Directory.CreateDirectory(
                Path.Combine(stagingRuntimeDir, FirmwareBootManifest.DiscDirectory));
            var stagedDisc = Path.Combine(stagingDiscDir.FullName, discLeaf);
            File.Copy(request.DiscPath, stagedDisc, overwrite: true);
            var discHash = OpenBiosRomInput.Sha256HexOfFile(stagedDisc);

            FirmwareAotPlan plan;
            using (var disc = DiscSectorSourceOpener.Open(stagedDisc))
            {
                plan = FirmwareAotPlan.Build(
                    firmware,
                    Path.Combine(stagingFirmwareDir.FullName, FirmwareAotBundle.RootsFileName),
                    request.CodeBytes,
                    Path.Combine(stagingFirmwareDir.FullName, FirmwareAotBundle.ImagesFileName),
                    disc.Source);
            }

            // Measurement only (Issue #773): PSXRECOMP_NATIVE_GUARD_STATS at package time builds the artifact with guard counters.
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

            // The ROM + RAM-image dispatch is large; the build gets the request's per-step toolchain timeout
            // (--build-timeout-seconds), or the same bounded three minutes the probe uses by default.
            var build = new GeneratedHostBuildService(request.BuildTimeout ?? DefaultBuildTimeout).Build(
                new GeneratedHostBuildRequest(source.Source!, staging, binaryName, FirmwareDistribution: true));
            if (build.Status != GeneratedHostBuildStatus.Succeeded)
            {
                throw new InvalidOperationException($"Artifact build failed ({build.Status}). {build.DiagnosticMessage}");
            }

            try
            {
                RuntimeHostBuildService.ProvisionRuntimeHost(stagingRuntimeDir, request.SelfContained, firmwareDistribution: true);
            }
            catch (RuntimeHostBuildService.RuntimeHostProvisionFailed exception)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }

            // No BIOS bytes, digest or sidecar are written (Issue #B2): the run names the BIOS by path (PSXRECOMP_BIOS_ROM).

            var boot = new FirmwareBootManifest(
                FirmwareBootManifest.CurrentSchemaVersion,
                BiosBackendSelection.NameOf(BiosBackendKind.OpenBios),
                request.CodeBytes,
                $"{FirmwareBootManifest.FirmwareDirectory}/{FirmwareAotBundle.RootsFileName}",
                $"{FirmwareBootManifest.FirmwareDirectory}/{FirmwareAotBundle.ImagesFileName}",
                $"{FirmwareBootManifest.DiscDirectory}/{discLeaf}",
                discHash,
                aotInputs);
            var bootText = FirmwareBootManifestJson.Serialize(boot);
            _ = FirmwareBootManifestJson.Parse(bootText);
            File.WriteAllText(Path.Combine(stagingRuntimeDir, FirmwareBootManifest.FileName), bootText);

            // The content diagnostic (Issue #B2, option 1): a report of BIOS content matches, never a refusal. It holds counts and
            // the provenance state only; no BIOS or input bytes are written.
            File.WriteAllText(Path.Combine(stagingRuntimeDir, BiosContentDiagnosticFileName),
                (plan.ContentDiagnostic?.Message ?? "BIOS content diagnostic: not computed.") + Environment.NewLine);

            // Block entry PCs are the artifact's static (ROM) blocks only: RAM-placed versions are selected by their
            // guard, and the fallback relies on a loaded PC NOT being listed (ArtifactFallbackSession).
            var blockEntryPcs = plan.Program.Blocks.Select(static b => b.EntryPc).Order().Distinct().ToArray();
            var host = new RuntimeHostManifest(
                RuntimeHostManifest.CurrentSchemaVersion,
                RuntimeHostLayout.ManifestRelativeArtifactPath(artifactName, windows),
                GuestFirmware: true,
                MixedFallback: request.MixedFallback,
                FallbackSegmentInstructionBudget: request.MixedFallback ? request.FallbackSegmentInstructionBudget : 0u,
                FallbackMaxTransitions: request.MixedFallback ? request.FallbackMaxTransitions : 0u,
                ImageLoadAddress: OpenBiosFirmware.ResetVector,
                Image: string.Empty,
                BlockEntryPcs: blockEntryPcs,
                ProgramEnd: OpenBiosFirmware.ResetVector + (uint)OpenBiosFirmware.ImageSize,
                ResultRegister: null,
                RunTimeoutMs: request.RunTimeoutMs,
                EntryPc: OpenBiosFirmware.ResetVector,
                BiosProvenance: request.BiosProvenance);
            var hostText = RuntimeHostManifestJson.Serialize(host);
            _ = RuntimeHostManifestJson.Parse(hostText);
            File.WriteAllText(Path.Combine(stagingRuntimeDir, RuntimeHostLayout.ManifestFileName), hostText);

            File.WriteAllText(Path.Combine(staging, RuntimeHostLayout.ReadmeFileName), Readme(binaryName));

            // Only a fully built distribution is committed into place.
            CommitStaging(staging, outputDir);
            return Path.Combine(outputDir, Path.GetFileName(build.Artifact!.BinaryPath));
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    /// Creates a sibling staging directory on the same volume as <paramref name="outputDir"/>, so the final
    /// commit is a directory rename rather than a cross-volume copy.
    /// </summary>
    private static string StagingDirectoryFor(string outputDir, string artifactName)
    {
        var parent = Path.GetDirectoryName(outputDir);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidDataException($"The output directory has no parent directory: {outputDir}");
        }

        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $".{artifactName}.staging-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Swaps a fully built <paramref name="staging"/> directory into <paramref name="outputDir"/>. A pre-existing
    /// output is moved aside first and restored if the swap fails, so a failed build never destroys a good
    /// previous distribution.
    /// </summary>
    private static void CommitStaging(string staging, string outputDir)
    {
        string? backup = null;
        if (Directory.Exists(outputDir))
        {
            backup = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.Move(outputDir, backup);
        }

        try
        {
            Directory.Move(staging, outputDir);
        }
        catch
        {
            if (backup is not null && !Directory.Exists(outputDir))
            {
                Directory.Move(backup, outputDir);
            }

            throw;
        }

        TryDeleteDirectory(backup);
    }

    // Best-effort cleanup: a leftover staging/backup directory must never mask the build's real result.
    private static void TryDeleteDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The distribution's README. The native artifact holds the disc's executables as compiled code; the OpenBIOS ROM is not compiled.</summary>
    internal static string Readme(string binaryName) => string.Join('\n',
        "# Standalone OpenBIOS game distribution",
        "",
        $"Launch: {binaryName}",
        "",
        "The native artifact holds the disc's executables as compiled code. The OpenBIOS ROM is not compiled: it runs",
        "interpreted from the BIOS image you supply. OpenBIOS runs its real MIPS code and loads the game from runtime/disc/",
        "through its own CD driver; BIOS calls are never answered by the host. No BIOS bytes or digest are written into",
        "this directory.",
        "",
        "- The OpenBIOS ROM is NOT in this directory. Set PSXRECOMP_BIOS_ROM to the path of your own 512 KiB openbios.bin",
        "  before launching; the run prints its SHA-256 and the BIOS mode (openbios-rom only for a user-supplied image).",
        "- runtime/disc/ holds the disc image you supplied (verified by SHA-256 on every run).",
        "- This directory is a LOCAL build. OpenBIOS redistribution is pending the audit in Issue #730, and the disc",
        "  is your own copyrighted media: do not redistribute this directory.",
        "- Exit codes: 0 completed, 2 blocked (unsupported transfer or hardware), 1 tooling failure, 78 setup failure.",
        "");
}

/// <summary>
/// The Runtime-host side of a firmware-booted distribution: verifies the ROM, disc and AOT-input identities,
/// supplies the disc to the shared device graph, and drives the prebuilt artifact in guest-firmware mode. No BIOS
/// HLE Runtime is ever attached, and an unverified ROM, disc, AOT input or backend stops the run before anything
/// executes. The runtime manifest's <c>mixedFallback</c> is honored exactly: when it is off, an unresolved in-image
/// transfer stops the run instead of reaching the interpreter (Issue #732 P1-1). When
/// <paramref name="diagnosticsPath"/> is given the host also writes the same Issue #763 stop diagnostic the HLE
/// path writes; the <c>firmware-run</c> JSON line remains the firmware run's own summary (Issue #732 P2-1).
/// </summary>
[Infrastructure]
public static class FirmwareRuntimeLauncher
{
    public const int SetupFailureExitCode = 78;

    public static bool IsFirmwareDistribution(string runtimeDirectory) =>
        File.Exists(Path.Combine(runtimeDirectory, FirmwareBootManifest.FileName));

    /// <summary>
    /// The interpreter-fallback options a runtime manifest asks for, or null when mixed fallback is off (Issue #732
    /// P1-1): the engine then stops an unresolved in-image transfer instead of handing it to the interpreter.
    /// </summary>
    public static MixedFallbackOptions? FallbackOptionsFor(RuntimeHostManifest host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.MixedFallback
            ? new MixedFallbackOptions(host.FallbackSegmentInstructionBudget, host.FallbackMaxTransitions)
            : null;
    }

    /// <summary>
    /// Reads the BIOS the run was given and classifies it from the bytes it loaded (Issue #B2, MAJOR-1, MEDIUM-3). The label is
    /// decided by the digest alone; the packaging-time declaration only produces a warning when the run does not confirm it. The
    /// registries are parameters so a test can inject them; the run passes none.
    /// </summary>
    public static (byte[] Bytes, string Sha256, string Label) LoadAndClassify(
        string biosPath,
        string? declared,
        TextWriter error,
        IReadOnlySet<string>? verified = null,
        IReadOnlySet<string>? synthetic = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (bytes, sha256) = OpenBiosRomInput.Read(biosPath);
        var label = BiosProvenance.Classify(
            sha256, verified ?? BiosProvenance.VerifiedOpenBiosSha256, synthetic ?? BiosProvenance.KnownSyntheticSha256);
        var warning = BiosProvenance.DeclarationWarning(declared, label);
        if (warning is not null)
        {
            error.WriteLine(warning);
        }

        return (bytes, sha256, label);
    }

    public static int Run(
        RuntimeHostManifest host,
        string runtimeDirectory,
        string artifactPath,
        string biosPath,
        TextWriter error,
        string? diagnosticsPath = null,
        string? frameOutPath = null,
        LoadedCodeMismatchDiagnostics? loadedCodeMismatch = null)
    {
        try
        {
            var boot = FirmwareBootManifestJson.Parse(File.ReadAllText(Path.Combine(runtimeDirectory, FirmwareBootManifest.FileName)));
            if (!host.GuestFirmware)
            {
                throw new InvalidDataException("A firmware distribution's runtime manifest must declare guest firmware.");
            }

            // The BIOS is read from the path the user names at run time; its digest is reported, never read from the package.
            if (biosPath.Length == 0)
            {
                throw new InvalidDataException(
                    $"Set {RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable} to the path of your own OpenBIOS image; none is shipped with this distribution.");
            }

            var (rom, romSha256, biosClassification) = FirmwareRuntimeLauncher.LoadAndClassify(biosPath, host.BiosProvenance, error);
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

            // P1-3: every AOT input is re-verified against the digest recorded at packaging time before the plan is
            // rebuilt from it, so a missing, escaping, linked or tampered input fails closed here.
            FirmwareAotBundle.Verify(runtimeDirectory, boot.AotInputs!);

            var mixedFallback = FallbackOptionsFor(host);

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
                mixedFallback: mixedFallback,
                runTimeout: TimeSpan.FromMilliseconds(host.RunTimeoutMs),
                loadedCode: plan.LoadedCode,
                imageWords: firmware.Words,
                imageLoadAddress: host.ImageLoadAddress,
                launchMode: RecompiledHostExecutionEngine.PrebuiltArtifactLaunchMode.RuntimeHost,
                disc: disc.Source);
            engine.LoadedCodeMismatchDiagnostics = loadedCodeMismatch;

            // Opt-in transition attribution (measurement only): absent the environment variable, no observer is installed.
            var attribution = TransitionAttributionRecorder.FromEnvironment();
            if (attribution is not null) engine.FallbackTransitionObserver = attribution.Observe;

            var wall = System.Diagnostics.Stopwatch.StartNew();
            var result = new ExecutionOrchestrator().Execute(
                engine,
                handoff: null,
                new TitleExecutionRequest(
                    host.EntryPc, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: uint.MaxValue));
            wall.Stop();
            if (OpenBiosRomInput.Sha256HexOfFile(biosPath) != romSha256)
            {
                throw new InvalidDataException(
                    "The BIOS image changed while the run was in progress (SHA-256 differs from the image the host loaded); the run is not valid.");
            }

            if (frameOutPath is not null)
            {
                RuntimeHostFrameOutput.TryWrite(engine, frameOutPath, error);
            }

            var evidence = engine.FallbackEvidence;
            var exitCode = RecompiledArtifactExitCode.ExitCodeFor(result.State);
            // Issue #786 review MINOR-1 / MEDIUM-1: marked once the run is classified; a host timeout stays incomplete.
            RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(loadedCodeMismatch, result);
            if (engine.IpcProfile is { } ipcProfile && IpcProfile.OutputPathFromEnvironment() is { } ipcPath)
            {
                ipcProfile.TryWrite(ipcPath, error, new Dictionary<string, object?>
                {
                    ["state"] = result.State.ToString(),
                    ["wallMilliseconds"] = wall.Elapsed.TotalMilliseconds,
                    ["nativeInstructions"] = engine.NativeRetiredInstructions,
                    ["mmioAccesses"] = engine.HostRoundTrips?.MmioAccesses,
                    ["timeReports"] = engine.HostRoundTrips?.TimeReports,
                });
            }
            if (attribution is not null && TransitionAttributionRecorder.OutputPathFromEnvironment() is { } attributionPath)
            {
                attribution.TryWrite(attributionPath, error, new Dictionary<string, object?>
                {
                    ["state"] = result.State.ToString(),
                    ["wallMilliseconds"] = wall.Elapsed.TotalMilliseconds,
                    ["nativeInstructions"] = engine.NativeRetiredInstructions,
                    ["fallbackInstructions"] = evidence?.FallbackInstructions,
                    ["fallbackTransitions"] = evidence?.Transitions,
                    ["transferMilliseconds"] = engine.FallbackTimings?.TransferMilliseconds,
                    ["fallbackMilliseconds"] = engine.FallbackTimings?.FallbackMilliseconds,
                });
            }
            WriteDiagnostics(
                diagnosticsPath,
                () => RuntimeHostDiagnostic.From(
                    host,
                    result,
                    exitCode,
                    engine.NativeRetiredInstructions,
                    evidence?.FallbackInstructions,
                    evidence?.Transitions,
                    engine.LastDeclinedTransfer,
                    evidence?.Targets,
                    RuntimeHostRunMetrics.From(
                        wall.Elapsed.TotalMilliseconds, engine.HostRoundTrips, engine.FallbackTimings, engine.FallbackEvidence, engine.LastMmioAccess,
                        engine.FallbackInFlight, engine.FallbackReturns)),
                error);
            error.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "firmware-run",
                bios = boot.BiosBackend,
                biosMode = biosClassification,
                declaredProvenance = host.BiosProvenance,
                romSha256,
                // The generation-stage audit (Issue #B2): no BIOS block is translated; loaded RAM blocks are the game's code.
                aotBiosBlocks = BiosAddressGuard.BiosBlockCount(plan.Program),
                aotLoadedBlocks = plan.LoadedCode.Blocks.Count(),
                state = result.State.ToString(),
                guestPc = result.FinalSnapshot is { } s ? $"0x{s.PC:X8}" : null,
                diagnosticCode = result.DiagnosticCode,
                diagnosticMessage = result.DiagnosticMessage,
                mixedFallback = host.MixedFallback,
                fallbackSegmentInstructionBudget = host.MixedFallback ? host.FallbackSegmentInstructionBudget : (uint?)null,
                fallbackMaxTransitions = host.MixedFallback ? host.FallbackMaxTransitions : (uint?)null,
                nativeInstructions = engine.NativeRetiredInstructions,
                fallbackInstructions = evidence?.FallbackInstructions,
                fallbackTransitions = evidence?.Transitions,
            }));
            return exitCode;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or FormatException)
        {
            error.WriteLine($"runtime host: firmware distribution: {exception.GetType().Name}: {exception.Message}");
            WriteDiagnostics(diagnosticsPath, () => RuntimeHostDiagnostic.FromFailure(host, SetupFailureExitCode, exception), error);
            return SetupFailureExitCode;
        }
    }

    /// <summary>
    /// Writes the Issue #763 stop diagnostic for a firmware run. Best-effort: a write failure is reported on stderr
    /// but never changes the run's classified exit code.
    /// </summary>
    private static void WriteDiagnostics(string? path, Func<RuntimeHostDiagnostic> build, TextWriter error)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            RuntimeHostReportFile.WriteAtomically(path, RuntimeHostDiagnosticJson.Serialize(build()));
        }
        catch (Exception exception)
        {
            error.WriteLine($"runtime host: could not write diagnostics to {path}: {exception.Message}");
        }
    }
}
