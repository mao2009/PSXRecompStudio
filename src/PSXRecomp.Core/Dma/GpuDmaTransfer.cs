using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// Moves the data of the GPU's two DMA channels (Issue #732): channel 2 (GPU) block and linked-list transfers between
/// guest RAM and GP0/GPUREAD, and channel 6 (OTC) ordering-table clear. Like <see cref="CdRomDmaTransfer"/>, register
/// ownership stays in the Rust DMA model: a started transfer is moved in full at once and completed through
/// <see cref="IDmaController.CompleteChannel"/>. Without this bridge the generic model only times a transfer, so a
/// GP0(A0h) image upload never receives its pixels and the GPU waits for them forever.
/// </summary>
[Domain]
public sealed class GpuDmaTransfer
{
    public const int GpuChannel = 2;
    public const int OtcChannel = 6;

    private const uint DirectionFromRam = 1u << 0;
    private const uint AddressDecrement = 1u << 1;
    private const int SyncShift = 9;
    private const uint StartBusy = 1u << 24;
    private const uint StartTrigger = 1u << 28;
    private const uint RamAddressMask = 0x001FFFFCu;
    private const uint LinkedListEnd = 0x00800000u;
    private const uint OtcEndMarker = 0x00FFFFFFu;

    /// <summary>
    /// ponytail: a linked list that never reaches its end marker would hang the hardware too; a bound keeps the
    /// emulator responsive. 2 MiB of RAM holds at most this many one-word nodes.
    /// </summary>
    private const int MaxLinkedListNodes = 0x80000;

    private readonly IGpu _gpu;
    private readonly IDmaController _dma;
    private readonly IMemoryBus _memory;

    public GpuDmaTransfer(IGpu gpu, IDmaController dma, IMemoryBus memory)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _dma = dma ?? throw new ArgumentNullException(nameof(dma));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    /// <summary>Moves and completes any started channel 2/6 transfer. Returns the words moved.</summary>
    public uint TryTransfer() => TryGpu() + TryOtc();

    private uint TryGpu()
    {
        if (Started(GpuChannel) is not { } chcr)
        {
            return 0;
        }

        var madr = _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(GpuChannel));
        var bcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelBcr(GpuChannel));
        uint moved = 0;
        switch ((chcr >> SyncShift) & 3)
        {
            case 1:
                var step = (chcr & AddressDecrement) != 0 ? unchecked((uint)-4) : 4u;
                var words = Field(bcr & 0xFFFF) * Field(bcr >> 16);
                for (; moved < words; moved++, madr += step)
                {
                    if ((chcr & DirectionFromRam) != 0)
                    {
                        _gpu.WriteGP0(_memory.Read(madr & RamAddressMask));
                    }
                    else
                    {
                        _memory.Write(madr & RamAddressMask, _gpu.ReadGpuread());
                    }
                }

                _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(GpuChannel), bcr & 0xFFFF);
                break;
            case 2 when (chcr & DirectionFromRam) != 0:
                for (var node = 0; node < MaxLinkedListNodes && (madr & LinkedListEnd) == 0; node++)
                {
                    var header = _memory.Read(madr & RamAddressMask);
                    for (uint i = 1; i <= header >> 24; i++, moved++)
                    {
                        _gpu.WriteGP0(_memory.Read((madr + 4 * i) & RamAddressMask));
                    }

                    madr = header & 0x00FFFFFF;
                }

                break;
            default:
                // Manual mode or a GPU-to-RAM list: not used by the GPU; left to the generic duration model.
                return 0;
        }

        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(GpuChannel), madr & 0x00FFFFFF);
        _dma.CompleteChannel(GpuChannel);
        return moved;
    }

    /// <summary>OTC: BCR words, from MADR downwards, each linking to the previous word; the last is the end marker.</summary>
    private uint TryOtc()
    {
        if (Started(OtcChannel) is null)
        {
            return 0;
        }

        var address = _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(OtcChannel)) & RamAddressMask;
        var words = Field(_dma.ReadRegister(Ps1MemoryMap.GetChannelBcr(OtcChannel)) & 0xFFFF);
        for (uint i = 0; i < words; i++, address = (address - 4) & RamAddressMask)
        {
            _memory.Write(address, i == words - 1 ? OtcEndMarker : (address - 4) & RamAddressMask);
        }

        _dma.CompleteChannel(OtcChannel);
        return words;
    }

    /// <summary>The channel's CHCR when it is started (CHCR bit 24, its DPCR enable, and bit 28 in manual mode).</summary>
    private uint? Started(int channel)
    {
        var chcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(channel));
        var enabled = (_dma.ReadRegister(Ps1MemoryMap.Dpcr) & (1u << (3 + 4 * channel))) != 0;
        var triggered = ((chcr >> SyncShift) & 3) != 0 || (chcr & StartTrigger) != 0;
        return enabled && (chcr & StartBusy) != 0 && triggered ? chcr : null;
    }

    private static uint Field(uint value) => value == 0 ? 0x1_0000u : value;
}
