using FluentAssertions;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure.Runtime.Gpu;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #818: a VBlank publishes the display the GPU shows into the live frame slot, reusing the screenshot conversion.
/// These tests use synthetic VRAM written through the GP0 CPU-to-VRAM transfer, not a real ROM.
/// </summary>
[Test]
public sealed class LiveFramePublisherTests
{
    private const uint DisplayMode24Bit = 0x08000010u;

    /// <summary>A read guard that always runs the read: these tests have no stop (the guard is the run's gate in production).</summary>
    private static readonly Func<Action, bool> Unguarded = read => { read(); return true; };

    /// <summary>Uploads <paramref name="halfwords"/> into VRAM at (<paramref name="x"/>, <paramref name="y"/>) as a 1-row strip.</summary>
    private static void UploadRow(GpuDevice gpu, int x, int y, ushort[] halfwords)
    {
        gpu.WriteGP0(0xA0000000u);
        gpu.WriteGP0((uint)x | ((uint)y << 16));
        gpu.WriteGP0((uint)halfwords.Length | (1u << 16));
        for (var i = 0; i < halfwords.Length; i += 2)
        {
            var high = i + 1 < halfwords.Length ? halfwords[i + 1] : (ushort)0;
            gpu.WriteGP0(halfwords[i] | ((uint)high << 16));
        }
    }

    [Fact]
    public void WithoutDrawing_NothingIsPublished()
    {
        using var gpu = new GpuDevice();
        var slot = new LiveFrameSlot();
        slot.TryGetNewer(0, out _);

        LiveFramePublisher.TryPublish(gpu, slot, Unguarded).Should().BeFalse();
        slot.TryGetNewer(0, out _).Should().BeFalse();
    }

    [Fact]
    public void WithoutAReader_NothingIsCopied()
    {
        using var gpu = new GpuDevice();
        UploadRow(gpu, 0, 0, [0x7C00]);
        var slot = new LiveFrameSlot();

        LiveFramePublisher.TryPublish(gpu, slot, Unguarded).Should().BeFalse("no reader asked for a frame");
        slot.TryGetNewer(0, out _).Should().BeFalse();
    }

    [Fact]
    public void A15BitDisplay_IsPublished_AndMatchesTheScreenshotConversion()
    {
        using var gpu = new GpuDevice();
        UploadRow(gpu, 0, 0, [0x7C00, 0x001F]);
        var slot = new LiveFrameSlot();
        slot.TryGetNewer(0, out _);

        LiveFramePublisher.TryPublish(gpu, slot, Unguarded).Should().BeTrue();

        slot.TryGetNewer(0, out var frame).Should().BeTrue();
        var expected = ScreenshotCapture.ToBgra32(gpu.CaptureFrame(), gpu);
        frame!.Bgra32.Should().Equal(expected);
        frame.Bgra32[0].Should().Be(0xFF, "the first pixel is blue (15-bit 0x7C00)");
        frame.Bgra32[2].Should().Be(0x00, "the first pixel has no red");
    }

    [Fact]
    public void A24BitDisplay_PixelBytesAreReadAsRedGreenBlue()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(DisplayMode24Bit);
        // The 24-bit stream is R, G, B bytes in order: halfword 0x2A1F then 0x003F gives R=1F, G=2A, B=3F.
        UploadRow(gpu, 0, 0, [0x2A1F, 0x003F]);
        var slot = new LiveFrameSlot();
        slot.TryGetNewer(0, out _);

        LiveFramePublisher.TryPublish(gpu, slot, Unguarded).Should().BeTrue();

        slot.TryGetNewer(0, out var frame).Should().BeTrue();
        // BGRA32 order: B, G, R, A.
        frame!.Bgra32.Take(4).Should().Equal(0x3F, 0x2A, 0x1F, 0xFF);
        frame.Bgra32.Should().Equal(ScreenshotCapture.ToBgra32(gpu.CaptureFrame(), gpu));
    }

    [Fact]
    public void VBlankPublisher_PublishesWhileHealthy()
    {
        using var gpu = new GpuDevice();
        UploadRow(gpu, 0, 0, [0x7C00]);
        var slot = new LiveFrameSlot();
        slot.TryGetNewer(0, out _);
        var reports = new List<Exception>();
        var publisher = new LiveFrameVBlankPublisher(slot, () => gpu, reports.Add, Unguarded);

        publisher.OnVBlank();

        slot.TryGetNewer(0, out var frame).Should().BeTrue();
        frame!.Sequence.Should().Be(1);
        reports.Should().BeEmpty();
    }

    [Fact]
    public void VBlankPublisher_AFailure_StopsPublishingForTheRun_AndIsReportedOnce()
    {
        // A null GPU makes every publish throw: the failure path is exercised without touching a real device.
        var slot = new LiveFrameSlot();
        slot.TryGetNewer(0, out _);
        var reports = new List<Exception>();
        var publisher = new LiveFrameVBlankPublisher(slot, () => null!, reports.Add, Unguarded);

        publisher.OnVBlank();
        publisher.OnVBlank();
        publisher.OnVBlank();

        reports.Should().HaveCount(1, "the first failure stops publishing; later VBlanks neither copy nor report");
        reports[0].Should().BeOfType<ArgumentNullException>();
    }

    [Fact]
    public void VBlankPublisher_AFailingReport_DoesNotEscapeIntoTheVBlankPath()
    {
        var publisher = new LiveFrameVBlankPublisher(new LiveFrameSlot(), () => null!, _ => throw new InvalidOperationException("report"), Unguarded);

        var act = publisher.OnVBlank;

        act.Should().NotThrow();
    }

    [Fact]
    public void OneAdvanceOverSeveralVBlanks_RaisesOneCallbackPerVBlank()
    {
        // The scheduler contract the live publish relies on: one Advance call that spans N VBlanks invokes the callback N times.
        // AdvanceExact is not used here: it splits the time at each VBlank, so no single call would cross several.
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var calls = new List<ulong>();
        scheduler.VblankCallback = count => calls.Add(count);

        scheduler.Advance(3 * DeviceScheduler.VblankIntervalCycles);

        calls.Should().Equal(1UL, 2UL, 3UL);
    }

    [Fact]
    public void EachVBlank_WithAReader_PublishesTheLatestDisplay_AndOnlyTheLatestIsKept()
    {
        using var graph = new PsxDeviceGraph();
        var gpu = graph.GpuDevice;
        UploadRow(gpu, 0, 0, [0x7C00]);
        var slot = new LiveFrameSlot();
        var scheduler = graph.CreateScheduler();
        scheduler.VblankCallback = _ => LiveFramePublisher.TryPublish(gpu, slot, Unguarded);

        // A reader polls once before each VBlank, as the GUI does on its timer.
        for (var i = 0; i < 3; i++)
        {
            slot.TryGetNewer(i, out _);
            scheduler.AdvanceExact(DeviceScheduler.VblankIntervalCycles);
        }

        slot.TryGetNewer(0, out var frame).Should().BeTrue();
        frame!.Sequence.Should().Be(3, "three polled VBlanks published three frames, and only the newest is kept");
    }
}
