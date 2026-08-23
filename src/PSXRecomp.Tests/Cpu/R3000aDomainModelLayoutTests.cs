using System.Runtime.CompilerServices;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aDomainModelLayoutTests
{
    [Fact]
    public void Operand_Size_Is8Bytes()
    {
        Unsafe.SizeOf<R3000aOperand>().Should().Be(8);
    }

    [Fact]
    public void LinkInfo_Size_Is2Bytes()
    {
        Unsafe.SizeOf<R3000aLinkInfo>().Should().Be(2);
    }

    [Fact]
    public void LoadDelayInfo_Size_Is3Bytes()
    {
        Unsafe.SizeOf<R3000aLoadDelayInfo>().Should().Be(3);
    }

    [Fact]
    public void CopInfo_Size_Is8Bytes()
    {
        Unsafe.SizeOf<R3000aCopInfo>().Should().Be(8);
    }

    [Fact]
    public void Instruction_Size_Is48Bytes()
    {
        Unsafe.SizeOf<R3000aInstruction>().Should().Be(48);
    }
}
