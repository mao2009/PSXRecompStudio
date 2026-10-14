using System.Buffers.Binary;
using System.IO.Compression;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure.Runtime.Gpu;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Runtime;

/// <summary>
/// Tests for <see cref="ScreenshotCapture"/> (Issue #XXX): verifies VBlank-based
/// screenshot capture with PNG output, duplicate VBlank handling, and completion.
/// </summary>
[Test]
public sealed class ScreenshotCaptureTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();
    private readonly GpuDevice _gpu = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly DeviceScheduler _scheduler;

    public ScreenshotCaptureTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
        _scheduler = new DeviceScheduler(_core, _interrupts, _gpu);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _gpu.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Capture_AtTargetVBlank_SavesPngFile()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 1, maxScreenshots: 2, startVblank: 1);

        // Advance scheduler to VBlank 1
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);

        // Try capture at VBlank 1
        var captured = capture.TryCapture();
        captured.Should().BeTrue("first target VBlank should capture");
        capture.ScreenshotsTaken.Should().Be(1);

        #pragma warning disable AARC003
        var files = Directory.GetFiles(dir.FullPath, "*.png");
        #pragma warning restore AARC003
        files.Should().HaveCount(1);
        files[0].Should().Contain("persona_vblank_0001.png");
    }

    [Fact]
    public void Capture_SkipsNonTargetVBlanks()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 2, maxScreenshots: 2, startVblank: 2);

        // Advance to VBlank 1 (not a target)
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        capture.TryCapture().Should().BeFalse("VBlank 1 is not a target");
        capture.ScreenshotsTaken.Should().Be(0);

        // Advance to VBlank 2 (target)
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        capture.TryCapture().Should().BeTrue("VBlank 2 is a target");
        capture.ScreenshotsTaken.Should().Be(1);
    }

    [Fact]
    public void Capture_DuplicateVBlankNotification_DoesNotDoubleCapture()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 1, maxScreenshots: 5, startVblank: 1);

        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);

        // First capture
        capture.TryCapture().Should().BeTrue();
        capture.ScreenshotsTaken.Should().Be(1);

        // Second call at same VBlank (simulating duplicate notification)
        capture.TryCapture().Should().BeFalse("duplicate VBlank should not capture again");
        capture.ScreenshotsTaken.Should().Be(1);

        #pragma warning disable AARC003
        var files = Directory.GetFiles(dir.FullPath, "*.png");
        #pragma warning restore AARC003
        files.Should().HaveCount(1);
    }

    [Fact]
    public void Capture_StopsAtMaxScreenshots()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 1, maxScreenshots: 2, startVblank: 1);

        // Capture at VBlank 1
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        capture.TryCapture().Should().BeTrue();

        // Capture at VBlank 2
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        capture.TryCapture().Should().BeTrue();

        // Should be complete now
        capture.IsComplete.Should().BeTrue();

        // Further captures should not happen
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        capture.TryCapture().Should().BeFalse();
        capture.ScreenshotsTaken.Should().Be(2);
    }

    [Fact]
    public void ForceCapture_SavesRegardlessOfInterval()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 100, maxScreenshots: 10, startVblank: 100);

        // Force capture at VBlank 0 (not a target)
        capture.ForceCapture("custom_name.png");

        #pragma warning disable AARC003
        var files = Directory.GetFiles(dir.FullPath, "*.png");
        #pragma warning restore AARC003
        files.Should().HaveCount(1);
        files[0].Should().EndWith("custom_name.png");
    }

    [Fact]
    public void Capture_PngIsValidImage()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 1, maxScreenshots: 1, startVblank: 1);

        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        capture.TryCapture();

        #pragma warning disable AARC003
        var files = Directory.GetFiles(dir.FullPath, "*.png");
        #pragma warning restore AARC003
        files.Should().HaveCount(1);

        // Validate PNG signature, IHDR dimensions and the independently decompressed RGBA scanlines.
        #pragma warning disable AARC003 // Test-only inspection of PNG output
        var png = File.ReadAllBytes(files[0]);
        #pragma warning restore AARC003
        png.AsSpan(0, 8).ToArray().Should().Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)).Should().BeGreaterThan(0);
        BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)).Should().BeGreaterThan(0);
        png[24].Should().Be(8);
        png[25].Should().Be(6);

        int offset = 8;
        using var idat = new MemoryStream();
        bool hasEnd = false;
        while (offset + 12 <= png.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            offset += 4;
            string type = System.Text.Encoding.ASCII.GetString(png, offset, 4);
            offset += 4;
            (offset + length + 4 <= png.Length).Should().BeTrue();
            if (type == "IDAT")
                idat.Write(png, offset, length);
            if (type == "IEND")
                hasEnd = true;
            offset += length + 4;
        }
        hasEnd.Should().BeTrue();
        idat.Position = 0;
        using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        raw.Length.Should().Be((long)height * (1 + width * 4));
        for (int y = 0; y < height; y++)
            raw.GetBuffer()[y * (1 + width * 4)].Should().Be(0);
    }

    [Fact]
    public void NextTargetVblank_ReturnsCorrectValue()
    {
        using var dir = new TempDirectory();
        var capture = new ScreenshotCapture(_gpu, _scheduler, dir.FullPath, interval: 300, maxScreenshots: 3, startVblank: 300);

        // Before any VBlanks
        capture.NextTargetVblank.Should().Be(300);

        // Advance past first target
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles * 300);
        capture.TryCapture();
        capture.NextTargetVblank.Should().Be(600);

        // Advance past second target
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles * 300);
        capture.TryCapture();
        capture.NextTargetVblank.Should().Be(900);

        // After last target
        _scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles * 300);
        capture.TryCapture();
        capture.NextTargetVblank.Should().BeNull();
    }

    // ===== 24-bit (8-8-8) display conversion (Issue #772) =====

    [Fact]
    public void Convert24BitRegion_PlacesFirstPixelAtHalfwordStartXInBytes()
    {
        // GP1(05h) X is a halfword address: the 24-bit display origin is at byte startX * 2,
        // then each pixel advances 3 bytes. startX = 1 -> byte 2 (not byte 3).
        var vram = new byte[16];
        vram[2] = 0x11; vram[3] = 0x22; vram[4] = 0x33; // pixel 0: R G B
        vram[5] = 0x44; vram[6] = 0x55; vram[7] = 0x66; // pixel 1: R G B

        var bgra = ScreenshotCapture.Convert24BitRegionToBgra32(vram, startX: 1, startY: 0, width: 2, height: 1);

        bgra.Should().Equal(new byte[]
        {
            0x33, 0x22, 0x11, 0xFF, // B G R A
            0x66, 0x55, 0x44, 0xFF,
        });
    }

    [Fact]
    public void Convert24BitRegion_ScalesStartXAsHalfwordsNotPixels()
    {
        // Regression: treating startX as a pixel index (startX * 3) would read byte 9 here.
        // The hardware uses startX * 2 = byte 6.
        var vram = new byte[16];
        vram[6] = 0x0A; vram[7] = 0x0B; vram[8] = 0x0C;

        var bgra = ScreenshotCapture.Convert24BitRegionToBgra32(vram, startX: 3, startY: 0, width: 1, height: 1);

        bgra.Should().Equal(new byte[] { 0x0C, 0x0B, 0x0A, 0xFF });
    }

    [Fact]
    public void Convert24BitRegion_UsesRowStrideAndStartY()
    {
        var vram = new byte[2 * 2048];
        vram[2048] = 0xAA; vram[2049] = 0xBB; vram[2050] = 0xCC; // row 1, column 0

        var bgra = ScreenshotCapture.Convert24BitRegionToBgra32(vram, startX: 0, startY: 1, width: 1, height: 1);

        bgra.Should().Equal(new byte[] { 0xCC, 0xBB, 0xAA, 0xFF });
    }

    [Fact]
    public void Convert24BitRegion_StopsAtVramEnd()
    {
        var vram = new byte[4]; // exactly one complete pixel fits
        vram[0] = 0x01; vram[1] = 0x02; vram[2] = 0x03;

        var bgra = ScreenshotCapture.Convert24BitRegionToBgra32(vram, startX: 0, startY: 0, width: 2, height: 1);

        bgra.Should().Equal(new byte[] { 0x03, 0x02, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00 });
    }
}