using System.Text.Json;
using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

[Test]
public sealed class ExecutionEnvironmentReportTests
{
    [Fact]
    public void Create_ContainsOnlyReproductionRelevantEnvironmentFields()
    {
        var report = ExecutionEnvironmentReport.Create(
            osPlatform: "Linux",
            osVersion: "6.8",
            osArchitecture: "X64",
            processArchitecture: "X64",
            dotNetVersion: "10.0.0",
            runtimeIdentifier: "linux-x64",
            compilerVersion: "gcc 15.2",
            targetArchitecture: "x64");

        var json = DiagnosticJson.Serialize(report);
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

        names.Should().Equal(
            "schema",
            "osPlatform",
            "osVersion",
            "osArchitecture",
            "processArchitecture",
            "dotNetVersion",
            "runtimeIdentifier",
            "compilerVersion",
            "targetArchitecture");

        names.Should().NotContain([
            "hostName",
            "userName",
            "homeDirectory",
            "currentDirectory",
            "path",
            "ipAddress",
            "machineId",
            "environmentVariables",
        ]);
    }

    [Fact]
    public void DiagnosticJson_IsDeterministic_AndWritesOptionalFieldsAsNull()
    {
        var report = ExecutionEnvironmentReport.Create(
            osPlatform: "Linux",
            osVersion: "6.8",
            osArchitecture: "X64",
            processArchitecture: "X64",
            dotNetVersion: "10.0.0");

        var first = DiagnosticJson.Serialize(report);
        var second = DiagnosticJson.Serialize(report);

        first.Should().Be(second);
        first.Should().EndWith("\n");

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        root.GetProperty("schema").GetString().Should().Be(ExecutionEnvironmentReport.CurrentSchema);
        root.GetProperty("runtimeIdentifier").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("compilerVersion").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("targetArchitecture").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("Linux\nsecret")]
    [InlineData("Linux\rsecret")]
    [InlineData("Linux\r\nsecret")]
    public void Create_RejectsMultilineEnvironmentMetadata(string value)
    {
        var act = () => ExecutionEnvironmentReport.Create(
            osPlatform: value,
            osVersion: "6.8",
            osArchitecture: "X64",
            processArchitecture: "X64",
            dotNetVersion: "10.0.0");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_TrimsValues_AndNormalizesWhitespaceOnlyOptionalValuesToNull()
    {
        var report = ExecutionEnvironmentReport.Create(
            osPlatform: " Linux ",
            osVersion: " 6.8 ",
            osArchitecture: " X64 ",
            processArchitecture: " X64 ",
            dotNetVersion: " 10.0.0 ",
            runtimeIdentifier: " ",
            compilerVersion: null,
            targetArchitecture: " x64 ");

        report.OsPlatform.Should().Be("Linux");
        report.OsVersion.Should().Be("6.8");
        report.DotNetVersion.Should().Be("10.0.0");
        report.RuntimeIdentifier.Should().BeNull();
        report.CompilerVersion.Should().BeNull();
        report.TargetArchitecture.Should().Be("x64");
    }
}
