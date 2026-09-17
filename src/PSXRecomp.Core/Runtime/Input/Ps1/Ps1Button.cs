using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input.Ps1;

/// <summary>
/// The digital buttons of a standard PS1 controller, expressed as a bit-flag
/// word so that any combination of simultaneous presses is one type-safe value.
/// </summary>
/// <remarks>
/// Bit positions match the standard digital pad's 16-bit response layout
/// (active-low on the wire; first byte: Select/Start/D-pad; second byte:
/// shoulders/face buttons) so a future SIO adapter can convert the logical
/// flags to serial data without a second constant table. Only the 14
/// digital-pad buttons are modeled; analog, vibration, and pressure
/// sensitivity are explicitly out of the current scope (Issue #47).
/// This type is owned by the PS1 console module; the console-agnostic host
/// input layer never references it.
/// </remarks>
[Flags]
[Domain]
public enum Ps1Button : ushort
{
    /// <summary>No button pressed.</summary>
    None = 0x0000,

    /// <summary>The Select button (response word bit 0).</summary>
    Select = 0x0001,

    /// <summary>The Start button (response word bit 3).</summary>
    Start = 0x0008,

    /// <summary>D-pad up (response word bit 4).</summary>
    Up = 0x0010,

    /// <summary>D-pad right (response word bit 5).</summary>
    Right = 0x0020,

    /// <summary>D-pad down (response word bit 6).</summary>
    Down = 0x0040,

    /// <summary>D-pad left (response word bit 7).</summary>
    Left = 0x0080,

    /// <summary>Left shoulder button 2 (response word bit 8).</summary>
    L2 = 0x0100,

    /// <summary>Right shoulder button 2 (response word bit 9).</summary>
    R2 = 0x0200,

    /// <summary>Left shoulder button 1 (response word bit 10).</summary>
    L1 = 0x0400,

    /// <summary>Right shoulder button 1 (response word bit 11).</summary>
    R1 = 0x0800,

    /// <summary>The Triangle button (the triangle glyph; response word bit 12).</summary>
    Triangle = 0x1000,

    /// <summary>The Circle button (the circle glyph; response word bit 13).</summary>
    Circle = 0x2000,

    /// <summary>The Cross button (the cross glyph; response word bit 14).</summary>
    Cross = 0x4000,

    /// <summary>The Square button (the square glyph; response word bit 15).</summary>
    Square = 0x8000,
}
