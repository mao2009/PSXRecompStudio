using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// A0:3F printf (Issue #670). PrintfService is tested directly for formatting and
// argument semantics; the generated-host / interpreter dispatch is covered by
// RecompiledBiosVectorDispatchTests.
[Test]
public sealed class PrintfServiceTests
{
    private const uint Fmt = 0x00000100;
    private const uint Sp = 0x00000800;

    private static void Write(RecompilerGuestMemory ram, uint address, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            ram.Write8(address + (uint)i, (byte)text[i]);
        }

        ram.Write8(address + (uint)text.Length, 0);
    }

    private static void WriteWord(RecompilerGuestMemory ram, uint address, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            ram.Write8(address + (uint)i, (byte)(value >> (8 * i)));
        }
    }

    private static (BiosServiceResult Result, string Output) Run(
        RecompilerGuestMemory ram, uint format, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint sp = Sp)
    {
        var gpr = new uint[32];
        gpr[(int)R3000aRegister.A0] = format;
        gpr[(int)R3000aRegister.A1] = a1;
        gpr[(int)R3000aRegister.A2] = a2;
        gpr[(int)R3000aRegister.A3] = a3;
        gpr[(int)R3000aRegister.Sp] = sp;
        var identity = new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PrintfFunction, arguments: [format], guestRegisters: gpr);
        var sink = new CapturedOutputSink();
        var result = PrintfService.Invoke(identity, new GuestMemoryReader(ram.Read8), sink);
        return (result, System.Text.Encoding.Latin1.GetString(sink.Bytes.ToArray()));
    }

    private static (BiosServiceResult Result, string Output) Format(string format, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, Fmt, format);
        return Run(ram, Fmt, a1, a2, a3);
    }

    [Theory]
    [InlineData("plain", 0u, 0u, "plain")]
    [InlineData("100%%", 0u, 0u, "100%")]
    [InlineData("%c%c", 65u, 66u, "AB")]
    [InlineData("addr=%08x", 0xBEEFu, 0u, "addr=0000beef")]
    [InlineData("%x %X", 0xBEEFu, 0xBEEFu, "beef BEEF")]
    [InlineData("%d %i", 0xFFFFFFFEu, 0xFFFFFFFEu, "-2 -2")]
    [InlineData("%u", 0xFFFFFFFEu, 0u, "4294967294")]
    [InlineData("%5d|%05d", 0xFFFFFFFEu, 0xFFFFFFFEu, "   -2|-0002")]
    public void Formats_Supported_Conversions(string format, uint a1, uint a2, string expected)
    {
        var (result, output) = Format(format, a1, a2);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().BeNull("no source documents printf's return value, so $v0 is not fabricated");
        output.Should().Be(expected);
    }

    [Fact]
    public void Signed_And_Unsigned_Boundaries()
    {
        Format("%d %d %u %x", 0x80000000u, 0x7FFFFFFFu, 0xFFFFFFFFu).Output
            .Should().Be("-2147483648 2147483647 4294967295 0", "the fourth value comes from $a3 (zero here)");
    }

    [Fact]
    public void String_Argument_Is_Read_From_Guest_Memory()
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, Fmt, "[%s]");
        Write(ram, 0x200, "hey");
        Run(ram, Fmt, a1: 0x200).Output.Should().Be("[hey]");
    }

    [Fact]
    public void Fifth_And_Later_Words_Come_From_The_Stack_At_Sp_Plus_16()
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, Fmt, "%d %d %d %d %d");
        WriteWord(ram, Sp + 16, 4);
        WriteWord(ram, Sp + 20, 5);
        WriteWord(ram, Sp, 99); // the $a0-$a3 home area is not an argument slot
        Run(ram, Fmt, 1, 2, 3).Output.Should().Be("1 2 3 4 5");
    }

    [Theory]
    [InlineData("%f")]
    [InlineData("%n")]
    [InlineData("%o")]
    [InlineData("%ld")]
    [InlineData("%-5d")]
    [InlineData("%.2d")]
    [InlineData("%5c")]
    [InlineData("%5%")]
    [InlineData("%")]
    [InlineData("%0")]
    [InlineData("%99999d")]
    public void Unsupported_Format_Fails_Closed_And_Writes_Nothing(string format)
    {
        var (result, output) = Format("ok" + format);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        output.Should().BeEmpty("output is atomic: nothing is written when the call fails");
    }

    [Fact]
    public void Unreadable_Format_Pointer_Fails_Closed()
    {
        var ram = new RecompilerGuestMemory();
        Run(ram, 0x1F801000).Result.Status.Should().Be(BiosServiceStatus.Unsupported); // MMIO, not RAM
        Run(ram, 0xFFFFFFF0).Result.Status.Should().Be(BiosServiceStatus.Unsupported); // wraparound
    }

    [Fact]
    public void Unterminated_Format_Fails_Closed_Within_The_Bound()
    {
        var ram = new RecompilerGuestMemory();
        for (var i = 0; i < PrintfService.MaxFormatLength + 8; i++)
        {
            ram.Write8(Fmt + (uint)i, (byte)'x');
        }

        var (result, output) = Run(ram, Fmt);
        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        output.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_Or_Unterminated_String_Argument_Fails_Closed()
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, Fmt, "a%s");
        Run(ram, Fmt, a1: 0x1F801000).Result.Status.Should().Be(BiosServiceStatus.Unsupported);

        for (var i = 0; i < PrintfService.MaxStringArgumentLength + 8; i++)
        {
            ram.Write8(0x2000 + (uint)i, (byte)'y');
        }

        var (result, output) = Run(ram, Fmt, a1: 0x2000);
        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        output.Should().BeEmpty();
    }

    [Fact]
    public void Unreadable_Stack_Argument_Fails_Closed()
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, Fmt, "%d %d %d %d");
        Run(ram, Fmt, sp: 0x1F801000).Result.Status.Should().Be(BiosServiceStatus.Unsupported);
    }

    [Fact]
    public void Output_Beyond_The_Bound_Fails_Closed()
    {
        var ram = new RecompilerGuestMemory();
        Write(ram, Fmt, "%s%s%s");
        for (var i = 0; i < PrintfService.MaxStringArgumentLength - 1; i++)
        {
            ram.Write8(0x2000 + (uint)i, (byte)'y');
        }

        var (result, output) = Run(ram, Fmt, 0x2000, 0x2000, 0x2000);
        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        output.Should().BeEmpty();
    }

    [Fact]
    public void Wrong_Argument_Count_Or_Missing_Register_File_Is_Rejected()
    {
        var reader = new GuestMemoryReader(new RecompilerGuestMemory().Read8);
        var sink = new CapturedOutputSink();

        PrintfService.Invoke(new BiosCallIdentity(BiosCallFamily.A0, 0x3F, arguments: []), reader, sink)
            .Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        PrintfService.Invoke(new BiosCallIdentity(BiosCallFamily.A0, 0x3F, arguments: [Fmt]), reader, sink)
            .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    [Fact]
    public void Registered_With_Arity_One_So_The_Live_Trap_Register_Guard_Is_Unchanged()
    {
        var ram = new RecompilerGuestMemory();
        IBiosRuntime runtime = new BiosHleRuntime(
            new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

        runtime.TryGetServiceArgumentCount(BiosCallFamily.A0, 0x3F, out var count).Should().BeTrue();
        count.Should().Be(1);
        BiosVectorDispatch.AbiArgumentRegisterCount.Should().Be(4);
    }
}
