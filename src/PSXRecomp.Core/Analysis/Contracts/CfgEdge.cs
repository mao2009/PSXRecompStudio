using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// Directed control-flow edge between two addresses.
/// </summary>
[Domain]
public record CfgEdge(uint SourceAddress, uint TargetAddress, string? Kind = null)
{
    public bool IsValid()
    {
        return true;
    }

    public string ToTokenString()
    {
        return StableToken.Field("sourceAddress", SourceAddress.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("targetAddress", TargetAddress.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("kind", Kind);
    }
}