using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

// Issue #773: the manifest's `roots` directive adds observed roots to one EXE image, and it is fail closed. Its source
// identity, load address and code-word SHA-256 must match the image, and each root must be a unique, aligned, in-image,
// structurally safe PC that is not already a root. The existing roots-file syntax is unchanged.
[Test]
public sealed class ObservedRootsManifestTests
{
    private const uint Dest = ObservedRootsFixtures.Dest;
    private const uint Callee = ObservedRootsFixtures.Callee;

    private static LoadImageManifest Load(TempDirectory dir, uint[] text, string manifest, string? observed = null)
    {
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(text));
        if (observed is not null)
        {
            dir.WriteFile("observed.txt", Encoding.ASCII.GetBytes(observed));
        }

        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifest));
        return LoadImageManifest.Read(path, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
    }

    // The manifest line for the test EXE, built from its own bytes so each mismatch case changes exactly one field.
    private static string Directive(uint[] text, string source = null!, string load = null!, string words = null!, string file = "observed.txt", string name = "test")
    {
        var exeSha = ObservedRootsFixtures.Sha256Hex(ObservedRootsFixtures.Exe(text));
        return $"roots {name} {file} {source ?? exeSha} {load ?? $"0x{Dest:X8}"} {words ?? LoadImageManifest.CodeWordsSha256(text)}\n";
    }

    [Fact]
    public void AMatchingDirective_AddsTheObservedRoot_AndKeepsTheEntry()
    {
        using var dir = new TempDirectory();
        var manifest = "exe test test.exe\n" + Directive(ObservedRootsFixtures.Text);

        var image = Load(dir, ObservedRootsFixtures.Text, manifest, observed: $"0x{Callee:X8}  # observed\n").Images.Single();

        image.Roots.Should().Equal(Dest, Callee);
    }

    [Fact]
    public void TheDirective_MayPrecedeItsImage()
    {
        using var dir = new TempDirectory();
        var manifest = Directive(ObservedRootsFixtures.Text) + "exe test test.exe\n";

        var image = Load(dir, ObservedRootsFixtures.Text, manifest, observed: $"0x{Callee:X8}\n").Images.Single();

        image.Roots.Should().Equal(Dest, Callee);
    }

    [Theory]
    [InlineData("source identity", "source")]
    [InlineData("load address", "load")]
    [InlineData("code-word SHA-256", "words")]
    public void AnIdentityMismatch_FailsClosed(string message, string field)
    {
        using var dir = new TempDirectory();
        var directive = field switch
        {
            "source" => Directive(ObservedRootsFixtures.Text, source: new string('0', 64)),
            "load" => Directive(ObservedRootsFixtures.Text, load: "0x80020000"),
            _ => Directive(ObservedRootsFixtures.Text, words: new string('0', 64)),
        };

        var act = () => Load(dir, ObservedRootsFixtures.Text, "exe test test.exe\n" + directive, observed: $"0x{Callee:X8}\n");

        act.Should().Throw<InvalidDataException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void AnUnknownImageName_FailsClosed()
    {
        using var dir = new TempDirectory();
        var manifest = "exe test test.exe\n" + Directive(ObservedRootsFixtures.Text, name: "other");

        var act = () => Load(dir, ObservedRootsFixtures.Text, manifest, observed: $"0x{Callee:X8}\n");

        act.Should().Throw<InvalidDataException>().WithMessage("*exactly one image; found 0*");
    }

    [Theory]
    [InlineData("duplicate", "0x80010010\n0x80010010\n", "*more than once*")]
    [InlineData("out-of-range", "0x80011000\n", "*not an aligned PC inside the image*")]
    [InlineData("unaligned", "0x80010012\n", "*not an aligned PC inside the image*")]
    [InlineData("already-a-root", "0x80010000\n", "*already a root*")]
    public void AnInvalidRoot_FailsClosed(string _, string observed, string message)
    {
        using var dir = new TempDirectory();
        var manifest = "exe test test.exe\n" + Directive(ObservedRootsFixtures.Text);

        var act = () => Load(dir, ObservedRootsFixtures.Text, manifest, observed: observed);

        act.Should().Throw<InvalidDataException>().WithMessage(message);
    }

    [Fact]
    public void AStructurallyUnsafeRoot_FailsClosed()
    {
        // The last word is a branch whose delay slot lies outside the image: discovery from it is a structural error.
        var text = new uint[]
        {
            MipsEncoding.JumpRegister(31), MipsEncoding.Nop, MipsEncoding.Nop, MipsEncoding.Nop,
            MipsEncoding.Nop, MipsEncoding.Branch(0x04, 0, 0, Dest + 20, Dest + 24),
        };
        using var dir = new TempDirectory();
        var manifest = "exe test test.exe\n" + Directive(text);

        var act = () => Load(dir, text, manifest, observed: $"0x{Dest + 20:X8}\n");

        act.Should().Throw<InvalidDataException>().WithMessage("*not structurally safe*");
    }

    [Fact]
    public void TheRootsFile_IsReportedAsAReferencedPath_SoPackagingHashesIt()
    {
        using var dir = new TempDirectory();
        var manifest = "exe test test.exe\n" + Directive(ObservedRootsFixtures.Text);
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(ObservedRootsFixtures.Text));
        dir.WriteFile("observed.txt", Encoding.ASCII.GetBytes($"0x{Callee:X8}\n"));
        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifest));

        LoadImageManifest.ReadReferencedPaths(path).Should().Contain("observed.txt");
    }

    [Fact]
    public void ARootsDirective_WithTheWrongArity_IsRejected()
    {
        using var dir = new TempDirectory();

        var act = () => Load(dir, ObservedRootsFixtures.Text, "exe test test.exe\nroots test observed.txt\n");

        act.Should().Throw<InvalidDataException>().WithMessage("*'roots' is*");
    }
}
