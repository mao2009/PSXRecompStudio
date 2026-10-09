using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class OpenBiosBootMonitorTests
{
    private const uint Jr = 0x00000008u;

    private static uint Lui(int rt, uint imm) => 0x3C000000u | ((uint)rt << 16) | imm;
    private static uint Ori(int rt, int rs, uint imm) => 0x34000000u | ((uint)rs << 21) | ((uint)rt << 16) | imm;
    private static uint Sw(int rt, uint offset) => 0xAC000000u | ((uint)rt << 16) | offset;
    private static uint JrReg(int rs) => ((uint)rs << 21) | Jr;
    private static uint Jalr(int rs) => ((uint)rs << 21) | (31u << 11) | 0x09u;

    private static byte[] Rom(params uint[] words)
    {
        var rom = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(i * 4, 4), words[i]);
        }
        return rom;
    }

    private static (OpenBiosBootReport Report, TitleExecutionState State) Run(byte[] rom, uint budget)
    {
        var backend = new OpenBiosBootBackend(OpenBiosFirmware.FromBytes(rom));
        using var engine = (InterpreterTitleExecutionEngine)backend.CreateEngine();
        var monitor = new OpenBiosBootMonitor(() => engine.Cop0Diagnostics.Cause);
        engine.FetchObserver = monitor.OnFetch;
        var result = new ExecutionOrchestrator().Execute(engine, null,
            new TitleExecutionRequest(backend.EntryPc, new uint[32], 0, 0,
                Array.Empty<RecompilerInitialMemoryItem>(), outerBudget: 1, segmentBudget: budget));
        return (monitor.Evaluate(engine.ReadGuestWord), result.State);
    }

    [Fact]
    public void Synthetic_Rom_That_Installs_Vectors_Takes_An_Exception_And_Enters_The_Shell_Is_Booted()
    {
        const uint nop = 0;
        var code = new List<uint>
        {
            Lui(8, 0x03E0), Ori(8, 8, 0x0008),                  // t0 = JR $ra
            Sw(8, 0xA0), Sw(8, 0xB0), Sw(8, 0xC0),              // A0/B0/C0 vector stubs
            Lui(9, 0x0340), Ori(9, 9, 0x0008),                  // t1 = JR $k0
            Lui(10, 0x401A), Ori(10, 10, 0x7000), Sw(10, 0x80), // 0x80: mfc0 $k0,EPC
            Sw(0, 0x84),                                        // 0x84: nop (load delay)
            Lui(10, 0x275A), Ori(10, 10, 0x0004), Sw(10, 0x88), // 0x88: addiu $k0,$k0,4
            Sw(9, 0x8C),                                        // 0x8C: jr $k0
            Lui(10, 0x4200), Ori(10, 10, 0x0010), Sw(10, 0x90), // 0x90: rfe (delay slot of the jr)
            0x0000000Cu, nop,                                   // syscall -> the general vector, which returns here
            Sw(8, 0x1000), Sw(0, 0x1004), 0x240C1000u, Jalr(12), nop, // kernel code expanded to low RAM runs: JR $ra
            Lui(11, 0x8003), 0xAD680000u, 0xAD600004u,          // shell at 0x80030000: JR $ra ; nop
            Jalr(11), nop,                                      // call the shell
        };
        code.Add(0x08000000u | (((0x1FC00000u + (uint)code.Count * 4) >> 2) & 0x03FFFFFFu)); // spin after the shell returns
        code.Add(nop);
        var (report, _) = Run(Rom(code.ToArray()), budget: 2000);
        Assert.True(report.ResetVectorFetched);
        Assert.True(report.VectorsInstalled);
        Assert.True(report.ExceptionVectorExecuted);
        Assert.True(report.ShellEntered);
        Assert.True(report.KernelBooted);
        Assert.Equal(1ul, report.SyscallEntries);
        Assert.Equal(0ul, report.InterruptEntries);
    }

    [Fact]
    public void Rom_That_Only_Spins_Is_Not_A_Booted_Kernel()
    {
        // j 0xBFC00000 ; nop  -> executes forever, never installs a vector or enters a shell.
        var (report, state) = Run(Rom(0x08000000u | ((0x1FC00000u >> 2) & 0x03FFFFFFu), 0), budget: 5000);
        Assert.Equal(TitleExecutionState.BudgetExhausted, state);
        Assert.True(report.ResetVectorFetched);
        Assert.False(report.VectorsInstalled);
        Assert.False(report.ShellEntered);
        Assert.False(report.KernelBooted);
    }
}
