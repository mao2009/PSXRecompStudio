using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Base interface for all PS1 hardware components.
/// Hardware components are addressed via memory-mapped I/O (MMIO).
/// </summary>
[Domain]
public interface IHardwareComponent
{
    /// <summary>
    /// Component name for diagnostics and logging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reset the component to its initial state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Read a 32-bit value from the component's register space.
    /// </summary>
    /// <param name="offset">Register offset within the component's address range.</param>
    /// <returns>Register value.</returns>
    uint Read32(uint offset);

    /// <summary>
    /// Write a 32-bit value to the component's register space.
    /// </summary>
    /// <param name="offset">Register offset within the component's address range.</param>
    /// <param name="value">Value to write.</param>
    void Write32(uint offset, uint value);

    /// <summary>
    /// Read a 16-bit value from the component's register space.
    /// </summary>
    /// <param name="offset">Register offset within the component's address range.</param>
    /// <returns>Register value.</returns>
    ushort Read16(uint offset);

    /// <summary>
    /// Write a 16-bit value to the component's register space.
    /// </summary>
    /// <param name="offset">Register offset within the component's address range.</param>
    /// <param name="value">Value to write.</param>
    void Write16(uint offset, ushort value);

    /// <summary>
    /// Read an 8-bit value from the component's register space.
    /// </summary>
    /// <param name="offset">Register offset within the component's address range.</param>
    /// <returns>Register value.</returns>
    byte Read8(uint offset);

    /// <summary>
    /// Write an 8-bit value to the component's register space.
    /// </summary>
    /// <param name="offset">Register offset within the component's address range.</param>
    /// <param name="value">Value to write.</param>
    void Write8(uint offset, byte value);
}
