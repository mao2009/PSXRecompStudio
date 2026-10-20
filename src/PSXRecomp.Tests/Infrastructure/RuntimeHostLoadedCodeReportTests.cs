using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
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
    [Fact]
    public void LoadedCodeDiagnostics_AFailedWrite_LeavesThePreviousReportIntact()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var report = dir.WriteFile("loaded.json", Encoding.UTF8.GetBytes("{\"previous\":true}"));
        dir.CreateSubdirectory("loaded.json.tmp");

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        ReadText(report).Should().Be("{\"previous\":true}", "a report that could not be written must not destroy the previous one");
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

    private static void CreateHardLinkOrSkip(string link, string target)
    {
#pragma warning disable AARC003
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        mklink.WaitForExit();
        Skip.IfNot(mklink.ExitCode == 0, "could not create a hard link on this machine");
#pragma warning restore AARC003
    }

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
    public void LoadedCodeDiagnostics_TempSiblingThatIsAnInputWithTheReportSchema_IsRefused()
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
}
