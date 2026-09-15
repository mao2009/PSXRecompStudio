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

    /// <summary>
    /// Reported when a guest transfer reached a BIOS trampoline vector that the
    /// Runtime could neither service nor turn into a jumpable guest target.
    /// </summary>
    public const string BiosDispatchDiagnosticCode = "BIOS_DISPATCH_UNRESOLVED";

    /// <summary>
    /// Reported when a patched jump-table entry names an address outside every
    /// translatable region (KSEG2 and above), so control cannot be transferred to it.
    /// </summary>
    public const string BiosUntranslatableTargetDiagnosticCode = "BIOS_PATCHED_TARGET_UNTRANSLATABLE";

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
            var status = core.Step();
            if (status != 0)
            {
                termination = RecompilerIrTerminationReason.Exception;
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
    /// This is the guest-jump-to-target mechanism Issue #362 asks for. The three
    /// <see cref="BiosServiceStatus"/> outcomes map onto the three things a real
    /// trampoline can do with a jump-table entry:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="BiosServiceStatus.PatchedTarget"/> — guest code owns this entry
    /// now, so control moves to the raw guest address it holds and the interpreter
    /// executes whatever MIPS lives there. The guest target returns through the
    /// <c>$ra</c> the original call site already linked, exactly as it would on
    /// hardware, because the trampoline never consumed the link register. No
    /// CPU semantics are reimplemented here: setting the PC hands the target back
    /// to the same interpreter that ran the caller.
    /// </description></item>
    /// <item><description>
    /// <see cref="BiosServiceStatus.Supported"/> — the entry still dispatches to
    /// HLE, so the service's return value lands in <c>$v0</c> and control returns
    /// to <c>$ra</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="BiosServiceStatus.Unsupported"/> — the Runtime can neither
    /// service the call nor name a guest target for it. Execution stops with the
    /// Runtime's own diagnostic rather than continuing past a call whose effects
    /// never happened.
    /// </description></item>
    /// </list>
    /// <para>
    /// A patched target that falls outside every translatable region is rejected
    /// before the PC moves. This adds no address policy of its own: it is the
    /// same <see cref="Ps1AddressTranslation.TryTranslate"/> boundary every guest
    /// memory access in this executor already passes through, applied to an
    /// address that is about to be fetched from.
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
        diagnosticCode = null;
        diagnosticMessage = null;

        // PS1 ABI: $t1 selects the function number, $a0-$a3 carry the argument
        // words, $v0 takes the return value, and $ra holds the call site's own
        // link — the trampoline is transparent to it.
        var identity = new BiosCallIdentity(
            family,
            (byte)(core.GetGpr((int)R3000aRegister.T1) & 0xFFu),
            core.Pc,
            [
                core.GetGpr((int)R3000aRegister.A0),
                core.GetGpr((int)R3000aRegister.A1),
                core.GetGpr((int)R3000aRegister.A2),
                core.GetGpr((int)R3000aRegister.A3),
            ]);

        var result = biosRuntime.Invoke(identity);
        switch (result.Status)
        {
            case BiosServiceStatus.PatchedTarget:
                var target = result.ReturnValue ?? 0u;
                if (!Ps1AddressTranslation.TryTranslate(target, out _))
                {
                    diagnosticCode = BiosUntranslatableTargetDiagnosticCode;
                    diagnosticMessage =
                        $"{identity.StableKey}: patched jump-table entry names guest address 0x{target:X8}, " +
                        "which falls outside every translatable region, so control cannot be transferred to it.";
                    return false;
                }

                core.Pc = target;
                return true;

            case BiosServiceStatus.Supported:
                if (result.ReturnValue is uint returnValue)
                {
                    core.SetGpr((int)R3000aRegister.V0, returnValue);
                }

                core.Pc = core.GetGpr((int)R3000aRegister.Ra);
                return true;

            default:
                diagnosticCode = result.Diagnostic?.Code ?? BiosDispatchDiagnosticCode;
                diagnosticMessage = result.Diagnostic?.ToStableString() ??
                    $"{identity.StableKey}: the Runtime returned {result.Status} with no diagnostic.";
                return false;
        }
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
