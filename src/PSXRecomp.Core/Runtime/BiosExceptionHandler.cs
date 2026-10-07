using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The CPU's exception state at the general exception vector. The CPU already
/// set EPC/CAUSE/SR; this is a read of that state, never a second copy of it.
/// HI/LO are the interrupted context's, which the kernel saves with the GPRs.
/// </summary>
[Domain]
public readonly record struct BiosExceptionContext(uint Epc, uint Cause, uint Sr, uint Hi, uint Lo)
{
    /// <summary>CAUSE.ExcCode (bits 2-6).</summary>
    public uint Excode => (Cause >> 2) & 0x1F;
}

/// <summary>How the priority chains ended (see <see cref="BiosExceptionChain"/>).</summary>
[Domain]
public enum BiosExceptionChainStatus : byte
{
    /// <summary>Every chain element ran; the handler then performs its completion step.</summary>
    Completed,

    /// <summary>An element called ReturnFromException; the completion step (the hook) is skipped.</summary>
    ReturnedFromException,

    /// <summary>An element the Runtime cannot run was reached; the run must stop.</summary>
    Unsupported,
}

/// <summary>Result of <see cref="BiosExceptionChain"/>.</summary>
[Domain]
public readonly record struct BiosExceptionChainResult(BiosExceptionChainStatus Status, string? Detail = null);

/// <summary>
/// Everything a kernel priority-chain element needs: the guest-visible kernel state (read and write),
/// the existing interrupt controller, and the exception the CPU took. No engine or CPU object is
/// exposed, so a chain element cannot become a second CPU semantics; the CPU side stays the execution
/// path's (<see cref="BiosExceptionHandlerOutcome"/>).
/// </summary>
[Domain]
public readonly record struct BiosExceptionChainContext(
    IGuestMemoryReader Reader,
    IGuestMemoryWriter Writer,
    IInterruptController Interrupts,
    BiosExceptionContext Exception);

/// <summary>
/// Walks the kernel's interrupt priority chains (ExCB, psx-spx control-blocks).
/// The chain is a seam: the default kernel elements (timer/VBlank #658/#660,
/// Pad/Card #661) and guest-enqueued elements (C0:02 SysEnqIntRP) plug in here
/// as the Runtime models them. Elements act through the existing
/// <see cref="IInterruptController"/> and guest memory, so no interrupt state is duplicated.
/// </summary>
[Domain]
public delegate BiosExceptionChainResult BiosExceptionChain(BiosExceptionChainContext context);

/// <summary>Outcome of <see cref="BiosExceptionHandler.Handle"/>.</summary>
/// <param name="Handled">True when the handler ran to its end and the caller applies the state below; false when the run must stop with the diagnostic.</param>
/// <param name="NextPc">Where the CPU continues. Meaningful only when handled.</param>
/// <param name="Gpr">The full register file to continue with (a copy; the input is never mutated).</param>
/// <param name="Hi">HI to continue with.</param>
/// <param name="Lo">LO to continue with.</param>
/// <param name="RestoredSr">
/// Non-null after ReturnFromException: the saved SR. The CPU writes it and then
/// performs RFE (the CPU's to apply, not re-implemented here). Null when the
/// hook was entered: SR is left as the CPU has it.
/// </param>
/// <param name="DiagnosticCode">Stable diagnostic code when not handled.</param>
/// <param name="DiagnosticMessage">Human-readable diagnostic when not handled.</param>
[Domain]
public sealed record BiosExceptionHandlerOutcome(
    bool Handled,
    uint NextPc,
    uint[] Gpr,
    uint Hi,
    uint Lo,
    uint? RestoredSr,
    string? DiagnosticCode,
    string? DiagnosticMessage)
{
    /// <summary>
    /// This outcome as the shared CPU-state replacement, so an execution path applies the
    /// kernel exception completion and a BIOS service that replaces the state — B0:17,
    /// #664 — through one implementation instead of two that can restore different subsets.
    /// Meaningful only when <paramref name="Handled"/> is true.
    /// </summary>
    public BiosCpuStateMutation CpuState => new(Gpr, Hi, Lo, RestoredSr, NextPc);
}

/// <summary>
/// The BIOS-less entry of the kernel exception handler (C0:06 ExceptionHandler),
/// stated once for every execution path (Issue #662). Real BIOS places a stub at
/// <see cref="GeneralExceptionVector"/> that jumps to C0:06; a BIOS-less run has
/// no code there, so an execution path that finds the vector unpopulated hands
/// the CPU's exception state to <see cref="Handle"/> instead.
/// </summary>
/// <remarks>
/// <para>
/// The handler follows PSX-SPX interrupt-exception-handling: save the
/// interrupted context into the current TCB, run the priority chains, and only
/// when they ran to the end perform the completion step
/// (<see cref="BiosExceptionCompletion.Complete"/>: the B0:19 hook, else the
/// default Exit = B0:17 ReturnFromException through
/// <see cref="BiosExceptionCompletion.TryReturnFromException"/>). A chain element
/// that returns from the exception itself skips the hook.
/// </para>
/// <para>
/// The execution path owns the CPU side (it applies <see cref="BiosExceptionHandlerOutcome"/>
/// and the RFE); this type holds no CPU state. The saved context lives in guest
/// RAM (the TCB), where a later B0:17 (#664) reads it.
/// </para>
/// <para>
/// BIOS-less kernel state: a real kernel builds the PCB/TCB at boot (SYSTEM.CNF
/// <c>TCB =</c>); a BIOS-less run has none, <c>[0x108] == 0</c>. Only when that
/// pointer is 0 does the first exception seed one PCB and one zeroed TCB at
/// <see cref="SeededPcbAddress"/>/<see cref="SeededTcbAddress"/> — this Runtime's
/// own choice inside the kernel-reserved page, as for the hook pointer
/// (<see cref="BiosExceptionHook.PointerAddress"/>); the real locations are not
/// documented. This is Runtime-reserved kernel state: a future ExCB/EvCB/TCB allocator must take
/// these addresses from one SSOT so it cannot collide with them. The TCB holds only what this handler saves; no thread, status or
/// ExCB content is invented. A pointer that is non-zero but unusable is guest
/// state and fails closed. The ExCB (<c>[0x100]</c>) is not modelled: the chain
/// is <see cref="BiosExceptionChain"/>.
/// </para>
/// </remarks>
[Domain]
public static class BiosExceptionHandler
{
    /// <summary>The general exception vector with SR.BEV = 0 (docs/cpu/exceptions.md).</summary>
    public const uint GeneralExceptionVector = 0x80000080;

    /// <summary>Reported when the exception is not a hardware INT (SYSCALL is the execution path's, Issue #663).</summary>
    public const string UnsupportedExceptionDiagnosticCode = "BIOS_EXCEPTION_UNSUPPORTED";

    /// <summary>Reported when the priority chain reaches an element the Runtime does not model.</summary>
    public const string ChainUnsupportedDiagnosticCode = "BIOS_EXCEPTION_CHAIN_UNSUPPORTED";

    /// <summary>Reported when the PCB/TCB or the completion state cannot be used; nothing is guessed.</summary>
    public const string KernelStateInvalidDiagnosticCode = "BIOS_EXCEPTION_KERNEL_STATE_INVALID";

    /// <summary>Where a BIOS-less run's PCB is seeded (Runtime's own choice, see remarks).</summary>
    public const uint SeededPcbAddress = 0x0000E000;

    /// <summary>Where a BIOS-less run's TCB is seeded (Runtime's own choice, see remarks).</summary>
    public const uint SeededTcbAddress = 0x0000E100;

    private const uint InterruptExcode = 0x00;
    private const uint VectorStubPhysicalAddress = 0x80;
    private const int VectorStubSize = 4 * sizeof(uint); // the real BIOS stub: 4 instructions
    private const int TcbSize = 0xC0;
    private const uint TcbRegistersOffset = 0x08;
    private const uint TcbCauseOffset = 0x98; // psx-spx control-blocks: 88h epc, 8Ch hi, 90h lo, 94h sr, 98h cause
    private const int EpcOffset = 0x88 - 0x08;
    private const int HiOffset = 0x8C - 0x08;
    private const int LoOffset = 0x90 - 0x08;
    private const int SrOffset = 0x94 - 0x08;
    private const int SavedStateSize = 0x98 - 0x08;

    /// <summary>
    /// Whether the guest left the vector unpopulated (all four stub words zero), i.e. the kernel handler
    /// is this Runtime's. A guest that installed its own code there keeps the guest-owned path.
    /// An unreadable vector is not claimed.
    /// </summary>
    public static bool IsKernelVector(IGuestMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Span<byte> stub = stackalloc byte[VectorStubSize];
        return reader.TryRead(VectorStubPhysicalAddress, stub) && !stub.ContainsAnyExcept((byte)0);
    }

    /// <summary>
    /// Runs the kernel exception handler once against the CPU's exception state. Nothing is written to guest
    /// memory unless the exception is supported and the chain walk has begun.
    /// </summary>
    /// <param name="gpr">The interrupted context's full register file; never mutated.</param>
    /// <param name="chain">The priority-chain walk; null uses <see cref="DefaultChain"/>.</param>
    public static BiosExceptionHandlerOutcome Handle(
        IGuestMemoryReader reader,
        IGuestMemoryWriter writer,
        IInterruptController interrupts,
        uint[] gpr,
        BiosExceptionContext context,
        BiosExceptionChain? chain = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(gpr);
        if (gpr.Length != 32)
        {
            throw new ArgumentException("A full 32-entry register file is required.", nameof(gpr));
        }

        if (context.Excode != InterruptExcode)
        {
            return Stop(gpr, context, UnsupportedExceptionDiagnosticCode,
                $"{UnsupportedExceptionDiagnosticCode}|Excode=0x{context.Excode:X2}|only a hardware INT enters the kernel exception handler here; {Describe(context)}.");
        }

        if (!TrySaveContext(reader, writer, gpr, context))
        {
            return Stop(gpr, context, KernelStateInvalidDiagnosticCode,
                $"{KernelStateInvalidDiagnosticCode}|[0x108]->PCB->TCB|the current TCB could not be used to save the interrupted context; {Describe(context)}.");
        }

        var chainResult = (chain ?? DefaultChain)(new BiosExceptionChainContext(reader, writer, interrupts, context));
        if (chainResult.Status == BiosExceptionChainStatus.Unsupported)
        {
            return Stop(gpr, context, ChainUnsupportedDiagnosticCode,
                $"{ChainUnsupportedDiagnosticCode}|{chainResult.Detail}|{Describe(context)}.");
        }

        var work = (uint[])gpr.Clone();
        if (chainResult.Status == BiosExceptionChainStatus.Completed)
        {
            switch (BiosExceptionCompletion.Complete(reader, work, out var hookPc))
            {
                case BiosExceptionCompletionStatus.HookEntered:
                    return new BiosExceptionHandlerOutcome(true, hookPc, work, context.Hi, context.Lo, null, null, null);
                case BiosExceptionCompletionStatus.InvalidState:
                    return Stop(gpr, context, KernelStateInvalidDiagnosticCode,
                        $"{KernelStateInvalidDiagnosticCode}|B0:19 hook buffer|the registered hook buffer could not be read; {Describe(context)}.");
            }
        }

        // The default Exit, or a chain element's own ReturnFromException.
        if (!BiosExceptionCompletion.TryReturnFromException(reader, work, out var hi, out var lo, out var sr, out var pc))
        {
            return Stop(gpr, context, KernelStateInvalidDiagnosticCode,
                $"{KernelStateInvalidDiagnosticCode}|ReturnFromException|the saved TCB state could not be read back; {Describe(context)}.");
        }

        return new BiosExceptionHandlerOutcome(true, pc, work, hi, lo, sr, null, null);
    }

    /// <summary>
    /// The chain as the Runtime models it today: priority 1 is <see cref="BiosTimerVblankIrqHandler"/> (#658) and
    /// ends the walk when a flag-1 element returns from the exception. Priority 2 (<c>PadCardIrq</c>) is skipped:
    /// it is enqueued only by StartPAD2/StartCARD, which the Runtime does not model, so it is empty (#661). Priority 3
    /// is <see cref="BiosDefaultInterruptHandler"/> (#690): it never acknowledges by default and never returns from
    /// the exception, so a chain that reaches it has run to the end and the completion step (the B0:19 hook, else the
    /// default Exit) follows; a pending enabled IRQ outside what DefInt models stops the run instead of pretending a
    /// handler ran. A claimed timer/VBlank source whose root-counter events cannot be delivered (#660: an EvCB table
    /// exists) stops at priority 1.
    /// </summary>
    public static BiosExceptionChainResult DefaultChain(BiosExceptionChainContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Interrupts);
        var priority1 = BiosTimerVblankIrqHandler.Run(context);
        if (priority1.Status != BiosExceptionChainStatus.Completed)
        {
            return priority1;
        }

        return BiosDefaultInterruptHandler.Run(context);
    }

    private static string Describe(BiosExceptionContext c) =>
        $"EPC=0x{c.Epc:X8}, CAUSE=0x{c.Cause:X8}, SR=0x{c.Sr:X8}";

    private static BiosExceptionHandlerOutcome Stop(uint[] gpr, BiosExceptionContext context, string code, string message) =>
        new(false, 0, gpr, context.Hi, context.Lo, null, code, message);

    /// <summary>Saves the interrupted context into the current TCB, seeding the BIOS-less PCB/TCB first when <c>[0x108] == 0</c>.</summary>
    private static bool TrySaveContext(
        IGuestMemoryReader reader, IGuestMemoryWriter writer, uint[] gpr, BiosExceptionContext context)
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];
        if (!reader.TryRead(BiosExceptionCompletion.ProcessControlBlockPointerAddress, word))
        {
            return false;
        }

        uint tcb;
        if (BitConverter.ToUInt32(word) == 0)
        {
            tcb = SeededTcbAddress;
            if (!writer.TryWrite(tcb, new byte[TcbSize]) ||
                !writer.TryWrite(SeededPcbAddress, BitConverter.GetBytes(tcb)) ||
                !writer.TryWrite(BiosExceptionCompletion.ProcessControlBlockPointerAddress, BitConverter.GetBytes(SeededPcbAddress)))
            {
                return false;
            }
        }
        else
        {
            var pcb = BitConverter.ToUInt32(word);
            if (!reader.TryRead(pcb, word) || (tcb = BitConverter.ToUInt32(word)) == 0 || tcb > uint.MaxValue - TcbCauseOffset - sizeof(uint))
            {
                return false;
            }
        }

        var saved = new byte[SavedStateSize];
        for (var r = 1; r < gpr.Length; r++)
        {
            BitConverter.TryWriteBytes(saved.AsSpan(r * sizeof(uint)), gpr[r]);
        }

        BitConverter.TryWriteBytes(saved.AsSpan(EpcOffset), context.Epc);
        BitConverter.TryWriteBytes(saved.AsSpan(HiOffset), context.Hi);
        BitConverter.TryWriteBytes(saved.AsSpan(LoOffset), context.Lo);
        BitConverter.TryWriteBytes(saved.AsSpan(SrOffset), context.Sr);
        return writer.TryWrite(tcb + TcbRegistersOffset, saved) &&
               writer.TryWrite(tcb + TcbCauseOffset, BitConverter.GetBytes(context.Cause));
    }
}
