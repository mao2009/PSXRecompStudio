using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// The interpreter side of the differential harness. Runs the fixture through
/// the existing native R3000A interpreter (<c>PSXCoreWrapper</c>, backed by
/// <c>PSXCpu</c>), which fetches and executes the real MIPS words from guest
/// memory. It shares the fixture's semantics at the MIPS instruction level and
/// does not reimplement CPU semantics in this class.
/// </summary>
[Domain]
public sealed class RecompilerInterpreterExecutor : IRecompilerExecutor
{
    public const string ExecutorName = "interpreter-native";

    /// <inheritdoc cref="BiosVectorDispatch.UnresolvedDiagnosticCode" />
    public const string BiosDispatchDiagnosticCode = BiosVectorDispatch.UnresolvedDiagnosticCode;

    /// <inheritdoc cref="BiosVectorDispatch.UntranslatableTargetDiagnosticCode" />
    public const string BiosUntranslatableTargetDiagnosticCode = BiosVectorDispatch.UntranslatableTargetDiagnosticCode;

    /// <inheritdoc cref="BiosVectorDispatch.ArityExceedsRegisterBoundaryDiagnosticCode" />
    public const string BiosServiceArityExceedsRegisterBoundaryDiagnosticCode =
        BiosVectorDispatch.ArityExceedsRegisterBoundaryDiagnosticCode;

    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;

    /// <summary>
    /// Creates an executor with no BIOS boundary: a transfer to an A0/B0/C0
    /// trampoline vector simply leaves the program, exactly as before this
    /// executor knew about BIOS calls at all.
    /// </summary>
    public RecompilerInterpreterExecutor()
    {
    }

    /// <summary>
    /// Creates an executor that traps guest transfers to the A0/B0/C0 trampoline
    /// vectors and dispatches them through an <see cref="IBiosRuntime"/>.
    /// </summary>
    /// <param name="biosRuntimeFactory">
    /// Builds the Runtime over this run's guest memory. The reader and writer
    /// handed to it are bound to the very core that executes the fixture, which
    /// is why a constructed instance cannot be injected directly: the Runtime
    /// must observe the same jump-table bytes the guest patches, and the core
    /// only exists for the duration of one <see cref="Execute"/> call. It is
    /// invoked after the fixture's initial memory and program words are in
    /// place, so an entry the fixture pre-patches is already visible to the
    /// Runtime's own seeding pass and is preserved by it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="biosRuntimeFactory"/> is null.</exception>
    public RecompilerInterpreterExecutor(Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory)
    {
        ArgumentNullException.ThrowIfNull(biosRuntimeFactory);
        _biosRuntimeFactory = biosRuntimeFactory;
    }

    public string Name => ExecutorName;

    public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        using var core = new PSXCoreWrapper();
        core.Reset();

        // Apply the initial guest memory first (byte writes, in fixture order).
        // PSXMemory addresses are physical, so virtual fixture addresses are
        // translated here just as the executed loads/stores translate them. The
        // program words are written afterwards so the code image wins at any
        // address InitialMemory overlaps — mirroring the generated host, where the
        // code is baked into the compiled blocks and initial memory only fills the
        // surrounding RAM (Issue #209, CodeRabbit finding 1).
        foreach (var item in fixture.InitialMemory)
        {
            core.WriteMemory8(TranslateAddress(item.Address), item.Value);
        }

        // Place the program in guest RAM at the entry address.
        var ramOffset = TranslateAddress(fixture.EntryPc);
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            core.WriteMemory32(ramOffset + unchecked((uint)i * 4u), fixture.Instructions[i]);
        }

        // Apply the initial architectural state.
        for (var i = 0; i < RecompilerDifferentialFixture.GprCount; i++)
        {
            core.SetGpr(i, fixture.InitialGpr[i]);
        }
        core.Hi = fixture.InitialHi;
        core.Lo = fixture.InitialLo;
        core.Pc = fixture.EntryPc;

        // Built only now: the Runtime reads and seeds jump-table entries through
        // this core's memory, so it must be constructed after the fixture's
        // initial memory and program words have landed.
        var biosRuntime = _biosRuntimeFactory?.Invoke(
            new GuestMemoryReader(core.ReadMemory8),
            new GuestMemoryWriter(core.WriteMemory8));

        // Bounded execution: retire at most ReferenceStepBudget instructions.
        // Fixtures with control transfer retire more MIPS instructions than the
        // host retires fused blocks, which is why the reference has its own budget.
        // Stop stepping as soon as the PC leaves the program: the host's dispatch
        // returns Success when the PC matches no block, so a surplus reference
        // budget must not keep the interpreter executing the zeroed RAM past the
        // end of the program (which would move its PC off the host's).
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success;
        string? diagnosticCode = null;
        string? diagnosticMessage = null;
        RecompilerExceptionState? exceptionState = null;
        var pcTrace = new List<uint>((int)fixture.ReferenceStepBudget);
        for (uint step = 0; step < fixture.ReferenceStepBudget; step++)
        {
            // The BIOS trampoline vectors live outside the fixture's program, so
            // this check must precede the program-bound check below or a BIOS
            // call would simply end the run. Dispatching one costs a step from
            // the same budget that bounds ordinary instructions, so a program
            // that loops on a BIOS call is bounded exactly like any other loop.
            if (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(core.Pc, out var family))
            {
                pcTrace.Add(core.Pc);
                if (!TryDispatchBiosVector(core, biosRuntime, family, out diagnosticCode, out diagnosticMessage))
                {
                    termination = RecompilerIrTerminationReason.UnresolvedIndirectFlow;
                    break;
                }

                continue;
            }

            if (!PcWithinProgram(core.Pc, fixture))
            {
                break;
            }

            pcTrace.Add(core.Pc);
            // Step() reports only a native-call failure; a guest exception leaves
            // it 0 and moves the PC to the exception vector. Asking Step() alone
            // (as this did before Issue #377) meant a faulting fixture — a GTE/
            // COP2, LWC2 or SWC2 word raising CpU, or any RI/AdEL/AdES — simply
            // left the program bounds on the next iteration and the reference
            // oracle reported Success: a faulted run indistinguishable from a
            // clean one, which is exactly the false-match risk this oracle exists
            // to rule out. Exception is a behavioral field in RecompilerStateDiff,
            // so reporting it makes any such fixture a hard mismatch.
            var status = core.Step();
            if (status != 0 || core.ExceptionRaised)
            {
                termination = RecompilerIrTerminationReason.Exception;
                // Exception details are populated only for the trap exceptions
                // this lowering stage models (currently BREAK, Excode 0x09) — a
                // deliberate scope bound: a GTE/CpU, AdEL/AdES or Overflow fault
                // still carries default exception state so the existing
                // fault-classification tests keep their single difference
                // (Issue #481 design, approach c).
                if (status == 0 && core.ExceptionRaised && core.ExceptionCode == MipsToIrLowerer.BreakExcode)
                {
                    exceptionState = new RecompilerExceptionState(
                        isRaised: true,
                        code: core.ExceptionCode,
                        faultPc: core.ExceptionFaultPc,
                        inDelaySlot: core.ExceptionInDelaySlot);
                }

                break;
            }
        }

        // The budget exhausted while the CPU is still inside the program means the
        // run was cut short rather than completed: the interpreter reports the
        // same ExecutionBudgetExceeded reason the generated dispatch reports. A PC
        // parked on a BIOS trampoline vector counts as still running for the same
        // reason — the pending call had not been dispatched yet.
        var stillRunning = PcWithinProgram(core.Pc, fixture) ||
                           (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(core.Pc, out _));
        if (termination == RecompilerIrTerminationReason.Success && stillRunning)
        {
            termination = RecompilerIrTerminationReason.ExecutionBudgetExceeded;
        }

        var gpr = new uint[RecompilerDifferentialFixture.GprCount];
        for (var i = 0; i < RecompilerDifferentialFixture.GprCount; i++)
        {
            gpr[i] = core.GetGpr(i);
        }

        var memory = new List<RecompilerMemoryObservation>(fixture.MemoryWindow.Count);
        foreach (var address in fixture.MemoryWindow)
        {
            memory.Add(new RecompilerMemoryObservation(
                address,
                core.ReadMemory8(TranslateAddress(address)),
                width: 1,
                RecompilerMemoryAccessKind.Read));
        }

        var snapshot = new RecompilerStateSnapshot(
            gpr,
            hi: core.Hi,
            lo: core.Lo,
            pc: core.Pc,
            exception: exceptionState ?? new RecompilerExceptionState(),
            termination: termination,
            memory: memory,
            pcTrace: pcTrace);

        // The snapshot is always produced — the executor mechanism itself did not
        // fail — but an unresolved BIOS dispatch carries its diagnostic alongside
        // it, so a caller can tell which call could not be dispatched and why.
        return new RecompilerExecutionResult(
            RecompilerExecutionStatus.Completed, snapshot, diagnosticCode, diagnosticMessage);
    }

    /// <summary>
    /// Dispatches one guest transfer to a BIOS trampoline vector, and moves the PC
    /// to wherever control should continue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The BIOS semantics themselves live in <see cref="BiosVectorDispatch"/>,
    /// shared with the generated host (Issue #362), so the two execution paths
    /// cannot disagree about what a BIOS call means. This method only applies the
    /// outcome to a live interpreter core.
    /// </para>
    /// <para>
    /// No CPU semantics are reimplemented here: setting the PC hands the patched
    /// target back to the same interpreter that ran the caller, and the target
    /// returns through the <c>$ra</c> the original call site already linked,
    /// exactly as it would on hardware.
    /// </para>
    /// </remarks>
    /// <returns>True when control was transferred; false when the run must stop.</returns>
    private static bool TryDispatchBiosVector(
        PSXCoreWrapper core,
        IBiosRuntime biosRuntime,
        BiosCallFamily family,
        out string? diagnosticCode,
        out string? diagnosticMessage)
    {
        var gpr = new uint[RecompilerDifferentialFixture.GprCount];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = core.GetGpr(i);
        }

        var outcome = BiosVectorDispatch.Dispatch(biosRuntime, family, gpr);
        if (!outcome.ContinueExecution)
        {
            diagnosticCode = outcome.DiagnosticCode;
            diagnosticMessage = outcome.DiagnosticMessage;
            return false;
        }

        diagnosticCode = null;
        diagnosticMessage = null;
        if (outcome.ReturnValue is uint returnValue)
        {
            core.SetGpr((int)R3000aRegister.V0, returnValue);
        }

        // A translatable patched target is jumped to verbatim — a KUSEG alias
        // stays a KUSEG alias, never normalised — because the interpreter can
        // fetch from any translatable address.
        core.Pc = outcome.NextPc;
        return true;
    }

    private static bool PcWithinProgram(uint pc, RecompilerDifferentialFixture fixture)
    {
        var programStart = fixture.EntryPc;
        var programEnd = unchecked(fixture.EntryPc + (uint)fixture.Instructions.Count * 4u);
        if (programEnd < programStart)
        {
            return false;
        }

        return pc >= programStart && pc < programEnd;
    }

    // Delegates to the shared Ps1AddressTranslation helper.
    private static uint TranslateAddress(uint virtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(virtualAddress, out var physical))
        {
            throw new ArgumentOutOfRangeException(nameof(virtualAddress), "Fixture entry PC must fall in KUSEG/KSEG0/KSEG1.");
        }
        return physical;
    }
}
