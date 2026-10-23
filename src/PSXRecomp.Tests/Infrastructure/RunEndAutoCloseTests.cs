using System;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #830: the game window closes by itself after its grace period, and not before.</summary>
[Test]
public sealed class RunEndAutoCloseTests
{
    [Fact]
    public void TheWindowDefault_IsThirtySeconds()
    {
        RunEndAutoClose.DefaultGracePeriod.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void BeforeTheRunEnds_NothingIsDue()
    {
        var timer = new RunEndAutoClose(TimeSpan.FromSeconds(30));

        timer.IsStarted.Should().BeFalse();
        timer.IsDue(TimeSpan.FromHours(10)).Should().BeFalse("the grace period starts only when the run ends");
    }

    [Fact]
    public void NotDueBeforeTheGraceEnds_AndDueAtTheGraceEnd()
    {
        var timer = new RunEndAutoClose(TimeSpan.FromSeconds(30));
        timer.Start(TimeSpan.FromSeconds(100));

        timer.IsDue(TimeSpan.FromSeconds(129.9)).Should().BeFalse();
        timer.IsDue(TimeSpan.FromSeconds(130)).Should().BeTrue("the window closes once the grace period has passed");
        timer.IsDue(TimeSpan.FromSeconds(500)).Should().BeTrue();
    }

    [Fact]
    public void StartingAgain_KeepsTheFirstDeadline()
    {
        var timer = new RunEndAutoClose(TimeSpan.FromSeconds(30));
        timer.Start(TimeSpan.FromSeconds(100));

        timer.Start(TimeSpan.FromSeconds(120));

        timer.IsDue(TimeSpan.FromSeconds(130)).Should().BeTrue("a second start does not extend the grace period");
    }

    [Fact]
    public void ANegativeGrace_IsRefused()
    {
        Action create = () => new RunEndAutoClose(TimeSpan.FromSeconds(-1));

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
