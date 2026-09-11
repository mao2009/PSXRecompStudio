using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// HLE implementation of the documented BIOS TTY service
/// <c>A(3Eh) or B(3Fh) std_out_puts(src)</c>: read a NUL-terminated string from
/// guest memory, write it to the output sink, and return the incoming string
/// pointer (<c>docs/REFERENCES.md</c>, ADR-014).
/// </summary>
/// <remarks>
/// The service is deliberately a pure function over its injected boundaries —
/// it never reaches host I/O directly and never branches on title identity.
/// <see cref="BiosHleRuntime"/> registers it under both documented identities,
/// A0:3E and its B0:3F alias, and dispatches both to this one implementation;
/// keeping the behavior here rather than in the registry is what lets it be
/// tested directly, without constructing a runtime. Diagnostics name the
/// identity they were invoked with rather than a hard-coded one, so a failure
/// reports the jump table the guest actually called.
/// </remarks>
[Domain]
public static class PutsService
{
    /// <summary>
    /// Hard bound on the number of guest bytes scanned for the NUL terminator,
    /// so no guest pointer can trigger an unbounded scan.
    /// </summary>
    // ponytail: bounded TTY-line ceiling — raise if a real title's puts calls
    // need a longer string, no evidence for that yet.
    internal const int MaxStringLength = 4096;

    /// <summary>
    /// Invokes <c>puts</c> for the given call identity.
    /// </summary>
    /// <param name="identity">
    /// The BIOS call identity; its single ABI argument is the guest virtual
    /// address of the NUL-terminated string.
    /// </param>
    /// <param name="reader">Guest-memory read boundary the string is scanned through.</param>
    /// <param name="outputSink">Output boundary the string bytes are written to.</param>
    /// <returns>
    /// <see cref="BiosServiceResult.Supported(BiosCallIdentity, uint?)"/> carrying the
    /// incoming string pointer once the whole string was written;
    /// <c>BIOS_HLE_INVALID_ARGUMENTS</c> when the argument count is not one; or
    /// <c>BIOS_HLE_UNSUPPORTED_STATE</c> when the string is unreadable or has no
    /// terminator within <see cref="MaxStringLength"/>.
    /// </returns>
    /// <remarks>
    /// The write is atomic with respect to the sink: the string is collected into
    /// a bounded temporary buffer first, and nothing is written until the complete
    /// string has been proven readable. A failure therefore emits zero bytes —
    /// partial TTY output would be exactly the "success while skipping the effect
    /// a caller depends on" outcome ADR-014 rejects. Bytes are forwarded raw, with
    /// no encoding or Unicode conversion, per the sink's byte-level contract.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static BiosServiceResult Invoke(
        BiosCallIdentity identity,
        IGuestMemoryReader reader,
        IRuntimeOutputSink outputSink)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(outputSink);

        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} puts requires one string-pointer argument.");
        }

        var address = identity.Arguments[0];

        // Guest pointers are 32-bit and the scan bound is added to them, so the
        // wraparound is rejected before any read rather than discovered mid-scan.
        if (address + (uint)MaxStringLength < address)
        {
            return BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} puts: guest string address is invalid or unmapped.");
        }

        var buffer = new byte[MaxStringLength];
        var length = 0;

        while (length < MaxStringLength)
        {
            if (!reader.TryReadByte(address + (uint)length, out var value))
            {
                return BiosServiceResult.UnsupportedState(
                    identity, $"{identity.StableKey} puts: guest string address is invalid or unmapped.");
            }

            if (value == 0)
            {
                for (var i = 0; i < length; i++)
                {
                    outputSink.WriteByte(buffer[i]);
                }

                return BiosServiceResult.Supported(identity, address);
            }

            buffer[length] = value;
            length++;
        }

        return BiosServiceResult.UnsupportedState(
            identity,
            $"{identity.StableKey} puts: string exceeds the bounded scan length without a NUL terminator.");
    }
}
