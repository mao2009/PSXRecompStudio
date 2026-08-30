using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// Reference to a disassembled instruction inside a function.
/// </summary>
[Domain]
public record MnemonicRef(uint Address, string Mnemonic, string? Operands = null)
{
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(Mnemonic);
    }

    public string ToTokenString()
    {
        return StableToken.Field("address", Address.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("mnemonic", Mnemonic)
            + StableToken.Field("operands", Operands);
    }
}