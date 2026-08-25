using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Geometry Transformation Engine (GTE) interface.
///
/// The GTE is a coprocessor (COP2) used for 3D geometry calculations.
/// It provides hardware-accelerated vector/matrix operations.
///
/// Data registers (32): V0-V2 vectors, IR0-IR3 intermediates,
///   SXY0-SXY2 screen coords, SZ0-SZ3 screen Z, MAC0-MAC3 accumulators,
///   OTZ average Z, RGBC color, RGB0-RGB2 output, LZCS/LZCR leading zero.
///
/// Control registers (32): rotation matrix, light vectors, light colors,
///   background color, far/color FIFO, offset, projection plane distance,
///   clipping values, screen offset, depth scaling.
///
/// Commands are issued via COP2 instructions (e.g. RTPS, NCLIP, AVSZ3).
/// </summary>
[Domain]
public interface IGte
{
    uint ReadDataRegister(int register);
    void WriteDataRegister(int register, uint value);
    uint ReadControlRegister(int register);
    void WriteControlRegister(int register, uint value);

    /// <summary>
    /// Execute a GTE command.
    /// </summary>
    /// <param name="command">Command code from COP2 instruction.</param>
    /// <param name="sf">Shift fraction (false = no shift, true = shift 12 bits).</param>
    /// <param name="lm">Saturate IR0 to 0x0000-0x7FFF when true.</param>
    void ExecuteCommand(uint command, bool sf, bool lm);

    bool HasPendingData { get; }
    void Reset();
}
