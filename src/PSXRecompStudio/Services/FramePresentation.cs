using System;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecompStudio.Services;

/// <summary>What the Studio frame surface has to show.</summary>
[Application]
public enum FramePresentationState
{
    /// <summary>The Runtime produced no frame (never ran, or untouched power-on VRAM). Nothing is drawn.</summary>
    NoFrame,

    /// <summary>A snapshot exists but has no displayable area (e.g. display region clipped to nothing).</summary>
    Unsupported,

    /// <summary>A real Runtime frame is available in <see cref="PresentationFrame.Bgra32"/>.</summary>
    Ready,
}

/// <summary>
/// Presentation copy of a Runtime <see cref="FrameSnapshot"/> (Issue #455): opaque
/// BGRA32 pixels converted 1:1 from the native 15-bit VRAM format. Scaling to the
/// window is the view's job (nearest-neighbor); this copy is never resized, and
/// the source snapshot is never touched.
/// </summary>
[Application]
public sealed class PresentationFrame
{
    private readonly byte[] _bgra;

    private PresentationFrame(FramePresentationState state, int width, int height, byte[] bgra)
    {
        State = state;
        Width = width;
        Height = height;
        _bgra = bgra;
    }

    public static PresentationFrame NoFrame { get; } = new(FramePresentationState.NoFrame, 0, 0, []);

    public FramePresentationState State { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major B,G,R,A bytes (A is always 255); length is Width * Height * 4.</summary>
    public ReadOnlySpan<byte> Bgra32 => _bgra;

    /// <summary>
    /// Adapts a Runtime frame. <paramref name="frame"/> null, or <paramref name="hasFrameEvidence"/>
    /// false, yields <see cref="FramePresentationState.NoFrame"/> — never a placeholder image.
    /// </summary>
    public static PresentationFrame From(FrameSnapshot? frame, bool hasFrameEvidence = true)
    {
        if (frame is null || !hasFrameEvidence)
        {
            return NoFrame;
        }

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return new(FramePresentationState.Unsupported, frame.Width, frame.Height, []);
        }

        var bgra = new byte[frame.Width * frame.Height * 4];
        var pixels = frame.Pixels;
        for (int i = 0; i < pixels.Count; i++)
        {
            int v = pixels[i];
            // 5-bit -> 8-bit by bit replication; bit 15 (mask/STP) is not display alpha.
            bgra[(i * 4) + 0] = Expand((v >> 10) & 0x1F);
            bgra[(i * 4) + 1] = Expand((v >> 5) & 0x1F);
            bgra[(i * 4) + 2] = Expand(v & 0x1F);
            bgra[(i * 4) + 3] = 0xFF;
        }

        return new(FramePresentationState.Ready, frame.Width, frame.Height, bgra);
    }

    private static byte Expand(int c5) => (byte)((c5 << 3) | (c5 >> 2));
}
