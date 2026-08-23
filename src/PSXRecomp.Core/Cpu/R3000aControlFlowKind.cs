using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public enum R3000aControlFlowKind : byte
{
    Sequential = 0,
    JumpAbsolute = 1,
    JumpRegister = 2,
    ConditionalBranch = 3,
    LinkBranch = 4,
    Trap = 5,
    Coprocessor = 6,
    Reserved = 7,
}
