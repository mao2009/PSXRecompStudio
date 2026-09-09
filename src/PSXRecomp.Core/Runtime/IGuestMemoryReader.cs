using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Generic Runtime boundary for reading guest memory addresses. Not BIOS-specific:
/// any service or diagnostic that needs a byte from guest memory can use it.
/// Try-style by design so an invalid or unmapped address never looks like a
/// successful read of a zero byte.
/// </summary>
[Domain]
public interface IGuestMemoryReader
{
    /// <summary>
    /// Reads a single byte from the given guest address.
    /// </summary>
    /// <param name="address">Guest virtual address to read.</param>
    /// <param name="value">The byte read; set to zero when the read fails.</param>
    /// <returns>True if the address is translatable and mapped; false otherwise.</returns>
    bool TryReadByte(uint address, out byte value);
}