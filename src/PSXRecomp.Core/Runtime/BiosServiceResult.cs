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

    /// <summary>
    /// Creates the explicit result for a registered service invoked with an ABI
    /// shape it does not accept. Shared by every service so argument rejection
    /// is never hand-rolled per implementation.
    /// </summary>
    public static BiosServiceResult InvalidArguments(BiosCallIdentity identity, string message) =>
        CreateInvalidArguments(identity, message);

    /// <summary>
    /// Creates the explicit result for a registered service that reached a state
    /// its HLE implementation cannot represent (for example an unmapped guest
    /// pointer). Status stays <see cref="BiosServiceStatus.Unsupported"/> — the
    /// status vocabulary is unchanged; only the diagnostic code distinguishes
    /// this from an unregistered call or a rejected argument shape.
    /// </summary>
    public static BiosServiceResult UnsupportedState(BiosCallIdentity identity, string message) =>
        CreateUnsupportedState(identity, message);

    /// <summary>
    /// Creates the result for a jump-table entry that guest code has patched to
    /// its own target. <see cref="ReturnValue"/> carries the patched guest target
    /// address (not a BIOS return-register value) so a caller can act on it; this
    /// Runtime does not execute or validate that target itself — see ADR-014's
    /// amendment for #360.
    /// </summary>
    public static BiosServiceResult PatchedTarget(BiosCallIdentity identity, uint targetAddress) =>
        CreatePatchedTarget(identity, targetAddress);

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

    private static BiosServiceResult CreateInvalidArguments(BiosCallIdentity identity, string message)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(message);
        return new(
            BiosServiceStatus.Unsupported,
            null,
            new BiosDiagnostic("BIOS_HLE_INVALID_ARGUMENTS", identity, message));
    }

    private static BiosServiceResult CreateUnsupportedState(BiosCallIdentity identity, string message)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(message);
        return new(
            BiosServiceStatus.Unsupported,
            null,
            new BiosDiagnostic("BIOS_HLE_UNSUPPORTED_STATE", identity, message));
    }

    private static BiosServiceResult CreatePatchedTarget(BiosCallIdentity identity, uint targetAddress)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            BiosServiceStatus.PatchedTarget,
            targetAddress,
            new BiosDiagnostic(
                "BIOS_HLE_PATCHED_TARGET",
                identity,
                $"{identity.StableKey}: jump-table entry was patched to guest address 0x{targetAddress:X8}; " +
                "this Runtime does not yet execute a patched target (no interpreter/recompiled dispatch trap exists)."));
    }
}

/// <summary>Whether the Runtime BIOS service was implemented or rejected.</summary>
[Domain]
public enum BiosServiceStatus : byte
{
    Supported,
    Unsupported,
    PatchedTarget,
}

/// <summary>Stable diagnostic data for an unsupported or otherwise rejected BIOS call.</summary>
[Domain]
public sealed record BiosDiagnostic(string Code, BiosCallIdentity Identity, string Message)
{
    /// <summary>Formats the diagnostic without process- or title-specific data.</summary>
    public string ToStableString() =>
        $"{Code}|{Identity.StableKey}|pc={(Identity.GuestPc is uint pc ? $"0x{pc:X8}" : "unknown")}|{Message}";
}
