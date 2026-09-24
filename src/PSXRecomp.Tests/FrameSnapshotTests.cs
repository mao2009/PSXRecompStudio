using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// Synthetic-fixture coverage for <see cref="FrameSnapshot"/> (Issue #441): the
/// scheduler-independent "current GPU/VRAM state -> deterministic frame" boundary.
/// No copyrighted title data is used anywhere in this file.
/// </summary>
[Test]
public sealed class FrameSnapshotTests
{
    private static uint Vertex(int x, int y) => (uint)(ushort)x | ((uint)(ushort)y << 16);

    private static uint Color(byte r, byte g, byte b) => ((uint)b << 16) | ((uint)g << 8) | r;

    [Fact]
    public void Capture_DefaultState_MatchesDefaultDisplayResolution()
    {
        using var vram = new GpuVram();
        var state = new GpuState();

        var snapshot = FrameSnapshot.Capture(vram, state, (256, 240));

        snapshot.Width.Should().Be(256);
        snapshot.Height.Should().Be(240);
        snapshot.Pixels.Count.Should().Be(256 * 240);
    }

    [Fact]
    public void Capture_PreservesRowMajorOrdering()
    {
        using var vram = new GpuVram();
        var state = new GpuState();
        vram[0, 0] = 0x1111;
        vram[1, 0] = 0x2222;
        vram[0, 1] = 0x3333;
        vram[1, 1] = 0x4444;

        var snapshot = FrameSnapshot.Capture(vram, state, (2, 2));

        snapshot.Pixels[0].Should().Be(0x1111); // (0,0)
        snapshot.Pixels[1].Should().Be(0x2222); // (1,0)
        snapshot.Pixels[2].Should().Be(0x3333); // (0,1)
        snapshot.Pixels[3].Should().Be(0x4444); // (1,1)
    }

    [Fact]
    public void Capture_HonoursConfiguredDisplayOffset()
    {
        using var vram = new GpuVram();
        var state = new GpuState { DisplayVramStart = 100u | (50u << 10) };
        vram[100, 50] = 0xABCD;

        var snapshot = FrameSnapshot.Capture(vram, state, (4, 4));

        snapshot.Pixels[0].Should().Be(0xABCD);
    }

    [Fact]
    public void Capture_ClipsAtVramBoundary_WithoutThrowing()
    {
        using var vram = new GpuVram();
        var state = new GpuState { DisplayVramStart = 1020u | (510u << 10) };

        var act = () => FrameSnapshot.Capture(vram, state, (256, 240));

        act.Should().NotThrow();
        var snapshot = FrameSnapshot.Capture(vram, state, (256, 240));
        snapshot.Width.Should().Be(GpuVram.Width - 1020);   // 4
        snapshot.Height.Should().Be(GpuVram.Height - 510);  // 2
    }

    [Fact]
    public void Capture_IdenticalState_ProducesIdenticalBytesAndHash()
    {
        using var vram = new GpuVram();
        var state = new GpuState();
        vram[3, 3] = 0x5A5A;

        var first = FrameSnapshot.Capture(vram, state, (16, 16));
        var second = FrameSnapshot.Capture(vram, state, (16, 16));

        first.Pixels.Should().Equal(second.Pixels);
        first.ComputeStableHash().Should().Equal(second.ComputeStableHash());
    }

    [Fact]
    public void ComputeStableHash_DiffersWhenPixelsDiffer()
    {
        using var vram = new GpuVram();
        var state = new GpuState();

        var before = FrameSnapshot.Capture(vram, state, (8, 8));
        vram[0, 0] = 0x0001;
        var after = FrameSnapshot.Capture(vram, state, (8, 8));

        before.ComputeStableHash().Should().NotEqual(after.ComputeStableHash());
    }

    // --- Synthetic end-to-end: GP0 -> GpuDevice -> VRAM -> FrameSnapshot -> hash/pixels ---

    private static void DrawSyntheticScene(GpuDevice gpu)
    {
        gpu.WriteGP0(0xE3000000);                                                  // draw area top-left (0,0)
        gpu.WriteGP0(0xE4000000 | (255u & 0x3FFu) | ((239u & 0x3FFu) << 10));      // draw area bottom-right (255,239)

        gpu.WriteGP0(0x60000000 | Color(0x00, 0x00, 0xF8));                        // blue rectangle
        gpu.WriteGP0(Vertex(10, 10));
        gpu.WriteGP0(0x00140014);                                                  // 20x20

        gpu.WriteGP0(0x30000000 | Color(0xF8, 0x00, 0x00));                        // Gouraud triangle
        gpu.WriteGP0(Vertex(100, 100));
        gpu.WriteGP0(Color(0x00, 0xF8, 0x00));
        gpu.WriteGP0(Vertex(100, 140));
        gpu.WriteGP0(Color(0x00, 0x00, 0xF8));
        gpu.WriteGP0(Vertex(140, 100));
    }

    [Fact]
    public void EndToEnd_SyntheticScene_ProducesExpectedPixelsInCapturedFrame()
    {
        using var gpu = new GpuDevice();
        DrawSyntheticScene(gpu);

        var snapshot = gpu.CaptureFrame(); // default display resolution is 256x240, region (0,0)-(255,239)

        snapshot.Width.Should().Be(256);
        snapshot.Height.Should().Be(240);
        snapshot.Pixels[(15 * 256) + 15].Should().Be((ushort)(0x1F << 10));  // inside the blue rectangle
        snapshot.Pixels[(100 * 256) + 100].Should().Be((ushort)0x1F);        // Gouraud vertex0: exact red
        snapshot.Pixels[0].Should().Be(0);                                    // untouched background
    }

    [Fact]
    public void EndToEnd_IdenticalSyntheticScene_ProducesIdenticalStableHash()
    {
        using var first = new GpuDevice();
        using var second = new GpuDevice();
        DrawSyntheticScene(first);
        DrawSyntheticScene(second);

        var hashA = first.CaptureFrame().ComputeStableHash();
        var hashB = second.CaptureFrame().ComputeStableHash();

        hashA.Should().Equal(hashB);
    }
}
