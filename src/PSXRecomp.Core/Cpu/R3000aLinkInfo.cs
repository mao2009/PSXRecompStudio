using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct R3000aLinkInfo
{
    public const byte MaxRegisterNumber = 31;
    public const byte DefaultLinkRegister = 31;

    private readonly bool _writesLink;
    private readonly byte _linkRegister;

    private R3000aLinkInfo(bool writesLink, byte linkRegister)
    {
        _writesLink = writesLink;
        _linkRegister = linkRegister;
    }

    public bool WritesLink => _writesLink;
    public byte LinkRegister => _linkRegister;

    public static R3000aLinkInfo None => default;

    public static R3000aLinkInfo CreateRa()
    {
        return new R3000aLinkInfo(true, DefaultLinkRegister);
    }

    public static R3000aLinkInfo Create(byte linkRegister)
    {
        if (linkRegister > MaxRegisterNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(linkRegister), linkRegister, "Link register must be within [0, 31].");
        }

        return new R3000aLinkInfo(true, linkRegister);
    }
}
