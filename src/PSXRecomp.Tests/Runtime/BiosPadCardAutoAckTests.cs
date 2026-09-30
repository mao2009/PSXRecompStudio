using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:5B ChangeClearPAD(int) — PSX-SPX kernelbios. Issue #652.
// Configuration only: the consumer is BiosPadCardIrqHandler (#654), covered by
// BiosPadCardIrqHandlerTests; these tests do not model any IRQ effect.
[Test]
public sealed class BiosPadCardAutoAckTests
{
    private static BiosHleRuntime CreateRuntime(RecompilerGuestMemory ram) =>
        new(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

    private static BiosServiceResult ChangeClearPad(BiosHleRuntime runtime, uint value) =>
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, arguments: [value]));

    private static BiosPadCardAutoAckSetting Setting(RecompilerGuestMemory ram)
    {
        BiosPadCardAutoAck.TryGetSetting(new GuestMemoryReader(ram.Read8), out var setting).Should().BeTrue();
        return setting;
    }

    private static byte[] Snapshot(RecompilerGuestMemory ram)
    {
        var bytes = new byte[0x1000];
        new GuestMemoryReader(ram.Read8).TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    [Fact]
    public void ChangeClearPad_Is_Registered_At_B0_5B_With_One_Argument_And_Neighbours_Stay_Unsupported()
    {
        BiosHleRuntime.ChangeClearPadFunction.Should().Be(0x5B);
        IBiosRuntime runtime = CreateRuntime(new RecompilerGuestMemory());

        runtime.TryGetServiceArgumentCount(BiosCallFamily.B0, 0x5B, out var count).Should().BeTrue();
        count.Should().Be(1);

        foreach (byte neighbour in new byte[] { 0x5A, 0x5C })
        {
            runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, neighbour, arguments: [1u]))
                .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        }
    }

    [Fact]
    public void The_Setting_Starts_Not_Configured()
    {
        var ram = new RecompilerGuestMemory();
        _ = CreateRuntime(ram);

        Setting(ram).Should().Be(BiosPadCardAutoAckSetting.NotConfigured);
    }

    [Theory]
    [InlineData(0u, BiosPadCardAutoAckSetting.Zero)]
    [InlineData(1u, BiosPadCardAutoAckSetting.NonZero)]
    [InlineData(0xFFFFFFFFu, BiosPadCardAutoAckSetting.NonZero)]
    public void The_Argument_Is_Recorded_As_Given(uint argument, BiosPadCardAutoAckSetting expected)
    {
        var ram = new RecompilerGuestMemory();

        var result = ChangeClearPad(CreateRuntime(ram), argument);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().BeNull("B0:5B has no documented return value");
        Setting(ram).Should().Be(expected);
    }

    // The raw word is what is stored, not just a Zero/NonZero classification.
    [Fact]
    public void The_Raw_Argument_Word_Is_Stored_Unmodified()
    {
        var ram = new RecompilerGuestMemory();

        ChangeClearPad(CreateRuntime(ram), 0xFFFFFFFFu);

        var raw = new byte[8];
        new GuestMemoryReader(ram.Read8).TryRead(BiosPadCardAutoAck.VariableAddress, raw).Should().BeTrue();
        raw.Should().Equal(new byte[] { 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
    }

    [Fact]
    public void Repeated_Calls_Overwrite_The_Setting_Deterministically()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);

        ChangeClearPad(runtime, 0);
        Setting(ram).Should().Be(BiosPadCardAutoAckSetting.Zero);
        ChangeClearPad(runtime, 1);
        Setting(ram).Should().Be(BiosPadCardAutoAckSetting.NonZero);
        ChangeClearPad(runtime, 0);
        Setting(ram).Should().Be(BiosPadCardAutoAckSetting.Zero);
    }

    // Some engines rebuild the Runtime every segment.
    [Fact]
    public void The_Setting_Survives_Rebuilding_The_Runtime_Over_The_Same_Memory()
    {
        var ram = new RecompilerGuestMemory();
        ChangeClearPad(CreateRuntime(ram), 0);

        _ = CreateRuntime(ram);

        Setting(ram).Should().Be(BiosPadCardAutoAckSetting.Zero);
    }

    // Configuration only: exactly the 8-byte variable changes. The service has
    // no interrupt-controller or device dependency, so nothing else can be acked.
    [Fact]
    public void The_Call_Changes_Only_Its_Own_Variable()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var before = Snapshot(ram);

        ChangeClearPad(runtime, 0);

        var after = Snapshot(ram);
        for (var i = 0; i < before.Length; i++)
        {
            var inVariable = i >= BiosPadCardAutoAck.VariableAddress && i < BiosPadCardAutoAck.VariableAddress + 8;
            if (!inVariable)
            {
                after[i].Should().Be(before[i], $"byte 0x{i:X} is outside the auto-ack variable");
            }
        }
    }

    [Fact]
    public void The_Variable_Does_Not_Overlap_The_Exception_Hook_Slot()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [0x00001000u]));

        ChangeClearPad(runtime, 1);

        var pointer = new byte[4];
        new GuestMemoryReader(ram.Read8).TryRead(BiosExceptionHook.PointerAddress, pointer).Should().BeTrue();
        BitConverter.ToUInt32(pointer).Should().Be(0x00001000u);
    }

    [Fact]
    public void Dispatch_Applies_The_Callers_Ra_Once_And_Leaves_V0_Alone()
    {
        var gpr = new uint[32];
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.ChangeClearPadFunction;
        gpr[(int)R3000aRegister.A0] = 0;
        gpr[(int)R3000aRegister.V0] = 0x1234;
        gpr[(int)R3000aRegister.Ra] = 0x80012345;

        var outcome = BiosVectorDispatch.Dispatch(
            CreateRuntime(new RecompilerGuestMemory()), BiosCallFamily.B0, gpr);

        outcome.ContinueExecution.Should().BeTrue();
        outcome.NextPc.Should().Be(0x80012345u);
        outcome.ReturnValue.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Wrong_Argument_Count_Is_Rejected_And_Writes_Nothing(int argumentCount)
    {
        var ram = new RecompilerGuestMemory();

        var result = CreateRuntime(ram).Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, arguments: new uint[argumentCount]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        Setting(ram).Should().Be(BiosPadCardAutoAckSetting.NotConfigured);
    }

    [Fact]
    public void An_Unwritable_Variable_Fails_Closed()
    {
        var runtime = new BiosHleRuntime(
            new CapturedOutputSink(),
            new GuestMemoryReader(new RecompilerGuestMemory().Read8),
            new RejectingWriter());

        ChangeClearPad(runtime, 0).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    private sealed class RejectingWriter : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => false;

        public bool TryWrite(uint address, ReadOnlySpan<byte> buffer) => false;
    }
}
