using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The underlying RAM window is the existing canonical test memory model
// (RecompilerGuestMemory), so these tests prove the Runtime reader reuses the
// same KUSEG/KSEG translation rule already proven by the Recompiler sliver.
[Test]
public sealed class GuestMemoryReaderTests
{
    [Fact]
    public void TryReadByte_Valid_KusegAddress_ReturnsStoredByte()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000000, 0xAB);
        var reader = new GuestMemoryReader(ram.Read8);

        var result = reader.TryReadByte(0x00000000, out var value);

        result.Should().BeTrue();
        value.Should().Be(0xAB);
    }

    [Fact]
    public void TryRead_ContiguousBytes_AcrossWordBoundary()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write32(0x00000000, 0x04030201);
        ram.Write32(0x00000004, 0x08070605);
        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> buffer = stackalloc byte[6];

        var result = reader.TryRead(0x00000003, buffer);

        result.Should().BeTrue();
        buffer.ToArray().Should().Equal(0x04, 0x05, 0x06, 0x07, 0x08, 0x00);
    }

    [Theory]
    [InlineData(0x00000100u)]
    [InlineData(0x80000100u)]
    [InlineData(0xA0000100u)]
    public void TryReadByte_Translation_Reuses_KusegKsegRule(uint address)
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000100, 0x4D);
        var reader = new GuestMemoryReader(ram.Read8);

        var result = reader.TryReadByte(address, out var value);

        result.Should().BeTrue();
        value.Should().Be(0x4D, "KUSEG, KSEG0 and KSEG1 aliases must map to the same physical byte");
    }

    [Fact]
    public void TryReadByte_Kseg2Address_IsUntranslatable()
    {
        var reader = new GuestMemoryReader(new RecompilerGuestMemory().Read8);

        var result = reader.TryReadByte(0xC0000000, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryReadByte_KusegBeyondMirrorWindow_IsUnmapped()
    {
        var reader = new GuestMemoryReader(new RecompilerGuestMemory().Read8);

        var result = reader.TryReadByte(0x00800000, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryReadByte_MirrorWindow_AliasesLow2MiB()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000100, 0x4D);
        var reader = new GuestMemoryReader(ram.Read8);

        // Physical 0x00200100 falls inside the mirror window (2 MiB above the
        // canonical byte); the reader folds it back into the low 2 MiB (#386).
        var mirrored = reader.TryReadByte(0x00200100, out var mirroredValue);

        mirrored.Should().BeTrue();
        mirroredValue.Should().Be(0x4D, "the mirror window must alias the low 2 MiB RAM");
    }

    [Fact]
    public void TryRead_CrossingMirrorWindowEnd_IsRejectedAtomically()
    {
        var ram = new RecompilerGuestMemory();
        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> buffer = stackalloc byte[4];

        var result = reader.TryRead(4 * RecompilerGuestMemory.RamSize - 1, buffer);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryReadByte_AtMirrorWindowEnd_IsUnmapped()
    {
        var reader = new GuestMemoryReader(new RecompilerGuestMemory().Read8);

        var result = reader.TryReadByte(4 * RecompilerGuestMemory.RamSize, out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryReadByte_RepeatedReads_AreDeterministic()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000080, 0x2E);
        var reader = new GuestMemoryReader(ram.Read8);

        reader.TryReadByte(0x00000080, out var first).Should().BeTrue();
        reader.TryReadByte(0x00000080, out var second).Should().BeTrue();

        first.Should().Be(second);
    }

    [Fact]
    public void TryTranslate_Kseg1_MasksRegionBits()
    {
        Ps1AddressTranslation.TryTranslate(0xA0000100, out var physical).Should().BeTrue();
        physical.Should().Be(0x00000100);
    }

    [Fact]
    public void TryTranslate_Kseg2_ReturnsFalse()
    {
        Ps1AddressTranslation.TryTranslate(0xC0000000, out _).Should().BeFalse();
    }

    [Fact]
    public void TryTranslate_Kuseg_Identity()
    {
        Ps1AddressTranslation.TryTranslate(0x00000100, out var physical).Should().BeTrue();
        physical.Should().Be(0x00000100);
    }

    [Fact]
    public void TryTranslate_Kseg0_MasksRegionBits()
    {
        Ps1AddressTranslation.TryTranslate(0x80000100, out var physical).Should().BeTrue();
        physical.Should().Be(0x00000100);
    }

    [Fact]
    public void TryRead_Failure_LeavesBufferUntouched()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000000, 0xAA);
        ram.Write8(0x00000001, 0xBB);
        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> buffer = stackalloc byte[2] { 0xFF, 0xFF };

        var result = reader.TryRead(0xC0000000, buffer);

        result.Should().BeFalse();
        buffer[0].Should().Be(0xFF, "buffer must not be modified on failure");
        buffer[1].Should().Be(0xFF, "buffer must not be modified on failure");
    }

    [Fact]
    public void TryRead_PartialFailure_LeavesBufferUntouched()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x001FFFFF, 0xAA);
        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> buffer = stackalloc byte[2] { 0xFF, 0xFF };

        // Guest 0x007FFFFF is the last byte of the mirror window; the next byte
        // (0x00800000) lies past it, so the read rejects atomically.
        var result = reader.TryRead(0x007FFFFF, buffer);

        result.Should().BeFalse();
        buffer[0].Should().Be(0xFF, "buffer must not be modified on partial failure");
        buffer[1].Should().Be(0xFF, "buffer must not be modified on partial failure");
    }

    [Fact]
    public void TryRead_AddressNearMaxUint_OverflowIsRejected()
    {
        var ram = new RecompilerGuestMemory();
        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> buffer = stackalloc byte[4];

        var result = reader.TryRead(0xFFFFFFFF, buffer);

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 2)]
    [InlineData(0xFFFFFFFEu, 3)]
    public void TryRead_AddressPlusLengthOverflows_IsRejected(uint address, int length)
    {
        var ram = new RecompilerGuestMemory();
        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> buffer = stackalloc byte[length];

        var result = reader.TryRead(address, buffer);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryRead_LengthGreaterThanMirrorWindow_IsRejectedWithoutPhysicalAccess()
    {
        var physicalReads = 0;
        var reader = new GuestMemoryReader(_ =>
        {
            physicalReads++;
            return 0;
        });
        var buffer = new byte[4 * RecompilerGuestMemory.RamSize + 1];
        buffer.AsSpan().Fill(0xFF);

        var result = reader.TryRead(0x00000000, buffer);

        result.Should().BeFalse();
        physicalReads.Should().Be(0, "oversized requests must be rejected before touching the physical reader");
        buffer.Should().OnlyContain(b => b == 0xFF, "caller buffer must not be modified on failure");
    }

    [Fact]
    public void TryRead_LengthAtMirrorWindowEndFromNonZeroStart_FailsAtomically()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(RecompilerGuestMemory.RamSize - 1, 0xAA);
        var reader = new GuestMemoryReader(ram.Read8);
        var buffer = new byte[4 * RecompilerGuestMemory.RamSize];
        buffer.AsSpan().Fill(0xFF);

        var result = reader.TryRead(4 * RecompilerGuestMemory.RamSize - 1, buffer);

        result.Should().BeFalse();
        buffer.Should().OnlyContain(b => b == 0xFF, "buffer must not be modified on partial failure");
    }
}
