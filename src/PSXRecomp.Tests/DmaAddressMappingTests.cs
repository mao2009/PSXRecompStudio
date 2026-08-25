using PSXRecomp.Core;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

[Test]
public class DmaAddressMappingTests
{
    [Fact]
    public void DmaBase_IsCorrect()
    {
        Ps1MemoryMap.DmaBase.Should().Be(0x1F801080u);
    }

    [Fact]
    public void Dpcr_IsCorrect()
    {
        Ps1MemoryMap.Dpcr.Should().Be(0x1F8010F0u);
    }

    [Fact]
    public void Dicr_IsCorrect()
    {
        Ps1MemoryMap.Dicr.Should().Be(0x1F8010F4u);
    }

    [Fact]
    public void ChannelCount_Is7()
    {
        Ps1MemoryMap.ChannelCount.Should().Be(7);
    }

    [Theory]
    [InlineData(0, 0x1F801080u)]
    [InlineData(1, 0x1F801090u)]
    [InlineData(2, 0x1F8010A0u)]
    [InlineData(3, 0x1F8010B0u)]
    [InlineData(4, 0x1F8010C0u)]
    [InlineData(5, 0x1F8010D0u)]
    [InlineData(6, 0x1F8010E0u)]
    public void GetChannelMadr_ReturnsCorrectAddress(int channel, uint expected)
    {
        Ps1MemoryMap.GetChannelMadr(channel).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0x1F801084u)]
    [InlineData(1, 0x1F801094u)]
    [InlineData(2, 0x1F8010A4u)]
    [InlineData(3, 0x1F8010B4u)]
    [InlineData(4, 0x1F8010C4u)]
    [InlineData(5, 0x1F8010D4u)]
    [InlineData(6, 0x1F8010E4u)]
    public void GetChannelBcr_ReturnsCorrectAddress(int channel, uint expected)
    {
        Ps1MemoryMap.GetChannelBcr(channel).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0x1F801088u)]
    [InlineData(1, 0x1F801098u)]
    [InlineData(2, 0x1F8010A8u)]
    [InlineData(3, 0x1F8010B8u)]
    [InlineData(4, 0x1F8010C8u)]
    [InlineData(5, 0x1F8010D8u)]
    [InlineData(6, 0x1F8010E8u)]
    public void GetChannelChcr_ReturnsCorrectAddress(int channel, uint expected)
    {
        Ps1MemoryMap.GetChannelChcr(channel).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x1F801080u)]
    [InlineData(0x1F801084u)]
    [InlineData(0x1F801088u)]
    [InlineData(0x1F801090u)]
    [InlineData(0x1F8010E8u)]
    [InlineData(0x1F8010F0u)]
    [InlineData(0x1F8010F4u)]
    public void IsDmaRegister_ReturnsTrue_ForValidAddresses(uint address)
    {
        Ps1MemoryMap.IsDmaRegister(address).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x1F801000u)]
    [InlineData(0x1F801070u)]
    [InlineData(0x1F8010FCu)]
    [InlineData(0x1F802000u)]
    public void IsDmaRegister_ReturnsFalse_ForInvalidAddresses(uint address)
    {
        Ps1MemoryMap.IsDmaRegister(address).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x1F801080u, 0)]
    [InlineData(0x1F801090u, 1)]
    [InlineData(0x1F8010A0u, 2)]
    [InlineData(0x1F8010B0u, 3)]
    [InlineData(0x1F8010C0u, 4)]
    [InlineData(0x1F8010D0u, 5)]
    [InlineData(0x1F8010E0u, 6)]
    public void GetChannelIndex_ReturnsCorrectIndex(uint address, int expected)
    {
        Ps1MemoryMap.GetChannelIndex(address).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x1F801080u, DmaRegisterType.Madr)]
    [InlineData(0x1F801084u, DmaRegisterType.Bcr)]
    [InlineData(0x1F801088u, DmaRegisterType.Chcr)]
    [InlineData(0x1F8010F0u, DmaRegisterType.Dpcr)]
    [InlineData(0x1F8010F4u, DmaRegisterType.Dicr)]
    public void GetRegisterType_ReturnsCorrectType(uint address, DmaRegisterType expected)
    {
        Ps1MemoryMap.GetRegisterType(address).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x00000000u, MemoryRegionClass.Ram)]
    [InlineData(0x001FFFFFu, MemoryRegionClass.Ram)]
    [InlineData(0x1FC00000u, MemoryRegionClass.Bios)]
    [InlineData(0x1FC7FFFFu, MemoryRegionClass.Bios)]
    [InlineData(0x1F801000u, MemoryRegionClass.HardwareRegisters)]
    [InlineData(0x1F801FFFu, MemoryRegionClass.HardwareRegisters)]
    [InlineData(0x20000000u, MemoryRegionClass.Unmapped)]
    public void ClassifyRegion_ReturnsCorrectRegion(uint address, MemoryRegionClass expected)
    {
        Ps1MemoryMap.ClassifyRegion(address).Should().Be(expected);
    }
}
