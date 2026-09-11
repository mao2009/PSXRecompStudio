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
    /// Highest valid function number for the A0 table (inclusive). Primary-source-confirmed:
    /// psx-spx (pinned commit ecd6f794f459ab5f72feb88d46df8d23b3c413e0) states the A0 table
    /// size as 0x300 bytes = 192 entries × 4 bytes, so valid function numbers are
    /// <c>0x00</c>–<c>0xBF</c>. Function numbers <c>&gt;= 0xC0</c> are out of the A0 table;
    /// their computed addresses fall into the "relocated kernel code" region of the BIOS
    /// memory map and must not be treated as jump-table slots.
    /// <see cref="BiosHleRuntime.Invoke"/> skips the patch-check for such numbers.
    /// </summary>
    public const byte A0MaxFunctionNumber = 0xBF;

    /// <summary>
    /// B0 jump-table base address. This Runtime's own design choice, not a
    /// confirmed real-hardware fact (see remarks).
    /// </summary>
    public const uint B0TableAddress = 0x00000874;

    /// <summary>
    /// C0 jump-table base address. This Runtime's own design choice, not a
    /// confirmed real-hardware fact (see remarks).
    /// </summary>
    /// <remarks>
    /// Note: the B0 and C0 table addresses chosen here are only 0x200 bytes apart
    /// (<c>0x874 - 0x674 = 0x200</c>). C0 entries at function numbers &gt;= 0x80 would
    /// arithmetically alias B0 entries starting at 0x00 (e.g.
    /// <c>EntryAddress(C0, 0x80) == EntryAddress(B0, 0x00) == 0x874</c>). This is
    /// expected and harmless: C0's real documented range never reaches 0x80, and no
    /// confirmed upper bound for B0 or C0 exists in this repository's primary sources —
    /// enforcing one would require guessing, which ADR-014 forbids. This is a
    /// deliberate, documented limitation; see ADR-014's amendment for #360.
    /// </remarks>
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
