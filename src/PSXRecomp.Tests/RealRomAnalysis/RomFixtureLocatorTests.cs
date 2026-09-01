namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Tests for fixture discovery: both supported <c>rom/</c> layouts, deterministic
/// ordering across multiple fixtures, and the empty result that CI turns into a SKIP.
/// </summary>
[Test]
public class RomFixtureLocatorTests
{
    [Fact]
    public void Discover_MissingRomDirectory_ReturnsNoFixtures()
    {
        using var temp = new TempDirectory();

        var fixtures = RomFixtureLocator.Discover(temp.Combine("does-not-exist"));

        fixtures.Should().BeEmpty("an absent rom/ directory is a SKIP condition, not an error");
    }

    [Fact]
    public void Discover_EmptyRomDirectory_ReturnsNoFixtures()
    {
        using var temp = new TempDirectory();

        RomFixtureLocator.Discover(temp.FullPath).Should().BeEmpty();
    }

    [Fact]
    public void Discover_FlatImageFile_UsesTheFileStemAsFixtureName()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("PERSONA.chd", [1, 2, 3]);

        var fixtures = RomFixtureLocator.Discover(temp.FullPath);

        fixtures.Should().ContainSingle();
        fixtures[0].Name.Should().Be("persona");
        fixtures[0].Format.Should().Be(RomFixtureFormat.Chd);
    }

    [Fact]
    public void Discover_PerTitleDirectory_UsesTheDirectoryNameAsFixtureName()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("some-title", "disc1.chd"), [1, 2, 3]);

        var fixtures = RomFixtureLocator.Discover(temp.FullPath);

        fixtures.Should().ContainSingle();
        fixtures[0].Name.Should().Be("some-title");
        fixtures[0].ImagePath.Should().EndWith("disc1.chd");
    }

    [Fact]
    public void Discover_IsoImage_IsRecognizedAsAnIsoFixture()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("plain.iso", [1, 2, 3]);

        RomFixtureLocator.Discover(temp.FullPath).Single().Format.Should().Be(RomFixtureFormat.Iso);
    }

    [Fact]
    public void Discover_UnsupportedFiles_AreIgnored()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("notes.txt", [1]);
        temp.WriteFile("README.md", [1]);
        temp.CreateSubdirectory("empty-title");

        RomFixtureLocator.Discover(temp.FullPath).Should().BeEmpty();
    }

    [Fact]
    public void Discover_MultipleFixtures_AreAllFoundInDeterministicOrder()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("zeta.chd", [1]);
        temp.WriteFile("alpha.iso", [1]);
        temp.WriteFile(Path.Combine("midgame", "disc.chd"), [1]);

        var first = RomFixtureLocator.Discover(temp.FullPath);
        var second = RomFixtureLocator.Discover(temp.FullPath);

        first.Select(f => f.Name).Should().Equal("alpha", "midgame", "zeta");
        second.Select(f => f.Name).Should().Equal(first.Select(f => f.Name));
    }
}
