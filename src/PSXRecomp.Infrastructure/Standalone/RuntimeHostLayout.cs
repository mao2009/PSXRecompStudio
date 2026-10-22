using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The on-disk contract of a packaged game distribution (Phase 2 Step C, Issue
/// #739, ADR-027 Option B): where the shared managed Runtime host, its manifest
/// and the embedded-image program image live inside a distribution, and the
/// relative paths the generated native game executable uses to reach them.
/// </summary>
/// <remarks>
/// <para>
/// A distribution produced by <c>psxrecomp package</c> lays out exactly:
/// </para>
/// <code>
/// &lt;game&gt;/                  distribution root
///   &lt;game&gt;[.exe]           native AOT artifact (bridge-capable, direct-launch)
///   README.txt
///   runtime/
///     &lt;host&gt;[.exe]         shared managed Runtime host (PSXRecomp.RuntimeHost)
///     runtime-host.json      <see cref="RuntimeHostManifest"/> describing the run
///     artifact-image.bin     the guest program image the artifact embeds
/// </code>
/// <para>
/// The native artifact reaches the host at runtime only by its own real path plus
/// this fixed relative structure: no Studio, no CLI and no absolute dev-repo path
/// are involved. The same relative names are embedded in the generated artifact's
/// <c>main()</c> (see <see cref="Core.Recompiler.RecompiledArtifactCodeGen"/>), so
/// this type is the single source of truth for the layout.
/// </para>
/// </remarks>
[Infrastructure]
public static class RuntimeHostLayout
{
    /// <summary>The subdirectory of a distribution that holds the shared Runtime host and its data.</summary>
    public const string RuntimeDirectoryName = "runtime";

    /// <summary>The Runtime host executable base name (the platform appends <c>.exe</c> on Windows).</summary>
    public const string HostBinaryName = "PSXRecomp.RuntimeHost";

    /// <summary>The Runtime host manifest file name, inside <see cref="RuntimeDirectoryName"/>.</summary>
    public const string ManifestFileName = "runtime-host.json";

    /// <summary>The embedded program image sidecar file name, inside <see cref="RuntimeDirectoryName"/>.</summary>
    public const string ImageFileName = "artifact-image.bin";

    /// <summary>The windowed (GUI) host's executable name inside <see cref="RuntimeDirectoryName"/> (Issue #830), with the Windows suffix.</summary>
    public const string GuiHostFileName = "PSXRecomp.GameWindow.exe";

    /// <summary>The PS-X EXE copy the windowed host runs, inside <see cref="RuntimeDirectoryName"/> (Issue #830).</summary>
    public const string GuiGameFileName = "program.exe";

    /// <summary>The human-readable dependency/launch note shipped next to the game executable.</summary>
    public const string ReadmeFileName = "README.txt";

    /// <summary>
    /// The host's path relative to the game executable, with forward slashes and no
    /// platform suffix. The generated artifact appends <c>.exe</c> on Windows (see
    /// <c>RUNTIME_HOST_EXE_SUFFIX</c> in the generated C).
    /// </summary>
    public const string HostRelativePath = RuntimeDirectoryName + "/" + HostBinaryName;

    /// <summary>The manifest's path relative to the game executable, with forward slashes.</summary>
    public const string ManifestRelativePath = RuntimeDirectoryName + "/" + ManifestFileName;

    /// <summary>
    /// The artifact exit code a directly launched game executable returns when it cannot
    /// locate or start the bundled Runtime host. Distinct from the artifact's own
    /// termination-reason exit codes (0..7) and the input/image failure codes (90..95).
    /// </summary>
    public const int MissingRuntimeHostExitCode = 78;

    /// <summary>The platform's host executable file name (<c>PSXRecomp.RuntimeHost</c> or <c>PSXRecomp.RuntimeHost.exe</c>).</summary>
    public static string HostFileName(bool windows) => windows ? HostBinaryName + ".exe" : HostBinaryName;

    /// <summary>
    /// The game artifact's path relative to the manifest's directory (the artifact sits one
    /// level above the manifest inside the distribution root). Forward slashes, no trailing
    /// separator; the manifest itself is written with the platform-appropriate suffix so the
    /// Runtime host can locate the artifact directly.
    /// </summary>
    public static string ManifestRelativeArtifactPath(string artifactName, bool windows) =>
        $"../{artifactName}{(windows ? ".exe" : "")}";

    /// <summary>The host path relative to the distribution root for the given platform.</summary>
    public static string HostRelativePathFor(bool windows) =>
        RuntimeDirectoryName + "/" + HostFileName(windows);

    private const string ArtifactPrefix = "../";

    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Syntactic rule for a manifest's <c>Artifact</c>: exactly <c>../&lt;leaf&gt;</c> with forward slashes, the
    /// form <see cref="ManifestRelativeArtifactPath"/> writes. No backslash, drive, UNC, device or further
    /// <c>..</c> component can be expressed, whatever the host OS's path grammar. Throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static void ValidateManifestArtifactPath(string? artifact)
    {
        if (artifact is null || !artifact.StartsWith(ArtifactPrefix, StringComparison.Ordinal)
            || !IsPlainLeafName(artifact[ArtifactPrefix.Length..]))
        {
            throw new InvalidDataException(
                $"The manifest's artifact path must be '{ArtifactPrefix}<file name>' (one level above runtime/); got '{artifact}'.");
        }
    }

    /// <summary>Syntactic rule for a manifest's <c>Image</c>: exactly <see cref="ImageFileName"/>. Throws <see cref="InvalidDataException"/>.</summary>
    public static void ValidateManifestImagePath(string? image)
    {
        if (!string.Equals(image, ImageFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The manifest's image path must be '{ImageFileName}'; got '{image}'.");
        }
    }

    /// <summary>
    /// Resolves the artifact the manifest names and confirms it is a regular file sitting directly in the
    /// distribution root (the parent of <paramref name="manifestDirectory"/>).
    /// </summary>
    public static string ResolveArtifactPath(string manifestDirectory, string artifact)
    {
        ValidateManifestArtifactPath(artifact);
        var runtimeDirectory = Path.GetFullPath(manifestDirectory);
        EnsureRealDirectory(runtimeDirectory);
        var root = Path.GetDirectoryName(runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? throw new InvalidDataException("The manifest directory has no parent distribution root.");
        return ConfineRegularFile(Path.GetFullPath(Path.Combine(runtimeDirectory, artifact)), root, "artifact");
    }

    /// <summary>Resolves the image the manifest names and confirms it is a regular file directly inside the manifest's directory.</summary>
    public static string ResolveImagePath(string manifestDirectory, string image)
    {
        ValidateManifestImagePath(image);
        var runtimeDirectory = Path.GetFullPath(manifestDirectory);
        EnsureRealDirectory(runtimeDirectory);
        return ConfineRegularFile(Path.GetFullPath(Path.Combine(runtimeDirectory, image)), runtimeDirectory, "image");
    }

    // The manifest's own directory (runtime/) is part of the distribution: if it were a link, the image and
    // artifact names would resolve through it to wherever it points. It must be a real directory. Its
    // ancestors are deliberately not inspected (a relocated or symlinked install location is legitimate).
    // DirectoryInfo.LinkTarget covers POSIX symlinks and Windows symlinks/junctions; the attribute check
    // covers any other reparse point.
    private static void EnsureRealDirectory(string runtimeDirectory)
    {
        var info = new DirectoryInfo(runtimeDirectory);
        if (!info.Exists)
        {
            throw new InvalidDataException($"The manifest directory was not found: {runtimeDirectory}");
        }

        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The manifest directory (runtime/) must not be a symbolic link, junction or reparse point.");
        }
    }

    // Policy: the named file must exist directly in `container` (parent compared as whole normalised paths,
    // never by string prefix) and must not itself be a symbolic link / reparse point, so a link planted at
    // the artifact or image name cannot lead out of the distribution. Directories above the distribution are
    // not inspected (a relocated or symlinked install is legitimate). A swap between this check and the
    // later open/launch (TOCTOU) is not prevented: the distribution directory is trusted to be unwritable
    // by an attacker, the manifest is not. Trust boundary: the distribution directory (root and runtime/) is
    // trusted; the manifest contents and anything it names are validated here.
    private static string ConfineRegularFile(string fullPath, string container, string what)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !string.Equals(
                parent.TrimEnd(Path.DirectorySeparatorChar), container.TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            throw new InvalidDataException($"The manifest's {what} path resolves outside its permitted directory.");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new InvalidDataException($"The manifest's {what} file was not found: {fullPath}");
        }

        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"The manifest's {what} file must not be a symbolic link or reparse point.");
        }

        return fullPath;
    }

    private static bool IsPlainLeafName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.'))
        {
            return false;
        }

        if (name.Any(c => c < ' ' || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return false;
        }

        var stem = name.Split('.')[0].TrimEnd();
        return !ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
