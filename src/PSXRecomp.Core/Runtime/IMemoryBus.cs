using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Memory Bus interface.
/// The memory bus routes read/write requests to the appropriate component
/// based on the physical address mapping.
///
/// PS1 Memory Map:
///   0x00000000-0x007FFFFF: RAM (2MB, mirrored to 8MB)
///   0x1F000000-0x1F07FFFF: Expansion Region 1 (ROM/RAM)
///   0x1F800000-0x1F8003FF: Scratchpad (1KB Fast RAM)
///   0x1F801000-0x1F801FFF: I/O Ports (Hardware Registers)
///   0x1FC00000-0x1FC7FFFF: BIOS ROM (512KB)
/// </summary>
[Domain]
public interface IMemoryBus
{
    /// <summary>
    /// Read a 32-bit value from the physical address.
    /// Routes to the appropriate component based on address mapping.
    /// </summary>
    uint Read32(uint address);

    /// <summary>
    /// Write a 32-bit value to the physical address.
    /// Routes to the appropriate component based on address mapping.
    /// </summary>
    void Write32(uint address, uint value);

    /// <summary>
    /// Read a 16-bit value from the physical address.
    /// </summary>
    ushort Read16(uint address);

    /// <summary>
    /// Write a 16-bit value to the physical address.
    /// </summary>
    void Write16(uint address, ushort value);

    /// <summary>
    /// Read an 8-bit value from the physical address.
    /// </summary>
    byte Read8(uint address);

    /// <summary>
    /// Write an 8-bit value to the physical address.
    /// </summary>
    void Write8(uint address, byte value);

    /// <summary>
    /// Get a direct pointer to RAM (for fast access in recompiled code).
    /// Returns IntPtr.Zero if RAM is not directly accessible.
    /// </summary>
    IntPtr GetRamPointer();

    /// <summary>
    /// Get the size of RAM in bytes.
    /// </summary>
    uint GetRamSize();

    /// <summary>
    /// Reset all memory components.
    /// </summary>
    void Reset();
}
