using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:08 OpenEvent / B0:0C EnableEvent / EvCB matching — psx-spx event-functions + control-blocks, OpenBIOS events.c. Issue #687.
[Test]
public sealed class BiosEventControlBlocksTests : IDisposable
{
    // The measured Persona call: OpenEvent(F0000009h, 0020h, 2000h, 0).
    private const uint SpuClass = 0xF0000009;
    private const uint CommandCompleted = 0x20;
    private const uint Table = BiosEventControlBlocks.SeededTableAddress;

    private readonly RecompilerGuestMemory _ram = new();

    public void Dispose() => GC.SuppressFinalize(this);

    private GuestMemoryReader Reader => new(_ram.Read8);

    private GuestMemoryWriter Writer => new(_ram.Write8);

    private BiosHleRuntime Runtime() => new(new CapturedOutputSink(), Reader, Writer);

    private BiosServiceResult Open(uint eventClass, uint spec, uint mode, uint function, BiosHleRuntime? runtime = null) =>
        (runtime ?? Runtime()).Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.OpenEventFunction, arguments: [eventClass, spec, mode, function]));

    private BiosServiceResult Enable(uint handle, BiosHleRuntime? runtime = null) =>
        (runtime ?? Runtime()).Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.EnableEventFunction, arguments: [handle]));

    private uint Word(uint address)
    {
        var bytes = new byte[4];
        Reader.TryRead(address, bytes).Should().BeTrue();
        return BitConverter.ToUInt32(bytes);
    }

    private uint Field(int slot, int offset) => Word(Table + (uint)(slot * BiosEventControlBlocks.EventControlBlockSize + offset));

    private void WriteWords(uint address, params uint[] words) =>
        Writer.TryWrite(address, words.SelectMany(BitConverter.GetBytes).ToArray()).Should().BeTrue();

    // ---- registration / arity -------------------------------------------------------------------------

    [Theory]
    [InlineData(BiosHleRuntime.OpenEventFunction, 4)]
    [InlineData(BiosHleRuntime.EnableEventFunction, 1)]
    public void Is_Registered_With_Its_Arity(byte function, int arity)
    {
        Runtime().TryGetServiceArgumentCount(BiosCallFamily.B0, function, out var count).Should().BeTrue();
        count.Should().Be(arity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void OpenEvent_With_Wrong_Arity_Is_Invalid_And_Seeds_Nothing(int count)
    {
        var result = Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.OpenEventFunction, arguments: new uint[count]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        Word(BiosEventControlBlocks.TableAddressPointer).Should().Be(0u);
        Word(BiosEventControlBlocks.TableSizePointer).Should().Be(0u);
    }

    [Fact]
    public void EnableEvent_With_Wrong_Arity_Is_Invalid()
    {
        var result = Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.EnableEventFunction, arguments: [1u, 2u]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
    }

    // ---- first allocation -----------------------------------------------------------------------------

    [Fact]
    public void The_First_OpenEvent_Seeds_The_Table_And_Returns_Slot_Zero_As_F1000000()
    {
        var result = Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0xF1000000u);
        Word(BiosEventControlBlocks.TableAddressPointer).Should().Be(Table);
        Word(BiosEventControlBlocks.TableSizePointer).Should().Be((uint)(BiosEventControlBlocks.SeededSlotCount * BiosEventControlBlocks.EventControlBlockSize));
        Field(0, 0x00).Should().Be(SpuClass);
        Field(0, 0x04).Should().Be(BiosEventControlBlocks.StatusDisabled, "an opened event is initially disabled");
        Field(0, 0x08).Should().Be(CommandCompleted);
        Field(0, 0x0C).Should().Be(BiosEventControlBlocks.ModeReady);
        Field(0, 0x10).Should().Be(0u);
        Field(1, 0x04).Should().Be(BiosEventControlBlocks.StatusFree);
    }

    // ---- multiple allocation / reuse / duplicates -----------------------------------------------------

    [Fact]
    public void Each_OpenEvent_Takes_The_Next_Free_Slot_And_Duplicates_Are_Allowed()
    {
        Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(0xF1000000u);
        Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(0xF1000001u);
        Open(0xF0000003, 0x80, BiosEventControlBlocks.ModeCallback, 0x80010000).ReturnValue.Should().Be(0xF1000002u);

        Field(2, 0x10).Should().Be(0x80010000u, "the callback is stored");
        Field(2, 0x0C).Should().Be(BiosEventControlBlocks.ModeCallback);
    }

    [Fact]
    public void A_Freed_Slot_Is_Reused_First()
    {
        for (var i = 0; i < 3; i++)
        {
            Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0);
        }

        WriteWords(Table + BiosEventControlBlocks.EventControlBlockSize + 4, BiosEventControlBlocks.StatusFree); // CloseEvent(slot 1)

        Open(1, 2, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(0xF1000001u);
    }

    [Fact]
    public void A_Full_Table_Returns_The_Documented_FFFFFFFF_Without_A_Diagnostic()
    {
        for (var i = 0; i < BiosEventControlBlocks.SeededSlotCount; i++)
        {
            Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(0xF1000000u + (uint)i);
        }

        var full = Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0);

        full.Status.Should().Be(BiosServiceStatus.Supported);
        full.ReturnValue.Should().Be(BiosEventControlBlocks.FailedHandle);
        full.Diagnostic.Should().BeNull();
    }

    // ---- state persistence ----------------------------------------------------------------------------

    [Fact]
    public void The_Table_Survives_A_Rebuilt_Runtime()
    {
        Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0);

        Open(0xF0000003, 0x10, BiosEventControlBlocks.ModeReady, 0, Runtime()).ReturnValue.Should().Be(0xF1000001u);
        Enable(0xF1000000, Runtime()).ReturnValue.Should().Be(1u);
        Field(0, 0x04).Should().Be(BiosEventControlBlocks.StatusEnabled);
    }

    [Fact]
    public void A_Guest_Owned_Table_Is_Used_As_Is()
    {
        WriteWords(BiosEventControlBlocks.TableAddressPointer, 0x00008000, 0x2 * 0x1C);

        Open(1, 2, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(0xF1000000u);
        Open(1, 2, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(0xF1000001u);
        Open(1, 2, BiosEventControlBlocks.ModeReady, 0).ReturnValue.Should().Be(BiosEventControlBlocks.FailedHandle);
        Word(0x00008000).Should().Be(1u);
        Word(Table).Should().Be(0u, "the Runtime's seed area is not touched when the guest has a table");
    }

    // ---- no speculative callback / unrelated state ----------------------------------------------------

    [Fact]
    public void OpenEvent_Touches_Only_The_Table_And_Its_Pointers()
    {
        var runtime = Runtime(); // construction seeds the jump-table slots; snapshot after it
        var before = Snapshot();

        Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeCallback, 0x80010000, runtime);

        var after = Snapshot();
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => (uint)i).ToArray();
        changed.Should().OnlyContain(a =>
            (a >= Table && a < Table + BiosEventControlBlocks.EventControlBlockSize) ||
            (a >= BiosEventControlBlocks.TableAddressPointer && a < BiosEventControlBlocks.TableAddressPointer + 8));
    }

    private byte[] Snapshot()
    {
        var bytes = new byte[0x10000];
        Reader.TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    // ---- EnableEvent ----------------------------------------------------------------------------------

    [Fact]
    public void EnableEvent_Turns_An_Opened_Event_To_Enabled_Busy_And_Returns_One()
    {
        var handle = Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0).ReturnValue!.Value;

        Enable(handle).ReturnValue.Should().Be(1u);

        Field(0, 0x04).Should().Be(BiosEventControlBlocks.StatusEnabled);
    }

    [Theory]
    [InlineData(0xF1000005u)]
    [InlineData(0xF1000010u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0u)]
    public void EnableEvent_Of_An_Unused_Or_Invalid_Handle_Returns_One_And_Changes_Nothing(uint handle)
    {
        Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0);
        var before = Snapshot();

        var result = Enable(handle);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(1u);
        Snapshot().Should().Equal(before);
    }

    [Fact]
    public void EnableEvent_Without_Any_Table_Returns_One_And_Seeds_Nothing()
    {
        Enable(0xF1000000).ReturnValue.Should().Be(1u);

        Word(BiosEventControlBlocks.TableAddressPointer).Should().Be(0u);
    }

    // ---- fail closed ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x00008000u, 0u)]
    [InlineData(0u, 0x1C0u)]
    [InlineData(0x00008000u, 0x1Du)]
    public void An_Unusable_Table_Fails_Closed_And_Is_Not_Corrected(uint address, uint size)
    {
        WriteWords(BiosEventControlBlocks.TableAddressPointer, address, size);

        Open(1, 2, BiosEventControlBlocks.ModeReady, 0).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        Enable(0xF1000000).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        Word(BiosEventControlBlocks.TableAddressPointer).Should().Be(address);
        Word(BiosEventControlBlocks.TableSizePointer).Should().Be(size);
    }

    [Fact]
    public void Unreadable_Memory_Fails_Closed_And_Writes_Nothing()
    {
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), new UnreadableReader(), Writer);

        Open(1, 2, BiosEventControlBlocks.ModeReady, 0, runtime).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        Enable(0xF1000000, runtime).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        Word(BiosEventControlBlocks.TableAddressPointer).Should().Be(0u);
        BiosEventControlBlocks.Deliver(new UnreadableReader(), Writer, 1, 2).Should().BeFalse();
    }

    [Fact]
    public void An_Unwritable_Seed_Area_Fails_Closed()
    {
        Open(1, 2, BiosEventControlBlocks.ModeReady, 0, new BiosHleRuntime(new CapturedOutputSink(), Reader, new RejectingWriter()))
            .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    private sealed class UnreadableReader : IGuestMemoryReader
    {
        public bool TryReadByte(uint address, out byte value)
        {
            value = 0;
            return false;
        }

        public bool TryRead(uint address, Span<byte> buffer) => false;
    }

    private sealed class RejectingWriter : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => false;

        public bool TryWrite(uint address, ReadOnlySpan<byte> buffer) => false;
    }

    // ---- DeliverEvent matching ------------------------------------------------------------------------

    private void OpenAndEnable(uint eventClass, uint spec, uint mode, uint function)
    {
        var handle = Open(eventClass, spec, mode, function).ReturnValue!.Value;
        Enable(handle);
    }

    [Fact]
    public void Deliver_Without_A_Table_Is_A_Successful_No_Op()
    {
        BiosEventControlBlocks.Deliver(Reader, Writer, SpuClass, CommandCompleted).Should().BeTrue();
        Word(BiosEventControlBlocks.TableAddressPointer).Should().Be(0u);
    }

    [Fact]
    public void Deliver_Marks_Only_Enabled_Matching_Ready_Mode_Events_Ready()
    {
        OpenAndEnable(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0); // slot 0: matches
        OpenAndEnable(SpuClass, 0x40, BiosEventControlBlocks.ModeReady, 0); // slot 1: other spec
        Open(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0); // slot 2: matches but disabled
        OpenAndEnable(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeCallback, 0); // slot 3: callback mode without a function

        BiosEventControlBlocks.Deliver(Reader, Writer, SpuClass, CommandCompleted).Should().BeTrue();

        Field(0, 0x04).Should().Be(BiosEventControlBlocks.StatusReady);
        Field(1, 0x04).Should().Be(BiosEventControlBlocks.StatusEnabled);
        Field(2, 0x04).Should().Be(BiosEventControlBlocks.StatusDisabled);
        Field(3, 0x04).Should().Be(BiosEventControlBlocks.StatusEnabled, "OpenBIOS skips a 1000h event without a callback");
    }

    [Fact]
    public void Deliver_To_An_Enabled_Callback_Event_Fails_Closed_Without_Writing()
    {
        OpenAndEnable(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeReady, 0);
        OpenAndEnable(SpuClass, CommandCompleted, BiosEventControlBlocks.ModeCallback, 0x80010000);

        BiosEventControlBlocks.Deliver(Reader, Writer, SpuClass, CommandCompleted).Should().BeFalse();

        Field(0, 0x04).Should().Be(BiosEventControlBlocks.StatusEnabled, "a failed delivery writes nothing");
    }
}
