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
    public void TheApphostTemplatePath_IsExemptOnlyInTheApphostFile_ExactAndCaseInsensitive()
    {
        var template = DistributionLeakScanner.ApphostTemplatePath;
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", Latin1(template))])
            .Should().BeNull("the exact SDK apphost template path, in its own file, is the one exemption");
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", Latin1(template.ToLowerInvariant()))])
            .Should().BeNull("the match is case-insensitive");
        DistributionLeakScanner.FindAbsolutePath([("runtime/other.exe", Latin1(template))])
            .Should().NotBeNull("the same string in any other file is flagged");
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", Latin1(@"D:\a\_work\1\s\src\other\x\"))])
            .Should().NotBeNull("another path under the same root is not the template");
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", Latin1(@"D:\a\_work\2\s\x\"))])
            .Should().NotBeNull("another CI root is not exempt");
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", Latin1(@"C:\Prog\winlibs_staging_ucrt\bin"))])
            .Should().NotBeNull("a toolchain path is never exempt");
    }

    [Fact]
    public void TheApphostTemplatePath_IsExemptInUtf16Too_OnlyInItsOwnFile()
    {
        var wide = Encoding.Unicode.GetBytes(DistributionLeakScanner.ApphostTemplatePath);
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", wide)]).Should().BeNull();
        DistributionLeakScanner.FindAbsolutePath([("runtime/other.exe", wide)]).Should().NotBeNull();
    }

    [Fact]
    public void ApphostExemption_DoesNotCoverAPrefixOfTheTemplate()
    {
        var truncated = DistributionLeakScanner.ApphostTemplatePath[..^"standalone\\".Length];
        DistributionLeakScanner.FindAbsolutePath([("runtime/PSXRecomp.RuntimeHost.exe", Latin1(truncated))])
            .Should().NotBeNull("the exemption is the full template path, not its root");
    }

    [Fact]
    public void TwelveByteFragment_IsDetected_BelowTheOldSixteenByteWindow()
    {
        var bios = Bios(4096, seed: 4);
        var file = Bios(1024, seed: 5);
        Array.Copy(bios, 0x800, file, 0x100, 12);

        DistributionLeakScanner.FindBiosLeak([("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), [])
            .Should().NotBeNull("a 12-byte BIOS fragment contains 8-byte windows and must fail the check");
    }

    [Fact]
    public void AccidentalMatches_AreCountedAndAbsorbedOnlyByTheBaseline()
    {
        // 64 random bytes have 57 windows of 8 bytes; the baseline holds the first 16 bytes, which are the windows at 0..8.
        var bios = Bios(64, seed: 6);
        var baseline = bios.AsSpan(0, 16).ToArray();
        var file = bios.AsSpan(0, 64).ToArray();

        var report = DistributionLeakScanner.Scan(
            [("game.exe", file)], DistributionLeakScanner.BiosWindows(bios), [("synthetic/game.exe", baseline)]);

        report.BiosWindows.Should().Be(57);
        report.ExcusedByBaseline.Should().Be(9, "the baseline absorbs exactly the windows it holds");
        report.Leaks.Should().Be(48, "every BIOS window the baseline does not hold is a leak in this file");
    }

    [Theory]
    [InlineData("C:\\Prog\\winlibs_staging_ucrt\\bin", 0)]
    [InlineData("C:\\Prog\\winlibs_staging_ucrt\\bin", 1)]
    [InlineData("C:\\Prog\\winlibs_staging_ucrt\\bin", 2)]
    public void Utf16LeToolchainPath_IsFlagged_AtEitherByteAlignment(string path, int prefixBytes)
    {
        var file = new byte[prefixBytes].Concat(Encoding.Unicode.GetBytes(path)).ToArray();

        DistributionLeakScanner.FindAbsolutePath([("game.exe", file)]).Should().NotBeNull("a UTF-16LE path is still a path");
    }

    [Theory]
    [InlineData("/c/Users/Hobart/AppData/Local/Temp")]
    [InlineData("/d/src/PSXRecompStudio/out")]
    public void MsysAbsolutePath_IsFlagged(string path)
    {
        DistributionLeakScanner.FindAbsolutePath([("game.exe", Latin1(path))]).Should().NotBeNull(path);
    }

    [Theory]
    [InlineData("ab/c/")]
    [InlineData("http://example.com/c/d/")]
    [InlineData("x/c/")]
    public void MsysLookalike_IsNotFlagged(string text)
    {
        DistributionLeakScanner.FindAbsolutePath([("game.exe", Latin1(text))]).Should().BeNull(text);
    }
}
