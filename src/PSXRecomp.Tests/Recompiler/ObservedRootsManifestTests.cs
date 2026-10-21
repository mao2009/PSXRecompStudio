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

    // The planner never writes more than the per-image budget, so a longer roots file is refused before any root is validated
    // (each validation walks the image).
    [Fact]
    public void ARootsFileLongerThanThePerImageBudget_FailsClosed()
    {
        using var dir = new TempDirectory();
        var text = new uint[200];
        var observed = string.Join("\n", Enumerable.Range(1, ObservedRootBudget.Default.MaxRootsPerImage + 1).Select(i => $"0x{Dest + (uint)i * 4:X8}")) + "\n";

        var act = () => Load(dir, text, "exe test test.exe\n" + Directive(text), observed);

        act.Should().Throw<InvalidDataException>().WithMessage("*per image*");
    }

    // The code budget applies to the manifest path too, not only to the planner: a hand-written root that makes ~3000 words
    // reachable (limit 1024 extra) is refused. Word 0 is `jr $ra` with its delay slot, so the entry reaches two words; the
    // root at word 2 walks the nops after it to the `jr $ra` at word 2998.
    [Fact]
    public void ARootsDirective_ThatAddsMoreCodeThanTheBudget_FailsClosed()
    {
        using var dir = new TempDirectory();
        var text = new uint[3000];
        text[0] = MipsEncoding.JumpRegister(31);
        text[1] = MipsEncoding.Nop;
        text[2998] = MipsEncoding.JumpRegister(31);
        text[2999] = MipsEncoding.Nop;
        var observed = $"0x{Dest + 8:X8}\n";

        var act = () => Load(dir, text, "exe test test.exe\n" + Directive(text), observed);

        act.Should().Throw<InvalidDataException>().WithMessage("*code words reachable*");
    }

    // The total budget is checked across every observed directive: five images at the per-image limit exceed it.
    [Fact]
    public void ObservedRootsAcrossImages_FailClosedAtTheTotalBudget()
    {
        using var dir = new TempDirectory();
        var text = new uint[200];
        var exe = ObservedRootsFixtures.Exe(text);
        var perImage = ObservedRootBudget.Default.MaxRootsPerImage;
        var roots = string.Join("\n", Enumerable.Range(1, perImage).Select(i => $"0x{Dest + (uint)i * 4:X8}")) + "\n";
        var manifest = new StringBuilder();
        foreach (var name in new[] { "img1", "img2", "img3", "img4", "img5" })
        {
            dir.WriteFile($"{name}.exe", exe);
            dir.WriteFile($"{name}.txt", Encoding.ASCII.GetBytes(roots));
            manifest.Append($"exe {name} {name}.exe\n").Append(Directive(text, file: $"{name}.txt", name: name));
        }

        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifest.ToString()));
        var act = () => LoadImageManifest.Read(path, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);

        act.Should().Throw<InvalidDataException>().WithMessage("*in total*");
    }

    // The per-image budget is checked on one roots file, so a second directive for the same image would raise the image past it.
    [Fact]
    public void ASecondDirectiveForTheSameImage_FailsClosed()
    {
        using var dir = new TempDirectory();
        var text = new uint[200];
        var perImage = ObservedRootBudget.Default.MaxRootsPerImage;
        string Roots(int from) => string.Join("\n", Enumerable.Range(from, perImage).Select(i => $"0x{Dest + (uint)i * 4:X8}")) + "\n";
        dir.WriteFile("observed2.txt", Encoding.ASCII.GetBytes(Roots(1 + perImage)));

        var act = () => Load(dir, text, "exe test test.exe\n" + Directive(text) + Directive(text, file: "observed2.txt"), Roots(1));

        act.Should().Throw<InvalidDataException>().WithMessage("*more than one 'roots'*");
    }

    [Fact]
    public void TheProbeUsage_SeparatesEveryOption()
    {
        PSXRecomp.Infrastructure.Cli.OpenBiosProbeCommand.Usage.Should().NotContain("][", "adjacent options need a space between them");
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
