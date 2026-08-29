using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Directed control-flow edge between two addresses.
/// </summary>
[Domain]
public record CfgEdge(uint SourceAddress, uint TargetAddress, string? Kind = null)
{
    public string ToTokenString()
    {
        return StableToken.Field("sourceAddress", SourceAddress.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("targetAddress", TargetAddress.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("kind", Kind);
    }
}