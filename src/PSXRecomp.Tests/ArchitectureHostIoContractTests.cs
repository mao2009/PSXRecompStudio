using System.Text.Json;

namespace PSXRecomp.Tests;

/// <summary>
/// Locks the Issue #38 machine contract at the data level. The analyzer itself is
/// already exercised by the repository-wide AARC gate; these assertions prevent
/// the port/adapter policy from drifting in the JSON SSOT without an intentional
/// architecture change.
/// </summary>
[Test]
public sealed class ArchitectureHostIoContractTests
{
    [Fact]
    public void Infrastructure_AllowsConcreteHostIoApis()
    {
        using var contract = LoadContract();
        var forbidden = contract.RootElement.GetProperty("forbiddenApis")
            .EnumerateArray()
            .Where(x => x.GetProperty("layer").GetString() == "Infrastructure")
            .Select(x => x.GetProperty("type").GetString())
            .ToHashSet(StringComparer.Ordinal);

        forbidden.Should().NotContain("System.IO.File");
        forbidden.Should().NotContain("System.IO.Directory");
        forbidden.Should().NotContain("System.Console");
        forbidden.Should().NotContain("System.Diagnostics.Process");
        forbidden.Should().NotContain("System.Net.Http.HttpClient");
        forbidden.Should().NotContain("System.Net.Sockets.Socket");
    }

    [Fact]
    public void Domain_CannotDependOnConcreteInfrastructure()
    {
        using var contract = LoadContract();

        HasForbiddenDependency(contract, "Domain", "Infrastructure")
            .Should().BeTrue("Domain owns ports and must not depend on concrete adapters");
    }

    [Fact]
    public void Application_CannotDependOnConcreteInfrastructureOutsideCompositionRootException()
    {
        using var contract = LoadContract();

        HasForbiddenDependency(contract, "Application", "Infrastructure")
            .Should().BeTrue("ordinary Application code must remain port-only; composition-root wiring is an explicit narrow suppression");
    }

    [Fact]
    public void Infrastructure_DependsInwardOnDomainButNeverApplication()
    {
        using var contract = LoadContract();

        HasForbiddenDependency(contract, "Infrastructure", "Domain").Should().BeFalse();
        HasForbiddenDependency(contract, "Infrastructure", "Application").Should().BeTrue();
    }

    [Theory]
    [InlineData("System.IO.File")]
    [InlineData("System.IO.Directory")]
    [InlineData("System.Diagnostics.Process")]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("System.Net.Sockets.Socket")]
    public void Domain_StillForbidsConcreteHostIo(string typeName)
    {
        using var contract = LoadContract();
        var forbidden = contract.RootElement.GetProperty("forbiddenApis")
            .EnumerateArray()
            .Any(x => x.GetProperty("layer").GetString() == "Domain"
                && x.GetProperty("type").GetString() == typeName);

        forbidden.Should().BeTrue($"{typeName} must stay behind a Domain-owned port");
    }

    private static bool HasForbiddenDependency(JsonDocument contract, string from, string to)
    {
        return contract.RootElement.GetProperty("forbiddenDependencies")
            .EnumerateArray()
            .Any(x => x.GetProperty("from").GetString() == from
                && x.GetProperty("to").GetString() == to);
    }

    private static JsonDocument LoadContract()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "architecture.contract.json"));

#pragma warning disable AARC003 // Test-only SSOT fixture read; production host I/O remains behind Infrastructure.
        var json = File.ReadAllText(path);
#pragma warning restore AARC003
        return JsonDocument.Parse(json);
    }
}
