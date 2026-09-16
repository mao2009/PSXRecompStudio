using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Parsed PS-X EXE header containing load address, entry point, and text/data layout.
/// </summary>
[Domain]
public sealed record PsxExeHeader
{
    public const ulong Magic = 0x45584520582D5350; // "PS-X EXE" as ulong LE (bytes: 50 53 2D 58 20 45 58 45)
    public const int HeaderSize = 2048;

    public required uint EntryPoint { get; init; }
    public required uint TextStart { get; init; }
    public required uint TextSize { get; init; }
    public required uint DataStart { get; init; }
    public required uint DataSize { get; init; }
    public required uint BssStart { get; init; }
    public required uint BssSize { get; init; }
    public required uint GpInitial { get; init; }
    public required uint SpInitial { get; init; }

    public uint TextEnd => TextStart + TextSize;
    public uint DataEnd => DataStart + DataSize;

    public static PsxExeHeader Parse(byte[] header)
    {
        if (header.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"PS-X EXE header too short: {header.Length} bytes, expected at least {HeaderSize}.");
        }

        // Validate magic (first 8 bytes: "PS-X EXE")
        ulong magic = BitConverter.ToUInt64(header, 0);
        if (magic != Magic)
        {
            throw new InvalidDataException(
                $"Invalid PS-X EXE magic: 0x{magic:X16}, expected 0x{Magic:X16}.");
        }

        // GP/R28 initial value from offset 0x14.
        var gpInitial = BitConverter.ToUInt32(header, 0x14);

        // SP/FP initial value: base (0x30) + offset (0x34).
        // Per PS-X EXE spec (no$psx / psx-spx / ARM9 libpsx): if base is non-zero
        // the BIOS sets SP = base + offset; if base == 0 the value is "None" and
        // SP is left at its reset default.  Fail closed on 32-bit overflow.
        var spBase = BitConverter.ToUInt32(header, 0x30);
        var spOffset = BitConverter.ToUInt32(header, 0x34);
        ulong spInitial = spBase == 0u ? 0u : (ulong)spBase + spOffset;
        if (spInitial > uint.MaxValue)
        {
            throw new InvalidDataException(
                $"PS-X EXE header SP base+offset overflows 32 bits: base=0x{spBase:X8}, offset=0x{spOffset:X8}.");
        }

        return new PsxExeHeader
        {
            EntryPoint = BitConverter.ToUInt32(header, 0x10),
            GpInitial = gpInitial,
            TextStart = BitConverter.ToUInt32(header, 0x18),
            TextSize = BitConverter.ToUInt32(header, 0x1C),
            DataStart = BitConverter.ToUInt32(header, 0x20),
            DataSize = BitConverter.ToUInt32(header, 0x24),
            BssStart = BitConverter.ToUInt32(header, 0x28),
            BssSize = BitConverter.ToUInt32(header, 0x2C),
            SpInitial = (uint)spInitial,
        };
    }
}
