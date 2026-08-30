using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// A specific revision of a title within a region. Build date is stored as year/month
/// integers (never <see cref="System.DateTime"/>) to keep the model deterministic.
/// </summary>
[Domain]
public sealed record Revision
{
    public Revision(int level, int buildYear, int buildMonth)
    {
        if (buildMonth is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(buildMonth), "BuildMonth must be between 1 and 12.");
        }

        Level = level;
        BuildYear = buildYear;
        BuildMonth = buildMonth;
    }

    public int Level { get; init; }

    public int BuildYear { get; init; }

    public int BuildMonth { get; init; }

    /// <summary>Free-form release notes for this revision. Empty when not recorded.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// Stable, deterministic canonical key for this revision. Uniquely identifies a
    /// version within a title independent of any presentation formatting.
    /// </summary>
    public string CanonicalKey => $"{Level}.{BuildYear:D4}.{BuildMonth:D2}";

    /// <inheritdoc />
    public override string ToString() => CanonicalKey;
}
