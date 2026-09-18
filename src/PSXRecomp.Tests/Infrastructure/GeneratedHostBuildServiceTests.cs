using System.Diagnostics;
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

    [Fact]
    public void Build_UnusableOutputPath_ReturnsOutputFailed()
    {
        using var dir = new TempDirectory();
        var blockingFile = Path.Combine(dir.FullPath, "not-a-directory");
#pragma warning disable AARC003
        File.WriteAllText(blockingFile, "occupies the directory path");
#pragma warning restore AARC003

        var result = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(ValidSource, blockingFile, "program"));

        result.Status.Should().Be(GeneratedHostBuildStatus.OutputFailed);
        result.DiagnosticCode.Should().Be("OUTPUT_FAILED");
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void Build_SourceWriteBlocked_ReturnsOutputFailed()
    {
        using var dir = new TempDirectory();
#pragma warning disable AARC003
        Directory.CreateDirectory(Path.Combine(dir.FullPath, "program.c"));
#pragma warning restore AARC003

        var result = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(ValidSource, dir.FullPath, "program"));

        result.Status.Should().Be(GeneratedHostBuildStatus.OutputFailed);
        result.DiagnosticCode.Should().Be("OUTPUT_FAILED");
        result.Artifact.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")] // traversal
    [InlineData("child/name")] // directory component, POSIX separator
    [InlineData("child\\name")] // directory component, Windows separator
    [InlineData("/absolute/path")] // rooted on POSIX and Windows
    [InlineData("C:\\absolute\\path")] // rooted on Windows, separator on POSIX
    [InlineData(".")]
    [InlineData("..")]
    public void Build_NonSimpleBinaryName_IsRejected(string? binaryName)
    {
        using var dir = new TempDirectory();
        var request = new GeneratedHostBuildRequest(ValidSource, dir.FullPath, binaryName!);

        var act = () => new GeneratedHostBuildService().Build(request);

        act.Should().Throw<ArgumentException>().WithParameterName("BinaryName");
    }

    [Fact]
    public void RunToolchain_HangingToolchain_TimesOutWithinBound()
    {
        using var dir = new TempDirectory();
        var driver = CompileHangingDriver(dir);
        var stopwatch = Stopwatch.StartNew();

#pragma warning disable AARC003
        var result = GeneratedHostBuildService.RunToolchain(driver, "ignored", timeoutMs: 250, cleanupMs: 500);
#pragma warning restore AARC003
        stopwatch.Stop();

        // The timeout path must stay bounded: the fixed code reads stderr only
        // within the cleanup budget instead of blocking on an open pipe.
        result.Outcome.Should().Be(GeneratedHostBuildService.ToolchainOutcome.TimedOut);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TimeoutCleanup_StderrThatNeverCompletes_ReturnsEmptyWithoutBlocking()
    {
        var neverCompleting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var stderr = GeneratedHostBuildService.ReadStderrWithinCleanupBudget(neverCompleting.Task, cleanupMs: 250);

        stopwatch.Stop();
        stderr.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TimeoutCleanup_StderrThatCompletes_IsReturned()
    {
        var completed = Task.FromResult("some diagnostics");

        GeneratedHostBuildService.ReadStderrWithinCleanupBudget(completed, cleanupMs: 1000)
            .Should().Be("some diagnostics");
    }

    private const string HangDriverSource =
        "#include <stdio.h>\n" +
        "int main(void) { fprintf(stderr, \"hanging driver\\n\"); fflush(stderr);" +
        " volatile unsigned long x = 0; for (;;) { x++; } }\n";

    private static string CompileHangingDriver(TempDirectory dir)
    {
        var sourcePath = Path.Combine(dir.FullPath, "hang-driver.c");
        var binaryPath = Path.Combine(dir.FullPath, "hang-driver");
#pragma warning disable AARC003
        File.WriteAllText(sourcePath, HangDriverSource);
        using var gcc = Process.Start(new ProcessStartInfo("gcc", $"\"{sourcePath}\" -o \"{binaryPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        gcc.WaitForExit(60000).Should().BeTrue("gcc must finish compiling the hang driver");
        gcc.ExitCode.Should().Be(0, "hang driver must compile");
        return OperatingSystem.IsWindows() ? binaryPath + ".exe" : binaryPath;
#pragma warning restore AARC003
    }
}
