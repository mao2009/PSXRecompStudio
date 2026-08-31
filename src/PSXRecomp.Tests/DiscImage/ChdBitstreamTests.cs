using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Focused tests for ChdBitstream.Peek, guarding the byte-boundary crossing fix.
/// Validates that Peek is a general bitstream primitive, not tuned to a single CHD.
/// </summary>
[Test]
public class ChdBitstreamTests
{
    [Fact]
    public void PeekByte_BoundaryExact_DoesNotAdvancePosition()
    {
        // 0b10101010
        var stream = new ChdBitstream([0xAA, 0x55]);

        // Exactly 8 bits available at the start.
        int peeked = stream.Peek(8);
        peeked.Should().Be(0xAA);

        // Position unchanged after peek.
        stream.Read(8).Should().Be(0xAA);
        stream.Read(8).Should().Be(0x55);
    }

    [Fact]
    public void PeekByte_MidByteCrossesBoundary()
    {
        // 0b11110000 0b10101010
        var stream = new ChdBitstream([0xF0, 0xAA, 0xFF]);

        stream.Read(4).Should().Be(0b1111);

        // Remaining bits: 0000 10101010 11111111.
        // An 8-bit peek from a 4-bit offset must cross the byte boundary and
        // return the true next 8 bits (00001010 = 0x0A).
        stream.Peek(8).Should().Be(0x0A);

        // Ensure position did not advance (only the temporary copy crossed).
        stream.Read(4).Should().Be(0b0000); // remaining low 4 bits of 0xF0
        stream.Read(8).Should().Be(0xAA);  // next full byte
    }

    [Fact]
    public void PeekBoundaryImmediatelyBeforeCrossing()
    {
        // Peek(2) at a point where only 2 bits remain in the byte; peek source crosses.
        var stream = new ChdBitstream([0xC0, 0x07]);
        stream.Read(6).Should().Be(0b110000);

        // Only 2 bits left in first byte; peek(2) must take both from the first byte.
        stream.Peek(2).Should().Be(0b00);
        stream.Read(2).Should().Be(0b00);

        // Next bits are the second byte.
        stream.Read(8).Should().Be(0x07);
    }

    [Fact]
    public void PeekAcrossMultipleBytes_MatchesReferenceImplementation()
    {
        var data = new byte[] { 0xF0, 0x0F, 0xAA, 0x55 };
        var stream = new ChdBitstream(data);

        stream.Read(4); // consume 0xF0 high nibble

        // Reference: concatenate remaining bits (0000 00001111 10101010 01010101)
        // starting after the consumed nibble, take up to 8 bits => 0x00.
        stream.Peek(8).Should().Be(0x00);

        // Consume exactly 4 to clear the low nibble of 0xF0, then peek middle.
        stream.Read(4);
        stream.Peek(8).Should().Be(0x0F);
        stream.Peek(8).Should().Be(0x0F); // still unchanged (idempotent)
    }

    [Fact]
    public void PeekDoesNotChangeSubsequentReads()
    {
        var stream = new ChdBitstream([0x80, 0x00]);
        stream.Read(1); // consume MSB

        // Peek at a boundary-crossing 8-bit window: bits 1..8 (0 padded via 0 byte).
        int a = stream.Peek(8);
        int b = stream.Peek(8);
        int c = (int)stream.Read(8); // actual read must equal peek, and not double-advance

        a.Should().Be(b);
        b.Should().Be(c);
    }

    [Fact]
    public void PeekHandleAllBitsVerifyAgainstManual()
    {
        var bytes = new byte[] { 0xA5, 0x5A, 0x3C, 0xC3 };
        var stream = new ChdBitstream(bytes);

        // Manually walk every position, comparing Peek(8) against the true next 8 bits.
        var allBits = new List<int>();
        foreach (var b in bytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                allBits.Add((b >> i) & 1);
            }
        }

        for (int pos = 0; pos <= allBits.Count - 8; pos++)
        {
            var stream2 = new ChdBitstream(bytes);
            for (int i = 0; i < pos; i++)
            {
                stream2.Read(1);
            }

            int expected = 0;
            for (int j = 0; j < 8; j++)
            {
                expected = (expected << 1) | allBits[pos + j];
            }
            stream2.Peek(8).Should().Be(expected, $"at bit position {pos}");
        }
    }

    [Fact]
    public void ReadBit_BoundaryAndEndianness()
    {
        var stream = new ChdBitstream([0xAA]);
        stream.Read(8).Should().Be(0xAA);

        // 0xAA = 10101010; MSB-first order.
        var stream2 = new ChdBitstream([0xAA]);
        stream2.Read(1).Should().Be(1);
        stream2.Read(1).Should().Be(0);
        stream2.Read(1).Should().Be(1);
        stream2.Read(1).Should().Be(0);
    }

    [Fact]
    public void PeekPastEnd_ThrowsWithinBounds()
    {
        // 1 byte = 8 bits. A 8-bit peek at position 4 fits exactly (bits 4..11 => needs byte 2),
        // so peeking at the very last byte boundary is OK, but peeking an 8-bit window
        // that would require reading beyond the final byte (position 1 of a 1-byte stream)
        // must throw.
        var stream = new ChdBitstream([0x00]);
        stream.Read(4);
        stream.Invoking(s => s.Peek(8)).Should().Throw<InvalidOperationException>();
    }
}
