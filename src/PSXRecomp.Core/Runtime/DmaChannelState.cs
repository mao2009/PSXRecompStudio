using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Pure domain model representing DMA channel state.
/// No I/O, no side effects.
/// </summary>
[Domain]
public sealed class DmaChannelState
{
    public uint BaseAddress { get; set; }
    public uint BlockControl { get; set; }
    public uint ChannelControl { get; set; }
    public uint WordsTransferred { get; set; }

    public bool Enabled => (ChannelControl & 0x80000000) != 0;
    public bool Active => (ChannelControl & 0x00000001) != 0;
    public bool IsToRam => (ChannelControl & 0x00000002) == 0;

    public void Reset()
    {
        BaseAddress = 0;
        BlockControl = 0;
        ChannelControl = 0;
        WordsTransferred = 0;
    }
}
