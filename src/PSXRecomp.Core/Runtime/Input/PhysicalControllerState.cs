using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// Console-agnostic mutable physical input state: which abstract physical
/// inputs are currently pressed on which controller port, and which kind of
/// console device is attached to each port. Owned by a future OS/device adapter
/// and later queried (or mutated by tests); the Runtime must never receive this
/// object directly — it is captured into an immutable
/// <see cref="ControllerInputSnapshot{TButton}"/> instead (Issue #47).
/// </summary>
/// <typeparam name="TDeviceKind">
/// The console module's device-kind enum. The host layer stores and validates
/// it generically; the console module owns its values, its default
/// (<c>NotPresent</c>-equivalent) member, and the policy for which kinds it
/// supports.
/// </typeparam>
[Domain]
public sealed class PhysicalControllerState<TDeviceKind>
    where TDeviceKind : struct, Enum
{
    private readonly HashSet<(ControllerPort Port, PhysicalInputId Input)> _pressed = new();
    private readonly Dictionary<ControllerPort, TDeviceKind> _devices = new();

    /// <summary>Sets or clears whether <paramref name="input"/> is pressed on <paramref name="port"/>.</summary>
    public void SetPressed(ControllerPort port, PhysicalInputId input, bool pressed)
    {
        if (pressed)
        {
            _pressed.Add((port, input));
        }
        else
        {
            _pressed.Remove((port, input));
        }
    }

    /// <summary>Whether <paramref name="input"/> is currently pressed on <paramref name="port"/>.</summary>
    public bool IsPressed(ControllerPort port, PhysicalInputId input) => _pressed.Contains((port, input));

    /// <summary>Declares the kind of controller attached to <paramref name="port"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined device kind.</exception>
    public void SetDevice(ControllerPort port, TDeviceKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown controller device kind.");
        }

        _devices[port] = kind;
    }

    /// <summary>
    /// Kind of controller currently attached to <paramref name="port"/>, or
    /// <c>default</c> (the console's <c>NotPresent</c>-equivalent member) when
    /// the port was never assigned.
    /// </summary>
    public TDeviceKind GetDevice(ControllerPort port)
    {
        return _devices.TryGetValue(port, out var kind) ? kind : default;
    }
}
