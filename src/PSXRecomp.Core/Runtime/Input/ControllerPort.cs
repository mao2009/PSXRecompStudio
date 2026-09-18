using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// The controller port on a PS1 that a controller (and its physical inputs)
/// is attached to. Hardware supports two controller ports; a Multitap is out
/// of scope (Issue #47).
/// </summary>
[Domain]
public enum ControllerPort
{
    /// <summary>Controller port 1.</summary>
    Port1 = 0,

    /// <summary>Controller port 2.</summary>
    Port2 = 1,
}