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
    /// Stable, deterministic canonical key combining serial, region, revision and the
    /// normalized title name. Consistent with value-based record equality so unequal
    /// identities never share a key. Suitable as a dictionary key or persisted identifier.
    /// </summary>
    public string CanonicalKey => $"{Serial}@{Region}:{Revision.CanonicalKey}:{TitleName.Trim()}";

    /// <inheritdoc />
    public override string ToString() => CanonicalKey;
}
