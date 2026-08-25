using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// MMIO route resolution result (Issue #44 Architecture contract).
/// Maps a physical address to a handler target and offset.
/// </summary>
[Domain]
public readonly struct MmioRoute
{
    public MmioTarget Target { get; init; }
    public uint Offset { get; init; }
    public int ChannelIndex { get; init; }
    public DmaRegisterType RegisterType { get; init; }

    public static MmioRoute Unmapped => new() { Target = MmioTarget.None };

    public static MmioRoute ForDma(int channel, DmaRegisterType registerType, uint offset) =>
        new()
        {
            Target = MmioTarget.DmaController,
            ChannelIndex = channel,
            RegisterType = registerType,
            Offset = offset,
        };

    public static MmioRoute ForDpcr(uint offset) =>
        new()
        {
            Target = MmioTarget.DmaController,
            ChannelIndex = -1,
            RegisterType = DmaRegisterType.Dpcr,
            Offset = offset,
        };

    public static MmioRoute ForDicr(uint offset) =>
        new()
        {
            Target = MmioTarget.DmaController,
            ChannelIndex = -1,
            RegisterType = DmaRegisterType.Dicr,
            Offset = offset,
        };

    public static MmioRoute Resolve(uint address)
    {
        if (!Ps1MemoryMap.IsDmaRegister(address))
            return Unmapped;

        var _registerType = Ps1MemoryMap.GetRegisterType(address);
        var _channelIndex = Ps1MemoryMap.GetChannelIndex(address);

        return _registerType switch
        {
            DmaRegisterType.Dpcr => ForDpcr(address - Ps1MemoryMap.Dpcr),
            DmaRegisterType.Dicr => ForDicr(address - Ps1MemoryMap.Dicr),
            DmaRegisterType.Madr => ForDma(_channelIndex, _registerType, address - Ps1MemoryMap.GetChannelMadr(_channelIndex)),
            DmaRegisterType.Bcr => ForDma(_channelIndex, _registerType, address - Ps1MemoryMap.GetChannelBcr(_channelIndex)),
            DmaRegisterType.Chcr => ForDma(_channelIndex, _registerType, address - Ps1MemoryMap.GetChannelChcr(_channelIndex)),
            _ => Unmapped,
        };
    }
}

/// <summary>
/// MMIO handler target classification.
/// </summary>
[Domain]
public enum MmioTarget
{
    None = 0,
    DmaController,
}
