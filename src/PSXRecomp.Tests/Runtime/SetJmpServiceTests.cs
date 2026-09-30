using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// A0:13 setjmp(buf) — PSX-SPX kernelbios/misc-functions. Issue #648.
[Test]
public sealed class SetJmpServiceTests
{
    private const uint Buffer = 0x00000100;

    // Every register gets a distinct value so a wrong offset or register is visible.
    private static uint[] DistinctRegisters()
    {
        var gpr = new uint[32];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = 0xA0000000u + (uint)i * 0x11u;
        }

        return gpr;
    }

    private static BiosHleRuntime CreateRuntime(RecompilerGuestMemory ram) =>
        new(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

    private static BiosCallIdentity Call(uint bufferAddress, uint[]? gpr) =>
        new(BiosCallFamily.A0, BiosHleRuntime.SetJmpFunction, arguments: [bufferAddress], guestRegisters: gpr);

    private static byte[] ReadBack(RecompilerGuestMemory ram, uint address, int length)
    {
        var bytes = new byte[length];
        new GuestMemoryReader(ram.Read8).TryRead(address, bytes).Should().BeTrue();
        return bytes;
    }

    [Fact]
    public void SetJmp_Is_Registered_At_A0_13_With_One_Argument()
    {
        BiosHleRuntime.SetJmpFunction.Should().Be(0x13);

        IBiosRuntime runtime = CreateRuntime(new RecompilerGuestMemory());
        runtime.TryGetServiceArgumentCount(BiosCallFamily.A0, 0x13, out var count).Should().BeTrue();
        count.Should().Be(1);
    }

    [Fact]
    public void SetJmp_Stores_The_Documented_0x30_Byte_Layout()
    {
        var ram = new RecompilerGuestMemory();
        var gpr = DistinctRegisters();

        var result = CreateRuntime(ram).Invoke(Call(Buffer, gpr));

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0u);
        result.Diagnostic.Should().BeNull();

        var expected = new[]
        {
            R3000aRegister.Ra, R3000aRegister.Sp, R3000aRegister.Fp,
            R3000aRegister.S0, R3000aRegister.S1, R3000aRegister.S2, R3000aRegister.S3,
            R3000aRegister.S4, R3000aRegister.S5, R3000aRegister.S6, R3000aRegister.S7,
            R3000aRegister.Gp,
        }.SelectMany(r => BitConverter.GetBytes(gpr[(int)r])).ToArray();

        expected.Should().HaveCount(0x30);
        ReadBack(ram, Buffer, 0x30).Should().Equal(expected);
    }

    [Fact]
    public void SetJmp_Does_Not_Touch_Bytes_Outside_The_Buffer_Or_Save_Other_Registers()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(Buffer - 1, 0x5A);
        ram.Write8(Buffer + 0x30, 0xA5);
        var gpr = DistinctRegisters();

        CreateRuntime(ram).Invoke(Call(Buffer, gpr)).Status.Should().Be(BiosServiceStatus.Supported);

        ram.Read8(Buffer - 1).Should().Be(0x5A);
        ram.Read8(Buffer + 0x30).Should().Be(0xA5);

        var words = ReadBack(ram, Buffer, 0x30)
            .Chunk(4).Select(c => BitConverter.ToUInt32(c)).ToHashSet();
        foreach (var notSaved in new[]
        {
            R3000aRegister.A0, R3000aRegister.A1, R3000aRegister.A2, R3000aRegister.A3,
            R3000aRegister.V0, R3000aRegister.V1,
            R3000aRegister.T0, R3000aRegister.T1, R3000aRegister.T9,
        })
        {
            words.Should().NotContain(gpr[(int)notSaved], $"{notSaved} is not part of jmp_buf");
        }
    }

    [Fact]
    public void SetJmp_Accepts_A_Buffer_That_Ends_Exactly_At_The_RAM_Mirror_Boundary()
    {
        var ram = new RecompilerGuestMemory();
        var address = 0x00800000u - (uint)SetJmpService.BufferSize;

        var result = CreateRuntime(ram).Invoke(Call(address, DistinctRegisters()));

        result.Status.Should().Be(BiosServiceStatus.Supported);
    }

    [Theory]
    [InlineData(0x00800000u - 0x2Fu)]  // last byte lands one past the RAM window
    [InlineData(0x00800000u)]          // wholly past the RAM window
    [InlineData(0xFFFFFFF0u)]          // 32-bit wraparound
    [InlineData(0xC0000000u)]          // KSEG2, untranslatable
    public void SetJmp_Invalid_Buffer_Fails_Closed_Without_Partial_Write(uint address)
    {
        var ram = new RecompilerGuestMemory();
        var runtime = CreateRuntime(ram);
        var before = ReadBack(ram, 0x00000000, 0x2000);

        var result = runtime.Invoke(Call(address, DistinctRegisters()));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        result.Diagnostic.Message.Should().StartWith("A0:13 setjmp:");
        ReadBack(ram, 0x00000000, 0x2000).Should().Equal(before);
    }

    [Fact]
    public void SetJmp_Straddling_The_Window_Edge_Writes_Nothing()
    {
        var ram = new RecompilerGuestMemory();
        var address = 0x00800000u - 0x10u;
        var before = ReadBack(ram, address, 0x10);

        CreateRuntime(ram).Invoke(Call(address, DistinctRegisters()))
            .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");

        ReadBack(ram, address, 0x10).Should().Equal(before);
    }

    [Fact]
    public void SetJmp_Without_A_Register_File_Fails_Closed()
    {
        var ram = new RecompilerGuestMemory();

        var result = CreateRuntime(ram).Invoke(Call(Buffer, gpr: null));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        ReadBack(ram, Buffer, 0x30).Should().OnlyContain(b => b == 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SetJmp_Rejects_A_Wrong_Argument_Count(int argumentCount)
    {
        var identity = new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.SetJmpFunction,
            arguments: new uint[argumentCount], guestRegisters: DistinctRegisters());

        var result = CreateRuntime(new RecompilerGuestMemory()).Invoke(identity);

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
    }

    // Through the shared dispatch boundary both execution paths use: $v0 = 0 and
    // control returns to the call site's own $ra, applied once by the caller.
    [Fact]
    public void Dispatch_Returns_Zero_In_V0_And_Continues_At_The_Call_Sites_Ra()
    {
        var ram = new RecompilerGuestMemory();
        var gpr = DistinctRegisters();
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.SetJmpFunction;
        gpr[(int)R3000aRegister.A0] = Buffer;
        gpr[(int)R3000aRegister.Ra] = 0x80012345;

        var outcome = BiosVectorDispatch.Dispatch(CreateRuntime(ram), BiosCallFamily.A0, gpr);

        outcome.ContinueExecution.Should().BeTrue();
        outcome.ReturnValue.Should().Be(0u);
        outcome.NextPc.Should().Be(0x80012345u);
        outcome.IsPatchedTarget.Should().BeFalse();
        BitConverter.ToUInt32(ReadBack(ram, Buffer, 4)).Should().Be(0x80012345u, "+00 holds the caller's $ra");
    }

    // A0:14 longjmp is out of scope for this Issue and must stay explicit.
    [Theory]
    [InlineData((byte)0x12)]
    [InlineData((byte)0x14)]
    public void Neighbouring_A0_Slots_Remain_Unsupported(byte function)
    {
        var result = CreateRuntime(new RecompilerGuestMemory()).Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, function, arguments: [Buffer, 1u], guestRegisters: DistinctRegisters()));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }
}
