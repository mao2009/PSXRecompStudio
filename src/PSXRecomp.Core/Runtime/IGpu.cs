using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Graphics Processing Unit (GPU) interface.
///
/// GP0 (0x1F801810): Rendering commands, VRAM access, display area.
/// GP1 (0x1F801814): Display control, reset, mode, DMA direction.
/// GPUREAD (0x1F801810): Read GP0/GP1 results.
///
/// VBlank triggers IRQ1 on the interrupt controller.
/// </summary>
[Domain]
public interface IGpu
{
    void WriteGP0(uint command);
    void WriteGP1(uint command);
    uint Read();
    IntPtr GetVramPointer();
    (ushort Width, ushort Height) GetDisplayResolution();
    bool HasVblank { get; }
    void AcknowledgeVblank();
    void Reset();
}
