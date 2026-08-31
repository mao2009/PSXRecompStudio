using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Records a failed decode attempt for a specific address.
/// </summary>
[Domain]
public sealed record DecodeFailure
{
    public required uint Address { get; init; }
    public required string Reason { get; init; }
}
