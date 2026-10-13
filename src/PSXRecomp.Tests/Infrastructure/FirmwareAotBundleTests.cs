using System.Text;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #732 P1-3: the distribution's AOT inputs must be enumerated, contained and pinned. Packaging resolves
/// every <c>image</c>/<c>exe</c> reference against the AOT directory, rejects absolute/escaping/missing/linked
/// references, copies the files under <c>firmware/</c> preserving their layout and records a SHA-256 each; the run
/// re-verifies every digest before the loaded-code table is rebuilt.
/// </summary>
[Test]
public sealed class FirmwareAotBundleTests
{
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    private static string WriteAot(TempDirectory dir, string images, params (string Name, string Content)[] files)
    {
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text(images));
        foreach (var (name, content) in files)
        {
            dir.WriteFile($"aot/{name}", Text(content));
        }

        return dir.Combine("aot");
    }

    [Fact]
    public void Collect_EnumeratesHashesAndCopiesEveryReferencedInput()
    {
        using var dir = new TempDirectory();
        var aot = WriteAot(
            dir,
            "image kernel 0x80001000 rom 0xBFC00100 0x08 kernel.roots\nexe prog sub/game.exe sub/game.roots\n",
            ("kernel.roots", "0x80001000\n"),
            ("sub/game.exe", "exe-bytes"),
            ("sub/game.roots", "0x80010000\n"));

        var (inputs, files) = FirmwareAotBundle.Collect(aot);

        inputs.Select(static i => i.Path).Should().Equal(
            "firmware/images.txt", "firmware/kernel.roots", "firmware/rom.roots", "firmware/sub/game.exe", "firmware/sub/game.roots");
        foreach (var input in inputs)
        {
            var relative = input.Path["firmware/".Length..];
            input.Sha256.Should().Be(OpenBiosRomInput.Sha256HexOfFile(Path.Combine(aot, relative)), $"the digest of {input.Path}");
        }

        using var runtime = new TempDirectory();
        var firmwareDir = runtime.CreateSubdirectory("firmware");
        FirmwareAotBundle.CopyInto(files, firmwareDir);

        OpenBiosRomInput.Sha256HexOfFile(Path.Combine(firmwareDir, "sub", "game.exe"))
            .Should().Be(OpenBiosRomInput.Sha256HexOfFile(Path.Combine(aot, "sub", "game.exe")), "the subdirectory layout is preserved byte for byte");
        OpenBiosRomInput.Sha256HexOfFile(Path.Combine(firmwareDir, "images.txt"))
            .Should().Be(OpenBiosRomInput.Sha256HexOfFile(Path.Combine(aot, "images.txt")), "the images file is copied byte for byte");
        FirmwareAotBundle.Verify(runtime.FullPath, inputs); // copies match the pinned digests
    }

    [Fact]
    public void Verify_RejectsTamperedMissingAndEscapingInputs()
    {
        using var dir = new TempDirectory();
        var inputs = new List<FirmwareAotInput>
        {
            new("firmware/rom.roots", OpenBiosRomInput.Sha256Hex(Text("0xBFC00000\n"))),
            new("firmware/images.txt", OpenBiosRomInput.Sha256Hex(Text("interpret 0x80000080\n"))),
        };
        dir.WriteFile("firmware/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("firmware/images.txt", Text("interpret 0x80000080\n"));
        FirmwareAotBundle.Verify(dir.FullPath, inputs);

        dir.WriteFile("firmware/rom.roots", Text("tampered\n"));
        ((Action)(() => FirmwareAotBundle.Verify(dir.FullPath, inputs))).Should().Throw<InvalidDataException>().WithMessage("*does not match*");

        dir.WriteFile("firmware/rom.roots", Text("0xBFC00000\n"));
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        File.Delete(dir.Combine("firmware", "images.txt"));
#pragma warning restore AARC003
        ((Action)(() => FirmwareAotBundle.Verify(dir.FullPath, inputs))).Should().Throw<InvalidDataException>().WithMessage("*not found*");

        ((Action)(() => FirmwareAotBundle.Verify(
            dir.FullPath,
            [new FirmwareAotInput("firmware/../outside.bin", OpenBiosRomInput.Sha256Hex([]))])))
            .Should().Throw<InvalidDataException>().WithMessage("*outside*");
    }

    [Theory]
    [InlineData("exe prog /absolute/game.exe\n")]
    [InlineData("exe prog ../escape.exe\n")]
    [InlineData("image kernel 0x80001000 rom 0xBFC00100 0x08 ../escape.roots\n")]
    public void Collect_RejectsAbsoluteAndEscapingReferences(string images)
    {
        using var dir = new TempDirectory();
        var aot = WriteAot(dir, images);
        ((Action)(() => FirmwareAotBundle.Collect(aot))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Collect_RejectsAMissingReference()
    {
        using var dir = new TempDirectory();
        var aot = WriteAot(dir, "exe prog missing.exe\n");
        ((Action)(() => FirmwareAotBundle.Collect(aot))).Should().Throw<FileNotFoundException>().WithMessage("*does not exist*");
    }

    [SkippableFact]
    public void Collect_RejectsALinkedReference()
    {
        using var dir = new TempDirectory();
        var aot = WriteAot(dir, "exe prog linked.exe\n");
        var outside = dir.WriteFile("outside.exe", Text("outside"));
        SymlinkFileOrSkip(Path.Combine(aot, "linked.exe"), outside);

        ((Action)(() => FirmwareAotBundle.Collect(aot))).Should().Throw<InvalidDataException>().WithMessage("*symbolic link*");
    }

    /// <summary>
    /// Issue #732 review P2: the two base files are inputs like any other, so a linked <c>rom.roots</c> or
    /// <c>images.txt</c> must be rejected rather than hashed and copied through its target.
    /// </summary>
    [SkippableFact]
    public void Collect_RejectsALinkedRootsFile()
    {
        using var dir = new TempDirectory();
        var aot = WriteAot(dir, "# none\n");
        var outside = dir.WriteFile("outside.roots", Text("0xBFC00000\n"));
        var roots = Path.Combine(aot, "rom.roots");
#pragma warning disable AARC003 // Test-layer I/O to swap the base file for a symlink
        File.Delete(roots);
#pragma warning restore AARC003
        SymlinkFileOrSkip(roots, outside);

        ((Action)(() => FirmwareAotBundle.Collect(aot))).Should().Throw<InvalidDataException>().WithMessage("*symbolic link*");
    }

    [SkippableFact]
    public void Collect_RejectsALinkedImagesFile()
    {
        using var dir = new TempDirectory();
        var aot = WriteAot(dir, "# none\n");
        var outside = dir.WriteFile("outside.images", Text("# none\n"));
        var images = Path.Combine(aot, "images.txt");
#pragma warning disable AARC003 // Test-layer I/O to swap the base file for a symlink
        File.Delete(images);
#pragma warning restore AARC003
        SymlinkFileOrSkip(images, outside);

        ((Action)(() => FirmwareAotBundle.Collect(aot))).Should().Throw<InvalidDataException>().WithMessage("*symbolic link*");
    }

    [SkippableFact]
    public void Collect_RejectsALinkedAotRoot()
    {
        using var dir = new TempDirectory();
        var realRoot = dir.CreateSubdirectory("real-aot");
        dir.WriteFile("real-aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("real-aot/images.txt", Text("# none\n"));
        var linkedRoot = dir.Combine("aot");
#pragma warning disable AARC003 // Test-layer I/O for a symlink fixture
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, realRoot);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip.If(true, $"symbolic links cannot be created in this environment: {e.Message}");
        }
#pragma warning restore AARC003

        ((Action)(() => FirmwareAotBundle.Collect(linkedRoot))).Should().Throw<InvalidDataException>().WithMessage("*symbolic link*");
    }

    private static void SymlinkFileOrSkip(string linkPath, string target)
    {
#pragma warning disable AARC003 // Test-layer I/O for a symlink fixture
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip.If(true, $"symbolic links cannot be created in this environment: {e.Message}");
        }
#pragma warning restore AARC003
    }

    [Fact]
    public void Manifest_RequiresAotInputsThatAreConfinedHashedAndIncludeTheRootsAndImagesFiles()
    {
        var valid = new FirmwareBootManifest(
            FirmwareBootManifest.CurrentSchemaVersion, "openbios", new string('a', 64), 64,
            "firmware/rom.roots", "firmware/images.txt", "disc/disc.chd", new string('b', 64),
            [new("firmware/rom.roots", new string('c', 64)), new("firmware/images.txt", new string('d', 64))]);
        FirmwareBootManifestJson.Parse(FirmwareBootManifestJson.Serialize(valid)).AotInputs.Should().HaveCount(2);

        ((Action)(() => RoundTrip(valid with { AotInputs = null! }))).Should().Throw<InvalidDataException>().WithMessage("*AOT inputs*");
        ((Action)(() => RoundTrip(valid with { AotInputs = [] }))).Should().Throw<InvalidDataException>().WithMessage("*AOT inputs*");
        ((Action)(() => RoundTrip(valid with { AotInputs = [new("firmware/rom.roots", "short")] }))).Should().Throw<InvalidDataException>().WithMessage("*64 lowercase*");
        ((Action)(() => RoundTrip(valid with { AotInputs = [new("other/rom.roots", new string('c', 64))] }))).Should().Throw<InvalidDataException>().WithMessage("*firmware/*");
        ((Action)(() => RoundTrip(valid with { AotInputs = [new("firmware/../rom.roots", new string('c', 64))] }))).Should().Throw<InvalidDataException>();
        ((Action)(() => RoundTrip(valid with { AotInputs = [new("firmware/rom.roots", new string('c', 64))] }))).Should().Throw<InvalidDataException>().WithMessage("*images*");
        ((Action)(() => RoundTrip(valid with
        {
            AotInputs = [new("firmware/rom.roots", new string('c', 64)), new("firmware/rom.roots", new string('c', 64))],
        }))).Should().Throw<InvalidDataException>().WithMessage("*more than once*");
    }

    private static void RoundTrip(FirmwareBootManifest manifest) => FirmwareBootManifestJson.Parse(FirmwareBootManifestJson.Serialize(manifest));
}