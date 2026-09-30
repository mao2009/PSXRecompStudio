using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>Outcome of <see cref="BiosExceptionCompletion.Complete"/>.</summary>
[Domain]
public enum BiosExceptionCompletionStatus : byte
{
    /// <summary>The B0:19 hook was entered: its saved registers are restored, <c>$v0 = 1</c>, control moves to the returned PC.</summary>
    HookEntered,

    /// <summary>No hook is registered, so the default Exit applies: the caller performs B0:17 ReturnFromException. Nothing was changed.</summary>
    ReturnFromException,

    /// <summary>The hook state could not be read in full; nothing was changed.</summary>
    InvalidState,
}

/// <summary>
/// The exception-completion boundary of the kernel exception handler
/// (C0:06 ExceptionHandler) and the saved-state restore of
/// B0:17 ReturnFromException (PSX-SPX kernelbios, interrupt-exception-handling
/// and control-blocks).
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx): "The hook function is executed only if the
/// ExceptionHandler has been fully executed"; a chain element that calls
/// ReturnFromException skips it. The default "Exit" structure applied by B0:18
/// has ReturnFromException as its PC. So the completion step is: hook
/// registered → enter it; otherwise → ReturnFromException. <see cref="Complete"/>
/// is the only place the hook fires. It must be called by the kernel exception
/// handler after the priority chains ran to the end, never at IRQ time and never
/// after an early ReturnFromException. That handler is not modelled yet, so
/// nothing in the execution paths calls this yet (#662).
/// </para>
/// <para>
/// ReturnFromException restores R1-R31 except R26/k0, HI, LO, SR and PC from
/// the current TCB, reached through the Process Control Block pointer in the
/// "table of tables" at <see cref="ProcessControlBlockPointerAddress"/>. The
/// TCB layout is psx-spx's (control-blocks); nothing here is a Runtime choice.
/// The returned SR is the saved value: the kernel then executes RFE, and RFE is
/// the CPU's to perform, so the caller applies it through the CPU rather than
/// this type re-implementing it. B0:17 is not a registered service yet (#664).
/// </para>
/// </remarks>
[Domain]
public static class BiosExceptionCompletion
{
    /// <summary>Guest address of the table-of-tables entry holding the PCB address (psx-spx: 00000108h).</summary>
    public const uint ProcessControlBlockPointerAddress = 0x00000108;

    // TCB offsets (psx-spx control-blocks): 08h r0..r31, 88h epc, 8Ch hi, 90h lo,
    // 94h sr. The restore reads 08h..97h in one piece.
    private const uint TcbRegistersOffset = 0x08;
    private const int TcbRestoreSize = 0x98 - 0x08;
    private const int EpcOffset = 0x88 - 0x08;
    private const int HiOffset = 0x8C - 0x08;
    private const int LoOffset = 0x90 - 0x08;
    private const int SrOffset = 0x94 - 0x08;

    /// <summary>
    /// The completion step of a fully executed exception handler: enters the
    /// registered B0:19 hook, or reports that the default Exit
    /// (ReturnFromException) applies. <paramref name="gpr"/> and
    /// <paramref name="pc"/> change only on <see cref="BiosExceptionCompletionStatus.HookEntered"/>.
    /// </summary>
    public static BiosExceptionCompletionStatus Complete(IGuestMemoryReader reader, uint[] gpr, out uint pc) =>
        BiosExceptionHook.TryComplete(reader, gpr, out pc) switch
        {
            BiosExceptionHookStatus.Resolved => BiosExceptionCompletionStatus.HookEntered,
            BiosExceptionHookStatus.NotRegistered => BiosExceptionCompletionStatus.ReturnFromException,
            _ => BiosExceptionCompletionStatus.InvalidState,
        };

    /// <summary>
    /// B0:17 ReturnFromException's restore: reads the current TCB in full first
    /// and only then writes R1-R31 (except k0) into <paramref name="gpr"/> and
    /// reports HI, LO, the saved SR and the saved EPC as the PC. False when the
    /// PCB, the TCB pointer or the TCB cannot be read, or a pointer is 0; then
    /// nothing is changed.
    /// </summary>
    public static bool TryReturnFromException(
        IGuestMemoryReader reader, uint[] gpr, out uint hi, out uint lo, out uint sr, out uint pc)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(gpr);
        if (gpr.Length != 32)
        {
            throw new ArgumentException("A full 32-entry register file is required.", nameof(gpr));
        }

        hi = lo = sr = pc = 0;
        if (!TryReadPointer(reader, ProcessControlBlockPointerAddress, out var pcb) ||
            !TryReadPointer(reader, pcb, out var tcb) ||
            tcb > uint.MaxValue - TcbRegistersOffset)
        {
            return false;
        }

        Span<byte> saved = stackalloc byte[TcbRestoreSize];
        if (!reader.TryRead(tcb + TcbRegistersOffset, saved))
        {
            return false;
        }

        for (var r = 1; r < gpr.Length; r++)
        {
            if (r != (int)R3000aRegister.K0)
            {
                gpr[r] = BitConverter.ToUInt32(saved.Slice(r * sizeof(uint), sizeof(uint)));
            }
        }

        pc = BitConverter.ToUInt32(saved.Slice(EpcOffset, sizeof(uint)));
        hi = BitConverter.ToUInt32(saved.Slice(HiOffset, sizeof(uint)));
        lo = BitConverter.ToUInt32(saved.Slice(LoOffset, sizeof(uint)));
        sr = BitConverter.ToUInt32(saved.Slice(SrOffset, sizeof(uint)));
        return true;
    }

    private static bool TryReadPointer(IGuestMemoryReader reader, uint address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        value = reader.TryRead(address, bytes) ? BitConverter.ToUInt32(bytes) : 0;
        return value != 0;
    }
}
