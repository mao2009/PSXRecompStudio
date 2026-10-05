using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The kernel's priority-3 <c>DefInt</c> (psx-spx interrupt-exception-handling: the default handlers are
/// <c>CdromDmaIrq, CdromIoIrq, SyscallException</c> at priority 0, the card/VBlank/timer handlers at 1,
/// <c>PadCardIrq</c> at 2 and <c>DefInt</c> at 3) as the last chain element, reduced to what the Runtime can
/// state without guessing (#690).
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx): DefInt checks every IRQ source and delivers its "default IRQ handler event"
/// (<c>F0000001h,1000h</c> for IRQ0/VBlank), and it does <em>not</em> acknowledge the IRQ unless
/// <c>C(0Dh) SetIrqAutoAck</c> enabled that for the IRQ ("By default, AutoAck is disabled for all IRQs").
/// DefInt never calls ReturnFromException, so a chain that reaches the end here is a fully executed
/// exception handler and the completion step (the B0:19 hook, else the default Exit) runs; the IRQ stays
/// pending for the hook to acknowledge.
/// </para>
/// <para>
/// Modelled: only IRQ0 pending and enabled (or nothing pending). The element delivers IRQ0's event as far as
/// the Runtime can (no EvCB table: nothing can match, a no-op, as for #660); an EvCB table that exists needs EvCB
/// matching and the <c>1000h</c> callback mode (#687), so it fails closed. Not modelled and failing closed: any other
/// pending enabled IRQ (priority 0 owners such as the CD-ROM handlers, #444; the other DefInt events), because an
/// unmodelled element could claim or acknowledge it first.
/// </para>
/// <para>
/// Priority 2 (<c>PadCardIrq</c>) is skipped by the caller: psx-spx has InitPAD2 not enqueue it and StartPAD2
/// enqueue it, and the Runtime registers none of StartPAD/StartCARD or the C0:02 enqueue, so nothing can be in
/// that chain (#661 models it when they exist).
/// </para>
/// <para>
/// Auto-ack: the per-IRQ flag is <c>C0:0D</c> state, which the Runtime does not model. Its default (disabled) is
/// the only value reachable, because the only writer (C0:0D) is not registered and a call to it stops the run.
/// <see cref="DefaultAutoAck"/> is that default and the seam a future C0:0D replaces.
/// </para>
/// </remarks>
[Domain]
public static class BiosDefaultInterruptHandler
{
    /// <summary>The DefInt event class of IRQ0 / VBlank (psx-spx event-summary: <c>F0000001h,1000h</c>).</summary>
    public const uint VblankEventClass = 0xF0000001;

    /// <summary>The spec every DefInt event is delivered with (psx-spx event-summary: <c>,1000h</c>).</summary>
    public const uint EventSpec = 0x1000;

    /// <summary>Per-IRQ auto-ack of DefInt as <c>C0:0D</c> would set it; the psx-spx default is disabled for every IRQ.</summary>
    public static bool DefaultAutoAck(int irq) => false;

    /// <summary>
    /// Runs DefInt once. <see cref="BiosExceptionChainStatus.Completed"/> means the chain ran to its end;
    /// <see cref="BiosExceptionChainStatus.Unsupported"/> means a pending enabled IRQ is outside the modelled set.
    /// </summary>
    /// <param name="context">The chain context.</param>
    /// <param name="autoAck">Per-IRQ auto-ack (C0:0D); null uses <see cref="DefaultAutoAck"/>.</param>
    public static BiosExceptionChainResult Run(BiosExceptionChainContext context, Func<int, bool>? autoAck = null)
    {
        ArgumentNullException.ThrowIfNull(context.Reader);
        ArgumentNullException.ThrowIfNull(context.Interrupts);
        autoAck ??= DefaultAutoAck;

        var status = context.Interrupts.Status;
        var mask = context.Interrupts.Mask;
        var pending = status & mask;
        if (pending == 0)
        {
            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        }

        const uint vblankBit = 1u << DeviceScheduler.VblankIrq;
        var label = $"I_STAT=0x{status:X4}, I_MASK=0x{mask:X4}, pendingEnabled=0x{pending:X4}";
        if (pending != vblankBit)
        {
            return Unsupported(
                $"{label}|a pending enabled IRQ other than IRQ0 (or several) needs a kernel priority-chain element the Runtime does not model " +
                "(priority 0 owners, other DefInt events)");
        }

        if (!BiosTimerVblankIrqHandler.EventTableIsAbsent(context.Reader))
        {
            return Unsupported(
                $"{label}|DefInt event 0x{VblankEventClass:X8},{EventSpec:X} could not be delivered (requires an absent, readable EvCB table at [0x120]; " +
                "EvCB matching and the 1000h callback mode are not modelled, #687)");
        }

        if (autoAck(DeviceScheduler.VblankIrq))
        {
            context.Interrupts.Acknowledge(~vblankBit);
        }

        return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
    }

    private static BiosExceptionChainResult Unsupported(string detail) =>
        new(BiosExceptionChainStatus.Unsupported, detail);
}
