using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// Identifies an individual disc within a multi-disc title. Carries the serial, region,
/// revision, the disc index and an optional layout hint describing the disc structure.
/// </summary>
[Domain]
public sealed record DiscIdentity(
    string Serial,
    Region Region,
    Revision Revision,
    int DiscIndex)
{
    /// <summary>
    /// Optional free-form hint describing the disc layout / format (e.g. "single-session",
    /// "mixed-mode"). Empty when not recorded.
    /// </summary>
    public string? LayoutHint { get; init; }

    /// <summary>
    /// Stable, deterministic canonical key combining serial, region and disc index.
    /// </summary>
    public string CanonicalKey => $"{Serial}@{Region}:disc{DiscIndex}";

    /// <inheritdoc />
    public override string ToString() => CanonicalKey;
}
