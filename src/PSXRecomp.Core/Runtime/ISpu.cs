using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Sound Processing Unit (SPU) higher-level interface.
///
/// Issue #445 implements the guest-visible 0x1F801C00-0x1F801DFF register
/// store in native Rust behind PSXMemory; this interface remains the future
/// behavior seam for 24-voice synthesis, ADSR/pitch, reverb, CD audio input,
/// sound-RAM behavior, and IRQ9. The register-only slice does not synthesize
/// audio or raise the interrupt.
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
