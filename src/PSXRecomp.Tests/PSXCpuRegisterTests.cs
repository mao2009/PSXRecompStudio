using PSXRecomp.Core;

namespace PSXRecomp.Tests;

[Test]
public class PSXCpuRegisterTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();

    public void Dispose()
    {
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GPR_AllRegisters_InitialValue_IsZero()
    {
        for (int i = 0; i < PSXCoreWrapper.GprCount; i++)
        {
            _core.GetGpr(i).Should().Be(0, $"GPR[{i}] should be 0 initially");
        }
    }

    [Fact]
    public void PC_InitialValue_IsZero()
    {
        _core.Pc.Should().Be(0);
    }

    [Fact]
    public void HI_InitialValue_IsZero()
    {
        _core.Hi.Should().Be(0);
    }

    [Fact]
    public void LO_InitialValue_IsZero()
    {
        _core.Lo.Should().Be(0);
    }

    [Fact]
    public void GPR_SetGet_RoundTrip()
    {
        _core.SetGpr(1, 0xDEADBEEF);
        _core.GetGpr(1).Should().Be(0xDEADBEEF);
    }

    [Fact]
    public void GPR_Zero_AlwaysZero()
    {
        _core.SetGpr(0, 0x12345678);
        _core.GetGpr(0).Should().Be(0);
    }

    [Fact]
    public void GPR_OutOfRange_ThrowsArgumentOutOfRange()
    {
        var act = () => _core.GetGpr(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PC_SetGet_RoundTrip()
    {
        _core.Pc = 0x80030000;
        _core.Pc.Should().Be(0x80030000);
    }

    [Fact]
    public void HI_SetGet_RoundTrip()
    {
        _core.Hi = 0xAABBCCDD;
        _core.Hi.Should().Be(0xAABBCCDD);
    }

    [Fact]
    public void LO_SetGet_RoundTrip()
    {
        _core.Lo = 0x11223344;
        _core.Lo.Should().Be(0x11223344);
    }

    [Fact]
    public void Reset_ClearsAllRegisters()
    {
        _core.SetGpr(5, 0x1234);
        _core.Pc = 0x80000000;
        _core.Hi = 0xAAAAAAAA;
        _core.Lo = 0xBBBBBBBB;

        _core.Reset();

        _core.GetGpr(5).Should().Be(0);
        _core.Pc.Should().Be(0);
        _core.Hi.Should().Be(0);
        _core.Lo.Should().Be(0);
    }
}
