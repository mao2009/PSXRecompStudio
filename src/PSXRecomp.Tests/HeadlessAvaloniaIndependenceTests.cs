using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// Issue #455 / #40: the Runtime (Core), Infrastructure and the headless CLI must
/// stay usable without the GUI, so none of them may reference an Avalonia assembly.
/// </summary>
[Test]
public sealed class HeadlessAvaloniaIndependenceTests
{
    [Fact]
    public void CoreInfrastructureAndCli_DoNotReferenceAvalonia()
    {
        var assemblies = new[]
        {
            typeof(FrameSnapshot).Assembly,
            typeof(PSXRecomp.Infrastructure.Cli.ProductionFrameEvidenceCollector).Assembly,
            typeof(PSXRecomp.Infrastructure.Cli.Program).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Should().NotContain(n => n.StartsWith("Avalonia", StringComparison.Ordinal),
                    $"{assembly.GetName().Name} must not depend on the GUI");
        }
    }
}
