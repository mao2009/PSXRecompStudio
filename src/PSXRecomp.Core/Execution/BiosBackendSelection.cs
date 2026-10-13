using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// The BIOS backends a standalone game distribution can be built for (Issue #732).
/// Only <see cref="OpenBios"/> executes today; the others are reserved selection
/// values so that choosing one is a clear, fail-closed error rather than a silent
/// substitution (no implicit OpenBIOS → HLE fallback, no fake BIOS success).
/// </summary>
[Domain]
public enum BiosBackendKind
{
    /// <summary>Real PCSX-Redux Nugget OpenBIOS MIPS code on the shared CPU/device graph (default).</summary>
    OpenBios,

    /// <summary>A future project-owned HLE BIOS. Not implemented.</summary>
    CustomHle,

    /// <summary>A future user-supplied genuine console BIOS image. Not implemented.</summary>
    Genuine,
}

/// <summary>A backend that is selectable by name but has no implementation yet.</summary>
[Domain]
public sealed class BiosBackendNotImplementedException(BiosBackendKind kind)
    : NotSupportedException(
        $"BIOS backend '{BiosBackendSelection.NameOf(kind)}' is not implemented; " +
        $"only '{BiosBackendSelection.NameOf(BiosBackendKind.OpenBios)}' can run today. " +
        "No other backend is substituted.")
{
    public BiosBackendKind Kind { get; } = kind;
}

/// <summary>Parses the explicit <c>--bios</c> selection; the default is OpenBIOS.</summary>
[Domain]
public static class BiosBackendSelection
{
    public const BiosBackendKind Default = BiosBackendKind.OpenBios;

    public static string NameOf(BiosBackendKind kind) => kind switch
    {
        BiosBackendKind.OpenBios => "openbios",
        BiosBackendKind.CustomHle => "custom-hle",
        BiosBackendKind.Genuine => "genuine",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Parses a backend name (case-insensitive). Unknown names are rejected, never defaulted.</summary>
    public static BiosBackendKind Parse(string? name)
    {
        if (name is null)
        {
            return Default;
        }

        foreach (var kind in Enum.GetValues<BiosBackendKind>())
        {
            if (string.Equals(NameOf(kind), name, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        throw new ArgumentException(
            $"Unknown BIOS backend '{name}'; expected openbios, custom-hle or genuine.", nameof(name));
    }

    /// <summary>
    /// Returns <paramref name="kind"/> only when it can execute; otherwise throws
    /// <see cref="BiosBackendNotImplementedException"/>.
    /// </summary>
    public static BiosBackendKind RequireImplemented(BiosBackendKind kind) =>
        kind == BiosBackendKind.OpenBios ? kind : throw new BiosBackendNotImplementedException(kind);
}
