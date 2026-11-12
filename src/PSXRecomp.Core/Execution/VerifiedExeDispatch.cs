using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Verified AOT dispatch for one OpenBIOS-loaded PS-X EXE (Issue #734).
/// Scaffolding: this type has no production call sites yet; wiring it into
/// <c>RecompiledHostExecutionEngine</c>/<c>ArtifactFallbackSession</c> is follow-up
/// work. It exists so the verification rules are reviewed and tested in isolation.
/// Wraps <see cref="LoadedCodeTable.Match"/> with the rules the issue requires:
/// <list type="bullet">
/// <item>guest PC alone is never identity: dispatch requires the full registered
/// word span to equal real RAM bytes at dispatch time;</item>
/// <item>the PC must lie in the EXE load range registered at load time;</item>
/// <item>any RAM write/DMA into a verified span invalidates it until re-verified;</item>
/// <item>no new CPU, IR, or codegen: version selection reuses <see cref="LoadedCodeTable"/>.</item>
/// </list>
/// </summary>
[Domain]
public sealed class VerifiedExeDispatch
{
    private readonly LoadedCodeTable _table;
    private readonly uint _loadAddress;
    private readonly uint _entryPc;
    private readonly string _imageId;
    private readonly uint[] _expectedWords;
    private bool _invalidated;

    public VerifiedExeDispatch(
        LoadedCodeTable table, uint loadAddress, uint entryPc, string imageId, IReadOnlyList<uint> expectedWords)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        ArgumentNullException.ThrowIfNull(expectedWords);
        if (expectedWords.Count == 0) throw new ArgumentOutOfRangeException(nameof(expectedWords));
        if ((loadAddress & 3u) != 0) throw new ArgumentOutOfRangeException(nameof(loadAddress), "Load address must be word-aligned.");
        var byteLen = (ulong)expectedWords.Count * 4ul;
        var end = (ulong)loadAddress + byteLen;
        if (end > 0x1_0000_0000ul) throw new ArgumentOutOfRangeException(nameof(expectedWords), "Span must not wrap the 32-bit address space.");
        if (!FallbackReturnCheck.TryRamOffset(loadAddress, out var physical))
            throw new ArgumentOutOfRangeException(nameof(loadAddress), "Load address must map to real RAM.");
        if (byteLen > (ulong)PSXCoreWrapper.RamSize - physical)
            throw new ArgumentOutOfRangeException(nameof(expectedWords), "Span must fit in real RAM from its load offset.");
        if ((entryPc & 3u) != 0) throw new ArgumentOutOfRangeException(nameof(entryPc), "Entry PC must be word-aligned.");
        if (entryPc < loadAddress || (ulong)entryPc >= end)
            throw new ArgumentOutOfRangeException(nameof(entryPc), "Entry PC must lie in the registered load range.");
        _table = table;
        _loadAddress = loadAddress;
        _entryPc = entryPc;
        _imageId = imageId;
        _expectedWords = expectedWords.ToArray();
    }

    public uint LoadAddress => _loadAddress;
    public uint EntryPc => _entryPc;
    public string ImageId => _imageId;
    public int WordCount => _expectedWords.Length;
    public ulong DispatchAttempts { get; private set; }
    public ulong NativeHits { get; private set; }
    public ulong Fallbacks { get; private set; }
    public ulong Invalidations { get; private set; }
    public ulong NativeToInterpreter { get; private set; }
    public ulong InterpreterToNative { get; private set; }

    public bool InLoadRange(uint pc)
    {
        var end = (ulong)_loadAddress + (ulong)_expectedWords.Length * 4ul;
        return pc >= _loadAddress && (ulong)pc < end && (pc & 3u) == 0;
    }

    /// <summary>
    /// Marks RAM written at [physicalStart, physicalStart+byteCount); any overlap
    /// with the EXE span invalidates the verified mapping (conservative).
    /// Ends are computed in <see cref="ulong"/> so address-space wraparound can
    /// never hide an overlap.
    /// </summary>
    public void NotifyRamWrite(uint physicalStart, uint byteCount)
    {
        if (byteCount == 0) return;
        if (!FallbackReturnCheck.TryRamOffset(_loadAddress, out var exePhysical)) { Invalidate(); return; }
        var exeStart = (ulong)exePhysical;
        var exeEnd = exeStart + (ulong)_expectedWords.Length * 4ul;
        var writeEnd = (ulong)physicalStart + byteCount;
        if ((ulong)physicalStart < exeEnd && exeStart < writeEnd) Invalidate();
    }

    private void Invalidate()
    {
        _invalidated = true;
        Invalidations++;
    }

    public void NotifyDmaWrite(uint physicalStart, uint byteCount) => NotifyRamWrite(physicalStart, byteCount);

    /// <summary>
    /// Tries verified native dispatch. Returns the matched version, or null for
    /// explicit fallback. Never dispatches on PC alone: the full expected span is
    /// compared against real RAM first, then the table selects the version.
    /// A reader failure is a closed fallback, never a dispatch.
    /// </summary>
    public GuardedBlock? TryDispatch(uint pc, Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        DispatchAttempts++;
        if (_invalidated || !InLoadRange(pc) || !FullSpanMatches(readWord))
        {
            Fallbacks++;
            return null;
        }
        GuardedBlock? match;
        try
        {
            match = _table.Match(pc, readWord);
        }
        catch
        {
            Fallbacks++;
            return null;
        }
        if (match is null)
        {
            Fallbacks++;
            return null;
        }
        NativeHits++;
        return match;
    }

    /// <summary>
    /// Re-verifies after invalidation; fails closed when RAM differs.
    /// Counts exactly one dispatch attempt and at most one fallback.
    /// </summary>
    public GuardedBlock? Reverify(uint pc, Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        DispatchAttempts++;
        _invalidated = false;
        if (!InLoadRange(pc) || !FullSpanMatches(readWord))
        {
            _invalidated = true;
            Fallbacks++;
            return null;
        }
        GuardedBlock? match;
        try
        {
            match = _table.Match(pc, readWord);
        }
        catch
        {
            _invalidated = true;
            Fallbacks++;
            return null;
        }
        if (match is null)
        {
            _invalidated = true;
            Fallbacks++;
            return null;
        }
        NativeHits++;
        return match;
    }

    /// <summary>
    /// Compares the whole expected image span against real RAM bytes.
    /// A single fused block <see cref="LoadedCodeTable.Match"/> is not enough:
    /// it proves only its own words, not the full EXE span.
    /// </summary>
    private bool FullSpanMatches(Func<uint, uint> readWord)
    {
        try
        {
            for (var i = 0; i < _expectedWords.Length; i++)
            {
                if (readWord(_loadAddress + (uint)i * 4u) != _expectedWords[i]) return false;
            }
        }
        catch
        {
            return false;
        }
        return true;
    }

    public void NoteNativeToInterpreter() => NativeToInterpreter++;
    public void NoteInterpreterToNative() => InterpreterToNative++;
}
