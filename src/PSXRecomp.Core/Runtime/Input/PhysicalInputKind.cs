using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// Broad family a physical input belongs to. Used only to keep identifiers
/// stable and readable across device classes; no single device framework is
/// implied (Issue #47).
/// </summary>
[Domain]
public enum PhysicalInputKind
{
    /// <summary>A physical keyboard key.</summary>
    Keyboard,

    /// <summary>A physical gamepad button or axis.</summary>
    Gamepad,

    /// <summary>A touch-surface action.</summary>
    Touch,
}