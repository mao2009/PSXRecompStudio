using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Default <see cref="IGuestMemoryWriter"/> implementation. Translates guest
/// addresses through the canonical KUSEG/KSEG rule, rejects unmapped physical
/// addresses, and writes bytes through the injected existing physical-memory
/// path. This adds a bounded write boundary only — it does not build new memory
/// semantics, mirrors, or caching. The write-side mirror of
/// <see cref="GuestMemoryReader"/>.
/// </summary>
[Domain]
public sealed class GuestMemoryWriter : IGuestMemoryWriter
{
    private readonly Action<uint, byte> _writePhysicalByte;

    /// <summary>
    /// Creates a writer over the existing physical-memory byte sink.
    /// </summary>
    /// <param name="writePhysicalByte">
    /// Writes one physical byte through the existing memory path (for example
    /// <c>memoryBus.Write8</c> or a test RAM window). It receives a translated
    /// physical address shown to be within RAM by
    /// <see cref="Ps1AddressTranslation.TryTranslate"/> and the
    /// <see cref="Ps1MemoryMap.RamSize"/> bound.
    /// </param>
    public GuestMemoryWriter(Action<uint, byte> writePhysicalByte)
    {
        _writePhysicalByte = writePhysicalByte ?? throw new ArgumentNullException(nameof(writePhysicalByte));
    }

    /// <summary>
    /// Writes a single byte to guest memory. Untranslatable addresses and
    /// physical addresses outside RAM are rejected: nothing is written and
    /// false is returned.
    /// </summary>
    /// <param name="address">Guest virtual address to write.</param>
    /// <param name="value">The byte to write.</param>
    /// <returns>True if the address is translatable and mapped to RAM; false otherwise.</returns>
    public bool TryWriteByte(uint address, byte value)
    {
        if (!Ps1AddressTranslation.TryTranslate(address, out var physical))
        {
            return false;
        }

        if (physical >= Ps1MemoryMap.RamSize)
        {
            return false;
        }

        _writePhysicalByte(physical, value);
        return true;
    }
}
