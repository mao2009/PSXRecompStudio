using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Project;

/// <summary>
/// Single source of truth for the persisted Project management-data manifest
/// format (Issue #42): the format version, the artifact-kind discriminator,
/// the canonical file name, and the SHA-256 identity validation/normalization
/// every manifest field must satisfy.
/// </summary>
[Domain]
public static class ProjectSchema
{
    /// <summary>
    /// Format version of <c>project.json</c>. Any shape or meaning change to a
    /// persisted field requires bumping this constant so a reader can tell a
    /// format change from a data change (see <see cref="ProjectMetadataSerializer"/>).
    /// </summary>
    public const int ProjectFormatVersion = 1;

    /// <summary>Artifact kind discriminator written into <c>project.json</c>.</summary>
    public const string ProjectArtifactKind = "psxrecomp.project.manifest";

    /// <summary>Canonical file name of the Project management-data manifest.</summary>
    public const string ProjectManifestFileName = "project.json";

    private const int Sha256HexLength = 64;

    /// <summary>
    /// True when <paramref name="value"/> is exactly <see cref="Sha256HexLength"/>
    /// hexadecimal characters (either case). Does not allocate or normalize.
    /// </summary>
    public static bool IsValidSha256Hex(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHexDigit = character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates <paramref name="value"/> as a SHA-256 hex digest and returns its
    /// canonical lowercase form. Throws <see cref="FormatException"/> rather than
    /// silently accepting or truncating an invalid value (wrong length, non-hex
    /// characters, or <see langword="null"/>/empty).
    /// </summary>
    public static string NormalizeSha256Hex(string? value)
    {
        if (!IsValidSha256Hex(value))
        {
            throw new FormatException(
                $"Invalid SHA-256 hex digest: expected exactly {Sha256HexLength} hexadecimal characters.");
        }

        return value!.ToLowerInvariant();
    }
}
