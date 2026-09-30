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
/// The kernel's Pad/Card IRQ handler (PSX-SPX "PadCardIrq", priority chain 2)
/// reduced to the one decision B0:5B <c>ChangeClearPAD(int)</c> controls:
/// whether IRQ0 (VBlank) is acknowledged after the handler runs.
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
/// as enable; the retail BIOS is not documented) and the setting before the
/// first B0:5B call (OpenBIOS StartPAD sets 1, but this Runtime does not model
/// StartPAD). Both are reported and leave IRQ0 pending rather than guessed.
/// </para>
/// <para>
/// Not modeled: the pad read and card state machine (SIO0 stays in the native
/// core) and enqueueing into the kernel exception handler's priority chains.
/// Calling <see cref="Handle"/> from the exception path is the kernel
/// ExceptionHandler's job (#651; the wiring is tracked in #661). The acknowledge goes through the existing
/// <see cref="IInterruptController"/> as an I_STAT write-0-to-clear of bit 0,
/// so no interrupt state is duplicated here.
/// </para>
/// </remarks>
[Domain]
public static class BiosPadCardIrqHandler
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;

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
}
