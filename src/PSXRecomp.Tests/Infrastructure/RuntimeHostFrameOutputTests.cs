using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
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

    [Fact]
    public void TryWrite_TwentyFourBitDisplay_PngCarriesRgbInChannelOrder()
    {
        // Synthetic GPU frame (no guest run): a 24-bit display goes through the shared capture conversion into the PNG.
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x08000010u); // 24-bit color depth
        gpu.Vram[0, 0] = 0x3412;   // pixel 0 bytes: R=0x12 G=0x34
        gpu.Vram[1, 0] = 0x0056;   // pixel 0 byte B=0x56
        var frame = gpu.CaptureFrame();
        SetLastFrameBgra32(engine, (ScreenshotCapture.ToBgra32(frame, gpu), frame.Width, frame.Height));
        var output = dir.Combine("frame24.png");

        RuntimeHostFrameOutput.TryWrite(engine, output, new StringWriter()).Should().BeTrue();

        #pragma warning disable AARC003
        var png24 = File.ReadAllBytes(output);
        #pragma warning restore AARC003
        ReadFirstPixelsRgba(png24, 1).Should().Equal(0x12, 0x34, 0x56, 0xFF);
    }

    [Fact]
    public void TryWrite_FifteenBitDisplay_PngExpandsEachChannelToEightBits()
    {
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x08000000u); // 15-bit color depth
        gpu.Vram[0, 0] = 0x001F;   // R=31 only -> RGB(0xFF, 0, 0)
        gpu.Vram[1, 0] = 0x7C00;   // B=31 only -> RGB(0, 0, 0xFF)
        var frame = gpu.CaptureFrame();
        SetLastFrameBgra32(engine, (ScreenshotCapture.ToBgra32(frame, gpu), frame.Width, frame.Height));
        var output = dir.Combine("frame15.png");

        RuntimeHostFrameOutput.TryWrite(engine, output, new StringWriter()).Should().BeTrue();

        #pragma warning disable AARC003
        var png15 = File.ReadAllBytes(output);
        #pragma warning restore AARC003
        ReadFirstPixelsRgba(png15, 2).Should().Equal(
            0xFF, 0x00, 0x00, 0xFF,
            0x00, 0x00, 0xFF, 0xFF);
    }

    [Fact]
    public void EndToEnd_TwentyFourBitDisplay_GuestWritesReachTheFrameThroughTheBridge_AndPngCarriesRgb()
    {
        // 24-bit: pixel 0 is bytes R=0x12 G=0x34 (halfword 0) and B=0x56 (low byte of halfword 1).
        var run = RunGuestDisplay(gp1DisplayMode: 0x08000010u, vramWord: 0x00563412u, pngName: "e2e24.png");

        run.Frame.Should().NotBeNull("the bridge must relay the guest's GP0/GP1 writes and propagate the frame to LastFrameBgra32");
        run.Frame!.Value.Bgra.Length.Should().Be(run.Frame.Value.Width * run.Frame.Value.Height * 4);
        run.Frame.Value.Bgra[..4].Should().Equal(new byte[] { 0x56, 0x34, 0x12, 0xFF }, "BGRA of the displayed pixel 0");
        run.Written.Should().BeTrue(run.Error);
        ReadFirstPixelsRgba(run.Png!, 1).Should().Equal(0x12, 0x34, 0x56, 0xFF);
    }

    [Fact]
    public void EndToEnd_FifteenBitDisplay_GuestWritesReachTheFrameThroughTheBridge_AndPngExpandsEachChannel()
    {
        // 15-bit: pixel 0 is R=31 only, pixel 1 is B=31 only; each 5-bit channel expands to 8 bits.
        var run = RunGuestDisplay(gp1DisplayMode: 0x08000000u, vramWord: 0x7C00001Fu, pngName: "e2e15.png");

        run.Frame.Should().NotBeNull("the bridge must relay the guest's GP0/GP1 writes and propagate the frame to LastFrameBgra32");
        run.Frame!.Value.Bgra[..8].Should().Equal(
            0x00, 0x00, 0xFF, 0xFF,   // pixel 0: R=31 -> BGRA(00,00,FF,FF)
            0xFF, 0x00, 0x00, 0xFF);  // pixel 1: B=31 -> BGRA(FF,00,00,FF)
        run.Written.Should().BeTrue(run.Error);
        ReadFirstPixelsRgba(run.Png!, 2).Should().Equal(
            0xFF, 0x00, 0x00, 0xFF,
            0x00, 0x00, 0xFF, 0xFF);
    }

    /// <summary>
    /// Runs a guest that programs the display only through the host-transfer bridge: GP1 display mode, GP1 display
    /// start (0,0), then a CPU-to-VRAM copy of two halfwords (<paramref name="vramWord"/>) at (0,0). The frame reaches
    /// <see cref="RecompiledHostExecutionEngine.LastFrameBgra32"/> only through the bridge's propagation at the end of
    /// the run, and <see cref="RuntimeHostFrameOutput.TryWrite"/> is called on the same engine before it is disposed.
    /// </summary>
    private static GuestDisplayRun RunGuestDisplay(uint gp1DisplayMode, uint vramWord, string pngName)
    {
        const uint gp0 = 0x1F801810u;
        const uint gp1 = 0x1F801814u;
        const R3000aRegister t0 = R3000aRegister.T0;
        const R3000aRegister t1 = R3000aRegister.T1;
        const R3000aRegister t2 = R3000aRegister.T2;
        var words = RecompiledArtifactMmioBridgeTests.Program(
            RecompiledArtifactMmioBridgeTests.Li(t0, gp0),
            RecompiledArtifactMmioBridgeTests.Li(t1, gp1),
            RecompiledArtifactMmioBridgeTests.Li(t2, gp1DisplayMode),
            [RecompiledArtifactMmioBridgeTests.Mem(R3000aOpcode.Sw, t2, t1, 0)],
            RecompiledArtifactMmioBridgeTests.Li(t2, 0x05000000u),
            [RecompiledArtifactMmioBridgeTests.Mem(R3000aOpcode.Sw, t2, t1, 0)],
            RecompiledArtifactMmioBridgeTests.Li(t2, 0xA0000000u),  // GP0 CPU-to-VRAM copy
            [RecompiledArtifactMmioBridgeTests.Mem(R3000aOpcode.Sw, t2, t0, 0)],
            RecompiledArtifactMmioBridgeTests.Li(t2, 0x00000000u),  // destination x=0, y=0
            [RecompiledArtifactMmioBridgeTests.Mem(R3000aOpcode.Sw, t2, t0, 0)],
            RecompiledArtifactMmioBridgeTests.Li(t2, 0x00010002u),  // 2 halfwords wide, 1 row high
            [RecompiledArtifactMmioBridgeTests.Mem(R3000aOpcode.Sw, t2, t0, 0)],
            RecompiledArtifactMmioBridgeTests.Li(t2, vramWord),     // the two halfwords
            [RecompiledArtifactMmioBridgeTests.Mem(R3000aOpcode.Sw, t2, t0, 0)]);

        using var dir = new TempDirectory();
        var png = dir.Combine(pngName);
        var error = new StringWriter();
        var run = new GuestDisplayRun();
        var result = RecompiledArtifactMmioBridgeTests.Run(words, dir, withRuntime: true, segmentBudget: 4096, observe: engine =>
        {
            run.Frame = engine.LastFrameBgra32;
            run.Written = RuntimeHostFrameOutput.TryWrite(engine, png, error);
        });
        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        run.Error = error.ToString();
        #pragma warning disable AARC003
        if (run.Written) run.Png = File.ReadAllBytes(png);
        #pragma warning restore AARC003
        return run;
    }

    private sealed class GuestDisplayRun
    {
        public (byte[] Bgra, int Width, int Height)? Frame { get; set; }
        public bool Written { get; set; }
        public byte[]? Png { get; set; }
        public string Error { get; set; } = "";
    }

    [Fact]
    public void Dispose_CalledTwiceAfterAFrameWasHeld_DoesNotThrow()
    {
        // Shutdown path: the run's engine is disposed after the frame has been read; a repeated dispose must be harmless.
        using var dir = new TempDirectory();
        var artifact = dir.WriteFile("artifact.bin", [0]);
        var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            artifact, new HashSet<uint>(), imageWords: [0u], imageLoadAddress: 0x80010000u);
        SetLastFrameBgra32(engine, CreateTestFrame(16, 16));

        Action act = () =>
        {
            engine.Dispose();
            engine.Dispose();
        };

        act.Should().NotThrow();
    }

    /// <summary>Decodes the first <paramref name="pixelCount"/> pixels of row 0 of a PNG written by <see cref="RuntimeHostFrameOutput"/> as RGBA bytes.</summary>
    private static byte[] ReadFirstPixelsRgba(byte[] png, int pixelCount)
    {
        var idatLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(33, 4)));
        Encoding.ASCII.GetString(png, 37, 4).Should().Be("IDAT");
        using var idat = new MemoryStream(png, 41, idatLength, writable: false);
        using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
        var scanline = new byte[1 + pixelCount * 4];
        inflater.ReadExactly(scanline);
        scanline[0].Should().Be(0); // PNG filter: none
        return scanline[1..];
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
