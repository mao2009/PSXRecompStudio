namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003
[Test]
public sealed class TestToolchainTests
{
    private static string DotnetHost => Environment.ProcessPath is { } p
        && Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? p
            : TestToolchain.FindOnPath("dotnet")!;

    [Fact]
    public void ProbeMachine_ReturnsTrimmedStdout_OnZeroExit()
    {
        TestToolchain.ProbeMachine(DotnetHost, "--version").Should().MatchRegex(@"^\d+\.\d+");
    }

    [Fact]
    public void ProbeMachine_NonZeroExit_ThrowsWithDiagnostics()
    {
        var act = () => TestToolchain.ProbeMachine(DotnetHost, "--no-such-option-xyz");
        act.Should().Throw<InvalidOperationException>().WithMessage("*exited*");
    }

    [Fact]
    public void ProbeMachine_MissingExecutable_ThrowsInsteadOfSkipping()
    {
        var act = () => TestToolchain.ProbeMachine(Path.Combine(Path.GetTempPath(), "no-such-compiler-xyz"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*could not start*");
    }

    [SkippableFact]
    public void RunBounded_HungTool_TimesOutAndKillsTheProcess()
    {
        var sleep = TestToolchain.LongSleep(999);
        Skip.If(sleep is null, "no long-running utility (ping on Windows, sleep elsewhere) found");

        var pid = 0;
        var act = () => TestToolchain.RunBounded(sleep!.Value.FileName, sleep.Value.Arguments, 500, id => pid = id);
        act.Should().Throw<TimeoutException>();

        pid.Should().BeGreaterThan(0);
        var alive = true;
        try { using var probe = System.Diagnostics.Process.GetProcessById(pid); alive = !probe.HasExited; }
        catch (ArgumentException) { alive = false; }
        alive.Should().BeFalse("the timed-out child must be killed and reaped");
    }

    [SkippableFact]
    public void ProbeMachine_HungTool_PropagatesTimeout()
    {
        var sleep = TestToolchain.LongSleep(999);
        Skip.If(sleep is null, "no long-running utility (ping on Windows, sleep elsewhere) found");

        var act = () => TestToolchain.ProbeMachine(sleep!.Value.FileName, sleep.Value.Arguments, 500);
        act.Should().Throw<TimeoutException>();
    }

    [SkippableFact]
    public void RunBounded_KillFailure_IsReportedAndNeverTreatedAsSuccess()
    {
        var sleep = TestToolchain.LongSleep(30);
        Skip.If(sleep is null, "no long-running utility (ping on Windows, sleep elsewhere) found");

        var pid = 0;
        try
        {
            var act = () => TestToolchain.RunBounded(
                sleep!.Value.FileName, sleep.Value.Arguments, 300, id => pid = id,
                reapTimeoutMs: 300,
                kill: _ => throw new System.ComponentModel.Win32Exception("simulated kill failure"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            act.Should().Throw<TimeoutException>()
                .Which.Message.Should().Contain("could not be confirmed").And.Contain("simulated kill failure");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "every wait after the timeout is bounded");
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    [SkippableFact]
    public void RunBounded_ExitedChildWithPipeHeldByDescendant_FailsInsteadOfHanging()
    {
        // The parent exits at once, but a background descendant inherits the stdout pipe.
        var shell = TestToolchain.FindOnPath(OperatingSystem.IsWindows() ? "cmd" : "sh");
        Skip.If(shell is null, "no command shell (cmd on Windows, sh elsewhere) found on PATH");
        var file = shell!;
        var args = OperatingSystem.IsWindows()
            ? "/c start /b ping -n 6 127.0.0.1"
            : "-c \"sleep 5 &\"";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = () => TestToolchain.RunBounded(file, args, 10000, drainTimeoutMs: 500);
        act.Should().Throw<TimeoutException>().WithMessage("*output streams*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    private static void KillQuietly(int pid)
    {
        if (pid <= 0) return;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    [Theory]
    [InlineData("x86_64-w64-mingw32", true)]
    [InlineData("x86_64-pc-cygwin", false)]
    [InlineData("i686-w64-mingw32", false)]
    [InlineData("x86_64-linux-gnu", false)]
    public void IsX64Mingw_RequiresX64AndMingw(string machine, bool expected) =>
        TestToolchain.IsX64Mingw(machine).Should().Be(expected);
}
#pragma warning restore AARC003
