using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>Outcome of one <see cref="BiosPadCardIrqHandler.Handle"/> pass.</summary>
[Domain]
public enum BiosPadCardIrqOutcome : byte
{
    /// <summary>IRQ0 is not both pending and enabled; the handler does not claim this exception.</summary>
    NotClaimed,

    /// <summary>B0:5B last set 1: IRQ0 was acknowledged (only its I_STAT bit cleared).</summary>
    Acknowledged,

    /// <summary>B0:5B last set 0: IRQ0 stays pending for another handler or the guest.</summary>
    LeftPending,

    /// <summary>B0:5B was never called; IRQ0 stays pending (the BIOS default is not modeled).</summary>
    NotConfigured,

    /// <summary>B0:5B last set a value other than 0/1 (undocumented); IRQ0 stays pending.</summary>
    UnknownSetting,

    /// <summary>The setting variable could not be read; nothing was changed.</summary>
    InvalidState,
}

/// <summary>
/// The kernel's Pad/Card IRQ handler (PSX-SPX "PadCardIrq", priority chain 2): <see cref="Run"/> is the chain element,
/// <see cref="Handle"/> its IRQ0 auto-ack decision (B0:5B <c>ChangeClearPAD(int)</c>).
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx kernelbios): the handler services pad and card on VBlank
/// and B(5Bh) configures its automatic IRQ0 acknowledge; disabling it leaves
/// IRQ0 to a custom VBlank handler. CONFIRMED by two independent sources, not
/// psx-spx: 0 disables and 1 enables the acknowledge (OpenBIOS
/// <c>sio0Handler</c> acknowledges only IRQ0 when the flag is set; PSn00bSDK
/// calls <c>ChangeClearPAD(0)</c> to keep the kernel from acknowledging and
/// <c>ChangeClearPAD(1)</c> to restore it). The claim test, IRQ0 set in both
/// I_MASK and I_STAT, follows OpenBIOS <c>sio0Verifier</c> (INFERRED for the
/// retail BIOS). UNKNOWN: values other than 0/1 (OpenBIOS treats any non-zero
/// as enable; the retail BIOS is not documented); reported and left pending by
/// <see cref="Handle"/>, and a stop in <see cref="Run"/>. B0:15 and StartCARD2 set the
/// value to 1 when they enqueue the element (OpenBIOS <c>startPad</c>/<c>startCard</c>), so
/// "never configured" is not reachable through the services and also stops <see cref="Run"/>.
/// The acknowledge goes through the existing <see cref="IInterruptController"/> as an I_STAT
/// write-0-to-clear of bit 0, so no interrupt state is duplicated here.
/// </para>
/// <para>
/// <see cref="Run"/> follows OpenBIOS <c>sio0Handler</c> (sio0/driver.c; INFERRED for the retail BIOS): (1) pad stage — when the
/// pad-started flag (<see cref="BiosCardState.TryGetPadStarted"/>) is set and B0:15's <c>button_dest</c> is non-zero, the PAD_dr value
/// is stored at <c>[button_dest]</c>; (2) IRQ0 auto-ack; (3) card stage when StartCARD2 started the card — the EVENT_CARD
/// (<see cref="CardEventClass"/>) specs 0004h/8000h/0100h/0200h/2000h are undelivered, and with no request in progress the stage
/// ends; (4) no ReturnFromException, so the walk continues to priority 3 and the completion step. The PAD_dr value is
/// <c>FFFFFFFFh</c>: psx-spx B(16h) sets each pad's halfword to FFFFh for a disconnected pad and OpenBIOS
/// <c>readPadHighLevel</c> keeps its <c>FFFFFFFFh</c> preset when the pad does not answer; this Runtime connects no pad to SIO0
/// (INFERRED; a connected pad needs the real read here). The pads are not read over SIO0 and no SIO0 register is touched.
/// </para>
/// <para>
/// Not modelled, deliberately (S3, #712): the memory-card request/timeout state machine (port flip, request start, timeout,
/// EVENT_CARD 0100h delivery, the priority-1 card handler). No service creates a card request (A0:70 <c>_bu_init</c> is
/// unregistered), so the idle path is the only reachable one. A pending enabled IRQ7 needs that priority-1 handler and stops
/// the run before anything is written.
/// </para>
/// </remarks>
[Domain]
public static class BiosPadCardIrqHandler
{
    /// <summary>EVENT_CARD (psx-spx event-summary <c>F0000011h</c>).</summary>
    public const uint CardEventClass = 0xF0000011;

    /// <summary>The PAD_dr value of two disconnected pads (psx-spx B(16h): FFFFh per pad).</summary>
    public const uint DisconnectedPadButtons = 0xFFFFFFFF;

    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint ControllerBit = 1u << 7; // IRQ7, SIO0 / controller and memory card

    // OpenBIOS firstStageCardAction undelivers these EVENT_CARD specs at every card stage (8001h is not among them).
    private static readonly uint[] UndeliveredCardSpecs = [0x0004, 0x8000, 0x0100, 0x0200, 0x2000];

    /// <summary>
    /// Runs the priority-2 element once. <see cref="BiosExceptionChainStatus.Completed"/> when it is not enqueued, does not claim, or
    /// ran (it never returns from the exception); <see cref="BiosExceptionChainStatus.Unsupported"/> when the state it needs cannot be
    /// used or a case outside the model is reached. Readable state is preflighted before writes. Actual writes are not atomic.
    /// </summary>
    public static BiosExceptionChainResult Run(BiosExceptionChainContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Reader);
        ArgumentNullException.ThrowIfNull(context.Writer);
        ArgumentNullException.ThrowIfNull(context.Interrupts);

        if (!BiosPadState.TryGetState(context.Reader, out var enqueued, out var buttonDestination))
        {
            return Unsupported("the pad state variable (B0:15) could not be read");
        }

        var status = context.Interrupts.Status;
        var mask = context.Interrupts.Mask;
        if (!enqueued || (status & mask & VblankBit) == 0)
        {
            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        }

        var label = $"I_STAT=0x{status:X4}, I_MASK=0x{mask:X4}|priority 2 PadCardIrq";
        if (!BiosCardState.TryGetPadStarted(context.Reader, out var padStarted) ||
            !BiosCardState.TryGetStarted(context.Reader, out var cardStarted) ||
            !BiosPadCardAutoAck.TryGetSetting(context.Reader, out var setting, out var autoAck))
        {
            return Unsupported($"{label}|the card state or the B0:5B auto-ack variable could not be read");
        }

        if (setting == BiosPadCardAutoAckSetting.NotConfigured || autoAck > 1)
        {
            return Unsupported(
                $"{label}|the B0:5B auto-ack is {(setting == BiosPadCardAutoAckSetting.NotConfigured ? "not configured" : $"0x{autoAck:X}")} " +
                "(only 0 and 1 are documented; not guessed)");
        }

        if ((status & mask & ControllerBit) != 0)
        {
            return Unsupported(
                $"{label}|IRQ7 (SIO0 / memory card) is pending and enabled: its priority-1 card handler and the card request " +
                "state machine are not modelled (#712)");
        }

        // Inspect the card EvCB table before the PAD_dr write or IRQ0 acknowledge.
        // A later writer failure can still be partial: guest memory has no transactional API.
        if (cardStarted && !BiosEventControlBlocks.CanReadUndeliverTable(context.Reader))
        {
            return Unsupported($"{label}|EVENT_CARD table is unreadable or invalid before IRQ0 acknowledgement");
        }

        if (padStarted != 0 && buttonDestination != 0 &&
            !context.Writer.TryWrite(buttonDestination, BitConverter.GetBytes(DisconnectedPadButtons)))
        {
            return Unsupported($"{label}|button_dest 0x{buttonDestination:X8} is not writable");
        }

        if (Handle(context.Reader, context.Interrupts) is not (BiosPadCardIrqOutcome.Acknowledged or BiosPadCardIrqOutcome.LeftPending))
        {
            return Unsupported($"{label}|the IRQ0 auto-ack decision could not be made (the B0:5B variable became unreadable)");
        }

        if (cardStarted)
        {
            foreach (var spec in UndeliveredCardSpecs)
            {
                if (!BiosEventControlBlocks.Undeliver(context.Reader, context.Writer, CardEventClass, spec))
                {
                    return Unsupported($"{label}|EVENT_CARD 0x{CardEventClass:X8},{spec:X4} could not be undelivered (unusable EvCB table)");
                }
            }
        }

        return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
    }

    /// <summary>Runs one handler pass against the current I_STAT/I_MASK and the B0:5B setting.</summary>
    public static BiosPadCardIrqOutcome Handle(IGuestMemoryReader reader, IInterruptController interrupts)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(interrupts);

        if ((interrupts.Status & interrupts.Mask & VblankBit) == 0)
        {
            return BiosPadCardIrqOutcome.NotClaimed;
        }

        if (!BiosPadCardAutoAck.TryGetSetting(reader, out var setting, out var argument))
        {
            return BiosPadCardIrqOutcome.InvalidState;
        }

        if (setting == BiosPadCardAutoAckSetting.NotConfigured)
        {
            return BiosPadCardIrqOutcome.NotConfigured;
        }

        switch (argument)
        {
            case 0:
                return BiosPadCardIrqOutcome.LeftPending;
            case 1:
                interrupts.Acknowledge(~VblankBit);
                return BiosPadCardIrqOutcome.Acknowledged;
            default:
                return BiosPadCardIrqOutcome.UnknownSetting;
        }
    }

    private static BiosExceptionChainResult Unsupported(string detail) =>
        new(BiosExceptionChainStatus.Unsupported, detail);
}
