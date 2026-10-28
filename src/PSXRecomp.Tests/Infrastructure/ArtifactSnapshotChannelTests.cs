using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

#pragma warning disable AARC003 // test fixtures create a file and set its mode; the rule guards production code
namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// PR #839 owner ruling: the state snapshot (GPR values) is written to stderr only for a run the parent manages, never to stdout, and the
/// parent reads it from stderr alone. A guest that prints the markers on stdout cannot make a run look completed, and a direct launch prints
/// no snapshot. The values here are synthetic and identifiable.
/// </summary>
[Test]
public sealed class ArtifactSnapshotChannelTests
{
    private const uint IdentifiableGpr = 0xC0FFEE11u;
    private const uint SpoofedGpr = 0xDEADBEEFu;

    private static string Snapshot(uint gpr9) =>
        RecompiledArtifactCodeGen.SnapshotBeginMarker + "\n"
        + "termination=0\npc=0x80010000\nhi=0x00000000\nlo=0x00000000\n"
        + string.Concat(Enumerable.Range(0, 32).Select(i => $"gpr[{i}]=0x{(i == 9 ? gpr9 : 0u):X8}\n"))
        + "exception.raised=0\nexception.code=0x00000000\nexception.faultPc=0x00000000\nexception.inDelaySlot=0\n"
        + "cop0.sr=0x00000000\ncop0.cause=0x00000000\ncop0.epc=0x00000000\n"
        + RecompiledArtifactCodeGen.SnapshotEndMarker + "\n";

    private static RecompilerHostCodeGenResult GenerateDispatch()
    {
        var instruction = R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = MipsToIrLowerer.LowerProgram([(instruction, 0x80010000u)]);
        return RecompilerHostCodeGen.Generate(program);
    }

    // The spoofing matrix: thirteen guest stdout forms, each ending in a complete, well-formed snapshot. Glued prefixes, BEGIN and END on
    // one line, and a block end in front of a begin are all covered. None may complete a run: a snapshot on stdout is never the channel.
    public static TheoryData<string> SpoofedStdoutForms() => new()
    {
        "",
        "HELLO",
        "RHOST_OK ",
        "RSNAPSHOT_FOO",
        "RHOST_",
        "RSNAPSHOT_RSNAPSHOT_",
        "HELLO RSNAPSHOT_BEGIN RSNAPSHOT_END",
        "RHOST_OK RSNAPSHOT_BEGIN RSNAPSHOT_END",
        "RSNAPSHOT_BEGIN RSNAPSHOT_END RSNAPSHOT_BEGIN",
        "X RSNAPSHOT_END",
        "RSNAPSHOT_BEGIN",
        "AB RHOST_OK RSNAPSHOT_BEGIN",
        "RSNAPSHOT_END",
    };

    [Theory]
    [MemberData(nameof(SpoofedStdoutForms))]
    public void StdoutSpoofingForm_NeverCompletesARun_WithoutAStderrSnapshot(string prefix)
    {
        var stdout = prefix + Snapshot(SpoofedGpr);

        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: stdout, stderr: "",
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().NotBe(RecompilerExecutionStatus.Completed, "the stdout form is not the channel");
        result.Snapshot.Should().BeNull();
    }

    [Fact]
    public void Source_WritesTheSnapshotToStderr_AndNeverToStdout()
    {
        var dispatch = GenerateDispatch();
        dispatch.Success.Should().BeTrue();

        var source = RecompiledArtifactCodeGen.Generate(dispatch).Source!;

        source.Should().Contain("fprintf(stderr, \"RSNAPSHOT_BEGIN\\n\");");
        source.Should().Contain("fprintf(stderr, \"RSNAPSHOT_END\\n\");");
        source.Should().NotContain("printf(\"RSNAPSHOT_", "no snapshot line is written to stdout");
        source.Should().Contain(RecompiledArtifactCodeGen.HostSnapshotFlag, "the snapshot is asked for by the parent's flag only");
        source.Should().Contain("int snapshot_flag = 0;");
    }

    [Fact]
    public void ASnapshotOnStderr_IsAccepted_AndRestoresTheGprValue()
    {
        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: "", stderr: Snapshot(IdentifiableGpr),
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Snapshot!.Gpr.ToArray()[9].Should().Be(IdentifiableGpr, "CPU state restore reads the stderr snapshot");
    }

    [Fact]
    public void ASnapshotOnStdoutOnly_IsNotAccepted()
    {
        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: Snapshot(IdentifiableGpr), stderr: "",
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().Be(RecompilerExecutionStatus.ExecutionFailed, "the stdout snapshot is not the channel");
        result.Snapshot.Should().BeNull();
    }

    // Guest spoofing: a complete, well-formed snapshot printed by the guest on stdout, with no snapshot on stderr. It must not be accepted.
    [Fact]
    public void GuestMarkersOnStdout_CannotStandInForTheSnapshot()
    {
        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: Snapshot(SpoofedGpr), stderr: "",
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().NotBe(RecompilerExecutionStatus.Completed);
        result.Snapshot.Should().BeNull();
    }

    // Regression: BEGIN and END on the same stdout line, and a genuine snapshot on stderr. Only the stderr values are restored.
    [Fact]
    public void BeginAndEndOnOneStdoutLine_DoNotDisturbTheGenuineStderrSnapshot()
    {
        var spoof = $"RSNAPSHOT_BEGIN RSNAPSHOT_END RSNAPSHOT_BEGIN\n{Snapshot(SpoofedGpr)}";

        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: spoof, stderr: Snapshot(IdentifiableGpr),
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Snapshot!.Gpr.ToArray()[9].Should().Be(IdentifiableGpr, "the spoofed value on stdout is never read");
    }

    [Fact]
    public void BeginAndEndOnOneStderrLine_IsMalformed_NotCompleted()
    {
        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: "",
            stderr: $"{RecompiledArtifactCodeGen.SnapshotBeginMarker} {RecompiledArtifactCodeGen.SnapshotEndMarker}\n",
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().Be(RecompilerExecutionStatus.MalformedResult);
    }

    // PR #839 MAJOR (re-review of 78bcf03): a bridged run whose child exited 0 after a complete snapshot on stderr, while a grandchild still holds
    // the stderr pipe past the output budget. The text read so far is the whole snapshot, so the run is Completed. Non-Windows: it runs a shell.
    [SkippableFact]
    public void BridgedRun_WithAGrandchildHoldingStderr_ClassifiesTheCompleteSnapshotFromThePartialText()
    {
        // A POSIX shell runs the child: /bin/sh on Linux and macOS, Git for Windows' sh on Windows. Without a shell it is NOT RUN.
        var shell = OperatingSystem.IsWindows() ? @"C:\Program Files\Git\usr\bin\sh.exe" : "/bin/sh";
        Skip.IfNot(File.Exists(shell), $"NOT RUN: no POSIX shell at {shell}");
        using var dir = new TempDirectory();
        // The snapshot is written into the script as a here-document to stderr, so no path is passed to the shell.
        var scriptPath = dir.WriteFile("child.sh", System.Text.Encoding.UTF8.GetBytes(
            "/usr/bin/cat >&2 <<'SNAPSHOT'\n" + Snapshot(IdentifiableGpr) + "SNAPSHOT\n/usr/bin/sleep 60 &\nexit 0\n")).Replace('\\', '/');

        var result = RecompiledHostExecutionEngine.RunBridgedChildForTest(shell, [scriptPath], timeoutMs: 60_000, outputBudgetMs: 3_000);

        result.Status.Should().Be(RecompilerExecutionStatus.Completed,
            "the child ended on its own, and its complete snapshot is in the stderr text read so far");
        result.Snapshot!.Gpr.ToArray()[9].Should().Be(IdentifiableGpr);
    }

    [Fact]
    public void AnAbnormalExit_WithACompleteStderrSnapshot_IsNotCompleted()
    {
        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(-1, stdout: "", stderr: Snapshot(IdentifiableGpr),
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().Be(RecompilerExecutionStatus.ExecutionFailed);
        result.Snapshot.Should().BeNull();
    }

    [Fact]
    public void AnIncompleteStderrSnapshot_IsMalformed_AndItsDiagnosticHoldsNoGprValue()
    {
        var incomplete = Snapshot(IdentifiableGpr).Replace(RecompiledArtifactCodeGen.SnapshotEndMarker, "");

        var result = RecompiledHostExecutionEngine.ClassifyWithoutBridge(0, stdout: "", stderr: incomplete,
            hostProtocolFaulted: false, timedOut: false);

        result.Status.Should().Be(RecompilerExecutionStatus.MalformedResult);
        result.DiagnosticMessage.Should().NotContain("C0FFEE11");
    }

    // Decision A (PR #839), launcher leak: the public JSON is built from a type with no register value.
    [Fact]
    public void LaunchOutcomeJson_CarriesNoRegisterValue_EvenWhenTheResultHasOne()
    {
        var result = new RecompiledArtifactResult(default, 0, default, 0x80010000u, IdentifiableGpr, "engine", null, null);

        var outcome = new RecompiledArtifactLauncher.LaunchOutcome(result, Array.Empty<byte>());

        outcome.Json.Should().Contain("\"exitCode\"");
        outcome.Json.Should().NotContain("resultValue");
        outcome.Json.Should().NotContain("C0FFEE11");
        outcome.Json.Should().NotContain(IdentifiableGpr.ToString());
    }
}

#pragma warning restore AARC003
