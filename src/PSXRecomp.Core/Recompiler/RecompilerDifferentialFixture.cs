using System.Collections.ObjectModel;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// A common input model that both the interpreter executor and the generated-host
/// executor consume, so that a single fixture drives both sides of the
/// differential comparison (Issue #211).
/// </summary>
/// <remarks>
/// <para>
/// The fixture holds the encoded MIPS words of a straight-line GPR program, the
/// address its first instruction lives at, the initial architectural state, and
/// the execution budget. Stage A is GPR-only: instructions are placed in RAM at
/// <see cref="EntryPc"/> and step sequentially; memory / load-delay / control-flow
/// are out of scope but the model stays forward-compatible (initial state carries
/// HI/LO, and <see cref="StepBudget"/> bounds execution).
/// </para>
/// </remarks>
[Domain]
public sealed record RecompilerDifferentialFixture
{
    public const int GprCount = 32;

    public RecompilerDifferentialFixture(
        string name,
        IEnumerable<uint> encodedInstructions,
        uint entryPc,
        uint stepBudget,
        IEnumerable<uint>? initialGpr = null,
        uint initialHi = 0,
        uint initialLo = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A fixture needs a name.", nameof(name));
        Instructions = new ReadOnlyCollection<uint>(encodedInstructions.ToArray());
        if (Instructions.Count == 0) throw new ArgumentException("A fixture needs at least one instruction.", nameof(encodedInstructions));
        if (stepBudget == 0) throw new ArgumentOutOfRangeException(nameof(stepBudget));
        EntryPc = entryPc;
        StepBudget = stepBudget;

        var gpr = initialGpr?.ToArray() ?? new uint[GprCount];
        if (gpr.Length != GprCount) throw new ArgumentException("Initial GPR must contain exactly 32 values.", nameof(initialGpr));
        gpr[0] = 0;
        InitialGpr = new ReadOnlyCollection<uint>(gpr);
        InitialHi = initialHi;
        InitialLo = initialLo;
    }

    public string Name { get; }
    public IReadOnlyList<uint> Instructions { get; }
    public uint EntryPc { get; }
    public uint StepBudget { get; }
    public IReadOnlyList<uint> InitialGpr { get; }
    public uint InitialHi { get; }
    public uint InitialLo { get; }

    /// <summary>Returns the guest PC of the <paramref name="index"/>-th instruction.</summary>
    public uint PcOfInstruction(int index) => EntryPc + unchecked((uint)index * 4u);
}
