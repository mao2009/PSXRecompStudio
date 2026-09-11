using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Guest addresses of the A0/B0/C0 kernel jump tables and the entry-slot
/// arithmetic shared by every table. Guest RAM is the source of truth for
/// table <em>content</em> (see <see cref="BiosHleRuntime"/>); this type only knows
/// where each table starts and how to compute one entry's address.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="A0TableAddress"/> is a primary-source-confirmed real-hardware
/// fact (psx-spx, pinned commit ecd6f794f459ab5f72feb88d46df8d23b3c413e0: the
/// BIOS memory map states the A0 table at 00000200h, size 300h). Real
/// software has no <c>GetA0Table</c> equivalent to discover this address
/// dynamically, so matching it exactly is a real compatibility requirement.
/// </para>
/// <para>
/// <see cref="B0TableAddress"/> and <see cref="C0TableAddress"/> are NOT
/// primary-source-confirmed real-hardware facts in this repository — only
/// secondary sources place the real BIOS's tables at these values, and this
/// Runtime never loads a real BIOS ROM (ADR-014 base Decision), so there is
/// no real layout to be faithful to. <c>GetB0Table</c>/<c>GetC0Table</c>
/// exist precisely so correct software discovers these addresses at runtime
/// rather than hardcoding them. These two constants are this Runtime's own
/// design choice — chosen to match the commonly-cited secondary-source
/// values purely for plausibility/future real-BIOS-fallback compatibility —
/// not an assertion about real hardware. See ADR-014's amendment for #360.
/// </para>
/// </remarks>
[Domain]
public static class BiosJumpTables
{
    /// <summary>A0 jump-table base address. Primary-source-confirmed (see remarks).</summary>
    public const uint A0TableAddress = 0x00000200;

    /// <summary>
    /// B0 jump-table base address. This Runtime's own design choice, not a
    /// confirmed real-hardware fact (see remarks).
    /// </summary>
    public const uint B0TableAddress = 0x00000874;

    /// <summary>
    /// C0 jump-table base address. This Runtime's own design choice, not a
    /// confirmed real-hardware fact (see remarks).
    /// </summary>
    public const uint C0TableAddress = 0x00000674;

    /// <summary>
    /// Computes the guest address of one function's 4-byte jump-table entry:
    /// <c>table_base + functionNumber * 4</c>, matching the real trampoline's
    /// documented indexing.
    /// </summary>
    public static uint EntryAddress(BiosCallFamily family, byte functionNumber) =>
        TableAddress(family) + (uint)functionNumber * 4u;

    private static uint TableAddress(BiosCallFamily family) => family switch
    {
        BiosCallFamily.A0 => A0TableAddress,
        BiosCallFamily.B0 => B0TableAddress,
        BiosCallFamily.C0 => C0TableAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
