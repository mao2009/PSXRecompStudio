using PSXRecomp.Core;

namespace PSXRecomp.Tests;

// Issue #157: domain-level integration test exercising the minimal MIPS
// program end-to-end through PSXCoreWrapper (RAM -> memory bus -> CPU
// fetch/decode/execute -> PC update), using only the existing public C ABI
// surface exposed by PSXCoreWrapper (no C ABI additions were needed).
[Test]
public class PSXCoreMinimalProgramTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();

    // addiu $t0, $zero, 10   ($t0 = GPR[8])
    // addiu $t1, $zero, 20   ($t1 = GPR[9])
    // addu  $t2, $t0, $t1    ($t2 = GPR[10])
    private const uint AddiuT0 = 0x2408000Au;
    private const uint AddiuT1 = 0x24090014u;
    private const uint AdduT2 = 0x01095021u;

    public void Dispose()
    {
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MinimalProgram_ExecutesThroughMemoryBus_ProducesExpectedRegistersAndPc()
    {
        _core.WriteMemory32(0, AddiuT0);
        _core.WriteMemory32(4, AddiuT1);
        _core.WriteMemory32(8, AdduT2);
        _core.Pc = 0;

        // Instruction fetch goes through the memory bus: confirm the words
        // written above are readable back through the same path the CPU uses.
        _core.ReadMemory32(0).Should().Be(AddiuT0);
        _core.ReadMemory32(4).Should().Be(AddiuT1);
        _core.ReadMemory32(8).Should().Be(AdduT2);

        _core.Step().Should().Be(0, "Step() should succeed for addiu");
        _core.Pc.Should().Be(4);
        _core.GetGpr(8).Should().Be(10, "$t0 == 10 after addiu $t0, $zero, 10");

        _core.Step().Should().Be(0, "Step() should succeed for addiu");
        _core.Pc.Should().Be(8);
        _core.GetGpr(9).Should().Be(20, "$t1 == 20 after addiu $t1, $zero, 20");

        _core.Step().Should().Be(0, "Step() should succeed for addu");
        _core.Pc.Should().Be(12);
        _core.GetGpr(10).Should().Be(30, "$t2 == 30 after addu $t2, $t0, $t1");

        _core.GetGpr(0).Should().Be(0, "$zero must always read 0");
    }

    [Fact]
    public void MinimalProgram_Run_ExecutesAllThreeInstructionsAndAdvancesPc()
    {
        _core.WriteMemory32(0, AddiuT0);
        _core.WriteMemory32(4, AddiuT1);
        _core.WriteMemory32(8, AdduT2);
        _core.Pc = 0;

        _core.Run(3).Should().Be(0, "Run() returns 0 when all requested instructions execute successfully");

        _core.GetGpr(8).Should().Be(10);
        _core.GetGpr(9).Should().Be(20);
        _core.GetGpr(10).Should().Be(30);
        _core.GetGpr(0).Should().Be(0);
        _core.Pc.Should().Be(12);
    }
}
