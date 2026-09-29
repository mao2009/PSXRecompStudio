using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Dma;

/// <summary>Outcome of one bounded CD-ROM DMA3 service attempt.</summary>
[Domain]
public enum CdRomDmaTransferStatus
{
    NotStarted = 0,
    WaitingForData,
    UnsupportedMode,
    InvalidDestination,
    Completed,
}

/// <summary>Result of one <see cref="CdRomDmaTransfer.TryTransfer"/> call.</summary>
[Domain]
public readonly record struct CdRomDmaTransferResult(
    CdRomDmaTransferStatus Status,
    uint WordsTransferred,
    uint DestinationAddress);

/// <summary>
/// Bounded CD-ROM DMA channel-3 bridge (Issue #587).
///
/// DMA register ownership remains in the existing Rust-backed
/// <see cref="IDmaController"/>. This type only moves an already-available
/// CD-ROM data FIFO into guest RAM when channel 3 is armed in the documented
/// CD-ROM burst/device-to-RAM mode, then signals that one channel's
/// completion on the existing DMA controller without advancing (and
/// potentially completing) any other active channel.
/// </summary>
[Domain]
public sealed class CdRomDmaTransfer
{
    public const int Channel = 3;

    private const uint DirectionRamToDevice = 1u << 0;
    private const uint AddressDecrement = 1u << 1;
    private const uint SyncMask = 3u << 9;
    private const uint StartBusy = 1u << 24;
    private const uint StartTrigger = 1u << 28;
    private const uint ChannelDpcrEnable = 1u << (3 + 4 * Channel);
    private const uint AddressMask = 0x00FFFFFCu;

    private readonly ICdRom _cdRom;
    private readonly IDmaController _dma;
    private readonly IMemoryBus _memory;

    public CdRomDmaTransfer(ICdRom cdRom, IDmaController dma, IMemoryBus memory)
    {
        _cdRom = cdRom ?? throw new ArgumentNullException(nameof(cdRom));
        _dma = dma ?? throw new ArgumentNullException(nameof(dma));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    /// <summary>
    /// Transfers one complete channel-3 burst when it is armed and the CD-ROM
    /// FIFO contains the whole requested payload. No partial transfer occurs.
    /// </summary>
    public CdRomDmaTransferResult TryTransfer()
    {
        var madr = _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(Channel));
        var bcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelBcr(Channel));
        var chcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(Channel));
        var dpcr = _dma.ReadRegister(Ps1MemoryMap.Dpcr);

        if ((dpcr & ChannelDpcrEnable) == 0 ||
            (chcr & StartBusy) == 0 ||
            (chcr & StartTrigger) == 0)
        {
            return new(CdRomDmaTransferStatus.NotStarted, 0, madr & AddressMask);
        }

        // PS1 CD-ROM DMA uses SyncMode 0, device -> RAM, incrementing words.
        // Other modes belong to other devices / later evidence and fail closed.
        if ((chcr & (DirectionRamToDevice | AddressDecrement | SyncMask)) != 0)
        {
            return new(CdRomDmaTransferStatus.UnsupportedMode, 0, madr & AddressMask);
        }

        var words = bcr & 0xFFFFu;
        if (words == 0)
            words = 0x1_0000u;

        var requiredBytes = checked((ulong)words * sizeof(uint));
        if (!_cdRom.DataReady || (ulong)_cdRom.DataBytesAvailable < requiredBytes)
        {
            return new(CdRomDmaTransferStatus.WaitingForData, 0, madr & AddressMask);
        }

        var address = madr & AddressMask;
        var endExclusive = (ulong)address + requiredBytes;
        if (address >= Ps1MemoryMap.RamMirrorEnd || endExclusive > Ps1MemoryMap.RamMirrorEnd)
        {
            return new(CdRomDmaTransferStatus.InvalidDestination, 0, address);
        }

        for (uint wordIndex = 0; wordIndex < words; wordIndex++)
        {
            var value =
                (uint)_cdRom.ReadData() |
                (uint)_cdRom.ReadData() << 8 |
                (uint)_cdRom.ReadData() << 16 |
                (uint)_cdRom.ReadData() << 24;

            _memory.Write(address, value);
            address = (address + (uint)sizeof(uint)) & AddressMask;
        }

        // The Rust DMA model owns CHCR completion and DICR flag semantics,
        // but this bridge already moved the whole burst's data itself in the
        // loop above, so it completes only this channel (Issue #587) instead
        // of ticking the generic per-cycle model, which would also age or
        // complete any other unrelated active DMA channel.
        _dma.CompleteChannel(Channel);

        return new(CdRomDmaTransferStatus.Completed, words, madr & AddressMask);
    }
}
