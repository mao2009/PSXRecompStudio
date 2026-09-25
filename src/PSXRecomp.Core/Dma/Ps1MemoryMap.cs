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

    /// <summary>End of the physical window in which main RAM aliases (Issue #386). An address in this window maps to the physical byte <c>address &amp; (RamSize - 1)</c>.</summary>
    public const uint RamMirrorEnd = 0x00800000;

    public const uint BiosBase = 0x1FC00000;
    public const uint BiosSize = 512 * 1024;
    public const uint BiosEnd = BiosBase + BiosSize;

    public const uint ScratchpadBase = 0x1F800000;
    public const uint ScratchpadSize = 0x400;
    public const uint ScratchpadEnd = ScratchpadBase + ScratchpadSize;

    public const uint HwRegBase = 0x1F801000;
    public const uint HwRegEnd = 0x1F802000;

    public const uint IStat = 0x1F801070;
    public const uint IMask = 0x1F801074;

    public const uint DmaBase = 0x1F801080;
    public const uint Dpcr = 0x1F8010F0;
    public const uint Dicr = 0x1F8010F4;

    public const int ChannelCount = 7;
    public const uint ChannelStride = 0x10;

    public const uint TimerBase = 0x1F801100;
    public const uint TimerStride = 0x10;
    public const int TimerCount = 3;
    public const uint TimerCountOffset = 0x00;
    public const uint TimerModeOffset = 0x04;
    public const uint TimerTargetOffset = 0x08;

    public const uint GpuPort = 0x1F801810;      // GP0 (write) / GPUREAD (read)
    public const uint GpuStatusPort = 0x1F801814; // GP1 (write) / GPUSTAT (read)
    public const uint GpuPortEnd = GpuPort + 0x10; // 0x1F801818/0x1F80181C mirror 0x1F801810/0x1F801814

    // SPU register window (Issue #445). Canonical registers are 16-bit:
    // 24 voice blocks, global/control registers, then reverb registers.
    public const uint SpuBase = 0x1F801C00;
    public const uint SpuEnd = 0x1F801E00;
    public const uint SpuVoiceEnd = SpuBase + 0x180;
    public const uint SpuControlEnd = SpuBase + 0x1C0;

    // SIO0 (controller / memory card serial port), Issue #542. The routed window
    // is 0x1F801040-0x1F80105F; only the five named registers below carry state.
    public const uint Sio0Base = 0x1F801040;
    public const uint Sio0End = 0x1F801060;
    public const uint Sio0DataOffset = 0x00;
    public const uint Sio0StatusOffset = 0x04;
    public const uint Sio0ModeOffset = 0x08;
    public const uint Sio0ControlOffset = 0x0A;
    public const uint Sio0BaudOffset = 0x0E;

    public static bool IsSpuRegister(uint address) =>
        address >= SpuBase && address < SpuEnd;

    public static SpuRegisterType GetSpuRegisterType(uint address)
    {
        if (!IsSpuRegister(address))
            return SpuRegisterType.None;
        if (address < SpuVoiceEnd)
            return SpuRegisterType.Voice;
        return address < SpuControlEnd
            ? SpuRegisterType.Control
            : SpuRegisterType.Reverb;
    }

    public static bool IsSio0Register(uint address) =>
        address >= Sio0Base && address < Sio0End;

    /// <summary>
    /// Classifies an address in the SIO0 window. Addresses in the window that are
    /// not one of the five named registers (e.g. SIO_MISC at +0x0C, the upper
    /// halves of DATA/STAT, and 0x1F801050-0x1F80105F) return
    /// <see cref="Sio0RegisterType.Reserved"/>, never <see cref="Sio0RegisterType.None"/>.
    /// </summary>
    public static Sio0RegisterType GetSio0RegisterType(uint address)
    {
        if (!IsSio0Register(address))
            return Sio0RegisterType.None;

        return (address - Sio0Base) switch
        {
            Sio0DataOffset => Sio0RegisterType.Data,
            Sio0StatusOffset => Sio0RegisterType.Status,
            Sio0ModeOffset => Sio0RegisterType.Mode,
            Sio0ControlOffset => Sio0RegisterType.Control,
            Sio0BaudOffset => Sio0RegisterType.Baud,
            _ => Sio0RegisterType.Reserved,
        };
    }

    public static uint GetTimerBase(int timer) =>
        TimerBase + (uint)(timer * (int)TimerStride);

    public static bool IsTimerRegister(uint address) =>
        address >= TimerBase && address <= TimerBase + (uint)(TimerCount * (int)TimerStride) - 1;

    public static int GetTimerIndex(uint address)
    {
        if (!IsTimerRegister(address))
            return -1;
        return (int)((address - TimerBase) / TimerStride);
    }

    public static TimerRegisterType GetTimerRegisterType(uint address)
    {
        if (!IsTimerRegister(address))
            return TimerRegisterType.None;

        return ((address - TimerBase) % TimerStride) switch
        {
            0x00 => TimerRegisterType.Count,
            0x04 => TimerRegisterType.Mode,
            0x08 => TimerRegisterType.Target,
            _ => TimerRegisterType.None,
        };
    }

    public static uint GetChannelMadr(int channel) =>
        DmaBase + (uint)(channel * (int)ChannelStride);

    public static uint GetChannelBcr(int channel) =>
        DmaBase + (uint)(channel * (int)ChannelStride) + 4;

    public static uint GetChannelChcr(int channel) =>
        DmaBase + (uint)(channel * (int)ChannelStride) + 8;

    public static bool IsDmaRegister(uint address) =>
        address >= DmaBase && address <= Dicr;

    public static bool IsInterruptControllerRegister(uint address) =>
        address == IStat || address == IMask;

    public static bool IsGpuRegister(uint address) =>
        address >= GpuPort && address < GpuPortEnd;

    public static GpuRegisterType GetGpuRegisterType(uint address)
    {
        if (!IsGpuRegister(address))
            return GpuRegisterType.None;
        return ((address - GpuPort) & 0x4) == 0 ? GpuRegisterType.Data : GpuRegisterType.Status;
    }

    public static InterruptControllerRegisterType GetInterruptControllerRegisterType(uint address) =>
        address switch
        {
            IStat => InterruptControllerRegisterType.Status,
            IMask => InterruptControllerRegisterType.Mask,
            _ => InterruptControllerRegisterType.None,
        };

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
        if (address < RamMirrorEnd)
            return MemoryRegionClass.Ram;
        if (address >= ScratchpadBase && address < ScratchpadEnd)
            return MemoryRegionClass.Scratchpad;
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
/// Timer register types for a single root counter.
/// </summary>
[Domain]
public enum TimerRegisterType
{
    None = 0,
    Count,
    Mode,
    Target,
}

/// <summary>
/// Interrupt controller register types.
/// </summary>
[Domain]
public enum InterruptControllerRegisterType
{
    None = 0,
    Status,
    Mask,
}

/// <summary>
/// GPU I/O port types. GP0/GPUREAD share 0x1F801810; GP1/GPUSTAT share 0x1F801814.
/// </summary>
[Domain]
public enum GpuRegisterType
{
    None = 0,
    Data,
    Status,
}

/// <summary>
/// SIO0 register types (Issue #542). <see cref="Reserved"/> covers every address
/// in the SIO0 window that is not a named register: reads return 0, writes are
/// accepted and ignored.
/// </summary>
[Domain]
public enum Sio0RegisterType
{
    None = 0,
    Data,
    Status,
    Mode,
    Control,
    Baud,
    Reserved,
}

/// <summary>
/// SPU register category (Issue #445). All addresses in the 512-byte window
/// are routed to the Rust-owned register store; the categories describe the
/// hardware grouping only and do not imply audio synthesis side effects.
/// </summary>
[Domain]
public enum SpuRegisterType
{
    None = 0,
    Voice,
    Control,
    Reverb,
}

/// <summary>
/// Memory region classification for address routing.
/// </summary>
[Domain]
public enum MemoryRegionClass
{
    Unmapped = 0,
    Ram,
    Scratchpad,
    Bios,
    HardwareRegisters,
}
