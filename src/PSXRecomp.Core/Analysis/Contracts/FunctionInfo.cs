using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// Function / symbol / CFG information discovered by an analysis.
/// </summary>
[Domain]
public record FunctionInfo(
    string Name,
    FunctionBoundary? Boundary = null,
    IReadOnlyList<MnemonicRef>? Mnemonics = null,
    IReadOnlyList<CfgEdge>? Edges = null,
    string? Overlay = null)
{
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(Name)
            && (Boundary is null || Boundary.IsValid())
            && AllValid(Mnemonics, static mnemonic => mnemonic.IsValid())
            && AllValid(Edges, static edge => edge.IsValid());
    }

    private static bool AllValid<T>(IReadOnlyList<T>? items, Func<T, bool> isValid)
    {
        if (items is null)
        {
            return true;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is null || !isValid(items[index]))
            {
                return false;
            }
        }

        return true;
    }

    public string ToTokenString()
    {
        var _builder = new StringBuilder();
        StableToken.AppendField(_builder, "name", Name);
        StableToken.AppendField(_builder, "boundary", Boundary?.ToTokenString());
        StableToken.AppendField(_builder, "overlay", Overlay);
        StableToken.AppendIndexed(_builder, "mnemonic", Mnemonics, static mnemonic => mnemonic.ToTokenString());
        StableToken.AppendIndexed(_builder, "edge", Edges, static edge => edge.ToTokenString());
        return _builder.ToString();
    }
}