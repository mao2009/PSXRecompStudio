using PSXRecomp.Core.MemoryCard;

namespace PSXRecomp.Tests.MemoryCard;

/// <summary>
/// The in-memory half of the memory-card contract (Issue #22): size validation,
/// byte preservation, range-checked access, and dirty tracking. Nothing here
/// touches a file — persistence is covered by the storage adapter's own tests.
/// </summary>
[Test]
public sealed class MemoryCardImageTests
{
    /// <summary>Required case 1: a valid 128 KiB raw image loads.</summary>
    [Fact]
    public void FromBytes_AcceptsExactly128KiB()
    {
        var raw = SyntheticCards.ExternalEmulatorCard();

        var image = MemoryCardImage.FromBytes(raw);

        image.AsSpan().Length.Should().Be(MemoryCardImage.SizeInBytes);
        image.IsDirty.Should().BeFalse("a freshly loaded card has no unsaved change");
    }

    /// <summary>Required case 2: an undersized image is rejected rather than padded.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(MemoryCardImage.SizeInBytes - 1)]
    [InlineData(MemoryCardImage.BlockSize)]
    public void FromBytes_RejectsUndersizedImage(int length)
    {
        var act = () => MemoryCardImage.FromBytes(new byte[length]);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{MemoryCardImage.SizeInBytes}*");
    }

    /// <summary>Required case 2: an oversized image is rejected rather than truncated.</summary>
    [Theory]
    [InlineData(MemoryCardImage.SizeInBytes + 1)]
    [InlineData(MemoryCardImage.SizeInBytes * 2)]
    public void FromBytes_RejectsOversizedImage(int length)
    {
        var act = () => MemoryCardImage.FromBytes(new byte[length]);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Required case 3: loading preserves every byte, including a block-0
    /// directory an external tool wrote. Nothing is normalized on load.
    /// </summary>
    [Fact]
    public void FromBytes_PreservesEveryByte()
    {
        var raw = SyntheticCards.ExternalEmulatorCard();

        var image = MemoryCardImage.FromBytes(raw);

        image.ToArray().Should().Equal(raw);
    }

    /// <summary>A loaded image owns its bytes; mutating the source array cannot reach it.</summary>
    [Fact]
    public void FromBytes_CopiesTheSourceBuffer()
    {
        var raw = SyntheticCards.ExternalEmulatorCard();
        var image = MemoryCardImage.FromBytes(raw);

        raw[0] ^= 0xFF;

        image.AsSpan()[0].Should().NotBe(raw[0]);
    }

    /// <summary>Required case 4: a blank card is exactly one standard card in size.</summary>
    [Fact]
    public void CreateBlank_ProducesExactly128KiB()
    {
        MemoryCardImage.CreateBlank().AsSpan().Length.Should().Be(131072);
        MemoryCardImage.SizeInBytes.Should().Be(MemoryCardImage.BlockCount * MemoryCardImage.BlockSize);
        MemoryCardImage.BlockSize.Should().Be(MemoryCardImage.FramesPerBlock * MemoryCardImage.FrameSize);
    }

    /// <summary>A blank card starts clean; creating one is not an unsaved edit.</summary>
    [Fact]
    public void CreateBlank_StartsClean() => MemoryCardImage.CreateBlank().IsDirty.Should().BeFalse();

    /// <summary>Required case 6: an in-range write is readable back.</summary>
    [Fact]
    public void ReadAndWrite_RoundTripInRange()
    {
        var image = MemoryCardImage.CreateBlank();
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var offset = MemoryCardImage.BlockOffset(3);

        image.Write(offset, payload);

        var read = new byte[payload.Length];
        image.Read(offset, read);
        read.Should().Equal(payload);
    }

    /// <summary>An in-range access touching the very last byte is allowed.</summary>
    [Fact]
    public void ReadAndWrite_AllowTheFinalByte()
    {
        var image = MemoryCardImage.CreateBlank();
        var last = MemoryCardImage.SizeInBytes - 1;

        image.Write(last, [0x5A]);

        var read = new byte[1];
        image.Read(last, read);
        read[0].Should().Be(0x5A);
    }

    /// <summary>Required case 7: a read that would leave the card fails.</summary>
    [Theory]
    [InlineData(MemoryCardImage.SizeInBytes, 1)]
    [InlineData(MemoryCardImage.SizeInBytes - 3, 4)]
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 1)]
    public void Read_RejectsRangeLeavingCard(int offset, int length)
    {
        var image = MemoryCardImage.CreateBlank();
        var buffer = new byte[length];

        var act = () => image.Read(offset, buffer);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Required case 7: a write that would leave the card fails and changes nothing.</summary>
    [Theory]
    [InlineData(MemoryCardImage.SizeInBytes, 1)]
    [InlineData(MemoryCardImage.SizeInBytes - 3, 4)]
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 1)]
    public void Write_RejectsRangeLeavingCard(int offset, int length)
    {
        var image = MemoryCardImage.CreateBlank();
        var before = image.ToArray();

        var act = () => image.Write(offset, new byte[length]);

        act.Should().Throw<ArgumentOutOfRangeException>();
        image.ToArray().Should().Equal(before, "a rejected write must not partially apply");
        image.IsDirty.Should().BeFalse();
    }

    /// <summary>Required case 8: a write that changes content marks the card dirty.</summary>
    [Fact]
    public void Write_MarksTheCardDirty()
    {
        var image = MemoryCardImage.CreateBlank();

        image.Write(MemoryCardImage.BlockOffset(1), [0x01]);

        image.IsDirty.Should().BeTrue();
    }

    /// <summary>Required case 8: a write that changes nothing does not manufacture a save.</summary>
    [Fact]
    public void Write_OfIdenticalContentLeavesTheCardClean()
    {
        var image = MemoryCardImage.CreateBlank();
        var offset = MemoryCardImage.BlockOffset(1);
        var existing = new byte[8];
        image.Read(offset, existing);

        image.Write(offset, existing);

        image.IsDirty.Should().BeFalse();
    }

    /// <summary>Required case 8: persisting clears the dirty flag.</summary>
    [Fact]
    public void MarkPersisted_ClearsTheDirtyFlag()
    {
        var image = MemoryCardImage.CreateBlank();
        image.Write(0, [0xFF]);

        image.MarkPersisted();

        image.IsDirty.Should().BeFalse();
    }

    /// <summary>A read never marks the card dirty.</summary>
    [Fact]
    public void Read_DoesNotMarkTheCardDirty()
    {
        var image = MemoryCardImage.CreateBlank();

        image.Read(0, new byte[MemoryCardImage.FrameSize]);

        image.IsDirty.Should().BeFalse();
    }

    /// <summary>
    /// Required case 14: writing one range leaves every other byte of a non-empty
    /// card untouched — including the directory block that identifies the saves.
    /// </summary>
    [Fact]
    public void Write_PreservesEveryByteOutsideTheWrittenRange()
    {
        var raw = SyntheticCards.ExternalEmulatorCard();
        var image = MemoryCardImage.FromBytes(raw);
        var offset = MemoryCardImage.BlockOffset(9);
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        image.Write(offset, payload);

        var after = image.ToArray();
        after.AsSpan(0, offset).ToArray().Should().Equal(raw.AsSpan(0, offset).ToArray());
        var tail = offset + payload.Length;
        after.AsSpan(tail).ToArray().Should().Equal(raw.AsSpan(tail).ToArray());
        after.AsSpan(offset, payload.Length).ToArray().Should().Equal(payload);
    }

    /// <summary>Block and frame offsets address the card the specification describes.</summary>
    [Fact]
    public void BlockAndFrameOffsets_FollowTheCardGeometry()
    {
        MemoryCardImage.BlockOffset(0).Should().Be(0);
        MemoryCardImage.BlockOffset(15).Should().Be(15 * 8192);
        MemoryCardImage.FrameOffset(0).Should().Be(0);
        MemoryCardImage.FrameOffset(64).Should().Be(MemoryCardImage.BlockOffset(1));

        var outOfRangeBlock = () => MemoryCardImage.BlockOffset(MemoryCardImage.BlockCount);
        outOfRangeBlock.Should().Throw<ArgumentOutOfRangeException>();

        var outOfRangeFrame = () => MemoryCardImage.FrameOffset(MemoryCardImage.BlockCount * MemoryCardImage.FramesPerBlock);
        outOfRangeFrame.Should().Throw<ArgumentOutOfRangeException>();
    }
}
