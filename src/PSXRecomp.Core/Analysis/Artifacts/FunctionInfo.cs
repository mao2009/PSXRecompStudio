using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

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
        return Name.Length > 0
            && (Boundary is null || Boundary.IsValid());
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