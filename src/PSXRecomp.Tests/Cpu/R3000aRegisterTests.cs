using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aRegisterTests
{
    [Fact]
    public void Register_Has32Members()
    {
        Enum.GetValues<R3000aRegister>().Length.Should().Be(32);
    }

    [Fact]
    public void Register_UnderlyingType_IsByte()
    {
        Enum.GetUnderlyingType(typeof(R3000aRegister)).Should().Be(typeof(byte));
    }

    [Theory]
    [InlineData(nameof(R3000aRegister.Zero), 0)]
    [InlineData(nameof(R3000aRegister.At), 1)]
    [InlineData(nameof(R3000aRegister.V0), 2)]
    [InlineData(nameof(R3000aRegister.A0), 4)]
    [InlineData(nameof(R3000aRegister.T0), 8)]
    [InlineData(nameof(R3000aRegister.T7), 15)]
    [InlineData(nameof(R3000aRegister.S0), 16)]
    [InlineData(nameof(R3000aRegister.S7), 23)]
    [InlineData(nameof(R3000aRegister.T8), 24)]
    [InlineData(nameof(R3000aRegister.K0), 26)]
    [InlineData(nameof(R3000aRegister.Gp), 28)]
    [InlineData(nameof(R3000aRegister.Sp), 29)]
    [InlineData(nameof(R3000aRegister.Fp), 30)]
    [InlineData(nameof(R3000aRegister.Ra), 31)]
    public void Register_Number_MatchesMipsConvention(string name, int expectedNumber)
    {
        var parsed = Enum.Parse<R3000aRegister>(name);
        ((int)parsed).Should().Be(expectedNumber);
    }
}
