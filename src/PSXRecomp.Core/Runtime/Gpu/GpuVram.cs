using PSXRecomp.Architecture;
using System.Runtime.InteropServices;

namespace PSXRecomp.Core.Runtime.Gpu;

/// <summary>
/// PS1 video memory: 1024 halfwords wide by 512 lines of 16-bit pixels (1 MiB).
/// Not mapped to the CPU bus; reachable only via GPU commands (GP0) and DMA.
/// Deterministically zeroed on construction; <see cref="GpuDevice.Reset"/>
/// deliberately leaves VRAM contents untouched (matches real hardware).
/// </summary>
[Domain]
public sealed class GpuVram : IDisposable
{
    public const int Width = 1024;
    public const int Height = 512;
    public const int HalfwordCount = Width * Height;

    private readonly ushort[] _data = new ushort[HalfwordCount];
    private GCHandle _pin;

    public GpuVram()
    {
        _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);
    }

    /// <summary>16-bit pixel at (<paramref name="x"/>, <paramref name="y"/>). <paramref name="x"/> is a halfword address (0..1023).</summary>
    public ushort this[int x, int y]
    {
        get
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"VRAM coordinate ({x},{y}) out of range 1024x512");
            return _data[y * Width + x];
        }
        set
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"VRAM coordinate ({x},{y}) out of range 1024x512");
            _data[y * Width + x] = value;
        }
    }

    /// <summary>Pointer to pixel (0,0). Valid until this VRAM is disposed.</summary>
    public IntPtr Pointer => _pin.AddrOfPinnedObject();

    /// <summary>Writes every halfword to zero. Not called by GPU reset.</summary>
    public void Clear()
    {
        Array.Clear(_data);
    }

    public void Dispose()
    {
        if (_pin.IsAllocated)
            _pin.Free();
        GC.SuppressFinalize(this);
    }

    ~GpuVram()
    {
        Dispose();
    }
}