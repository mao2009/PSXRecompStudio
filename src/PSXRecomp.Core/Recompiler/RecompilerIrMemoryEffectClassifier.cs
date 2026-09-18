using PSXRecomp.Architecture;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Classifies a compile-time-known guest virtual address into the
/// <see cref="RecompilerIrMemoryEffectKind"/> a Load/Store IR operation over
/// that address should carry. Reuses the canonical PS1 address translation
/// (<see cref="Ps1AddressTranslation"/>) and physical memory-region
/// classification (<see cref="Ps1MemoryMap.ClassifyRegion"/>) rather than
/// re-deriving either (Issue #411).
/// </summary>
/// <remarks>
/// Only a compile-time-constant guest address can be classified this way. The
/// vast majority of PS1 load/store effective addresses are base-register
/// relative and therefore not known until the guest register holds a value at
/// execution time, so <see cref="MipsToIrLowerer"/>'s ordinary lowering leaves
/// <see cref="RecompilerIrOperation.MemoryEffect"/> at its
/// <see cref="RecompilerIrMemoryEffectKind.Unknown"/> default rather than call
/// this classifier with a guess. This classifier exists for a caller that does
/// know the address ahead of time — most notably a test building IR directly
/// over a synthetic, statically-known address — and for any future lowering
/// stage that can prove a constant effective address.
/// </remarks>
[Domain]
public static class RecompilerIrMemoryEffectClassifier
{
    /// <summary>
    /// Classifies a guest virtual address. An address outside the translatable
    /// KUSEG/KSEG0/KSEG1 range, or inside a translatable but unmapped region,
    /// classifies as <see cref="RecompilerIrMemoryEffectKind.Unknown"/> —
    /// never guessed to be ordinary RAM.
    /// </summary>
    public static RecompilerIrMemoryEffectKind Classify(uint guestVirtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(guestVirtualAddress, out var physical))
        {
            return RecompilerIrMemoryEffectKind.Unknown;
        }

        return Ps1MemoryMap.ClassifyRegion(physical) switch
        {
            MemoryRegionClass.Ram => RecompilerIrMemoryEffectKind.Ordinary,
            MemoryRegionClass.Bios => RecompilerIrMemoryEffectKind.Ordinary,
            MemoryRegionClass.HardwareRegisters => RecompilerIrMemoryEffectKind.Device,
            _ => RecompilerIrMemoryEffectKind.Unknown,
        };
    }
}
