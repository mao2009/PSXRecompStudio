using PSXRecomp.Core;

namespace PSXRecomp.Tests;

/// <summary>
/// Managed -> native -> Rust smoke tests for the coexistence substrate
/// (Issue #473).
/// </summary>
/// <remarks>
/// These exercise the real staged artifact: the assertions can only pass if
/// cargo actually built the Rust <c>staticlib</c>, CMake linked it into
/// <c>PSXRecomp.Native</c>, the library was staged next to the test assembly,
/// and P/Invoke resolved the exported symbols. There is no managed fallback or
/// mock — a broken step in that chain fails here rather than silently degrading.
/// </remarks>
[Test]
public class RustAbiSmokeTests
{
    private const uint RoundTripSentinel = 0x5A5A5A5Au;

    [Fact]
    public void AbiVersion_ReturnsTheContractVersion()
    {
        NativeInterop.PSXRecompRust_AbiVersion().Should().Be(1u);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x1234_5678u)]
    [InlineData(RoundTripSentinel)]
    [InlineData(0x7FFF_FFFFu)]
    [InlineData(0x8000_0000u)]
    [InlineData(uint.MaxValue)]
    public void RoundTrip_AppliesTheSentinel(uint value)
    {
        RoundTrip(value).Should().Be(value ^ RoundTripSentinel);
    }

    [Fact]
    public void RoundTrip_IsAnInvolution()
    {
        RoundTrip(RoundTrip(0x1234_5678u)).Should().Be(0x1234_5678u);
    }

    [Fact]
    public void RoundTrip_WithNullOutParameter_ReportsNullArgumentAndWritesNothing()
    {
        unsafe
        {
            NativeInterop.PSXRecompRust_RoundTrip(0x1234_5678u, null)
                .Should().Be(NativeInterop.RustErrNullArgument);
        }
    }

    private static uint RoundTrip(uint value)
    {
        uint result = 0;
        int status;
        unsafe
        {
            status = NativeInterop.PSXRecompRust_RoundTrip(value, &result);
        }

        status.Should().Be(NativeInterop.RustOk);
        return result;
    }
}
