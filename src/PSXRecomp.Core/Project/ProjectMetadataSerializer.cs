using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Core.Project;

/// <summary>
/// Pure save/load contract for <see cref="ProjectManifestDocument"/>: validates
/// and canonically encodes a document to bytes, and validates and decodes bytes
/// back into a document. No file, path, or other host I/O is performed here —
/// that is <see cref="IProjectMetadataStore"/>'s responsibility — so this type
/// is directly reusable from a GUI, a CLI, or an MCP server (Issue #42).
/// </summary>
[Domain]
public static class ProjectMetadataSerializer
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Validates <paramref name="document"/> and returns its canonical UTF-8
    /// encoding (see <see cref="ArtifactJson"/>): the same document value always
    /// produces the same bytes, on any machine, in any locale.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ProjectManifestDocument.SchemaVersion"/> is not the format version
    /// this serializer writes.
    /// </exception>
    /// <exception cref="FormatException">
    /// <see cref="ProjectManifestDocument.ProjectId"/> or
    /// <see cref="ProjectManifestDocument.InputSha256"/> is invalid.
    /// </exception>
    public static byte[] Serialize(ProjectManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Validate(document);
        var json = ArtifactJson.Serialize(normalized);
        return ArtifactJson.ToUtf8Bytes(json);
    }

    /// <summary>
    /// Decodes and validates a <see cref="ProjectManifestDocument"/> from its
    /// canonical UTF-8 encoding.
    /// </summary>
    /// <exception cref="JsonException">
    /// The bytes are not well-formed JSON, or a required field is missing.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document's <see cref="ProjectManifestDocument.SchemaVersion"/> is not a
    /// version this serializer understands.
    /// </exception>
    /// <exception cref="FormatException">
    /// <see cref="ProjectManifestDocument.ProjectId"/> or
    /// <see cref="ProjectManifestDocument.InputSha256"/> is invalid.
    /// </exception>
    public static ProjectManifestDocument Deserialize(byte[] utf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(utf8Bytes);
        var document = JsonSerializer.Deserialize<ProjectManifestDocument>(utf8Bytes, ReadOptions)
            ?? throw new JsonException("project.json decoded to a null document.");
        return Validate(document);
    }

    /// <summary>
    /// Checks every field's validity and returns a copy with identity fields in
    /// their canonical normalized form (lowercase SHA-256 hex). Never accepted
    /// silently: an unsupported format version or an invalid identity field
    /// throws rather than falling back to a default.
    /// </summary>
    private static ProjectManifestDocument Validate(ProjectManifestDocument document)
    {
        if (document.SchemaVersion != ProjectSchema.ProjectFormatVersion)
        {
            throw new NotSupportedException(
                $"Unsupported project format version {document.SchemaVersion}; "
                + $"this build understands version {ProjectSchema.ProjectFormatVersion} only.");
        }

        if (!AnalysisArtifactSchema.IsValidFixtureId(document.ProjectId))
        {
            throw new FormatException(
                $"Invalid ProjectId '{document.ProjectId}': expected 1-"
                + $"{AnalysisArtifactSchema.MaxFixtureIdLength} lowercase alphanumeric characters, "
                + "'-', '_' or '.', starting with a letter or digit.");
        }

        var normalizedInputSha256 = ProjectSchema.NormalizeSha256Hex(document.InputSha256);

        return document with
        {
            ArtifactKind = ProjectSchema.ProjectArtifactKind,
            InputSha256 = normalizedInputSha256,
        };
    }
}
