using System.Buffers.Binary;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Reads the subsystem and the imported DLL names of a Windows image (PE32 or PE32+), so a package can check what a native
/// file needs before it ships (Issue #830). Reads only the headers and the import directory; nothing is loaded or run.
/// </summary>
[Infrastructure]
public static class PeImageInfo
{
    /// <summary>The PE subsystem of a Windows GUI program (no console).</summary>
    public const ushort SubsystemWindowsGui = 2;

    /// <summary>The PE subsystem of a Windows console program.</summary>
    public const ushort SubsystemWindowsCui = 3;

    private const int OptionalSubsystemOffset = 68;
    private const int ImportDirectoryIndex = 1;
    private const int ImportDescriptorSize = 20;

    /// <summary>The Windows subsystem the image is linked for.</summary>
    public static ushort Subsystem(byte[] image)
    {
        var optional = OptionalHeaderOffset(image);
        return BinaryPrimitives.ReadUInt16LittleEndian(Slice(image, optional + OptionalSubsystemOffset, 2));
    }

    /// <summary>
    /// Whether an imported library is a Windows library the loader provides (a system DLL or an API-set stub), so a package does not
    /// carry it. Any other name is a library the package must carry.
    /// </summary>
    public static bool IsWindowsLibrary(string name) =>
        name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase)
        || File.Exists(Path.Combine(Environment.SystemDirectory, name));

    /// <summary>The names of the DLLs the image imports directly, in import-directory order.</summary>
    public static IReadOnlyList<string> ImportedDllNames(byte[] image)
    {
        var optional = OptionalHeaderOffset(image);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(Slice(image, optional, 2));
        var dataDirectories = magic switch
        {
            0x10B => optional + 96,   // PE32
            0x20B => optional + 112,  // PE32+
            _ => throw new InvalidDataException($"Unknown optional header magic 0x{magic:X4}."),
        };
        var importRva = BinaryPrimitives.ReadUInt32LittleEndian(Slice(image, dataDirectories + ImportDirectoryIndex * 8, 4));
        var names = new List<string>();
        if (importRva == 0)
        {
            return names;
        }

        var descriptor = RvaToOffset(image, optional, importRva);
        while (true)
        {
            var span = Slice(image, descriptor, ImportDescriptorSize);
            var originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
            var firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            names.Add(ReadAscii(image, RvaToOffset(image, optional, nameRva)));
            descriptor += ImportDescriptorSize;
        }

        return names;
    }

    private static int OptionalHeaderOffset(byte[] image)
    {
        var peOffset = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(Slice(image, 0x3C, 4)));
        if (peOffset > image.Length - 24)
        {
            throw new InvalidDataException("The PE header is outside the file.");
        }

        var signature = Slice(image, peOffset, 4);
        if (signature[0] != (byte)'P' || signature[1] != (byte)'E' || signature[2] != 0 || signature[3] != 0)
        {
            throw new InvalidDataException("The file is not a Windows image (no PE signature).");
        }

        return peOffset + 4 + 20;
    }

    private static int RvaToOffset(byte[] image, int optional, uint rva)
    {
        var coff = optional - 20;
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(Slice(image, coff + 2, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(Slice(image, coff + 16, 2));
        var table = optional + optionalSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var section = Slice(image, table + i * 40, 40);
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(8));
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(12));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(16));
            var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(20));
            var span = Math.Max(virtualSize, rawSize);
            if (rva >= virtualAddress && rva - virtualAddress < span)
            {
                return ToOffset((long)rawPointer + (rva - virtualAddress));
            }
        }

        throw new InvalidDataException($"The RVA 0x{rva:X8} is outside every section.");
    }

    private static string ReadAscii(byte[] image, int offset)
    {
        var end = offset;
        while (end < image.Length && image[end] != 0)
        {
            end++;
        }

        if (end >= image.Length)
        {
            throw new InvalidDataException("An import name is not terminated.");
        }

        return Encoding.ASCII.GetString(image, offset, end - offset);
    }

    /// <summary>A file offset read from the image, refused as invalid data when it does not fit an int (a malformed image).</summary>
    private static int ToOffset(long value) =>
        value < 0 || value > int.MaxValue
            ? throw new InvalidDataException("The image has an offset outside the file.")
            : (int)value;

    private static ReadOnlySpan<byte> Slice(byte[] image, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > image.Length - length)
        {
            throw new InvalidDataException("The image is truncated.");
        }

        return image.AsSpan(offset, length);
    }
}
