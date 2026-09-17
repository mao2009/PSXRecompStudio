using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input.Ps1;

/// <summary>
/// Immutable PS1 logical controller state for one controller: which digital
/// buttons are pressed at one instant. Carries no physical-device, OS, or
/// backend concept (Issue #47).
/// </summary>
[Domain]
public readonly record struct Ps1ControllerState(Ps1Button Pressed)
{
    /// <summary>Whether <paramref name="button"/> is currently pressed.</summary>
    /// <param name="button">A single digital pad button (not <see cref="Ps1Button.None"/>).</param>
    public bool IsPressed(Ps1Button button)
    {
        return button != Ps1Button.None && (Pressed & button) == button;
    }

    /// <summary>
    /// Returns a new state with <paramref name="button"/> set to
    /// <paramref name="pressed"/>. The current state is not mutated.
    /// </summary>
    public Ps1ControllerState WithButton(Ps1Button button, bool pressed)
    {
        return new Ps1ControllerState(pressed ? Pressed | button : Pressed & ~button);
    }
}
