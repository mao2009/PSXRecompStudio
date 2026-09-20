using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Project;

/// <summary>
/// Domain-owned port for persisting and reloading a <see cref="ProjectManifestDocument"/>
/// in a caller-selected directory (Issue #42). The concrete adapter performs the actual
/// file I/O and lives in managed Infrastructure, per the managed host I/O port-adapter
/// boundary; Domain and Application depend only on this contract.
///
/// This is also the production <b>reopen contract</b>: given a project directory,
/// <see cref="Load"/> returns the canonical, validated manifest that answers "which
/// Project does this directory hold, and which input identity does it analyze?" without
/// ever returning <see langword="null"/> or silently falling back to a default. Every
/// failure reason is a distinct, documented exception.
/// </summary>
[Domain]
public interface IProjectMetadataStore
{
    /// <summary>
    /// Validates and writes <paramref name="document"/> as <c>project.json</c> under
    /// <paramref name="directoryPath"/>, creating the directory if it does not exist.
    /// The directory's lifecycle belongs entirely to the caller; no hidden temp
    /// workspace or environment-dependent location is used.
    /// </summary>
    void Save(ProjectManifestDocument document, string directoryPath);

    /// <summary>
    /// Reopens the project stored under <paramref name="directoryPath"/>: reads
    /// <c>project.json</c>, validates its format version, artifact kind, project id and
    /// SHA-256 identity, and returns the canonical normalized manifest. This is the code
    /// path that answers "which Project does this directory correspond to, and which
    /// input identity does it analyze" from saved management data.
    ///
    /// Failure is explicit and never ambiguous — no <see langword="null"/> return and no
    /// silent fallback:
    /// <list type="bullet">
    ///   <item><see cref="ArgumentException"/> — <paramref name="directoryPath"/> is null, empty or whitespace.</item>
    ///   <item><see cref="System.IO.DirectoryNotFoundException"/> — the directory does not exist.</item>
    ///   <item><see cref="System.IO.FileNotFoundException"/> — the directory exists but has no <c>project.json</c>.</item>
    ///   <item><see cref="System.Text.Json.JsonException"/> — <c>project.json</c> is not well-formed JSON or a required field is missing.</item>
    ///   <item><see cref="NotSupportedException"/> — the manifest's format version is not understood by this build.</item>
    ///   <item><see cref="FormatException"/> — <c>artifactKind</c>, <c>projectId</c> or <c>inputSha256</c> is invalid.</item>
    /// </list>
    /// </summary>
    ProjectManifestDocument Load(string directoryPath);

    /// <summary>
    /// Safe, presence-only probe: true iff a <c>project.json</c> manifest file exists
    /// directly under <paramref name="directoryPath"/>. It never parses or validates the
    /// manifest contents — a directory holding a malformed, foreign, or
    /// version-incompatible <c>project.json</c> still reports true, so a caller that
    /// needs identity or validity must follow up with <see cref="Load"/>.
    ///
    /// A missing directory, a non-directory path, or a directory without a
    /// <c>project.json</c> yield <see langword="false"/>; only an invalid
    /// <paramref name="directoryPath"/> argument (null, empty or whitespace) throws
    /// <see cref="ArgumentException"/>. This keeps "is this a project at all?" distinct
    /// from "is this a <em>valid</em> project?", so reopen callers never catch an
    /// exception just to decide the directory is not a project.
    /// </summary>
    bool IsProjectDirectory(string directoryPath);
}
