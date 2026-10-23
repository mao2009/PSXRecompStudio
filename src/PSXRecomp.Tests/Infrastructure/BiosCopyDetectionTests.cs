using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (owner ruling: cumulative): the AOT inputs of one packaging hold, together, at most <see cref="BiosAddressGuard.CopyThresholdBytes"/>
/// bytes of BIOS code. A BIOS word counts once however many inputs or matches hold it; a match of fewer than
/// <see cref="BiosAddressGuard.MinRunWords"/> words, or of low-entropy words, is not counted; a copy split across inputs or by
/// non-BIOS words is counted across them. Tests named <c>...Limit...</c> fix a documented limit: they assert what the guard does,
/// not what is wished for.
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
    public void ThresholdIsExclusive_24WordsAccepted_25WordsRefused()
    {
        // 96 bytes is 24 words: at the threshold, not over it. 25 words (100 bytes) is over it.
        var bios = Bios();
        var twentyFour = Manifest(("exe-file", Fragment(bios, 1000, 24)));
        var twentyFive = Manifest(("exe-file", Fragment(bios, 1000, 25)));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(twentyFour, bios))).Should().NotThrow("96 bytes is at the threshold, not over it");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(twentyFive, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 100 bytes of BIOS code*");
    }

    [Theory]
    [InlineData("exe-file")]
    [InlineData("boot-exe")]
    [InlineData("disc-exe")]
    public void A252ByteBiosCopy_InADiscOrFileImage_IsRefused(string kind)
    {
        // 252 bytes is 63 whole words, all of them covered: over the 96-byte threshold by a wide margin.
        var bios = Bios();
        var manifest = Manifest((kind, Fragment(bios, 8192, 63)));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 252 bytes of BIOS code*",
                $"a 252-byte BIOS copy in an input of kind {kind} must be refused");
    }

    [Fact]
    public void A124ByteWholeWordCopySplitInTwoImages_IsRefused_ByTheCumulativeCount()
    {
        // A 127-byte copy holds 31 whole words (124 bytes); the 3 trailing bytes are not a word and are not counted.
        // Split in two inputs of 16 and 15 words (64 and 60 bytes): each half alone is under the threshold, together 124 are over it.
        var bios = Bios();
        var first = Fragment(bios, 4096, 16);
        var second = Fragment(bios, 20000, 15);
        var halves = Manifest(("boot-exe", first), ("exe-file", second));

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(Manifest(("exe-file", first)), bios))).Should().NotThrow(
            "one half alone is under the threshold: the rule is cumulative, so a single input is not the test");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(halves, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 124 bytes of BIOS code*");
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
    public void FourPiecesOfEightWords_SplitByNonBiosWords_AreCountedTogether()
    {
        // Four matches of 8 words (the minimum), each separated by a non-BIOS word: 32 words, 128 bytes, over the threshold.
        var bios = Bios();
        var words = new List<uint>();
        for (var k = 0; k < 4; k++)
        {
            words.AddRange(Fragment(bios, 2000 + k * 300, 8));
            words.Add(Separator);
        }

        BiosAddressGuard.BiosCopyBytes([words.ToArray()], bios).Should().Be(128,
            "each 8-word match is a counted match; the separators between them do not stop the count");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(Manifest(("exe-file", words.ToArray())), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*together hold 128 bytes of BIOS code*");
    }

    [Fact]
    public void PiecesShorterThanTheMinimumMatch_AreNotCounted_ThisIsTheLimit()
    {
        // Limit (documented): ten BIOS pieces of seven words, each separated by a non-BIOS word, are 280 bytes of BIOS code in
        // total, and are not counted, because no single match reaches MinRunWords. The guard does not detect this copy.
        var bios = Bios();
        var words = new List<uint>();
        for (var k = 0; k < 10; k++)
        {
            words.AddRange(Fragment(bios, 2000 + k * 300, BiosAddressGuard.MinRunWords - 1));
            words.Add(Separator);
        }

        BiosAddressGuard.BiosCopyBytes([words.ToArray()], bios).Should().Be(0,
            "pieces shorter than MinRunWords are not matches; this limit is recorded in the docs and is not a pass condition");
    }

    [Fact]
    public void ALowEntropyMatch_IsNotEvidence_ButTheSameLengthHighEntropyMatchIs()
    {
        // The BIOS holds a 16-word run of only two values (a padding-like table). Copying it is a coincidence in ordinary code.
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        var state = 0x5EED5EEDUL;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        const int at = 3000 * 4;
        for (var k = 0; k < 16; k++)
        {
            var value = (k % 2 == 0) ? 0xAAAAAAAAu : 0x55555555u;
            BitConverter.GetBytes(value).CopyTo(bytes, at + k * 4);
        }

        var bios = OpenBiosFirmware.FromBytes(bytes);
        var lowEntropy = Manifest(("exe-file", Fragment(bios, 3000, 16)));

        BiosAddressGuard.BiosCopyBytes(lowEntropy.Images.Select(static i => i.Words), bios).Should().Be(0,
            "two distinct values in 16 words are not evidence of a copy");
        BiosAddressGuard.BiosCoveredWords(lowEntropy.Images.Select(static i => i.Words), bios, BiosAddressGuard.MinRunWords, 1)
            .Count.Should().Be(16, "without the distinct-value filter the same match is counted (the filter is what removes it)");
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
