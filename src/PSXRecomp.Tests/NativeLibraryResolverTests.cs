using PSXRecomp.Core;

namespace PSXRecomp.Tests;

[Test]
public class NativeLibraryResolverTests
{
    [Fact]
    public void Windows_CandidateNames_PreferStagedFileName()
    {
        var names = NativeLibraryResolver.CandidateFileNames(NativeLibraryOs.Windows).ToArray();

        names.Should().Equal(
            "PSXRecomp.Native.dll",
            "libPSXRecomp.Native.dll");
    }

    [Fact]
    public void MacOs_CandidateNames_PreferStagedFileName()
    {
        var names = NativeLibraryResolver.CandidateFileNames(NativeLibraryOs.MacOS).ToArray();

        names.Should().Equal(
            "libPSXRecomp.Native.dylib",
            "PSXRecomp.Native.dylib");
    }

    [Fact]
    public void Linux_CandidateNames_PreferStagedFileName()
    {
        var names = NativeLibraryResolver.CandidateFileNames(NativeLibraryOs.Linux).ToArray();

        names.Should().Equal(
            "libPSXRecomp.Native.so",
            "PSXRecomp.Native.so");
    }
}