using System.Numerics;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gte;

/// <summary>
/// PS1 GTE (COP2) register bank: 32 data + 32 control registers with the
/// documented per-register read/write semantics (Issue #581).
/// Storage keeps the raw written word; truncation/sign-extension is applied on
/// read, which is observably identical to hardware for MFC2/CFC2/SWC2.
/// Semantics follow the public psx-spx "GTE Registers" hardware documentation.
/// <para>
/// This is the one GTE state of a running guest (Issue #447): the native CPU
/// reaches it through <c>PSXCoreWrapper.AttachGte</c>. <see cref="ExecuteCommand"/>
/// applies the arithmetic kernels (RTPS, NCLIP, AVSZ3, AVSZ4) to these registers;
/// any other command fails closed and is recorded in <see cref="LastUnsupportedCommand"/>.
/// </para>
/// </summary>
[Domain]
public sealed class GteRegisterBank : IGte
{
    public const int RegisterCount = 32;

    // Data register indices with special semantics.
    private const int Vz0 = 1, Vz1 = 3, Vz2 = 5, Otz = 7, Ir0 = 8, Ir1 = 9, Ir2 = 10, Ir3 = 11;
    private const int Sxy0 = 12, Sxy1 = 13, Sxy2 = 14, Sxyp = 15, Sz0 = 16, Sz1 = 17, Sz2 = 18, Sz3 = 19;
    private const int Mac0 = 24, Mac1 = 25, Mac2 = 26, Mac3 = 27;
    private const int Irgb = 28, Orgb = 29, Lzcs = 30, Lzcr = 31;
    private const int Vxy0 = 0;

    // Control register indices with special semantics.
    private const int Rt33 = 4, L33 = 12, Lb3 = 20, H = 26, Dqa = 27, Zsf3 = 29, Zsf4 = 30, Flag = 31;
    private const int Rt11Rt12 = 0, Rt13Rt21 = 1, Rt22Rt23 = 2, Rt31Rt32 = 3, Trx = 5, Try = 6, Trz = 7;
    private const int Ofx = 24, Ofy = 25, Dqb = 28;

    // GTE command opcodes (instruction bits 0-5) the bank implements.
    public const uint CommandRtps = 0x01, CommandNclip = 0x06, CommandAvsz3 = 0x2D, CommandAvsz4 = 0x2E;

    /// <summary>FLAG bits 0-11 are hard-wired to zero; bit 31 is computed.</summary>
    private const uint FlagWritableMask = 0x7FFF_F000;

    /// <summary>FLAG bits 30-23 and 18-13 feed the bit-31 error summary.</summary>
    private const uint FlagErrorMask = 0x7F87_E000;

    private readonly uint[] _data = new uint[RegisterCount];
    private readonly uint[] _control = new uint[RegisterCount];

    /// <summary>Commands complete synchronously, so no result is ever pending.</summary>
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

    /// <summary>The last command <see cref="ExecuteCommand"/> refused (its 25-bit word), or null.</summary>
    public uint? LastUnsupportedCommand { get; private set; }

    /// <summary>Commands executed since construction or <see cref="Reset"/> (run evidence).</summary>
    public ulong CommandsExecuted { get; private set; }

    public bool ExecuteCommand(uint command)
    {
        if (!TryExecute(command))
        {
            LastUnsupportedCommand = command & 0x01FF_FFFF;
            return false;
        }

        CommandsExecuted++;
        return true;
    }

    private bool TryExecute(uint command)
    {
        bool sf = (command & (1u << 19)) != 0;
        bool lm = (command & (1u << 10)) != 0;
        switch (command & 0x3F)
        {
            case CommandRtps:
                ApplyRtps(GteRtpsKernel.Execute(RtpsInput(), sf, lm));
                return true;
            case CommandNclip:
                var nclip = GteNclipKernel.Execute(
                    Low(_data[Sxy0]), High(_data[Sxy0]), Low(_data[Sxy1]), High(_data[Sxy1]),
                    Low(_data[Sxy2]), High(_data[Sxy2]));
                _data[Mac0] = (uint)nclip.Mac0;
                WriteControlRegister(Flag, nclip.Flag);
                return true;
            case CommandAvsz3:
                ApplyAvsz(GteAvszKernel.Avsz3(Sz(Sz1), Sz(Sz2), Sz(Sz3), Low(_control[Zsf3])));
                return true;
            case CommandAvsz4:
                ApplyAvsz(GteAvszKernel.Avsz4(Sz(Sz0), Sz(Sz1), Sz(Sz2), Sz(Sz3), Low(_control[Zsf4])));
                return true;
            default:
                return false;
        }
    }

    public void Reset()
    {
        Array.Clear(_data);
        Array.Clear(_control);
        LastUnsupportedCommand = null;
        CommandsExecuted = 0;
    }

    private GteRtpsInput RtpsInput() => new(
        Low(_control[Rt11Rt12]), High(_control[Rt11Rt12]), Low(_control[Rt13Rt21]),
        High(_control[Rt13Rt21]), Low(_control[Rt22Rt23]), High(_control[Rt22Rt23]),
        Low(_control[Rt31Rt32]), High(_control[Rt31Rt32]), Low(_control[Rt33]),
        (int)_control[Trx], (int)_control[Try], (int)_control[Trz],
        Low(_data[Vxy0]), High(_data[Vxy0]), Low(_data[Vz0]),
        (ushort)_control[H], (int)_control[Ofx], (int)_control[Ofy], Low(_control[Dqa]), (int)_control[Dqb]);

    private void ApplyRtps(in GteRtpsResult r)
    {
        _data[Mac0] = (uint)r.Mac0;
        _data[Mac1] = (uint)r.Mac1;
        _data[Mac2] = (uint)r.Mac2;
        _data[Mac3] = (uint)r.Mac3;
        _data[Ir0] = (uint)r.Ir0;
        _data[Ir1] = (uint)r.Ir1;
        _data[Ir2] = (uint)r.Ir2;
        _data[Ir3] = (uint)r.Ir3;
        PushSz(r.Sz);
        WriteDataRegister(Sxyp, (ushort)r.Sx | ((uint)(ushort)r.Sy << 16));
        WriteControlRegister(Flag, r.Flag);
    }

    private void ApplyAvsz(in GteAvszResult r)
    {
        _data[Mac0] = (uint)r.Mac0;
        _data[Otz] = r.Otz;
        WriteControlRegister(Flag, r.Flag);
    }

    private void PushSz(ushort sz)
    {
        _data[Sz0] = _data[Sz1];
        _data[Sz1] = _data[Sz2];
        _data[Sz2] = _data[Sz3];
        _data[Sz3] = sz;
    }

    private ushort Sz(int register) => (ushort)_data[register];

    private static short Low(uint value) => (short)value;

    private static short High(uint value) => (short)(value >> 16);

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
