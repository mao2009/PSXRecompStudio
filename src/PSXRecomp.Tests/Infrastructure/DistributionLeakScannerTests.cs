using System.Text;
using FluentAssertions;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review MEDIUM-1, MEDIUM-2): the leak checks themselves. They run on synthetic inputs in CI, so a check that
/// stops detecting is caught without any BIOS and without any environment variable.
/// </summary>
[Test]
public sealed class DistributionLeakScannerTests
{
    private static byte[] Bios(int length, int seed)
    {
        var bytes = new byte[length];
        var state = (ulong)seed;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    [Fact]
    public void PlantedBiosFragment_IsDetected_WithItsOffsets()
    {
        var bios = Bios(4096, seed: 1);
        var file = Bios(2048, seed: 2);
        Array.Copy(bios, 0x400, file, 0x200, 64);

        var leak = DistributionLeakScanner.FindBiosLeak(
            [("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), []);

        leak.Should().NotBeNull();
        leak.Should().Contain("game.exe").And.Contain("file offset 0x200").And.Contain("BIOS offset 0x400");
    }

    [Fact]
    public void CleanFile_IsNotFlagged()
    {
        var bios = Bios(4096, seed: 1);
        var file = Bios(2048, seed: 2);

        DistributionLeakScanner.FindBiosLeak([("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), [])
            .Should().BeNull();
    }

    [Fact]
    public void TextLikeBiosWindow_IsNotExcused_ByItsLookingLikeText()
    {
        // A digit table, the kind of text every binary carries: zero tolerance means it is still a window of the BIOS.
        var bios = Latin1("0123456789abcdef0123456789abcdef" + new string('x', 4096));
        var file = Latin1("prefix-0123456789abcdef0123456789abcdef-suffix");

        DistributionLeakScanner.FindBiosLeak([("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), [])
            .Should().NotBeNull("text is not evidence of innocence");
    }

    [Fact]
    public void ZeroBiosWindow_IsNotExcused_ByItsBeingPadding()
    {
        var bios = new byte[256];
        Array.Copy(Bios(64, seed: 3), 0, bios, 0, 64);
        var file = new byte[64];

        DistributionLeakScanner.FindBiosLeak([("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), [])
            .Should().NotBeNull("zero padding is not evidence of innocence either");
    }

    [Fact]
    public void BaselineWindow_IsExcused_BecauseNonBiosMaterialHoldsIt()
    {
        var bios = Latin1("0123456789abcdef0123456789abcdef" + new string('x', 4096));
        var file = Latin1("0123456789abcdef0123456789abcdef");
        var baseline = Latin1("a synthetic run's output that also holds the same digit table 0123456789abcdef0123456789abcdef");

        DistributionLeakScanner.FindBiosLeak(
                [("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), [("synthetic/game.exe", baseline)])
            .Should().BeNull("a window the non-BIOS baseline also holds is excused");
    }

    [Theory]
    [InlineData(@"C:\Prog\winlibs_staging_ucrt\home64\gcc-16.1.0\build_mingw\x86_64-w64-mingw32\lib")]
    [InlineData(@"C:\Prog\winlibs_staging_ucrt\bin")]
    [InlineData(@"D:/src/PSXRecompStudio/out/game")]
    [InlineData(@"C:\Users\Hobart\AppData\Local\Temp")]
    [InlineData("/home/builder/psx/out")]
    [InlineData("/mnt/c/Users/build/psx")]
    [InlineData("/Users/builder/psx")]
    [InlineData("/users/builder/psx")]
    public void AbsoluteDeveloperPath_IsFlagged(string path)
    {
        var file = Latin1("\0\0" + path + "\0\0");

        DistributionLeakScanner.FindAbsolutePath([("game.exe", file)]).Should().NotBeNull(path);
    }

    [Theory]
    [InlineData("p://schemas.microsoft.com/SMI/2016/WindowsSettings")]
    [InlineData("https://github.com/mao2009/PSXRecompStudio")]
    [InlineData("ca:/x/y")]
    [InlineData("C:")]
    [InlineData("name=value")]
    public void NonPathText_IsNotFlagged(string text)
    {
        DistributionLeakScanner.FindAbsolutePath([("game.exe", Latin1(text))]).Should().BeNull(text);
    }

    [Fact]
    public void TheSdkApphostTemplatePath_IsTheOnlyExemption_AndOnlyItsOwnRoot()
    {
        DistributionLeakScanner.FindAbsolutePath([("runtime/host.exe", Latin1(@"D:\a\_work\1\s\src\runtime\artifacts\obj\standalone\"))])
            .Should().BeNull("the .NET SDK apphost template's own build root is exempt");
        DistributionLeakScanner.FindAbsolutePath([("runtime/host.exe", Latin1(@"D:\a\_work\2\s\x"))])
            .Should().NotBeNull("another root on the same machine is not the template");
        DistributionLeakScanner.FindAbsolutePath([("runtime/host.exe", Latin1(@"C:\Prog\winlibs_staging_ucrt\bin"))])
            .Should().NotBeNull("the MinGW toolchain path is never exempt");
    }
}
