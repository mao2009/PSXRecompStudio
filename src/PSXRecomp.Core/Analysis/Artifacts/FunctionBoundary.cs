using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Inclusive function address range.
/// </summary>
[Domain]
public record FunctionBoundary(uint StartAddress, uint EndAddress)
{
    public bool IsValid()
    {
        return StartAddress <= EndAddress;
    }

    public string ToTokenString()
    {
        return StableToken.Field("startAddress", StartAddress.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("endAddress", EndAddress.ToString("x8", CultureInfo.InvariantCulture));
    }
}