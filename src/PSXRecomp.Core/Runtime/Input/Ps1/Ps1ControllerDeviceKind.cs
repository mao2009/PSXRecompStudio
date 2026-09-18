using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input.Ps1;

/// <summary>
/// The kind of PS1 controller device attached to a controller port. The standard
/// digital pad is the only kind this contract currently models and supports;
/// every other kind is recognized and labeled so an attached-but-unimplemented
/// device is represented explicitly rather than silently treated as a digital
/// pad. Future devices extend this enum; their protocol and behavior are out of
/// scope of the input contract (Issue #47). This type is owned by the PS1
/// console module.
/// </summary>
[Domain]
public enum Ps1ControllerDeviceKind
{
    /// <summary>No controller is attached to the port.</summary>
    NotPresent = 0,

    /// <summary>A standard digital pad (SCPH-1010). The only supported kind.</summary>
    StandardDigitalPad = 1,

    /// <summary>A Sony DualShock (analog sticks + vibration). Recognized, not yet supported.</summary>
    DualShock = 2,

    /// <summary>A Sony Analog Controller (analog sticks, no vibration). Recognized, not yet supported.</summary>
    AnalogController = 3,

    /// <summary>A Namco NeGcon twist controller. Recognized, not yet supported.</summary>
    NeGcon = 4,

    /// <summary>A PS1 mouse (SCPH-1090). Recognized, not yet supported.</summary>
    Mouse = 5,

    /// <summary>A light gun (e.g. GunCon). Recognized, not yet supported.</summary>
    GunCon = 6,

    /// <summary>Any other recognized-but-unimplemented dedicated controller.</summary>
    Unsupported = 7,
}
