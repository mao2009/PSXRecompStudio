using System.Collections.ObjectModel;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>Identifies one guest call into the PlayStation BIOS jump tables.</summary>
[Domain]
public sealed record BiosCallIdentity
{
    /// <summary>Creates a BIOS call identity with an optional guest call-site PC.</summary>
    public BiosCallIdentity(
        BiosCallFamily family,
        byte functionNumber,
        uint? guestPc = null,
        IEnumerable<uint>? arguments = null,
        string? name = null)
    {
        if (!Enum.IsDefined(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family));
        }

        Family = family;
        FunctionNumber = functionNumber;
        GuestPc = guestPc;
        Arguments = new ReadOnlyCollection<uint>((arguments ?? Array.Empty<uint>()).ToArray());
        Name = name;
    }

    /// <summary>The BIOS jump-table family (A0, B0, or C0).</summary>
    public BiosCallFamily Family { get; }

    /// <summary>The 8-bit function number selected in the jump table.</summary>
    public byte FunctionNumber { get; }

    /// <summary>The guest PC at the call site, when execution tracking provides it.</summary>
    public uint? GuestPc { get; }

    /// <summary>The ABI argument words in guest register order.</summary>
    public IReadOnlyList<uint> Arguments { get; }

    /// <summary>Optional human-readable service name.</summary>
    public string? Name { get; }

    /// <summary>Stable family/function identity suitable for logs and artifacts.</summary>
    public string StableKey => $"{Family}:{FunctionNumber:X2}";

    /// <inheritdoc />
    public override string ToString() => GuestPc is uint pc
        ? $"{StableKey} at 0x{pc:X8}"
        : StableKey;
}

/// <summary>BIOS jump-table families exposed by the PS1 ABI.</summary>
[Domain]
public enum BiosCallFamily : byte
{
    A0,
    B0,
    C0,
}
