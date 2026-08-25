using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// DMA channel register state (Issue #44 Architecture contract).
/// </summary>
[Domain]
[StructLayout(LayoutKind.Sequential)]
public struct DmaChannelState
{
    public uint Madr;
    public uint Bcr;
    public uint Chcr;

    public static DmaChannelState Create() => new();

    public static readonly uint StructSize = (uint)Marshal.SizeOf<DmaChannelState>();
}
