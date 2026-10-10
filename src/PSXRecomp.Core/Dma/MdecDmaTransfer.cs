using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.Mdec;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// MDEC DMA (Issue #732): channel 0 feeds guest RAM into the decoder, channel 1 drains its output into guest RAM.
/// Block mode only (how libpress drives it); register ownership stays in the Rust DMA model, as for
/// <see cref="CdRomDmaTransfer"/>. Channel 1 completes only once the decoder holds the whole burst, so arming it
/// before the input arrives simply waits.
/// </summary>
[Domain]
public sealed class MdecDmaTransfer
{
    public const int InChannel = 0;
    public const int OutChannel = 1;

    private const uint StartBusy = 1u << 24;
    private const uint SyncMask = 3u << 9;
    private const uint BlockSync = 1u << 9;
    private const uint RamAddressMask = 0x001FFFFCu;

    private readonly MdecDevice _mdec;
    private readonly IDmaController _dma;
    private readonly IMemoryBus _memory;

    public MdecDmaTransfer(MdecDevice mdec, IDmaController dma, IMemoryBus memory)
    {
        _mdec = mdec ?? throw new ArgumentNullException(nameof(mdec));
        _dma = dma ?? throw new ArgumentNullException(nameof(dma));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    /// <summary>Moves and completes a started channel 0 burst, then a channel 1 burst whose data is ready.</summary>
    public void TryTransfer()
    {
        if (Armed(InChannel) is { } input)
        {
            for (uint i = 0; i < input.Words; i++)
            {
                _mdec.WriteData(_memory.Read((input.Madr + 4 * i) & RamAddressMask));
            }

            Complete(InChannel, input);
        }

        if (Armed(OutChannel) is { } output && _mdec.FifoWordCount >= output.Words)
        {
            for (uint i = 0; i < output.Words; i++)
            {
                _memory.Write((output.Madr + 4 * i) & RamAddressMask, _mdec.ReadData());
            }

            Complete(OutChannel, output);
        }
    }

    private (uint Madr, uint Words)? Armed(int channel)
    {
        var chcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(channel));
        var enabled = (_dma.ReadRegister(Ps1MemoryMap.Dpcr) & (1u << (3 + 4 * channel))) != 0;
        if (!enabled || (chcr & StartBusy) == 0 || (chcr & SyncMask) != BlockSync)
        {
            return null;
        }

        var bcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelBcr(channel));
        var words = Field(bcr & 0xFFFF) * Field(bcr >> 16);
        return (_dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(channel)), words);
    }

    private void Complete(int channel, (uint Madr, uint Words) burst)
    {
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(channel), (burst.Madr + 4 * burst.Words) & 0x00FFFFFF);
        _dma.CompleteChannel((uint)channel);
    }

    private static uint Field(uint value) => value == 0 ? 0x1_0000u : value;
}
