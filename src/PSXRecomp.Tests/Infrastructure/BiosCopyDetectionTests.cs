using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (owner ruling: cumulative): the AOT inputs of one packaging hold, together, at most <see cref="BiosAddressGuard.CopyThresholdBytes"/>
/// bytes of BIOS code. A BIOS word counts once however many inputs or runs hold it; a run of fewer than <see cref="BiosAddressGuard.MinRunWords"/>
/// words is a coincidence and is not counted; a copy split across inputs is counted across them.
/// </summary>
[Test]
public sealed class BiosCopyDetectionTests
{
    /// <summary>A deterministic 512 KiB image with no structure: not OpenBIOS, not a registered stub.</summary>
    private static OpenBiosFirmware Bios()
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        var state = 0x5EED5EEDUL;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        return OpenBiosFirmware.FromBytes(bytes);
    }

    /// <summary>The BIOS words from <paramref name="offset"/>, <paramref name="count"/> of them.</summary>
    private static uint[] Fragment(OpenBiosFirmware bios, int offset, int count) => bios.Words.Skip(offset).Take(count).ToArray();

    /// <summary>A word the BIOS does not hold at any offset of these fragments (a non-BIOS separator).</summary>
    private const uint Separator = 0xC0FFEE00u;

    private static LoadImageManifest Manifest(params (string Kind, uint[] Words)[] images) => new(
        images.Select((image, index) => new LoadedImageSpec($"input{index}", 0x00000500u + (uint)index * 0x1000u, image.Words, [], image.Kind)).ToArray(),
        new HashSet<uint>());

    [Fact]
    public void ThresholdIsExclusive_56WordsAccepted_57WordsRefused()
    {
        var bios = Bios();
        var thirtyTwo = Manifest(("exe-file", Fragment(bios, 1000, 56)));
        var thirtyThree = Manifest(("exe-file", Fragment(bios, 1000, 57)));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(thirtyTwo, bios))).Should().NotThrow("224 bytes is at the threshold, not over it");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(thirtyThree, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 228 bytes of BIOS code*");
    }

    [Theory]
    [InlineData("exe-file")]
    [InlineData("boot-exe")]
    [InlineData("disc-exe")]
    public void A254ByteBiosCopy_InADiscOrFileImage_IsRefused(string kind)
    {
        // 254 bytes hold 63 whole BIOS words (252 bytes covered): over the 128-byte threshold, by a wide margin.
        var bios = Bios();
        var manifest = Manifest((kind, Fragment(bios, 8192, 63)));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 252 bytes of BIOS code*",
                $"a 254-byte BIOS copy in an input of kind {kind} must be refused");
    }

    [Fact]
    public void A127ByteCopySplitInTwoImages_IsRefused_ByTheCumulativeCount()
    {
        // Each half is 31 whole words (124 bytes): alone, each is under the threshold; together, 248 bytes are over it.
        var bios = Bios();
        var first = Fragment(bios, 4096, 31);
        var second = Fragment(bios, 20000, 31);
        var halves = Manifest(("boot-exe", first), ("exe-file", second));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(Manifest(("exe-file", first)), bios))).Should().NotThrow(
            "one half alone is under the threshold: the rule is cumulative, so a single input is not the test");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(halves, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 248 bytes of BIOS code*");
    }

    [Fact]
    public void AWordCountedOnce_WhenTwoInputsHoldTheSameCopy()
    {
        // 20 words in both inputs: 80 bytes once (a sum would be 160, over the threshold). The union stays under it.
        var bios = Bios();
        var shared = Fragment(bios, 12000, 20);
        var manifest = Manifest(("exe-file", shared), ("exe-file", shared));

        BiosAddressGuard.BiosCopyBytes(manifest.Images.Select(static i => i.Words), bios).Should().Be(80,
            "a BIOS word held by two inputs is counted once");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios))).Should().NotThrow();
    }

    [Fact]
    public void SingleWordCoincidences_AreNotCounted()
    {
        // Ten BIOS runs of three words each, each separated by a non-BIOS word: every run is shorter than MinRunWords.
        var bios = Bios();
        var words = new List<uint>();
        for (var k = 0; k < 10; k++)
        {
            words.AddRange(Fragment(bios, 2000 + k * 50, 3));
            words.Add(Separator);
        }

        BiosAddressGuard.BiosCopyBytes([words.ToArray()], bios).Should().Be(0,
            "a run of 3 words is a coincidence: it is not evidence of a copy, however many there are");
    }

    [Fact]
    public void ARamCopyOfBiosCode_InsideADiscImage_IsRefusedByTheSameCount()
    {
        // A disc program that copies a BIOS routine into RAM: the RAM address does not matter, the content does.
        var bios = Bios();
        var words = Enumerable.Range(0, 16).Select(static i => 0x24080000u | (uint)i).Concat(Fragment(bios, 20000, 60)).ToArray();
        var manifest = new LoadImageManifest([new LoadedImageSpec("copy-in-ram", 0x80100000u, words, [], "disc-exe")], new HashSet<uint>());

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 240 bytes of BIOS code*");
    }

    [Fact]
    public void AWholeBiosHeldAsInputData_IsRefused_RegardlessOfTheOtherInputs()
    {
        // Data the BIOS holds in full is refused by its size, not by a judgement of its content.
        var bios = Bios();
        var whole = Manifest(("exe-file", bios.Words.ToArray()));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(whole, bios))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TheRomKindRefusal_IsItsOwnPath_EvenForAShortCopy()
    {
        var bios = Bios();
        var manifest = Manifest(("rom-image", Fragment(bios, 0, 4)));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*copies BIOS bytes into RAM*",
                "the rom kind is refused by name, independently of the cumulative count");
    }

    [Fact]
    public void AnUnknownSourceKind_IsRefused()
    {
        var bios = Bios();
        var manifest = Manifest(("unknown", Fragment(bios, 0, 2)));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*not an allowed AOT source*");
    }
}
