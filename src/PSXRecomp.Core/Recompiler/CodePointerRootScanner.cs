using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>How a code-pointer root candidate was found in the image (Issue #773).</summary>
[Domain]
internal enum CodePointerRootKind
{
    /// <summary>A word of the image (a pointer table, a callback record) holds the target address.</summary>
    DataWord,

    /// <summary>A <c>lui</c> immediately followed by <c>addiu</c> or <c>ori</c> on the same register materializes the target.</summary>
    LuiAddiuPair,
}

/// <summary>
/// One discovered code-pointer root (Issue #773). <see cref="TargetPc"/> is the in-image PC that becomes a block entry.
/// <see cref="SourcePc"/> is the image word that named it: the pointer word itself, or the <c>lui</c> of a materializing pair.
/// Both are kept so that every root traces back to the bytes that produced it.
/// </summary>
[Domain]
internal sealed record CodePointerRoot(uint TargetPc, uint SourcePc, CodePointerRootKind Kind);

/// <summary>
/// Finds indirect-call targets that direct reachability from the entry point cannot see (Issue #773): a runtime-loaded
/// PS-X EXE calls functions through pointer tables and callback registrations, and those callees have no static call edge.
/// <para>
/// The scan reads only the image's own words, with no title-specific address. A candidate is a word that names an in-image
/// PC, either as a data word or as a <c>lui</c>/<c>addiu|ori</c> pair. It is kept only when all of these hold:
/// </para>
/// <list type="bullet">
/// <item>the target is not already reachable from the entry or the explicit roots (existing discovery is unchanged);</item>
/// <item>the target looks like a function entry: an <c>addiu $sp,$sp,-N</c> prologue, or the word after a <c>jr $ra</c> and its delay slot;</item>
/// <item>discovery from the target alone is structurally valid (<see cref="ReachableProgramBuilder.IsDiscoverableRoot"/>).</item>
/// </list>
/// <para>
/// Every AOT block is guarded by the words it was compiled from, so an accepted root that never runs costs only build time.
/// A rejected candidate that does run stays on the interpreter fallback; the rules therefore favor rejection over acceptance.
/// Cost is one linear pass over the words plus one bounded discovery per shape-matching candidate.
/// </para>
/// <para>
/// ponytail: the scan matches only direct data-word pointers and adjacent <c>lui</c>/<c>addiu|ori</c> pairs. Computed targets
/// (<c>sll</c>/<c>addu</c> table indexing, pointers built from non-adjacent instructions) are not matched. Upgrade path: a
/// fallback-observed root feed (see the design note for the next stage), not a wider static pattern.
/// </para>
/// </summary>
[Domain]
internal static class CodePointerRootScanner
{
    private const uint InstructionSize = 4;
    private const uint JrRa = 0x03E00008u;
    private const uint SpRegister = 29;
    private const uint OpAddiu = 0x09, OpOri = 0x0D, OpLui = 0x0F;

    /// <summary>Upper bound on accepted roots in one scan (Issue #782 review). Exceeding it fails the scan.</summary>
    internal const int MaxRoots = 256;

    /// <summary>Upper bound on discovery passes (<see cref="ReachableProgramBuilder.IsDiscoverableRoot"/>), the expensive step.</summary>
    internal const int MaxCandidateChecks = 1024;

    /// <summary>
    /// The code-pointer roots of an image not already reached from <paramref name="roots"/>, ascending by target PC. The
    /// first word that names a target is the one recorded as its source.
    /// </summary>
    /// <remarks>
    /// Fail closed: when the scan would need more than <see cref="MaxRoots"/> roots or <see cref="MaxCandidateChecks"/>
    /// discovery passes, it throws <see cref="InvalidDataException"/>. A truncated set would look like a complete one.
    /// </remarks>
    /// <param name="loadAddress">Guest address of the first word.</param>
    /// <param name="words">The image, as little-endian words, in guest order.</param>
    /// <param name="roots">The entry point and explicit roots, whose reachable code is excluded.</param>
    internal static IReadOnlyList<CodePointerRoot> Find(uint loadAddress, IReadOnlyList<uint> words, IEnumerable<uint> roots)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(roots);

        var known = ReachableProgramBuilder.ReachablePcs(loadAddress, words, roots);
        var imageEnd = (ulong)loadAddress + (ulong)words.Count * InstructionSize;
        var found = new SortedDictionary<uint, CodePointerRoot>();
        var rejected = new HashSet<uint>();
        var checks = 0;

        void Consider(uint target, uint source, CodePointerRootKind kind)
        {
            if ((target & (InstructionSize - 1)) != 0 || target < loadAddress || target >= imageEnd
                || known.Contains(target) || found.ContainsKey(target) || rejected.Contains(target))
            {
                return;
            }

            if (!LooksLikeFunctionEntry(loadAddress, words, target))
            {
                rejected.Add(target);
                return;
            }

            if (checks >= MaxCandidateChecks)
            {
                throw new InvalidDataException(
                    $"code-pointer scan: more than {MaxCandidateChecks} candidate checks; the root set would be incomplete.");
            }

            checks++;
            if (!ReachableProgramBuilder.IsDiscoverableRoot(loadAddress, words, target))
            {
                rejected.Add(target);
                return;
            }

            if (found.Count >= MaxRoots)
            {
                throw new InvalidDataException(
                    $"code-pointer scan: more than {MaxRoots} roots; the root set would be incomplete.");
            }

            found.Add(target, new CodePointerRoot(target, source, kind));
        }

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var source = loadAddress + (uint)index * InstructionSize;
            Consider(word, source, CodePointerRootKind.DataWord);

            if (index + 1 < words.Count && OpcodeOf(word) == OpLui)
            {
                var next = words[index + 1];
                var register = RtOf(word);
                if (OpcodeOf(next) is OpAddiu or OpOri && RsOf(next) == register && RtOf(next) == register)
                {
                    // addiu sign-extends its immediate, ori zero-extends it: the same rule as the MIPS instructions.
                    var low = OpcodeOf(next) == OpAddiu ? (uint)(short)(ushort)next : next & 0xFFFFu;
                    Consider(((word & 0xFFFFu) << 16) + low, source, CodePointerRootKind.LuiAddiuPair);
                }
            }
        }

        return found.Values.ToArray();
    }

    private static bool LooksLikeFunctionEntry(uint loadAddress, IReadOnlyList<uint> words, uint target)
    {
        var index = (int)((target - loadAddress) / InstructionSize);
        var first = words[index];
        var stackFrame = OpcodeOf(first) == OpAddiu && RsOf(first) == SpRegister && RtOf(first) == SpRegister
            && (first & 0x8000u) != 0;
        var followsReturn = index >= 2 && words[index - 2] == JrRa;
        return stackFrame || followsReturn;
    }

    private static uint OpcodeOf(uint word) => word >> 26;

    private static uint RsOf(uint word) => (word >> 21) & 31;

    private static uint RtOf(uint word) => (word >> 16) & 31;
}
