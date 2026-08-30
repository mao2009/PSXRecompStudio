using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// A stable, verifiable reference to evidence attached to a finding.
/// The <see cref="Id"/> is derived deterministically from the content and never randomized.
/// </summary>
[Domain]
public record EvidenceReference(
    string Id,
    EvidenceType Type,
    string Source,
    string Description,
    long? CapturedUnixSeconds,
    IReadOnlyDictionary<string, string>? Metadata)
{
    /// <summary>
    /// Creates an evidence reference whose id is the deterministic content hash.
    /// Same inputs always produce the same id.
    /// </summary>
    public static EvidenceReference Create(
        EvidenceType type,
        string source,
        string description,
        long? capturedUnixSeconds = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var _reference = new EvidenceReference(string.Empty, type, source, description, capturedUnixSeconds, metadata);
        return _reference with { Id = StableToken.Hash(_reference.ToTokenString()) };
    }

    public bool IsValid()
    {
        return IsNonEmpty(Id)
            && IsNonEmpty(Source)
            && IsNonEmpty(Description)
            && Enum.IsDefined(Type)
            && (CapturedUnixSeconds is null or >= 0)
            && Id == StableToken.Hash(ToTokenString());
    }

    private static bool IsNonEmpty(string? value)
    {
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>
    /// Deterministic canonical form of the evidence content. The id is the hash of this token.
    /// </summary>
    public string ToTokenString()
    {
        return StableToken.Field("type", Type.ToString())
            + StableToken.Field("source", Source)
            + StableToken.Field("description", Description)
            + StableToken.Field("capturedUnixSeconds", StableToken.FormatLong(CapturedUnixSeconds))
            + StableToken.Field("metadata", StableToken.CanonicalMetadata(Metadata));
    }
}