using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The BIOS-less entry of the C0:06 ExceptionHandler: context save, chain walk, completion. Issue #662.
// Runs over the real Rust-backed interrupt controller and DeviceScheduler.
[Test]
public sealed class BiosExceptionHandlerTests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint HookBuffer = 0x00001000;
    private const uint Epc = 0x80025CBC;
    private const uint Cause = 0x00000400;
    private const uint EntrySr = 0x00000404; // as the CPU leaves it after the entry push

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly DeviceScheduler _scheduler;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosExceptionHandlerTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
        _scheduler = new DeviceScheduler(_core, _interrupts);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private GuestMemoryReader Reader => new(_ram.Read8);

    private GuestMemoryWriter Writer => new(_ram.Write8);

    private static uint[] Registers(uint seed)
    {
        var gpr = new uint[32];
        for (var i = 1; i < gpr.Length; i++)
        {
            gpr[i] = seed + (uint)i * 0x11u;
        }

        return gpr;
    }

    private static BiosExceptionContext Context(uint cause = Cause) => new(Epc, cause, EntrySr, 0xAAAA0001, 0xBBBB0002);

    private uint ReadWord(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Reader.TryRead(address, bytes).Should().BeTrue();
        return BitConverter.ToUInt32(bytes);
    }

    private void WriteWords(uint address, params uint[] words) =>
        Writer.TryWrite(address, words.SelectMany(BitConverter.GetBytes).ToArray()).Should().BeTrue();

    /// <summary>Pending, enabled VBlank: the chain has something to claim.</summary>
    private void RaiseEnabledVblank()
    {
        _interrupts.SetMask(VblankBit);
        _scheduler.Advance(DeviceScheduler.VblankIntervalCycles);
        (_interrupts.Status & _interrupts.Mask).Should().Be(VblankBit);
    }

    private void RegisterHook(uint[] saved)
    {
        // Documented jmp_buf order: ra, sp, fp, s0..s7, gp.
        WriteWords(HookBuffer, new[] { 31, 29, 30, 16, 17, 18, 19, 20, 21, 22, 23, 28 }.Select(r => saved[r]).ToArray());
        new BiosHleRuntime(new CapturedOutputSink(), Reader, Writer)
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [HookBuffer]))
            .Status.Should().Be(BiosServiceStatus.Supported);
    }

    private BiosExceptionHandlerOutcome Handle(uint[] gpr, BiosExceptionChain? chain = null, uint cause = Cause) =>
        BiosExceptionHandler.Handle(Reader, Writer, _interrupts, gpr, Context(cause), chain);

    private static BiosExceptionChain Completing { get; } = _ => new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);

    // ---- vector ownership ----------------------------------------------------------------

    [Fact]
    public void An_Unpopulated_Vector_Is_The_Kernels_And_Any_Guest_Word_Keeps_It_Guest_Owned()
    {
        BiosExceptionHandler.IsKernelVector(Reader).Should().BeTrue("RAM is zero: nothing of the guest's is there");

        // The last of the four stub words: a guest handler need not start with a non-nop.
        WriteWords(0x8C, 0x42000010);

        BiosExceptionHandler.IsKernelVector(Reader).Should().BeFalse();
    }

    // ---- entry ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0x08u)] // Sys: the execution path's SYSCALL contract (#663), not this handler's
    [InlineData(0x09u)] // Bp
    [InlineData(0x0Au)] // RI
    public void A_NonInterrupt_Excode_Fails_Closed_Without_Touching_Guest_Memory(uint excode)
    {
        var gpr = Registers(0x100);

        var outcome = Handle(gpr, Completing, cause: excode << 2);

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.UnsupportedExceptionDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain($"Excode=0x{excode:X2}").And.Contain("EPC=0x80025CBC");
        ReadWord(BiosExceptionCompletion.ProcessControlBlockPointerAddress).Should().Be(0u, "no PCB was seeded for an exception the handler does not serve");
    }

    [Fact]
    public void Without_A_PCB_The_First_Exception_Seeds_One_Zeroed_TCB_And_Saves_The_Context_There()
    {
        var gpr = Registers(0x1000);

        Handle(gpr, Completing).Handled.Should().BeTrue();

        var pcb = ReadWord(BiosExceptionCompletion.ProcessControlBlockPointerAddress);
        pcb.Should().Be(BiosExceptionHandler.SeededPcbAddress);
        var tcb = ReadWord(pcb);
        tcb.Should().Be(BiosExceptionHandler.SeededTcbAddress);
        ReadWord(tcb).Should().Be(0u, "no thread status is invented");
        for (var r = 1; r < 32; r++)
        {
            ReadWord(tcb + 0x08 + (uint)r * 4).Should().Be(gpr[r], $"r{r} is saved at TCB+08h+4r");
        }

        ReadWord(tcb + 0x88).Should().Be(Epc);
        ReadWord(tcb + 0x8C).Should().Be(0xAAAA0001);
        ReadWord(tcb + 0x90).Should().Be(0xBBBB0002);
        ReadWord(tcb + 0x94).Should().Be(EntrySr, "the SR the CPU left, not a second copy");
        ReadWord(tcb + 0x98).Should().Be(Cause);
    }

    [Fact]
    public void A_Guest_PCB_Is_Used_As_Is_And_Never_Replaced()
    {
        const uint pcb = 0x00003000, tcb = 0x00003100;
        WriteWords(BiosExceptionCompletion.ProcessControlBlockPointerAddress, pcb);
        WriteWords(pcb, tcb);
        var gpr = Registers(0x2000);

        Handle(gpr, Completing).Handled.Should().BeTrue();

        ReadWord(BiosExceptionCompletion.ProcessControlBlockPointerAddress).Should().Be(pcb);
        ReadWord(tcb + 0x88).Should().Be(Epc, "the context went to the guest's current TCB");
        ReadWord(BiosExceptionHandler.SeededTcbAddress + 0x88).Should().Be(0u);
    }

    [Fact]
    public void A_Guest_PCB_Without_A_Current_TCB_Fails_Closed_And_Is_Left_Alone()
    {
        const uint pcb = 0x00003000;
        WriteWords(BiosExceptionCompletion.ProcessControlBlockPointerAddress, pcb);
        WriteWords(pcb, 0);

        var outcome = Handle(Registers(1), Completing);

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.KernelStateInvalidDiagnosticCode);
        ReadWord(pcb).Should().Be(0u);
        ReadWord(BiosExceptionHandler.SeededTcbAddress + 0x88).Should().Be(0u, "nothing is fabricated over guest kernel state");
    }

    // ---- the chain ------------------------------------------------------------------------

    [Fact]
    public void The_Default_Chain_Stops_On_A_Pending_Enabled_Irq_It_Does_Not_Model_Instead_Of_Pretending_A_Handler_Ran()
    {
        // IRQ2 (CD-ROM) belongs to priority 0, which the Runtime does not model (#690 models only IRQ0 at priority 3).
        const uint cdromBit = 1u << 2;
        _interrupts.SetMask(cdromBit);
        _interrupts.Raise(2);
        var saved = Registers(0x1000);
        RegisterHook(saved);
        var gpr = Registers(0x5000);

        var outcome = Handle(gpr);

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain($"I_STAT=0x{cdromBit:X4}")
            .And.Contain("EPC=0x80025CBC").And.Contain("CAUSE=0x00000400").And.Contain("SR=0x00000404")
            .And.Contain($"pendingEnabled=0x{cdromBit:X4}");
        outcome.Gpr.Should().Equal(gpr, "the hook did not fire: the chain never ran to the end");
        (_interrupts.Status & cdromBit).Should().Be(cdromBit, "nothing acknowledged the IRQ");
    }

    [Fact]
    public void The_Default_Chain_Runs_To_The_End_For_An_Unacknowledged_Vblank_And_Leaves_It_To_The_Hook()
    {
        RaiseEnabledVblank();
        var saved = Registers(0x1000);
        RegisterHook(saved);

        var outcome = Handle(Registers(0x5000));

        outcome.Handled.Should().BeTrue("priority 1 (flag 0) continues, priority 2 is empty, DefInt does not acknowledge (#690)");
        outcome.NextPc.Should().Be(saved[31]);
        outcome.Gpr[(int)R3000aRegister.V0].Should().Be(1u);
        (_interrupts.Status & VblankBit).Should().Be(VblankBit, "the guest's hook acknowledges, not the kernel");
    }

    [Fact]
    public void The_Default_Chain_Ends_When_Nothing_Enabled_Is_Pending()
    {
        _interrupts.SetMask(0);

        BiosExceptionHandler.DefaultChain(new BiosExceptionChainContext(Reader, Writer, _interrupts, Context())).Status.Should().Be(BiosExceptionChainStatus.Completed);
    }

    // ---- completion -----------------------------------------------------------------------

    [Fact]
    public void A_Completed_Chain_Without_A_Hook_Returns_From_The_Exception_Through_The_Saved_TCB()
    {
        var gpr = Registers(0x1000);
        var snapshot = (uint[])gpr.Clone();

        var outcome = Handle(gpr, Completing);

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(Epc, "ReturnFromException resumes at the saved EPC");
        outcome.Gpr.Should().Equal(snapshot, "the saved context is restored as it was");
        outcome.Hi.Should().Be(0xAAAA0001);
        outcome.Lo.Should().Be(0xBBBB0002);
        outcome.RestoredSr.Should().Be(EntrySr, "the saved SR is handed to the CPU, which performs RFE");
        gpr.Should().Equal(snapshot, "the input register file is never mutated");
    }

    [Fact]
    public void A_Completed_Chain_Fires_The_Registered_Hook_And_Leaves_SR_To_The_Cpu()
    {
        var saved = Registers(0x1000);
        RegisterHook(saved);
        var live = Registers(0x5000);

        var outcome = Handle(live, Completing);

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(saved[31], "the hook continues at its saved ra");
        outcome.Gpr[(int)R3000aRegister.V0].Should().Be(1u);
        outcome.Gpr[(int)R3000aRegister.Sp].Should().Be(saved[29]);
        outcome.Gpr[(int)R3000aRegister.A0].Should().Be(live[4], "only the hook's registers are restored");
        outcome.RestoredSr.Should().BeNull("no ReturnFromException, so no RFE");
    }

    [Fact]
    public void A_Chain_Element_That_Returns_From_The_Exception_Skips_The_Hook()
    {
        var saved = Registers(0x1000);
        RegisterHook(saved);
        var live = Registers(0x5000);

        var outcome = Handle(live, _ => new BiosExceptionChainResult(BiosExceptionChainStatus.ReturnedFromException));

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(Epc, "the early return resumes at EPC, not at the hook");
        outcome.Gpr[(int)R3000aRegister.V0].Should().Be(live[2], "the hook did not fire");
        outcome.RestoredSr.Should().Be(EntrySr);
    }

    [Fact]
    public void An_Unreadable_Hook_Buffer_Fails_Closed()
    {
        // The hook pointer names an address past guest RAM.
        WriteWords(BiosExceptionHook.PointerAddress, 0x7FFFFFF0);

        var outcome = Handle(Registers(1), Completing);

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.KernelStateInvalidDiagnosticCode);
    }

    [Fact]
    public void The_Chain_Sees_The_Real_Interrupt_Controller_And_Can_Acknowledge_Through_It()
    {
        RaiseEnabledVblank();

        var outcome = Handle(Registers(1), ctx =>
        {
            ctx.Interrupts.Acknowledge(~VblankBit);
            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        });

        outcome.Handled.Should().BeTrue();
        (_interrupts.Status & VblankBit).Should().Be(0u);
    }

    [Fact]
    public void The_Chain_Context_Carries_Guest_Memory_The_Interrupt_Controller_And_The_Exception()
    {
        // What a #658/#661 element needs: read guest kernel state, act on the controller, know the exception.
        const uint probe = 0x00004000;
        _ram.Write8(probe, 0x5A);
        RaiseEnabledVblank();
        BiosExceptionChainContext? seen = null;
        byte read = 0;

        var outcome = Handle(Registers(1), ctx =>
        {
            seen = ctx;
            Span<byte> b = stackalloc byte[1];
            ctx.Reader.TryRead(probe, b).Should().BeTrue();
            read = b[0];
            ctx.Interrupts.Acknowledge(~VblankBit);
            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        });

        outcome.Handled.Should().BeTrue();
        read.Should().Be(0x5A);
        (_interrupts.Status & VblankBit).Should().Be(0u);
        seen!.Value.Exception.Epc.Should().Be(Epc);
        seen.Value.Exception.Cause.Should().Be(Cause);
        seen.Value.Exception.Sr.Should().Be(EntrySr);
        seen.Value.Writer.Should().NotBeNull();
    }
}
