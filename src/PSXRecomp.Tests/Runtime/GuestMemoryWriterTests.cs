using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The underlying RAM window is the existing canonical test memory model
// (RecompilerGuestMemory), so these tests prove the Runtime writer reuses the
// same KUSEG/KSEG translation rule already proven by the reader sliver
// (GuestMemoryReaderTests) and by the Recompiler sliver.
[Test]
public sealed class GuestMemoryWriterTests
{
    [Fact]
    public void TryWriteByte_Valid_KusegAddress_StoresByte()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);

        var result = writer.TryWriteByte(0x00000000, 0xAB);

        result.Should().BeTrue();
        ram.Read8(0x00000000).Should().Be(0xAB);
    }

    [Theory]
    [InlineData(0x00000100u)]
    [InlineData(0x80000100u)]
    [InlineData(0xA0000100u)]
    public void TryWriteByte_Translation_Reuses_KusegKsegRule(uint address)
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);

        var result = writer.TryWriteByte(address, 0x4D);

        result.Should().BeTrue();
        ram.Read8(0x00000100).Should().Be(0x4D, "KUSEG, KSEG0 and KSEG1 aliases must map to the same physical byte");
    }

    [Fact]
    public void TryWriteByte_Kseg2Address_IsUntranslatable()
    {
        var writes = 0;
        var writer = new GuestMemoryWriter((_, _) => writes++);

        var result = writer.TryWriteByte(0xC0000000, 0x11);

        result.Should().BeFalse();
        writes.Should().Be(0, "an untranslatable address must never reach the physical write path");
    }

    [Fact]
    public void TryWriteByte_KusegNonRam_IsUnmapped()
    {
        var writes = 0;
        var writer = new GuestMemoryWriter((_, _) => writes++);

        var result = writer.TryWriteByte(0x00200000, 0x11);

        result.Should().BeFalse();
        writes.Should().Be(0, "an out-of-RAM address must never reach the physical write path");
    }

    [Fact]
    public void TryWriteByte_AtRamEnd_IsUnmapped()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);

        var result = writer.TryWriteByte(RecompilerGuestMemory.RamSize, 0x11);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryWriteByte_RepeatedWrites_AreDeterministic()
    {
        var ram1 = new RecompilerGuestMemory();
        var ram2 = new RecompilerGuestMemory();
        var writer1 = new GuestMemoryWriter(ram1.Write8);
        var writer2 = new GuestMemoryWriter(ram2.Write8);

        writer1.TryWriteByte(0x00000080, 0x2E).Should().BeTrue();
        writer2.TryWriteByte(0x00000080, 0x2E).Should().BeTrue();

        ram1.Read8(0x00000080).Should().Be(ram2.Read8(0x00000080));
    }

    [Fact]
    public void FreshInstances_DoNotShareState()
    {
        var ramA = new RecompilerGuestMemory();
        var ramB = new RecompilerGuestMemory();
        var writerA = new GuestMemoryWriter(ramA.Write8);

        writerA.TryWriteByte(0x00000000, 0xFF);

        ramA.Read8(0x00000000).Should().Be(0xFF);
        ramB.Read8(0x00000000).Should().Be(0, "a fresh backing store must not observe another instance's write");
    }

    [Fact]
    public void TryWriteByte_ThenReadBack_RoundTripsThroughReaderBoundary()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        var reader = new GuestMemoryReader(ram.Read8);

        writer.TryWriteByte(0x00000010, 0x7A).Should().BeTrue();
        reader.TryReadByte(0x00000010, out var value).Should().BeTrue();

        value.Should().Be(0x7A);
    }

    [Fact]
    public void Constructor_NullSink_Throws()
    {
        var act = () => new GuestMemoryWriter(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryWrite_MultiByteRange_WritesAllBytesAndReadsBackByByte()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);

        var result = writer.TryWrite(0x00000010, new byte[] { 0x11, 0x22, 0x33, 0x44 });

        result.Should().BeTrue();
        ram.Read8(0x00000010).Should().Be(0x11);
        ram.Read8(0x00000011).Should().Be(0x22);
        ram.Read8(0x00000012).Should().Be(0x33);
        ram.Read8(0x00000013).Should().Be(0x44);
    }

    [Fact]
    public void TryWrite_RangePartiallyOutOfRam_IsAllOrNothingRejected()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);

        // Write a sentinel at an in-bound address so we can verify it was not touched.
        ram.Write8(RecompilerGuestMemory.RamSize - 1, 0xAA);

        // The range straddles the RAM boundary: first byte is in-bound, second is not.
        Span<byte> buffer = stackalloc byte[2] { 0xBB, 0xCC };
        var result = writer.TryWrite(RecompilerGuestMemory.RamSize - 1, buffer);

        result.Should().BeFalse();
        ram.Read8(RecompilerGuestMemory.RamSize - 1).Should().Be(0xAA, "a rejected write must not touch any byte");
    }

    [Fact]
    public void TryWrite_AddressPlusLengthOverflows_IsRejected()
    {
        var writes = 0;
        var writer = new GuestMemoryWriter((_, _) => writes++);

        var result = writer.TryWrite(0xFFFFFFFF, new byte[] { 0x01, 0x02 });

        result.Should().BeFalse();
        writes.Should().Be(0, "an overflow-guarded request must never reach the physical write path");
    }

    [Fact]
    public void TryWrite_LengthGreaterThanRamSize_IsRejected()
    {
        var writes = 0;
        var writer = new GuestMemoryWriter((_, _) => writes++);
        var buffer = new byte[RecompilerGuestMemory.RamSize + 1];

        var result = writer.TryWrite(0x00000000, buffer);

        result.Should().BeFalse();
        writes.Should().Be(0, "an oversized request must be rejected before touching the physical write path");
    }

    [Fact]
    public void TryWrite_EmptyBuffer_Succeeds()
    {
        var writer = new GuestMemoryWriter(new RecompilerGuestMemory().Write8);

        var result = writer.TryWrite(0x00000000, ReadOnlySpan<byte>.Empty);

        result.Should().BeTrue("an empty write is vacuously valid");
    }
}
