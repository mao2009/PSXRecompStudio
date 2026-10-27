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
}
