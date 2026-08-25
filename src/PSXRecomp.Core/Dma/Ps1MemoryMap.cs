using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// PS1 physical memory map constants (Issue #44 Architecture contract).
/// </summary>
[Domain]
public static class Ps1MemoryMap
{
    public const uint RamBase = 0x00000000;
    public const uint RamSize = 2 * 1024 * 1024;
    public const uint RamEnd = RamBase + RamSize;

    public const uint BiosBase = 0x1FC00000;
    public const uint BiosSize = 512 * 1024;
    public const uint BiosEnd = BiosBase + BiosSize;

    public const uint HwRegBase = 0x1F801000;
    public const uint HwRegEnd = 0x1F802000;

    public const uint DmaBase = 0x1F801080;
    public const uint Dpcr = 0x1F8010F0;
    public const uint Dicr = 0x1F8010F4;

    public const int ChannelCount = 7;
    public const uint ChannelStride = 0x10;

    public static uint GetChannelMadr(int channel) =>
        DmaBase + (uint)(channel * (int)ChannelStride);

    public static uint GetChannelBcr(int channel) =>
        DmaBase + (uint)(channel * (int)ChannelStride) + 4;

    public static uint GetChannelChcr(int channel) =>
        DmaBase + (uint)(channel * (int)ChannelStride) + 8;

    public static bool IsDmaRegister(uint address) =>
        address >= DmaBase && address <= Dicr;

    public static int GetChannelIndex(uint address)
    {
        if (address < DmaBase || address >= DmaBase + ChannelCount * ChannelStride)
            return -1;
        return (int)((address - DmaBase) / ChannelStride);
    }

    public static DmaRegisterType GetRegisterType(uint address)
    {
        if (!IsDmaRegister(address))
            return DmaRegisterType.None;

        if (address == Dpcr)
            return DmaRegisterType.Dpcr;
        if (address == Dicr)
            return DmaRegisterType.Dicr;

        var _offset = (address - DmaBase) % ChannelStride;
        return _offset switch
        {
            0 => DmaRegisterType.Madr,
            4 => DmaRegisterType.Bcr,
            8 => DmaRegisterType.Chcr,
            _ => DmaRegisterType.None,
        };
    }

    public static MemoryRegionClass ClassifyRegion(uint address)
    {
        if (address < RamEnd)
            return MemoryRegionClass.Ram;
        if (address >= BiosBase && address < BiosEnd)
            return MemoryRegionClass.Bios;
        if (address >= HwRegBase && address < HwRegEnd)
            return MemoryRegionClass.HardwareRegisters;
        return MemoryRegionClass.Unmapped;
    }
}

/// <summary>
/// DMA register types within a channel or global.
/// </summary>
[Domain]
public enum DmaRegisterType
{
    None = 0,
    Madr,
    Bcr,
    Chcr,
    Dpcr,
    Dicr,
}

/// <summary>
/// Memory region classification for address routing.
/// </summary>
[Domain]
public enum MemoryRegionClass
{
    Unmapped = 0,
    Ram,
    Bios,
    HardwareRegisters,
}
