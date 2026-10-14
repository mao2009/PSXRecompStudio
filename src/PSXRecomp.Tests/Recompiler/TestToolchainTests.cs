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

    [Theory]
    [InlineData("x86_64-w64-mingw32", true)]
    [InlineData("x86_64-pc-cygwin", false)]
    [InlineData("i686-w64-mingw32", false)]
    [InlineData("x86_64-linux-gnu", false)]
    public void IsX64Mingw_RequiresX64AndMingw(string machine, bool expected) =>
        TestToolchain.IsX64Mingw(machine).Should().Be(expected);
}
#pragma warning restore AARC003
