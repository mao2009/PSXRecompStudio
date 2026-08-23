using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public enum R3000aOperandKind : byte
{
    None = 0,
    Register = 1,
    Immediate = 2,
    MemoryOffset = 3,
    Shamt = 4,
    JumpIndex = 5,
    CopReg = 6,
}
