using FluentAssertions;
using PSXRecomp.Core.Dma;
using PSXRecomp.Infrastructure;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #693: while a mixed-execution fallback segment runs, a device that moves data into guest RAM must reach the
/// working copy the interpreter executes on, never <c>artifact_ram</c> (the write-back would erase it); outside a
/// segment the pre-existing fail-closed rule is unchanged.
/// </summary>
[Test]
public sealed class ArtifactDeviceRamRedirectTests
{
    private sealed class FakeBus : IMemoryBus
    {
        public Dictionary<uint, uint> Words { get; } = [];

        public uint Read(uint address) => Words.GetValueOrDefault(address);

        public void Write(uint address, uint value) => Words[address] = value;
    }

    [Fact]
    public void WithoutARedirect_AnAccessOutsideAServingWindowStillFailsClosed()
    {
        var ram = new ArtifactDeviceRam(_ => 0, (_, _) => { });

        var read = () => ram.Read(0x100);

        read.Should().Throw<ArtifactDeviceRam.UnroutableException>();
    }

    [Fact]
    public void WithARedirect_ReadsAndWritesGoToTheCoreBus_AndNeverToTheArtifact()
    {
        var artifactTouches = 0;
        var ram = new ArtifactDeviceRam(_ => { artifactTouches++; return 0; }, (_, _) => artifactTouches++);
        var core = new FakeBus();

        ram.RedirectTo(core);
        ram.Write(0x2000, 0xCAFEF00Du);
        var read = ram.Read(0x2000);

        read.Should().Be(0xCAFEF00Du);
        core.Words[0x2000].Should().Be(0xCAFEF00Du, "a device write during the segment lands in the working copy");
        artifactTouches.Should().Be(0, "artifact_ram is not touched while the segment runs");
    }

    [Fact]
    public void ClearingTheRedirect_RestoresTheArtifactRoute()
    {
        var written = new List<(uint, byte)>();
        var ram = new ArtifactDeviceRam(_ => 0, (a, v) => written.Add((a, v)));
        ram.RedirectTo(new FakeBus());
        ram.RedirectTo(null);

        ram.BeginServing();
        ram.Write(0x10, 0x01020304u);

        written.Should().Equal((0x10u, (byte)4), (0x11u, (byte)3), (0x12u, (byte)2), (0x13u, (byte)1));
    }
}
