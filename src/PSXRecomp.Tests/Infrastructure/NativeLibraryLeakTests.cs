using System.Buffers.Binary;
using FluentAssertions;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review M1): the native shared library is shipped unstripped, so it keeps its symbols; the developer's toolchain and
/// checkout paths are removed by the compile-time prefix maps instead. These checks read a built library named by an environment
/// variable (never committed, never built by the test); they are skipped when the variable is not set.
/// </summary>
[Test]
public sealed class NativeLibraryLeakTests
{
    [SkippableFact]
    public void NativeLibrary_CarriesNoDeveloperPath()
    {
#pragma warning disable AARC003 // Test-layer measurement: the built library is named by the environment
        var path = Environment.GetEnvironmentVariable("PSXRECOMP_NATIVE_DLL");
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(path), "set PSXRECOMP_NATIVE_DLL to a built PSXRecomp.Native.dll");

#pragma warning disable AARC003 // Test-layer measurement of a built library named by the environment
        var bytes = File.ReadAllBytes(path!);
#pragma warning restore AARC003
        DistributionLeakScanner.FindAbsolutePath([("PSXRecomp.Native.dll", bytes)]).Should().BeNull(
            "the build-path prefix maps must remove the developer's toolchain and checkout paths from the shipped library");
    }

    [SkippableFact]
    public void NativeLibrary_KeepsItsSymbolTable()
    {
#pragma warning disable AARC003 // Test-layer measurement: the built library is named by the environment
        var path = Environment.GetEnvironmentVariable("PSXRECOMP_NATIVE_DLL");
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(path), "set PSXRECOMP_NATIVE_DLL to a built PSXRecomp.Native.dll");

        // PE/COFF: the COFF symbol table is written unless the image is stripped (-s). Its count is in the COFF file header.
#pragma warning disable AARC003 // Test-layer measurement of a built library named by the environment
        var bytes = File.ReadAllBytes(path!);
#pragma warning restore AARC003
        var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3C));
        peOffset.Should().BeGreaterThan(0);
        bytes.AsSpan(peOffset, 4).SequenceEqual("PE\0\0"u8).Should().BeTrue("the file is a PE image");
        var numberOfSymbols = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset + 4 + 12));
        numberOfSymbols.Should().BeGreaterThan(0u, "the library is not stripped (no -s), so its symbols are kept");
    }

    [SkippableFact]
    public void NativeLibrary_KeepsTheNamedSymbolsOfItsApi()
    {
#pragma warning disable AARC003 // Test-layer measurement: the built library is named by the environment
        var path = Environment.GetEnvironmentVariable("PSXRECOMP_NATIVE_DLL");
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(path), "set PSXRECOMP_NATIVE_DLL to a built PSXRecomp.Native.dll");

#pragma warning disable AARC003 // Test-layer measurement of a built library named by the environment
        var bytes = File.ReadAllBytes(path!);
#pragma warning restore AARC003
        var names = CoffSymbolNames(bytes);
        names.Should().Contain(n => n.EndsWith("PSXCore_Create", StringComparison.Ordinal),
            "the COFF symbol table names the exported API function (a name lookup works in the debugger and the linker)");
    }

    /// <summary>The names of the COFF symbol table (aux records skipped), long names read from the string table.</summary>
    private static List<string> CoffSymbolNames(byte[] bytes)
    {
        var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3C));
        var symbolTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset + 4 + 8));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset + 4 + 12));
        var stringTable = symbolTable + 18 * count;
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var record = symbolTable + 18 * i;
            var auxiliary = bytes[record + 17];
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(record, 4)) == 0)
            {
                var offset = stringTable + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(record + 4, 4));
                var end = Array.IndexOf(bytes, (byte)0, offset);
                names.Add(System.Text.Encoding.ASCII.GetString(bytes, offset, end - offset));
            }
            else
            {
                var end = Array.IndexOf(bytes, (byte)0, record, 8);
                names.Add(System.Text.Encoding.ASCII.GetString(bytes, record, (end < 0 ? record + 8 : end) - record));
            }

            i += auxiliary;
        }

        return names;
    }
}
