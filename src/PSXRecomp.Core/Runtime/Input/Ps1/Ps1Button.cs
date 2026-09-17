using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input.Ps1;

/// <summary>
/// The digital buttons of a standard PS1 controller, expressed as a bit-flag
/// word so that any combination of simultaneous presses is one type-safe value.
/// </summary>
/// <remarks>
/// Bit positions match the standard digital pad's 16-bit response layout
/// (active-low on the wire, high bit = Select/Start) so a future SIO adapter
/// can convert the logical flags to serial data without a second constant
/// table. Only the 14 digital-pad buttons are modeled; analog, vibration, and
/// pressure sensitivity are explicitly out of the current scope (Issue #47).
/// This type is owned by the PS1 console module; the console-agnostic host
/// input layer never references it.
/// </remarks>
[Flags]
[Domain]
public enum Ps1Button : ushort
{
    /// <summary>No button pressed.</summary>
    None = 0x0000,

    /// <summary>D-pad up.</summary>
    Up = 0x0001,

    /// <summary>D-pad down.</summary>
    Down = 0x0002,

    /// <summary>D-pad left.</summary>
    Left = 0x0004,

    /// <summary>D-pad right.</summary>
    Right = 0x0008,

    /// <summary>The Square button (the square glyph).</summary>
    Square = 0x0010,

    /// <summary>The Cross button (the cross glyph).</summary>
    Cross = 0x0020,

    /// <summary>The Circle button (the circle glyph).</summary>
    Circle = 0x0040,

    /// <summary>The Triangle button (the triangle glyph).</summary>
    Triangle = 0x0080,

    /// <summary>Right shoulder button 1.</summary>
    R1 = 0x0100,

    /// <summary>Left shoulder button 1.</summary>
    L1 = 0x0200,

    /// <summary>Right shoulder button 2.</summary>
    R2 = 0x0400,

    /// <summary>Left shoulder button 2.</summary>
    L2 = 0x0800,

    /// <summary>The Select button (high bit of the digital-pad response word).</summary>
    Select = 0x1000,

    /// <summary>The Start button (highest bit of the digital-pad response word).</summary>
    Start = 0x8000,
}
