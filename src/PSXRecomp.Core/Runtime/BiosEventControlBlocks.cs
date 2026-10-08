using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The kernel's Event Control Block (EvCB) table and the event functions the Runtime has measured a guest to need:
/// B0:08 <c>OpenEvent</c>, B0:0C <c>EnableEvent</c> and the matching step of B0:07 <c>DeliverEvent</c> (#687), plus the
/// <c>UnDeliverEvent</c> step the kernel's <c>PadCardIrq</c> card stage performs (#661; B0:20 itself is not a registered service).
/// The table is the only event state; it lives in guest RAM, so every engine and every rebuilt
/// <see cref="BiosHleRuntime"/> sees the same one.
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx control-blocks, event-functions; PCSX-Redux OpenBIOS <c>openbios/kernel/events.c</c> agrees):
/// the table is <c>[0x120]</c> address / <c>[0x124]</c> size; an EvCB is 1Ch bytes (00h class, 04h status, 08h spec,
/// 0Ch mode, 10h callback, 14h unused) with status 0 free / 1000h disabled / 2000h enabled-busy / 4000h
/// enabled-ready. OpenEvent takes the first free slot (no duplicate check), stores class/spec/mode/func, leaves the
/// event disabled and returns <c>F1000000h + slot</c>, or <c>FFFFFFFFh</c> when no slot is free. EnableEvent turns a
/// used slot to 2000h and "always returns 1 (even if the event handle is unused or invalid)". DeliverEvent scans every
/// EvCB; an enabled-busy one whose class and spec match becomes ready when its mode is 2000h, or runs its callback
/// when its mode is 1000h. Closing a slot frees it for reuse.
/// </para>
/// <para>
/// BIOS-less table: a real kernel allocates the table at boot (SYSTEM.CNF <c>EVENT =</c>, default 10h entries, five of
/// them used by the BIOS's own CD-ROM driver). A BIOS-less run has none ([0x120] == [0x124] == 0), so the first OpenEvent
/// seeds one zeroed table of <see cref="SeededSlotCount"/> entries at <see cref="SeededTableAddress"/> (this Runtime's
/// own choice in the kernel-reserved page after the PCB/TCB, as for <see cref="BiosExceptionHandler.SeededTcbAddress"/>;
/// the real location is undocumented). The five internal CD-ROM events are not opened (the Runtime has no CD driver
/// init), so 16 entries are free here. [0x124] holds the size in bytes (OpenBIOS). A table that exists but cannot be
/// used (one pointer 0, size not a multiple of 1Ch, unreadable or unwritable) is guest state and fails closed.
/// </para>
/// <para>
/// INFERRED / UNKNOWN: an invalid mode is stored as given (neither source validates it) and is never matched by
/// delivery; the callback (mode 1000h) is stored only, it is never run by OpenEvent. Delivery to an enabled 1000h event
/// with a callback is not modelled and reports failure (the caller stops the run); a 1000h event without a callback is
/// skipped (OpenBIOS). The handle is checked as <c>F1xxxxxxh</c> with a slot below the table size; OpenBIOS masks with
/// FFFFh and does not check, which would write outside the table, so an invalid handle is a no-op returning 1 as
/// psx-spx documents. TestEvent, DisableEvent, CloseEvent and WaitEvent are not implemented (not measured).
/// </para>
/// <para>
/// Write-failure contract: fail closed means no EvCB table becomes visible or is silently trusted, not that guest RAM is
/// left unmodified. <see cref="IGuestMemoryWriter"/> is Try-style with no transaction, and a rollback write could fail
/// too, so none is attempted. (1) Seeding: the pointer pair <c>[0x120]/[0x124]</c> is the publication boundary; if the
/// write fails after the seed area was zero-filled but before the pointers are published, the unreferenced seed bytes may
/// remain modified and the table stays absent. (2) <see cref="Deliver"/> marks matching ready-mode events one write at a
/// time after validating the whole table; if a later write fails, earlier events stay ready and it returns false.
/// </para>
/// </remarks>
[Domain]
public static class BiosEventControlBlocks
{
    /// <summary>Guest address of the EvCB table address word (psx-spx "table of tables").</summary>
    public const uint TableAddressPointer = 0x00000120;

    /// <summary>Guest address of the EvCB table size word, in bytes.</summary>
    public const uint TableSizePointer = 0x00000124;

    /// <summary>Where a BIOS-less run's table is seeded (Runtime's own choice, after the PCB at E000h and 4 TCBs at E100h..E3FFh).</summary>
    public const uint SeededTableAddress = 0x0000E400;

    /// <summary>Entries in a seeded table (the BIOS default <c>EVENT = 10h</c>).</summary>
    public const int SeededSlotCount = 16;

    /// <summary>Size of one EvCB in bytes.</summary>
    public const int EventControlBlockSize = 0x1C;

    /// <summary>EvCB status: free.</summary>
    public const uint StatusFree = 0;

    /// <summary>EvCB status: opened, disabled.</summary>
    public const uint StatusDisabled = 0x1000;

    /// <summary>EvCB status: enabled, busy.</summary>
    public const uint StatusEnabled = 0x2000;

    /// <summary>EvCB status: enabled, ready.</summary>
    public const uint StatusReady = 0x4000;

    /// <summary>Mode: execute the callback and stay busy.</summary>
    public const uint ModeCallback = 0x1000;

    /// <summary>Mode: no callback, mark ready.</summary>
    public const uint ModeReady = 0x2000;

    /// <summary>The handle of slot <c>n</c> is <c>HandleBase + n</c>.</summary>
    public const uint HandleBase = 0xF1000000;

    /// <summary>OpenEvent's documented failure value (no free slot).</summary>
    public const uint FailedHandle = 0xFFFFFFFF;

    private const int ClassOffset = 0x00;
    private const int StatusOffset = 0x04;
    private const int SpecOffset = 0x08;
    private const int ModeOffset = 0x0C;
    private const int FunctionOffset = 0x10;

    /// <summary>B0:08 handler.</summary>
    internal static BiosServiceResult OpenEvent(BiosCallIdentity identity, IGuestMemoryReader reader, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Arguments.Count != 4)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} OpenEvent requires four arguments: class, spec, mode, func.");
        }

        var table = Locate(reader);
        if (table.State == TableState.Absent)
        {
            if (!Seed(writer))
            {
                return BiosServiceResult.UnsupportedState(
                    identity, $"{identity.StableKey} OpenEvent: the EvCB table at 0x{SeededTableAddress:X8} cannot be seeded (guest RAM is not writable).");
            }

            table = new Table(TableState.Present, SeededTableAddress, SeededSlotCount);
        }

        if (table.State == TableState.Invalid)
        {
            return InvalidTable(identity);
        }

        for (var slot = 0; slot < table.Slots; slot++)
        {
            if (!TryReadWord(reader, SlotAddress(table, slot) + StatusOffset, out var status))
            {
                return InvalidTable(identity);
            }

            if (status != StatusFree)
            {
                continue;
            }

            var record = new byte[FunctionOffset + sizeof(uint)];
            BitConverter.TryWriteBytes(record.AsSpan(ClassOffset, 4), identity.Arguments[0]);
            BitConverter.TryWriteBytes(record.AsSpan(StatusOffset, 4), StatusDisabled);
            BitConverter.TryWriteBytes(record.AsSpan(SpecOffset, 4), identity.Arguments[1]);
            BitConverter.TryWriteBytes(record.AsSpan(ModeOffset, 4), identity.Arguments[2]);
            BitConverter.TryWriteBytes(record.AsSpan(FunctionOffset, 4), identity.Arguments[3]);
            return writer.TryWrite(SlotAddress(table, slot), record)
                ? BiosServiceResult.Supported(identity, HandleBase + (uint)slot)
                : InvalidTable(identity);
        }

        return BiosServiceResult.Supported(identity, FailedHandle);
    }

    /// <summary>B0:0C handler: always returns 1; a used slot becomes enabled.</summary>
    internal static BiosServiceResult EnableEvent(BiosCallIdentity identity, IGuestMemoryReader reader, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(identity, $"{identity.StableKey} EnableEvent requires one argument: event.");
        }

        var table = Locate(reader);
        if (table.State == TableState.Invalid)
        {
            return InvalidTable(identity);
        }

        var handle = identity.Arguments[0];
        if (table.State == TableState.Present && (handle & 0xFF000000) == HandleBase && (handle & 0x00FFFFFF) < table.Slots)
        {
            var statusAddress = SlotAddress(table, (int)(handle & 0x00FFFFFF)) + StatusOffset;
            if (!TryReadWord(reader, statusAddress, out var status))
            {
                return InvalidTable(identity);
            }

            if (status != StatusFree && !writer.TryWrite(statusAddress, BitConverter.GetBytes(StatusEnabled)))
            {
                return InvalidTable(identity);
            }
        }

        return BiosServiceResult.Supported(identity, 1);
    }

    /// <summary>
    /// The matching step of B0:07 <c>DeliverEvent(class, spec)</c> as far as the Runtime performs it. No table: nothing can match,
    /// true. Otherwise every enabled-busy EvCB matching class and spec with mode 2000h becomes ready. False (nothing written) when
    /// the table is unusable or a match is a mode 1000h event with a callback, which is not modelled (decided before any write).
    /// Not atomic once writing starts: if a status write fails, the events written before it stay ready and the result is
    /// false (see the type remarks).
    /// </summary>
    public static bool Deliver(IGuestMemoryReader reader, IGuestMemoryWriter writer, uint eventClass, uint spec)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var table = Locate(reader);
        if (table.State != TableState.Present)
        {
            return table.State == TableState.Absent;
        }

        var ready = new List<uint>();
        var record = new byte[EventControlBlockSize];
        for (var slot = 0; slot < table.Slots; slot++)
        {
            var address = SlotAddress(table, slot);
            if (!reader.TryRead(address, record))
            {
                return false;
            }

            if (BitConverter.ToUInt32(record, StatusOffset) != StatusEnabled ||
                BitConverter.ToUInt32(record, ClassOffset) != eventClass ||
                BitConverter.ToUInt32(record, SpecOffset) != spec)
            {
                continue;
            }

            var mode = BitConverter.ToUInt32(record, ModeOffset);
            if (mode == ModeReady)
            {
                ready.Add(address + StatusOffset);
            }
            else if (mode == ModeCallback && BitConverter.ToUInt32(record, FunctionOffset) != 0)
            {
                return false;
            }
        }

        foreach (var statusAddress in ready)
        {
            if (!writer.TryWrite(statusAddress, BitConverter.GetBytes(StatusReady)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the EvCB table is absent or fully readable for a subsequent Undeliver pass.</summary>
    /// <remarks>This does not guarantee that subsequent writes will succeed; there is no transaction boundary.</remarks>
    internal static bool CanReadUndeliverTable(IGuestMemoryReader reader) => Locate(reader).State != TableState.Invalid;

    /// <summary>
    /// B0:20 <c>UnDeliverEvent(class, spec)</c> as the kernel's own card driver calls it (OpenBIOS <c>undeliverEvent</c>; psx-spx
    /// event-functions): every ready (4000h) EvCB matching class and spec with mode 2000h goes back to enabled-busy (2000h).
    /// No table: nothing can match, true. False when the table is unusable. Not atomic once writing starts (as <see cref="Deliver"/>).
    /// </summary>
    public static bool Undeliver(IGuestMemoryReader reader, IGuestMemoryWriter writer, uint eventClass, uint spec)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var table = Locate(reader);
        if (table.State != TableState.Present)
        {
            return table.State == TableState.Absent;
        }

        var record = new byte[EventControlBlockSize];
        for (var slot = 0; slot < table.Slots; slot++)
        {
            var address = SlotAddress(table, slot);
            if (!reader.TryRead(address, record))
            {
                return false;
            }

            if (BitConverter.ToUInt32(record, StatusOffset) == StatusReady &&
                BitConverter.ToUInt32(record, ClassOffset) == eventClass &&
                BitConverter.ToUInt32(record, SpecOffset) == spec &&
                BitConverter.ToUInt32(record, ModeOffset) == ModeReady &&
                !writer.TryWrite(address + StatusOffset, BitConverter.GetBytes(StatusEnabled)))
            {
                return false;
            }
        }

        return true;
    }

    private enum TableState
    {
        Absent,
        Present,
        Invalid,
    }

    private readonly record struct Table(TableState State, uint Address, int Slots);

    private static Table Locate(IGuestMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!TryReadWord(reader, TableAddressPointer, out var address) || !TryReadWord(reader, TableSizePointer, out var size))
        {
            return new Table(TableState.Invalid, 0, 0);
        }

        if (address == 0 && size == 0)
        {
            return new Table(TableState.Absent, 0, 0);
        }

        var valid = address != 0 && size != 0 && size % EventControlBlockSize == 0 && size <= 0x10000 &&
                    reader.TryRead(address, new byte[size]);
        return valid ? new Table(TableState.Present, address, (int)(size / EventControlBlockSize)) : new Table(TableState.Invalid, 0, 0);
    }

    // The pointer pair is the publication boundary: a failure after the zero-fill leaves orphan seed bytes, no visible table.
    private static bool Seed(IGuestMemoryWriter writer)
    {
        var size = SeededSlotCount * EventControlBlockSize;
        var pointers = new byte[8];
        BitConverter.TryWriteBytes(pointers.AsSpan(0, 4), SeededTableAddress);
        BitConverter.TryWriteBytes(pointers.AsSpan(4, 4), (uint)size);
        return writer.TryWrite(SeededTableAddress, new byte[size]) && writer.TryWrite(TableAddressPointer, pointers);
    }

    private static uint SlotAddress(Table table, int slot) => table.Address + (uint)(slot * EventControlBlockSize);

    private static bool TryReadWord(IGuestMemoryReader reader, uint address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        var ok = reader.TryRead(address, bytes);
        value = ok ? BitConverter.ToUInt32(bytes) : 0;
        return ok;
    }

    private static BiosServiceResult InvalidTable(BiosCallIdentity identity) =>
        BiosServiceResult.UnsupportedState(
            identity,
            $"{identity.StableKey}: the EvCB table at [0x{TableAddressPointer:X}]/[0x{TableSizePointer:X}] is unusable " +
            "(one pointer is 0, the size is not a multiple of 1Ch, or the table cannot be read or written).");
}
