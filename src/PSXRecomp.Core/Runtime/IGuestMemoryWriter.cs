using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Generic Runtime boundary for writing guest memory addresses. Not BIOS-specific:
/// any service or diagnostic that needs to mutate a guest byte can use it. The
/// mirror of <see cref="IGuestMemoryReader"/> — Try-style by design so an invalid
/// or unmapped address never looks like a successful write.
/// </summary>
[Domain]
public interface IGuestMemoryWriter
{
    /// <summary>
    /// Writes a single byte to the given guest address.
    /// </summary>
    /// <param name="address">Guest virtual address to write.</param>
    /// <param name="value">The byte to write.</param>
    /// <returns>True if the address is translatable and mapped; false otherwise.</returns>
    bool TryWriteByte(uint address, byte value);
}
