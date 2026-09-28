using PSXRecomp.Core.Runtime.Gte;

namespace PSXRecomp.Tests;

[Test]
public class GteRegisterBankTests
{
    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(4)] [InlineData(6)] [InlineData(12)] [InlineData(13)]
    [InlineData(14)] [InlineData(20)] [InlineData(21)] [InlineData(22)] [InlineData(23)] [InlineData(24)]
    [InlineData(25)] [InlineData(26)] [InlineData(27)] [InlineData(30)]
    public void PlainDataRegisters_RoundTripFull32Bits(int register)
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(register, 0x8765_4321);
        gte.ReadDataRegister(register).Should().Be(0x8765_4321);
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(5)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void SignedHalfwordDataRegisters_ReadSignExtended(int register)
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(register, 0x1234_8001);
        gte.ReadDataRegister(register).Should().Be(0xFFFF_8001);
        gte.WriteDataRegister(register, 0xFFFF_7FFF);
        gte.ReadDataRegister(register).Should().Be(0x0000_7FFF);
    }

    [Theory]
    [InlineData(7)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public void UnsignedHalfwordDataRegisters_ReadZeroExtended(int register)
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(register, 0x1234_8001);
        gte.ReadDataRegister(register).Should().Be(0x0000_8001);
    }

    [Fact]
    public void SxypWrite_PushesScreenXyFifo_AndReadMirrorsSxy2()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(12, 0xA);
        gte.WriteDataRegister(13, 0xB);
        gte.WriteDataRegister(14, 0xC);

        gte.WriteDataRegister(15, 0xD);

        gte.ReadDataRegister(12).Should().Be(0xBu);
        gte.ReadDataRegister(13).Should().Be(0xCu);
        gte.ReadDataRegister(14).Should().Be(0xDu);
        gte.ReadDataRegister(15).Should().Be(0xDu);
    }

    [Fact]
    public void DirectSxy2Write_DoesNotPushFifo()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(13, 0xB);
        gte.WriteDataRegister(14, 0xC);
        gte.ReadDataRegister(13).Should().Be(0xBu);
        gte.ReadDataRegister(15).Should().Be(0xCu);
    }

    [Fact]
    public void IrgbWrite_ExpandsTo_Ir1Ir2Ir3()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(28, (0x1Fu << 10) | (0x10u << 5) | 0x01u);
        gte.ReadDataRegister(9).Should().Be(0x0080u);
        gte.ReadDataRegister(10).Should().Be(0x0800u);
        gte.ReadDataRegister(11).Should().Be(0x0F80u);
    }

    [Fact]
    public void OrgbAndIrgbRead_PackIrSaturatedTo5Bits()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(9, 0xFFFF_8000);  // negative -> 0
        gte.WriteDataRegister(10, 0x0000_0100); // 0x100/0x80 = 2
        gte.WriteDataRegister(11, 0x0000_7FFF); // saturates to 0x1F
        uint expected = (0x1Fu << 10) | (2u << 5);
        gte.ReadDataRegister(29).Should().Be(expected);
        gte.ReadDataRegister(28).Should().Be(expected);
    }

    [Fact]
    public void OrgbWrite_IsIgnored()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(29, 0xFFFF_FFFF);
        gte.ReadDataRegister(29).Should().Be(0u);
        gte.ReadDataRegister(9).Should().Be(0u);
    }

    [Theory]
    [InlineData(0x0000_0000u, 32u)]
    [InlineData(0xFFFF_FFFFu, 32u)]
    [InlineData(0x0000_0001u, 31u)]
    [InlineData(0x8000_0000u, 1u)]
    [InlineData(0x7FFF_FFFFu, 1u)]
    [InlineData(0xFFF0_0000u, 12u)]
    [InlineData(0x000F_FFFFu, 12u)]
    public void Lzcr_CountsLeadingBitsEqualToLzcsSign(uint lzcs, uint expected)
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(30, lzcs);
        gte.ReadDataRegister(31).Should().Be(expected);
    }

    [Fact]
    public void LzcrWrite_IsIgnored()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(30, 0x0000_0001);
        gte.WriteDataRegister(31, 5);
        gte.ReadDataRegister(31).Should().Be(31u);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(5)] [InlineData(7)] [InlineData(11)] [InlineData(13)]
    [InlineData(19)] [InlineData(21)] [InlineData(24)] [InlineData(25)] [InlineData(28)]
    public void PlainControlRegisters_RoundTripFull32Bits(int register)
    {
        var gte = new GteRegisterBank();
        gte.WriteControlRegister(register, 0x8765_4321);
        gte.ReadControlRegister(register).Should().Be(0x8765_4321);
    }

    [Theory]
    [InlineData(4)] [InlineData(12)] [InlineData(20)] [InlineData(26)] [InlineData(27)] [InlineData(29)] [InlineData(30)]
    public void HalfwordControlRegisters_ReadSignExtended(int register)
    {
        var gte = new GteRegisterBank();
        gte.WriteControlRegister(register, 0x1234_8001);
        gte.ReadControlRegister(register).Should().Be(0xFFFF_8001);
        gte.WriteControlRegister(register, 0x0000_7FFF);
        gte.ReadControlRegister(register).Should().Be(0x0000_7FFF);
    }

    [Fact]
    public void Flag_LowBitsAreZero_AndBit31SummarizesErrorBits()
    {
        var gte = new GteRegisterBank();

        gte.WriteControlRegister(31, 0x0000_0FFF);
        gte.ReadControlRegister(31).Should().Be(0u);

        gte.WriteControlRegister(31, 0x8000_0000); // bit 31 is not stored
        gte.ReadControlRegister(31).Should().Be(0u);

        gte.WriteControlRegister(31, 0x0000_1000); // bit 12 is not an error bit
        gte.ReadControlRegister(31).Should().Be(0x0000_1000u);

        gte.WriteControlRegister(31, 0x0007_8000); // bits 15-18 are error bits
        gte.ReadControlRegister(31).Should().Be(0x8007_8000u);

        gte.WriteControlRegister(31, 0x0078_0000); // bits 19-22 are not error bits
        gte.ReadControlRegister(31).Should().Be(0x0078_0000u);

        gte.WriteControlRegister(31, 0x4000_0000); // bit 30 is an error bit
        gte.ReadControlRegister(31).Should().Be(0xC000_0000u);
    }

    [Fact]
    public void Reset_ClearsEveryRegister()
    {
        var gte = new GteRegisterBank();
        for (int i = 0; i < GteRegisterBank.RegisterCount; i++)
        {
            gte.WriteDataRegister(i, 0xFFFF_FFFF);
            gte.WriteControlRegister(i, 0xFFFF_FFFF);
        }

        gte.Reset();

        for (int i = 0; i < GteRegisterBank.RegisterCount; i++)
        {
            uint expectedData = i == 31 ? 32u : 0u; // LZCR of LZCS=0
            gte.ReadDataRegister(i).Should().Be(expectedData, $"data register {i}");
            gte.ReadControlRegister(i).Should().Be(0u, $"control register {i}");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public void OutOfRangeRegister_Throws(int register)
    {
        var gte = new GteRegisterBank();
        ((Action)(() => gte.ReadDataRegister(register))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => gte.WriteDataRegister(register, 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => gte.ReadControlRegister(register))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => gte.WriteControlRegister(register, 0))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ExecuteCommand_IsExplicitlyUnsupported()
    {
        var gte = new GteRegisterBank();
        ((Action)(() => gte.ExecuteCommand(0x01, false, false))).Should().Throw<NotSupportedException>();
        gte.HasPendingData.Should().BeFalse();
    }
}
