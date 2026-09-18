using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Project;

/// <summary>
/// Domain-owned port for persisting and reloading a <see cref="ProjectManifestDocument"/>
/// in a caller-selected directory (Issue #42). The concrete adapter performs the actual
/// file I/O and lives in managed Infrastructure, per the managed host I/O port-adapter
/// boundary; Domain and Application depend only on this contract.
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
    /// Reads and validates the <c>project.json</c> manifest under
    /// <paramref name="directoryPath"/>.
    /// </summary>
    ProjectManifestDocument Load(string directoryPath);
}
