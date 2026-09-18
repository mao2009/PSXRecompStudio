using System.Collections.Generic;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// Locks PS1 GP0 packet word counts (from psx-spx/Nocash). The command word
/// carries the first vertex's color, so Gouraud only adds (vertices - 1) words,
/// and the polyline family is variable-length (terminator-scanned).
/// </summary>
[Test]
public sealed class Gp0CommandDecoderTests
{
    [Theory]
    [InlineData(0x20, 4)]          // monochrome triangle (opaque)
    [InlineData(0x22, 4)]          // monochrome triangle (semi-transparent)
    [InlineData(0x28, 5)]          // monochrome quad (opaque)
    [InlineData(0x24, 7)]          // textured triangle
    [InlineData(0x2C, 9)]          // textured quad
    [InlineData(0x30, 6)]          // Gouraud triangle
    [InlineData(0x3A, 8)]          // Gouraud quad (semi-transparent)
    [InlineData(0x34, 9)]          // Gouraud textured triangle
    [InlineData(0x3C, 12)]         // Gouraud textured quad
    [InlineData(0x40, 3)]          // monochrome single line
    [InlineData(0x50, 4)]          // Gouraud single line
    public void Decode_GivesPsxPacketWordCounts(byte opcode, int expectedWords)
    {
        var cmd = Gp0CommandDecoder.Decode((uint)opcode << 24);

        cmd.TotalWords.Should().Be(expectedWords);
        cmd.HasDataPhase.Should().BeFalse();
    }

    [Fact]
    public void Polyline_IsDiscardUntilTerminator_NotFixedCount()
    {
        var cmd = Gp0CommandDecoder.Decode(0x48000000);

        cmd.Result.Should().Be(GpuCommandResult.Unsupported);
        cmd.DiscardUntilTerminator.Should().BeTrue();
    }

    [Theory]
    [InlineData(0x55555555)]
    [InlineData(0x50005000)]
    [InlineData(0x51115222)]
    public void IsPolylineTerminator_Matches50005000Rule(uint word)
    {
        Gp0CommandDecoder.IsPolylineTerminator(word).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x00000000)]
    [InlineData(0x12345678)]
    [InlineData(0x60001111)]
    public void IsPolylineTerminator_RejectsNonTerminators(uint word)
    {
        Gp0CommandDecoder.IsPolylineTerminator(word).Should().BeFalse();
    }
}