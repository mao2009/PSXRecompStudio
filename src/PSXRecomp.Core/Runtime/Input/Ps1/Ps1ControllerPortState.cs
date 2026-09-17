using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input.Ps1;

/// <summary>
/// What a single PS1 controller port carries at one instant: the attached
/// device kind and, for supported kinds, the logical PS1 input state.
/// Unsupported device kinds keep the empty state — no digital-pad state is
/// fabricated for a device whose protocol this contract does not model
/// (Issue #47).
/// </summary>
[Domain]
public readonly record struct Ps1ControllerPortState(Ps1ControllerDeviceKind DeviceKind, Ps1ControllerState State)
{
    /// <summary>Whether the attached device kind has a modeled, usable state contract.</summary>
    public bool IsSupported => DeviceKind == Ps1ControllerDeviceKind.StandardDigitalPad;

    /// <summary>Whether any controller is attached to the port (supported or not).</summary>
    public bool IsDevicePresent => DeviceKind != Ps1ControllerDeviceKind.NotPresent;
}
