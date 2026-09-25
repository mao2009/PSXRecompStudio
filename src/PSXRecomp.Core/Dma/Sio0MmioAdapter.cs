using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.Sio;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// SIO0 MMIO Runtime Adapter (Issue #542).
/// Bridges Physical Address → IMemoryBus → MMIO routing → managed <see cref="Sio0Device"/>.
/// Unlike the DMA/timer/interrupt adapters this does not touch the native core:
/// SIO0 is a pure managed model, following the GPU precedent (ADR-022).
///
/// Every address in 0x1F801040-0x1F80105F is routed here. Named registers are
/// accessed at their exact address (DATA byte, STAT word, MODE/CTRL/BAUD
/// halfwords; wider writes are truncated to the register width). Any other
/// address in the window (<see cref="Sio0RegisterType.Reserved"/>) reads 0 and
/// accepts-and-ignores writes.
/// </summary>
[Domain]
public sealed class Sio0MmioAdapter : IMemoryBus
{
    private readonly Sio0Device _device;

    public Sio0MmioAdapter(Sio0Device device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public uint Read(uint address) => ReadRegister(address);

    public void Write(uint address, uint value) => WriteRegister(address, value);

    public uint ReadRegister(uint address) =>
        Ps1MemoryMap.GetSio0RegisterType(address) switch
        {
            Sio0RegisterType.Data => _device.ReadData(),
            Sio0RegisterType.Status => _device.ReadStatus(),
            Sio0RegisterType.Mode => _device.ReadMode(),
            Sio0RegisterType.Control => _device.ReadControl(),
            Sio0RegisterType.Baud => _device.ReadBaud(),
            _ => 0, // Reserved (or outside the window): fixed 0.
        };

    public void WriteRegister(uint address, uint value)
    {
        switch (Ps1MemoryMap.GetSio0RegisterType(address))
        {
            case Sio0RegisterType.Data:
                _device.WriteData((byte)value);
                break;
            case Sio0RegisterType.Mode:
                _device.WriteMode((ushort)value);
                break;
            case Sio0RegisterType.Control:
                _device.WriteControl((ushort)value);
                break;
            case Sio0RegisterType.Baud:
                _device.WriteBaud((ushort)value);
                break;
            // Status is read-only; Reserved writes are accepted and ignored.
        }
    }
}
