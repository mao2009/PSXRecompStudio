using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// A block compiled ahead of time for code the guest itself places in RAM (Issue #732). It is valid only while guest
/// memory at <see cref="RecompilerIrBlock.EntryPc"/> holds exactly <paramref name="Words"/> (the block's whole fused
/// unit, <see cref="RecompilerIrBlock.RetiredInstructionCount"/> words).
/// </summary>
[Domain]
public sealed record GuardedBlock(RecompilerIrBlock Block, IReadOnlyList<uint> Words);

/// <summary>The ahead-of-time build of one loaded code image (<c>ReachableProgramBuilder.BuildLoadedImage</c>).</summary>
/// <param name="Blocks">One guarded block per lowered entry of the image.</param>
/// <param name="SkippedEntries">Reachable entries that could not be lowered; they keep no block and stay interpreted.</param>
[Domain]
public sealed record GuardedImageProgram(IReadOnlyList<GuardedBlock> Blocks, IReadOnlyList<uint> SkippedEntries)
{
    /// <summary>Image words covered by a block.</summary>
    public int NativeInstructionCount => Blocks.Sum(static b => b.Words.Count);

    /// <summary>The manifest's declaration of the image (Issue #773); null when the caller did not declare one. Not proof of the loaded file.</summary>
    public LoadedImageOrigin? Origin { get; init; }
}

/// <summary>
/// The manifest's declaration of one loaded code image, fixed when the image is registered (Issue #773). It repeats the
/// label and directive the manifest gave; it is not a check of the file's bytes or of what the game loaded at run time, so
/// it proves no more than the manifest does. It never holds a host path. Diagnostics only.
/// </summary>
/// <param name="Name">The manifest label (for a disc image, the normalized ISO path under its prefix).</param>
/// <param name="Kind">The manifest directive it was declared with: <c>rom-image</c>, <c>exe-file</c>, <c>boot-exe</c>, <c>disc-exe</c>.</param>
/// <param name="LoadAddress">The guest address the image is loaded at.</param>
/// <param name="WordCount">The image size in words.</param>
[Domain]
public sealed record LoadedImageOrigin(string Name, string Kind, uint LoadAddress, int WordCount);

/// <summary>
/// Every pre-generated version of RAM-placed code, linked into one guest address space (Issue #732). Several images may
/// place different code at the same guest address (a shell and an executable, overlays): each is a separate
/// <em>version</em> of that entry. At run time the dispatcher and the fallback interpreter select the version whose words
/// guest memory holds now (<see cref="Match"/>); with none, the PC has no native code. Nothing is compiled at run time.
/// </summary>
[Domain]
public sealed class LoadedCodeTable
{
    private readonly Dictionary<uint, GuardedBlock[]> _versions;
    private readonly Dictionary<uint, IReadOnlyList<LoadedImageOrigin>[]> _origins;

    /// <param name="images">The image builds to link. A version identical to an earlier one (same entry, same words) is
    /// the same code and is kept once; its <see cref="OriginsAt"/> lists every image that carried it.</param>
    public LoadedCodeTable(IEnumerable<GuardedImageProgram> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var perPc = images
            .SelectMany(static image => image.Blocks.Select(block => (Block: block, image.Origin)))
            .GroupBy(static b => b.Block.Block.EntryPc)
            .ToDictionary(static g => g.Key, static g => g.GroupBy(static b => string.Join(',', b.Block.Words)).ToArray());
        _versions = perPc.ToDictionary(static e => e.Key, static e => e.Value.Select(static g => g.First().Block).ToArray());
        _origins = perPc.ToDictionary(
            static e => e.Key,
            static e => e.Value.Select(static g => (IReadOnlyList<LoadedImageOrigin>)g.Select(static b => b.Origin).OfType<LoadedImageOrigin>().ToArray()).ToArray());
        Blocks = _versions.OrderBy(static e => e.Key).SelectMany(static e => e.Value).ToArray();
    }

    /// <summary>An empty table: no RAM-placed code.</summary>
    public static LoadedCodeTable Empty { get; } = new([]);

    /// <summary>All versions, ordered by entry PC and then by link order.</summary>
    public IReadOnlyList<GuardedBlock> Blocks { get; }

    /// <summary>The versions registered for <paramref name="pc"/>, in link order (empty when none).</summary>
    public IReadOnlyList<GuardedBlock> VersionsAt(uint pc) => _versions.TryGetValue(pc, out var v) ? v : [];

    /// <summary>
    /// For each version at <paramref name="pc"/> (aligned with <see cref="VersionsAt"/>), the images that registered exactly
    /// that code. An image without a recorded origin contributes nothing, so a version may have an empty list.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<LoadedImageOrigin>> OriginsAt(uint pc) => _origins.TryGetValue(pc, out var o) ? o : [];

    /// <summary>Whether any version is registered for <paramref name="pc"/>.</summary>
    public bool Contains(uint pc) => _versions.ContainsKey(pc);

    /// <summary>
    /// The version whose words guest memory holds at <paramref name="pc"/> now, or null (stale, overwritten or
    /// unknown code: no native code may run there).
    /// </summary>
    /// <param name="readWord">Reads the aligned guest word at a virtual address.</param>
    public GuardedBlock? Match(uint pc, Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        foreach (var version in VersionsAt(pc))
        {
            var current = version.Words.Count > 0; // an empty version vouches for nothing: fail closed, as the C guard does
            for (var i = 0; i < version.Words.Count && current; i++)
            {
                current = readWord(pc + (uint)i * 4u) == version.Words[i];
            }

            if (current) return version;
        }

        return null;
    }
}
