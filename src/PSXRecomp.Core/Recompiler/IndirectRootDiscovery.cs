using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// One indirect-call root found by <see cref="IndirectRootDiscovery"/> (Issue #773). <see cref="TargetPc"/> is the in-image
/// function entry that becomes a root. <see cref="TableAddress"/> is the pointer-table word that names it, and
/// <see cref="MaterializedBy"/> is the <c>lui</c> of the reachable <c>lui</c>/<c>addiu|ori</c> pair that gave the table's address.
/// </summary>
[Domain]
internal sealed record IndirectRoot(uint TargetPc, uint TableAddress, uint MaterializedBy);

/// <summary>
/// Finds the functions a PS-X EXE calls only through a function-pointer table (Issue #773): the direct JAL/J/branch graph from
/// the entry point cannot see them, so their code never gets an AOT block and runs through the interpreter fallback.
/// <para>
/// Evidence comes only from reachable code. A reachable <c>lui</c> followed by <c>addiu</c> or <c>ori</c> on the same register
/// names a table base. The table is read as ascending words from that base, and a word becomes a candidate only if it is an
/// in-image, 4-aligned PC. The run stops at the first word that is not such a PC, or after <see cref="MaxTableWords"/> words.
/// A candidate is accepted only when all of these hold:
/// </para>
/// <list type="bullet">
/// <item>it is not already reachable (existing discovery is unchanged);</item>
/// <item>it has a function-entry prologue: <c>addiu $sp,$sp,-N</c>, or the word after a <c>jr $ra</c> and its delay slot;</item>
/// <item>its first instruction is not a reserved encoding, and discovery from it alone is structurally valid.</item>
/// </list>
/// <para>
/// The table base is never a root, and neither is any word that is not a table entry. A base such as a data array that happens
/// to look like code is therefore not registered, which prevents the false root seen in the Persona EXE (0x800A724C).
/// The scan is a fixed point: each round adds the code its accepted roots reach, which can materialize further tables. It stops
/// at the first round that adds nothing, or at <see cref="MaxRounds"/>, <see cref="MaxRoots"/>, or <see cref="MaxCandidateChecks"/>.
/// The result is ascending by target PC and depends only on the input words.
/// </para>
/// <para>
/// Each accepted root is compiled as a guarded block, so a root that never runs costs only build time. A root that runs executes
/// the same words the guest runs. Words not in any table, and computed targets, are not found. Static analysis cannot prove
/// that a table word is code, so a data word that looks like a prologue can still be accepted. Such a block runs only when the
/// guest jumps to it, and the RAM guard still checks its words before it runs.
/// </para>
/// <para>
/// ponytail: only the <c>lui</c>/<c>addiu|ori</c> base form is matched. A table reached through a pointer variable (<c>lw</c> from
/// a fixed address) or a computed index is not. Upgrade path: a fallback-observed root feed, not a wider pattern.
/// </para>
/// </summary>
[Domain]
internal static class IndirectRootDiscovery
{
    /// <summary>Upper bound on fixed-point rounds, each of which runs one reachability pass.</summary>
    internal const int MaxRounds = 8;

    /// <summary>Upper bound on accepted roots.</summary>
    internal const int MaxRoots = 256;

    /// <summary>Upper bound on the words read from one table.</summary>
    internal const int MaxTableWords = 32;

    /// <summary>Upper bound on candidates checked with a discovery pass, which is the expensive step.</summary>
    internal const int MaxCandidateChecks = 1024;

    private const uint InstructionSize = 4;
    private const uint JrRa = 0x03E00008u;
    private const uint SpRegister = 29;

    /// <summary>
    /// The indirect-call roots of an image that direct discovery from <paramref name="roots"/> does not reach, ascending by target PC.
    /// </summary>
    /// <param name="loadAddress">Guest address of the first word.</param>
    /// <param name="words">The image, as little-endian words, in guest order.</param>
    /// <param name="roots">The entry point and explicit roots.</param>
    internal static IReadOnlyList<IndirectRoot> Find(uint loadAddress, IReadOnlyList<uint> words, IEnumerable<uint> roots)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(roots);

        var seeds = roots.Distinct().Order().ToArray();
        var imageEnd = (ulong)loadAddress + (ulong)words.Count * InstructionSize;
        var accepted = new SortedDictionary<uint, IndirectRoot>();
        var checks = 0;

        bool Full() => accepted.Count >= MaxRoots || checks >= MaxCandidateChecks;

        try
        {
            for (var round = 0; round < MaxRounds && !Full(); round++)
            {
                var reach = ReachableProgramBuilder.ReachablePcs(loadAddress, words, seeds.Concat(accepted.Keys));
                var added = false;

                foreach (var pc in reach.Order())
                {
                    if (!TryGetTableBase(loadAddress, words, imageEnd, reach, pc, out var tableBase))
                    {
                        continue;
                    }

                    for (var i = 0; i < MaxTableWords && !Full(); i++)
                    {
                        var address = (ulong)tableBase + (ulong)i * InstructionSize;
                        if (address > uint.MaxValue || address + InstructionSize > imageEnd)
                        {
                            break;
                        }

                        var tableAddress = (uint)address;
                        var target = words[(int)((tableAddress - loadAddress) / InstructionSize)];
                        if (!IsInImagePc(loadAddress, imageEnd, target))
                        {
                            break;
                        }

                        if (reach.Contains(target) || accepted.ContainsKey(target))
                        {
                            continue;
                        }

                        checks++;
                        if (IsFunctionCandidate(loadAddress, words, target))
                        {
                            accepted.Add(target, new IndirectRoot(target, tableAddress, pc));
                            added = true;
                        }
                    }
                }

                if (!added)
                {
                    break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // A seed reaches a structurally invalid transfer. The caller's build reports that, so the roots found so far stand.
        }

        return accepted.Values.ToArray();
    }

    /// <summary>
    /// A reachable <c>lui rt, hi</c> followed by reachable <c>addiu rt, rt, lo</c> or <c>ori rt, rt, lo</c> names a table base.
    /// The base is a 4-aligned in-image address.
    /// </summary>
    private static bool TryGetTableBase(
        uint loadAddress, IReadOnlyList<uint> words, ulong imageEnd, IReadOnlySet<uint> reach, uint pc, out uint tableBase)
    {
        tableBase = 0;
        if (!reach.Contains(pc) || !reach.Contains(pc + InstructionSize) || !IsInImagePc(loadAddress, imageEnd, pc)
            || !IsInImagePc(loadAddress, imageEnd, pc + InstructionSize))
        {
            return false;
        }

        var lui = words[(int)((pc - loadAddress) / InstructionSize)];
        var next = words[(int)((pc + InstructionSize - loadAddress) / InstructionSize)];
        var register = RtOf(lui);
        if (R3000aDecoder.Decode(lui).Opcode != R3000aOpcode.Lui || register == 0
            || RsOf(next) != register || RtOf(next) != register)
        {
            return false;
        }

        var opcode = R3000aDecoder.Decode(next).Opcode;
        if (opcode is not (R3000aOpcode.Addiu or R3000aOpcode.Ori))
        {
            return false;
        }

        // addiu sign-extends its immediate, ori zero-extends it: the same rule as the MIPS instructions.
        var low = opcode == R3000aOpcode.Addiu ? (uint)(short)(ushort)next : next & 0xFFFFu;
        tableBase = ((lui & 0xFFFFu) << 16) + low;
        return tableBase % InstructionSize == 0 && IsInImagePc(loadAddress, imageEnd, tableBase);
    }

    private static bool IsFunctionCandidate(uint loadAddress, IReadOnlyList<uint> words, uint target)
    {
        var index = (int)((target - loadAddress) / InstructionSize);
        if (R3000aDecoder.Decode(words[index]).Opcode == R3000aOpcode.Reserved || !LooksLikeFunctionEntry(words, index))
        {
            return false;
        }

        try
        {
            _ = ReachableProgramBuilder.ReachablePcs(loadAddress, words, [target]);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool LooksLikeFunctionEntry(IReadOnlyList<uint> words, int index)
    {
        var first = words[index];
        var stackFrame = (first >> 26) == 0x09 && RsOf(first) == SpRegister && RtOf(first) == SpRegister && (first & 0x8000u) != 0;
        var followsReturn = index >= 2 && words[index - 2] == JrRa;
        return stackFrame || followsReturn;
    }

    private static bool IsInImagePc(uint loadAddress, ulong imageEnd, uint value) =>
        value % InstructionSize == 0 && value >= loadAddress && value < imageEnd;

    private static uint RsOf(uint word) => (word >> 21) & 31;

    private static uint RtOf(uint word) => (word >> 16) & 31;
}
