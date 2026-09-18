using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Exercises the production <see cref="GeneratedHostBuildService"/> (Issue #458)
/// directly, independent of any fixture/differential harness. These fixtures are
/// synthetic C source and always run — no ROM/BIOS dependency.
/// </summary>
[Test]
public sealed class GeneratedHostBuildServiceTests
{
    private const string ValidSource = "int main(void) { return 0; }\n";

    [Fact]
    public void Build_SyntheticFixture_ProducesNativeArtifact()
    {
        using var dir = new TempDirectory();
        var result = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(ValidSource, dir.FullPath, "program"));

        result.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        result.Artifact.Should().NotBeNull();
#pragma warning disable AARC003
        File.Exists(result.Artifact!.BinaryPath).Should().BeTrue();
        File.Exists(result.Artifact.SourcePath).Should().BeTrue();
#pragma warning restore AARC003
    }

    [Fact]
    public void Build_RespectsCallerSelectedOutputDirectory()
    {
        using var dir = new TempDirectory();
        var outputDirectory = dir.CreateSubdirectory("caller-chosen");

        var result = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(ValidSource, outputDirectory, "program"));

        result.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
#pragma warning disable AARC003
        Path.GetDirectoryName(result.Artifact!.BinaryPath).Should().Be(outputDirectory);
#pragma warning restore AARC003
    }

    [Fact]
    public void Build_UnavailableToolchain_ReturnsMachineReadableFailure()
    {
        using var dir = new TempDirectory();
        var result = new GeneratedHostBuildService().Build(new GeneratedHostBuildRequest(
            ValidSource, dir.FullPath, "program", CompilerExecutable: "psxrecomp-no-such-compiler-458"));

        result.Status.Should().Be(GeneratedHostBuildStatus.ToolchainUnavailable);
        result.DiagnosticCode.Should().Be("TOOLCHAIN_UNAVAILABLE");
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void Build_CompilerFailure_IsClassifiedSeparatelyFromLinkerFailure()
    {
        using var dir = new TempDirectory();
        var result = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest("this is not valid C {{{", dir.FullPath, "program"));

        result.Status.Should().Be(GeneratedHostBuildStatus.CompileFailed);
        result.DiagnosticCode.Should().Be("COMPILE_FAILED");
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void Build_LinkerFailure_IsClassifiedSeparatelyFromCompilerFailure()
    {
        const string source =
            "extern int psxrecomp_missing_symbol_458(void);\n" +
            "int main(void) { return psxrecomp_missing_symbol_458(); }\n";

        using var dir = new TempDirectory();
        var result = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(source, dir.FullPath, "program"));

        result.Status.Should().Be(GeneratedHostBuildStatus.LinkFailed);
        result.DiagnosticCode.Should().Be("LINK_FAILED");
        result.Artifact.Should().BeNull();
    }
}
