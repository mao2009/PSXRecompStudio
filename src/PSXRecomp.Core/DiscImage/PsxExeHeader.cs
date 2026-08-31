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
    public required uint SpInitial { get; init; }
    public required uint GpInitial { get; init; }

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

        return new PsxExeHeader
        {
            EntryPoint = BitConverter.ToUInt32(header, 0x10),
            TextStart = BitConverter.ToUInt32(header, 0x18),
            TextSize = BitConverter.ToUInt32(header, 0x1C),
            DataStart = BitConverter.ToUInt32(header, 0x20),
            DataSize = BitConverter.ToUInt32(header, 0x24),
            BssStart = BitConverter.ToUInt32(header, 0x28),
            BssSize = BitConverter.ToUInt32(header, 0x2C),
            SpInitial = BitConverter.ToUInt32(header, 0x30),
            GpInitial = BitConverter.ToUInt32(header, 0x34),
        };
    }
}
