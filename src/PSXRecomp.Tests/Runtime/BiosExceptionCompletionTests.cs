using System.Reflection;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// C0:06 ExceptionHandler completion step and B0:17 ReturnFromException restore —
// PSX-SPX kernelbios/interrupt-exception-handling and control-blocks. Issue #651.
[Test]
public sealed class BiosExceptionCompletionTests
{
    private const uint HookBuffer = 0x00001000;
    private const uint Pcb = 0x0000E000;
    private const uint Tcb = 0x0000E100;

    private static uint[] Registers(uint seed)
    {
        var gpr = new uint[32];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = seed + (uint)i * 0x11u;
        }

        return gpr;
    }

    private static void Write(RecompilerGuestMemory ram, uint address, params uint[] words) =>
        new GuestMemoryWriter(ram.Write8).TryWrite(address, words.SelectMany(BitConverter.GetBytes).ToArray())
            .Should().BeTrue();

    private static GuestMemoryReader Reader(RecompilerGuestMemory ram) => new(ram.Read8);

    private static void RegisterHook(RecompilerGuestMemory ram, uint address, uint[] saved)
    {
        // Documented jmp_buf order: ra, sp, fp, s0..s7, gp.
        Write(ram, address, new[] { 31, 29, 30, 16, 17, 18, 19, 20, 21, 22, 23, 28 }.Select(r => saved[r]).ToArray());
        new BiosHleRuntime(new CapturedOutputSink(), Reader(ram), new GuestMemoryWriter(ram.Write8))
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [address]))
            .Status.Should().Be(BiosServiceStatus.Supported);
    }

    // TCB (psx-spx control-blocks): 08h r0..r31, 88h epc, 8Ch hi, 90h lo, 94h sr, 98h cause.
    private static void WriteCurrentTcb(RecompilerGuestMemory ram, uint[] saved, uint epc, uint hi, uint lo, uint sr)
    {
        Write(ram, BiosExceptionCompletion.ProcessControlBlockPointerAddress, Pcb, 4);
        Write(ram, Pcb, Tcb);
        Write(ram, Tcb, 0x4000, 0x1000);
        Write(ram, Tcb + 0x08, saved);
        Write(ram, Tcb + 0x88, epc, hi, lo, sr, 0xCA05E000);
    }

    [Fact]
    public void Completion_Enters_The_Registered_Hook_With_V0_One_And_Its_Saved_Ra_As_Pc()
    {
        var ram = new RecompilerGuestMemory();
        var saved = Registers(0x1000);
        RegisterHook(ram, HookBuffer, saved);
        var live = Registers(0x5000);
        var gpr = (uint[])live.Clone();

        var status = BiosExceptionCompletion.Complete(Reader(ram), gpr, out var pc);

        status.Should().Be(BiosExceptionCompletionStatus.HookEntered);
        pc.Should().Be(saved[31]);
        gpr[(int)R3000aRegister.V0].Should().Be(1u);
        gpr[(int)R3000aRegister.Sp].Should().Be(saved[29]);
        gpr[(int)R3000aRegister.A0].Should().Be(live[4], "only the hook's saved registers are restored");
    }

    [Fact]
    public void Completion_Without_A_Hook_Selects_ReturnFromException_And_Changes_Nothing()
    {
        var ram = new RecompilerGuestMemory();
        WriteCurrentTcb(ram, Registers(0x1000), 0x80010000, 1, 2, 3);
        var gpr = Registers(0x5000);

        var status = BiosExceptionCompletion.Complete(Reader(ram), gpr, out var pc);

        status.Should().Be(BiosExceptionCompletionStatus.ReturnFromException);
        gpr.Should().Equal(Registers(0x5000), "the completion step itself restores nothing; B0:17 does");
        pc.Should().Be(0u);
    }

    [Theory]
    [InlineData(0xC0000000u)]          // KSEG2, untranslatable
    [InlineData(0x00800000u - 0x10u)]  // buffer straddles the RAM window end
    public void Completion_With_An_Unreadable_Hook_Buffer_Fails_Closed(uint address)
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, BiosExceptionHook.PointerAddress, address);
        var gpr = Registers(0x5000);

        BiosExceptionCompletion.Complete(Reader(ram), gpr, out var pc)
            .Should().Be(BiosExceptionCompletionStatus.InvalidState);

        gpr.Should().Equal(Registers(0x5000));
        pc.Should().Be(0u);
    }

    [Fact]
    public void ReturnFromException_Restores_R1_R31_Except_K0_And_Reports_Hi_Lo_Sr_Epc()
    {
        var ram = new RecompilerGuestMemory();
        var saved = Registers(0x1000);
        WriteCurrentTcb(ram, saved, epc: 0x80012340, hi: 0x11111111, lo: 0x22222222, sr: 0x40000404);
        var live = Registers(0x5000);
        var gpr = (uint[])live.Clone();

        BiosExceptionCompletion.TryReturnFromException(Reader(ram), gpr, out var hi, out var lo, out var sr, out var pc)
            .Should().BeTrue();

        for (var r = 0; r < 32; r++)
        {
            var expected = r is 0 or (int)R3000aRegister.K0 ? live[r] : saved[r];
            gpr[r].Should().Be(expected, $"r{r}");
        }

        (hi, lo, sr, pc).Should().Be((0x11111111u, 0x22222222u, 0x40000404u, 0x80012340u));
    }

    [Fact]
    public void ReturnFromException_Without_A_Pcb_Fails_Closed()
    {
        var gpr = Registers(0x5000);

        BiosExceptionCompletion.TryReturnFromException(
                Reader(new RecompilerGuestMemory()), gpr, out _, out _, out _, out var pc)
            .Should().BeFalse("a BIOS-less run has no kernel-initialised PCB");

        gpr.Should().Equal(Registers(0x5000));
        pc.Should().Be(0u);
    }

    [Theory]
    [InlineData(0u)]                   // PCB names no current TCB
    [InlineData(0x00800000u - 0x40u)]  // TCB straddles the RAM window end
    [InlineData(0xFFFFFFFCu)]          // TCB + 08h would wrap
    public void ReturnFromException_With_An_Unreadable_Tcb_Fails_Closed_Without_Partial_Restore(uint tcb)
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, BiosExceptionCompletion.ProcessControlBlockPointerAddress, Pcb, 4);
        Write(ram, Pcb, tcb);
        var gpr = Registers(0x5000);

        BiosExceptionCompletion.TryReturnFromException(Reader(ram), gpr, out var hi, out var lo, out var sr, out var pc)
            .Should().BeFalse();

        gpr.Should().Equal(Registers(0x5000));
        (hi, lo, sr, pc).Should().Be((0u, 0u, 0u, 0u));
    }

    // The boundary keeps no state of its own: the hook pointer and the TCB are
    // guest RAM, and SR/EPC stay the CPU's.
    [Fact]
    public void The_Boundary_Holds_No_Mutable_State()
    {
        typeof(BiosExceptionCompletion)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().OnlyContain(f => f.IsLiteral);
    }
}
