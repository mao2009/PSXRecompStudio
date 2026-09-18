using PSXRecomp.Architecture;
using PSXRecomp.Core.Project;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Concrete managed adapter for <see cref="IProjectMetadataStore"/> (Issue #42):
/// writes and reads <c>project.json</c> directly under a caller-selected directory.
/// Creates no hidden temporary workspace of its own — the directory's lifecycle
/// belongs entirely to the caller.
/// </summary>
[Infrastructure]
public sealed class FileProjectMetadataStore : IProjectMetadataStore
{
    public void Save(ProjectManifestDocument document, string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDirectoryPath(directoryPath);

        // Validate/encode before touching disk, so an invalid document never
        // creates the directory or a partial file.
        var bytes = ProjectMetadataSerializer.Serialize(document);

        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(Path.Combine(directoryPath, ProjectSchema.ProjectManifestFileName), bytes);
    }

    public ProjectManifestDocument Load(string directoryPath)
    {
        ValidateDirectoryPath(directoryPath);

        var path = Path.Combine(directoryPath, ProjectSchema.ProjectManifestFileName);
        var bytes = File.ReadAllBytes(path);
        return ProjectMetadataSerializer.Deserialize(bytes);
    }

    private static void ValidateDirectoryPath(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("directoryPath must be a non-empty path.", nameof(directoryPath));
        }
    }
}
