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
    private readonly IGuestMemoryWriter _guestMemoryWriter;

    /// <summary>
    /// Creates the registry over the three Runtime boundaries its services need:
    /// the output boundary every TTY-class service writes through, the
    /// guest-memory read boundary the string-reading services scan through, and
    /// the guest-memory write boundary used here to seed every registered
    /// service's own jump-table slot with a real value (see below). A service
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
    /// <param name="guestMemoryWriter">
    /// Writes each registered service's own jump-table slot with its HLE sentinel
    /// (<see cref="BiosJumpTables.HleSentinelTarget"/>) so a guest read of an
    /// *existing* entry observes real, non-zero content rather than 0 — the
    /// read/save/patch/restore pattern psx-spx documents for GetB0Table/GetC0Table
    /// (ADR-014's amendment for #360's Blocker 1 fix). Required for the same
    /// reason as the sink and reader: a missing writer would leave every
    /// registered slot's guest-visible content silently unmodeled.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="outputSink"/>, <paramref name="guestMemoryReader"/>, or
    /// <paramref name="guestMemoryWriter"/> is null.
    /// </exception>
    public BiosHleRuntime(
        IRuntimeOutputSink outputSink,
        IGuestMemoryReader guestMemoryReader,
        IGuestMemoryWriter guestMemoryWriter)
    {
        ArgumentNullException.ThrowIfNull(outputSink);
        ArgumentNullException.ThrowIfNull(guestMemoryReader);
        ArgumentNullException.ThrowIfNull(guestMemoryWriter);
        _outputSink = outputSink;
        _guestMemoryReader = guestMemoryReader;
        _guestMemoryWriter = guestMemoryWriter;

        services = new Dictionary<(BiosCallFamily, byte), Func<BiosCallIdentity, BiosServiceResult>>
        {
            [(BiosCallFamily.A0, PutCharFunction)] = InvokePutChar,
            [(BiosCallFamily.A0, PutsFunction)] = InvokePuts,
            [(BiosCallFamily.B0, PutsAliasFunction)] = InvokePuts,
            [(BiosCallFamily.B0, GetC0TableFunction)] = InvokeGetC0Table,
            [(BiosCallFamily.B0, GetB0TableFunction)] = InvokeGetB0Table,
        };

        // Seed every registered slot's own guest-visible table entry with its HLE
        // sentinel, so a guest that reads (and later saves/restores) an existing
        // entry sees a real, deterministic, non-zero value instead of 0 — this is
        // the actual production-behavior fix for #360's Blocker 1. Best-effort: a
        // rejected write here only leaves that one slot at its pre-existing
        // all-zero default (Invoke still dispatches it through the registry
        // exactly as before), never a hard construction failure, because a
        // memory-bound rejection at these fixed, always-in-range low addresses
        // would indicate a degenerate memory implementation, not guest state.
        foreach (var (family, function) in services.Keys)
        {
            var sentinel = BiosJumpTables.HleSentinelTarget(family, function);
            _guestMemoryWriter.TryWrite(BiosJumpTables.EntryAddress(family, function), BitConverter.GetBytes(sentinel));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Dispatch now consults guest-visible jump-table state before falling back
    /// to the registry. If the table entry for this call's
    /// <c>(Family, FunctionNumber)</c> holds a non-zero 32-bit value that is
    /// <em>not</em> this Runtime's own HLE sentinel for that exact slot
    /// (<see cref="BiosJumpTables.HleSentinelTarget"/>), the entry has been
    /// patched by guest code and <see cref="BiosServiceResult.PatchedTarget"/>
    /// is returned — regardless of whether a host-side handler is also registered
    /// for that slot. The raw patched address is carried in
    /// <see cref="BiosServiceResult.ReturnValue"/> for the caller to inspect;
    /// this Runtime does not execute or validate it (ADR-014 amendment for #360).
    /// A slot with no registered service is zero-initialised by construction and
    /// stays that way, falling through to the registry exactly as before. A
    /// registered service's own slot is seeded with its sentinel at construction
    /// (see the constructor), so reading it, saving the read value, patching it,
    /// and later restoring the exact saved value all round-trip correctly: the
    /// restored sentinel is recognised again and dispatch resumes reaching the
    /// registered service (#360's Blocker 1 fix).
    /// </remarks>
    public BiosServiceResult Invoke(BiosCallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // A0's table has a primary-source-confirmed size (192 entries, 0x00-0xBF).
        // Function numbers >= 0xC0 are out of the A0 table; probing RAM there would
        // read unrelated content and could falsely report PatchedTarget. Skip the
        // patch-check for those and fall through directly to the registry (which
        // correctly returns Unsupported for anything unregistered).
        // B0/C0 need no such guard: primary source documents their full byte domain
        // (0x00-0xFF) already — see BiosJumpTables' remarks.
        var inTableRange = identity.Family != BiosCallFamily.A0 ||
                           identity.FunctionNumber <= BiosJumpTables.A0MaxFunctionNumber;

        if (inTableRange)
        {
            var entryAddress = BiosJumpTables.EntryAddress(identity.Family, identity.FunctionNumber);
            Span<byte> entryBytes = stackalloc byte[4];

            if (_guestMemoryReader.TryRead(entryAddress, entryBytes))
            {
                var entryValue = (uint)(entryBytes[0] | (entryBytes[1] << 8) | (entryBytes[2] << 16) | (entryBytes[3] << 24));
                var isOwnHleSentinel = entryValue == BiosJumpTables.HleSentinelTarget(identity.Family, identity.FunctionNumber);
                if (entryValue != 0 && !isOwnHleSentinel)
                {
                    return BiosServiceResult.PatchedTarget(identity, entryValue);
                }
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
