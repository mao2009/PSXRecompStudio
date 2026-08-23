using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct R3000aLoadDelayInfo
{
    public const byte MaxRegisterNumber = 31;

    private readonly bool _producesLoadDelay;
    private readonly byte _targetRegister;
    private readonly bool _lwlLwrPairSpecial;

    private R3000aLoadDelayInfo(bool producesLoadDelay, byte targetRegister, bool lwlLwrPairSpecial)
    {
        _producesLoadDelay = producesLoadDelay;
        _targetRegister = targetRegister;
        _lwlLwrPairSpecial = lwlLwrPairSpecial;
    }

    public bool ProducesLoadDelay => _producesLoadDelay;
    public byte TargetRegister => _targetRegister;
    public bool LwlLwrPairSpecial => _lwlLwrPairSpecial;

    public static R3000aLoadDelayInfo None => default;

    public static R3000aLoadDelayInfo Create(byte targetRegister)
    {
        EnsureRegisterInRange(targetRegister);
        return new R3000aLoadDelayInfo(true, targetRegister, false);
    }

    public static R3000aLoadDelayInfo CreateLwlLwrPair(byte targetRegister)
    {
        EnsureRegisterInRange(targetRegister);
        return new R3000aLoadDelayInfo(true, targetRegister, true);
    }

    private static void EnsureRegisterInRange(byte register)
    {
        if (register > MaxRegisterNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(register), register, "Target register must be within [0, 31].");
        }
    }
}
