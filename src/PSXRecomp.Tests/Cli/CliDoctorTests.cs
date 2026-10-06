using System.Text.Json;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

/// <summary>Coverage for <c>psxrecomp doctor</c> (Issue #623).</summary>
[Test]
public sealed class CliDoctorTests
{
    // The OS x architecture decision table is the subject here, so the toolchain
    // probes are stubbed: a real `gcc --version` has a wall-clock budget and turns
    // this table's verdict into a function of how busy the machine happens to be.
    // The genuine probe stays covered by MissingCompiler() below (a real launch of
    // a real missing executable) and by Execute_Doctor_HostRunHonors*.
    private static DoctorCommand.Probes Healthy(string os = "Windows", string arch = "X64") =>
        new(os, arch, ".NET 10", "gcc", () => true, () => true);

    /// <summary>Same, but with no compiler-availability override, so the real probe runs.</summary>
    private static DoctorCommand.Probes MissingCompiler() =>
        Healthy() with { CompilerExecutable = "psxrecomp-no-such-compiler", CompilerAvailable = null };

    private static (int Exit, string Output) Doctor(bool json, DoctorCommand.Probes probes)
    {
        var output = new StringWriter();
        return (DoctorCommand.Run(json, output, probes), output.ToString());
    }

    private static Dictionary<string, string> Statuses(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("status").GetString()!);

    [Fact]
    public void Doctor_UnavailableCompiler_ExitOneWithStableId()
    {
        var probes = MissingCompiler();
        var (exit, output) = Doctor(json: true, probes);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        var doc = JsonDocument.Parse(output).RootElement;
        doc.GetProperty("status").GetString().Should().Be("failed");
        doc.GetProperty("success").GetBoolean().Should().BeFalse();
        Statuses(output)["c-compiler"].Should().Be("failed");
        output.Should().Contain("TOOLCHAIN_UNAVAILABLE");
    }

    [Fact]
    public void Doctor_UnavailableNativeRuntime_ExitOne()
    {
        var (exit, output) = Doctor(json: true, Healthy() with { NativeRuntimeAvailable = () => false });

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        Statuses(output)["native-runtime"].Should().Be("failed");
        output.Should().Contain("NATIVE_RUNTIME_UNAVAILABLE");
    }

    [Theory]
    [InlineData("Windows", "X64")]
    [InlineData("Linux", "X64")]
    [InlineData("macOS", "Arm64")]
    public void Doctor_ValidatedPlatform_ExitZero(string os, string arch)
    {
        var (exit, output) = Doctor(json: true, Healthy(os, arch));

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        Statuses(output)["os"].Should().Be("ok");
    }

    /// <summary>
    /// The compiler probe is bounded by a wall-clock budget, so a machine busy enough to
    /// delay the probe past it must still report a tooling failure, never a pass.
    /// </summary>
    [Fact]
    public void Doctor_SlowCompiler_ExitOneRatherThanExitZero()
    {
        var (exit, output) = Doctor(json: true, Healthy() with { CompilerAvailable = () => false });

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        Statuses(output)["c-compiler"].Should().Be("failed");
        output.Should().Contain("TOOLCHAIN_UNAVAILABLE");
    }

    [Theory]
    [InlineData("FreeBSD", "X64")]
    [InlineData("Windows", "X86")]
    [InlineData("Windows", "Arm64")]
    [InlineData("Linux", "Arm64")]
    [InlineData("macOS", "X64")]
    public void Doctor_UnsupportedPlatform_ExitTwo(string os, string arch)
    {
        var (exit, output) = Doctor(json: true, Healthy(os, arch));

        exit.Should().Be(RecompiledArtifactExitCode.Blocked);
        Statuses(output)["os"].Should().Be("unsupported");
    }

    [Fact]
    public void Doctor_Json_IsDeterministicWithStableIdOrder()
    {
        var probes = MissingCompiler();
        var first = Doctor(json: true, probes).Output;
        var second = Doctor(json: true, probes).Output;

        second.Should().Be(first);
        Statuses(first).Keys.Should().Equal("os", "dotnet", "native-runtime", "c-compiler");
    }

    [Fact]
    public void Doctor_HumanOutput_ListsEveryCheck()
    {
        var (_, output) = Doctor(json: false, MissingCompiler());

        output.Should().Contain("OS").And.Contain(".NET").And.Contain("Native runtime").And.Contain("C compiler");
        output.Should().Contain("FAILED");
    }

    [Fact]
    public void Execute_Doctor_HostRunHonorsExitContractAndJsonShape()
    {
        var output = new StringWriter();
        var exit = Program.Execute(["doctor", "--json"], output, new StringWriter());

        exit.Should().BeOneOf(
            RecompiledArtifactExitCode.Success, RecompiledArtifactExitCode.Failure, RecompiledArtifactExitCode.Blocked);
        Statuses(output.ToString()).Keys.Should().Equal("os", "dotnet", "native-runtime", "c-compiler");
    }

    [Fact]
    public void Execute_DoctorUnknownArgument_ExitOne()
    {
        var error = new StringWriter();
        var exit = Program.Execute(["doctor", "--wat"], new StringWriter(), error);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.ToString().Should().Contain("psxrecomp doctor:");
    }

    [Fact]
    public void Execute_DoctorHelp_PrintsUsageExitZero()
    {
        var output = new StringWriter();
        var exit = Program.Execute(["doctor", "--help"], output, new StringWriter());

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        output.ToString().Should().Contain("usage: psxrecomp doctor");
    }
}
