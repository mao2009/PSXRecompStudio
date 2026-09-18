using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// Immutable, backend-independent, console-agnostic snapshot of the logical
/// button flags resolved for both controller ports at one instant. This is the
/// value captured from the mutable physical layer; a console module wraps it
/// with its own device-kind policy and state type. Its reads are deterministic
/// and free of OS/UI-thread dependencies (Issue #47).
/// </summary>
/// <typeparam name="TButton">
/// The console's logical button type (a <see cref="FlagsAttribute"/> enum owned
/// by the console module).
/// </typeparam>
[Domain]
public readonly record struct ControllerInputSnapshot<TButton>(TButton Port1, TButton Port2)
    where TButton : struct, Enum
{
    /// <summary>Gets the resolved logical button flags for the requested port.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not a defined port.</exception>
    public TButton this[ControllerPort port] => port switch
    {
        ControllerPort.Port1 => Port1,
        ControllerPort.Port2 => Port2,
        _ => throw new ArgumentOutOfRangeException(nameof(port), port, "Unknown controller port."),
    };
}
