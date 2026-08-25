using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Sound Processing Unit (SPU) interface.
///
/// 24 voices with pitch, volume, ADSR envelope.
/// Main volume, reverb, CD audio input, noise generator.
/// Register space: 0x1F801C00-0x1F801DFF.
/// Triggers IRQ8 when sound buffer crosses IRQ address.
/// </summary>
[Domain]
public interface ISpu
{
    ushort ReadRegister(uint offset);
    void WriteRegister(uint offset, ushort value);
    bool HasInterrupt { get; }
    void AcknowledgeInterrupt();
    void Reset();
}
