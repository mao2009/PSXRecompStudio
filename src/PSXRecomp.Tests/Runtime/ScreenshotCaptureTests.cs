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

        // Verify it's a valid PNG by loading with ImageSharp
        using var image = SixLabors.ImageSharp.Image.Load(files[0]);
        image.Width.Should().BeGreaterThan(0);
        image.Height.Should().BeGreaterThan(0);
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
}