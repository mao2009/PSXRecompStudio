using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

// Integration (#773): the three root sources of one EXE image in one manifest. An explicit root on the exe line, an indirect root
// named by a reachable lui/addiu table base (#792), and an observed root from a roots file (#784) must all be applied, and
// reading the manifest twice must give the same result. Each fixture word is synthetic.
[Test]
public sealed class IntegratedRootSourcesTests
{
    private const uint Dest = ObservedRootsFixtures.Dest;

    internal const uint CalleeA = Dest + 0x10; // word 4: addiu $sp,$sp,-16; jr $ra; nop
    internal const uint CalleeB = Dest + 0x1C; // word 7
    internal const uint CalleeC = Dest + 0x34; // word 13: observed (roots file)
    internal const uint CalleeE = Dest + 0x40; // word 16: explicit (exe line)

    // Word 0-1 materialize the table base Dest+0x28 into $v0, so the table at word 10 is a candidate source (#792). Word 12 ends it.
    internal static readonly uint[] Text =
    [
        MipsEncoding.I(0x0F, 2, 0, 0x8001), MipsEncoding.I(0x09, 2, 2, 0x0028), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        CalleeA, CalleeB, 0,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
    ];

    private static LoadImageManifest Load(TempDirectory dir)
    {
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(Text));
        dir.WriteFile("observed.txt", Encoding.ASCII.GetBytes($"0x{CalleeC:X8}  # observed\n"));
        dir.WriteFile("explicit.txt", Encoding.ASCII.GetBytes($"0x{CalleeE:X8}\n"));
        var exeSha = ObservedRootsFixtures.Sha256Hex(ObservedRootsFixtures.Exe(Text));
        var manifest = "exe test test.exe explicit.txt\n" +
            "indirect-roots\n" +
            $"roots test observed.txt {exeSha} 0x{Dest:X8} {LoadImageManifest.CodeWordsSha256(Text)}\n";
        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifest));
        return LoadImageManifest.Read(path, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
    }

    [Fact]
    public void ExplicitIndirectAndObservedRoots_AreAllAppliedInOneManifest()
    {
        using var dir = new TempDirectory();

        var image = Load(dir).Images.Single();

        image.Roots.Should().Equal(Dest, CalleeA, CalleeB, CalleeC, CalleeE);
        image.SourceSha256.Should().NotBeNullOrEmpty("the observed-roots directive needs the source identity");
    }

    // Application order (#773): indirect roots are applied before the observed roots are checked, so a root that the indirect
    // discovery already added is "already a root" to the observed directive and is refused. The reverse order would accept it
    // and then silently merge the two.
    [Fact]
    public void AnObservedRoot_ThatIndirectDiscoveryAlreadyAdded_IsRefused()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(Text));
        dir.WriteFile("observed.txt", Encoding.ASCII.GetBytes($"0x{CalleeA:X8}\n"));
        var exeSha = ObservedRootsFixtures.Sha256Hex(ObservedRootsFixtures.Exe(Text));
        var manifest = "exe test test.exe\nindirect-roots\n" +
            $"roots test observed.txt {exeSha} 0x{Dest:X8} {LoadImageManifest.CodeWordsSha256(Text)}\n";
        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifest));

        var act = () => LoadImageManifest.Read(path, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);

        act.Should().Throw<InvalidDataException>().WithMessage("*already a root*");
    }

    [Fact]
    public void ReadingTheSameManifestTwice_GivesTheSameRoots()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        var a = Load(first).Images.Single();
        var b = Load(second).Images.Single();

        b.Roots.Should().Equal(a.Roots);
    }

    // The registered (guarded) version of an observed root runs only while RAM holds the file's words. A RAM word that differs
    // (the Persona 0x80030C44 shape: file words and the executed words disagree) leaves the PC to the interpreter; adding the root
    // does not make the build run code the file does not hold.
    [Fact]
    public void TheGuardedBlockOfAnObservedRoot_MatchesOnlyWhileRamHoldsTheFileWords()
    {
        using var dir = new TempDirectory();
        var image = Load(dir).Images.Single();
        var table = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(image.LoadAddress, image.Words, image.Roots, new HashSet<uint>())]);
        uint Ram(uint address, uint patchAt, uint patch) => address == patchAt ? patch : image.Words[(int)((address - Dest) / 4)];

        table.Match(CalleeC, a => Ram(a, 0, 0)).Should().NotBeNull("RAM holds the file words");
        table.Match(CalleeC, a => Ram(a, CalleeC, MipsEncoding.Nop)).Should().BeNull("RAM holds other code at the root");
    }

    [Fact]
    public void WithoutAnyRootsDirective_TheRootsAreExactlyTheEntryPoint()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(Text));
        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes("exe test test.exe\n"));

        LoadImageManifest.Read(path, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null)
            .Images.Single().Roots.Should().Equal(Dest);
    }
}
