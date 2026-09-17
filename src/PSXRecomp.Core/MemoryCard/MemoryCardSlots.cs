using System;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.MemoryCard;

/// <summary>
/// One of the console's two memory-card slots. Slot identity is a type rather
/// than a loose integer so no caller can pass a slot number the hardware does
/// not have.
/// </summary>
[Domain]
public enum MemoryCardSlot
{
    /// <summary>The first slot.</summary>
    Slot1 = 1,

    /// <summary>The second slot.</summary>
    Slot2 = 2,
}

/// <summary>
/// Which card file, if any, is inserted in each slot.
/// </summary>
/// <remarks>
/// <para>
/// A <see langword="null"/> path means the slot is empty — the state the console
/// is in with no card inserted, which is distinct from a slot holding a blank
/// card. Paths are opaque here: the Domain layer never resolves, canonicalizes,
/// or opens them, so an application-managed default card and a card living in
/// another emulator's directory are the same kind of value.
/// </para>
/// <para>
/// The shared-card and per-game-card strategies both fall out of this model
/// rather than needing their own types: a shared card is one path reused across
/// titles (and may legitimately be the same path in both slots), a per-game card
/// is a path chosen per title. Choosing between them is the caller's policy, not
/// this record's.
/// </para>
/// </remarks>
/// <param name="Slot1Path">The card file in slot 1, or <see langword="null"/> when the slot is empty.</param>
/// <param name="Slot2Path">The card file in slot 2, or <see langword="null"/> when the slot is empty.</param>
[Domain]
public sealed record MemoryCardSlotConfiguration(string? Slot1Path, string? Slot2Path)
{
    /// <summary>Both slots empty.</summary>
    public static MemoryCardSlotConfiguration Empty { get; } = new(null, null);

    /// <summary>The card file in <paramref name="slot"/>, or <see langword="null"/> when it is empty.</summary>
    /// <param name="slot">The slot to read.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a declared slot.</exception>
    public string? this[MemoryCardSlot slot] => slot switch
    {
        MemoryCardSlot.Slot1 => Slot1Path,
        MemoryCardSlot.Slot2 => Slot2Path,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown memory-card slot."),
    };

    /// <summary>Whether <paramref name="slot"/> currently holds a card.</summary>
    /// <param name="slot">The slot to test.</param>
    /// <returns><see langword="true"/> when a card path is configured for the slot.</returns>
    public bool HasCard(MemoryCardSlot slot) => !string.IsNullOrEmpty(this[slot]);

    /// <summary>Returns this configuration with <paramref name="slot"/> holding <paramref name="path"/>.</summary>
    /// <param name="slot">The slot to fill.</param>
    /// <param name="path">The card file to insert.</param>
    /// <returns>A new configuration; this one is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a declared slot.</exception>
    public MemoryCardSlotConfiguration WithCard(MemoryCardSlot slot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return With(slot, path);
    }

    /// <summary>Returns this configuration with <paramref name="slot"/> emptied.</summary>
    /// <param name="slot">The slot to empty.</param>
    /// <returns>A new configuration; this one is unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a declared slot.</exception>
    public MemoryCardSlotConfiguration WithoutCard(MemoryCardSlot slot) => With(slot, null);

    private MemoryCardSlotConfiguration With(MemoryCardSlot slot, string? path) => slot switch
    {
        MemoryCardSlot.Slot1 => this with { Slot1Path = path },
        MemoryCardSlot.Slot2 => this with { Slot2Path = path },
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown memory-card slot."),
    };
}
