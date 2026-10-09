using PSXRecomp.Core.Runtime.Gte;

namespace PSXRecomp.Tests;

/// <summary>
/// Issue #447: <see cref="GteRegisterBank.ExecuteCommand"/> applies the arithmetic kernels to the
/// architectural registers (inputs read from, results and FIFO pushes written to, the same bank), and
/// refuses every other command without touching a register.
/// </summary>
[Test]
public class GteCommandDispatchTests
{
    private const int Vxy0 = 0, Vz0 = 1, Otz = 7, Ir0 = 8, Ir1 = 9, Ir2 = 10, Ir3 = 11;
    private const int Sxy0 = 12, Sxy1 = 13, Sxy2 = 14, Sz0 = 16, Sz1 = 17, Sz2 = 18, Sz3 = 19;
    private const int Mac0 = 24, Mac1 = 25, Mac2 = 26, Mac3 = 27;
    private const int Flag = 31;
    private const uint Sf = 1u << 19, Lm = 1u << 10;

    private static GteRegisterBank RtpsScene(short vx, short vy, short vz)
    {
        var gte = new GteRegisterBank();
        gte.WriteControlRegister(0, 0x0000_1000); // RT11=1.0, RT12=0
        gte.WriteControlRegister(2, 0x0000_1000); // RT22=1.0, RT23=0
        gte.WriteControlRegister(4, 0x1000);      // RT33=1.0
        gte.WriteControlRegister(7, 0x200);       // TRZ
        gte.WriteControlRegister(24, 160u << 16); // OFX = 160.0
        gte.WriteControlRegister(25, 120u << 16); // OFY = 120.0
        gte.WriteControlRegister(26, 0x100);      // H
        gte.WriteControlRegister(27, 0xFFFF_FF00); // DQA = -256
        gte.WriteControlRegister(28, 0x0140_0000); // DQB
        gte.WriteDataRegister(Vxy0, (ushort)vx | ((uint)(ushort)vy << 16));
        gte.WriteDataRegister(Vz0, (ushort)vz);
        gte.WriteDataRegister(Sz3, 0x1234);
        gte.WriteDataRegister(Sxy2, 0x0005_0006);
        return gte;
    }

    [Fact]
    public void Rtps_WritesTheKernelResult_AndPushesBothScreenFifos()
    {
        var gte = RtpsScene(100, -50, 0x300);
        var expected = GteRtpsKernel.Execute(new GteRtpsInput(
            0x1000, 0, 0, 0, 0x1000, 0, 0, 0, 0x1000, 0, 0, 0x200, 100, -50, 0x300,
            0x100, 160 << 16, 120 << 16, -256, 0x0140_0000), sf: true, lm: false);

        gte.ExecuteCommand(0x0018_0001 | Sf).Should().BeTrue(); // RTPS sf=1
        gte.CommandsExecuted.Should().Be(1UL);

        gte.ReadDataRegister(Mac0).Should().Be((uint)expected.Mac0);
        gte.ReadDataRegister(Mac1).Should().Be((uint)expected.Mac1);
        gte.ReadDataRegister(Mac2).Should().Be((uint)expected.Mac2);
        gte.ReadDataRegister(Mac3).Should().Be((uint)expected.Mac3);
        gte.ReadDataRegister(Ir0).Should().Be((uint)(int)expected.Ir0);
        gte.ReadDataRegister(Ir1).Should().Be((uint)(int)expected.Ir1);
        gte.ReadDataRegister(Ir2).Should().Be((uint)(int)expected.Ir2);
        gte.ReadDataRegister(Ir3).Should().Be((uint)(int)expected.Ir3);
        gte.ReadDataRegister(Sz2).Should().Be(0x1234u, "the old SZ3 moved down the FIFO");
        gte.ReadDataRegister(Sz3).Should().Be(expected.Sz);
        gte.ReadDataRegister(Sxy1).Should().Be(0x0005_0006u, "the old SXY2 moved down the FIFO");
        gte.ReadDataRegister(Sxy2).Should().Be((ushort)expected.Sx | ((uint)(ushort)expected.Sy << 16));
        gte.ReadControlRegister(Flag).Should().Be(expected.Flag);

        // Hand-checked: Z = 0x300 + 0x200; SX ~ 160 + 100*H/Z = 180, SY ~ 120 - 50*H/Z = 110 (+-1 for the divider).
        expected.Sz.Should().Be((ushort)0x500);
        ((short)gte.ReadDataRegister(Sxy2)).Should().BeInRange((short)179, (short)180);
        ((short)(gte.ReadDataRegister(Sxy2) >> 16)).Should().BeInRange((short)109, (short)110);
    }

    [Fact]
    public void Rtps_HonoursSfAndLm_FromTheCommandWord()
    {
        var gte = RtpsScene(-0x7000, 0x10, 0x10);
        gte.ExecuteCommand(0x01 | Sf | Lm).Should().BeTrue();

        // lm=1 clamps the negative IR1 to 0 and flags it (FLAG.24); sf=1 keeps MAC1 in 1.0 units.
        gte.ReadDataRegister(Ir1).Should().Be(0u);
        (gte.ReadControlRegister(Flag) & GteRtpsKernel.FlagIr1Saturated).Should().NotBe(0u);
        ((int)gte.ReadDataRegister(Mac1)).Should().Be(-0x7000);

        var noShift = RtpsScene(1, 0, 0);
        noShift.ExecuteCommand(0x01).Should().BeTrue(); // sf=0: MAC1 = 1 * 0x1000 unshifted
        ((int)noShift.ReadDataRegister(Mac1)).Should().Be(0x1000);
    }

    [Fact]
    public void Nclip_ReadsTheScreenFifo_AndWritesMac0AndFlag()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(Sxy0, 0x0000_0000);  // (0,0)
        gte.WriteDataRegister(Sxy1, 0x0000_000A);  // (10,0)
        gte.WriteDataRegister(Sxy2, 0x000A_0000);  // (0,10)
        gte.WriteControlRegister(Flag, 0x7000_0000); // a command starts with FLAG = 0

        gte.ExecuteCommand(0x0140_0006).Should().BeTrue();

        ((int)gte.ReadDataRegister(Mac0)).Should().Be(100);
        gte.ReadControlRegister(Flag).Should().Be(0u);
    }

    [Fact]
    public void Avsz3AndAvsz4_AverageTheZFifo_IntoOtz()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(Sz0, 400);
        gte.WriteDataRegister(Sz1, 100);
        gte.WriteDataRegister(Sz2, 200);
        gte.WriteDataRegister(Sz3, 300);
        gte.WriteControlRegister(29, 0x555); // ZSF3 ~ 1/3
        gte.WriteControlRegister(30, 0x400); // ZSF4 = 1/4

        gte.ExecuteCommand(0x0158_002D).Should().BeTrue();
        ((int)gte.ReadDataRegister(Mac0)).Should().Be(0x555 * 600);
        gte.ReadDataRegister(Otz).Should().Be((uint)(0x555 * 600 >> 12));

        gte.ExecuteCommand(0x0168_002E).Should().BeTrue();
        gte.ReadDataRegister(Otz).Should().Be(250u);
        gte.ReadControlRegister(Flag).Should().Be(0u);
    }

    [Fact]
    public void Avsz3_NegativeAverage_SaturatesOtzAndSetsTheErrorFlag()
    {
        var gte = new GteRegisterBank();
        gte.WriteDataRegister(Sz1, 0x1000);
        gte.WriteControlRegister(29, 0xFFFF_F000); // ZSF3 = -4096

        gte.ExecuteCommand(0x2D).Should().BeTrue();

        gte.ReadDataRegister(Otz).Should().Be(0u);
        gte.ReadControlRegister(Flag).Should().Be(0x8004_0000u);
    }

    [Theory]
    [InlineData(0x00u)] // no command
    [InlineData(0x30u)] // RTPT: not implemented yet
    [InlineData(0x3Fu)]
    public void AnUnimplementedCommand_FailsClosed_WithoutTouchingARegister(uint opcode)
    {
        var gte = RtpsScene(1, 2, 3);
        var before = Enumerable.Range(0, 32).Select(r => (gte.ReadDataRegister(r), gte.ReadControlRegister(r))).ToArray();

        gte.ExecuteCommand(0x0180_0000 | opcode).Should().BeFalse();

        gte.LastUnsupportedCommand.Should().Be(0x0180_0000 | opcode);
        gte.CommandsExecuted.Should().Be(0UL);
        Enumerable.Range(0, 32).Select(r => (gte.ReadDataRegister(r), gte.ReadControlRegister(r))).Should().Equal(before);
    }
}
