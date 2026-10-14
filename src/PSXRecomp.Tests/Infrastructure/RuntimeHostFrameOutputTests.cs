using FluentAssertions;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Runtime.Gpu;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #762: the packaged runtime host writes the display its guest left in VRAM, reusing the headless screenshot
/// capture; with nothing drawn it says so and writes nothing.
/// </summary>
[Test]
public sealed class RuntimeHostFrameOutputTests
{
    [Fact]
    public void WithoutARun_NoFrameIsWritten_AndTheAbsenceIsReported()
    {
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);
        var output = Path.Combine(dir.FullPath, "frame.png");
        var error = new StringWriter();

        RuntimeHostFrameOutput.TryWrite(engine, output, error).Should().BeFalse();

        #pragma warning disable AARC003
        File.Exists(output).Should().BeFalse();
        #pragma warning restore AARC003
        error.ToString().Should().Contain("no frame");
    }

    [Fact]
    public void ToBgra32_OfAnUndrawnFifteenBitDisplay_IsOpaqueBlackAtTheFrameSize()
    {
        using var gpu = new GpuDevice();
        var frame = gpu.CaptureFrame();

        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);

        bgra.Length.Should().Be(frame.Width * frame.Height * 4);
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i].Should().Be(0);
            bgra[i + 3].Should().Be(0xFF);
        }
    }
}
