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

    /// <summary>Guest address of the kernel's EvCB table entry (psx-spx "table of tables": <c>00000120h</c> address, <c>00000124h</c> size).</summary>
    public const uint EventControlBlockTableAddress = 0x00000120;

    /// <summary>The root-counter event class of <c>t</c> = 0..3 is <c>F2000000h + t</c> (psx-spx event-summary).</summary>
    public const uint RootCounterEventClassBase = 0xF2000000;

    /// <summary>The spec every root-counter event is delivered with (psx-spx event-summary: <c>F200000xh,2</c>).</summary>
    public const uint RootCounterEventSpec = 2;

    /// <summary>
    /// B0:07 <c>DeliverEvent(F2000000h + t, 2)</c> as far as the Runtime can perform it. DeliverEvent marks the EvCBs
    /// matching class and spec (psx-spx), and an EvCB exists only inside the kernel's EvCB table. A BIOS-less run has
    /// none (<c>[0x120]</c> and <c>[0x124]</c> are 0) and registers no event opener (B0:08 OpenEvent is unregistered,
    /// so a call to it stops the run), so nothing can match and the delivery succeeds with no effect. A table that
    /// exists is guest state needing EvCB matching and callbacks, which are not modelled (#687): false, as is an
    /// unreadable table or <c>t</c> &gt; 3. Nothing is written.
    /// </summary>
    public static bool DeliverEvents(BiosExceptionChainContext context, uint source)
    {
        ArgumentNullException.ThrowIfNull(context.Reader);

        return source <= BiosRootCounterClearPolicy.MaxSource && EventTableIsAbsent(context.Reader);
    }

    /// <summary>True when the kernel's EvCB table address and size (<c>[0x120]</c>, <c>[0x124]</c>) are readable and both 0: no EvCB can match any delivered event.</summary>
    internal static bool EventTableIsAbsent(IGuestMemoryReader reader)
    {
        Span<byte> table = stackalloc byte[2 * sizeof(uint)];
        return reader.TryRead(EventControlBlockTableAddress, table) && BitConverter.ToUInt64(table) == 0;
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
                    "delivered (default delivery requires an absent, readable EvCB table at [0x120]; EvCB matching is not modelled, #687; a custom delivery callback may also fail)");
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
