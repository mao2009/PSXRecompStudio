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
    /// Creates the registry. A service is registered as <c>Supported</c> only
    /// when no guest-memory access needed to compute its documented contract
    /// is skipped (ADR-014); a service whose documented effect depends on
    /// guest memory it cannot yet read (e.g. A0:3E puts) is deliberately left
    /// unregistered rather than registered with only its return value
    /// modeled. A0:3C putchar is accepted as a narrower Phase-1 <c>Supported</c>:
    /// its argument is a plain scalar, so nothing about its CPU-observable
    /// outcome is skipped, but its TTY output side effect is not yet
    /// implemented (tracked under Issue #279) — this is not a claim that
    /// putchar is fully implemented.
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

        // Phase-1-limited: this models only the register-visible return-value
        // contract. The documented TTY output side effect is NOT implemented
        // yet (tracked under Issue #279) and this must not be read as "putchar
        // is fully implemented" — see ADR-014's note on what Supported means.
        return BiosServiceResult.Supported(identity, identity.Arguments[0] & 0xFFu);
    }
}
