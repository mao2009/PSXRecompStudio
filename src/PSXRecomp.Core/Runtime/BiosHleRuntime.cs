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

    /// <summary>
    /// Creates the registry. Only a service whose full guest-observable behavior
    /// can be modeled today is registered as <c>Supported</c> (ADR-014); a
    /// service whose documented effect needs Runtime capability that does not
    /// exist yet (e.g. A0:3E puts, which needs guest-memory access and an
    /// output sink) is deliberately left unregistered rather than registered
    /// with only its return value modeled.
    /// </summary>
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
            return BiosServiceResult.InvalidArguments(
                identity, "A0:3C putchar requires one character argument.");
        }

        // The service is intentionally modeled as a pure contract in Phase 1:
        // return the guest character and leave host output policy to a later
        // Runtime sink. This keeps the result deterministic and side-effect free.
        return BiosServiceResult.Supported(identity, identity.Arguments[0] & 0xFFu);
    }
}
