using System.Buffers.Binary;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Builds a PS-X EXE file (header plus text) that runs <see cref="SyntheticLiveProgram"/> (Issue #630). The file is a real EXE
/// in the layout the existing <see cref="Core.DiscImage.PsxExe"/> loader reads, so it can be saved to disk and loaded like a
/// game. It is generated, never checked in, and it contains no title data.
/// </summary>
[Infrastructure]
public static class SyntheticPsxExe
{
    /// <summary>The RAM address the text is loaded to, which is also the initial PC.</summary>
    public const uint LoadAddress = 0x80010000u;

    /// <summary>The initial stack pointer, which the header gives as a base plus a zero offset.</summary>
    private const uint StackBase = 0x801FFF00u;

    /// <summary>Returns the complete EXE file bytes.</summary>
    public static byte[] Build()
    {
        var words = SyntheticLiveProgram.Words(LoadAddress);
        var headerSize = PsxExeHeader.HeaderSize;
        var file = new byte[headerSize + words.Length * sizeof(uint)];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10), LoadAddress); // initial PC
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x18), LoadAddress); // text destination
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x1C), (uint)(words.Length * sizeof(uint))); // text size
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x30), StackBase); // stack base
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(headerSize + i * sizeof(uint)), words[i]);
        }

        return file;
    }
}
