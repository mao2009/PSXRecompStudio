using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Verified AOT dispatch for one OpenBIOS-loaded PS-X EXE (Issue #734).
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
    private readonly int _wordCount;
    private bool _invalidated;

    public VerifiedExeDispatch(LoadedCodeTable table, uint loadAddress, uint entryPc, string imageId, int wordCount)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        if (wordCount <= 0) throw new ArgumentOutOfRangeException(nameof(wordCount));
        _table = table;
        _loadAddress = loadAddress;
        _entryPc = entryPc;
        _imageId = imageId;
        _wordCount = wordCount;
    }

    public uint LoadAddress => _loadAddress;
    public uint EntryPc => _entryPc;
    public string ImageId => _imageId;
    public ulong DispatchAttempts { get; private set; }
    public ulong NativeHits { get; private set; }
    public ulong Fallbacks { get; private set; }
    public ulong Invalidations { get; private set; }
    public ulong NativeToInterpreter { get; private set; }
    public ulong InterpreterToNative { get; private set; }

    public bool InLoadRange(uint pc)
    {
        var end = (ulong)_loadAddress + (ulong)_wordCount * 4ul;
        return pc >= _loadAddress && pc < end && (pc & 3u) == 0;
    }

    /// <summary>
    /// Marks RAM written at [physicalStart, physicalStart+byteCount); any overlap
    /// with the EXE span invalidates the verified mapping (conservative).
    /// </summary>
    public void NotifyRamWrite(uint physicalStart, uint byteCount)
    {
        if (byteCount == 0) return;
        if (!FallbackReturnCheck.TryRamOffset(_loadAddress, out var exePhysical)) { Invalidate(); return; }
        var exeStart = exePhysical;
        var exeEnd = exeStart + (uint)_wordCount * 4u;
        var writeEnd = physicalStart + byteCount;
        if (physicalStart < exeEnd && exeStart < writeEnd) Invalidate();
    }

    private void Invalidate()
    {
        _invalidated = true;
        Invalidations++;
    }

    public void NotifyDmaWrite(uint physicalStart, uint byteCount) => NotifyRamWrite(physicalStart, byteCount);

    /// <summary>
    /// Tries verified native dispatch. Returns the matched version, or null for
    /// explicit fallback. Never dispatches on PC alone.
    /// </summary>
    public GuardedBlock? TryDispatch(uint pc, Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        DispatchAttempts++;
        if (_invalidated || !InLoadRange(pc))
        {
            Fallbacks++;
            return null;
        }
        var match = _table.Match(pc, readWord);
        if (match is null)
        {
            Fallbacks++;
            return null;
        }
        NativeHits++;
        return match;
    }

    /// <summary>Re-verifies after invalidation; fails closed when RAM differs.</summary>
    public GuardedBlock? Reverify(uint pc, Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        _invalidated = false;
        var match = TryDispatch(pc, readWord);
        if (match is null) _invalidated = true;
        return match;
    }

    public void NoteNativeToInterpreter() => NativeToInterpreter++;
    public void NoteInterpreterToNative() => InterpreterToNative++;
}
