using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #739 Phase 2 Step C: the manifest's <c>Artifact</c>/<c>Image</c> paths are untrusted input. They must
/// parse only in the exact forms <c>psxrecomp package</c> writes, and resolve (fail closed, before any launch)
/// only to regular files inside the distribution.
/// </summary>
[Test]
public sealed class RuntimeHostManifestPathTests
{
    private static string Manifest(string artifact, string image = RuntimeHostLayout.ImageFileName) =>
        RuntimeHostManifestJson.Serialize(new RuntimeHostManifest(
            RuntimeHostManifest.CurrentSchemaVersion, artifact, GuestFirmware: true, MixedFallback: false, 0, 0,
            0x80010000u, image, [], 0x80010010u, 2, 1000, 0x80010000u));

    [Theory]
    [InlineData("../game")]
    [InlineData("../game.exe")]
    [InlineData("../my game.exe")]
    [InlineData("../game-1_x.bin")]
    public void ValidArtifactPaths_Parse(string artifact) =>
        RuntimeHostManifestJson.Parse(Manifest(artifact)).Artifact.Should().Be(artifact);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("game")]                       // not one level above runtime/
    [InlineData("./game")]
    [InlineData("/usr/bin/game")]              // rooted (POSIX)
    [InlineData("C:/game.exe")]                // drive
    [InlineData("C:game.exe")]                 // drive-relative
    [InlineData("//server/share/game.exe")]    // UNC (forward slashes)
    [InlineData("..\\\\server\\\\share\\\\game.exe")] // UNC (backslashes)
    [InlineData("../../other-executable")]     // traversal out of the distribution
    [InlineData("../../../etc/passwd")]
    [InlineData("../..")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("../.")]
    [InlineData("../..\\game.exe")]            // mixed separators
    [InlineData("..\\game.exe")]               // backslash instead of slash
    [InlineData("../sub/game.exe")]            // nested
    [InlineData("../sub\\game.exe")]
    [InlineData("../C:/game.exe")]
    [InlineData("../game.exe:stream")]         // NTFS alternate data stream
    [InlineData("../NUL")]                     // Windows device name
    [InlineData("../com1.txt")]
    [InlineData("../game.")]                   // trailing dot is dropped by Windows
    [InlineData("../game ")]
    [InlineData("../ga\0me")]
    public void InvalidArtifactPaths_AreRejectedAtParse(string artifact)
    {
        var act = () => RuntimeHostManifestJson.Parse(Manifest(artifact));
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../artifact-image.bin")]
    [InlineData("/abs/artifact-image.bin")]
    [InlineData("C:/artifact-image.bin")]
    [InlineData("//server/share/artifact-image.bin")]
    [InlineData("sub/artifact-image.bin")]
    [InlineData("sub\\artifact-image.bin")]
    [InlineData("other.bin")]
    [InlineData("ARTIFACT-IMAGE.BIN")]
    public void InvalidImagePaths_AreRejectedAtParse(string image)
    {
        var act = () => RuntimeHostManifestJson.Parse(Manifest("../game", image));
        act.Should().Throw<InvalidDataException>();
    }

    /// <summary>A relocatable distribution (here with spaces in its path): root/{game, game.exe, runtime/artifact-image.bin}.</summary>
    private static (string Root, string Runtime) Distribution(TempDirectory dir, string name = "my game dist")
    {
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        var root = dir.CreateSubdirectory(name);
        var runtime = Path.Combine(root, RuntimeHostLayout.RuntimeDirectoryName);
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(root, "game"), "x");
        File.WriteAllText(Path.Combine(root, "game.exe"), "x");
        File.WriteAllText(Path.Combine(runtime, RuntimeHostLayout.ImageFileName), "x");
#pragma warning restore AARC003
        return (root, runtime);
    }

    [Theory]
    [InlineData("../game")]
    [InlineData("../game.exe")]
    public void ResolvesTheArtifactAndImage_InsideADistributionWithSpacesInItsPath(string artifact)
    {
        using var dir = new TempDirectory();
        var (root, runtime) = Distribution(dir);

        RuntimeHostLayout.ResolveArtifactPath(runtime, artifact)
            .Should().Be(Path.Combine(root, Path.GetFileName(artifact)));
        RuntimeHostLayout.ResolveImagePath(runtime, RuntimeHostLayout.ImageFileName)
            .Should().Be(Path.Combine(runtime, RuntimeHostLayout.ImageFileName));
    }

    [Fact]
    public void ResolvesAfterTheDistributionIsMoved()
    {
        using var dir = new TempDirectory();
        var (root, _) = Distribution(dir, "before");
        var moved = Path.Combine(dir.FullPath, "after moved");
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        Directory.Move(root, moved);
#pragma warning restore AARC003

        RuntimeHostLayout.ResolveArtifactPath(Path.Combine(moved, "runtime"), "../game.exe")
            .Should().Be(Path.Combine(moved, "game.exe"));
    }

    [Fact]
    public void MissingFiles_AreRejected()
    {
        using var dir = new TempDirectory();
        var (_, runtime) = Distribution(dir);

        var artifact = () => RuntimeHostLayout.ResolveArtifactPath(runtime, "../absent.exe");
        artifact.Should().Throw<InvalidDataException>().WithMessage("*not found*");
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        File.Delete(Path.Combine(runtime, RuntimeHostLayout.ImageFileName));
#pragma warning restore AARC003
        var image = () => RuntimeHostLayout.ResolveImagePath(runtime, RuntimeHostLayout.ImageFileName);
        image.Should().Throw<InvalidDataException>().WithMessage("*not found*");
    }

    [Theory]
    [InlineData("../../other-executable")]
    [InlineData("/etc/passwd")]
    public void ResolveRejectsEscapes_EvenWhenTheTargetExists(string artifact)
    {
        using var dir = new TempDirectory();
        var (_, runtime) = Distribution(dir);
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        File.WriteAllText(Path.Combine(dir.FullPath, "other-executable"), "x");
#pragma warning restore AARC003

        var act = () => RuntimeHostLayout.ResolveArtifactPath(runtime, artifact);
        act.Should().Throw<InvalidDataException>();
    }

    [SkippableFact]
    public void SymbolicLinkArtifactOrImage_IsRejected()
    {
        using var dir = new TempDirectory();
        var (root, runtime) = Distribution(dir);
        var outside = Path.Combine(dir.FullPath, "outside-target");
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        File.WriteAllText(outside, "x");
        File.Delete(Path.Combine(root, "game"));
        File.Delete(Path.Combine(runtime, RuntimeHostLayout.ImageFileName));
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "game"), outside);
            File.CreateSymbolicLink(Path.Combine(runtime, RuntimeHostLayout.ImageFileName), outside);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip.If(true, $"symbolic links cannot be created in this environment: {e.Message}");
        }
#pragma warning restore AARC003

        var artifact = () => RuntimeHostLayout.ResolveArtifactPath(runtime, "../game");
        artifact.Should().Throw<InvalidDataException>().WithMessage("*symbolic link*");
        var image = () => RuntimeHostLayout.ResolveImagePath(runtime, RuntimeHostLayout.ImageFileName);
        image.Should().Throw<InvalidDataException>().WithMessage("*symbolic link*");
    }

    /// <summary>
    /// Creates a directory link: a junction on Windows (needs no privilege) or a symbolic link elsewhere.
    /// Skips the test visibly when the environment does not allow it.
    /// </summary>
    internal static void CreateDirectoryLinkOrSkip(string link, string target)
    {
#pragma warning disable AARC003 // Test-layer I/O / process use to build link fixtures
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", "mklink", "/J", link, target },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(10_000).Should().BeTrue();
                if (process.ExitCode != 0 || !Directory.Exists(link))
                {
                    throw new IOException("mklink /J failed");
                }
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip.If(true, $"directory links cannot be created in this environment: {e.Message}");
        }
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void LinkedRuntimeDirectory_PointingOutsideTheDistribution_IsRejected_AndOutsideImageIsNotUsed()
    {
        using var dir = new TempDirectory();
        var (root, _) = Distribution(dir);
        var outsideRuntime = dir.CreateSubdirectory("elsewhere");
#pragma warning disable AARC003 // Test-layer I/O for fixture construction
        File.WriteAllText(Path.Combine(outsideRuntime, RuntimeHostLayout.ImageFileName), "outside");
#pragma warning restore AARC003
        var link = Path.Combine(root, "runtime-link");
        CreateDirectoryLinkOrSkip(link, outsideRuntime);

        var image = () => RuntimeHostLayout.ResolveImagePath(link, RuntimeHostLayout.ImageFileName);
        image.Should().Throw<InvalidDataException>().WithMessage("*must not be a symbolic link*");
        var artifact = () => RuntimeHostLayout.ResolveArtifactPath(link, "../game");
        artifact.Should().Throw<InvalidDataException>().WithMessage("*must not be a symbolic link*");
    }

    [SkippableFact]
    public void LinkedRuntimeDirectory_PointingInsideTheDistribution_IsAlsoRejected()
    {
        using var dir = new TempDirectory();
        var (root, runtime) = Distribution(dir);
        var link = Path.Combine(root, "runtime-alias");
        CreateDirectoryLinkOrSkip(link, runtime);

        var image = () => RuntimeHostLayout.ResolveImagePath(link, RuntimeHostLayout.ImageFileName);
        image.Should().Throw<InvalidDataException>().WithMessage("*must not be a symbolic link*");
    }

    [SkippableFact]
    public void LinkedAncestorOfTheDistribution_StillResolves()
    {
        using var dir = new TempDirectory();
        var (root, _) = Distribution(dir);
        var alias = Path.Combine(dir.FullPath, "alias to dist");
        CreateDirectoryLinkOrSkip(alias, root);

        var runtime = Path.Combine(alias, RuntimeHostLayout.RuntimeDirectoryName);
        RuntimeHostLayout.ResolveArtifactPath(runtime, "../game.exe").Should().Be(Path.Combine(alias, "game.exe"));
        RuntimeHostLayout.ResolveImagePath(runtime, RuntimeHostLayout.ImageFileName)
            .Should().Be(Path.Combine(runtime, RuntimeHostLayout.ImageFileName));
    }

    [Fact]
    public void MissingRuntimeDirectory_IsRejected()
    {
        using var dir = new TempDirectory();
        var act = () => RuntimeHostLayout.ResolveImagePath(Path.Combine(dir.FullPath, "absent"), RuntimeHostLayout.ImageFileName);
        act.Should().Throw<InvalidDataException>().WithMessage("*not found*");
    }
}
