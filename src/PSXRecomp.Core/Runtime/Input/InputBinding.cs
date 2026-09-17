using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// A single mapping entry: one <see cref="Input"/> physical input is bound to
/// one logical control of a console controller. The control type
/// <typeparamref name="TButton"/> (typically a flags enum such as the PS1
/// digital-pad buttons) is owned by the console module; this host layer never
/// references a console's button or protocol semantics (Issue #47).
/// </summary>
[Domain]
public readonly record struct InputBinding<TButton>(PhysicalInputId Input, TButton Button)
    where TButton : struct, Enum;