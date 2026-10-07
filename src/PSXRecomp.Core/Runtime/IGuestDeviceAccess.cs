using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The 32-bit physical device-register boundary a BIOS service uses when its documented behaviour is a register
/// access (A0:49 GPU_cw writes GP0). It is the same Runtime device graph every execution backend already shares
/// (<see cref="PsxDeviceGraph"/>); it never advances device time.
/// </summary>
[Domain]
public interface IGuestDeviceAccess
{
    /// <summary>Reads one 32-bit device register; false when the Runtime does not model the address.</summary>
    bool TryRead32(uint physicalAddress, out uint value);

    /// <summary>Writes one 32-bit device register; false when the Runtime does not model the address.</summary>
    bool TryWrite32(uint physicalAddress, uint value);
}

/// <summary>
/// An <see cref="IBiosRuntime"/> whose services need the device registers. The execution backend that owns the
/// device graph attaches it after the factory built the runtime; until then such a service fails closed
/// (<see cref="BiosServiceResult.UnsupportedState"/>), never a silent success.
/// </summary>
[Domain]
public interface IDeviceBiosRuntime : IBiosRuntime
{
    /// <summary>Supplies the device-register boundary the services use.</summary>
    void AttachDevices(IGuestDeviceAccess devices);
}
