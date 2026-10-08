using PSXRecomp.Core.MemoryCard;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.MemoryCard;

/// <summary>The BIOS-less Runtime's memory-card slot policy (Issue #715): explicit, immutable, default Empty/Empty.</summary>
[Test]
public sealed class PsxDeviceGraphMemoryCardSlotTests
{
    [Fact]
    public void Default_BothSlotsEmpty()
    {
        using var graph = new PsxDeviceGraph();

        graph.MemoryCardSlots.Should().Be(MemoryCardSlotConfiguration.Empty);
        graph.HasMemoryCard(0).Should().BeFalse();
        graph.HasMemoryCard(1).Should().BeFalse();
        graph.MemoryCardSlotSummary.Should().Be("slot0=Empty slot1=Empty");
    }

    [Fact]
    public void ExplicitEmpty_EqualsDefault()
    {
        using var graph = new PsxDeviceGraph(memoryCardSlots: MemoryCardSlotConfiguration.Empty);

        graph.MemoryCardSlots.Should().Be(new PsxDeviceGraph().MemoryCardSlots);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidPort_IsRejected(int port)
    {
        using var graph = new PsxDeviceGraph();

        var query = () => graph.HasMemoryCard(port);

        query.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Configuration_IsFixedForTheGraphsLife_AndDeterministicAcrossGraphs()
    {
        // The graph outlives per-segment BIOS-runtime rebuilds (it is shared), so a graph-owned immutable value is
        // what a rebuilt runtime sees; a freshly built graph with the same input reports the same state.
        var config = MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot1, "/cards/a.mcr");
        using var first = new PsxDeviceGraph(memoryCardSlots: config);
        using var second = new PsxDeviceGraph(memoryCardSlots: config);

        first.MemoryCardSlots.Should().BeSameAs(config);
        first.MemoryCardSlotSummary.Should().Be(second.MemoryCardSlotSummary);
        first.MemoryCardSlots.Should().Be(second.MemoryCardSlots);
    }

    [Fact]
    public void ConfiguredSlot_IsReportedPerPort_WithoutDisclosingPaths()
    {
        var config = MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot2, "/cards/a.mcr");
        using var graph = new PsxDeviceGraph(memoryCardSlots: config);

        graph.HasMemoryCard(0).Should().BeFalse();
        graph.HasMemoryCard(1).Should().BeTrue();
        graph.MemoryCardSlotSummary.Should().Be("slot0=Empty slot1=Inserted");
    }
}
