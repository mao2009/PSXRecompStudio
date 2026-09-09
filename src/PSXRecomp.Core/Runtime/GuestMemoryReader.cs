using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Default <see cref="IGuestMemoryReader"/> implementation. Translates guest
/// addresses through the canonical KUSEG/KSEG rule, rejects unmapped physical
/// addresses, and reads bytes from the injected existing physical-memory path.
/// This adds a bounded read boundary only — it does not build new memory
/// semantics, mirrors, or caching.
/// </summary>
[Domain]
public sealed class GuestMemoryReader : IGuestMemoryReader
{
    private readonly Func<uint, byte> _readPhysicalByte;

    /// <summary>
    /// Creates a reader over the existing physical-memory byte source.
    /// </summary>
    /// <param name="readPhysicalByte">
    /// Reads one physical byte from the existing memory path (for example
    /// <c>memoryBus.Read8</c> or a test RAM window). It receives a translated
    /// physical address shown to be within RAM by <see cref="TryTranslate"/> and
    /// the <see cref="Ps1MemoryMap.RamSize"/> bound.
    /// </param>
    public GuestMemoryReader(Func<uint, byte> readPhysicalByte)
    {
        _readPhysicalByte = readPhysicalByte ?? throw new ArgumentNullException(nameof(readPhysicalByte));
    }

    /// <summary>
    /// Translates a guest virtual address to a physical address using the
    /// canonical KUSEG/KSEG0/KSEG1 rule. Delegates to the shared
    /// <see cref="Ps1AddressTranslation.TryTranslate"/> helper.
    /// </summary>
    /// <param name="address">Guest virtual address.</param>
    /// <param name="physical">Translated physical address when the method returns true.</param>
    /// <returns>True if the address falls in a translatable region.</returns>
    public static bool TryTranslate(uint address, out uint physical)
        => Ps1AddressTranslation.TryTranslate(address, out physical);

    /// <summary>
    /// Reads a single byte from guest memory. Untranslatable addresses and
    /// physical addresses outside RAM are rejected: the value is set to zero and
    /// false is returned, so a stored zero byte can still be read as success.
    /// </summary>
    /// <param name="address">Guest virtual address to read.</param>
    /// <param name="value">The byte read; set to zero when the read fails.</param>
    /// <returns>True if the address is translatable and mapped to RAM; false otherwise.</returns>
    public bool TryReadByte(uint address, out byte value)
    {
        if (!TryTranslate(address, out var physical))
        {
            value = 0;
            return false;
        }

        if (physical >= Ps1MemoryMap.RamSize)
        {
            value = 0;
            return false;
        }

        value = _readPhysicalByte(physical);
        return true;
    }

    /// <summary>
    /// Reads an address range contiguously into <paramref name="buffer"/>.
    /// All-or-nothing: if any byte fails to be read, the buffer is left untouched
    /// and false is returned.
    /// </summary>
    /// <param name="address">Guest virtual address of the first byte.</param>
    /// <param name="buffer">Buffer to fill; its length is the number of bytes to read.</param>
    /// <returns>True if every byte was read; false if any byte failed.</returns>
    public bool TryRead(uint address, Span<byte> buffer)
    {
        var length = (uint)buffer.Length;
        if (length > 0 && address + length < address)
        {
            return false;
        }

        Span<byte> temp = stackalloc byte[buffer.Length];

        for (var i = 0; i < buffer.Length; i++)
        {
            if (!TryReadByte(address + (uint)i, out var value))
            {
                return false;
            }

            temp[i] = value;
        }

        temp.CopyTo(buffer);
        return true;
    }
}