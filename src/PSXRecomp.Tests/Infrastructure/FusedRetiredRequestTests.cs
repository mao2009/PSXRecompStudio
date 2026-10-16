using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Infrastructure;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #768: the fused report + MMIO request is validated whole, before the host performs any part of it.</summary>
[Test]
public sealed class FusedRetiredRequestTests
{
    [Theory]
    [InlineData("12 R 4 528488468", "12", false, "4 528488468")]
    [InlineData("1 W 2 528488468 65535", "1", true, "2 528488468 65535")]
    [InlineData("18446744073709551615 R 1 0", "18446744073709551615", false, "1 0")]
    public void WellFormedRequests_AreSplitIntoTheReportAndTheAccess(string fields, string retired, bool isWrite, string access)
    {
        FusedRetiredRequest.TryParse(fields, out var r, out var w, out var a).Should().BeTrue();
        (r, w, a).Should().Be((retired, isWrite, access));
    }

    [Theory]
    [InlineData("12 R 4")]                       // read without an address
    [InlineData("12 R 4 1 2")]                   // read carrying a value
    [InlineData("12 W 4 1")]                     // write without a value
    [InlineData("12 W 4 1 2 3")]                 // extra field
    [InlineData("12 X 4 1")]                     // neither read nor write
    [InlineData("12 r 4 1")]                     // case matters
    [InlineData("0x1 R 4 1")]                    // count is a plain decimal
    [InlineData("-1 R 4 1")]
    [InlineData("12 R 3 1")]                     // width is 1, 2 or 4
    [InlineData("12 R 4 4294967296")]            // physical address is 32-bit
    [InlineData("12 W 4 1 4294967296")]          // value is 32-bit
    [InlineData("12  R 4 1")]                    // no empty fields
    [InlineData("")]
    public void MalformedRequests_AreRejectedBeforeAnyEffect(string fields) =>
        FusedRetiredRequest.TryParse(fields, out _, out _, out _).Should().BeFalse();
}
