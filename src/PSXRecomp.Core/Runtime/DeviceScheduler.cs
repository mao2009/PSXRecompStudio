using PSXRecomp.Architecture;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Advances the PS1 devices by the CPU cycles an execution engine reports
/// (Issue #442) and delivers their interrupts to the Interrupt Controller.
/// </summary>
/// <remarks>
/// <para>
/// This type only decides <i>when</i> and <i>in which order</i> devices advance.
/// Timer, DMA and interrupt-controller semantics stay in the Rust-backed native
/// core behind <see cref="PSXCoreWrapper"/>; nothing here models a counter, a
/// channel or a register.
/// </para>
/// <para>
/// Elapsed cycles come from the caller on every <see cref="Advance"/> and are
/// accumulated for diagnostics. Scheduling state consists of the VBlank phase
/// and device interrupt source levels used for edge detection.
/// </para>
/// <para>
/// Fixed order within one <see cref="Advance"/>, each stage raising its own
/// line: Timers (IRQ4-6) → DMA (IRQ3) → CD-ROM (IRQ2) → SIO0 (IRQ7)
/// → GPU command IRQ (IRQ1) → VBlank (IRQ0). Whether
/// the CPU takes the aggregate line as an INT exception is the stepping
/// caller's choice: <c>PSXCore_Step</c> samples it (Issue #144) — the production
/// interpreter steps that way and runs the guest's handler (Issue #499) — while
/// <see cref="PSXCoreWrapper.StepWithoutInterrupts"/> holds it low.
/// </para>
/// </remarks>
[Domain]
public sealed class DeviceScheduler
{
    /// <summary>
    /// CPU cycles between VBlank interrupts: the 33.8688 MHz CPU clock over a
    /// nominal 60 Hz frame. A deterministic stand-in, not NTSC/PAL-exact timing.
    /// </summary>
    public const uint VblankIntervalCycles = 33_868_800 / 60;

    /// <summary>VBlank interrupt line.</summary>
    public const int VblankIrq = 0;

    /// <summary>GPU command interrupt line (GP0(1Fh), Issue #574).</summary>
    public const int GpuIrq = 1;

    /// <summary>CD-ROM command/data interrupt line.</summary>
    public const int CdRomIrq = 2;

    /// <summary>DMA interrupt line.</summary>
    public const int DmaIrq = 3;

    /// <summary>Timer 0 interrupt line; Timer <c>n</c> raises <c>Timer0Irq + n</c>.</summary>
    public const int Timer0Irq = 4;

    /// <summary>SIO0 (controller/memory-card, "byte received") interrupt line (Issue #543).</summary>
    public const int Sio0Irq = 7;

    private const int TimerCount = 3;

    private readonly PSXCoreWrapper _core;
    private readonly IInterruptController _interrupts;
    private readonly IGpu? _gpu;
    private readonly ICdRom? _cdRom;
    private readonly CdRomDmaTransfer? _cdRomDma;
    private readonly GpuDmaTransfer? _gpuDma;
    private readonly MdecDmaTransfer? _mdecDma;
    private readonly uint _bridgedDmaChannels;
    private uint _cyclesSinceVblank;
    private ulong _vblankCount;
    private bool _dmaIrqLine;
    private bool _gpuIrqLine;
    private ulong _lastCdRomInterruptGeneration;

    /// <summary>Total VBlank interrupts that have been delivered since scheduler creation.</summary>
    public ulong VblankCount => _vblankCount;

    /// <summary>Creates a scheduler over <paramref name="core"/>'s devices.</summary>
    /// <param name="core">The native core whose Timer/DMA state advances.</param>
    /// <param name="interrupts">Where device interrupts are raised; normally the
    /// core's own <c>InterruptControllerMmioAdapter</c>.</param>
    /// <param name="gpu">Optional GPU command-interrupt source. Production title execution
    /// passes its existing managed GPU adapter; callers without a GPU may leave it null.</param>
    /// <param name="cdRom">Optional CD-ROM interrupt source. Each newly activated,
    /// enabled command/data response packet raises IRQ2 once.</param>
    /// <param name="cdRomDma">Optional CD-ROM DMA3 bridge. When present, this
    /// scheduler exclusively owns channel 3's completion through it (Issue
    /// #587): the generic per-cycle DMA model below never advances or
    /// completes channel 3 itself, so a real CD-ROM burst is never faked by
    /// the deterministic duration model.</param>
    /// <param name="gpuDma">Optional GPU DMA2/OTC DMA6 bridge (Issue #732). When present, a started channel 2 or 6
    /// transfer moves its data and completes; the generic model never times channels 2 and 6.</param>
    /// <param name="mdecDma">Optional MDEC DMA0/DMA1 bridge (Issue #732), owning channels 0 and 1 the same way.</param>
    public DeviceScheduler(
        PSXCoreWrapper core,
        IInterruptController interrupts,
        IGpu? gpu = null,
        ICdRom? cdRom = null,
        CdRomDmaTransfer? cdRomDma = null,
        GpuDmaTransfer? gpuDma = null,
        MdecDmaTransfer? mdecDma = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _interrupts = interrupts ?? throw new ArgumentNullException(nameof(interrupts));
        _gpu = gpu;
        _cdRom = cdRom;
        _cdRomDma = cdRomDma;
        _gpuDma = gpuDma;
        _mdecDma = mdecDma;
        _bridgedDmaChannels =
            (cdRomDma is null ? 0 : 1u << CdRomDmaTransfer.Channel) |
            (gpuDma is null ? 0 : 1u << GpuDmaTransfer.GpuChannel | 1u << GpuDmaTransfer.OtcChannel) |
            (mdecDma is null ? 0 : 1u << MdecDmaTransfer.InChannel | 1u << MdecDmaTransfer.OutChannel);
    }

    /// <summary>Total guest cycles accepted by this scheduler, independent of report chunking.</summary>
    public ulong ElapsedCycles { get; private set; }

    /// <summary>Conservative positive deadline owned by the same devices that Advance services.</summary>
    public ulong NextEventCycles
    {
        get
        {
            if ((_cdRomDma?.CanTransfer ?? false) ||
                (_core.GetDmaInterruptPending() && !_dmaIrqLine) || _core.GetSio0InterruptPending() ||
                (_gpu is not null && _gpu.HasCommandInterrupt && !_gpuIrqLine) ||
                (_cdRom is not null && _cdRom.HasInterrupt && _cdRom.InterruptGeneration != _lastCdRomInterruptGeneration))
                return 1;
            var next = Math.Min((ulong)(VblankIntervalCycles - _cyclesSinceVblank),
                _core.GetNextDeviceEventCycles(_cdRomDma is null ? uint.MaxValue : CdRomDmaTransfer.Channel));
            var deadline = Math.Min(next, _cdRom?.NextEventCycles ?? ulong.MaxValue);
            if (deadline == 0) throw new InvalidOperationException("Device deadlines must be positive.");
            return deadline;
        }
    }

    /// <summary>Advances a bounded batch without passing a device deadline, preserving stage order at each one.</summary>
    public void AdvanceExact(ulong cycles)
    {
        while (cycles != 0)
        {
            // One instruction is already the scheduler's minimum retirement quantum.
            var chunk = cycles == 1 ? 1u : (uint)Math.Min(Math.Min(cycles, int.MaxValue), NextEventCycles);
            Advance(chunk);
            cycles -= chunk;
        }
    }

    /// <summary>Advances every device by <paramref name="cycles"/> elapsed CPU cycles.</summary>
    public void Advance(uint cycles)
    {
        ElapsedCycles = checked(ElapsedCycles + cycles);
        if (cycles == 0)
        {
            return;
        }

        // Timers: the latch is the timer's edge to the controller, so it is
        // consumed on delivery and a repeat-mode timer can fire again.
        _core.TickTimers(cycles);
        for (var timer = 0; timer < TimerCount; timer++)
        {
            if (_core.GetTimerInterruptPending(timer))
            {
                _core.ClearTimerInterrupt(timer);
                _interrupts.Raise(Timer0Irq + timer);
            }
        }

        // DMA: IRQ3 fires on a rising edge of the DICR bit-31 line, whatever
        // raised it (a completion here, this Advance's CD-ROM DMA3 service,
        // or a guest DICR write in between). When a CD-ROM DMA3 bridge is
        // configured it exclusively owns channel 3's completion (Issue #587):
        // it is serviced first and the deterministic per-word model skips
        // channel 3. Without a bridge, every channel (including 3) keeps the
        // generic model. The GPU (channels 2/6) and MDEC (channels 0/1)
        // bridges own their channels the same way (Issue #732).
        _gpuDma?.TryTransfer();
        _mdecDma?.TryTransfer();
        _cdRomDma?.TryTransfer();
        _core.TickDmaExcludingChannels(cycles, _bridgedDmaChannels);
        var dmaLine = _core.GetDmaInterruptPending();
        if (dmaLine && !_dmaIrqLine)
        {
            _interrupts.Raise(DmaIrq);
        }
        _dmaIrqLine = dmaLine;

        // CD-ROM: the drive's clock (a disc's sector stream and response delays,
        // Issue #732) advances first. Use the packet generation, not only a level
        // edge, so an INT3 ack that exposes a queued INT1/INT2 still produces a
        // distinct IRQ2.
        _cdRom?.Advance(cycles);
        if (_cdRom is not null &&
            _cdRom.HasInterrupt &&
            _cdRom.InterruptGeneration != _lastCdRomInterruptGeneration)
        {
            _interrupts.Raise(CdRomIrq);
            _lastCdRomInterruptGeneration = _cdRom.InterruptGeneration;
        }

        // SIO0: no clock of its own (Issue #543 is event-driven off DATA
        // writes, not cycle count), so this is a bare poll/clear, not a Tick.
        // Since Issue #716 an empty port never sets the latch, so this stage cannot fire
        // until a device model (#715/#717) drives it; its positive test returns then.
        if (_core.GetSio0InterruptPending())
        {
            _core.ClearSio0Interrupt();
            _interrupts.Raise(Sio0Irq);
        }

        // GPU command interrupt: GP0(1Fh) asserts the GPU-internal source
        // (GPUSTAT bit 24). Deliver only its rising edge as IRQ1. GP1(02h)
        // deasserts the source, while I_STAT remains independently latched until
        // the guest acknowledges it (Issue #574).
        if (_gpu is not null)
        {
            var gpuLine = _gpu.HasCommandInterrupt;
            if (gpuLine && !_gpuIrqLine)
            {
                _interrupts.Raise(GpuIrq);
            }
            _gpuIrqLine = gpuLine;
        }

        // VBlank: several intervals elapsed in one call still latch one IRQ0.
        var phase = (ulong)_cyclesSinceVblank + cycles;
        if (phase >= VblankIntervalCycles)
        {
            var vblanksElapsed = phase / VblankIntervalCycles;
            // Each VBlank toggles the field; an even number leaves it unchanged.
            if ((vblanksElapsed & 1u) != 0) _gpu?.OnVblank();
            _interrupts.Raise(VblankIrq);
            _vblankCount += vblanksElapsed;
        }
        _cyclesSinceVblank = (uint)(phase % VblankIntervalCycles);
    }
}
