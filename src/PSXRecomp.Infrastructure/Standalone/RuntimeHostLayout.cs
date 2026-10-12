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
}
