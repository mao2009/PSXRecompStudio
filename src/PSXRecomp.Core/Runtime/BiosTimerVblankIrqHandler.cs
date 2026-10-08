using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Delivers the root-counter events (psx-spx event-summary <c>F2000000h..F2000003h,2</c>) of one
/// timer/VBlank source. Returns false when the Runtime cannot deliver them: a handler that skipped
/// delivery would pretend the IRQ was serviced. The default is <see cref="BiosTimerVblankIrqHandler.DeliverEvents"/> (#660).
/// </summary>
/// <param name="context">The chain context (guest memory, interrupt controller, exception).</param>
/// <param name="source">C0:0A <c>t</c>: 0..2 timer 0..2, 3 VBlank.</param>
[Domain]
public delegate bool BiosRootCounterEventDelivery(BiosExceptionChainContext context, uint source);

/// <summary>
/// The kernel's priority-1 timer/VBlank IRQ handlers (psx-spx interrupt-exception-handling:
/// <c>VblankIrq, Timer2Irq, Timer1Irq, Timer0Irq</c>) as one chain element, consuming the C0:0A
/// <c>ChangeClearRCnt</c> flags kept in <see cref="BiosRootCounterClearPolicy"/> (the only state).
/// </summary>
/// <remarks>
/// <para>
/// Per source (t=0..2 → Timer0..2 / IRQ4..6, t=3 → VBlank / IRQ0): the handler claims the exception when
/// its IRQ is pending in I_STAT and enabled in I_MASK (INFERRED for the mask; psx-spx only says a handler
/// checks I_STAT). A claimed source first has its root-counter events delivered
/// (<see cref="BiosRootCounterEventDelivery"/>), then C0:0A decides: flag 0 → nothing more (no
/// acknowledge, no return, the chain continues); flag 1 → the IRQ is acknowledged through
/// <see cref="IInterruptController.Acknowledge"/> (W0C: only its own bit) and the exception is returned
/// from at once, so lower priorities and the B0:19 hook are skipped. A flag other than 0/1 or an
/// unreadable one is undocumented and fails closed; guest state is never corrected.
/// </para>
/// <para>
/// Elements run in the order VBlank, Timer2, Timer1, Timer0. That order is INFERRED from the
/// priority-chain listing. Priority 2 (Pad/Card, #661) is not reached by this element: it runs only
/// when no priority-1 element returned from the exception.
/// </para>
/// </remarks>
[Domain]
public static class BiosTimerVblankIrqHandler
{
    private static readonly (uint Source, int Irq, string Name)[] Elements =
    [
        (3, DeviceScheduler.VblankIrq, "VBlank"),
        (2, DeviceScheduler.Timer0Irq + 2, "Timer2"),
        (1, DeviceScheduler.Timer0Irq + 1, "Timer1"),
        (0, DeviceScheduler.Timer0Irq, "Timer0"),
    ];

    /// <summary>The root-counter event class of <c>t</c> = 0..3 is <c>F2000000h + t</c> (psx-spx event-summary).</summary>
    public const uint RootCounterEventClassBase = 0xF2000000;

    /// <summary>The spec every root-counter event is delivered with (psx-spx event-summary: <c>F200000xh,2</c>).</summary>
    public const uint RootCounterEventSpec = 2;

    /// <summary>
    /// B0:07 <c>DeliverEvent(F2000000h + t, 2)</c> as far as the Runtime can perform it: the EvCB matching of
    /// <see cref="BiosEventControlBlocks.Deliver"/> over the one EvCB table (#687). A BIOS-less run with no table has nothing
    /// to match and succeeds with no effect. False for <c>t</c> &gt; 3, an unusable table, or a matching enabled mode 1000h
    /// event with a callback (callback execution is not modelled).
    /// </summary>
    public static bool DeliverEvents(BiosExceptionChainContext context, uint source)
    {
        ArgumentNullException.ThrowIfNull(context.Reader);
        ArgumentNullException.ThrowIfNull(context.Writer);

        return source <= BiosRootCounterClearPolicy.MaxSource &&
               BiosEventControlBlocks.Deliver(context.Reader, context.Writer, RootCounterEventClassBase + source, RootCounterEventSpec);
    }

    /// <summary>
    /// Runs the priority-1 elements once. <see cref="BiosExceptionChainStatus.Completed"/> means none returned from
    /// the exception and the next priority runs. <paramref name="deliverEvents"/> defaults to <see cref="DeliverEvents"/>.
    /// </summary>
    public static BiosExceptionChainResult Run(BiosExceptionChainContext context, BiosRootCounterEventDelivery? deliverEvents = null)
    {
        ArgumentNullException.ThrowIfNull(context.Reader);
        ArgumentNullException.ThrowIfNull(context.Interrupts);
        deliverEvents ??= DeliverEvents;

        foreach (var (source, irq, name) in Elements)
        {
            var bit = 1u << irq;
            if ((context.Interrupts.Status & context.Interrupts.Mask & bit) == 0)
            {
                continue;
            }

            var label = $"I_STAT=0x{context.Interrupts.Status:X4}, I_MASK=0x{context.Interrupts.Mask:X4}|{name} IRQ{irq} (C0:0A t={source})";
            if (!BiosRootCounterClearPolicy.TryGetFlag(context.Reader, source, out var flag) || flag > 1)
            {
                return Unsupported($"{label}|the ChangeClearRCnt flag is unreadable or not 0/1 (undocumented; not guessed)");
            }

            if (!deliverEvents(context, source))
            {
                return Unsupported(
                    $"{label} flag={flag}|event 0x{RootCounterEventClassBase + source:X8},{RootCounterEventSpec} could not be " +
                    "delivered (default delivery fails on an unusable EvCB table or a matching enabled 1000h callback event, which is not modelled, #687; a custom delivery callback may also fail)");
            }

            if (flag == 1)
            {
                context.Interrupts.Acknowledge(~bit);
                return new BiosExceptionChainResult(BiosExceptionChainStatus.ReturnedFromException);
            }
        }

        return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
    }

    private static BiosExceptionChainResult Unsupported(string detail) =>
        new(BiosExceptionChainStatus.Unsupported, detail);
}
