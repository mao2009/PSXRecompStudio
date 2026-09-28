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

    /// <summary>Number of bytes currently readable from the CD-ROM data FIFO.</summary>
    int DataBytesAvailable { get; }

    /// <summary>Whether the current command response exposes a data-ready condition.</summary>
    bool DataReady { get; }

    /// <summary>
    /// Monotonic identity of the most recently activated interrupt packet.
    /// Schedulers use this to distinguish INT3 -> INT1/INT2 transitions even
    /// when the enabled CD-ROM interrupt line never has an observable low gap.
    /// </summary>
    ulong InterruptGeneration { get; }

    byte ReadStatus();
    void WriteCommand(byte command);
    byte GetInterruptFlag();
    void SetInterruptFlag(byte value);
    bool HasInterrupt { get; }
    void AcknowledgeInterrupt();
    void Reset();
}
