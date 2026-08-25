using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Motion Decoder (MDEC) interface.
///
/// JPEG decoding and motion video decompression.
/// Registers at 0x1F801820-0x1F801824.
/// Provides DMA channels: MDECin (ch0) and MDECout (ch1).
/// </summary>
[Domain]
public interface IMdec
{
    uint ReadRegister(uint offset);
    void WriteRegister(uint offset, uint value);
    bool IsBusy { get; }
    int FifoWordCount { get; }
    void Reset();
}
