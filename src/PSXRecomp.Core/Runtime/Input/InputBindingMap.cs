using System.Collections.ObjectModel;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// Backend-independent, console-agnostic mapping of abstract physical inputs to
/// the logical buttons of a console controller. Bindings are validated on
/// insertion and the resulting contract is deterministic: the same pressed
/// physical inputs always produce the same logical snapshot. Multiple physical
/// inputs may map to the same button (e.g. keyboard and gamepad both bound to
/// the same control), but one physical input may not map to more than one
/// button (Issue #47).
/// </summary>
/// <typeparam name="TButton">
/// The console's logical button type: a <see cref="FlagsAttribute"/> enum owned
/// by the console module (for example the PS1 digital-pad buttons). This host
/// layer never references any console's buttons, device kinds, or protocol
/// semantics; each console module supplies its own enum and resolves the
/// resulting flags into its own device-aware snapshot.
/// </typeparam>
/// <remarks>
/// This is the console-agnostic "mapping" step of the input pipeline:
/// <c>physical input → mapping → logical input flags → console-specific
/// controller/SIO adapter</c>. It holds no OS, UI, console, or device
/// dependency (see docs/runtime/input.md).
/// </remarks>
[Domain]
public sealed class InputBindingMap<TButton>
    where TButton : struct, Enum
{
    private readonly List<InputBinding<TButton>> _bindings = new();

    /// <summary>The bindings added so far, in insertion order.</summary>
    public IReadOnlyList<InputBinding<TButton>> Bindings { get; }

    /// <summary>Creates an empty input binding map.</summary>
    public InputBindingMap()
    {
        Bindings = new ReadOnlyCollection<InputBinding<TButton>>(_bindings);
    }

    /// <summary>
    /// Adds a binding. Rejecting an input already bound to a different button
    /// keeps the mapping unambiguous; adding a second input to the same button
    /// is allowed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="binding"/> is <c>default</c>, targets the zero
    /// (<c>None</c>) button, or its input is already bound.
    /// </exception>
    public InputBindingMap<TButton> Add(InputBinding<TButton> binding)
    {
        if (binding.Input == default)
        {
            throw new ArgumentException("A binding must reference a defined physical input.", nameof(binding));
        }

        if (EqualityComparer<TButton>.Default.Equals(binding.Button, default))
        {
            throw new ArgumentException(
                "A binding must target a real button, not the zero/None enum value.", nameof(binding));
        }

        ulong buttonBits = Convert.ToUInt64(binding.Button);
        if ((buttonBits & (buttonBits - 1)) != 0)
        {
            throw new ArgumentException(
                $"A binding must target exactly one button; {binding.Button} is a composite flag value.", nameof(binding));
        }

        foreach (var existing in _bindings)
        {
            if (existing.Input != binding.Input)
            {
                continue;
            }

            if (EqualityComparer<TButton>.Default.Equals(existing.Button, binding.Button))
            {
                throw new ArgumentException(
                    $"Physical input {binding.Input} is already bound to {binding.Button}.", nameof(binding));
            }

            throw new ArgumentException(
                $"Physical input {binding.Input} is already bound to {existing.Button}; binding it to {binding.Button} as well would be ambiguous.",
                nameof(binding));
        }

        _bindings.Add(binding);
        return this;
    }

    /// <summary>
    /// Resolves the currently pressed physical inputs into an immutable,
    /// console-agnostic snapshot carrying the resulting logical button flags
    /// for both controller ports. This step has no device-kind policy: every
    /// console decides which attached device kinds it supports and turns the
    /// flags into its own snapshot (for the PS1, see
    /// <c>PSXRecomp.Core.Runtime.Input.Ps1.Ps1ControllerSnapshot.Resolve</c>).
    /// </summary>
    /// <typeparam name="TDeviceKind">
    /// The console's device-kind enum. It is carried by the physical state and
    /// ignored here; it exists only so this generic method accepts the
    /// console-owned state object.
    /// </typeparam>
    public ControllerInputSnapshot<TButton> Resolve<TDeviceKind>(PhysicalControllerState<TDeviceKind> state)
        where TDeviceKind : struct, Enum
    {
        return new ControllerInputSnapshot<TButton>(
            ResolveButtons(state, ControllerPort.Port1),
            ResolveButtons(state, ControllerPort.Port2));
    }

    private TButton ResolveButtons<TDeviceKind>(PhysicalControllerState<TDeviceKind> state, ControllerPort port)
        where TDeviceKind : struct, Enum
    {
        ulong pressed = 0;

        foreach (var binding in _bindings)
        {
            if (state.IsPressed(port, binding.Input))
            {
                pressed |= Convert.ToUInt64(binding.Button);
            }
        }

        return (TButton)Enum.ToObject(typeof(TButton), pressed);
    }
}
