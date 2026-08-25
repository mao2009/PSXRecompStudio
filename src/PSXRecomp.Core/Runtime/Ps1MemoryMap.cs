using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 physical memory map definition.
/// Pure value type with no side effects.
/// </summary>
[Domain]
public static class Ps1MemoryMap
{
    public const uint RamStart = 0x00000000;
    public const uint RamEnd = 0x001FFFFF;
    public const uint RamSize = 0x200000;
    public const uint RamMirrorEnd = 0x007FFFFF;

    public const uint BiosStart = 0x1FC00000;
    public const uint BiosEnd = 0x1FC7FFFF;
    public const uint BiosSize = 0x80000;

    public const uint ScratchpadStart = 0x1F800000;
    public const uint ScratchpadEnd = 0x1F8003FF;

    public const uint HardwareRegisterStart = 0x1F801000;
    public const uint HardwareRegisterEnd = 0x1F801FFF;

    public const uint SpuStart = 0x1F801C00;
    public const uint SpuEnd = 0x1F801DFF;

    public const uint CacheControlAddress = 0xFFFE0130;

    public const uint InterruptStatus = 0x1F801070;
    public const uint InterruptMask = 0x1F801074;

    public const uint DmaBase = 0x1F801080;
    public const uint DmaControl = 0x1F8010F0;
    public const uint DmaInterruptControl = 0x1F8010F4;

    public const uint Timer0Base = 0x1F801100;
    public const uint Timer1Base = 0x1F801110;
    public const uint Timer2Base = 0x1F801120;

    public const uint CdRomBase = 0x1F801800;
    public const uint GpuGp0 = 0x1F801810;
    public const uint GpuGp1 = 0x1F801814;
    public const uint MdecBase = 0x1F801820;

    public static bool IsRam(uint address) => address <= RamMirrorEnd;
    public static bool IsBios(uint address) => address >= BiosStart && address <= BiosEnd;
    public static bool IsScratchpad(uint address) => address >= ScratchpadStart && address <= ScratchpadEnd;
    public static bool IsHardwareRegister(uint address) => address >= HardwareRegisterStart && address <= HardwareRegisterEnd;
    public static bool IsSpu(uint address) => address >= SpuStart && address <= SpuEnd;
    public static bool IsCacheControl(uint address) => address >= CacheControlAddress && address <= CacheControlAddress + 3;

    public static uint MaskRamAddress(uint address) => address & 0x1FFFFF;
    public static uint MaskBiosAddress(uint address) => (address - BiosStart) & 0x7FFFF;
}
