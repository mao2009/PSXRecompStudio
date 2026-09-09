using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Outcome of reading a NUL-terminated guest string.
/// </summary>
/// <param name="Success">True when a terminator was found within the bound.</param>
/// <param name="Length">Number of bytes before the NUL terminator; zero on failure.</param>
/// <param name="Error">Stable failure reason when <paramref name="Success"/> is false; null on success.</param>
[Domain]
public sealed record GuestStringReadResult(bool Success, int Length, string? Error);

/// <summary>
/// Reads NUL-terminated guest strings through <see cref="IGuestMemoryReader"/>.
/// Generic helper — not named for a specific BIOS service. Reads are always
/// bounded, so no caller can trigger an unbounded scan.
/// </summary>
[Domain]
public static class GuestMemoryStringReader
{
    /// <summary>
    /// Reads a NUL-terminated string starting at <paramref name="address"/>.
    /// </summary>
    /// <param name="reader">The guest-memory read boundary to scan through.</param>
    /// <param name="address">Guest virtual address of the first character.</param>
    /// <param name="maxLength">Hard bound on the number of bytes scanned, including the terminator.</param>
    /// <returns>
    /// A result whose <see cref="GuestStringReadResult.Success"/> is true with
    /// <see cref="GuestStringReadResult.Length"/> set to the number of characters
    /// before the NUL when a terminator is found, or false with a stable error
    /// when the first byte is unreadable ("invalid address") or no NUL is found
    /// within the bound ("unterminated").
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is not positive.</exception>
    public static GuestStringReadResult TryReadCString(IGuestMemoryReader reader, uint address, int maxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "maxLength must be positive.");
        }

        ArgumentNullException.ThrowIfNull(reader);

        for (var length = 0; length < maxLength; length++)
        {
            if (!reader.TryReadByte(address + (uint)length, out var value))
            {
                return new GuestStringReadResult(false, 0, "invalid address");
            }

            if (value == 0)
            {
                return new GuestStringReadResult(true, length, null);
            }
        }

        return new GuestStringReadResult(false, maxLength, "unterminated");
    }
}