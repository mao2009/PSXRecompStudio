using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Represents a loaded PS-X EXE with its header and text segment data.
/// </summary>
[Domain]
public sealed record PsxExe
{
    public required PsxExeHeader Header { get; init; }
    public required string FileName { get; init; }
    public required byte[] TextSegment { get; init; }
    public required uint FileSize { get; init; }

    public int DecodedInstructionCount => TextSegment.Length / 4;

    /// <summary>
    /// Gets the 32-bit encoded word at the given address within the text segment.
    /// </summary>
    public uint GetInstructionWord(uint address)
    {
        var offset = (int)(address - Header.TextStart);
        if (offset < 0 || offset + 4 > TextSegment.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                $"Address 0x{address:X8} is outside the text segment [0x{Header.TextStart:X8}..0x{Header.TextEnd:X8}).");
        }

        return BitConverter.ToUInt32(TextSegment, offset);
    }

    public static PsxExe Load(byte[] fileContent, string fileName)
    {
        var header = PsxExeHeader.Parse(fileContent);

        var textOffset = PsxExeHeader.HeaderSize;
        var textLength = (int)header.TextSize;
        if (textLength == 0)
        {
            textLength = fileContent.Length - textOffset;
        }

        var textSegment = new byte[textLength];
        Buffer.BlockCopy(fileContent, textOffset, textSegment, 0,
            Math.Min(textLength, fileContent.Length - textOffset));

        return new PsxExe
        {
            Header = header,
            FileName = fileName,
            TextSegment = textSegment,
            FileSize = (uint)fileContent.Length,
        };
    }
}
