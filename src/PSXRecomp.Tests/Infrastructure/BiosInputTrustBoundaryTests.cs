using System.Buffers.Binary;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (owner ruling, the input-kind trust boundary): the kind of an AOT input is derived from what the parser produced (the
/// ROM reader's bytes, the PS-X file reader's verified evidence), the actual load address and the actual words. A user-written
/// directive keyword selects a reader; it does not decide the class. Each regression below is independent and fails when its rule
/// is removed (checked by mutation; see the PR body).
/// </summary>
[Test]
public sealed class BiosInputTrustBoundaryTests
{
#pragma warning disable AARC003 // Test-layer I/O: the manifest is written to a temporary directory and read back by the production reader
    private const uint RamTextStart = 0x80010000u;
    private const string Evidence = "synthetic-evidence";

    /// <summary>1. A normal disc executable (the production reader, a disc-exes-style spec with its evidence) passes.</summary>
    [Fact]
    public void Regression1_NormalDiscExe_Passes()
    {
        var bios = Bios();
        var words = OrdinaryWords(16);
        var image = new LoadedImageSpec("disc-normal", RamTextStart, words, [RamTextStart], "disc-exe") { SourceSha256 = Evidence };

        BiosAddressGuard.DeriveInputClass(image, bios).Should().Be(BiosInputClass.Executable);
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([image], new HashSet<uint>()), bios))).Should().NotThrow();
    }

    /// <summary>2. A normal exe-file input, read by the production PS-X reader from a file, passes.</summary>
    [Fact]
    public void Regression2_NormalExeFile_ReadByTheProductionReader_Passes()
    {
        var bios = Bios();
        using var temp = new TempDirectory();
        temp.WriteFile("game.exe", SyntheticPsxExeBuilder.Build(RamTextStart, RamTextStart, 64, WordBytes(OrdinaryWords(16))));
        var images = temp.WriteFile("images.txt", System.Text.Encoding.ASCII.GetBytes("exe game game.exe\n"));

        var manifest = LoadImageManifest.Read(images, bios, null);

        manifest.Images.Should().ContainSingle().Which.Kind.Should().Be("exe-file");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios))).Should().NotThrow();
    }

    /// <summary>3. A BIOS ROM image (the ROM reader's product: BIOS bytes placed in RAM) is refused.</summary>
    [Fact]
    public void Regression3_BiosRomImage_IsRefused()
    {
        var bios = Bios();
        using var temp = new TempDirectory();
        var images = temp.WriteFile("images.txt", System.Text.Encoding.ASCII.GetBytes("image bios 0x00000500 rom 0xBFC00000 0x40\n"));
        var manifest = LoadImageManifest.Read(images, bios, null);

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*structural kind 'rom-image'*");
    }

    /// <summary>
    /// 4. A ROM copy relabelled as a disc executable is still refused, by its actual load address (the BIOS range) and by its actual
    /// content (the complete image at a RAM address). The label does not decide.
    /// </summary>
    [Fact]
    public void Regression4_RomCopyRelabelledAsDiscExe_IsRefusedByAddressAndContent()
    {
        var bios = Bios();
        var romWords = bios.Words.Skip(8).Take(16).ToArray();
        var atBiosAddress = new LoadedImageSpec("relabelled", 0xBFC00000u, romWords, [0xBFC00000u], "disc-exe") { SourceSha256 = Evidence };
        var wholeAtRam = new LoadedImageSpec("relabelled-whole", RamTextStart, bios.Words.ToArray(), [RamTextStart], "disc-exe") { SourceSha256 = Evidence };

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([atBiosAddress], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*loads into the BIOS range*");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([wholeAtRam], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*byte-identical to the complete BIOS image*");
    }

    /// <summary>5. A load range that overlaps the BIOS range is refused, from the actual load address.</summary>
    [Fact]
    public void Regression5_LoadRangeOverlappingTheBios_IsRefused()
    {
        var bios = Bios();
        var image = new LoadedImageSpec("in-bios", 0xBFC00000u, OrdinaryWords(16), [0xBFC00000u], "exe-file") { SourceSha256 = Evidence };

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([image], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*loads into the BIOS range*");
    }

    /// <summary>6. The complete BIOS image, every byte equal, is refused (content identity from the actual words).</summary>
    [Fact]
    public void Regression6_WholeBiosImage_IsRefused()
    {
        var bios = Bios();
        var image = new LoadedImageSpec("whole", RamTextStart, bios.Words.ToArray(), [RamTextStart], "exe-file") { SourceSha256 = Evidence };

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([image], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*byte-identical to the complete BIOS image*");
    }

    /// <summary>7. An unknown kind is refused fail-closed, and the message claims no BIOS origin.</summary>
    [Fact]
    public void Regression7_UnknownKind_IsRefusedFailClosed_WithoutAnOriginClaim()
    {
        var bios = Bios();
        var image = new LoadedImageSpec("mystery", RamTextStart, OrdinaryWords(16), [RamTextStart], "mystery-kind") { SourceSha256 = Evidence };

        BiosAddressGuard.DeriveInputClass(image, bios).Should().Be(BiosInputClass.Unknown);
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([image], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*not an allowed AOT source*fail-closed*no BIOS origin is claimed*");
    }

    /// <summary>
    /// 8. Missing or corrupt metadata is refused fail-closed: an executable kind without its verified evidence is Unknown, and a file
    /// that does not parse as a PS-X EXE stops the production reader before any image exists.
    /// </summary>
    [Fact]
    public void Regression8_MissingOrCorruptMetadata_IsRefusedFailClosed()
    {
        var bios = Bios();
        var missingEvidence = new LoadedImageSpec("no-evidence", RamTextStart, OrdinaryWords(16), [RamTextStart], "exe-file");

        BiosAddressGuard.DeriveInputClass(missingEvidence, bios).Should().Be(BiosInputClass.Unknown);
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([missingEvidence], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*fail-closed*");

        using var temp = new TempDirectory();
        temp.WriteFile("corrupt.exe", System.Text.Encoding.ASCII.GetBytes("this is not a PS-X EXE at all, just text"));
        var images = temp.WriteFile("images.txt", System.Text.Encoding.ASCII.GetBytes("exe bad corrupt.exe\n"));
        ((Action)(() => LoadImageManifest.Read(images, bios, null))).Should().Throw<Exception>(
            "a corrupt executable is invalid metadata: the production reader stops, it does not guess a kind");
    }

    /// <summary>The disc-or-file ordinary words used by the normal inputs: a plain MIPS load-immediate sequence, not BIOS data.</summary>
    private static uint[] OrdinaryWords(int count)
    {
        var words = new uint[count];
        for (var i = 0; i < count; i++)
        {
            words[i] = 0x24080000u | (uint)i;
        }

        return words;
    }

    private static byte[] WordBytes(uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }

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
#pragma warning restore AARC003
}
