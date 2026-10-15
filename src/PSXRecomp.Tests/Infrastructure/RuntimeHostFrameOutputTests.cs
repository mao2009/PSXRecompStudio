using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
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

    [Fact]
    public void TryWrite_DrawnFrame_WritesValidPng()
    {
        // Create an engine with a drawn frame (15-bit mode, gray pixel)
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);

        // Manually set a frame on the engine using reflection (since LastFrameBgra32 is internal set)
        var frame = CreateTestFrame(16, 16);
        SetLastFrameBgra32(engine, frame);

        var output = dir.Combine("frame.png");
        var error = new StringWriter();

        RuntimeHostFrameOutput.TryWrite(engine, output, error).Should().BeTrue();

        #pragma warning disable AARC003
        File.Exists(output).Should().BeTrue();
        #pragma warning restore AARC003

        // Verify it's a valid PNG by checking the signature
        byte[] bytes;
        #pragma warning disable AARC003
        bytes = File.ReadAllBytes(output);
        #pragma warning restore AARC003
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50); // 'P'
        bytes[2].Should().Be(0x4E); // 'N'
        bytes[3].Should().Be(0x47); // 'G'
        bytes[4].Should().Be(0x0D);
        bytes[5].Should().Be(0x0A);
        bytes[6].Should().Be(0x1A);
        bytes[7].Should().Be(0x0A);
        bytes.Length.Should().BeGreaterThan(33);
        Encoding.ASCII.GetString(bytes, 12, 4).Should().Be("IHDR");
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(16u);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(16u);
        bytes[24].Should().Be(8); // Bit depth
        bytes[25].Should().Be(6); // RGBA

        // Decode the first PNG scanline and confirm pixel content, not just its header.
        // This catches regressions where a valid PNG contains incorrect color channels.
        var idatLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(33, 4)));
        Encoding.ASCII.GetString(bytes, 37, 4).Should().Be("IDAT");
        using (var idat = new MemoryStream(bytes, 41, idatLength, writable: false))
        using (var inflater = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var firstScanline = new byte[1 + 16 * 4];
            inflater.ReadExactly(firstScanline);
            firstScanline[0].Should().Be(0); // PNG filter: none
            firstScanline.AsSpan(1, 8).ToArray().Should().Equal(
                0x30, 0x20, 0x10, 0xFF,
                0x60, 0x50, 0x40, 0xFF);
        }

        error.ToString().Should().Contain("16x16");
        error.ToString().Should().Contain("written");
    }

    [Fact]
    public void TryWrite_WriteFailure_DoesNotChangeResult_ReportsError()
    {
        // Try to write to an invalid path (directory as file)
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);

        var frame = CreateTestFrame(16, 16);
        SetLastFrameBgra32(engine, frame);

        // Try to write to a path that will fail (directory as file)
        var invalidOutput = dir.FullPath; // This is a directory, not a file
        var error = new StringWriter();

        // This should not throw, should return false, and report the error
        RuntimeHostFrameOutput.TryWrite(engine, invalidOutput, error).Should().BeFalse();

        error.ToString().Should().Contain("could not write");
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid\0frame.png")]
    public void TryWrite_InvalidOptionalPath_DoesNotThrow(string invalidPath)
    {
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);
        SetLastFrameBgra32(engine, CreateTestFrame(16, 16));
        var error = new StringWriter();

        RuntimeHostFrameOutput.TryWrite(engine, invalidPath, error).Should().BeFalse();

        error.ToString().Should().Contain("could not write");
    }

    [Fact]
    public void TryWrite_TimeoutScenario_FrameCapturedBeforeDisposal()
    {
        // This test verifies that the frame is captured while the GPU is still valid
        // (simulating the timeout path where bridge?.CaptureFrameBgra32() is called
        // before the bridge is disposed)
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x08000010u); // 24-bit mode
        SetDisplayVramStart(gpu, 100, 0);

        // Write a test pattern
        gpu.Vram[100, 0] = 0xBBAA;
        gpu.Vram[101, 0] = 0x00CC;

        var frame = gpu.CaptureFrame();
        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);

        // Verify the frame was captured correctly (startX=100 -> byte offset 200)
        bgra[0].Should().Be(0xCC); // B
        bgra[1].Should().Be(0xBB); // G
        bgra[2].Should().Be(0xAA); // R
        bgra[3].Should().Be(0xFF); // A

        // Now dispose the GPU (simulating bridge disposal after timeout)
        gpu.Dispose();

        // The captured frame data should still be valid (it's a copy)
        bgra[0].Should().Be(0xCC);
        bgra[1].Should().Be(0xBB);
        bgra[2].Should().Be(0xAA);
        bgra[3].Should().Be(0xFF);
    }

    private static (byte[] Bgra, int Width, int Height) CreateTestFrame(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = i == 0 ? (byte)0x10 : i == 4 ? (byte)0x40 : (byte)0x80;     // B
            bgra[i + 1] = i == 0 ? (byte)0x20 : i == 4 ? (byte)0x50 : (byte)0x80; // G
            bgra[i + 2] = i == 0 ? (byte)0x30 : i == 4 ? (byte)0x60 : (byte)0x80; // R
            bgra[i + 3] = 0xFF; // A
        }
        return (bgra, width, height);
    }

    private static void SetLastFrameBgra32(RecompiledHostExecutionEngine engine, (byte[] Bgra, int Width, int Height) frame)
    {
        var prop = typeof(RecompiledHostExecutionEngine).GetProperty("LastFrameBgra32",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop!.SetValue(engine, frame);
    }

    private static void SetDisplayVramStart(GpuDevice gpu, int x, int y)
    {
        uint param = (uint)(x & 0x3FF) | ((uint)(y & 0x1FF) << 10);
        gpu.WriteGP1(0x05000000u | param);
    }
}
