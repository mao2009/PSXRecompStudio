using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 BIOS interface.
///
/// 512KB ROM at 0x1FC00000-0x1FC7FFFF.
/// Provides system calls: GPU, SPU, CD-ROM, memory card, controller I/O,
/// overlay loading, event handling, memory allocation.
/// </summary>
[Domain]
public interface IBios
{
    uint ReadBios(uint address);
    bool IsBiosFunction(uint pc);
    string GetBiosFunctionName(uint pc);
    void Reset();
}
