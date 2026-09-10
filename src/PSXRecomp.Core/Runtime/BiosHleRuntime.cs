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

    /// <summary>A0:3E puts, the first service that reads guest memory.</summary>
    public const byte PutsFunction = 0x3E;

    private readonly IReadOnlyDictionary<(BiosCallFamily Family, byte Function), Func<BiosCallIdentity, BiosServiceResult>> services;
    private readonly IRuntimeOutputSink _outputSink;
    private readonly IGuestMemoryReader _guestMemoryReader;

    /// <summary>
    /// Creates the registry over the two Runtime boundaries its services need:
    /// the output boundary every TTY-class service writes through, and the
    /// guest-memory boundary the string-reading services scan through. A service
    /// is registered as <c>Supported</c> only when its full documented behavior —
    /// including host-visible output and any guest-memory access the behavior
    /// depends on — is implemented (ADR-014); a service whose documented effect
    /// the Runtime cannot yet honour is left unregistered rather than registered
    /// with only its return value modeled.
    /// </summary>
    /// <param name="outputSink">
    /// Receives the bytes registered services emit, such as A0:3C putchar's TTY
    /// character and A0:3E puts's string. Required: a missing sink is a
    /// construction error, never a silent no-op, so a host can never run the
    /// registry with a documented side effect quietly discarded.
    /// </param>
    /// <param name="guestMemoryReader">
    /// Reads the guest bytes pointer-taking services dereference, such as A0:3E
    /// puts's string. Required for the same reason as the sink: a missing reader
    /// would leave a registered service unable to perform the guest-memory access
    /// its documented behavior depends on.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="outputSink"/> or <paramref name="guestMemoryReader"/> is null.
    /// </exception>
    public BiosHleRuntime(IRuntimeOutputSink outputSink, IGuestMemoryReader guestMemoryReader)
    {
        ArgumentNullException.ThrowIfNull(outputSink);
        ArgumentNullException.ThrowIfNull(guestMemoryReader);
        _outputSink = outputSink;
        _guestMemoryReader = guestMemoryReader;

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

    /// <summary>
    /// A0:3C putchar. Writes the low byte of the character argument to the
    /// injected output sink and returns that same byte, which is putchar's full
    /// documented behavior (ADR-014). An argument shape the ABI does not accept
    /// is rejected before anything is written, so a rejected call has no side
    /// effect. The byte is emitted raw: encoding is the sink receiver's concern,
    /// never the Domain layer's.
    /// </summary>
    private BiosServiceResult InvokePutChar(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, "A0:3C putchar requires one character argument.");
        }

        _outputSink.WriteByte((byte)(identity.Arguments[0] & 0xFFu));
        return BiosServiceResult.Supported(identity, identity.Arguments[0] & 0xFFu);
    }

    /// <summary>
    /// A0:3E puts. Delegates to <see cref="PutsService"/>, which reads the
    /// NUL-terminated guest string through the injected reader, writes it to the
    /// injected sink, and returns the incoming string pointer (ADR-014). The
    /// behavior lives in the service, not here: this registry entry only binds
    /// the identity to it.
    /// </summary>
    private BiosServiceResult InvokePuts(BiosCallIdentity identity) =>
        PutsService.Invoke(identity, _guestMemoryReader, _outputSink);
}
