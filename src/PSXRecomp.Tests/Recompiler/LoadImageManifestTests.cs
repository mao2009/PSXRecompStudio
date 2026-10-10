using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

// Issue #732: the manifest is the explicit, checked-in list of RAM-placed code images the AOT build compiles. These
// tests pin its grammar and the derived build inputs (words, destination, roots, interpret set); the image compile
// itself is covered by LoadedImageBuildTests and the run by RuntimeCodeGeneratedHostTests.
[Test]
public sealed class LoadImageManifestTests
{
    private const uint RomBase = OpenBiosFirmware.ResetVector;

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    private static OpenBiosFirmware Firmware(params (uint Address, uint Word)[] words)
    {
        var rom = new byte[OpenBiosFirmware.ImageSize];
        foreach (var (address, word) in words)
        {
            BitConverter.GetBytes(word).CopyTo(rom, (int)(address - RomBase));
        }

        return OpenBiosFirmware.FromBytes(rom);
    }

    [Fact]
    public void RomImage_CopiesTheRomWordsToTheDestination_AndRootsAtTheDestination()
    {
        using var dir = new TempDirectory();
        var manifest = LoadImageManifest.Read(
            dir.WriteFile("images.txt", Text("image blk 0x80001000 rom 0xBFC00100 0x08\n")),
            Firmware((0xBFC00100u, 0xDEADBEEFu), (0xBFC00104u, 0xCAFEBABEu)),
            disc: null);

        var image = manifest.Images.Should().ContainSingle().Subject;
        image.Name.Should().Be("blk");
        image.LoadAddress.Should().Be(0x80001000u);
        image.Words.Should().Equal(0xDEADBEEFu, 0xCAFEBABEu);
        image.Roots.Should().Equal(0x80001000u);
        manifest.Interpreted.Should().BeEmpty();
    }

    [Fact]
    public void RomImage_ResolvesItsRootsRelativeToTheManifestDirectory()
    {
        using var dir = new TempDirectory();
        const string rootsName = "manifest-relative-rom.roots";
        dir.WriteFile(rootsName, Text("0x80001004\n0x80001000\n"));
        var manifest = LoadImageManifest.Read(
            dir.WriteFile("images.txt", Text($"image blk 0x80001000 rom 0xBFC00100 0x08 {rootsName}\n")),
            Firmware((0xBFC00100u, 0xDEADBEEFu), (0xBFC00104u, 0xCAFEBABEu)),
            disc: null);

        manifest.Images.Should().ContainSingle().Which.Roots.Should().Equal(0x80001000u, 0x80001004u);
    }

    [Fact]
    public void Comments_BlankLines_AndObservedInterpretedPoints_AreParsed()
    {
        using var dir = new TempDirectory();
        var manifest = LoadImageManifest.Read(
            dir.WriteFile("images.txt", Text("# header\n\ninterpret 0x80000080\ninterpret 0x26A4  # observation\n")),
            Firmware(),
            disc: null);

        manifest.Images.Should().BeEmpty();
        manifest.Interpreted.Should().BeEquivalentTo(new uint[] { 0x80000080u, 0x26A4u });
    }

    [Fact]
    public void ExeImage_UsesTheHeaderTextStart_AndMergesTheEntrypointWithItsRootsFile()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("prog.exe", SyntheticPsxExeBuilder.BuildValid(instructionCount: 4, textStart: 0x80010000u));
        dir.WriteFile("prog.roots", Text("0x80010000\n80010004\n"));

        var manifest = LoadImageManifest.Read(
            dir.WriteFile("images.txt", Text("exe prog prog.exe prog.roots\n")), Firmware(), disc: null);

        var image = manifest.Images.Should().ContainSingle().Subject;
        image.Name.Should().Be("prog");
        image.LoadAddress.Should().Be(0x80010000u);
        image.Words.Should().HaveCount(4);
        image.Roots.Should().Equal(0x80010000u, 0x80010004u);
    }

    [Fact]
    public void BootExe_ResolvesTheSystemCnfExecutable_FromTheDisc()
    {
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile(SyntheticChdBuilder.ExeIsoName, exe)
            .Build();
        using var dir = new TempDirectory();

        var manifest = LoadImageManifest.Read(
            dir.WriteFile("images.txt", Text("boot-exe game\n")), Firmware(), new RawCdSectorSource(SyntheticDiscBuilder.Mode2Form1(iso)));

        var image = manifest.Images.Should().ContainSingle().Subject;
        image.Name.Should().Be("game");
        image.LoadAddress.Should().Be(0x80010000u);
        image.Words.Should().HaveCount(3);
        image.Roots.Should().Equal(0x80010000u);
    }

    [Fact]
    public void AnUnknownDirective_IsRejected()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteFile("images.txt", Text("nonsense 0x1\n"));

        var act = () => LoadImageManifest.Read(path, Firmware(), disc: null);
        act.Should().Throw<InvalidDataException>().WithMessage("*unknown directive*");
    }
}
