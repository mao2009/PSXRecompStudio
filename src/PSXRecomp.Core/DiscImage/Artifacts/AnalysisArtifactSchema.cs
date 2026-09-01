using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// Single source of truth for the deterministic real-ROM analysis artifact format:
/// schema versions, artifact kind identifiers, canonical file names, canonical
/// ordering labels, and the pure identity derivations shared by every document.
///
/// Every value here is part of the persisted contract. Changing the shape or the
/// meaning of a field in any document requires bumping the corresponding
/// <c>*SchemaVersion</c> constant, because consumers diff artifacts across
/// analyzer revisions and must be able to tell a schema change from an
/// analysis change.
/// </summary>
[Domain]
public static class AnalysisArtifactSchema
{
    /// <summary>Schema version of <c>manifest.json</c>.</summary>
    public const int ManifestSchemaVersion = 1;

    /// <summary>Schema version of <c>report.json</c>.</summary>
    public const int ReportSchemaVersion = 1;

    /// <summary>Schema version of <c>instructions.json</c>.</summary>
    public const int InstructionsSchemaVersion = 1;

    /// <summary>Schema version of <c>cfg.json</c>.</summary>
    public const int CfgSchemaVersion = 1;

    /// <summary>Artifact kind discriminator written into <c>manifest.json</c>.</summary>
    public const string ManifestArtifactKind = "psxrecomp.real-rom-analysis.manifest";

    /// <summary>Artifact kind discriminator written into <c>report.json</c>.</summary>
    public const string ReportArtifactKind = "psxrecomp.real-rom-analysis.report";

    /// <summary>Artifact kind discriminator written into <c>instructions.json</c>.</summary>
    public const string InstructionsArtifactKind = "psxrecomp.real-rom-analysis.instructions";

    /// <summary>Artifact kind discriminator written into <c>cfg.json</c>.</summary>
    public const string CfgArtifactKind = "psxrecomp.real-rom-analysis.cfg";

    public const string ManifestFileName = "manifest.json";
    public const string ReportFileName = "report.json";
    public const string InstructionsFileName = "instructions.json";
    public const string CfgFileName = "cfg.json";

    /// <summary>Canonical ordering of the <c>instructions</c> array, recorded in the artifact itself.</summary>
    public const string InstructionOrdering = "address-ascending";

    /// <summary>Canonical ordering of the <c>basicBlocks</c> array, recorded in the artifact itself.</summary>
    public const string BasicBlockOrdering = "start-address-ascending,end-address-ascending";

    /// <summary>Canonical ordering of the <c>edges</c> array, recorded in the artifact itself.</summary>
    public const string CfgEdgeOrdering = "source-address-ascending,target-address-ascending,kind-ordinal";

    /// <summary>Canonical ordering of every <c>*Mix</c> distribution array.</summary>
    public const string DistributionOrdering = "name-ordinal-ascending";

    /// <summary>Maximum length of a fixture identifier.</summary>
    public const int MaxFixtureIdLength = 64;

    /// <summary>
    /// True when <paramref name="fixtureId"/> is a canonical fixture identifier:
    /// 1..64 characters of lowercase ASCII letters, digits, '-', '_' or '.',
    /// starting with a letter or a digit. Fixture identifiers are human-facing
    /// aliases only; the formal identity of an analysis is the disc image SHA-256.
    /// </summary>
    public static bool IsValidFixtureId(string? fixtureId)
    {
        if (string.IsNullOrEmpty(fixtureId) || fixtureId.Length > MaxFixtureIdLength)
        {
            return false;
        }

        if (!IsLowerAlphanumeric(fixtureId[0]))
        {
            return false;
        }

        foreach (var character in fixtureId)
        {
            if (!IsLowerAlphanumeric(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deterministically maps an arbitrary human label (typically a fixture file name)
    /// onto a canonical fixture identifier. The transform is pure and culture-invariant:
    /// ASCII letters are lowercased, digits are kept, '-', '_' and '.' are kept, every
    /// other character collapses to a single '-', leading/trailing '-' are trimmed and
    /// the result is truncated to <see cref="MaxFixtureIdLength"/>.
    ///
    /// No title-specific knowledge is encoded here; any disc image can be named this way.
    /// Returns <c>"unnamed"</c> when nothing usable remains, so the result is always a
    /// valid fixture identifier.
    /// </summary>
    public static string NormalizeFixtureId(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return "unnamed";
        }

        var builder = new StringBuilder(label.Length);
        foreach (var character in label)
        {
            var lowered = ToLowerAsciiInvariant(character);
            if (IsLowerAlphanumeric(lowered) || lowered is '-' or '_' or '.')
            {
                builder.Append(lowered);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length > MaxFixtureIdLength)
        {
            normalized = normalized[..MaxFixtureIdLength].TrimEnd('-');
        }

        // A canonical id must start with a letter or a digit.
        var start = 0;
        while (start < normalized.Length && !IsLowerAlphanumeric(normalized[start]))
        {
            start++;
        }

        normalized = normalized[start..];
        return normalized.Length == 0 ? "unnamed" : normalized;
    }

    /// <summary>
    /// Derives the executable serial from the boot executable's on-disc file name.
    /// The transform is pure and title-agnostic: the ISO 9660 <c>;version</c> suffix is
    /// stripped, the name is uppercased invariantly, and a name shaped like
    /// <c>AAAA_NNN.NN</c> (the standard Sony disc label) is folded into its canonical
    /// <c>AAAA-NNNNN</c> form. Any other name is returned uppercased and unchanged, so
    /// homebrew and demo discs still produce a stable serial.
    /// </summary>
    public static string DeriveExecutableSerial(string? executableFileName)
    {
        if (string.IsNullOrEmpty(executableFileName))
        {
            return string.Empty;
        }

        var name = executableFileName;
        var versionSeparator = name.IndexOf(';', StringComparison.Ordinal);
        if (versionSeparator >= 0)
        {
            name = name[..versionSeparator];
        }

        name = name.ToUpperInvariant();

        // AAAA_NNN.NN -> AAAA-NNNNN
        if (name.Length == 11 && name[4] == '_' && name[8] == '.'
            && IsUpperAscii(name[0]) && IsUpperAscii(name[1]) && IsUpperAscii(name[2]) && IsUpperAscii(name[3])
            && IsDigit(name[5]) && IsDigit(name[6]) && IsDigit(name[7])
            && IsDigit(name[9]) && IsDigit(name[10]))
        {
            return string.Concat(name.AsSpan(0, 4), "-", name.AsSpan(5, 3), name.AsSpan(9, 2));
        }

        return name;
    }

    /// <summary>
    /// Formats a 32-bit value as the canonical, culture-invariant artifact address
    /// literal <c>0xXXXXXXXX</c>. Every address and raw instruction word in every
    /// artifact uses this form so textual diffs align column-for-column.
    /// </summary>
    public static string FormatWord32(uint value)
    {
        return "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static bool IsLowerAlphanumeric(char character)
    {
        return character is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    private static bool IsUpperAscii(char character) => character is >= 'A' and <= 'Z';

    private static bool IsDigit(char character) => character is >= '0' and <= '9';

    private static char ToLowerAsciiInvariant(char character)
    {
        return character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
    }
}
