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

    /// <summary>A0:3E puts, the sibling TTY-output service of <see cref="PutCharFunction" />.</summary>
    public const byte PutsFunction = 0x3E;

    private readonly IReadOnlyDictionary<(BiosCallFamily Family, byte Function), Func<BiosCallIdentity, BiosServiceResult>> services;

    /// <summary>Creates the registry of deterministic A0 TTY-output services.</summary>
    public BiosHleRuntime()
    {
        services = new Dictionary<(BiosCallFamily, byte), Func<BiosCallIdentity, BiosServiceResult>>
        {
            [(BiosCallFamily.A0, PutCharFunction)] = InvokePutChar,
            [(BiosCallFamily.A0, PutsFunction)] = InvokePuts,
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

    private static BiosServiceResult InvokePuts(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, "A0:3E puts requires one string-pointer argument.");
        }

        // Documented ABI: R4 is the address of a NUL-terminated string and R2
        // returns that same address. Like putchar, the Runtime models the pure
        // contract only: the guest string is not read and no host output is
        // emitted, so the result stays deterministic and side-effect free until
        // a Runtime output sink and guest-memory access exist (ADR-014).
        return BiosServiceResult.Supported(identity, identity.Arguments[0]);
    }
}
