using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// Synthetic-fixture coverage for <see cref="GpuRasterizer"/> (Issue #441), driven
/// end-to-end through <see cref="GpuDevice.WriteGP0"/> exactly as real GP0
/// traffic would arrive, never by constructing a <see cref="GpuPrimitivePacket"/>
/// directly. No copyrighted title data is used anywhere in this file.
/// </summary>
[Test]
public sealed class GpuRasterizerTests
{
    private static GpuDevice NewGpuWithFullDrawArea()
    {
        var gpu = new GpuDevice();
        gpu.WriteGP0(0xE3000000); // top-left (0,0)
        gpu.WriteGP0(0xE4000000 | (1023 & 0x3FFu) | ((511 & 0x3FFu) << 10)); // bottom-right (1023,511)
        return gpu;
    }

    private static uint Vertex(int x, int y) => (uint)(ushort)x | ((uint)(ushort)y << 16);

    private static uint Color(byte r, byte g, byte b) => ((uint)b << 16) | ((uint)g << 8) | r;

    private static ushort Rgb555(byte r, byte g, byte b) =>
        (ushort)(((uint)r >> 3) | (((uint)g >> 3) << 5) | (((uint)b >> 3) << 10));

    // --- Rectangle ---------------------------------------------------------

    [Fact]
    public void Rectangle_1x1_SetsExactlyOnePixel()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x68000000 | Color(0xF8, 0x00, 0x00)); // 1x1, red
        gpu.WriteGP0(Vertex(10, 20));

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.HasFrameEvidence.Should().BeTrue();
        gpu.Vram[10, 20].Should().Be(Rgb555(0xF8, 0x00, 0x00));
        gpu.Vram[11, 20].Should().Be(0);
        gpu.Vram[9, 20].Should().Be(0);
        gpu.Vram[10, 19].Should().Be(0);
        gpu.Vram[10, 21].Should().Be(0);
    }

    [Fact]
    public void Rectangle_SmallVariableSize_FillsExactExtent()
    {
        using var gpu = NewGpuWithFullDrawArea();
        ushort _pixel = Rgb555(0x00, 0xF8, 0x00);

        gpu.WriteGP0(0x60000000 | Color(0x00, 0xF8, 0x00));
        gpu.WriteGP0(Vertex(100, 50));
        gpu.WriteGP0(0x00030004); // width=4, height=3

        for (int y = 50; y < 53; y++)
            for (int x = 100; x < 104; x++)
                gpu.Vram[x, y].Should().Be(_pixel);

        gpu.Vram[99, 50].Should().Be(0);
        gpu.Vram[104, 50].Should().Be(0);
        gpu.Vram[100, 49].Should().Be(0);
        gpu.Vram[100, 53].Should().Be(0);
    }

    [Fact]
    public void Rectangle_ClippedAtLeftTop_OnlyInBoundsPortionDraws()
    {
        using var gpu = NewGpuWithFullDrawArea();
        ushort _pixel = Rgb555(0x18, 0x18, 0x18);

        gpu.WriteGP0(0x60000000 | Color(0x18, 0x18, 0x18));
        gpu.WriteGP0(Vertex(-2, -3));
        gpu.WriteGP0(0x00050005); // width=5, height=5 => covers x[-2,2], y[-3,1]

        gpu.Vram[0, 0].Should().Be(_pixel);
        gpu.Vram[2, 1].Should().Be(_pixel);
        gpu.Vram[3, 0].Should().Be(0); // outside the 5x5 extent entirely
    }

    [Fact]
    public void Rectangle_ClippedAtRightBottom_OnlyInBoundsPortionDraws()
    {
        using var gpu = NewGpuWithFullDrawArea();
        ushort _pixel = Rgb555(0x08, 0x10, 0x20);

        gpu.WriteGP0(0x60000000 | Color(0x08, 0x10, 0x20));
        gpu.WriteGP0(Vertex(1022, 510));
        gpu.WriteGP0(0x00040004); // width=4, height=4 => would cover x[1022,1025], y[510,513]

        gpu.Vram[1022, 510].Should().Be(_pixel);
        gpu.Vram[1023, 511].Should().Be(_pixel);

        var _act = () => gpu.Vram[1024, 510].Should().Be(0);
        _act.Should().Throw<ArgumentOutOfRangeException>(); // proves the rasterizer never wrote past VRAM bounds
    }

    [Fact]
    public void Rectangle_FullyOffscreen_ChangesNoPixelsAndDoesNotThrow()
    {
        using var gpu = NewGpuWithFullDrawArea();

        var _act = () =>
        {
            gpu.WriteGP0(0x60000000 | Color(0xFF, 0xFF, 0xFF));
            gpu.WriteGP0(Vertex(2000, 2000));
            gpu.WriteGP0(0x00100010); // 16x16, entirely outside VRAM
        };

        _act.Should().NotThrow();
        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.HasFrameEvidence.Should().BeFalse(
            "a fully clipped primitive must not count as frame evidence");
        for (int y = 0; y < GpuVram.Height; y += 64)
            for (int x = 0; x < GpuVram.Width; x += 64)
                gpu.Vram[x, y].Should().Be(0);
    }

    [Fact]
    public void Rectangle_ZeroSize_DoesNotCreateFrameEvidence()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x60000000 | Color(0xFF, 0xFF, 0xFF));
        gpu.WriteGP0(Vertex(10, 10));
        gpu.WriteGP0(0x00000000); // width=0, height=0

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.HasFrameEvidence.Should().BeFalse();
        gpu.Vram[10, 10].Should().Be(0);
    }

    [Fact]
    public void Rectangle_VertexCoordinates_AreSigned11BitValues()
    {
        using var gpu = NewGpuWithFullDrawArea();
        ushort pixel = Rgb555(0xF8, 0x00, 0x00);

        // Raw 0x7FF is -1 in the GPU's 11-bit signed coordinate field.
        // A +2,+2 drawing offset therefore places the pixel at (1,1).
        gpu.WriteGP0(0xE5000000 | 2u | (2u << 11));
        gpu.WriteGP0(0x68000000 | Color(0xF8, 0x00, 0x00));
        gpu.WriteGP0(0x07FF07FF);

        gpu.Vram[1, 1].Should().Be(pixel);
        gpu.Vram[2, 2].Should().Be(0);
    }

    [Fact]
    public void Rectangle_VertexCoordinates_IgnoreUnusedUpperBitsInEachSlot()
    {
        using var gpu = NewGpuWithFullDrawArea();
        ushort pixel = Rgb555(0x00, 0xF8, 0x00);

        // Bits 11-15 are outside the PS1 GPU coordinate field and must not
        // affect the decoded position. Low 11 bits are still x=5, y=6.
        const uint rawVertex = 0xF806F805u;
        gpu.WriteGP0(0x68000000 | Color(0x00, 0xF8, 0x00));
        gpu.WriteGP0(rawVertex);

        gpu.Vram[5, 6].Should().Be(pixel);
        gpu.Vram[0, 0].Should().Be(0);
    }

    [Fact]
    public void Rectangle_KnownColor_PacksExpectedRgb555()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x68000000 | Color(0x08, 0x40, 0xF8)); // 1x1
        gpu.WriteGP0(Vertex(5, 5));

        // R8=0x08 -> R5=1, G8=0x40 -> G5=8, B8=0xF8 -> B5=31
        gpu.Vram[5, 5].Should().Be((ushort)(1 | (8 << 5) | (31 << 10)));
    }

    // --- Flat triangle -------------------------------------------------------

    [Fact]
    public void FlatTriangle_SimpleRightTriangle_InteriorSetExteriorUntouched()
    {
        using var gpu = NewGpuWithFullDrawArea();
        ushort _pixel = Rgb555(0x20, 0x60, 0xA0);

        gpu.WriteGP0(0x20000000 | Color(0x20, 0x60, 0xA0));
        gpu.WriteGP0(Vertex(10, 10));
        gpu.WriteGP0(Vertex(10, 20));
        gpu.WriteGP0(Vertex(20, 10));

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.Vram[11, 11].Should().Be(_pixel);  // interior, near the right-angle corner
        gpu.Vram[10, 10].Should().Be(_pixel);  // vertex pixel itself
        gpu.Vram[19, 19].Should().Be(0);       // outside the hypotenuse
        gpu.Vram[0, 0].Should().Be(0);
    }

    [Fact]
    public void FlatTriangle_ReversedWinding_RendersSameFilledSet()
    {
        using var forward = NewGpuWithFullDrawArea();
        forward.WriteGP0(0x20000000 | Color(0x40, 0x40, 0x40));
        forward.WriteGP0(Vertex(10, 10));
        forward.WriteGP0(Vertex(10, 20));
        forward.WriteGP0(Vertex(20, 10));

        using var reversed = NewGpuWithFullDrawArea();
        reversed.WriteGP0(0x20000000 | Color(0x40, 0x40, 0x40));
        reversed.WriteGP0(Vertex(10, 10));
        reversed.WriteGP0(Vertex(20, 10)); // v1/v2 swapped => opposite winding
        reversed.WriteGP0(Vertex(10, 20));

        for (int y = 0; y < 25; y++)
            for (int x = 0; x < 25; x++)
                reversed.Vram[x, y].Should().Be(forward.Vram[x, y]);
    }

    [Fact]
    public void FlatTriangle_Degenerate_ChangesNoPixels()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x20000000 | Color(0xFF, 0xFF, 0xFF));
        gpu.WriteGP0(Vertex(10, 10));
        gpu.WriteGP0(Vertex(20, 20));
        gpu.WriteGP0(Vertex(30, 30)); // collinear => zero area

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.HasFrameEvidence.Should().BeFalse(
            "a degenerate triangle writes no VRAM pixel");
        for (int i = 10; i <= 30; i++)
            gpu.Vram[i, i].Should().Be(0);
    }

    [Fact]
    public void FlatTriangle_NegativeVertexCoordinates_ClipsToVramWithoutThrowing()
    {
        using var gpu = NewGpuWithFullDrawArea();

        var act = () =>
        {
            gpu.WriteGP0(0x20000000 | Color(0x60, 0x60, 0x60));
            gpu.WriteGP0(Vertex(-5, -5));
            gpu.WriteGP0(Vertex(-5, 15));
            gpu.WriteGP0(Vertex(15, -5));
        };

        act.Should().NotThrow();
        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.Vram[3, 3].Should().Be(Rgb555(0x60, 0x60, 0x60)); // interior, inside VRAM despite negative vertices
        gpu.Vram[0, 0].Should().Be(Rgb555(0x60, 0x60, 0x60)); // vertex-adjacent corner clipped into VRAM
    }

    [Fact]
    public void FlatTriangle_ClippedByDrawArea_OnlyInBoundsPortionDraws()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0xE3000000 | (5u << 0)); // draw area left = x5, top = 0
        gpu.WriteGP0(0xE4000000 | (1023 & 0x3FFu) | ((511 & 0x3FFu) << 10));

        gpu.WriteGP0(0x20000000 | Color(0x7C, 0x7C, 0x7C));
        gpu.WriteGP0(Vertex(0, 0));
        gpu.WriteGP0(Vertex(0, 10));
        gpu.WriteGP0(Vertex(10, 0));

        gpu.Vram[2, 2].Should().Be(0); // left of the draw-area clip, would otherwise be interior
        gpu.Vram[6, 1].Should().Be(Rgb555(0x7C, 0x7C, 0x7C));
    }

    [Fact]
    public void FlatTriangle_IsDeterministic_AcrossFreshDevices()
    {
        using var a = NewGpuWithFullDrawArea();
        using var b = NewGpuWithFullDrawArea();

        foreach (var gpu in new[] { a, b })
        {
            gpu.WriteGP0(0x22000000 | Color(0x11, 0x22, 0x33));
            gpu.WriteGP0(Vertex(3, 3));
            gpu.WriteGP0(Vertex(3, 15));
            gpu.WriteGP0(Vertex(15, 3));
        }

        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                a.Vram[x, y].Should().Be(b.Vram[x, y]);
    }

    // --- Gouraud triangle ----------------------------------------------------

    [Fact]
    public void GouraudTriangle_VertexPixels_MatchTheirOwnExactColor()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x30000000 | Color(0xF8, 0x00, 0x00));   // vertex0: red
        gpu.WriteGP0(Vertex(0, 0));
        gpu.WriteGP0(Color(0x00, 0xF8, 0x00));                 // vertex1: green
        gpu.WriteGP0(Vertex(0, 20));
        gpu.WriteGP0(Color(0x00, 0x00, 0xF8));                 // vertex2: blue
        gpu.WriteGP0(Vertex(20, 0));

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.Rasterized);
        gpu.Vram[0, 0].Should().Be(Rgb555(0xF8, 0x00, 0x00));
        gpu.Vram[0, 20].Should().Be(Rgb555(0x00, 0xF8, 0x00));
        gpu.Vram[20, 0].Should().Be(Rgb555(0x00, 0x00, 0xF8));
    }

    [Fact]
    public void GouraudTriangle_InteriorPixel_IsWithinVertexChannelBounds()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x30000000 | Color(0xF8, 0x00, 0x00));
        gpu.WriteGP0(Vertex(0, 0));
        gpu.WriteGP0(Color(0x00, 0xF8, 0x00));
        gpu.WriteGP0(Vertex(0, 30));
        gpu.WriteGP0(Color(0x00, 0x00, 0xF8));
        gpu.WriteGP0(Vertex(30, 0));

        ushort _interior = gpu.Vram[5, 5];
        int _r = _interior & 0x1F;
        int _g = (_interior >> 5) & 0x1F;
        int _b = (_interior >> 10) & 0x1F;

        // Interpolated channels must stay within the convex hull of the vertex
        // channel values (red=31,0,0 / green=0,31,0 / blue=0,0,31 => every
        // channel's interpolated value is bounded by [0,31] here).
        _r.Should().BeInRange(0, 31);
        _g.Should().BeInRange(0, 31);
        _b.Should().BeInRange(0, 31);
        (_r + _g + _b).Should().BeGreaterThan(0); // proves interpolation actually ran, not a stray zero pixel
    }

    [Fact]
    public void GouraudTriangle_RepeatedRender_ProducesIdenticalVram()
    {
        static void Draw(GpuDevice gpu)
        {
            gpu.WriteGP0(0x30000000 | Color(0xF8, 0x00, 0x00));
            gpu.WriteGP0(Vertex(2, 2));
            gpu.WriteGP0(Color(0x00, 0xF8, 0x00));
            gpu.WriteGP0(Vertex(2, 25));
            gpu.WriteGP0(Color(0x00, 0x00, 0xF8));
            gpu.WriteGP0(Vertex(25, 2));
        }

        using var first = NewGpuWithFullDrawArea();
        using var second = NewGpuWithFullDrawArea();
        Draw(first);
        Draw(second);

        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 30; x++)
                first.Vram[x, y].Should().Be(second.Vram[x, y]);
    }

    // --- Unsupported feature ---------------------------------------------------

    [Fact]
    public void TexturedTriangle_IsUnsupportedFeature_VramUntouched()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x24000000 | Color(0xFF, 0xFF, 0xFF)); // textured triangle (bit26 set)
        gpu.WriteGP0(Vertex(0, 0));
        gpu.WriteGP0(0x00000000); // texcoord/clut word
        gpu.WriteGP0(Vertex(0, 10));
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(Vertex(10, 0));
        gpu.WriteGP0(0x00000000);

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.UnsupportedFeature);
        gpu.Vram[2, 2].Should().Be(0);
    }

    [Fact]
    public void TexturedRectangle_IsUnsupportedFeature_VramUntouched()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x64000000 | Color(0xFF, 0xFF, 0xFF)); // variable-size textured rectangle (bit26 set)
        gpu.WriteGP0(Vertex(0, 0));
        gpu.WriteGP0(0x00000000); // texcoord/clut word
        gpu.WriteGP0(0x00100010); // width/height

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.UnsupportedFeature);
        gpu.Vram[0, 0].Should().Be(0);
    }

    [Fact]
    public void QuadPolygon_IsUnsupportedFeature_VramUntouched()
    {
        using var gpu = NewGpuWithFullDrawArea();

        gpu.WriteGP0(0x28000000 | Color(0xFF, 0xFF, 0xFF)); // flat quad (bit27 set)
        gpu.WriteGP0(Vertex(0, 0));
        gpu.WriteGP0(Vertex(0, 10));
        gpu.WriteGP0(Vertex(10, 0));
        gpu.WriteGP0(Vertex(10, 10));

        gpu.LastRasterOutcome.Should().Be(GpuRasterOutcome.UnsupportedFeature);
        gpu.Vram[2, 2].Should().Be(0);
    }
}
