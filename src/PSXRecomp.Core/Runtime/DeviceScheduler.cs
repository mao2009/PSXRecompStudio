using PSXRecomp.Architecture;

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
/// It keeps no clock of its own: elapsed cycles come from the caller on every
/// <see cref="Advance"/>. The only state is the phase inside the current VBlank
/// interval and the DMA IRQ line's last level, for edge detection.
/// </para>
/// <para>
/// Fixed order within one <see cref="Advance"/>, each stage raising its own
/// line: Timers (IRQ4-6) → DMA (IRQ3) → SIO0 (IRQ7) → VBlank (IRQ0). Whether
/// the CPU takes the aggregate line as an INT exception is the stepping
/// caller's choice: <c>PSXCore_Step</c> samples it (Issue #144), while
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

    /// <summary>DMA interrupt line.</summary>
    public const int DmaIrq = 3;

    /// <summary>Timer 0 interrupt line; Timer <c>n</c> raises <c>Timer0Irq + n</c>.</summary>
    public const int Timer0Irq = 4;

    /// <summary>SIO0 (controller/memory-card, "byte received") interrupt line (Issue #543).</summary>
    public const int Sio0Irq = 7;

    private const int TimerCount = 3;

    private readonly PSXCoreWrapper _core;
    private readonly IInterruptController _interrupts;
    private uint _cyclesSinceVblank;
    private bool _dmaIrqLine;

    /// <summary>Creates a scheduler over <paramref name="core"/>'s devices.</summary>
    /// <param name="core">The native core whose Timer/DMA state advances.</param>
    /// <param name="interrupts">Where device interrupts are raised; normally the
    /// core's own <c>InterruptControllerMmioAdapter</c>.</param>
    public DeviceScheduler(PSXCoreWrapper core, IInterruptController interrupts)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _interrupts = interrupts ?? throw new ArgumentNullException(nameof(interrupts));
    }

    /// <summary>Advances every device by <paramref name="cycles"/> elapsed CPU cycles.</summary>
    public void Advance(uint cycles)
    {
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
        // raised it (a completion here or a guest DICR write in between).
        _core.TickDma(cycles);
        var dmaLine = _core.GetDmaInterruptPending();
        if (dmaLine && !_dmaIrqLine)
        {
            _interrupts.Raise(DmaIrq);
        }
        _dmaIrqLine = dmaLine;

        // SIO0: no clock of its own (Issue #543 is event-driven off DATA
        // writes, not cycle count), so this is a bare poll/clear, not a Tick.
        if (_core.GetSio0InterruptPending())
        {
            _core.ClearSio0Interrupt();
            _interrupts.Raise(Sio0Irq);
        }

        // VBlank: several intervals elapsed in one call still latch one IRQ0.
        var phase = (ulong)_cyclesSinceVblank + cycles;
        if (phase >= VblankIntervalCycles)
        {
            _interrupts.Raise(VblankIrq);
        }
        _cyclesSinceVblank = (uint)(phase % VblankIntervalCycles);
    }
}
