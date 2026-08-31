using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// A single decoded instruction with its address, raw word, mnemonic, and operands.
/// </summary>
[Domain]
public sealed record DecodedInstruction
{
    public required uint Address { get; init; }
    public required uint RawWord { get; init; }
    public required string Mnemonic { get; init; }
    public required string Operands { get; init; }
    public required string Format { get; init; }
    public required string ControlFlow { get; init; }
}
