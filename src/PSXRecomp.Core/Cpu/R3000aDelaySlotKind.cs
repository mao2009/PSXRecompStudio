using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public enum R3000aDelaySlotKind : byte
{
    None = 0,
    Unconditional = 1,
    Conditional = 2,
}
