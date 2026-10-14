using FluentAssertions;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// Regression tests for 24-bit VRAM address calculation in ScreenshotCapture (Issue #762, PR #771).
/// These tests verify the fix for the VRAM byte offset calculation where DisplayVramStart.X
/// is in 16-bit halfword units, not byte units.
/// </summary>
[Test]
public sealed class ScreenshotCapture24BitTests
{
    /// <summary>
    /// Creates a GPU in 24-bit mode with default 256x240 resolution.
    /// </summary>
    private static GpuDevice Create24BitGpu()
    {
        var gpu = new GpuDevice();
        gpu.WriteGP1(0x08000010u); // DisplayMode bit 4 = 1 (24-bit color depth)
        // Default resolution: 256x240 (DisplayMode bits 1:0=0, bit 2=0, bit 6=0)
        return gpu;
    }

    /// <summary>
    /// Sets DisplayVramStart (GP1(05h)). X and Y are in halfword units (0-1023, 0-511).
    /// </summary>
    private static void SetDisplayVramStart(GpuDevice gpu, int x, int y)
    {
        // GP1(05h): param = X | (Y << 10), command = 0x05000000 | param
        uint param = (uint)(x & 0x3FF) | ((uint)(y & 0x1FF) << 10);
        gpu.WriteGP1(0x05000000u | param);
    }

    [Fact]
    public void ToBgra32_24Bit_StartXZero_ProducesCorrectPixels()
    {
        using var gpu = Create24BitGpu();

        var frame = gpu.CaptureFrame();
        frame.Width.Should().Be(256);
        frame.Height.Should().Be(240);

        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);

        // With startX=0, startY=0, VRAM is zeroed -> all pixels black
        bgra.Length.Should().Be(256 * 240 * 4);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i].Should().Be(0);     // B
            bgra[i + 1].Should().Be(0); // G
            bgra[i + 2].Should().Be(0); // R
            bgra[i + 3].Should().Be(0xFF); // A
        }
    }

    [Fact]
    public void ToBgra32_24Bit_StartXNonZero_CalculatesCorrectByteOffset()
    {
        // This test verifies the fix: startX is in halfword units, so byte offset = startX * 2
        // The old buggy code did: vramX * 3 where vramX = startX + x
        // The correct code does: startX * 2 + x * 3

        using var gpu = Create24BitGpu();
        SetDisplayVramStart(gpu, 100, 0); // X=100 halfwords, Y=0

        // Write RGB(0xAA, 0xBB, 0xCC) at VRAM halfword X=100 (byte offset 200)
        // Halfword 100: low=R=0xAA, high=G=0xBB
        // Halfword 101: low=B=0xCC
        gpu.Vram[100, 0] = 0xBBAA;
        gpu.Vram[101, 0] = (ushort)(0xCC | (gpu.Vram[101, 0] & 0xFF00));

        var frame = gpu.CaptureFrame();
        // Resolution is 256, VRAM width is 1024, startX=100 -> clipped to min(256, 1024-100) = 256
        frame.Width.Should().Be(256);
        frame.Height.Should().Be(240);

        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);

        // First pixel (x=0, y=0) reads from VRAM byte offset startX*2 = 200
        // Which we set to RGB(0xAA, 0xBB, 0xCC) -> BGRA(0xCC, 0xBB, 0xAA, 0xFF)
        bgra[0].Should().Be(0xCC);    // B
        bgra[1].Should().Be(0xBB);    // G
        bgra[2].Should().Be(0xAA);    // R
        bgra[3].Should().Be(0xFF);    // A
    }

    [Fact]
    public void ToBgra32_24Bit_RgbToBgraChannelOrder()
    {
        using var gpu = Create24BitGpu();
        SetDisplayVramStart(gpu, 0, 0);

        // Write 4 pixels with distinct RGB values at VRAM (0,0)
        // Pixel 0: RGB(0x11, 0x22, 0x33) -> bytes 0,1,2
        // Pixel 1: RGB(0x44, 0x55, 0x66) -> bytes 3,4,5
        // Pixel 2: RGB(0x77, 0x88, 0x99) -> bytes 6,7,8
        // Pixel 3: RGB(0xAA, 0xBB, 0xCC) -> bytes 9,10,11
        gpu.Vram[0, 0] = 0x2211;      // R=0x11, G=0x22
        gpu.Vram[1, 0] = 0x4433;      // B=0x33, p1R=0x44
        gpu.Vram[2, 0] = 0x6655;      // p1G=0x55, p1B=0x66
        gpu.Vram[3, 0] = 0x8877;      // p2R=0x77, p2G=0x88
        gpu.Vram[4, 0] = 0xAA99;      // p2B=0x99, p3R=0xAA
        gpu.Vram[5, 0] = 0xCCBB;      // p3G=0xBB, p3B=0xCC

        var frame = gpu.CaptureFrame();
        frame.Width.Should().Be(256);
        frame.Height.Should().Be(240);

        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);

        // Pixel 0: RGB(0x11,0x22,0x33) -> BGRA(0x33,0x22,0x11,0xFF)
        bgra[0].Should().Be(0x33);
        bgra[1].Should().Be(0x22);
        bgra[2].Should().Be(0x11);
        bgra[3].Should().Be(0xFF);

        // Pixel 1: RGB(0x44,0x55,0x66) -> BGRA(0x66,0x55,0x44,0xFF)
        bgra[4].Should().Be(0x66);
        bgra[5].Should().Be(0x55);
        bgra[6].Should().Be(0x44);
        bgra[7].Should().Be(0xFF);

        // Pixel 2: RGB(0x77,0x88,0x99) -> BGRA(0x99,0x88,0x77,0xFF)
        bgra[8].Should().Be(0x99);
        bgra[9].Should().Be(0x88);
        bgra[10].Should().Be(0x77);
        bgra[11].Should().Be(0xFF);

        // Pixel 3: RGB(0xAA,0xBB,0xCC) -> BGRA(0xCC,0xBB,0xAA,0xFF)
        bgra[12].Should().Be(0xCC);
        bgra[13].Should().Be(0xBB);
        bgra[14].Should().Be(0xAA);
        bgra[15].Should().Be(0xFF);
    }

    [Fact]
    public void ToBgra32_24Bit_RowBoundary_StrideIs2048Bytes()
    {
        // Verify that row stride is 2048 bytes (1024 halfwords * 2)
        using var gpu = Create24BitGpu();
        SetDisplayVramStart(gpu, 0, 0);

        // Row 0, pixel 0: RGB(0x11, 0x22, 0x33) at byte offset 0
        gpu.Vram[0, 0] = 0x2211;
        gpu.Vram[1, 0] = 0x0033; // B=0x33

        // Row 1, pixel 0: RGB(0x44, 0x55, 0x66) at byte offset 2048 (row 1)
        // Row 1 starts at halfword Y=1, X=0 (VRAM coordinate (0, 1))
        gpu.Vram[0, 1] = 0x5544;
        gpu.Vram[1, 1] = 0x0066;

        var frame = gpu.CaptureFrame();
        frame.Width.Should().Be(256);
        frame.Height.Should().Be(240);

        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);

        // Row 0, pixel 0 (index 0)
        bgra[0].Should().Be(0x33);
        bgra[1].Should().Be(0x22);
        bgra[2].Should().Be(0x11);
        bgra[3].Should().Be(0xFF);

        // Row 1, pixel 0 (index = 1 * 256 * 4 = 1024)
        int row1Index = 1 * 256 * 4;
        bgra[row1Index + 0].Should().Be(0x66);
        bgra[row1Index + 1].Should().Be(0x55);
        bgra[row1Index + 2].Should().Be(0x44);
        bgra[row1Index + 3].Should().Be(0xFF);
    }

    [Fact]
    public void ToBgra32_24Bit_VramEnd_ClipsCorrectly()
    {
        // Test that display region at VRAM boundary is clipped, not wrapped
        using var gpu = Create24BitGpu();
        // Set DisplayVramStart near VRAM right edge: X=1016 (halfwords), so width clipped to 8
        SetDisplayVramStart(gpu, 1016, 0);

        var frame = gpu.CaptureFrame();
        // Width clipped to 1024 - 1016 = 8 (resolution is 256, but VRAM only has 8 halfwords left)
        frame.Width.Should().Be(8);
        frame.Height.Should().Be(240);

        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);
        bgra.Length.Should().Be(8 * 240 * 4);
    }

    [Fact]
    public void ToBgra32_24BitAnd15Bit_Switching_WorksCorrectly()
    {
        using var gpu = new GpuDevice();

        // First: 15-bit mode (default)
        gpu.WriteGP1(0x08000000u); // 15-bit mode

        // Write a 15-bit pixel: 0x7FFF (white)
        gpu.Vram[0, 0] = 0x7FFF;

        var frame15 = gpu.CaptureFrame();
        var bgra15 = ScreenshotCapture.ToBgra32(frame15, gpu);

        // 15-bit 0x7FFF -> R=31, G=31, B=31 -> expanded to 8-bit: 0xFF each
        bgra15[0].Should().Be(0xFF); // B
        bgra15[1].Should().Be(0xFF); // G
        bgra15[2].Should().Be(0xFF); // R
        bgra15[3].Should().Be(0xFF); // A

        // Now switch to 24-bit mode
        gpu.WriteGP1(0x08000010u); // 24-bit mode

        // Write 24-bit pixel at same location: RGB(0x12, 0x34, 0x56)
        gpu.Vram[0, 0] = 0x3412;
        gpu.Vram[1, 0] = 0x0056;

        var frame24 = gpu.CaptureFrame();
        var bgra24 = ScreenshotCapture.ToBgra32(frame24, gpu);

        // 24-bit RGB(0x12, 0x34, 0x56) -> BGRA(0x56, 0x34, 0x12, 0xFF)
        bgra24[0].Should().Be(0x56);
        bgra24[1].Should().Be(0x34);
        bgra24[2].Should().Be(0x12);
        bgra24[3].Should().Be(0xFF);
    }
}