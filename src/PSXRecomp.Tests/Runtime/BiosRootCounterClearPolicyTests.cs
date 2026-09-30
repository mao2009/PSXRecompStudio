using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// C0:0A ChangeClearRCnt(t,flag) — PSX-SPX kernelbios/timer-functions. Issue #655.
// Configuration only: the Runtime has no kernel timer/vblank IRQ handler yet, so
// nothing consumes the flags and no IRQ effect is modelled here.
[Test]
public sealed class BiosRootCounterClearPolicyTests
{
    private static BiosHleRuntime CreateRuntime(RecompilerGuestMemory ram) =>
        new(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

    private static BiosServiceResult Change(BiosHleRuntime runtime, uint t, uint flag) =>
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, arguments: [t, flag]));

    private static uint Flag(RecompilerGuestMemory ram, uint t)
    {
        BiosRootCounterClearPolicy.TryGetFlag(new GuestMemoryReader(ram.Read8), t, out var flag).Should().BeTrue();
        return flag;
    }

    private static byte[] Snapshot(RecompilerGuestMemory ram)
    {
        var bytes = new byte[0x1000];
        new GuestMemoryReader(ram.Read8).TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    [Fact]
    public void ChangeClearRCnt_Is_Registered_At_C0_0A_With_Two_Arguments_And_Neighbours_Stay_Unsupported()
    {
        BiosHleRuntime.ChangeClearRCntFunction.Should().Be(0x0A);
        IBiosRuntime runtime = CreateRuntime(new RecompilerGuestMemory());

        runtime.TryGetServiceArgumentCount(BiosCallFamily.C0, 0x0A, out var count).Should().BeTrue();
        count.Should().Be(2);

        foreach (byte neighbour in new byte[] { 0x09, 0x0B })
        {
            runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, neighbour, arguments: [0u, 1u]))
                .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        }
    }

    [Fact]
    public void The_First_Call_Returns_The_Inferred_Initial_Flag_Zero()
    {
        var result = Change(CreateRuntime(new RecompilerGuestMemory()), 0, 1);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0u, "the flag variable starts zero-initialised (INFERRED)");
    }

    [Fact]
    public void Each_Call_Returns_The_Previous_Flag_And_Stores_The_New_One()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);

        Change(runtime, 2, 1).ReturnValue.Should().Be(0u);
        Flag(ram, 2).Should().Be(1u);
        Change(runtime, 2, 0).ReturnValue.Should().Be(1u);
        Flag(ram, 2).Should().Be(0u);
        Change(runtime, 2, 1).ReturnValue.Should().Be(0u);
        Change(runtime, 2, 1).ReturnValue.Should().Be(1u);
    }

    [Fact]
    public void The_Four_Sources_Are_Independent()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);

        Change(runtime, 1, 1);
        Change(runtime, 3, 1);

        Flag(ram, 0).Should().Be(0u);
        Flag(ram, 1).Should().Be(1u);
        Flag(ram, 2).Should().Be(0u);
        Flag(ram, 3).Should().Be(1u, "t=3 is vblank");
    }

    [Fact]
    public void The_Flags_Survive_Rebuilding_The_Runtime_Over_The_Same_Memory()
    {
        var ram = new RecompilerGuestMemory();
        Change(CreateRuntime(ram), 0, 1);

        Change(CreateRuntime(ram), 0, 0).ReturnValue.Should().Be(1u);
    }

    [Fact]
    public void The_Call_Changes_Only_The_Selected_Flag_Word()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var before = Snapshot(ram);

        Change(runtime, 1, 1);

        var after = Snapshot(ram);
        var word = BiosRootCounterClearPolicy.VariableAddress + 4;
        for (var i = 0; i < before.Length; i++)
        {
            if (i < word || i >= word + 4)
            {
                after[i].Should().Be(before[i], $"byte 0x{i:X} is outside the selected flag word");
            }
        }
    }

    [Fact]
    public void The_Variable_Does_Not_Overlap_The_Other_Kernel_Variables()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [0x1000u]));
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, arguments: [1u]));

        for (uint t = 0; t <= 3; t++)
        {
            Change(runtime, t, 1);
        }

        var reader = new GuestMemoryReader(ram.Read8);
        var hook = new byte[4];
        reader.TryRead(BiosExceptionHook.PointerAddress, hook).Should().BeTrue();
        BitConverter.ToUInt32(hook).Should().Be(0x1000u);
        BiosPadCardAutoAck.TryGetSetting(reader, out var pad).Should().BeTrue();
        pad.Should().Be(BiosPadCardAutoAckSetting.NonZero);
    }

    [Theory]
    [InlineData(4u, 0u)]
    [InlineData(0xFFFFFFFFu, 1u)]
    [InlineData(0u, 2u)]
    [InlineData(3u, 0xFFFFFFFFu)]
    public void Undocumented_Arguments_Are_Rejected_And_Change_Nothing(uint t, uint flag)
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var before = Snapshot(ram);

        var result = Change(runtime, t, flag);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        Snapshot(ram).Should().Equal(before);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Wrong_Argument_Count_Is_Rejected(int argumentCount)
    {
        var result = CreateRuntime(new RecompilerGuestMemory()).Invoke(new BiosCallIdentity(
            BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, arguments: new uint[argumentCount]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
    }

    [Fact]
    public void Dispatch_Writes_The_Old_Flag_To_V0_And_Applies_Ra_Once()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        Change(runtime, 0, 1);

        var gpr = new uint[32];
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.ChangeClearRCntFunction;
        gpr[(int)R3000aRegister.A0] = 0;
        gpr[(int)R3000aRegister.A1] = 0;
        gpr[(int)R3000aRegister.Ra] = 0x80012345;

        var outcome = BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.C0, gpr);

        outcome.ContinueExecution.Should().BeTrue();
        outcome.ReturnValue.Should().Be(1u);
        outcome.NextPc.Should().Be(0x80012345u);
    }

    [Fact]
    public void An_Inaccessible_Variable_Fails_Closed()
    {
        var runtime = new BiosHleRuntime(
            new CapturedOutputSink(),
            new GuestMemoryReader(new RecompilerGuestMemory().Read8),
            new RejectingWriter());

        Change(runtime, 0, 1).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    private sealed class RejectingWriter : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => false;

        public bool TryWrite(uint address, ReadOnlySpan<byte> buffer) => false;
    }
}
