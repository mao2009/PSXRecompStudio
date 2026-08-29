using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// Canonical identity of a title: product/serial code, title name, region and revision.
/// Equality is value-based (a record) so the same logical title maps to equal instances.
/// </summary>
[Domain]
public sealed record TitleIdentity(
    string Serial,
    string TitleName,
    Region Region,
    Revision Revision)
{
    /// <summary>
    /// Stable, deterministic canonical key combining serial, region and revision.
    /// Suitable as a dictionary key or persisted identifier for provenance.
    /// </summary>
    public string CanonicalKey => $"{Serial}@{Region}:{Revision.CanonicalKey}";

    /// <inheritdoc />
    public override string ToString() => CanonicalKey;
}
