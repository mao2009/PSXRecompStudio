using System.Collections.ObjectModel;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// A single ordered byte write applied to guest RAM before a fixture executes.
/// Applying them in order makes an intentionally overlapping initial state
/// deterministic rather than compiler-dependent.
/// </summary>
[Domain]
public sealed record RecompilerInitialMemoryItem(uint Address, byte Value);

/// <summary>
/// A common input model that both the interpreter executor and the generated-host
/// executor consume, so that a single fixture drives both sides of the
/// differential comparison (Issue #211).
/// </summary>
/// <remarks>
/// <para>
/// The fixture holds the encoded MIPS words of a program, the address its first
/// instruction lives at, the initial architectural state, and the execution
/// budget. <see cref="StepBudget"/> bounds the generated-host dispatch (the unit
/// is retired host blocks); <see cref="ReferenceStepBudget"/> independently bounds
/// the interpreter (the unit is retired MIPS instructions) when the two differ —
/// a control-transfer instruction and its delay slot lower to a single fused block
/// on the host but retire as two instructions on the interpreter, so a shared
/// budget only coincides for straight-line code. When the reference budget is
/// omitted it defaults to <see cref="StepBudget"/>.
/// </para>
/// <para>
/// <see cref="InitialMemory"/> populates guest RAM at both sides before execution,
/// with the program words written after the initial memory bytes so the code image
/// wins at any overlapping address — matching the host, where the code is baked
/// into the generated blocks rather than loaded into RAM.
/// <see cref="MemoryWindow"/> names guest addresses that both executors sample
/// after execution into the snapshot's memory observations, so the differential
/// comparison can prove store/load behavior (widths, endianness, store order)
/// through the whole pipeline rather than only in registers.
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
        uint initialLo = 0,
        IEnumerable<RecompilerInitialMemoryItem>? initialMemory = null,
        IEnumerable<uint>? memoryWindow = null,
        uint? referenceStepBudget = null,
        bool budgetsAreShared = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A fixture needs a name.", nameof(name));
        Name = name;
        Instructions = new ReadOnlyCollection<uint>(encodedInstructions.ToArray());
        if (Instructions.Count == 0) throw new ArgumentException("A fixture needs at least one instruction.", nameof(encodedInstructions));
        if (stepBudget == 0) throw new ArgumentOutOfRangeException(nameof(stepBudget));
        EntryPc = entryPc;
        StepBudget = stepBudget;
        if (referenceStepBudget is 0) throw new ArgumentOutOfRangeException(nameof(referenceStepBudget));

        var gpr = initialGpr?.ToArray() ?? new uint[GprCount];
        if (gpr.Length != GprCount) throw new ArgumentException("Initial GPR must contain exactly 32 values.", nameof(initialGpr));
        gpr[0] = 0;
        InitialGpr = new ReadOnlyCollection<uint>(gpr);
        InitialHi = initialHi;
        InitialLo = initialLo;
        InitialMemory = new ReadOnlyCollection<RecompilerInitialMemoryItem>(
            (initialMemory ?? Array.Empty<RecompilerInitialMemoryItem>()).ToArray());
        MemoryWindow = new ReadOnlyCollection<uint>((memoryWindow ?? Array.Empty<uint>()).ToArray());
        ReferenceStepBudget = referenceStepBudget ?? stepBudget;
        BudgetsAreShared = budgetsAreShared;
    }

    public string Name { get; }
    public IReadOnlyList<uint> Instructions { get; }
    public uint EntryPc { get; }
    public uint StepBudget { get; }
    public IReadOnlyList<uint> InitialGpr { get; }
    public uint InitialHi { get; }
    public uint InitialLo { get; }
    public IReadOnlyList<RecompilerInitialMemoryItem> InitialMemory { get; }
    public IReadOnlyList<uint> MemoryWindow { get; }

    /// <summary>
    /// The interpreter's execution budget in retired MIPS instructions. Differs
    /// from <see cref="StepBudget"/> (retired host blocks) whenever control
    /// transfer fuses an instruction with its delay slot.
    /// </summary>
    public uint ReferenceStepBudget { get; }

    /// <summary>
    /// An explicit, author-asserted fact (CodeRabbit finding on #305): true only when
    /// whoever built this fixture has verified that <see cref="StepBudget"/> (host
    /// blocks) and <see cref="ReferenceStepBudget"/> (guest instructions) represent
    /// the same real execution window for this specific program, despite counting
    /// different units. Equal numeric values do <b>not</b> imply this on their own —
    /// a control transfer fused with its delay slot retires one host block per two
    /// guest instructions, so two independently-chosen equal budgets can still stop
    /// the executors at different points. Defaults to <c>false</c>; only a fixture
    /// deliberately built to exercise <see cref="RecompilerComparisonClassification.BudgetInconclusive"/>
    /// through <see cref="RecompilerDifferentialRunner"/> should set this true.
    /// </summary>
    public bool BudgetsAreShared { get; }

    /// <summary>Returns the guest PC of the <paramref name="index"/>-th instruction.</summary>
    public uint PcOfInstruction(int index) => EntryPc + unchecked((uint)index * 4u);
}
