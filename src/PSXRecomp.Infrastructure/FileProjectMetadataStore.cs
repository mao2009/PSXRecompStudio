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

        // Distinguish the absence cases explicitly (and deterministically) before any
        // file read, so the reopen contract's failure modes never depend on the subtle
        // exception File.ReadAllBytes chooses for a given path shape.
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Project directory '{directoryPath}' does not exist.");
        }

        var manifestPath = Path.Combine(directoryPath, ProjectSchema.ProjectManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"'{ProjectSchema.ProjectManifestFileName}' was not found under project "
                + $"directory '{directoryPath}'; this directory is not a reopenable "
                + "PSXRecomp project.",
                manifestPath);
        }

        var bytes = File.ReadAllBytes(manifestPath);
        return ProjectMetadataSerializer.Deserialize(bytes);
    }

    public bool IsProjectDirectory(string directoryPath)
    {
        ValidateDirectoryPath(directoryPath);

        // Presence-only: File.Exists is false for a missing directory, a non-directory
        // path, a missing manifest, and a manifest that is actually a directory, so all
        // "not a project" outcomes collapse to false without throwing.
        return Directory.Exists(directoryPath)
            && File.Exists(Path.Combine(directoryPath, ProjectSchema.ProjectManifestFileName));
    }

    private static void ValidateDirectoryPath(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("directoryPath must be a non-empty path.", nameof(directoryPath));
        }
    }
}
