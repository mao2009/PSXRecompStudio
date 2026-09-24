using System.Security.Cryptography;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gpu;

/// <summary>
/// A deterministic, presentation-agnostic capture of the GPU's configured
/// display region at a point in time (Issue #441). Pixels are copied in the
/// native PS1 15-bit VRAM format (see <see cref="GpuVram"/>); no display-format
/// conversion (e.g. to RGBA32) happens here, and no Studio/Avalonia bitmap type
/// is referenced — that is a presentation concern for a future consumer, not
/// this boundary.
///
/// <para>
/// <see cref="Capture"/> is a pure function of the supplied VRAM and display
/// state: it has no dependency on scheduler/VBlank timing (Issue #442) and can
/// be called at any point to answer "what would the display show right now",
/// not "when does a frame become final".
/// </para>
/// </summary>
[Domain]
public sealed class FrameSnapshot
{
    private readonly ushort[] _pixels;

    private FrameSnapshot(int width, int height, ushort[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major (top-to-bottom, left-to-right) native 15-bit VRAM pixels; length is <see cref="Width"/> * <see cref="Height"/>.</summary>
    public IReadOnlyList<ushort> Pixels => _pixels;

    /// <summary>
    /// Captures the region named by GP1(05h) (<see cref="GpuState.DisplayVramStart"/>)
    /// and <paramref name="resolution"/> (from <see cref="GpuDevice.GetDisplayResolution"/>)
    /// out of <paramref name="vram"/>. A display region that extends past VRAM
    /// bounds (1024x512) is clipped, never wrapped or thrown.
    /// </summary>
    public static FrameSnapshot Capture(GpuVram vram, GpuState state, (ushort Width, ushort Height) resolution)
    {
        ArgumentNullException.ThrowIfNull(vram);
        ArgumentNullException.ThrowIfNull(state);

        int _startX = (int)(state.DisplayVramStart & 0x3FF);
        int _startY = (int)((state.DisplayVramStart >> 10) & 0x1FF);

        int _width = Math.Max(0, Math.Min((int)resolution.Width, GpuVram.Width - _startX));
        int _height = Math.Max(0, Math.Min((int)resolution.Height, GpuVram.Height - _startY));

        var _pixels = new ushort[_width * _height];
        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                _pixels[(y * _width) + x] = vram[_startX + x, _startY + y];

        return new FrameSnapshot(_width, _height, _pixels);
    }

    /// <summary>
    /// Deterministic content hash (SHA-256 over the row-major native-format
    /// pixel bytes, little-endian per halfword). No project-wide hash
    /// convention exists yet for frame evidence (checked: no prior "frame hash"
    /// usage in the repository); SHA-256 is chosen as a standard,
    /// dependency-free, deterministic choice already used elsewhere in this
    /// project (<see cref="PSXRecomp.Core.TitleIdentity.BootExecutableFingerprint"/>).
    /// </summary>
    public byte[] ComputeStableHash()
    {
        var _bytes = new byte[_pixels.Length * sizeof(ushort)];
        Buffer.BlockCopy(_pixels, 0, _bytes, 0, _bytes.Length);
        return SHA256.HashData(_bytes);
    }
}
