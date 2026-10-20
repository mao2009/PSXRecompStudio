using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #786 review (M-A, M-C): the <c>--loaded-code-diagnostics</c> wiring of the runtime host entry point. Those runs
/// need a RAM-placed code table only a firmware distribution provides, so the flag fails closed elsewhere; and a report
/// never outlives its run or passes for a complete one when the run did not finish.
/// </summary>
[Test]
public sealed class RuntimeHostLoadedCodeReportTests
{
    private const string Flag = "--loaded-code-diagnostics";

#pragma warning disable AARC003
    private static string ReadText(string path) => File.ReadAllText(path);

    private static bool Exists(string path) => File.Exists(path);
#pragma warning restore AARC003

    [Fact]
    public void LoadedCodeDiagnostics_OnANonFirmwareManifest_FailsClosed_AndWritesNoReport()
    {
        using var dir = new TempDirectory();
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("{}"));
        var report = dir.Combine("loaded.json");

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        code.Should().Be(78, "a non-firmware run has no RAM-placed code table to report on");
        #pragma warning disable AARC003
        Exists(report).Should().BeFalse("an empty, complete-looking report must not be written");
        #pragma warning restore AARC003
    }

    // Issue #786 B1: the target equals an input (the manifest) in every spelling: same path, relative, other case, junction.
    // Each must be refused before anything is deleted or written, and the input must survive.
    [Fact]
    public void LoadedCodeDiagnostics_TargetEqualToTheManifest_IsRefused_AndTheManifestSurvives()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, manifest, manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be("not json", "the manifest is an input and must never be overwritten or deleted");
    }

    [SkippableFact]
    public void LoadedCodeDiagnostics_TargetEqualToTheManifestInOtherCase_IsRefused_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "case-insensitive path identity is a Windows rule");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var otherCase = manifest.ToUpperInvariant();

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, otherCase, manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be("not json");
    }

    [Fact]
    public void LoadedCodeDiagnostics_TargetEqualToTheManifestInAnotherSpelling_IsRefused()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        dir.CreateSubdirectory("sub");
        var dotDot = dir.Combine("sub", "..", "runtime-host.json");

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, dotDot, manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be("not json");
    }

    [SkippableFact]
    public void LoadedCodeDiagnostics_TargetThroughAJunctionToTheDistribution_IsRefused_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "directory junctions are a Windows rule");
        using var dir = new TempDirectory();
        var distribution = dir.CreateSubdirectory("dist");
        dir.WriteFile(Path.Combine("dist", FirmwareBootManifest.FileName), Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile(Path.Combine("dist", "runtime-host.json"), Encoding.UTF8.GetBytes("not json"));
        var junction = dir.Combine("junction");
        #pragma warning disable AARC003
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{junction}\" \"{distribution}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            mklink.WaitForExit();
            Skip.IfNot(mklink.ExitCode == 0, "could not create a junction on this machine");
        }
        #pragma warning restore AARC003

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, Path.Combine(junction, "runtime-host.json"), manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be("not json", "a junction is the same file, so the input must survive");
    }

    [Fact]
    public void LoadedCodeDiagnostics_TargetEqualToAnotherInput_IsRefused_AndTheInputSurvives()
    {
        using var dir = new TempDirectory();
        var bootManifest = dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, bootManifest, manifest]);

        code.Should().Be(78);
        ReadText(bootManifest).Should().Be("{}", "firmware-boot.json is an input and must survive");
    }

    // Issue #786 B1: a write that fails leaves the previous valid report in place (the temp file cannot be created).
    // The write fails at the final replace: Windows refuses to move over a read-only file. The temp it made must be removed, and the
    // previous report must be untouched. (POSIX rename ignores the file's own mode, so this failure is Windows-only.)
    [SkippableFact]
    public void LoadedCodeDiagnostics_AFailedWrite_LeavesThePreviousReportIntact_AndNoTempBehind_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a read-only destination refuses the final replace on Windows only");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var report = dir.WriteFile("loaded.json", Encoding.UTF8.GetBytes("{\"previous\":true}"));
#pragma warning disable AARC003
        File.SetAttributes(report, FileAttributes.ReadOnly);
        try
        {
            PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);
        }
        finally
        {
            File.SetAttributes(report, FileAttributes.Normal);
        }
        var leftovers = Directory.GetFiles(dir.FullPath, "*.tmp");
#pragma warning restore AARC003

        ReadText(report).Should().Be("{\"previous\":true}", "a report that could not be written must not destroy the previous one");
        leftovers.Should().BeEmpty("the temp file this write created is removed when the replace fails");
    }

    // Issue #786 (review round 5): a report target that cannot be inspected is refused as a classified setup failure. On Windows,
    // denying write access to the directory makes the target inspection fail, which used to escape the host as an exception.
    [SkippableFact]
    public void LoadedCodeDiagnostics_WhenTheReportTargetCannotBeInspected_IsRefused_AndThePreviousReportSurvives()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the inspection failure here is a Windows access rule");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var previous = "{\"previous\":true}";
        var report = dir.WriteFile("loaded.json", Encoding.UTF8.GetBytes(previous));
        Skip.IfNot(RunProcess("icacls.exe", $"\"{dir.FullPath}\" /deny \"{CurrentAccount()}\":(W)"), "this account cannot change the directory's access rule");
        try
        {
            var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

            code.Should().Be(78, "a target that cannot be inspected is refused, not run past");
        }
        finally
        {
            RestoreWriting(dir.FullPath);
        }

        ReadText(report).Should().Be(previous);
    }

    // Issue #786 B1: an exception in the run (here a manifest that does not parse) leaves the inputs intact.
    [Fact]
    public void LoadedCodeDiagnostics_AnAbnormalExit_LeavesTheInputsIntact()
    {
        using var dir = new TempDirectory();
        var bootManifest = dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var report = dir.Combine("loaded.json");

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        ReadText(bootManifest).Should().Be("{}");
        ReadText(manifest).Should().Be("not json");
        Exists(report).Should().BeTrue("the abnormal run still leaves its own report");
    }

    // Issue #786 B1: a previous report is replaced by the next run's report (not deleted up front), and each run's report carries
    // its own run id, so a report left by an earlier run cannot pass for this one.
    [Fact]
    public void LoadedCodeDiagnostics_APreviousReport_IsReplacedByTheNextRunsOwnReport()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var report = dir.Combine("loaded.json");

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);
        var first = JsonNode.Parse(ReadText(report))!.AsObject()["runId"]!.GetValue<string>();
        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);
        var second = JsonNode.Parse(ReadText(report))!.AsObject()["runId"]!.GetValue<string>();

        second.Should().NotBe(first);
    }

    [Fact]
    public void LoadedCodeDiagnostics_AFailedManifestParse_WritesAReportThatSaysItDidNotExecute()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var report = dir.Combine("loaded.json");

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        code.Should().Be(78);
        #pragma warning disable AARC003
        var json = JsonNode.Parse(ReadText(report))!.AsObject();
        #pragma warning restore AARC003
        json["executed"]!.GetValue<bool>().Should().BeFalse("the run never started");
        json["incomplete"]!.GetValue<bool>().Should().BeTrue("a run that did not execute is not a complete report");
    }

    // Issue #786 review MEDIUM-1: a host timeout cut the run short, so its report is not complete even with no gaps.
    [Fact]
    public void MarkLoadedCodeReportFinished_OnAHostTimeout_LeavesTheReportIncomplete()
    {
        var report = new LoadedCodeMismatchDiagnostics();
        var timedOut = new TitleExecutionResult(
            TitleExecutionState.RuntimeFailure, null, 0, "engine", RecompiledHostExecutionEngine.TimeoutDiagnosticCode, "timed out");

        RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(report, timedOut);

        var json = JsonNode.Parse(report.ToJson())!.AsObject();
        json["executed"]!.GetValue<bool>().Should().BeFalse("the run was stopped by the host, not classified");
        json["incomplete"]!.GetValue<bool>().Should().BeTrue();
    }

    // Issue #786 review (round 3, MAJOR): only a run that reached a completed end is executed. Every other end is an allowlist
    // miss, so a failed or budget-stopped run never reads as complete.
    [Theory]
    [InlineData(TitleExecutionState.Completed)]
    [InlineData(TitleExecutionState.Returned)]
    [InlineData(TitleExecutionState.RuntimeHandoff)]
    public void MarkLoadedCodeReportFinished_OnACompletedEnd_MarksTheReportExecuted(TitleExecutionState state)
    {
        var report = new LoadedCodeMismatchDiagnostics();
        var result = new TitleExecutionResult(state, null, 1, "engine", null, null);

        RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(report, result);

        var json = JsonNode.Parse(report.ToJson())!.AsObject();
        json["executed"]!.GetValue<bool>().Should().BeTrue();
        json["incomplete"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData(TitleExecutionState.BudgetExhausted, null)]
    [InlineData(TitleExecutionState.UnsupportedTransfer, null)]
    [InlineData(TitleExecutionState.InvalidState, null)]
    [InlineData(TitleExecutionState.RuntimeFailure, "CPU_EXCEPTION")]
    [InlineData(TitleExecutionState.RuntimeFailure, "UNRESOLVED_TRANSFER")]
    [InlineData(TitleExecutionState.RuntimeFailure, "ARTIFACT_HOST_PROTOCOL_FAILED")]
    [InlineData(TitleExecutionState.RuntimeFailure, "ENGINE_FAILED")]
    public void MarkLoadedCodeReportFinished_OnAnEndThatIsNotCompleted_LeavesTheReportIncomplete(TitleExecutionState state, string? code)
    {
        var report = new LoadedCodeMismatchDiagnostics();
        var result = new TitleExecutionResult(state, null, 1, "engine", code, "stopped");

        RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(report, result);

        var json = JsonNode.Parse(report.ToJson())!.AsObject();
        json["executed"]!.GetValue<bool>().Should().BeFalse($"{state}/{code} is not a completed end");
        json["incomplete"]!.GetValue<bool>().Should().BeTrue();
    }

    // Issue #786 B1 (PR review): input protection is by path, never by content. A file that already carries the report schema
    // is still an input when the run reads it, so the report must not replace it.
    private static string ReportText(string runId) => $"{{\"schema\":\"{LoadedCodeMismatchDiagnostics.SchemaName}\",\"runId\":\"{runId}\"}}";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // A valid firmware manifest: its artifact sits one level above runtime/, and its image is always artifact-image.bin.
    private static string ManifestText(string artifactLeaf) => RuntimeHostManifestJson.Serialize(new RuntimeHostManifest(
        RuntimeHostManifest.CurrentSchemaVersion, "../" + artifactLeaf, true, false, 0, 0, 0x80010000u,
        RuntimeHostLayout.ImageFileName, [], 0x80020000u, null, 1000, 0x80010000u));

    private static string BootText(string discFile) => FirmwareBootManifestJson.Serialize(new FirmwareBootManifest(
        FirmwareBootManifest.CurrentSchemaVersion, "openbios", new string('a', 64), 64, "firmware/rom.roots", "firmware/images.txt",
        discFile, new string('b', 64),
        [new FirmwareAotInput("firmware/rom.roots", new string('c', 64)), new FirmwareAotInput("firmware/images.txt", new string('d', 64))]));

    private static void CreateFileSymlinkOrSkip(string link, string target)
    {
#pragma warning disable AARC003
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip.If(true, $"this machine does not allow a symbolic link here: {ex.Message}");
        }
#pragma warning restore AARC003
    }

    private static void CreateJunctionOrSkip(string link, string target)
    {
#pragma warning disable AARC003
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        mklink.WaitForExit();
        Skip.IfNot(mklink.ExitCode == 0, "could not create a junction on this machine");
#pragma warning restore AARC003
    }

    // A hard link is mklink /H on Windows and ln on POSIX; both name the same file under a second name.
    private static void CreateHardLinkOrSkip(string link, string target)
    {
#pragma warning disable AARC003
        var info = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
            : new System.Diagnostics.ProcessStartInfo("ln", $"\"{target}\" \"{link}\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        var created = process.ExitCode == 0;
#pragma warning restore AARC003
        Skip.IfNot(created, "could not create a hard link on this machine");
    }

    // Runs a tool and reports whether it exited 0. Used for the access rules these tests put on a directory.
    private static bool RunProcess(string file, string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
#pragma warning disable AARC003
        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        var succeeded = process.ExitCode == 0;
#pragma warning restore AARC003
        return succeeded;
    }

    // Restores the write access RunProcess-based denial removed: the Windows deny entry for this account.
    private static void RestoreWriting(string directory)
    {
        RunProcess("icacls.exe", $"\"{directory}\" /remove:d \"{CurrentAccount()}\"");
    }

    private static string CurrentAccount()
    {
#pragma warning disable AARC003
        return Environment.UserName;
#pragma warning restore AARC003
    }

    // Runs the host and returns its exit code with what it wrote to stderr: a refusal names itself there, and an exit code alone
    // cannot tell a refusal from a run that failed later.
#pragma warning disable AARC003
    private static (int Code, string Stderr) RunCapturingStderr(params string[] args)
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main(args);
            return (code, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }
#pragma warning restore AARC003

    [Fact]
    public void LoadedCodeDiagnostics_TargetIsTheManifestWhoseContentHasTheReportSchema_IsRefused()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var content = ReportText("planted");
        var manifest = dir.WriteFile("runtime-host.json", Bytes(content));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, manifest, manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be(content, "a file with the report schema is still the manifest, an input, and must survive");
    }

    [Fact]
    public void LoadedCodeDiagnostics_TargetIsFirmwareBootWithTheReportSchema_IsRefused()
    {
        using var dir = new TempDirectory();
        var content = ReportText("planted");
        var boot = dir.WriteFile(FirmwareBootManifest.FileName, Bytes(content));
        var manifest = dir.WriteFile("runtime-host.json", Bytes("not json"));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, boot, manifest]);

        code.Should().Be(78);
        ReadText(boot).Should().Be(content, "firmware-boot.json is an input and must survive whatever it contains");
    }

    [SkippableFact]
    public void LoadedCodeDiagnostics_TargetInOtherCaseWithTheReportSchema_IsRefused_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "case-insensitive path identity is a Windows rule");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var content = ReportText("planted");
        var manifest = dir.WriteFile("runtime-host.json", Bytes(content));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, manifest.ToUpperInvariant(), manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be(content);
    }

    [SkippableFact]
    public void LoadedCodeDiagnostics_SymlinkToTheManifestWithTheReportSchema_IsRefused()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var content = ReportText("planted");
        var manifest = dir.WriteFile("runtime-host.json", Bytes(content));
        var link = dir.Combine("loaded.json");
        CreateFileSymlinkOrSkip(link, manifest);

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, link, manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be(content, "the symlink names the manifest, so the manifest must survive");
    }

    [SkippableFact]
    public void LoadedCodeDiagnostics_JunctionToTheDistributionWithTheManifestSchema_IsRefused_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "directory junctions are a Windows rule");
        using var dir = new TempDirectory();
        var distribution = dir.CreateSubdirectory("dist");
        dir.WriteFile(Path.Combine("dist", FirmwareBootManifest.FileName), Bytes("{}"));
        var content = ReportText("planted");
        var manifest = dir.WriteFile(Path.Combine("dist", "runtime-host.json"), Bytes(content));
        var junction = dir.Combine("junction");
        CreateJunctionOrSkip(junction, distribution);

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, Path.Combine(junction, "runtime-host.json"), manifest]);

        code.Should().Be(78);
        ReadText(manifest).Should().Be(content);
    }

    // A hard link is the input under another name, so the temp name matches no path. A stale temp is removed by name before the
    // write, so the write cannot reach the input through that link.
    [SkippableFact]
    public void LoadedCodeDiagnostics_StaleTempThatIsAHardLinkToAnInput_LeavesTheInputIntact_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "mklink /H is a Windows command");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Bytes("not json"));
        CreateHardLinkOrSkip(dir.Combine("loaded.json.tmp"), manifest);

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, dir.Combine("loaded.json"), manifest]);

        ReadText(manifest).Should().Be("not json", "a hard link is the manifest under another name, and the manifest must survive");
    }

    // The report's temp file is the artifact the manifest names (../loaded.json.tmp from runtime/): a temp write must never
    // replace an input.
    [Fact]
    public void LoadedCodeDiagnostics_TempSiblingThatIsAnInputWithTheReportSchema_IsNotWrittenThrough()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var content = ReportText("planted");
        dir.WriteFile("loaded.json.tmp", Bytes(content));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("loaded.json.tmp")));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, dir.Combine("loaded.json"), manifest]);

        code.Should().Be(78);
        ReadText(dir.Combine("loaded.json.tmp")).Should().Be(content, "the temp file is the artifact input and must survive");
    }

    // The firmware run reads the disc; a disc that carries the schema is an input all the same.
    [Fact]
    public void LoadedCodeDiagnostics_TargetIsTheDiscWithTheReportSchema_IsRefused()
    {
        using var dir = new TempDirectory();
        var content = ReportText("planted");
        dir.WriteFile(Path.Combine("runtime", "disc", "game.bin"), Bytes(content));
        dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes(BootText("disc/game.bin")));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("game.exe")));

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, dir.Combine("runtime", "disc", "game.bin"), manifest]);

        code.Should().Be(78);
        ReadText(dir.Combine("runtime", "disc", "game.bin")).Should().Be(content, "the disc is an input the firmware run reads");
    }

    [Fact]
    public void LoadedCodeDiagnostics_APreviousReportWithTheSchema_IsNotAnInput_AndIsReplaced()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Bytes("not json"));
        var report = dir.WriteFile("loaded.json", Bytes(ReportText("earlier")));

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        var json = JsonNode.Parse(ReadText(report))!.AsObject();
        json["runId"]!.GetValue<string>().Should().NotBe("earlier", "an earlier report that is not an input is replaced by this run's report");
    }

    [Fact]
    public void LoadedCodeDiagnostics_Off_WritesNoReportAndLeavesTheDirectoryAsItWas()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Bytes("not json"));
        var earlier = ReportText("earlier");
        dir.WriteFile("loaded.json", Bytes(earlier));

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([manifest]);

        ReadText(dir.Combine("loaded.json")).Should().Be(earlier, "without the flag no report is touched");
        Exists(dir.Combine("loaded.json.tmp")).Should().BeFalse();
        ReadText(manifest).Should().Be("not json");
    }

    // Issue #786 (PR review): --diagnostics is an output too. Its target is refused by path before anything runs, so the run exits
    // 78 and every input keeps its content.
    private const string DiagnosticsFlag = "--diagnostics";

    private static (string Manifest, string Boot, string Artifact) WriteDistribution(TempDirectory dir)
    {
        var boot = dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("game.exe")));
        var artifact = dir.WriteFile("game.exe", Bytes("artifact bytes"));
        return (manifest, boot, artifact);
    }

    private static void AssertDiagnosticsRefused(string target, string manifest, params string[] inputs)
    {
        var before = inputs.Select(path => (Path: path, Text: ReadText(path))).ToList();

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([DiagnosticsFlag, target, manifest]);

        code.Should().Be(78, "a diagnostics target that is an input is refused before the run");
        foreach (var (path, text) in before)
        {
            ReadText(path).Should().Be(text, $"{path} is an input and must survive");
        }
    }

    [Fact]
    public void Diagnostics_TargetIsTheManifest_IsRefused_AndTheInputsSurvive()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);

        AssertDiagnosticsRefused(manifest, manifest, manifest, boot, artifact);
    }

    [Fact]
    public void Diagnostics_TargetIsFirmwareBoot_IsRefused_AndTheInputsSurvive()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);

        AssertDiagnosticsRefused(boot, manifest, manifest, boot, artifact);
    }

    [Fact]
    public void Diagnostics_TargetIsTheArtifact_IsRefused_AndTheInputsSurvive()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);

        AssertDiagnosticsRefused(artifact, manifest, manifest, boot, artifact);
    }

    [SkippableFact]
    public void Diagnostics_TargetIsTheManifestInOtherCase_IsRefused_AndTheInputsSurvive_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "case-insensitive path identity is a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);

        AssertDiagnosticsRefused(manifest.ToUpperInvariant(), manifest, manifest, boot, artifact);
    }

    [SkippableFact]
    public void Diagnostics_TargetIsTheManifestThroughAJunction_IsRefused_AndTheInputsSurvive_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "directory junctions are a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);
        var junction = dir.Combine("alias");
        CreateJunctionOrSkip(junction, dir.Combine("runtime"));

        AssertDiagnosticsRefused(Path.Combine(junction, "runtime-host.json"), manifest, manifest, boot, artifact);
    }

    // Issue #786 review (round 4): path comparison cannot see a hard link, a chain of links, or an 8.3 short name. Each names an input's
    // file identity, so each must be refused, and the inputs must keep their bytes.
    private const string LoadedCodeFlag = "--loaded-code-diagnostics";

    private static string WriteRomImage(TempDirectory dir) =>
        dir.WriteFile(Path.Combine("runtime", RuntimeHostLayout.ImageFileName), Bytes("rom bytes"));

    private static (string Manifest, string Boot, string Artifact, string Disc) WriteDiscDistribution(TempDirectory dir)
    {
        var disc = dir.WriteFile(Path.Combine("runtime", "disc", "disc.bin"), Bytes("disc bytes"));
        var boot = dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes(BootText("disc/disc.bin")));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("game.exe")));
        var artifact = dir.WriteFile("game.exe", Bytes("artifact bytes"));
        return (manifest, boot, artifact, disc);
    }

    private static void AssertRefused(string flag, string target, string manifest, params string[] inputs)
    {
        var before = inputs.Select(path => (Path: path, Text: ReadText(path))).ToList();

        var (code, stderr) = RunCapturingStderr(flag, target, manifest);

        code.Should().Be(78, $"{flag} naming {target} is an input and is refused before the run");
        stderr.Should().Contain("refusing " + flag, "the refusal, not a later run failure, is what turned it away");
        foreach (var (path, text) in before)
        {
            ReadText(path).Should().Be(text, $"{path} is an input and must survive");
        }
    }

    // cmd's for-loop expands the 8.3 name; a path with spaces would need quoting that cmd echoes back, so those give no name.
    private static string? ShortNameOf(string path)
    {
        if (path.Contains(' ')) return null;
        var info = new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c for %I in ({path}) do @echo %~sI")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        #pragma warning disable AARC003
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var named = process.ExitCode == 0 && output.Length > 0 ? output : null;
        #pragma warning restore AARC003
        return named;
    }

    [SkippableFact]
    // A valid firmware boot manifest keeps the input list exact: an unparseable one would fall back to listing the whole distribution
    // root, which would refuse the link by path and hide whether identity works.
    public void Diagnostics_TargetIsAHardLinkToTheManifest_IsRefused_AndTheManifestSurvives()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var link = dir.Combine("hardlink.json");
        CreateHardLinkOrSkip(link, manifest);

        AssertRefused(DiagnosticsFlag, link, manifest, manifest, boot, artifact, disc);
    }

    [SkippableFact]
    public void Diagnostics_TargetIsAHardLinkToTheDisc_IsRefused_AndTheDiscSurvives()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var link = dir.Combine("hardlink.bin");
        CreateHardLinkOrSkip(link, disc);

        AssertRefused(DiagnosticsFlag, link, manifest, manifest, boot, artifact, disc);
    }

    [Fact]
    public void Diagnostics_TargetIsTheDiscWhenFirmwareBootDoesNotParse_IsRefused_AndTheDiscSurvives()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);
        var disc = dir.WriteFile(Path.Combine("runtime", "disc", "disc.bin"), Bytes("disc bytes"));

        AssertRefused(DiagnosticsFlag, disc, manifest, manifest, boot, artifact, disc);
    }

    [Theory]
    [InlineData("game.exe")]
    [InlineData("runtime/artifact-image.bin")]
    public void LoadedCodeDiagnostics_WhenTheManifestDoesNotParse_TheArtifactAndTheRomAreRefused(string relativeTarget)
    {
        using var dir = new TempDirectory();
        var boot = dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes("not json"));
        var artifact = dir.WriteFile("game.exe", Bytes("artifact bytes"));
        var rom = WriteRomImage(dir);

        AssertRefused(LoadedCodeFlag, dir.Combine(relativeTarget), manifest, manifest, boot, artifact, rom);
    }

    [SkippableFact]
    public void Diagnostics_TargetThroughAChainOfJunctionsToTheDisc_IsRefused_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "directory junctions are a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var first = dir.Combine("first");
        CreateJunctionOrSkip(first, dir.Combine("runtime"));
        var second = dir.Combine("second");
        CreateJunctionOrSkip(second, first);

        AssertRefused(DiagnosticsFlag, Path.Combine(second, "disc", "disc.bin"), manifest, manifest, boot, artifact, disc);
    }

    [SkippableFact]
    public void Diagnostics_TargetByItsEightPointThreeName_IsRefused_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "8.3 short names are a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact) = WriteDistribution(dir);
        var shortName = ShortNameOf(manifest);
        Skip.If(shortName is null || string.Equals(shortName, manifest, StringComparison.OrdinalIgnoreCase),
            "this volume gives the manifest no 8.3 short name");

        AssertRefused(DiagnosticsFlag, shortName!, manifest, manifest, boot, artifact);
    }

    // Issue #786 (PR review, round 5): an input listing that throws is a classified setup failure, not an escaped exception. The
    // fallback lists the firmware directory, which is denied read for this account for the duration of the run.
    private static bool RunIcacls(string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("icacls.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
#pragma warning disable AARC003
        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        var succeeded = process.ExitCode == 0;
#pragma warning restore AARC003
        return succeeded;
    }

    [SkippableFact]
    public void LoadedCodeDiagnostics_WhenAListingThrows_ExitsClassified78_AndTheInputsSurvive_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the access rule used here is a Windows ACL");
        using var dir = new TempDirectory();
        var boot = dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes("not json"));
        var firmwareDirectory = Path.GetDirectoryName(dir.WriteFile(Path.Combine("runtime", "firmware", "rom.roots"), Bytes("roots")))!;
#pragma warning disable AARC003
        var user = Environment.UserName;
#pragma warning restore AARC003
        Skip.IfNot(RunIcacls($"\"{firmwareDirectory}\" /deny \"{user}\":(OI)(CI)(R)"),
            "this account cannot change the directory's access rule here");
        try
        {
            var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([LoadedCodeFlag, dir.Combine("loaded.json"), manifest]);

            code.Should().Be(78, "a listing that cannot be read is a classified setup failure, not an escaped exception");
            ReadText(manifest).Should().Be("not json");
            ReadText(boot).Should().Be("{}");
        }
        finally
        {
            RunIcacls($"\"{firmwareDirectory}\" /remove:d \"{user}\"");
        }
    }

    // Issue #786 (review round 5): a symbolic link is followed to its file on every OS (POSIX always; Windows where the account may
    // create one). The link itself is not the input, so the refusal must come from the file it leads to.
    [SkippableFact]
    public void Diagnostics_TargetIsASymlinkToTheManifest_IsRefused_AndTheManifestSurvives()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var link = dir.Combine("symlink.json");
        CreateFileSymlinkOrSkip(link, manifest);

        AssertRefused(DiagnosticsFlag, link, manifest, manifest, boot, artifact, disc);
    }

    // The report's temp name is unguessable and fresh per write, so a file already at the old fixed name is someone else's and must
    // survive the write untouched.
    [Fact]
    public void WriteAtomically_ALeftoverFileAtTheOldTempName_IsLeftAlone()
    {
        using var dir = new TempDirectory();
        var report = dir.Combine("loaded.json");
        var leftover = dir.WriteFile("loaded.json.tmp", Bytes("someone else's bytes"));

        RuntimeHostReportFile.WriteAtomically(report, "{\"new\":true}");

        ReadText(report).Should().Be("{\"new\":true}", "the write lands at the report path");
        ReadText(leftover).Should().Be("someone else's bytes", "only a temp file this call created is ever deleted");
    }

    // A link at the old temp name must not carry the write into its input, and must not be deleted by the write either.
    [SkippableFact]
    public void WriteAtomically_AHardLinkAtTheOldTempName_LeavesTheInputIntact()
    {
        using var dir = new TempDirectory();
        var input = dir.WriteFile("input.json", Bytes("input bytes"));
        var report = dir.Combine("loaded.json");
        CreateHardLinkOrSkip(dir.Combine("loaded.json.tmp"), input);

        RuntimeHostReportFile.WriteAtomically(report, "{\"new\":true}");

        ReadText(input).Should().Be("input bytes", "the input is written through by no link");
        ReadText(report).Should().Be("{\"new\":true}");
        Exists(dir.Combine("loaded.json.tmp")).Should().BeTrue("the write did not create that name, so it does not delete it");
    }

    // Issue #786 (review round 6): a FIFO is a file whose open blocks until a writer appears. Its type is read without opening it,
    // so a FIFO named as the diagnostics target is refused and the host never blocks.
    [SkippableFact]
    public void Diagnostics_TargetIsAFifo_IsRefusedWithoutBlocking()
    {
        Skip.If(OperatingSystem.IsWindows(), "a FIFO is a POSIX file type");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var fifo = dir.Combine("report.fifo");
        Skip.IfNot(RunProcess("mkfifo", $"\"{fifo}\""), "could not create a FIFO on this machine");

        var run = Task.Run(() => PSXRecomp.Infrastructure.RuntimeHost.Program.Main([DiagnosticsFlag, fifo, manifest]));
        var finished = run.Wait(TimeSpan.FromSeconds(10));

        finished.Should().BeTrue("a FIFO must be refused by its type, before any open that would block");
        run.Result.Should().Be(78);
        ReadText(disc).Should().Be("disc bytes");
    }

    // Issue #786 (review round 6): POSIX failed write, with the report directory read-only. Creating the new temp file is refused,
    // so the previous report stays byte-for-byte and no temp is left behind. Root bypasses directory permissions, so it is skipped.
    [SkippableFact]
    public void LoadedCodeDiagnostics_AFailedWriteOnPosix_LeavesThePreviousReportIntact_AndNoTempBehind()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX directory permissions; Windows has its own failed-write test");
        Skip.If(CurrentAccount() == "root", "root bypasses directory write permission, so the failed write cannot be injected");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Bytes("not json"));
        const string previous = "{\"previous\":true}";
        var report = dir.WriteFile("loaded.json", Bytes(previous));
        Skip.IfNot(RunProcess("chmod", $"u-w \"{dir.FullPath}\""), "could not make the directory read-only");
        try
        {
            PSXRecomp.Infrastructure.RuntimeHost.Program.Main([LoadedCodeFlag, report, manifest]);
        }
        finally
        {
            RunProcess("chmod", $"u+w \"{dir.FullPath}\"");
        }

        ReadText(report).Should().Be(previous, "a report that could not be written must not destroy the previous one");
        #pragma warning disable AARC003
        Directory.GetFiles(dir.FullPath, "*.tmp").Should().BeEmpty("no temp file is left behind by a failed write");
        #pragma warning restore AARC003
    }

    // Issue #786 (review round 6): the report's temp name is now unguessable, so a file at the old fixed "<report>.tmp" name is an
    // ordinary name. The run must not write through it, and the report lands at its own path.
    [Fact]
    public void LoadedCodeDiagnostics_AFileAtTheOldTempName_IsNeverWrittenThrough()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var content = "someone else's bytes";
        dir.WriteFile("loaded.json.tmp", Bytes(content));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("loaded.json.tmp")));

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([LoadedCodeFlag, dir.Combine("loaded.json"), manifest]);

        ReadText(dir.Combine("loaded.json.tmp")).Should().Be(content, "the old temp name is an ordinary file the report never writes to");
    }

    // Issue #786 (review round 7): Win32 ignores trailing spaces and dots in a name, so "CON " and "NUL." are the console and the null
    // device. Each must be refused as a diagnostics target, before any open. (Created names with these spellings are only refused here.)
    [SkippableTheory]
    [InlineData("CON ")]
    [InlineData("NUL.")]
    [InlineData("AUX  ")]
    public void Diagnostics_TargetIsAWindowsDeviceNameWithTrailingSpacesOrDots_IsRefused(string name)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);

        AssertRefused(DiagnosticsFlag, dir.Combine(name), manifest, manifest, boot, artifact, disc);
    }

    // Issue #786 (review round 7): a read-only previous report refuses the replace, but it does not stop an in-place writer, which would
    // truncate it. This holds the previous report open so that others may write it but not replace or delete it. The replace must fail,
    // the previous bytes must survive, and the temp must be removed. An in-place write would change the bytes and fail this test.
    [SkippableFact]
    public void LoadedCodeDiagnostics_AReplaceRefusedWhileTheReportIsHeld_KeepsItsBytes_AndNoTempBehind_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a sharing mode that refuses a replace but allows writes is a Windows rule");
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Bytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Bytes("not json"));
        const string previous = "{\"previous\":true,\"note\":\"a complete earlier report that must not be truncated\"}";
        var report = dir.WriteFile("loaded.json", Bytes(previous));
        var leftovers = Array.Empty<string>();
#pragma warning disable AARC003
        using (var holder = new FileStream(report, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);
        }
        leftovers = Directory.GetFiles(dir.FullPath, "*.tmp");
#pragma warning restore AARC003

        ReadText(report).Should().Be(previous, "a replace that is refused leaves the previous report's bytes, not a truncated copy");
        leftovers.Should().BeEmpty("the temp file this write created is removed when the replace fails");
    }

    // Issue #786 (review round 8): the further Windows device names, each refused as a diagnostics target before any open. The
    // superscript digits are the reserved COM and LPT forms (U+00B9, U+00B2, U+00B3).
    [SkippableTheory]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("COM¹")]
    [InlineData("LPT²")]
    [InlineData("COM³")]
    public void Diagnostics_TargetIsAFurtherWindowsDeviceName_IsRefused(string name)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);

        AssertRefused(DiagnosticsFlag, dir.Combine(name), manifest, manifest, boot, artifact, disc);
    }

    // Issue #786 (final review, round 10): the NT object form of a device path. It must be refused before anything is opened. The
    // device is never written: a refusal leaves the disc and every other input as they were.
    [SkippableFact]
    public void Diagnostics_TargetIsAnNtDevicePath_IsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the NT device namespace is a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);

        AssertRefused(DiagnosticsFlag, "\\??\\PhysicalDrive0", manifest, manifest, boot, artifact, disc);
    }

    // Issue #786 (final review, round 14): a report target that is an alternate data stream of an input (input.json:report) is refused before
    // anything is read or written. The input keeps its data, and no stream named "report" is created on it.
    [SkippableTheory]
    [InlineData(DiagnosticsFlag)]
    [InlineData(LoadedCodeFlag)]
    public void AReportTargetThatIsAStreamOfAnInput_IsRefused_AndTheInputKeepsItsDataAndStreams_OnWindows(string flag)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "alternate data streams are an NTFS rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var stream = disc + ":report";
        var streamsBefore = StreamListing(disc);

        var (code, stderr) = RunCapturingStderr(flag, stream, manifest);

        code.Should().Be(78, "a report never names a stream");
        stderr.Should().Contain("alternate data stream", "the refusal, not a run failure, is what turned it away");
        ReadText(disc).Should().Be("disc bytes", "the input keeps its data");
        StreamListing(disc).Should().Be(streamsBefore, "the input's full stream list is unchanged, not only the named stream");
        #pragma warning disable AARC003
        File.Exists(stream).Should().BeFalse("no stream named report is created on the input");
        #pragma warning restore AARC003
    }

    // True when the attributes of the path are refused by access control (not merely not found).
    private static bool DeniesAttributesOf(string path)
    {
        try
        {
#pragma warning disable AARC003
            _ = File.GetAttributes(path);
#pragma warning restore AARC003
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // Runs the host with a supplied identity probe, and returns its exit code with its stderr (as RunCapturingStderr does).
    private static (int Code, string Stderr) RunCapturingStderrWithProbe(IFileIdentityProbe probe, params string[] args)
    {
#pragma warning disable AARC003
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Run(args, probe);
            return (code, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
#pragma warning restore AARC003
    }

    // Issue #786 (final round 17): on a host whose identity path is unverified (macOS x86-64, which no real host here can check), an existing
    // report target is refused before any write, and the existing report and inputs keep their bytes. Fail-closed, with no fallback.
    [Fact]
    public void AnExistingReportTarget_OnAnUnverifiedIdentityHost_IsRefused_AndTheReportAndInputsSurvive()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        const string earlier = "{\"earlier\":true}";
        var report = dir.WriteFile("report.json", Bytes(earlier));
        var inputsBefore = new[] { manifest, boot, artifact, disc }.Select(ReadText).ToList();

        var (code, stderr) = RunCapturingStderrWithProbe(new PSXRecomp.Infrastructure.RuntimeHost.UnverifiedIdentityProbe(new FileSystemIdentityProbe()), DiagnosticsFlag, report, manifest);

        code.Should().Be(78);
        stderr.Should().Contain("refusing " + DiagnosticsFlag, "an existing report is refused by the unverified identity, not reached by a run");
        ReadText(report).Should().Be(earlier, "the existing report is not replaced");
        new[] { manifest, boot, artifact, disc }.Select(ReadText).Should().Equal(inputsBefore, "every input keeps its bytes");
    }

    // Issue #786 (final review, round 18, item 1): on an unverified host, a dangling symbolic link at the report path is an existing name. stat
    // follows the link to a missing target and reports ENOENT, so the probe must not call it absent: the write would replace the link entry.
    // The refusal must come before any write, with the link and its (absent) target unchanged. POSIX only: the Windows probe is verified.
    [SkippableFact]
    public void ADanglingSymlinkAtTheReportPath_OnAnUnverifiedIdentityHost_IsRefused_AndTheLinkSurvives()
    {
        Skip.If(OperatingSystem.IsWindows(), "a dangling link at the report path is a POSIX case: the Windows probe is verified");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var link = dir.Combine("report.json");
        var missingTarget = dir.Combine("missing-target.json");
        CreateFileSymlinkOrSkip(link, missingTarget);
        #pragma warning disable AARC003
        var linkTargetBefore = new FileInfo(link).LinkTarget;
        #pragma warning restore AARC003
        Skip.If(linkTargetBefore is null, "the link was not created as a link on this machine");
        var inputsBefore = new[] { manifest, boot, artifact, disc }.Select(ReadText).ToList();

        var (code, stderr) = RunCapturingStderrWithProbe(new PSXRecomp.Infrastructure.RuntimeHost.UnverifiedIdentityProbe(new FileSystemIdentityProbe()), DiagnosticsFlag, link, manifest);

        code.Should().Be(78);
        stderr.Should().Contain("refusing " + DiagnosticsFlag, "a dangling link is an existing name on an unverified host, so it is refused");
        #pragma warning disable AARC003
        new FileInfo(link).LinkTarget.Should().Be(linkTargetBefore, "the link entry is not replaced");
        File.Exists(missingTarget).Should().BeFalse("nothing is written through or created at the link's target");
        #pragma warning restore AARC003
        new[] { manifest, boot, artifact, disc }.Select(ReadText).Should().Equal(inputsBefore, "every input keeps its bytes");
    }

    // Issue #786 (final review, round 19, item 1): a dangling symbolic link at the report path is refused on every POSIX host, verified or not. The
    // link entry is never replaced. On a verified host the link is not "absent": its existence is the fact that matters.
    [SkippableFact]
    public void ADanglingSymlinkAtTheReportPath_OnAVerifiedPosixHost_IsRefused_AndTheLinkSurvives()
    {
        Skip.If(OperatingSystem.IsWindows(), "a dangling link at the report path is a POSIX case");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var link = dir.Combine("report.json");
        var missingTarget = dir.Combine("missing-target.json");
        CreateFileSymlinkOrSkip(link, missingTarget);
        #pragma warning disable AARC003
        var linkTargetBefore = new FileInfo(link).LinkTarget;
        #pragma warning restore AARC003
        Skip.If(linkTargetBefore is null, "the link was not created as a link on this machine");
        var inputsBefore = new[] { manifest, boot, artifact, disc }.Select(ReadText).ToList();

        var (code, stderr) = RunCapturingStderr(DiagnosticsFlag, link, manifest);

        code.Should().Be(78);
        stderr.Should().Contain("refusing " + DiagnosticsFlag, "a dangling link is an existing name, refused before any write");
        #pragma warning disable AARC003
        new FileInfo(link).LinkTarget.Should().Be(linkTargetBefore, "the link entry is not replaced");
        File.Exists(missingTarget).Should().BeFalse("nothing is written through or created at the link's target");
        #pragma warning restore AARC003
        new[] { manifest, boot, artifact, disc }.Select(ReadText).Should().Equal(inputsBefore, "every input keeps its bytes");
    }

    // Issue #786 (final round 17): on the same unverified host, a report where no file exists yet still proceeds (creating a new file is not a
    // replacement). This guard checks that the refusal is not over-broad.
    [Fact]
    public void AnAbsentReportTarget_OnAnUnverifiedIdentityHost_StillProceeds()
    {
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var report = dir.Combine("new-report.json");

        var (code, stderr) = RunCapturingStderrWithProbe(new PSXRecomp.Infrastructure.RuntimeHost.UnverifiedIdentityProbe(new FileSystemIdentityProbe()), DiagnosticsFlag, report, manifest);

        code.Should().Be(78, "the run itself fails: the ROM is absent in this layout");
        stderr.Should().NotContain("refusing", "a report where no file exists is not refused by the unverified identity");
        #pragma warning disable AARC003
        File.Exists(report).Should().BeTrue("the diagnostic is written where no file existed");
        #pragma warning restore AARC003
    }

    // The listing of a file from dir /r: its own directory entry and its $DATA lines, in listing order. A failed dir call fails the test,
    // and an empty listing, or one without the file's own entry, is refused, so a before/after comparison cannot pass by comparing nothing.
    private static string StreamListing(string file)
    {
        var output = RunProcessStdout("cmd.exe", $"/c dir /r \"{file}\"");
        output.Should().NotBeNull("dir /r must run on the input, so that its streams can be listed");
        var leaf = Path.GetFileName(file);
        var lines = output!.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && (line.Contains(leaf, StringComparison.OrdinalIgnoreCase) || line.Contains("$DATA", StringComparison.Ordinal)))
            .ToList();
        lines.Should().NotBeEmpty("the listing must have content to compare");
        lines.Should().Contain(line => line.Contains(leaf, StringComparison.OrdinalIgnoreCase), "the input's own directory entry is in the listing");
        return string.Join("\n", lines);
    }

    private static string? RunProcessStdout(string file, string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
#pragma warning disable AARC003
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        var succeeded = process.ExitCode == 0;
#pragma warning restore AARC003
        return succeeded ? output : null;
    }

    // Issue #786 (final review, round 15, M1): a ':' in a directory component (C:\a:b\x.json), not only in the file name, is refused before
    // anything is read or written. The name is literal: the refusal happens before any I/O, so nothing on disk is touched.
    [SkippableTheory]
    [InlineData(DiagnosticsFlag)]
    [InlineData(LoadedCodeFlag)]
    public void AReportTargetWithAColonInADirectory_IsRefused_BeforeAnyRead_OnWindows(string flag)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a drive letter and a colon in a directory are a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        var before = new[] { manifest, boot, artifact, disc }.Select(ReadText).ToList();

        var (code, stderr) = RunCapturingStderr(flag, "C:\\a:b\\x.json", manifest);

        code.Should().Be(78, "a colon after the drive letter is never a report path");
        stderr.Should().Contain("alternate data stream", "the refusal, not a later failure, is what turned it away");
        new[] { manifest, boot, artifact, disc }.Select(ReadText).Should().Equal(before, "every input keeps its bytes");
    }

    // Issue #786 (final review, round 16, item 1): a target whose directory denies traverse is not "absent": File.Exists would report false on
    // the access denial. The same-file check must refuse it as unverifiable, and the inputs must survive.
    [SkippableFact]
    public void TwoReportFlags_WhenATargetDirectoryDeniesAccess_AreRefusedAsUnverifiable_AndTheInputsSurvive_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the access rule used here is a Windows ACL");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        const string earlier = "{\"earlier\":true}";
        var first = dir.WriteFile("first.json", Bytes(earlier));
        var locked = dir.CreateSubdirectory("locked");
        var second = dir.WriteFile(Path.Combine("locked", "second.json"), Bytes(earlier));
        var inputsBefore = new[] { manifest, boot, artifact, disc }.Select(ReadText).ToList();
        Skip.IfNot(RunProcess("icacls.exe", $"\"{locked}\" /deny \"{CurrentAccount()}\":(X)"), "this account cannot change the directory's access rule");
        try
        {
            // Precondition: the denial must be effective for this account. Traverse checks can be bypassed for a user (SeChangeNotifyPrivilege),
            // in which case the target is readable and this case is not the one under test; the test then skips rather than passing vacuously.
            Skip.IfNot(DeniesAttributesOf(second), "this account is not denied the target's attributes here (traverse is not enforced for it)");
            var (code, stderr) = RunCapturingStderr(DiagnosticsFlag, first, LoadedCodeFlag, second, manifest);

            code.Should().Be(78);
            stderr.Should().Contain("cannot be shown to be different files", "an access denial is unverifiable, not an absent name");
        }
        finally
        {
            RestoreWriting(locked);
        }

        ReadText(first).Should().Be(earlier, "a refused report writes nothing");
        new[] { manifest, boot, artifact, disc }.Select(ReadText).Should().Equal(inputsBefore, "every input keeps its bytes");
    }

    // Issue #786 (final review, round 15, M2): a report target whose identity cannot be read (here, a file held open without sharing) is
    // refused, not treated as a different file. The two report flags are refused before any write, and the inputs survive.
    [SkippableFact]
    public void TwoReportFlags_WhenATargetsIdentityCannotBeRead_AreRefused_AndTheInputsSurvive_OnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a sharing violation that blocks the identity read is a Windows rule");
        using var dir = new TempDirectory();
        var (manifest, boot, artifact, disc) = WriteDiscDistribution(dir);
        const string earlier = "{\"earlier\":true}";
        var first = dir.WriteFile("first.json", Bytes(earlier));
        var second = dir.WriteFile("second.json", Bytes(earlier));
#pragma warning disable AARC003
        using var holder = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
#pragma warning restore AARC003

        var inputsBefore = new[] { manifest, boot, artifact, disc }.Select(ReadText).ToList();

        var (code, stderr) = RunCapturingStderr(DiagnosticsFlag, first, LoadedCodeFlag, second, manifest);

        code.Should().Be(78);
        stderr.Should().Contain("cannot be shown to be different files", "an unreadable identity is treated as the same file, so the run is refused");
        ReadText(first).Should().Be(earlier, "a refused report writes nothing");
        new[] { manifest, boot, artifact, disc }.Select(ReadText).Should().Equal(inputsBefore, "every input keeps its bytes");
    }

    // Issue #786 (final review, round 14): the two report flags never name one file. The same name, or two names for one file, is refused
    // before any write: the second report would overwrite the first.
    [Fact]
    public void TwoReportFlagsNamingOneFile_AreRefused_BeforeAnyWrite()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("game.exe")));
        var report = dir.Combine("same.json");

        var (code, stderr) = RunCapturingStderr(DiagnosticsFlag, report, LoadedCodeFlag, report, manifest);

        code.Should().Be(78);
        stderr.Should().Contain("both would be written to one file");
        #pragma warning disable AARC003
        File.Exists(report).Should().BeFalse("the refusal happens before any report is written");
        #pragma warning restore AARC003
    }

    // Issue #786 (final review, round 14): two names for one file (a hard link) are refused too, and neither report overwrites the other's file.
    [SkippableFact]
    public void TwoReportFlagsOnHardLinkedFiles_AreRefused_AndNeitherFileIsWritten()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(Path.Combine("runtime", FirmwareBootManifest.FileName), Bytes("{}"));
        var manifest = dir.WriteFile(Path.Combine("runtime", "runtime-host.json"), Bytes(ManifestText("game.exe")));
        const string earlier = "{\"earlier\":true}";
        var first = dir.WriteFile("first.json", Bytes(earlier));
        var second = dir.Combine("second.json");
        CreateHardLinkOrSkip(second, first);

        var (code, stderr) = RunCapturingStderr(DiagnosticsFlag, first, LoadedCodeFlag, second, manifest);

        code.Should().Be(78);
        stderr.Should().Contain("both would be written to one file");
        ReadText(first).Should().Be(earlier);
        ReadText(second).Should().Be(earlier);
    }
}
