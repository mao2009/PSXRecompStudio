using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:19 HookEntryInt(addr) — PSX-SPX kernelbios/interrupt-exception-handling. Issue #650.
[Test]
public sealed class BiosExceptionHookTests
{
    private const uint BufferA = 0x00000100 + 0x1000;
    private const uint BufferB = 0x00000100 + 0x2000;

    private static uint[] Registers(uint seed)
    {
        var gpr = new uint[32];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = seed + (uint)i * 0x11u;
        }

        return gpr;
    }

    private static BiosHleRuntime CreateRuntime(RecompilerGuestMemory ram) =>
        new(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

    private static BiosServiceResult HookEntryInt(BiosHleRuntime runtime, uint address) =>
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [address]));

    private static void WriteBuffer(RecompilerGuestMemory ram, uint address, uint[] gpr) =>
        new GuestMemoryWriter(ram.Write8).TryWrite(address, JmpBufLayoutForTests(gpr)).Should().BeTrue();

    // Independent of JmpBufLayout: spells out the documented offsets.
    private static byte[] JmpBufLayoutForTests(uint[] gpr)
    {
        var order = new[] { 31, 29, 30, 16, 17, 18, 19, 20, 21, 22, 23, 28 };
        return order.SelectMany(r => BitConverter.GetBytes(gpr[r])).ToArray();
    }

    [Fact]
    public void HookEntryInt_Is_Registered_At_B0_19_With_One_Argument_And_Neighbours_Stay_Unsupported()
    {
        BiosHleRuntime.HookEntryIntFunction.Should().Be(0x19);
        IBiosRuntime runtime = CreateRuntime(new RecompilerGuestMemory());

        runtime.TryGetServiceArgumentCount(BiosCallFamily.B0, 0x19, out var count).Should().BeTrue();
        count.Should().Be(1);

        // B0:17 (ReturnFromException) now occupies the neighbour below the hook, so only the other
        // two neighbours may still decline the call.
        foreach (byte neighbour in new byte[] { 0x18, 0x1A })
        {
            runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, neighbour, arguments: [BufferA]))
                .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        }
    }

    [Fact]
    public void HookEntryInt_Registers_The_Address_Without_Producing_A_Return_Value()
    {
        var ram = new RecompilerGuestMemory();

        var result = HookEntryInt(CreateRuntime(ram), BufferA);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().BeNull("B0:19 has no documented return value, so $v0 stays untouched");
        var stored = new byte[4];
        new GuestMemoryReader(ram.Read8).TryRead(BiosExceptionHook.PointerAddress, stored).Should().BeTrue();
        BitConverter.ToUInt32(stored).Should().Be(BufferA);
    }

    [Fact]
    public void A_Second_Registration_Replaces_The_Current_Hook()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var first = Registers(0x1000);
        var second = Registers(0x9000);
        WriteBuffer(ram, BufferA, first);
        WriteBuffer(ram, BufferB, second);

        HookEntryInt(runtime, BufferA);
        HookEntryInt(runtime, BufferB);

        var gpr = Registers(0x5000);
        BiosExceptionHook.TryComplete(new GuestMemoryReader(ram.Read8), gpr, out var pc)
            .Should().Be(BiosExceptionHookStatus.Resolved);
        gpr[(int)R3000aRegister.Sp].Should().Be(second[29]);
        pc.Should().Be(second[31]);
    }

    // The buffer is registered by pointer: a change made after registration is
    // what the hook restores.
    [Fact]
    public void The_Hook_Observes_Buffer_Changes_Made_After_Registration()
    {
        var ram = new RecompilerGuestMemory();
        WriteBuffer(ram, BufferA, Registers(0x1000));
        HookEntryInt(CreateRuntime(ram), BufferA);

        var updated = Registers(0x7000);
        WriteBuffer(ram, BufferA, updated);

        var gpr = Registers(0x5000);
        BiosExceptionHook.TryComplete(new GuestMemoryReader(ram.Read8), gpr, out var pc)
            .Should().Be(BiosExceptionHookStatus.Resolved);
        gpr[(int)R3000aRegister.S3].Should().Be(updated[19]);
        pc.Should().Be(updated[31]);
    }

    [Fact]
    public void Firing_Restores_Only_The_Saved_Registers_And_Sets_V0_To_One()
    {
        var ram = new RecompilerGuestMemory();
        var saved = Registers(0x1000);
        WriteBuffer(ram, BufferA, saved);
        HookEntryInt(CreateRuntime(ram), BufferA);

        var live = Registers(0x5000);
        var gpr = (uint[])live.Clone();

        var status = BiosExceptionHook.TryComplete(new GuestMemoryReader(ram.Read8), gpr, out var pc);

        status.Should().Be(BiosExceptionHookStatus.Resolved);
        var restored = new HashSet<int> { 31, 29, 30, 16, 17, 18, 19, 20, 21, 22, 23, 28 };
        for (var r = 0; r < 32; r++)
        {
            if (r == (int)R3000aRegister.V0)
            {
                gpr[r].Should().Be(1u, "the hook is entered with $v0 = 1");
            }
            else if (restored.Contains(r))
            {
                gpr[r].Should().Be(saved[r], $"r{r} is part of the saved state");
            }
            else
            {
                gpr[r].Should().Be(live[r], $"r{r} is not part of the saved state");
            }
        }

        pc.Should().Be(saved[31], "control continues at the saved $ra");
    }

    [Fact]
    public void Firing_Without_A_Registration_Changes_Nothing()
    {
        var gpr = Registers(0x5000);

        BiosExceptionHook.TryComplete(new GuestMemoryReader(new RecompilerGuestMemory().Read8), gpr, out var pc)
            .Should().Be(BiosExceptionHookStatus.NotRegistered);

        gpr.Should().Equal(Registers(0x5000));
        pc.Should().Be(0u);
    }

    [Theory]
    [InlineData(0xC0000000u)]          // KSEG2, untranslatable
    [InlineData(0x00800000u)]          // past the RAM window
    [InlineData(0x00800000u - 0x10u)]  // buffer straddles the window end
    [InlineData(0xFFFFFFF0u)]          // 32-bit wraparound
    public void Firing_With_An_Unreadable_Buffer_Fails_Closed_Without_Partial_Restore(uint address)
    {
        var ram = new RecompilerGuestMemory();
        HookEntryInt(CreateRuntime(ram), address).Status.Should().Be(BiosServiceStatus.Supported);

        var gpr = Registers(0x5000);
        var status = BiosExceptionHook.TryComplete(new GuestMemoryReader(ram.Read8), gpr, out var pc);

        status.Should().Be(BiosExceptionHookStatus.InvalidState);
        gpr.Should().Equal(Registers(0x5000), "no register may change when the buffer cannot be read in full");
        pc.Should().Be(0u);
    }

    // Some engines rebuild the Runtime every segment; the registration lives in
    // guest RAM, so a new Runtime over the same memory still fires it.
    [Fact]
    public void The_Registration_Survives_Rebuilding_The_Runtime_Over_The_Same_Memory()
    {
        var ram = new RecompilerGuestMemory();
        WriteBuffer(ram, BufferA, Registers(0x1000));
        HookEntryInt(CreateRuntime(ram), BufferA);

        _ = CreateRuntime(ram);

        BiosExceptionHook.TryComplete(new GuestMemoryReader(ram.Read8), Registers(0x5000), out _)
            .Should().Be(BiosExceptionHookStatus.Resolved);
    }

    [Fact]
    public void Dispatch_Applies_The_Callers_Ra_Once_And_Leaves_V0_Alone()
    {
        var ram = new RecompilerGuestMemory();
        var gpr = Registers(0x5000);
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.HookEntryIntFunction;
        gpr[(int)R3000aRegister.A0] = BufferA;
        gpr[(int)R3000aRegister.Ra] = 0x80012345;

        var outcome = BiosVectorDispatch.Dispatch(CreateRuntime(ram), BiosCallFamily.B0, gpr);

        outcome.ContinueExecution.Should().BeTrue();
        outcome.NextPc.Should().Be(0x80012345u);
        outcome.ReturnValue.Should().BeNull();
        outcome.IsPatchedTarget.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void HookEntryInt_Rejects_A_Wrong_Argument_Count(int argumentCount)
    {
        var result = CreateRuntime(new RecompilerGuestMemory()).Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: new uint[argumentCount]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
    }

    [Fact]
    public void HookEntryInt_With_An_Unwritable_Pointer_Slot_Fails_Closed()
    {
        var runtime = new BiosHleRuntime(
            new CapturedOutputSink(),
            new GuestMemoryReader(new RecompilerGuestMemory().Read8),
            new RejectingWriter());

        HookEntryInt(runtime, BufferA).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    private sealed class RejectingWriter : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => false;

        public bool TryWrite(uint address, ReadOnlySpan<byte> buffer) => false;
    }
}
