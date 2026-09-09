using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Canonical PS1 virtual-to-physical address translation (KUSEG/KSEG0/KSEG1).
/// Shared by the Runtime guest-memory reader, the interpreter executor, and
/// the recompiler test memory model so the translation logic is defined once.
/// Mirrors native PSXCpu::TranslateAddress (src/PSXRecomp.Native/src/psx_cpu.cpp).
/// </summary>
[Domain]
public static class Ps1AddressTranslation
{
    /// <summary>
    /// Translates a guest virtual address to a physical address using the
    /// canonical KUSEG/KSEG0/KSEG1 rule:
    /// KUSEG (≤ 0x7FFFFFFF) maps to itself; KSEG0/KSEG1 (≤ 0xBFFFFFFF) mask
    /// off the region bits. Anything else is not translatable.
    /// </summary>
    /// <param name="virtualAddress">Guest virtual address.</param>
    /// <param name="physicalAddress">Translated physical address when the method returns true.</param>
    /// <returns>True if the address falls in a translatable region.</returns>
    public static bool TryTranslate(uint virtualAddress, out uint physicalAddress)
    {
        if (virtualAddress <= 0x7FFFFFFF)
        {
            physicalAddress = virtualAddress;
            return true;
        }

        if (virtualAddress <= 0xBFFFFFFF)
        {
            physicalAddress = virtualAddress & 0x1FFFFFFF;
            return true;
        }

        physicalAddress = 0;
        return false;
    }
}
