using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input.Ps1;

/// <summary>
/// Immutable, backend-independent PS1 input state for both controller ports at
/// one instant: which kind of controller is attached to each port and, when
/// supported, which buttons are pressed. This is the PS1 console module's
/// device-aware view over the console-agnostic flags produced by
/// <see cref="InputBindingMap{TButton}"/>; it can be constructed freely in
/// tests and its reads are deterministic and free of OS/UI-thread dependencies
/// (Issue #47).
/// </summary>
[Domain]
public readonly record struct Ps1ControllerSnapshot(Ps1ControllerPortState Port1, Ps1ControllerPortState Port2)
{
    /// <summary>Gets the controller-port state for the requested port.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not a defined port.</exception>
    public Ps1ControllerPortState this[ControllerPort port] => port switch
    {
        ControllerPort.Port1 => Port1,
        ControllerPort.Port2 => Port2,
        _ => throw new ArgumentOutOfRangeException(nameof(port), port, "Unknown controller port."),
    };

    /// <summary>
    /// Resolves the current physical input state into the PS1 snapshot, applying
    /// PS1 device policy: digital-button mapping is applied only on ports holding
    /// a <see cref="Ps1ControllerDeviceKind.StandardDigitalPad"/>; unsupported or
    /// unattached ports keep the empty digital state, never a fabricated one.
    /// </summary>
    public static Ps1ControllerSnapshot Resolve(
        InputBindingMap<Ps1Button> map,
        PhysicalControllerState<Ps1ControllerDeviceKind> state)
    {
        var _flags = map.Resolve(state);

        return new Ps1ControllerSnapshot(
            ResolvePort(state, _flags, ControllerPort.Port1),
            ResolvePort(state, _flags, ControllerPort.Port2));
    }

    private static Ps1ControllerPortState ResolvePort(
        PhysicalControllerState<Ps1ControllerDeviceKind> state,
        ControllerInputSnapshot<Ps1Button> flags,
        ControllerPort port)
    {
        var _kind = state.GetDevice(port);
        var _digital = _kind == Ps1ControllerDeviceKind.StandardDigitalPad
            ? new Ps1ControllerState(flags[port])
            : default;

        return new Ps1ControllerPortState(_kind, _digital);
    }
}
