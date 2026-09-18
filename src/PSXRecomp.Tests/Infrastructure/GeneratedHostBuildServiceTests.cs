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
        var result = GeneratedHostBuildService.RunToolchain(driver, ["ignored"], timeoutMs: 250, cleanupMs: 500);
#pragma warning restore AARC003
        stopwatch.Stop();

        // The timeout path must stay bounded: the fixed code reads stderr only
        // within the cleanup budget instead of blocking on an open pipe.
        result.Outcome.Should().Be(GeneratedHostBuildService.ToolchainOutcome.TimedOut);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CleanupBudget_StreamThatNeverCompletes_ReturnsEmptyWithoutBlocking()
    {
        var neverCompleting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var output = GeneratedHostBuildService.ReadStreamWithinCleanupBudget(neverCompleting.Task, cleanupMs: 250);

        stopwatch.Stop();
        output.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CleanupBudget_StreamThatCompletes_IsReturned()
    {
        var completed = Task.FromResult("some diagnostics");

        GeneratedHostBuildService.ReadStreamWithinCleanupBudget(completed, cleanupMs: 1000)
            .Should().Be("some diagnostics");
    }

    [Fact]
    public void RunToolchain_ArgumentWithEmbeddedSpace_ArrivesAsSingleToken()
    {
        using var dir = new TempDirectory();
        var driver = CompileArgvDumpDriver(dir);
        var dumpFile = Path.Combine(dir.FullPath, "argv-dump.txt");
        var arguments = new[] { dumpFile, "-I/path with spaces/include", "-DFOO=1" };

#pragma warning disable AARC003
        var result = GeneratedHostBuildService.RunToolchain(driver, arguments, timeoutMs: 5000);
        result.Outcome.Should().Be(GeneratedHostBuildService.ToolchainOutcome.Success);
        File.ReadAllLines(dumpFile).Should().Equal("-I/path with spaces/include", "-DFOO=1");
#pragma warning restore AARC003
    }

    [Fact]
    public void Build_ExtraCompilerArgumentWithEmbeddedSpace_IsPassedAsSingleArgument()
    {
        using var dir = new TempDirectory();
        var result = new GeneratedHostBuildService().Build(new GeneratedHostBuildRequest(
            ValidSource, dir.FullPath, "program",
            ExtraCompilerArguments: ["-I/does not exist/include"]));

        // gcc tolerates an unused, nonexistent -I search path when reached as
        // one token. If the argument boundary were lost (flattened into a
        // string and re-split), gcc would instead treat "not"/"exist/include"
        // as bogus input filenames and fail to compile.
        result.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        result.Artifact.Should().NotBeNull();
    }

    [Fact]
    public void Build_CompilerReportsSuccessWithoutObjectFile_ReturnsCompileFailed()
    {
        using var dir = new TempDirectory();
        var fakeCompiler = CompileNoOpExecutable(dir, "fake-compiler");

        var result = new GeneratedHostBuildService().Build(new GeneratedHostBuildRequest(
            ValidSource, dir.FullPath, "program", CompilerExecutable: fakeCompiler));

        result.Status.Should().Be(GeneratedHostBuildStatus.CompileFailed);
        result.DiagnosticCode.Should().Be("COMPILE_FAILED");
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void Build_LinkerReportsSuccessWithoutBinary_ReturnsLinkFailed()
    {
        using var dir = new TempDirectory();
        var fakeCompiler = CompileNoOpExecutable(dir, "fake-compiler");

        // Pre-seed the object file the compile step is expected to produce so
        // the no-op fake compiler's compile invocation still passes the
        // object-existence check, isolating the link-stage check under test.
#pragma warning disable AARC003
        File.WriteAllText(Path.Combine(dir.FullPath, "program.o"), "not a real object file");
#pragma warning restore AARC003

        var result = new GeneratedHostBuildService().Build(new GeneratedHostBuildRequest(
            ValidSource, dir.FullPath, "program", CompilerExecutable: fakeCompiler));

        result.Status.Should().Be(GeneratedHostBuildStatus.LinkFailed);
        result.DiagnosticCode.Should().Be("LINK_FAILED");
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void RunToolchain_ChildExitsButDescendantHoldsPipesOpen_ReturnsWithinBound()
    {
        using var dir = new TempDirectory();
        var driver = CompileHangingDescendantDriver(dir);
        var pidFile = Path.Combine(dir.FullPath, "descendant.pid");
        var stopwatch = Stopwatch.StartNew();

#pragma warning disable AARC003
        var result = GeneratedHostBuildService.RunToolchain(driver, [pidFile], timeoutMs: 10000, cleanupMs: 300);
#pragma warning restore AARC003
        stopwatch.Stop();

        try
        {
            // The immediate process exits normally; a hanging descendant that
            // inherited the redirected pipes must not block the result.
            result.Outcome.Should().Be(GeneratedHostBuildService.ToolchainOutcome.Success);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            KillDescendantIfRecorded(pidFile);
        }
    }

    private const string HangDriverSource =
        "#include <stdio.h>\n" +
        "int main(void) { fprintf(stderr, \"hanging driver\\n\"); fflush(stderr);" +
        " volatile unsigned long x = 0; for (;;) { x++; } }\n";

    private const string ArgvDumpDriverSource =
        "#include <stdio.h>\n" +
        "int main(int argc, char** argv) {\n" +
        "    FILE* f = fopen(argv[1], \"w\");\n" +
        "    if (!f) { return 1; }\n" +
        "    for (int i = 2; i < argc; i++) { fprintf(f, \"%s\\n\", argv[i]); }\n" +
        "    fclose(f);\n" +
        "    return 0;\n" +
        "}\n";

    // Reproduces "child exits immediately, but a descendant it spawned keeps
    // the inherited stdout/stderr pipe handle open": on first invocation
    // (argv[1] is the pidfile) it spawns a detached copy of itself that
    // inherits the redirected pipes, then returns immediately; the detached
    // copy (argv[2] == "child") records its own pid and hangs forever.
    private const string HangingDescendantDriverSource =
        "#ifdef _WIN32\n" +
        "#include <process.h>\n" +
        "#include <stdio.h>\n" +
        "static void hang_forever(const char* pidfile) {\n" +
        "    FILE* f = fopen(pidfile, \"w\");\n" +
        "    if (f) { fprintf(f, \"%d\", _getpid()); fclose(f); }\n" +
        "    for (;;) { }\n" +
        "}\n" +
        "int main(int argc, char** argv) {\n" +
        "    if (argc >= 3) { hang_forever(argv[1]); return 0; }\n" +
        "    _spawnl(_P_DETACH, argv[0], argv[0], argv[1], \"child\", NULL);\n" +
        "    return 0;\n" +
        "}\n" +
        "#else\n" +
        "#include <unistd.h>\n" +
        "#include <stdio.h>\n" +
        "int main(int argc, char** argv) {\n" +
        "    pid_t pid = fork();\n" +
        "    if (pid == 0) {\n" +
        "        FILE* f = fopen(argv[1], \"w\");\n" +
        "        if (f) { fprintf(f, \"%d\", getpid()); fclose(f); }\n" +
        "        for (;;) { }\n" +
        "    }\n" +
        "    return 0;\n" +
        "}\n" +
        "#endif\n";

    private static string CompileHangingDriver(TempDirectory dir) =>
        CompileCDriver(dir, "hang-driver", HangDriverSource);

    private static string CompileArgvDumpDriver(TempDirectory dir) =>
        CompileCDriver(dir, "argv-dump-driver", ArgvDumpDriverSource);

    private static string CompileNoOpExecutable(TempDirectory dir, string name) =>
        CompileCDriver(dir, name, ValidSource);

    private static string CompileHangingDescendantDriver(TempDirectory dir) =>
        CompileCDriver(dir, "hanging-descendant-driver", HangingDescendantDriverSource);

    private static string CompileCDriver(TempDirectory dir, string name, string source)
    {
        var sourcePath = Path.Combine(dir.FullPath, name + ".c");
        var binaryPath = Path.Combine(dir.FullPath, name);
#pragma warning disable AARC003
        File.WriteAllText(sourcePath, source);
        using var gcc = Process.Start(new ProcessStartInfo("gcc", $"\"{sourcePath}\" -o \"{binaryPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        gcc.WaitForExit(60000).Should().BeTrue($"gcc must finish compiling {name}");
        gcc.ExitCode.Should().Be(0, $"{name} must compile");
        return OperatingSystem.IsWindows() ? binaryPath + ".exe" : binaryPath;
#pragma warning restore AARC003
    }

    // Best-effort cleanup for the detached/forked descendant that the hanging-
    // descendant driver leaves running forever; it never affects the test
    // result, which is already captured before this runs.
    private static void KillDescendantIfRecorded(string pidFile)
    {
        try
        {
#pragma warning disable AARC003
            // The descendant writes its pid shortly after the (already-returned)
            // immediate process spawned it; poll briefly rather than leaving a
            // busy-looping process behind on a lost race.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            if (!File.Exists(pidFile))
            {
                return;
            }

            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            using var descendant = Process.GetProcessById(pid);
            descendant.Kill(true);
#pragma warning restore AARC003
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            // Already exited, or never recorded (e.g. spawn/fork failed): nothing to clean up.
        }
    }
}
