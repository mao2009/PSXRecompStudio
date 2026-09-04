using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Verifies that <see cref="RecompilerGuestMemory"/> matches the native
/// <c>PSXMemory</c> bounds semantics: out-of-range reads return 0 and
/// out-of-range writes are silently ignored (see
/// <c>src/PSXRecomp.Native/src/psx_memory.h</c>).
/// </summary>
[Test]
public class RecompilerGuestMemoryBoundsTests
{
    private const uint RamSize = RecompilerGuestMemory.RamSize;

    [Fact]
    public void Read8_AtLastValidAddress_ReturnsStoredValue()
    {
        var memory = CreateMemory();
        var addr = RamSize - 1;
        WriteByte(memory, addr, 0xAB);

        memory.Read8(addr).Should().Be(0xAB);
    }

    [Fact]
    public void Read8_AtRamSize_ReturnsZero()
    {
        var memory = CreateMemory();

        memory.Read8(RamSize).Should().Be(0);
    }

    [Fact]
    public void Read8_BeyondRamSize_ReturnsZero()
    {
        var memory = CreateMemory();

        memory.Read8(RamSize + 0x1000).Should().Be(0);
    }

    [Fact]
    public void Read16_AtLastValidTwoByteBoundary_ReturnsStoredValue()
    {
        var memory = CreateMemory();
        var addr = RamSize - 2;
        WriteByte(memory, addr, 0xCD);
        WriteByte(memory, addr + 1, 0xAB);

        memory.Read16(addr).Should().Be(0xABCD);
    }

    [Fact]
    public void Read16_CrossingRamSizeBoundary_ReturnsZero()
    {
        var memory = CreateMemory();
        WriteByte(memory, RamSize - 1, 0xFF);

        memory.Read16(RamSize - 1).Should().Be(0);
    }

    [Fact]
    public void Read16_AtRamSize_ReturnsZero()
    {
        var memory = CreateMemory();

        memory.Read16(RamSize).Should().Be(0);
    }

    [Fact]
    public void Read32_AtLastValidFourByteBoundary_ReturnsStoredValue()
    {
        var memory = CreateMemory();
        var addr = RamSize - 4;
        WriteByte(memory, addr, 0x78);
        WriteByte(memory, addr + 1, 0x56);
        WriteByte(memory, addr + 2, 0x34);
        WriteByte(memory, addr + 3, 0x12);

        memory.Read32(addr).Should().Be(0x12345678);
    }

    [Fact]
    public void Read32_CrossingRamSizeBoundary_ReturnsZero()
    {
        var memory = CreateMemory();
        WriteByte(memory, RamSize - 2, 0xFF);

        memory.Read32(RamSize - 2).Should().Be(0);
    }

    [Fact]
    public void Read32_AtRamSize_ReturnsZero()
    {
        var memory = CreateMemory();

        memory.Read32(RamSize).Should().Be(0);
    }

    [Fact]
    public void Write8_BeyondRamSize_IsIgnored()
    {
        var memory = CreateMemory();

        memory.Write8(RamSize, 0xAB);

        memory.Read8(RamSize).Should().Be(0);
    }

    [Fact]
    public void Write16_BeyondRamSize_IsIgnored()
    {
        var memory = CreateMemory();

        memory.Write16(RamSize, 0xABCD);

        memory.Read16(RamSize).Should().Be(0);
    }

    [Fact]
    public void Write16_CrossingRamSizeBoundary_IsIgnored()
    {
        var memory = CreateMemory();

        memory.Write16(RamSize - 1, 0xABCD);

        memory.Read8(RamSize - 1).Should().Be(0);
    }

    [Fact]
    public void Write32_BeyondRamSize_IsIgnored()
    {
        var memory = CreateMemory();

        memory.Write32(RamSize, 0x12345678);

        memory.Read32(RamSize).Should().Be(0);
    }

    [Fact]
    public void Write32_CrossingRamSizeBoundary_IsIgnored()
    {
        var memory = CreateMemory();

        memory.Write32(RamSize - 2, 0x12345678);

        memory.Read8(RamSize - 2).Should().Be(0);
        memory.Read8(RamSize - 1).Should().Be(0);
    }

    [Fact]
    public void Write32_AtLastValidFourByteBoundary_StoresAndReads()
    {
        var memory = CreateMemory();
        var addr = RamSize - 4;

        memory.Write32(addr, 0xDEADBEEF);

        memory.Read32(addr).Should().Be(0xDEADBEEF);
    }

    [Fact]
    public void OutOfBoundsRead_DoesNotCorruptAdjacentInBoundsData()
    {
        var memory = CreateMemory();
        var lastAddr = RamSize - 1;
        WriteByte(memory, lastAddr, 0x42);

        memory.Read8(RamSize);

        memory.Read8(lastAddr).Should().Be(0x42);
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x001FFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData(0x801FFFFFu)]
    [InlineData(0xA0000000u)]
    [InlineData(0xA01FFFFFu)]
    public void KUSEG_KSEG0_KSEG1_PhysicalAddress_IsWithinRam(uint virtualAddress)
    {
        var physical = RecompilerGuestMemory.Translate(virtualAddress);

        physical.Should().BeLessThan(RamSize);
    }

    private static RecompilerGuestMemory CreateMemory()
    {
        return new RecompilerGuestMemory();
    }

    private static void WriteByte(RecompilerGuestMemory memory, uint address, byte value)
    {
        memory.Write8(address, value);
    }
}
