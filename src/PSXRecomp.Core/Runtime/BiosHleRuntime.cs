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

    /// <summary>
    /// B0:3F puts, the B0-table alias of the same service — a distinct function
    /// number, not A0's, selected by real-ROM evidence (ADR-014 amendment
    /// "B0:3F selected by real-ROM evidence").
    /// </summary>
    public const byte PutsAliasFunction = 0x3F;

    /// <summary>B0:56 GetC0Table — returns <see cref="BiosJumpTables.C0TableAddress"/>.</summary>
    public const byte GetC0TableFunction = 0x56;

    /// <summary>B0:57 GetB0Table — returns <see cref="BiosJumpTables.B0TableAddress"/>.</summary>
    public const byte GetB0TableFunction = 0x57;

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
    /// character and puts's string. Required: a missing sink is a
    /// construction error, never a silent no-op, so a host can never run the
    /// registry with a documented side effect quietly discarded.
    /// </param>
    /// <param name="guestMemoryReader">
    /// Reads the guest bytes pointer-taking services dereference, such as puts's
    /// string. Required for the same reason as the sink: a missing reader
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
            [(BiosCallFamily.B0, PutsAliasFunction)] = InvokePuts,
            [(BiosCallFamily.B0, GetC0TableFunction)] = InvokeGetC0Table,
            [(BiosCallFamily.B0, GetB0TableFunction)] = InvokeGetB0Table,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Dispatch now consults guest-visible jump-table state before falling back
    /// to the registry. If the table entry for this call's
    /// <c>(Family, FunctionNumber)</c> holds a non-zero 32-bit value, the entry
    /// has been patched by guest code and <see cref="BiosServiceResult.PatchedTarget"/>
    /// is returned — regardless of whether a host-side handler is also registered for
    /// that slot. The raw patched address is carried in
    /// <see cref="BiosServiceResult.ReturnValue"/> for the caller to inspect;
    /// this Runtime does not execute or validate it (ADR-014 amendment for #360).
    /// Guest RAM backing is zero-initialised by construction, so an unpatched slot
    /// reads as zero and falls through to the registry exactly as before.
    /// </remarks>
    public BiosServiceResult Invoke(BiosCallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var entryAddress = BiosJumpTables.EntryAddress(identity.Family, identity.FunctionNumber);
        Span<byte> entryBytes = stackalloc byte[4];

        if (_guestMemoryReader.TryRead(entryAddress, entryBytes))
        {
            var entryValue = (uint)(entryBytes[0] | (entryBytes[1] << 8) | (entryBytes[2] << 16) | (entryBytes[3] << 24));
            if (entryValue != 0)
            {
                return BiosServiceResult.PatchedTarget(identity, entryValue);
            }
        }

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
    /// puts, reached through A0:3E and through its B0:3F alias. Delegates to
    /// <see cref="PutsService"/>, which reads the NUL-terminated guest string
    /// through the injected reader, writes it to the injected sink, and returns
    /// the incoming string pointer (ADR-014). The behavior lives in the service,
    /// not here: both registry entries only bind an identity to it, so the two
    /// tables reach one implementation rather than a per-family copy of it. The
    /// identity the caller used is carried through unchanged, so a diagnostic
    /// names the table that was actually called.
    /// </summary>
    private BiosServiceResult InvokePuts(BiosCallIdentity identity) =>
        PutsService.Invoke(identity, _guestMemoryReader, _outputSink);

    /// <summary>
    /// B0:56 GetC0Table. Returns <see cref="BiosJumpTables.C0TableAddress"/>,
    /// which is this Runtime's own design choice for the C0 jump-table base
    /// address (not a primary-source-confirmed real-hardware fact — see
    /// ADR-014's amendment for #360). The returned address is now backed by
    /// guest-visible RAM connected to dispatch: reads and writes at that address
    /// affect actual dispatch outcomes through <see cref="Invoke"/>.
    /// </summary>
    private BiosServiceResult InvokeGetC0Table(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 0)
        {
            return BiosServiceResult.InvalidArguments(identity, "B0:56 GetC0Table takes no arguments.");
        }

        return BiosServiceResult.Supported(identity, BiosJumpTables.C0TableAddress);
    }

    /// <summary>
    /// B0:57 GetB0Table. Returns <see cref="BiosJumpTables.B0TableAddress"/>,
    /// which is this Runtime's own design choice for the B0 jump-table base
    /// address (not a primary-source-confirmed real-hardware fact — see
    /// ADR-014's amendment for #360). The returned address is now backed by
    /// guest-visible RAM connected to dispatch: reads and writes at that address
    /// affect actual dispatch outcomes through <see cref="Invoke"/>.
    /// </summary>
    private BiosServiceResult InvokeGetB0Table(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 0)
        {
            return BiosServiceResult.InvalidArguments(identity, "B0:57 GetB0Table takes no arguments.");
        }

        return BiosServiceResult.Supported(identity, BiosJumpTables.B0TableAddress);
    }
}
