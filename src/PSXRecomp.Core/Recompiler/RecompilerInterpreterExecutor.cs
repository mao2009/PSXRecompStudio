using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

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

    public string Name => ExecutorName;

    public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        using var core = new PSXCoreWrapper();
        core.Reset();

        // Place the straight-line program in guest RAM at the entry address.
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

        // Bounded execution: retire at most StepBudget instructions.
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success;
        for (uint step = 0; step < fixture.StepBudget; step++)
        {
            var status = core.Step();
            if (status != 0)
            {
                termination = RecompilerIrTerminationReason.Exception;
                break;
            }
        }

        var gpr = new uint[RecompilerDifferentialFixture.GprCount];
        for (var i = 0; i < RecompilerDifferentialFixture.GprCount; i++)
        {
            gpr[i] = core.GetGpr(i);
        }

        var snapshot = new RecompilerStateSnapshot(
            gpr,
            hi: core.Hi,
            lo: core.Lo,
            pc: core.Pc,
            termination: termination);

        return RecompilerExecutionResult.Completed(snapshot);
    }

    // Mirrors PSXCpu::TranslateAddress for the KUSEG/KSEG0/KSEG1 ranges used by
    // test fixtures; see src/PSXRecomp.Native/src/psx_cpu.cpp.
    private static uint TranslateAddress(uint virtualAddress)
    {
        if (virtualAddress <= 0x7FFFFFFF) return virtualAddress;
        if (virtualAddress <= 0xBFFFFFFF) return virtualAddress & 0x1FFFFFFF;
        throw new ArgumentOutOfRangeException(nameof(virtualAddress), "Fixture entry PC must fall in KUSEG/KSEG0/KSEG1.");
    }
}
