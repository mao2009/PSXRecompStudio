using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 CD-ROM Controller interface.
///
/// Registers at 0x1F801800-0x1F801803 (index 0-3).
/// Index is selected by bits 0-1 of the address.
/// Handles sector reads, seeking, audio playback, lid open/close.
/// Triggers IRQ2 on command completion and data ready.
/// </summary>
[Domain]
public interface ICdRom
{
    byte ReadRegister(int index);
    void WriteRegister(int index, byte value);
    byte ReadData();
    byte ReadStatus();
    void WriteCommand(byte command);
    byte GetInterruptFlag();
    void SetInterruptFlag(byte value);
    bool HasInterrupt { get; }
    void AcknowledgeInterrupt();
    void Reset();
}
