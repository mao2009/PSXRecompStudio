using PSXRecomp.Core.MemoryCard;

namespace PSXRecomp.Tests.MemoryCard;

/// <summary>
/// The Slot 1 / Slot 2 configuration model (Issue #22): two independent slots,
/// each either empty or holding one card file, with the shared-card and
/// per-game-card strategies expressed purely through the paths chosen.
/// </summary>
[Test]
public sealed class MemoryCardSlotConfigurationTests
{
    private const string Slot1Card = "/cards/slot1.mcr";
    private const string Slot2Card = "/cards/slot2.mcr";

    /// <summary>The console starts with no card in either slot.</summary>
    [Fact]
    public void Empty_HasNoCardInEitherSlot()
    {
        MemoryCardSlotConfiguration.Empty.HasCard(MemoryCardSlot.Slot1).Should().BeFalse();
        MemoryCardSlotConfiguration.Empty.HasCard(MemoryCardSlot.Slot2).Should().BeFalse();
        MemoryCardSlotConfiguration.Empty[MemoryCardSlot.Slot1].Should().BeNull();
        MemoryCardSlotConfiguration.Empty[MemoryCardSlot.Slot2].Should().BeNull();
    }

    /// <summary>Required case 12: configuring one slot never disturbs the other.</summary>
    [Fact]
    public void WithCard_ChangesOnlyTheAddressedSlot()
    {
        var configuration = MemoryCardSlotConfiguration.Empty
            .WithCard(MemoryCardSlot.Slot1, Slot1Card)
            .WithCard(MemoryCardSlot.Slot2, Slot2Card);

        configuration[MemoryCardSlot.Slot1].Should().Be(Slot1Card);
        configuration[MemoryCardSlot.Slot2].Should().Be(Slot2Card);

        var ejected = configuration.WithoutCard(MemoryCardSlot.Slot1);

        ejected.HasCard(MemoryCardSlot.Slot1).Should().BeFalse();
        ejected[MemoryCardSlot.Slot2].Should().Be(Slot2Card, "ejecting slot 1 must not eject slot 2");
    }

    /// <summary>The record is immutable: deriving a new configuration leaves the old one alone.</summary>
    [Fact]
    public void WithCard_LeavesTheOriginalConfigurationUnchanged()
    {
        var original = MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot1, Slot1Card);

        original.WithCard(MemoryCardSlot.Slot1, Slot2Card);

        original[MemoryCardSlot.Slot1].Should().Be(Slot1Card);
    }

    /// <summary>
    /// A shared card is the same file in both slots, and a per-game card is a
    /// per-title path: both strategies are configuration, not separate types.
    /// </summary>
    [Fact]
    public void SharedAndPerGameStrategies_AreJustPathChoices()
    {
        const string Shared = "/cards/shared.mcr";
        var sharedInBothSlots = new MemoryCardSlotConfiguration(Shared, Shared);
        sharedInBothSlots[MemoryCardSlot.Slot1].Should().Be(sharedInBothSlots[MemoryCardSlot.Slot2]);

        var perGame = MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot1, "/cards/per-game/title-a.mcr");
        perGame.Should().NotBe(MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot1, "/cards/per-game/title-b.mcr"));
    }

    /// <summary>A blank path is not a way to express an empty slot; <see cref="MemoryCardSlotConfiguration.WithoutCard"/> is.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithCard_RejectsABlankPath(string path)
    {
        var act = () => MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot1, path);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>A slot value outside the two the hardware has is rejected, not silently mapped.</summary>
    [Fact]
    public void UndeclaredSlot_IsRejected()
    {
        const MemoryCardSlot Slot3 = (MemoryCardSlot)3;

        var read = () => MemoryCardSlotConfiguration.Empty[Slot3];
        read.Should().Throw<ArgumentOutOfRangeException>();

        var write = () => MemoryCardSlotConfiguration.Empty.WithCard(Slot3, Slot1Card);
        write.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// CodeRabbit round 2: direct positional construction enforces the same
    /// invariant as <see cref="MemoryCardSlotConfiguration.WithCard"/>, so
    /// <c>new(...)</c> can never produce a configuration whose whitespace path
    /// <see cref="MemoryCardSlotConfiguration.HasCard"/> would misreport as an
    /// inserted card.
    /// </summary>
    [Fact]
    public void Constructor_AcceptsNullForAnEmptySlot()
    {
        var configuration = new MemoryCardSlotConfiguration(null, null);

        configuration.HasCard(MemoryCardSlot.Slot1).Should().BeFalse();
        configuration.HasCard(MemoryCardSlot.Slot2).Should().BeFalse();
        configuration[MemoryCardSlot.Slot1].Should().BeNull();
        configuration[MemoryCardSlot.Slot2].Should().BeNull();
    }

    /// <summary>A non-blank path is accepted directly, without going through <see cref="MemoryCardSlotConfiguration.WithCard"/>.</summary>
    [Fact]
    public void Constructor_AcceptsAValidPathInEitherSlot()
    {
        var configuration = new MemoryCardSlotConfiguration(Slot1Card, Slot2Card);

        configuration[MemoryCardSlot.Slot1].Should().Be(Slot1Card);
        configuration[MemoryCardSlot.Slot2].Should().Be(Slot2Card);
    }

    /// <summary>A blank slot 1 path is rejected at construction, the same as <see cref="MemoryCardSlotConfiguration.WithCard"/> rejects it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankSlot1Path(string path)
    {
        var act = () => new MemoryCardSlotConfiguration(path, null);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>A blank slot 2 path is rejected at construction, the same as <see cref="MemoryCardSlotConfiguration.WithCard"/> rejects it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankSlot2Path(string path)
    {
        var act = () => new MemoryCardSlotConfiguration(null, path);

        act.Should().Throw<ArgumentException>();
    }
}
