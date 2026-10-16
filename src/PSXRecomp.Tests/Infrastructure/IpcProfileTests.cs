using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #768: the opt-in pipe-protocol profile classifies messages exactly and reports what it counted.</summary>
[Test]
public sealed class IpcProfileTests
{
    [Theory]
    [InlineData("RHOST_RETIRED 12", IpcProfile.Kind.TimeReport)]
    [InlineData("RHOST_RETIRED 12 R 4 528488468", IpcProfile.Kind.FusedMmio)]
    [InlineData("RHOST_RETIRED 12 W 4 528488468 7", IpcProfile.Kind.FusedMmio)]
    [InlineData("RHOST_MMIO_READ 4 528488468", IpcProfile.Kind.MmioRead)]
    [InlineData("RHOST_MMIO_WRITE 4 528488468 1", IpcProfile.Kind.MmioWrite)]
    [InlineData("RHOST_TRANSFER 2147483648", IpcProfile.Kind.Transfer)]
    [InlineData("something else", IpcProfile.Kind.Other)]
    public void Classify_UsesTheProtocolPrefixes(string line, IpcProfile.Kind expected) =>
        IpcProfile.Classify(line).Should().Be(expected);

    [Fact]
    public void CountsEveryMessageOnceAndSeparatesWritesFromDeviceTime()
    {
        var profile = new IpcProfile();
        profile.Begin("RHOST_RETIRED 5");
        profile.Device(10);
        profile.Wrote(4, 3);
        profile.Wrote(2, 3);
        profile.End();
        profile.Begin("RHOST_MMIO_READ 4 1");
        profile.Wrote(3, 2);
        profile.End();

        var doc = JsonDocument.Parse(profile.ToJson(new Dictionary<string, object?> { ["x"] = 1 })).RootElement;
        doc.GetProperty("totalMessages").GetDecimal().Should().Be(2);
        var kinds = doc.GetProperty("kinds").EnumerateArray().ToDictionary(k => k.GetProperty("kind").GetString()!);
        kinds["TimeReport"].GetProperty("count").GetUInt64().Should().Be(1);
        kinds["TimeReport"].GetProperty("writes").GetUInt64().Should().Be(2, "a report's credit and ack are two lines");
        kinds["TimeReport"].GetProperty("bytesOut").GetUInt64().Should().Be(6);
        kinds["MmioRead"].GetProperty("writes").GetUInt64().Should().Be(1);
    }
}
