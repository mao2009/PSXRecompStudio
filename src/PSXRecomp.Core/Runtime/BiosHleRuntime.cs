using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Minimal BIOS-less Runtime dispatcher. The registry is deliberately generic:
/// service implementations are selected by A0/B0/C0 identity, never by title,
/// address range, or generated-code provenance.
/// </summary>
[Domain]
public sealed class BiosHleRuntime : IDeviceBiosRuntime
{
    /// <summary>
    /// A0:39 InitHeap(addr,size) — the identity real-ROM analysis observed most
    /// broadly (5 of 5 locally available executables, docs/runtime/bios-hle-evidence.md
    /// §3.4). Its documented effect (ADR-014 amendment "A0:39 InitHeap registered")
    /// is guest-kernel heap bookkeeping this Runtime has no consumer for — no
    /// malloc/free-family service is registered — so nothing about its ABI/return
    /// contract is skipped by modeling only argument-shape validation.
    /// </summary>
    public const byte InitHeapFunction = 0x39;

    /// <summary>A0:13 setjmp(buf) — saves the ABI-saved registers into a 0x30-byte guest buffer.</summary>
    public const byte SetJmpFunction = 0x13;

    /// <summary>B0:19 HookEntryInt(addr) — registers a guest register-state buffer as the exception-completion hook.</summary>
    public const byte HookEntryIntFunction = 0x19;

    /// <summary>
    /// B0:17 ReturnFromException() — not a call the guest returns from but the
    /// kernel's exception-return operation: restore the interrupted context from the
    /// current TCB, RFE, and continue at the saved EPC (Issue #664).
    /// </summary>
    public const byte ReturnFromExceptionFunction = 0x17;

    /// <summary>B0:5B ChangeClearPAD(int) — configures the Pad/Card IRQ handler's IRQ0 auto-ack policy.</summary>
    public const byte ChangeClearPadFunction = 0x5B;

    /// <summary>B0:08 OpenEvent(class,spec,mode,func): takes a free EvCB slot and returns its handle.</summary>
    public const byte OpenEventFunction = 0x08;

    /// <summary>B0:0C EnableEvent(event): enables an opened EvCB; always returns 1.</summary>
    public const byte EnableEventFunction = 0x0C;

    /// <summary>C0:0A ChangeClearRCnt(t,flag) — selects the timer/vblank IRQ handlers' post-IRQ behavior; returns the old flag.</summary>
    public const byte ChangeClearRCntFunction = 0x0A;

    /// <summary>A0:72 CdRemove() (PSY-Q <c>_96_remove</c>) — void, no arguments; no modelled guest-visible effect (see the handler).</summary>
    public const byte CdRemoveFunction = 0x72;

    /// <summary>B0:15 OutdatedPadInitAndStart(type, button_dest, unused, unused) — see <see cref="BiosPadState"/>.</summary>
    public const byte OutdatedPadInitAndStartFunction = 0x15;

    /// <summary>A0:49 GPU_cw(cmd): waits for the GPU, then writes one word to GP0 (see <see cref="BiosGpuCommandService"/>).</summary>
    public const byte GpuCommandWordFunction = 0x49;

    /// <summary>A0:3C putchar, the first deterministic service in this vertical slice.</summary>
    public const byte PutCharFunction = 0x3C;

    /// <summary>B0:3D putchar, the verified B0-table alias of A0:3C.</summary>
    public const byte PutCharAliasFunction = 0x3D;

    /// <summary>A0:3E puts, the first service that reads guest memory.</summary>
    public const byte PutsFunction = 0x3E;

    /// <summary>
    /// B0:3F puts, the B0-table alias of the same service — a distinct function
    /// number, not A0's, selected by real-ROM evidence (ADR-014 amendment
    /// "B0:3F selected by real-ROM evidence").
    /// </summary>
    public const byte PutsAliasFunction = 0x3F;

    /// <summary>A0:3F printf(txt,param1,param2,etc.) — variadic; registered with the one fixed parameter (see <see cref="PrintfService"/>).</summary>
    public const byte PrintfFunction = 0x3F;

    /// <summary>B0:56 GetC0Table — returns <see cref="BiosJumpTables.C0TableAddress"/>.</summary>
    public const byte GetC0TableFunction = 0x56;

    /// <summary>B0:57 GetB0Table — returns <see cref="BiosJumpTables.B0TableAddress"/>.</summary>
    public const byte GetB0TableFunction = 0x57;

    /// <summary>
    /// One registered service: its exact expected argument count (the arity
    /// SSOT <see cref="TryGetServiceArgumentCount"/> exposes to live traps) and
    /// the handler that implements it. Kept together so registration can never
    /// drift out of sync with dispatch — there is exactly one place a service's
    /// arity is declared.
    /// </summary>
    private readonly IReadOnlyDictionary<(BiosCallFamily Family, byte Function), (int ArgumentCount, Func<BiosCallIdentity, BiosServiceResult> Handler)> services;
    private readonly IRuntimeOutputSink _outputSink;
    private readonly IGuestMemoryReader _guestMemoryReader;
    private readonly IGuestMemoryWriter _guestMemoryWriter;
    private IGuestDeviceAccess? _devices;

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
    /// (<see cref="BiosJumpTables.HleSentinelTarget"/>) — but only when that slot
    /// currently reads as zero — so a guest read of an *existing* entry observes
    /// real, non-zero content rather than 0, the read/save/patch/restore pattern
    /// psx-spx documents for GetB0Table/GetC0Table (ADR-014's amendment for
    /// #360's Blocker 1 fix). A slot that already holds a guest patch or restored
    /// save-state content is left untouched: this constructor never overwrites
    /// pre-existing guest-visible state. Required for the same reason as the sink
    /// and reader: a missing writer would leave every registered slot's
    /// guest-visible content silently unmodeled.
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

        services = new Dictionary<(BiosCallFamily, byte), (int ArgumentCount, Func<BiosCallIdentity, BiosServiceResult> Handler)>
        {
            [(BiosCallFamily.A0, SetJmpFunction)] = (1, InvokeSetJmp),
            [(BiosCallFamily.B0, HookEntryIntFunction)] = (1, InvokeHookEntryInt),
            [(BiosCallFamily.B0, ReturnFromExceptionFunction)] = (0, InvokeReturnFromException),
            [(BiosCallFamily.B0, ChangeClearPadFunction)] = (1, InvokeChangeClearPad),
            [(BiosCallFamily.B0, OutdatedPadInitAndStartFunction)] = (4, identity => BiosPadState.OutdatedPadInitAndStart(identity, _guestMemoryWriter)),
            [(BiosCallFamily.B0, OpenEventFunction)] = (4, InvokeOpenEvent),
            [(BiosCallFamily.B0, EnableEventFunction)] = (1, InvokeEnableEvent),
            [(BiosCallFamily.C0, ChangeClearRCntFunction)] = (2, InvokeChangeClearRCnt),
            [(BiosCallFamily.A0, CdRemoveFunction)] = (0, InvokeCdRemove),
            [(BiosCallFamily.A0, InitHeapFunction)] = (2, InvokeInitHeap),
            [(BiosCallFamily.A0, GpuCommandWordFunction)] = (1, identity => BiosGpuCommandService.Invoke(identity, _devices)),
            [(BiosCallFamily.A0, PutCharFunction)] = (1, InvokePutChar),
            [(BiosCallFamily.B0, PutCharAliasFunction)] = (1, InvokePutChar),
            [(BiosCallFamily.A0, PutsFunction)] = (1, InvokePuts),
            [(BiosCallFamily.B0, PutsAliasFunction)] = (1, InvokePuts),
            [(BiosCallFamily.A0, PrintfFunction)] = (1, InvokePrintf),
            [(BiosCallFamily.B0, GetC0TableFunction)] = (0, InvokeGetC0Table),
            [(BiosCallFamily.B0, GetB0TableFunction)] = (0, InvokeGetB0Table),
        };

        // Seed every registered slot's own guest-visible table entry with its HLE
        // sentinel, so a guest that reads (and later saves/restores) an existing
        // entry sees a real, deterministic, non-zero value instead of 0 — this is
        // the actual production-behavior fix for #360's Blocker 1. Zero-only:
        // this Runtime can be constructed over memory that already holds a guest
        // patch or a restored save-state value, and seeding must never clobber
        // that pre-existing state, so each slot is read first and only written
        // when its current value is exactly zero. A read failure is treated the
        // same as a non-zero read — do not seed — so a slot is never partially or
        // speculatively mutated when its actual current content is unknown; the
        // slot then simply keeps whatever it already held (Invoke still
        // dispatches it through the registry exactly as before). A rejected write
        // is likewise best-effort, never a hard construction failure, because a
        // memory-bound rejection at these fixed, always-in-range low addresses
        // would indicate a degenerate memory implementation, not guest state.
        foreach (var (family, function) in services.Keys)
        {
            var entryAddress = BiosJumpTables.EntryAddress(family, function);
            Span<byte> existing = stackalloc byte[4];
            var isZero = _guestMemoryReader.TryRead(entryAddress, existing) &&
                         existing[0] == 0 && existing[1] == 0 && existing[2] == 0 && existing[3] == 0;

            if (isZero)
            {
                var sentinel = BiosJumpTables.HleSentinelTarget(family, function);
                _guestMemoryWriter.TryWrite(entryAddress, BitConverter.GetBytes(sentinel));
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Dispatch now consults guest-visible jump-table state before falling back
    /// to the registry. If the table entry for this call's
    /// <c>(Family, FunctionNumber)</c> holds a non-zero 32-bit value that is
    /// <em>not</em> this Runtime's own HLE sentinel for that exact physical slot
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
    /// registered service (#360's Blocker 1 fix). A C0 high-range call is a
    /// second identity for the very same physical slot as its mirrored B0 call
    /// (<see cref="BiosJumpTables.CanonicalizeIdentity"/>); the registry is
    /// consulted under that canonical identity so the alias reaches the
    /// registered B0 service exactly as the direct B0 call does, while the
    /// service still receives — and the result still reports — the identity the
    /// guest actually called, not the canonical one (#360's Blocker 2 fix).
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

        // Registry lookup uses the canonical physical-slot identity so a C0
        // high-range alias reaches the same registered handler as its mirrored
        // B0 call (#360's Blocker 2 fix). The handler still receives the
        // original, uncanonicalized identity — the guest's own call is never
        // rewritten — matching the identity-preserving pattern the A0:3E/B0:3F
        // puts alias already established.
        var (canonicalFamily, canonicalFunction) =
            BiosJumpTables.CanonicalizeIdentity(identity.Family, identity.FunctionNumber);

        return services.TryGetValue((canonicalFamily, canonicalFunction), out var registration)
            ? registration.Handler(identity)
            : BiosServiceResult.Unsupported(identity);
    }

    /// <inheritdoc />
    public bool TryGetServiceArgumentCount(BiosCallFamily family, byte functionNumber, out int argumentCount)
    {
        var (canonicalFamily, canonicalFunction) = BiosJumpTables.CanonicalizeIdentity(family, functionNumber);
        if (services.TryGetValue((canonicalFamily, canonicalFunction), out var registration))
        {
            argumentCount = registration.ArgumentCount;
            return true;
        }

        argumentCount = 0;
        return false;
    }

    /// <summary>
    /// A0:72 CdRemove() (PSY-Q <c>void _96_remove(void)</c>; psx-spx alias A0:56,
    /// same routine, not registered). Its documented intent is to remove the
    /// kernel's priority-0 CD-ROM IRQ chain elements, but psx-spx marks it
    /// "does NOT work due to SysDeqIntRP bug": SysDeqIntRP only checks the first
    /// chain element properly and then reads an uninitialised stack value. (The
    /// current no$psx text drops that note; the sources disagree.) What the
    /// retail routine actually leaves in the chain is therefore not known, so
    /// this Runtime must not claim the handler was removed. It also models no
    /// priority chain, CD-ROM IRQ dispatch (#444) or kernel event table, so there
    /// is no Runtime state to change: the call is accepted with its documented
    /// arity and void return (<c>$v0</c> untouched, <c>$ra</c> applied by
    /// dispatch) and no effect. OpenBIOS's event closing and critical-section
    /// entry come from a reimplementation and are not modelled either.
    /// A future CD-ROM IRQ path must not assume A0:72 removed anything.
    /// </summary>
    private static BiosServiceResult InvokeCdRemove(BiosCallIdentity identity) =>
        identity.Arguments.Count == 0
            ? BiosServiceResult.Supported(identity)
            : BiosServiceResult.InvalidArguments(identity, $"{identity.StableKey} CdRemove takes no arguments.");

    /// <summary>C0:0A ChangeClearRCnt(t,flag). The behavior lives in <see cref="BiosRootCounterClearPolicy"/>.</summary>
    private BiosServiceResult InvokeChangeClearRCnt(BiosCallIdentity identity) =>
        BiosRootCounterClearPolicy.Change(identity, _guestMemoryReader, _guestMemoryWriter);

    /// <summary>B0:08 OpenEvent. The behavior lives in <see cref="BiosEventControlBlocks"/>.</summary>
    private BiosServiceResult InvokeOpenEvent(BiosCallIdentity identity) =>
        BiosEventControlBlocks.OpenEvent(identity, _guestMemoryReader, _guestMemoryWriter);

    /// <summary>B0:0C EnableEvent. The behavior lives in <see cref="BiosEventControlBlocks"/>.</summary>
    private BiosServiceResult InvokeEnableEvent(BiosCallIdentity identity) =>
        BiosEventControlBlocks.EnableEvent(identity, _guestMemoryReader, _guestMemoryWriter);

    /// <summary>B0:5B ChangeClearPAD(int). The behavior lives in <see cref="BiosPadCardAutoAck"/>.</summary>
    private BiosServiceResult InvokeChangeClearPad(BiosCallIdentity identity) =>
        BiosPadCardAutoAck.Change(identity, _guestMemoryWriter);

    /// <summary>B0:19 HookEntryInt(addr). The behavior lives in <see cref="BiosExceptionHook"/>.</summary>
    private BiosServiceResult InvokeHookEntryInt(BiosCallIdentity identity) =>
        BiosExceptionHook.Register(identity, _guestMemoryWriter);

    /// <summary>
    /// B0:17 ReturnFromException(). This is the one registered service that takes no
    /// arguments and returns no value, because it is not a call at all: it completes
    /// the exception the CPU took and continues at the EPC the kernel saved. The
    /// restore itself is <see cref="BiosExceptionCompletion.TryReturnFromException"/>
    /// — the same source of truth <see cref="BiosExceptionHandler"/> uses — and this
    /// handler only shapes its result into a CPU-state replacement the caller applies
    /// (Issue #664).
    /// </summary>
    /// <remarks>
    /// The register file is cloned before the restore so the <c>k0</c> the kernel
    /// left in place survives, and so a rejected restore provably leaves the live
    /// register file untouched: nothing is applied until the whole TCB has been read
    /// and the replacement has been built.
    /// </remarks>
    private BiosServiceResult InvokeReturnFromException(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 0)
        {
            return BiosServiceResult.InvalidArguments(identity, $"{identity.StableKey} ReturnFromException takes no arguments.");
        }

        if (identity.GuestRegisters is not { Count: PSXCoreWrapper.GprCount } live)
        {
            return BiosServiceResult.UnsupportedState(
                identity,
                $"{identity.StableKey} ReturnFromException needs the live register file to restore the context onto; " +
                "the caller supplied no registers.");
        }

        var gpr = live.ToArray();
        if (!BiosExceptionCompletion.TryReturnFromException(_guestMemoryReader, gpr, out var hi, out var lo, out var sr, out var pc))
        {
            return BiosServiceResult.UnsupportedState(
                identity,
                $"{identity.StableKey} ReturnFromException could not read the current TCB through " +
                $"[0x{BiosExceptionCompletion.ProcessControlBlockPointerAddress:X8}]->PCB->TCB in full, so no register was restored.");
        }

        return BiosServiceResult.CpuStateReplacement(identity, new BiosCpuStateMutation(gpr, hi, lo, sr, pc));
    }

    /// <inheritdoc />
    public void AttachDevices(IGuestDeviceAccess devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    /// <summary>A0:13 setjmp(buf). The behavior lives in <see cref="SetJmpService"/>.</summary>
    private BiosServiceResult InvokeSetJmp(BiosCallIdentity identity) =>
        SetJmpService.Invoke(identity, _guestMemoryWriter);

    /// <summary>
    /// A0:39 InitHeap(addr, size). Documented behavior (docs/REFERENCES.md): sets
    /// the address and size of the heap used by the malloc/realloc/calloc/free
    /// and qsort family, and deallocates all existing memory handles; the BIOS
    /// never calls it automatically, so software must. Neither argument is a
    /// pointer this service dereferences (both are scalar words, unlike puts's
    /// string pointer), no return value is documented (unlike puts or
    /// GetC0Table/GetB0Table), and no host-visible output is involved. This
    /// Runtime registers no malloc/realloc/calloc/free/qsort service, so no
    /// guest-observable state exists for InitHeap's heap bookkeeping to feed —
    /// validating the ABI's argument shape is therefore this call's complete
    /// documented, guest-observable contract (ADR-014).
    /// </summary>
    private BiosServiceResult InvokeInitHeap(BiosCallIdentity identity)
    {
        if (identity.Arguments.Count != 2)
        {
            return BiosServiceResult.InvalidArguments(
                identity, "A0:39 InitHeap requires two arguments: addr and size.");
        }

        return BiosServiceResult.Supported(identity);
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
                identity, $"{identity.StableKey} putchar requires one character argument.");
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

    /// <summary>A0:3F printf. The behavior lives in <see cref="PrintfService"/>.</summary>
    private BiosServiceResult InvokePrintf(BiosCallIdentity identity) =>
        PrintfService.Invoke(identity, _guestMemoryReader, _outputSink);

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
