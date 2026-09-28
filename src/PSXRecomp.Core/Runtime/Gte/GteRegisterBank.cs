using System.Numerics;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gte;

/// <summary>
/// PS1 GTE (COP2) register bank: 32 data + 32 control registers with the
/// documented per-register read/write semantics (Issue #581).
/// Storage keeps the raw written word; truncation/sign-extension is applied on
/// read, which is observably identical to hardware for MFC2/CFC2/SWC2.
/// Command execution is intentionally not implemented yet and fails closed.
/// Semantics follow the public psx-spx "GTE Registers" hardware documentation.
/// </summary>
[Domain]
public sealed class GteRegisterBank : IGte
{
    public const int RegisterCount = 32;

    // Data register indices with special semantics.
    private const int Vz0 = 1, Vz1 = 3, Vz2 = 5, Otz = 7, Ir0 = 8, Ir1 = 9, Ir2 = 10, Ir3 = 11;
    private const int Sxy0 = 12, Sxy1 = 13, Sxy2 = 14, Sxyp = 15, Sz0 = 16, Sz3 = 19;
    private const int Irgb = 28, Orgb = 29, Lzcs = 30, Lzcr = 31;

    // Control register indices with special semantics.
    private const int Rt33 = 4, L33 = 12, Lb3 = 20, H = 26, Dqa = 27, Zsf3 = 29, Zsf4 = 30, Flag = 31;

    /// <summary>FLAG bits 0-11 are hard-wired to zero; bit 31 is computed.</summary>
    private const uint FlagWritableMask = 0x7FFF_F000;

    /// <summary>FLAG bits 30-23 and 18-13 feed the bit-31 error summary.</summary>
    private const uint FlagErrorMask = 0x7F87_E000;

    private readonly uint[] _data = new uint[RegisterCount];
    private readonly uint[] _control = new uint[RegisterCount];

    /// <summary>No command is ever in flight while commands are unimplemented.</summary>
    public bool HasPendingData => false;

    public uint ReadDataRegister(int register)
    {
        uint _raw = _data[CheckIndex(register)];
        return register switch
        {
            Vz0 or Vz1 or Vz2 or Ir0 or Ir1 or Ir2 or Ir3 => SignExtend16(_raw),
            Otz or >= Sz0 and <= Sz3 => _raw & 0xFFFF,
            Sxyp => _data[Sxy2],
            Irgb or Orgb => ComputeOrgb(),
            Lzcr => (uint)CountLeadingSignBits(_data[Lzcs]),
            _ => _raw,
        };
    }

    public void WriteDataRegister(int register, uint value)
    {
        switch (CheckIndex(register))
        {
            case Sxyp:
                _data[Sxy0] = _data[Sxy1];
                _data[Sxy1] = _data[Sxy2];
                _data[Sxy2] = value;
                break;
            case Irgb:
                _data[Ir1] = (value & 0x1F) << 7;
                _data[Ir2] = ((value >> 5) & 0x1F) << 7;
                _data[Ir3] = ((value >> 10) & 0x1F) << 7;
                break;
            case Orgb:
            case Lzcr:
                break; // read-only
            default:
                _data[register] = value;
                break;
        }
    }

    public uint ReadControlRegister(int register)
    {
        uint _raw = _control[CheckIndex(register)];
        return register switch
        {
            // H is unsigned but reads back sign-extended (documented hardware quirk).
            Rt33 or L33 or Lb3 or H or Dqa or Zsf3 or Zsf4 => SignExtend16(_raw),
            Flag => (_raw & FlagErrorMask) != 0 ? _raw | 0x8000_0000 : _raw,
            _ => _raw,
        };
    }

    public void WriteControlRegister(int register, uint value)
    {
        _control[CheckIndex(register)] = register == Flag ? value & FlagWritableMask : value;
    }

    public void ExecuteCommand(uint command, bool sf, bool lm)
    {
        throw new NotSupportedException(
            $"GTE command 0x{command & 0x3F:X2} is not implemented (register bank only, Issue #581).");
    }

    public void Reset()
    {
        Array.Clear(_data);
        Array.Clear(_control);
    }

    private static int CheckIndex(int register)
    {
        if ((uint)register >= RegisterCount)
            throw new ArgumentOutOfRangeException(nameof(register), register, "GTE register index must be 0..31.");
        return register;
    }

    private static uint SignExtend16(uint value) => (uint)(short)value;

    /// <summary>Packs IR1-IR3 as 5:5:5 (value / 0x80, saturated to 0..0x1F).</summary>
    private uint ComputeOrgb()
    {
        static uint Channel(uint ir) => (uint)Math.Clamp((short)ir >> 7, 0, 0x1F);
        return Channel(_data[Ir1]) | (Channel(_data[Ir2]) << 5) | (Channel(_data[Ir3]) << 10);
    }

    /// <summary>Leading bits equal to bit 31 (1..32).</summary>
    private static int CountLeadingSignBits(uint value) =>
        BitOperations.LeadingZeroCount((int)value < 0 ? ~value : value);
}
