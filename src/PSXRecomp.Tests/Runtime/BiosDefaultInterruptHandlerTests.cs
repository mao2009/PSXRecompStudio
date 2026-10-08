using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The priority-3 DefInt chain element and the completion it lets the default chain reach. Issue #690.
// Runs over the real Rust-backed interrupt controller. Not covered: a Pad/Card owner in priority 2 — the Runtime registers
// no StartPAD/StartCARD or C0:02, so that state cannot be built yet (#661).
[Test]
public sealed class BiosDefaultInterruptHandlerTests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint Timer0Bit = 1u << DeviceScheduler.Timer0Irq;
    private const uint CdromBit = 1u << DeviceScheduler.CdRomIrq;
    private const uint DmaBit = 1u << DeviceScheduler.DmaIrq;
    private const uint HookBuffer = 0x00001000;
    private const uint Epc = 0x80025CBC;
    private const uint EntrySr = 0x00000404;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosDefaultInterruptHandlerTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private GuestMemoryReader Reader => new(_ram.Read8);

    private GuestMemoryWriter Writer => new(_ram.Write8);

    private BiosExceptionChainContext Context() =>
        new(Reader, Writer, _interrupts, new BiosExceptionContext(Epc, 0x400, EntrySr, 1, 2));

    private static uint[] Registers(uint seed)
    {
        var gpr = new uint[32];
        for (var i = 1; i < gpr.Length; i++)
        {
            gpr[i] = seed + (uint)i * 0x11u;
        }

        return gpr;
    }

    private BiosHleRuntime Runtime() => new(new CapturedOutputSink(), Reader, Writer);

    private void RegisterHook(uint[] saved)
    {
        // Documented jmp_buf order: ra, sp, fp, s0..s7, gp.
        Writer.TryWrite(HookBuffer, new[] { 31, 29, 30, 16, 17, 18, 19, 20, 21, 22, 23, 28 }
            .SelectMany(r => BitConverter.GetBytes(saved[r])).ToArray()).Should().BeTrue();
        Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [HookBuffer]))
            .Status.Should().Be(BiosServiceStatus.Supported);
    }

    private void SetFlag(uint t, uint flag) =>
        Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, arguments: [t, flag]))
            .Status.Should().Be(BiosServiceStatus.Supported);

    private void Pend(uint mask, params int[] irqs)
    {
        _interrupts.SetMask(mask);
        foreach (var irq in irqs)
        {
            _interrupts.Raise(irq);
        }
    }

    private BiosExceptionHandlerOutcome Handle(uint[] gpr) =>
        BiosExceptionHandler.Handle(Reader, Writer, _interrupts, gpr, new BiosExceptionContext(Epc, 0x400, EntrySr, 0xAAAA0001, 0xBBBB0002));

    // ---- the Persona-shaped state: IRQ0 only, C0:0A(3,0), no owner, no EvCB, B0:19 registered ----------------

    [Fact]
    public void An_Unacknowledged_Vblank_Runs_The_Chain_To_The_End_And_Enters_The_Registered_Hook()
    {
        Pend(VblankBit, DeviceScheduler.VblankIrq);
        SetFlag(3, 0);
        var saved = Registers(0x1000);
        RegisterHook(saved);
        var live = Registers(0x5000);

        var outcome = Handle(live);

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(saved[31], "the hook continues at its saved ra");
        outcome.Gpr[(int)R3000aRegister.V0].Should().Be(1u);
        outcome.Gpr[(int)R3000aRegister.Ra].Should().Be(saved[31]);
        outcome.Gpr[(int)R3000aRegister.Sp].Should().Be(saved[29]);
        outcome.Gpr[(int)R3000aRegister.Fp].Should().Be(saved[30]);
        outcome.Gpr[(int)R3000aRegister.Gp].Should().Be(saved[28]);
        for (var s = 16; s <= 23; s++)
        {
            outcome.Gpr[s].Should().Be(saved[s], $"$s{s - 16} is restored from the hook buffer");
        }

        outcome.Gpr[(int)R3000aRegister.A0].Should().Be(live[4], "only the hook's registers are restored");
        outcome.RestoredSr.Should().BeNull("no ReturnFromException, so no RFE");
        (_interrupts.Status & VblankBit).Should().Be(VblankBit, "DefInt does not acknowledge: the hook does");
    }

    [Fact]
    public void Without_A_Hook_The_Same_Chain_Takes_The_Default_Exit()
    {
        Pend(VblankBit, DeviceScheduler.VblankIrq);
        var live = Registers(0x5000);
        var snapshot = (uint[])live.Clone();

        var outcome = Handle(live);

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(Epc, "ReturnFromException resumes at the saved EPC");
        outcome.Gpr.Should().Equal(snapshot);
        outcome.RestoredSr.Should().Be(EntrySr);
        (_interrupts.Status & VblankBit).Should().Be(VblankBit);
    }

    [Fact]
    public void Nothing_Pending_Completes_The_Chain()
    {
        _interrupts.SetMask(0);

        BiosDefaultInterruptHandler.Run(Context()).Status.Should().Be(BiosExceptionChainStatus.Completed);
    }

    // ---- fail-closed boundaries ---------------------------------------------------------------------------

    [Fact]
    public void A_Pending_Enabled_Irq_Outside_The_Modelled_Set_Fails_Closed()
    {
        Pend(DmaBit, DeviceScheduler.DmaIrq);
        RegisterHook(Registers(0x1000));
        var live = Registers(0x5000);

        var outcome = Handle(live);

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain($"pendingEnabled=0x{DmaBit:X4}").And.Contain("EPC=0x80025CBC");
        outcome.Gpr.Should().Equal(live, "the hook did not fire");
        (_interrupts.Status & DmaBit).Should().Be(DmaBit);
    }

    [Fact]
    public void Several_Pending_Enabled_Irqs_Fail_Closed()
    {
        // Timer0 and VBlank are both claimed by priority 1 (flag 0: no ack, the chain continues) and both stay pending.
        Pend(VblankBit | Timer0Bit, DeviceScheduler.VblankIrq, DeviceScheduler.Timer0Irq);

        var outcome = Handle(Registers(1));

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain($"pendingEnabled=0x{VblankBit | Timer0Bit:X4}");
    }

    [Fact]
    public void A_Pending_Irq_That_Is_Not_Enabled_Is_Not_Pending_For_The_Chain()
    {
        _interrupts.SetMask(Timer0Bit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        BiosDefaultInterruptHandler.Run(Context()).Status.Should().Be(BiosExceptionChainStatus.Completed);
    }

    [Fact]
    public void An_Unusable_EvCB_Table_Fails_Closed()
    {
        Pend(VblankBit, DeviceScheduler.VblankIrq);
        Writer.TryWrite(BiosEventControlBlocks.TableAddressPointer, BitConverter.GetBytes(0x00002000u)).Should().BeTrue();

        var result = BiosDefaultInterruptHandler.Run(Context());

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("#687");
        (_interrupts.Status & VblankBit).Should().Be(VblankBit);
    }

    [Fact]
    public void An_Unusable_EvCB_Table_Stops_The_Default_Chain_Before_Any_Completion()
    {
        Pend(VblankBit, DeviceScheduler.VblankIrq);
        Writer.TryWrite(BiosEventControlBlocks.TableAddressPointer, BitConverter.GetBytes(0x00002000u)).Should().BeTrue();
        RegisterHook(Registers(0x1000));

        Handle(Registers(0x5000)).DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
    }

    // ---- IRQ2 / CD-ROM: DefInt's EVENT_CDROM (F0000003h,1000h) -----------------------------------------

    [Fact]
    public void An_Unacknowledged_CdRom_Irq_Runs_The_Chain_To_The_End_And_Enters_The_Registered_Hook()
    {
        Pend(CdromBit, DeviceScheduler.CdRomIrq);
        var saved = Registers(0x1000);
        RegisterHook(saved);

        var outcome = Handle(Registers(0x5000));

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(saved[31]);
        outcome.Gpr[(int)R3000aRegister.V0].Should().Be(1u);
        outcome.RestoredSr.Should().BeNull("no ReturnFromException: the guest's hook returns itself");
        (_interrupts.Status & CdromBit).Should().Be(CdromBit, "DefInt does not acknowledge I_STAT: the guest's callback does");
    }

    [Fact]
    public void A_CdRom_Irq_Without_A_Hook_Takes_The_Default_Exit_And_Stays_Pending()
    {
        Pend(CdromBit, DeviceScheduler.CdRomIrq);
        var live = Registers(0x5000);

        var outcome = Handle(live);

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(Epc);
        outcome.RestoredSr.Should().Be(EntrySr);
        (_interrupts.Status & CdromBit).Should().Be(CdromBit);
    }

    [Fact]
    public void A_CdRom_Irq_That_Is_Not_Enabled_Is_Not_Pending_For_The_Chain()
    {
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.CdRomIrq);

        BiosDefaultInterruptHandler.Run(Context()).Status.Should().Be(BiosExceptionChainStatus.Completed);
    }

    [Fact]
    public void A_CdRom_Irq_With_Another_Pending_Irq_Fails_Closed()
    {
        Pend(CdromBit | VblankBit, DeviceScheduler.CdRomIrq, DeviceScheduler.VblankIrq);
        RegisterHook(Registers(0x1000));

        var outcome = Handle(Registers(0x5000));

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain($"pendingEnabled=0x{CdromBit | VblankBit:X4}");
        (_interrupts.Status & (CdromBit | VblankBit)).Should().Be(CdromBit | VblankBit, "nothing is acknowledged");
    }

    [Fact]
    public void A_CdRom_Irq_With_An_Unusable_EvCB_Table_Fails_Closed_Naming_The_Cdrom_Event()
    {
        Pend(CdromBit, DeviceScheduler.CdRomIrq);
        Writer.TryWrite(BiosEventControlBlocks.TableAddressPointer, BitConverter.GetBytes(0x00002000u)).Should().BeTrue();
        RegisterHook(Registers(0x1000));

        var result = BiosDefaultInterruptHandler.Run(Context());

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("0xF0000003,1000").And.Contain("#687");
        Handle(Registers(0x5000)).DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        (_interrupts.Status & CdromBit).Should().Be(CdromBit);
    }

    [Fact]
    public void An_Enabled_CdRom_Auto_Ack_Clears_Irq2_And_Keeps_A_Masked_Pending_Irq()
    {
        // pendingEnabled = IRQ2 only; I_STAT = IRQ2 + IRQ3 (DMA is pending but masked).
        _interrupts.SetMask(CdromBit);
        _interrupts.Raise(DeviceScheduler.CdRomIrq);
        _interrupts.Raise(DeviceScheduler.DmaIrq);
        (_interrupts.Status & (CdromBit | DmaBit)).Should().Be(CdromBit | DmaBit);

        var result = BiosDefaultInterruptHandler.Run(Context(), irq => irq == DeviceScheduler.CdRomIrq);

        result.Status.Should().Be(BiosExceptionChainStatus.Completed);
        (_interrupts.Status & CdromBit).Should().Be(0u, "IRQ2 was auto-acknowledged");
        (_interrupts.Status & DmaBit).Should().Be(DmaBit, "the masked IRQ3 stays latched");
    }

    [Fact]
    public void Priority_1_Elements_Do_Not_Claim_A_CdRom_Irq()
    {
        Pend(CdromBit, DeviceScheduler.CdRomIrq);
        SetFlag(3, 1);

        BiosTimerVblankIrqHandler.Run(Context()).Status.Should().Be(BiosExceptionChainStatus.Completed);
        (_interrupts.Status & CdromBit).Should().Be(CdromBit);
    }

    // ---- priority 1 still comes first ----------------------------------------------------------------------

    [Fact]
    public void A_Priority_1_Auto_Ack_Returns_From_The_Exception_And_Never_Reaches_DefInt_Or_The_Hook()
    {
        Pend(VblankBit, DeviceScheduler.VblankIrq);
        SetFlag(3, 1);
        RegisterHook(Registers(0x1000));
        var live = Registers(0x5000);

        var outcome = Handle(live);

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(Epc, "the early return resumes at EPC, not at the hook");
        outcome.Gpr[(int)R3000aRegister.V0].Should().Be(live[2], "the hook did not fire");
        outcome.RestoredSr.Should().Be(EntrySr);
        (_interrupts.Status & VblankBit).Should().Be(0u, "priority 1 acknowledged it");
    }

    // ---- auto-ack seam (C0:0D) ----------------------------------------------------------------------------

    [Fact]
    public void DefInts_Auto_Ack_Is_Disabled_By_Default()
    {
        for (var irq = 0; irq <= 10; irq++)
        {
            BiosDefaultInterruptHandler.DefaultAutoAck(irq).Should().BeFalse($"psx-spx: AutoAck is disabled for all IRQs (IRQ{irq})");
        }
    }

    [Fact]
    public void An_Enabled_Auto_Ack_Acknowledges_Only_The_Irq_Without_Returning_From_The_Exception()
    {
        Pend(VblankBit, DeviceScheduler.VblankIrq);

        var result = BiosDefaultInterruptHandler.Run(Context(), irq => irq == DeviceScheduler.VblankIrq);

        result.Status.Should().Be(BiosExceptionChainStatus.Completed, "DefInt never calls ReturnFromException: the hook still runs");
        (_interrupts.Status & VblankBit).Should().Be(0u);
    }
}
