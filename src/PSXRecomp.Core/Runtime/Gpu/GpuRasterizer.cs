using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gpu;

/// <summary>
/// Outcome of attempting to rasterize a decoded drawing-primitive packet
/// (Issue #441). Distinct from <see cref="GpuCommandResult"/>, which classifies
/// GP0 command decoding itself and is unaffected by rasterizer coverage.
/// </summary>
[Domain]
public enum GpuRasterOutcome
{
    /// <summary>The primitive was rasterized into VRAM.</summary>
    Rasterized = 0,

    /// <summary>
    /// A recognized primitive whose feature set is not implemented by this
    /// rasterizer (texture mapping, quads). VRAM is left untouched rather than
    /// drawing a wrong or partial result.
    /// </summary>
    UnsupportedFeature,
}

/// <summary>
/// Minimal, deterministic, integer-only software rasterizer for the flat and
/// Gouraud-shaded rectangle/triangle primitives decoded by
/// <see cref="Gp0CommandDecoder"/> (Issue #441). Texture mapping, quads, line
/// primitives, semi-transparency blending, dithering, and mask-bit checking are
/// explicitly out of scope for this slice.
///
/// <para>
/// Deliberate simplifications, documented here rather than re-litigated per
/// call site: pixel sampling uses each primitive's raw integer coordinates as
/// the sample point (no +0.5 pixel-center offset), and edge inclusion uses a
/// fixed <c>&gt;=0</c>/<c>&lt;=0</c> rule rather than a true top-left tie-break,
/// so two triangles sharing an edge can both draw that edge's pixels.
/// Semi-transparency (command bit 25) is recognized but ignored: every
/// primitive draws opaque. None of this reproduces real hardware pixel-exact
/// behavior; it is fully deterministic (same state always renders identical
/// bytes), which is what this slice's headless frame evidence requires.
/// </para>
/// </summary>
internal readonly record struct GpuRasterResult(GpuRasterOutcome Outcome, bool WrotePixels);

[Domain]
public static class GpuRasterizer
{
    /// <summary>Rasterizes a decoded drawing-primitive packet into <paramref name="vram"/>.</summary>
    public static GpuRasterOutcome Rasterize(GpuPrimitivePacket primitive, GpuState state, GpuVram vram) =>
        RasterizeDetailed(primitive, state, vram).Outcome;

    /// <summary>
    /// Rasterizes a primitive and also reports whether at least one VRAM pixel
    /// was actually written. The extra bit is used only for frame-evidence
    /// provenance; <see cref="Rasterize"/> keeps the existing public outcome API.
    /// </summary>
    internal static GpuRasterResult RasterizeDetailed(GpuPrimitivePacket primitive, GpuState state, GpuVram vram)
    {
        uint word = primitive.CommandWord;
        uint family = word >> 29;

        return family switch
        {
            1 => RasterizePolygon(word, primitive.Parameters, state, vram),
            3 => RasterizeRectangle(word, primitive.Parameters, state, vram),
            _ => new GpuRasterResult(GpuRasterOutcome.UnsupportedFeature, false),
        };
    }

    private static GpuRasterResult RasterizePolygon(uint word, IReadOnlyList<uint> p, GpuState state, GpuVram vram)
    {
        bool _gouraud = ((word >> 28) & 1) != 0;
        bool _quad = ((word >> 27) & 1) != 0;
        bool _textured = ((word >> 26) & 1) != 0;

        // Quads and texture mapping are not implemented by this slice; report
        // explicitly rather than mis-rendering half a quad or an untextured guess.
        if (_quad || _textured)
            return new GpuRasterResult(GpuRasterOutcome.UnsupportedFeature, false);

        var _offset = DecodeDrawOffset(state.DrawOffset);
        var _c0 = DecodeColor5(word);
        var _v0 = DecodeVertex(p[0], _offset);

        var _v1 = DecodeVertex(p[_gouraud ? 2 : 1], _offset);
        var _v2 = DecodeVertex(p[_gouraud ? 4 : 2], _offset);
        var _c1 = _gouraud ? DecodeColor5(p[1]) : _c0;
        var _c2 = _gouraud ? DecodeColor5(p[3]) : _c0;

        bool _wrotePixels = FillTriangle(_v0, _c0, _v1, _c1, _v2, _c2, state, vram);
        return new GpuRasterResult(GpuRasterOutcome.Rasterized, _wrotePixels);
    }

    private static GpuRasterResult RasterizeRectangle(uint word, IReadOnlyList<uint> p, GpuState state, GpuVram vram)
    {
        bool _textured = ((word >> 26) & 1) != 0;
        if (_textured)
            return new GpuRasterResult(GpuRasterOutcome.UnsupportedFeature, false);

        int _size = (int)((word >> 27) & 3);
        var _offset = DecodeDrawOffset(state.DrawOffset);
        var (_vx, _vy) = DecodeVertex(p[0], _offset);

        (int Width, int Height) _dims = _size switch
        {
            1 => (1, 1),
            2 => (8, 8),
            3 => (16, 16),
            _ => ((int)(p[1] & 0x3FF), (int)((p[1] >> 16) & 0x1FF)),
        };
        int _width = _dims.Width;
        int _height = _dims.Height;

        if (_width <= 0 || _height <= 0)
            return new GpuRasterResult(GpuRasterOutcome.Rasterized, false);

        var _color = DecodeColor5(word);
        ushort _pixel = PackPixel(_color.R, _color.G, _color.B);

        var (_minX, _minY, _maxX, _maxY) = ClipBounds(_vx, _vy, _vx + _width - 1, _vy + _height - 1, state);
        if (_minX > _maxX || _minY > _maxY)
            return new GpuRasterResult(GpuRasterOutcome.Rasterized, false);

        for (int y = _minY; y <= _maxY; y++)
            for (int x = _minX; x <= _maxX; x++)
                vram[x, y] = _pixel;

        return new GpuRasterResult(GpuRasterOutcome.Rasterized, true);
    }

    private static bool FillTriangle(
        (int X, int Y) v0, (int R, int G, int B) c0,
        (int X, int Y) v1, (int R, int G, int B) c1,
        (int X, int Y) v2, (int R, int G, int B) c2,
        GpuState state, GpuVram vram)
    {
        long _area = EdgeFunction(v0, v1, v2);
        if (_area == 0)
            return false; // degenerate (collinear/zero-area) triangle: deterministic no-op

        int _minX = Math.Min(v0.X, Math.Min(v1.X, v2.X));
        int _maxX = Math.Max(v0.X, Math.Max(v1.X, v2.X));
        int _minY = Math.Min(v0.Y, Math.Min(v1.Y, v2.Y));
        int _maxY = Math.Max(v0.Y, Math.Max(v1.Y, v2.Y));

        var (_clipMinX, _clipMinY, _clipMaxX, _clipMaxY) = ClipBounds(_minX, _minY, _maxX, _maxY, state);
        if (_clipMinX > _clipMaxX || _clipMinY > _clipMaxY)
            return false;

        bool _wrotePixels = false;
        for (int y = _clipMinY; y <= _clipMaxY; y++)
        {
            for (int x = _clipMinX; x <= _clipMaxX; x++)
            {
                var _p = (X: x, Y: y);
                long _w0 = EdgeFunction(v1, v2, _p);
                long _w1 = EdgeFunction(v2, v0, _p);
                long _w2 = EdgeFunction(v0, v1, _p);

                bool _inside = _area > 0
                    ? _w0 >= 0 && _w1 >= 0 && _w2 >= 0
                    : _w0 <= 0 && _w1 <= 0 && _w2 <= 0;

                if (!_inside)
                    continue;

                int _r = (int)((_w0 * c0.R + _w1 * c1.R + _w2 * c2.R) / _area);
                int _g = (int)((_w0 * c0.G + _w1 * c1.G + _w2 * c2.G) / _area);
                int _b = (int)((_w0 * c0.B + _w1 * c1.B + _w2 * c2.B) / _area);

                vram[x, y] = PackPixel(_r, _g, _b);
                _wrotePixels = true;
            }
        }

        return _wrotePixels;
    }

    private static long EdgeFunction((int X, int Y) a, (int X, int Y) b, (int X, int Y) p) =>
        (long)(b.X - a.X) * (p.Y - a.Y) - (long)(b.Y - a.Y) * (p.X - a.X);

    /// <summary>Vertex word: low 16 bits X, high 16 bits Y, each carrying an 11-bit signed coordinate; bits 11-15 of each slot are ignored, then GP0(E5h) drawing offset is applied.</summary>
    private static (int X, int Y) DecodeVertex(uint word, (int X, int Y) offset) =>
        (SignExtend11(word & 0x7FF) + offset.X, SignExtend11((word >> 16) & 0x7FF) + offset.Y);

    /// <summary>Primitive color word: bits 0-7 R, 8-15 G, 16-23 B (8-bit), truncated to the 5-bit VRAM channel width (same convention as <see cref="GpuDevice"/>'s Quick Rectangle Fill).</summary>
    private static (int R, int G, int B) DecodeColor5(uint word) =>
        ((int)((word >> 3) & 0x1F), (int)((word >> 11) & 0x1F), (int)((word >> 19) & 0x1F));

    private static ushort PackPixel(int r, int g, int b) =>
        (ushort)((r & 0x1F) | ((g & 0x1F) << 5) | ((b & 0x1F) << 10));

    /// <summary>GP0(E5h): bits 0-10 signed X offset, bits 11-21 signed Y offset (11-bit two's complement each).</summary>
    private static (int X, int Y) DecodeDrawOffset(uint value) =>
        (SignExtend11(value & 0x7FF), SignExtend11((value >> 11) & 0x7FF));

    private static int SignExtend11(uint value) => (value & 0x400) != 0 ? (int)value - 0x800 : (int)value;

    /// <summary>
    /// Intersects <paramref name="x0"/>..<paramref name="x1"/> / <paramref name="y0"/>..<paramref name="y1"/>
    /// with the GP0(E3h)/(E4h) drawing area and the VRAM bounds (1024x512). A
    /// fully clipped-away result yields <c>MinX &gt; MaxX</c> (an empty range the
    /// caller's loop naturally skips).
    /// </summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) ClipBounds(int x0, int y0, int x1, int y1, GpuState state)
    {
        int _areaX0 = (int)(state.DrawAreaTopLeft & 0x3FF);
        int _areaY0 = (int)((state.DrawAreaTopLeft >> 10) & 0x3FF);
        int _areaX1 = (int)(state.DrawAreaBottomRight & 0x3FF);
        int _areaY1 = (int)((state.DrawAreaBottomRight >> 10) & 0x3FF);

        int _minX = Math.Max(Math.Min(x0, x1), Math.Max(0, _areaX0));
        int _minY = Math.Max(Math.Min(y0, y1), Math.Max(0, _areaY0));
        int _maxX = Math.Min(Math.Max(x0, x1), Math.Min(GpuVram.Width - 1, _areaX1));
        int _maxY = Math.Min(Math.Max(y0, y1), Math.Min(GpuVram.Height - 1, _areaY1));

        return (_minX, _minY, _maxX, _maxY);
    }
}
