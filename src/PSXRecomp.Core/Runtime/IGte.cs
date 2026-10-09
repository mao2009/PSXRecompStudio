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
///
/// Issue #447: <see cref="Gte.GteRegisterBank"/> is the one GTE register state.
/// <see cref="PsxDeviceGraph"/> attaches it to the native CPU, which forwards
/// MFC2/CFC2/MTC2/CTC2, LWC2/SWC2 and COP2 commands to it after its own SR.CU2
/// check; every execution backend reaches this same instance. A command it does
/// not implement fails closed (<see cref="ExecuteCommand"/> returns false and the
/// CPU stops), never a NOP.
/// </summary>
[Domain]
public interface IGte
{
    uint ReadDataRegister(int register);
    void WriteDataRegister(int register, uint value);
    uint ReadControlRegister(int register);
    void WriteControlRegister(int register, uint value);

    /// <summary>
    /// Executes a GTE command: the COP2 instruction's low 25 bits (opcode in bits
    /// 0-5, lm bit 10, sf bit 19, MVMVA selectors in bits 13-18).
    /// </summary>
    /// <returns>False, with no register changed, when the command is not implemented.</returns>
    bool ExecuteCommand(uint command);

    bool HasPendingData { get; }
    void Reset();
}
