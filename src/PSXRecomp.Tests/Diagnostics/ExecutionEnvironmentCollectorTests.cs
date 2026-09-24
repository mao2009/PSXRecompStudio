using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Infrastructure.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

[Test]
public sealed class ExecutionEnvironmentCollectorTests
{
    [Fact]
    public void Capture_PopulatesOnlySafeSingleLineHostFacts()
    {
        var report = ExecutionEnvironmentCollector.Capture();

        report.Schema.Should().Be(ExecutionEnvironmentReport.CurrentSchema);
        report.OsPlatform.Should().NotBeNullOrWhiteSpace();
        report.OsVersion.Should().NotBeNullOrWhiteSpace();
        report.OsArchitecture.Should().NotBeNullOrWhiteSpace();
        report.ProcessArchitecture.Should().NotBeNullOrWhiteSpace();
        report.DotNetVersion.Should().NotBeNullOrWhiteSpace();
        report.RuntimeIdentifier.Should().NotBeNullOrWhiteSpace();
        report.CompilerVersion.Should().BeNull();
        report.TargetArchitecture.Should().Be(report.ProcessArchitecture);

        foreach (var value in new[]
                 {
                     report.OsPlatform,
                     report.OsVersion,
                     report.OsArchitecture,
                     report.ProcessArchitecture,
                     report.DotNetVersion,
                     report.RuntimeIdentifier!,
                     report.TargetArchitecture!,
                 })
        {
            value.Should().NotContain("\r");
            value.Should().NotContain("\n");
        }
    }

    [Fact]
    public void Capture_SerializesThroughTheExistingEnvironmentContract()
    {
        var report = ExecutionEnvironmentCollector.Capture();

        var json = DiagnosticJson.Serialize(report);
        var roundTrip = DiagnosticJson.Deserialize<ExecutionEnvironmentReport>(json);

        roundTrip.Should().Be(report);
    }
}
