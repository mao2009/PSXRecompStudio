using System.Threading.Tasks;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #630: the live frame handoff keeps only the latest frame and never blocks the publisher.</summary>
[Test]
public sealed class LiveFrameSlotTests
{
    [Fact]
    public void BeforeAnyPublish_NothingIsNewer()
    {
        var slot = new LiveFrameSlot();

        slot.TryGetNewer(0, out _).Should().BeFalse();
    }

    [Fact]
    public void UnreadFrames_AreReplaced_AndTheReaderSeesTheGapAsSkippedFrames()
    {
        var slot = new LiveFrameSlot();
        slot.Publish(1, 1, [1, 0, 0, 255]);
        slot.Publish(1, 1, [2, 0, 0, 255]);
        slot.Publish(1, 1, [3, 0, 0, 255]);

        slot.TryGetNewer(0, out var frame).Should().BeTrue();
        frame!.Sequence.Should().Be(3);
        frame.Bgra32[0].Should().Be(3);
        slot.TryGetNewer(3, out _).Should().BeFalse();
    }

    [Fact]
    public void Publish_RejectsALengthThatDoesNotMatchTheSize()
    {
        var slot = new LiveFrameSlot();

        var act = () => slot.Publish(2, 2, new byte[15]);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void Publish_RejectsNonPositiveDimensions(int width, int height)
    {
        var slot = new LiveFrameSlot();

        var act = () => slot.Publish(width, height, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Publish_RejectsAFrameTooLargeForAnArray()
    {
        var slot = new LiveFrameSlot();

        var act = () => slot.Publish(int.MaxValue, int.MaxValue, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryGetNewer_WithNothingNewer_ReturnsNullFrame()
    {
        var slot = new LiveFrameSlot();
        slot.Publish(1, 1, [0, 0, 0, 255]);

        slot.TryGetNewer(1, out var frame).Should().BeFalse();
        frame.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentPublisherAndReader_SeeMonotonicSequences_AndTheFinalFrame()
    {
        const int publishes = 20_000;
        var slot = new LiveFrameSlot();
        var publisher = Task.Run(() =>
        {
            for (var i = 1; i <= publishes; i++)
            {
                slot.Publish(1, 1, [0, 0, 0, 255]);
            }
        });

        long last = 0;
        // The reader also stops once the publisher has finished, so a publisher that faults cannot leave it spinning.
        // Completion is sampled before the read: a frame published before completion is then always read.
        var reader = Task.Run(() =>
        {
            while (last < publishes)
            {
                var done = publisher.IsCompleted;
                if (slot.TryGetNewer(last, out var frame))
                {
                    frame!.Sequence.Should().BeGreaterThan(last);
                    last = frame.Sequence;
                }
                else if (done)
                {
                    break;
                }
            }
        });

        await Task.WhenAll(publisher, reader).WaitAsync(TimeSpan.FromSeconds(30));
        last.Should().Be(publishes);
    }
}
