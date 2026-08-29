using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Reference to a disassembled instruction inside a function.
/// </summary>
[Domain]
public record MnemonicRef(uint Address, string Mnemonic, string? Operands = null)
{
    public bool IsValid()
    {
        return Mnemonic.Length > 0;
    }

    public string ToTokenString()
    {
        return StableToken.Field("address", Address.ToString("x8", CultureInfo.InvariantCulture))
            + StableToken.Field("mnemonic", Mnemonic)
            + StableToken.Field("operands", Operands);
    }
}