using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Minimal BIOS-less Runtime dispatcher. The registry is deliberately generic:
/// service implementations are selected by A0/B0/C0 identity, never by title,
/// address range, or generated-code provenance.
/// </summary>
[Domain]
public sealed class BiosHleRuntime : IBiosRuntime
{
    /// <summary>A0:3C putchar, the first deterministic service in this vertical slice.</summary>
    public const byte PutCharFunction = 0x3C;

    private readonly IReadOnlyDictionary<(BiosCallFamily Family, byte Function), Func<BiosCallIdentity, BiosServiceResult>> services;

    /// <summary>Creates the Phase 1 registry with the deterministic A0 putchar service.</summary>
    public BiosHleRuntime()
    {
        services = new Dictionary<(BiosCallFamily, byte), Func<BiosCallIdentity, BiosServiceResult>>
        {
            [(BiosCallFamily.A0, PutCharFunction)] = InvokePutChar,
        };
    }

    /// <inheritdoc />
    public BiosServiceResult Invoke(BiosCallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return services.TryGetValue((identity.Family, identity.FunctionNumber), out var service)
            ? service(identity)
            : BiosServiceResult.Unsupported(identity);
    }

    private static BiosServiceResult InvokePutChar(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 1)
        {
            return new BiosServiceResult(
                BiosServiceStatus.Unsupported,
                null,
                new BiosDiagnostic(
                    "BIOS_HLE_INVALID_ARGUMENTS",
                    identity,
                    "A0:3C putchar requires one character argument."));
        }

        // The service is intentionally modeled as a pure contract in Phase 1:
        // return the guest character and leave host output policy to a later
        // Runtime sink. This keeps the result deterministic and side-effect free.
        return BiosServiceResult.Supported(identity, identity.Arguments[0] & 0xFFu);
    }
}
