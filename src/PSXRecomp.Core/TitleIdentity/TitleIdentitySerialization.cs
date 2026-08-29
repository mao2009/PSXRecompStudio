using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// Pure, deterministic serialization helpers that project the title identity contracts
/// onto a plain dictionary shape suitable for JSON or other structured persistence.
///
/// JSON shape (example for <see cref="TitleIdentity"/>):
/// <code>
/// {
///   "serial": "SLUS-00594",
///   "titleName": "Example",
///   "region": "NorthAmerica",
///   "revision": "1.1997.09",
///   "canonicalKey": "SLUS-00594@NorthAmerica:1.1997.09"
/// }
/// </code>
/// The values intentionally repeat the canonical key so a consumer can round-trip a
/// stable identity without re-deriving it. No JSON library is referenced by the Domain
/// layer; callers serialize the returned dictionaries with their JSON stack of choice.
/// </summary>
[Domain]
public static class TitleIdentitySerialization
{
    /// <summary>Projects a <see cref="TitleIdentity"/> onto a flat, deterministic dictionary.</summary>
    public static IReadOnlyDictionary<string, string> ToJsonShape(TitleIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new Dictionary<string, string>
        {
            ["serial"] = identity.Serial,
            ["titleName"] = identity.TitleName,
            ["region"] = identity.Region.ToString(),
            ["revision"] = identity.Revision.CanonicalKey,
            ["canonicalKey"] = identity.CanonicalKey,
        };
    }

    /// <summary>Projects a <see cref="DiscIdentity"/> onto a flat, deterministic dictionary.</summary>
    public static IReadOnlyDictionary<string, string> ToJsonShape(DiscIdentity disc)
    {
        ArgumentNullException.ThrowIfNull(disc);
        return new Dictionary<string, string>
        {
            ["serial"] = disc.Serial,
            ["region"] = disc.Region.ToString(),
            ["revision"] = disc.Revision.CanonicalKey,
            ["discIndex"] = disc.DiscIndex.ToString(),
            ["layoutHint"] = disc.LayoutHint ?? string.Empty,
            ["canonicalKey"] = disc.CanonicalKey,
        };
    }

    /// <summary>Projects an <see cref="ExecutableIdentity"/> onto a flat, deterministic dictionary.</summary>
    public static IReadOnlyDictionary<string, string> ToJsonShape(ExecutableIdentity executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return new Dictionary<string, string>
        {
            ["fileName"] = executable.FileName,
            ["imageLoadAddress"] = $"0x{executable.ImageLoadAddress:X8}",
            ["entryPoint"] = $"0x{executable.EntryPoint:X8}",
            ["size"] = executable.Size.ToString(),
            ["fileHashHex"] = executable.FileHashHex,
            ["canonicalKey"] = executable.CanonicalKey,
        };
    }

    /// <summary>Projects a <see cref="BootExecutableFingerprint"/> onto a flat, deterministic dictionary.</summary>
    public static IReadOnlyDictionary<string, string> ToJsonShape(BootExecutableFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return new Dictionary<string, string>
        {
            ["algorithm"] = fingerprint.Algorithm,
            ["value"] = fingerprint.Value,
            ["canonicalKey"] = fingerprint.CanonicalKey,
        };
    }
}
