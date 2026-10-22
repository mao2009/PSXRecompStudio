using System.IO;
using System.Linq;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// End-to-end checks of <c>psxrecomp package --gui</c> (Issue #830): the game executable is a Windows GUI program that imports no
/// compiler runtime, the window package carries the libraries its native module needs, and the window's run has a finite timeout
/// far beyond the headless one. A headless package of the same input is checked alongside, to keep its output as it was.
/// </summary>
[Test]
public sealed class GuiWindowPackageE2E : IDisposable
{
    private const int HeadlessRunTimeoutMs = 60_000;
    private const int MaxGuiRunTimeoutMs = 4 * 60 * 60 * 1000;

    private readonly TempDirectory _tempDir;

    public GuiWindowPackageE2E()
    {
        _tempDir = new TempDirectory();
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    [SkippableFact]
    public void GuiPackage_IsWindowsGuiProgram_CarriesItsCompilerLibraries_AndHasAFiniteWindowTimeout()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the window package and its PE checks are Windows-only");
        RuntimePublishGate();

        var input = _tempDir.WriteFile("synthetic.exe", SyntheticPsxExe.Build());
        var outputDir = _tempDir.CreateSubdirectory("gui-game");

        var (exit, output, error) = Invoke("package", input, "--output", outputDir, "--gui");
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package --gui must succeed: error='{error}', output='{output}'");

#pragma warning disable AARC003 // Test-layer I/O for verification of the packaged window
        var runtimeDir = Path.Join(outputDir, RuntimeHostLayout.RuntimeDirectoryName);
        var artifactBytes = File.ReadAllBytes(Path.Join(outputDir, "gui-game.exe"));
        PeImageInfo.Subsystem(artifactBytes).Should().Be(PeImageInfo.SubsystemWindowsGui, "the game executable must not open a console");
        CompilerImports(artifactBytes).Should().BeEmpty("the game executable is linked with -static-libgcc and imports no compiler runtime");

        File.Exists(Path.Join(runtimeDir, RuntimeHostLayout.GuiHostFileName)).Should().BeTrue("the window host ships under runtime/");
        File.Exists(Path.Join(runtimeDir, "PSXRecomp.RuntimeHost.exe")).Should().BeFalse("a window package runs the artifact itself");

        // Every non-Windows import of the native module, and of each library it pulls in, must be carried by the package.
        var pending = new Queue<string>(PeImageInfo.ImportedDllNames(File.ReadAllBytes(Path.Join(runtimeDir, "PSXRecomp.Native.dll"))));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (PeImageInfo.IsWindowsLibrary(name) || !visited.Add(name))
            {
                continue;
            }

            var carried = Path.Join(runtimeDir, name);
            File.Exists(carried).Should().BeTrue($"a library in the window's native module's import chain ({name}) must be carried");
            foreach (var next in PeImageInfo.ImportedDllNames(File.ReadAllBytes(carried)))
            {
                pending.Enqueue(next);
            }
        }

        var manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(Path.Join(runtimeDir, RuntimeHostLayout.ManifestFileName)));
        manifest.RunTimeoutMs.Should().BeGreaterThan(HeadlessRunTimeoutMs, "a window runs until it is closed");
        manifest.RunTimeoutMs.Should().BeLessThanOrEqualTo(MaxGuiRunTimeoutMs, "the window's bound is finite");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void HeadlessPackage_KeepsConsoleProgram_HeadlessTimeout_AndItsLinkOptions()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the window package and its PE checks are Windows-only");
        RuntimePublishGate();

        var input = _tempDir.WriteFile("synthetic.exe", SyntheticPsxExe.Build());
        var outputDir = _tempDir.CreateSubdirectory("headless-game");

        var (exit, output, error) = Invoke("package", input, "--output", outputDir);
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package must succeed: error='{error}', output='{output}'");

#pragma warning disable AARC003 // Test-layer I/O for verification of the headless package
        var runtimeDir = Path.Join(outputDir, RuntimeHostLayout.RuntimeDirectoryName);
        var artifactBytes = File.ReadAllBytes(Path.Join(outputDir, "headless-game.exe"));
        PeImageInfo.Subsystem(artifactBytes).Should().Be(PeImageInfo.SubsystemWindowsCui, "the headless program stays a console program");
        CompilerImports(artifactBytes).Should().BeEmpty("the headless artifact imports only Windows libraries, as before this change");
        File.Exists(Path.Join(runtimeDir, "PSXRecomp.RuntimeHost.exe")).Should().BeTrue("the headless package bundles the Runtime host");
        File.Exists(Path.Join(runtimeDir, RuntimeHostLayout.GuiHostFileName)).Should().BeFalse("the headless package has no window host");

        var manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(Path.Join(runtimeDir, RuntimeHostLayout.ManifestFileName)));
        manifest.RunTimeoutMs.Should().Be(HeadlessRunTimeoutMs, "the headless run timeout is unchanged");
#pragma warning restore AARC003
    }

    /// <summary>The imported DLLs that come from the compiler (not from Windows).</summary>
    private static IReadOnlyList<string> CompilerImports(byte[] image) =>
        PeImageInfo.ImportedDllNames(image)
            .Where(name => name.StartsWith("libgcc_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("libstdc++", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("libwinpthread", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void RuntimePublishGate() => RuntimeHostPublishE2E.RequireGcc();

    private static (int Exit, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Program.Execute(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }
}
