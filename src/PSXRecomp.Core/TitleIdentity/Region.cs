using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// Geographic release region of a title. Values are explicit and stable so they can be
/// persisted and compared across analysis runs without ambiguity.
/// </summary>
[Domain]
public enum Region
{
    /// <summary>Region not recorded or not yet identified.</summary>
    Unknown = 0,

    /// <summary>Japan release.</summary>
    Japan = 1,

    /// <summary>North America release.</summary>
    NorthAmerica = 2,

    /// <summary>Europe release.</summary>
    Europe = 3,

    /// <summary>Asia release.</summary>
    Asia = 4,

    /// <summary>Korea release.</summary>
    Korea = 5,

    /// <summary>Australia / Oceania release.</summary>
    Australia = 6,
}
