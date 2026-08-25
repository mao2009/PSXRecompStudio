using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Memory-mapped I/O route entry.
/// Maps a physical address range to a hardware component.
/// </summary>
[Domain]
public readonly record struct MmioRoute
{
    public uint Start { get; init; }
    public uint End { get; init; }
    public int ComponentIndex { get; init; }
}
