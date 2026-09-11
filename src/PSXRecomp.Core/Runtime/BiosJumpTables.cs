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
/// <para>
/// The function-number <em>domain</em> of both tables — unlike their base
/// addresses — IS primary-source-confirmed (psx-spx, pinned commit
/// ecd6f794f459ab5f72feb88d46df8d23b3c413e0, <c>docs/kernelbios.md</c>): B0
/// documents <c>B(00h..5Dh)</c> real functions, then <c>B(5Eh..FFh) N/A
/// ;jump_to_00000000h</c> — covering the full byte range 0x00-0xFF. The
/// undocumented <c>B(100h....) N/A ;garbage</c> region starts at 0x100,
/// already outside <see cref="byte"/> <c>FunctionNumber</c>'s representable
/// range, so this Runtime cannot reach it and no extra guard is needed. C0
/// documents <c>C(00h..1Dh)</c> real functions, then <c>C(1Eh..7Fh) N/A
/// ;jump_to_00000000h</c>, then <c>C(80h.....) N/A ;mirrors to
/// B(00h.....)</c> — i.e. real hardware documents C-function numbers
/// 0x80-0xFF as reading through the very same jump-list memory as B-function
/// numbers 0x00-0x7F, not as a separate, unbounded C0 region. See
/// <see cref="C0TableAddress"/>'s remarks for what this means for this
/// Runtime's own C0/B0 base-address choice.
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
    /// <para>
    /// Note: the B0 and C0 table addresses chosen here are only 0x200 bytes apart
    /// (<c>0x874 - 0x674 = 0x200</c>). C0 entries at function numbers &gt;= 0x80
    /// therefore arithmetically alias B0 entries starting at 0x00 (e.g.
    /// <c>EntryAddress(C0, 0x80) == EntryAddress(B0, 0x00) == 0x874</c>).
    /// </para>
    /// <para>
    /// This is not an incidental Runtime quirk to be excused as harmless — psx-spx
    /// (pinned commit ecd6f794f459ab5f72feb88d46df8d23b3c413e0,
    /// <c>docs/kernelbios.md</c>) documents this exact relationship as real-hardware
    /// behavior: <c>C(80h.....) N/A ;mirrors to B(00h.....)</c>. On real hardware,
    /// C-function numbers 0x80 and up dispatch through the very same jump-list memory
    /// as B-function numbers 0x00 and up. The 0x200 separation between
    /// <see cref="C0TableAddress"/> and <see cref="B0TableAddress"/> reproduces that
    /// documented aliasing exactly, given the (secondary-source-cited, not
    /// primary-source-confirmed as exact values — see the class remarks) base
    /// addresses this Runtime chose. A future change to either base address must
    /// preserve this 0x200 separation to keep matching the documented mirror.
    /// </para>
    /// <para>
    /// <c>C(1Eh..7Fh) N/A ;jump_to_00000000h</c> is separately documented: real,
    /// present slots that dispatch to guest address 0, not an unbounded or
    /// unconfirmed region. This Runtime's all-zero default for a slot with no
    /// registered service (see <see cref="BiosHleRuntime"/>) already matches that
    /// convention. No additional upper-bound guard is enforced for C0 (unlike A0,
    /// see <see cref="A0MaxFunctionNumber"/>) because primary source documents C0's
    /// full byte domain, 0x00-0xFF (0x00-0x1D real, 0x1E-0x7F jump-to-0, 0x80-0xFF
    /// mirrors B0) — there is no undocumented range left to guess a bound for.
    /// </para>
    /// </remarks>
    public const uint C0TableAddress = 0x00000674;

    /// <summary>
    /// The reserved high half-word of this Runtime's own "this slot still
    /// dispatches to its registered HLE implementation" sentinel value (see
    /// <see cref="HleSentinelTarget"/> and <see cref="BiosHleRuntime"/>'s
    /// constructor). Chosen inside the KSEG2 window (any address above
    /// 0xBFFFFFFF on the MIPS R3000 map): <see cref="Ps1AddressTranslation.TryTranslate"/>
    /// rejects every address in that window, so this sentinel can never collide
    /// with a real guest RAM, BIOS-reserved, or relocated-kernel-code address. Unlike
    /// a low address such as 0x00xxxxxx/0x80xxxxxx/0xA0xxxxxx, it is also never
    /// mistakable for a genuine PS1 code pointer on inspection. This is this
    /// Runtime's own bookkeeping value, not a claim about real hardware.
    /// </summary>
    public const uint HleSentinelHighWord = 0xFFFF0000;

    /// <summary>
    /// Computes the sentinel <see cref="BiosHleRuntime"/> writes into a registered
    /// service's own jump-table slot at construction time, so guest code that reads
    /// (and later saves/patches/restores) that slot's existing entry observes a
    /// real, deterministic, non-zero value instead of 0 (ADR-014's amendment for
    /// #360's Blocker 1 fix). Distinct per <paramref name="family"/>/
    /// <paramref name="functionNumber"/> pair, so two different registered slots
    /// never collide with each other.
    /// </summary>
    public static uint HleSentinelTarget(BiosCallFamily family, byte functionNumber) =>
        HleSentinelHighWord | ((uint)family << 8) | functionNumber;

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
