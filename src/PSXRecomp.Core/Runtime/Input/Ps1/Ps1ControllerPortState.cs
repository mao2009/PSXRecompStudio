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
    /// <summary>The kind of device attached to this port.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="DeviceKind"/> is not a defined device kind.</exception>
    public Ps1ControllerDeviceKind DeviceKind { get; init; } = Enum.IsDefined(DeviceKind)
        ? DeviceKind
        : throw new ArgumentOutOfRangeException(nameof(DeviceKind), DeviceKind, "Unknown device kind.");

    /// <summary>
    /// The logical PS1 input state for this port. Always <c>default</c> for
    /// unsupported or absent device kinds; only a <see cref="Ps1ControllerDeviceKind.StandardDigitalPad"/>
    /// carries meaningful digital-pad state.
    /// </summary>
    public Ps1ControllerState State { get; init; } = DeviceKind == Ps1ControllerDeviceKind.StandardDigitalPad
        ? State
        : default;

    /// <summary>Whether the attached device kind has a modeled, usable state contract.</summary>
    public bool IsSupported => DeviceKind == Ps1ControllerDeviceKind.StandardDigitalPad;

    /// <summary>Whether any controller is attached to the port (supported or not).</summary>
    public bool IsDevicePresent => DeviceKind != Ps1ControllerDeviceKind.NotPresent;
}
