using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// A0:72 CdRemove() / PSY-Q void _96_remove(void). Issue #657. psx-spx: "does NOT
// work due to SysDeqIntRP bug", so the retail effect on the priority-0 chain is not
// known and the Runtime must not pretend the CD-ROM IRQ handler was removed. The
// call is accepted with its documented contract (no arguments, void) and changes
// no guest state.
[Test]
public sealed class BiosCdRemoveTests
{
    private const int RamSize = 0x200000;

    private static BiosHleRuntime CreateRuntime(RecompilerGuestMemory ram) =>
        new(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

    private static BiosServiceResult Remove(BiosHleRuntime runtime) =>
        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.A0, BiosHleRuntime.CdRemoveFunction, arguments: []));

    private static byte[] Snapshot(RecompilerGuestMemory ram)
    {
        var bytes = new byte[RamSize];
        new GuestMemoryReader(ram.Read8).TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    [Fact]
    public void CdRemove_Is_Registered_At_A0_72_With_No_Arguments_And_Neighbours_Stay_Unsupported()
    {
        BiosHleRuntime.CdRemoveFunction.Should().Be(0x72);
        IBiosRuntime runtime = CreateRuntime(new RecompilerGuestMemory());

        runtime.TryGetServiceArgumentCount(BiosCallFamily.A0, 0x72, out var count).Should().BeTrue();
        count.Should().Be(0);

        // A0:56 is psx-spx's alias of the same routine, deliberately not registered by
        // #657; A0:71 is CdInit; B0:72 / C0:72 are other tables.
        foreach (var (family, function) in new[]
                 {
                     (BiosCallFamily.A0, (byte)0x71), (BiosCallFamily.A0, (byte)0x73), (BiosCallFamily.A0, (byte)0x56),
                     (BiosCallFamily.B0, (byte)0x72), (BiosCallFamily.C0, (byte)0x72),
                 })
        {
            runtime.TryGetServiceArgumentCount(family, function, out _).Should().BeFalse();
            runtime.Invoke(new BiosCallIdentity(family, function, arguments: []))
                .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL", $"{family}:{function:X2} is not A0:72");
        }
    }

    [Fact]
    public void The_Call_Is_Void_And_Changes_No_Guest_Memory()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var before = Snapshot(ram);

        var result = Remove(runtime);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().BeNull("_96_remove is void");
        result.Diagnostic.Should().BeNull();
        Snapshot(ram).Should().Equal(before, "the SysDeqIntRP bug leaves the retail effect unknown, so no removal is recorded");
    }

    [Fact]
    public void Repeated_Calls_Are_Deterministic_And_Leave_Other_Kernel_State_Alone()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [0x1000u]));
        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, arguments: [1u]));
        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, arguments: [3u, 1u]));
        var before = Snapshot(ram);

        var first = Remove(runtime);
        var second = Remove(runtime);

        second.Should().Be(first);
        Snapshot(ram).Should().Equal(before);
    }

    [Fact]
    public void Arguments_Are_Rejected()
    {
        var result = CreateRuntime(new RecompilerGuestMemory()).Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.CdRemoveFunction, arguments: [0u]));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
    }

    [Fact]
    public void Dispatch_Leaves_V0_Untouched_And_Applies_Ra_Once()
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var before = Snapshot(ram);

        var gpr = new uint[32];
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.CdRemoveFunction;
        gpr[(int)R3000aRegister.V0] = 0xDEADBEEF;
        gpr[(int)R3000aRegister.Ra] = 0x80012345;

        var outcome = BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.A0, gpr);

        outcome.ContinueExecution.Should().BeTrue();
        outcome.ReturnValue.Should().BeNull("a void service leaves $v0 to the caller");
        outcome.NextPc.Should().Be(0x80012345u);
        gpr[(int)R3000aRegister.V0].Should().Be(0xDEADBEEFu);
        Snapshot(ram).Should().Equal(before);
    }
}
