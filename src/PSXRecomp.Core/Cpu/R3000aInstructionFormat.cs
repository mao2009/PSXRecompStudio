using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public enum R3000aInstructionFormat : byte
{
    None = 0,
    R = 1,
    I = 2,
    J = 3,
    Regimm = 4,
    Cop = 5,
}
