using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>Machine-readable outcome of a Runtime BIOS service invocation.</summary>
[Domain]
public sealed record BiosServiceResult(
    BiosServiceStatus Status,
    uint? ReturnValue,
    BiosDiagnostic? Diagnostic)
{
    /// <summary>Creates a successful service result.</summary>
    public static BiosServiceResult Supported(BiosCallIdentity identity, uint? returnValue = null) =>
        CreateSupported(identity, returnValue);

    /// <summary>Creates the explicit result for a service not implemented by HLE.</summary>
    public static BiosServiceResult Unsupported(BiosCallIdentity identity) =>
        CreateUnsupported(identity);

    private static BiosServiceResult CreateSupported(BiosCallIdentity identity, uint? returnValue)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(BiosServiceStatus.Supported, returnValue, null);
    }

    private static BiosServiceResult CreateUnsupported(BiosCallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            BiosServiceStatus.Unsupported,
            null,
            new BiosDiagnostic(
                "BIOS_HLE_UNSUPPORTED_CALL",
                identity,
                $"No HLE implementation is registered for {identity.StableKey}."));
    }
}

/// <summary>Whether the Runtime BIOS service was implemented or rejected.</summary>
[Domain]
public enum BiosServiceStatus : byte
{
    Supported,
    Unsupported,
}

/// <summary>Stable diagnostic data for an unsupported or otherwise rejected BIOS call.</summary>
[Domain]
public sealed record BiosDiagnostic(string Code, BiosCallIdentity Identity, string Message)
{
    /// <summary>Formats the diagnostic without process- or title-specific data.</summary>
    public string ToStableString() =>
        $"{Code}|{Identity.StableKey}|pc={(Identity.GuestPc is uint pc ? $"0x{pc:X8}" : "unknown")}|{Message}";
}
