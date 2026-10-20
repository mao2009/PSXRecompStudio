using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// A failed runtime host toolchain step keeps its stdout and exit code. dotnet reports MSBuild and NuGet errors on stdout,
/// so a stderr-only failure message left the macOS publish failure of PR #784 without a cause (Issue #805).
/// </summary>
[Test]
public sealed class RuntimeHostToolchainDiagnosticsTests
{
    private const string FailingDriverSource =
        "#include <stdio.h>\n" +
        "int main(void) { puts(\"stdout-marker\"); fputs(\"stderr-marker\\n\", stderr); return 3; }\n";

    [Fact]
    public void FailedStep_KeepsItsStdoutAndExitCode_AlongsideStderr()
    {
        using var dir = new TempDirectory();
        var driver = CompileFailingDriver(dir);

#pragma warning disable AARC003
        var result = RuntimeHostBuildService.RunToolchain(driver, [], timeoutMs: 20000);
#pragma warning restore AARC003

        result.Outcome.Should().Be(RuntimeHostBuildService.ToolchainOutcome.Failed);
        result.ExitCode.Should().Be(3);
        result.Stdout.Should().Contain("stdout-marker", "a failed step's stdout is where dotnet reports its errors");
        result.Stderr.Should().Contain("stderr-marker");
    }

    private static string CompileFailingDriver(TempDirectory dir)
    {
        var sourcePath = Path.Combine(dir.FullPath, "failing-driver.c");
        var binaryPath = Path.Combine(dir.FullPath, "failing-driver");
#pragma warning disable AARC003
        File.WriteAllText(sourcePath, FailingDriverSource);
        using var gcc = Process.Start(new ProcessStartInfo("gcc", $"\"{sourcePath}\" -o \"{binaryPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        gcc.WaitForExit(60000).Should().BeTrue("gcc must finish compiling the driver");
        gcc.ExitCode.Should().Be(0, "the driver must compile");
        return OperatingSystem.IsWindows() ? binaryPath + ".exe" : binaryPath;
#pragma warning restore AARC003
    }
}
