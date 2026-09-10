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
    private readonly IRuntimeOutputSink _outputSink;

    /// <summary>
    /// Creates the registry over the Runtime output boundary every TTY-class
    /// service writes through. A service is registered as <c>Supported</c> only
    /// when its full documented behavior — including host-visible output — is
    /// implemented (ADR-014); a service whose documented effect depends on
    /// guest memory or output the Runtime does not honour (e.g. A0:3E puts) is
    /// deliberately left unregistered rather than registered with only its
    /// return value modeled.
    /// </summary>
    /// <param name="outputSink">
    /// Receives the bytes registered services emit, such as A0:3C putchar's TTY
    /// character. Required: a missing sink is a construction error, never a
    /// silent no-op, so a host can never run the registry with putchar's
    /// documented side effect quietly discarded.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="outputSink"/> is null.</exception>
    public BiosHleRuntime(IRuntimeOutputSink outputSink)
    {
        ArgumentNullException.ThrowIfNull(outputSink);
        _outputSink = outputSink;

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
}
