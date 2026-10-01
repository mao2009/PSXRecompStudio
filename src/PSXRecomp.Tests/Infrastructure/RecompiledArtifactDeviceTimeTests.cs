using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #679: the generated-host artifact reports the guest instructions it retires, the host
/// turns them into device time for the existing <see cref="DeviceScheduler"/>, and a device that
/// moves data into guest RAM reaches the artifact's RAM — never the device graph's private one.
/// Device state is observed the way a guest observes it: through MMIO reads and RAM loads.
/// </summary>
[Test]
public sealed class RecompiledArtifactDeviceTimeTests
{
    private const uint Entry = 0x80001000u;
    private const uint InterruptStatus = 0x1F801070u;
    private const uint Timer2Mode = 0x1F801124u;
    private const uint Timer2Target = 0x1F801128u;
    private const uint Dpcr = 0x1F8010F0u;
    private const uint Dma3Madr = 0x1F8010B0u;
    private const uint Dma3Bcr = 0x1F8010B4u;
    private const uint Dma3Chcr = 0x1F8010B8u;

    private const uint Timer2Irq = 1u << (DeviceScheduler.Timer0Irq + 2);
    private const uint VblankIrq = 1u << DeviceScheduler.VblankIrq;
    private const uint ChcrBusy = 1u << 24;

    private const R3000aRegister T0 = R3000aRegister.T0;
    private const R3000aRegister T1 = R3000aRegister.T1;
    private const R3000aRegister T2 = R3000aRegister.T2;
    private const R3000aRegister S0 = R3000aRegister.S0;
    private const R3000aRegister Zero = R3000aRegister.Zero;

    private static uint Nop => MipsEncoding.Nop;

    // ---- cycle accounting: retired instructions, not blocks ------------------

    /// <summary>The instructions the artifact reports retiring for <paramref name="words"/>, one report per flush.</summary>
    private static IReadOnlyList<ulong> ScriptedRetired(uint[] words)
    {
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false); // builds the artifact and writes its input files
        var run = RunScripted(dir, _ => "V 0");
        run.HasSnapshot.Should().BeTrue();
        return run.Retired;
    }

    [Fact]
    public void Retired_StraightLineProgram_IsTheInstructionCount_AndDeterministic()
    {
        var words = Program(
            Li(T0, 0x80002000u),
            [Mem(R3000aOpcode.Lw, S0, T0, 0), Ori(R3000aRegister.S1, S0, 0)], // load + observer: one fused block
            [Ori(T1, Zero, 5)]);

        ReachableProgramBuilder.Build(Entry, words, Entry).Blocks.Count
            .Should().BeLessThan(words.Length, "a fused load/observer pair is one block of two instructions");

        var first = ScriptedRetired(words);
        var second = ScriptedRetired(words);

        first.Sum(static c => (long)c).Should().Be(words.Length);
        second.Should().Equal(first);
    }

    [Fact]
    public void Retired_BranchAndItsDelaySlot_AreTwoInstructions_AndASkippedInstructionIsNone()
    {
        var words = Program(
            [Ori(T0, Zero, 1)],                                              // 0
            [MipsEncoding.Branch(0x04, (byte)Zero, (byte)Zero, Entry + 4, Entry + 16)], // 1: beq zero,zero -> 4
            [Ori(T1, Zero, 5)],                                              // 2: delay slot, retires
            [Ori(T2, Zero, 7)],                                              // 3: skipped
            [Ori(R3000aRegister.T3, Zero, 9)]);                              // 4, then the appended nop (5)

        var retired = ScriptedRetired(words);

        retired.Sum(static c => (long)c).Should().Be(5, "instructions 0, 1, 2, 4 and 5 retire; 3 is branched over");
    }

    [Fact]
    public void Retired_LoadBranchAndDelaySlot_AreThreeInstructions()
    {
        var words = Program(
            Li(T0, 0x80002000u),
            [Mem(R3000aOpcode.Lw, S0, T0, 0), MipsEncoding.Branch(0x05, (byte)S0, (byte)Zero, Entry + 12, Entry + 24), Nop],
            [Ori(T1, Zero, 5)],
            [Ori(T2, Zero, 7)]);

        var blocks = ReachableProgramBuilder.Build(Entry, words, Entry).Blocks;
        blocks.Should().Contain(static block => block.RetiredInstructionCount == 3);

        var retired = ScriptedRetired(words);

        // lui, ori, lw+bne+nop (not taken: $s0 is 0), ori, ori, appended nop.
        retired.Sum(static c => (long)c).Should().Be(words.Length);
    }

    [Fact]
    public void Retired_IsReportedAtMmioAccesses_AndAtTheEnd_NotPerInstruction()
    {
        var words = Program(
            [Lui(T0, 0x1F80)],
            [Nop, Nop, Nop],
            Load(R3000aOpcode.Lw, S0, T0, 0x1814),
            [Nop, Nop]);

        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);

        var run = RunScripted(dir, _ => "V 0");

        // lui + 3 nops before the read, then the load's delay-slot nop and the rest at the end.
        run.Retired.Should().Equal(4ul, (ulong)(words.Length - 4));
    }

    // ---- the existing scheduler advances from those instructions ----------------

    /// <summary>
    /// Arms Timer 2 to raise IRQ6 at counter 100 (free-running from the mode write), runs
    /// <paramref name="between"/> and <paramref name="nops"/> more instructions, then loads I_STAT.
    /// </summary>
    private static uint IStatAfterTimerArmed(uint[] between, int nops, bool withRuntime = true)
    {
        var words = Program(
            [Lui(T0, 0x1F80)],
            [Ori(T1, Zero, 100), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Timer2Target & 0xFFFF))],
            [Ori(T1, Zero, 0x10), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Timer2Mode & 0xFFFF))],
            between,
            Enumerable.Repeat(Nop, nops).ToArray(),
            Load(R3000aOpcode.Lw, S0, T0, (ushort)(InterruptStatus & 0xFFFF)));

        using var dir = new TempDirectory();
        var result = Run(words, dir, withRuntime);
        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        return result.FinalSnapshot!.Gpr[(int)S0];
    }

    [Theory]
    [InlineData(98, 0u)]        // the mode store (1) + 98 = 99 cycles: the counter has not reached 100
    [InlineData(99, Timer2Irq)] // + 99 = exactly 100: IRQ6 pending in I_STAT
    public void Timer_ConfiguredThroughMmio_RaisesItsIrqAtExactlyTheTargetCycle(int nops, uint expectedStatus)
    {
        IStatAfterTimerArmed([], nops).Should().Be(expectedStatus);
    }

    [Theory]
    [InlineData(96, 0u)]
    [InlineData(97, Timer2Irq)]
    public void Timer_AfterASyscall_ChargesTheSyscallInstructionOnly(int nops, uint expectedStatus)
    {
        // The serviced SYSCALL costs its own one instruction (interpreter parity: Advance(1)
        // after the kernel service) — no invented kernel time — plus the ORI that loads $a0 (1 + 1 + 1 + 97 = 100).
        var syscall = new[]
        {
            Ori(R3000aRegister.A0, Zero, (ushort)BiosKernelSyscall.ExitCriticalSection),
            MipsEncoding.Syscall(),
        };

        IStatAfterTimerArmed(syscall, nops).Should().Be(expectedStatus);
    }

    [Theory]
    [InlineData(94, 0u)]
    [InlineData(95, Timer2Irq)]
    public void Timer_AfterABiosHleCall_ChargesTheGuestInstructionsOnly(int nops, uint expectedStatus)
    {
        // ori t1, ori a0, jal, delay slot = 4 retired; the A0 host transfer is a Runtime service
        // (no guest instruction), exactly as the interpreter's vector dispatch costs no Advance.
        var bios = new[]
        {
            Ori(T1, Zero, BiosHleRuntime.PutCharFunction),
            Ori(R3000aRegister.A0, Zero, (ushort)'P'),
            MipsEncoding.JumpAndLink(BiosJumpTables.A0VectorAddress),
            Nop,
        };

        IStatAfterTimerArmed(bios, nops).Should().Be(expectedStatus);
    }

    private static uint[] CountdownProgram(uint iterations) => Program(
        [Lui(T0, 0x1F80)],
        Li(T2, iterations),
        [MipsEncoding.I(0x09, (byte)T2, (byte)T2, 0xFFFF)],                             // loop: addiu t2,t2,-1
        [MipsEncoding.Branch(0x05, (byte)T2, (byte)Zero, Entry + 16, Entry + 12), Nop], // bne t2,zero,loop
        Load(R3000aOpcode.Lw, S0, T0, (ushort)(InterruptStatus & 0xFFFF)));

    [Fact]
    public void Vblank_AfterEnoughGuestCycles_LatchesIrq0InIStat_WithoutDeliveringItToTheCpu()
    {
        // 3 cycles of setup + 3 per iteration: past one VBlank interval (563200 cycles).
        const uint iterations = 190_000;
        using var dir = new TempDirectory();

        var result = Run(CountdownProgram(iterations), dir, withRuntime: true, segmentBudget: 1_000_000);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        (result.FinalSnapshot!.Gpr[(int)S0] & VblankIrq).Should().Be(VblankIrq);
        result.FinalSnapshot.Gpr[(int)T2].Should().Be(0u, "the loop ran to its end: SR never enabled interrupts, so the pending IRQ0 was not taken as an INT exception (#680)");
    }

    [Fact]
    public void Vblank_BeforeTheInterval_IsNotPending()
    {
        using var dir = new TempDirectory();

        var result = Run(CountdownProgram(100_000), dir, withRuntime: true, segmentBudget: 1_000_000);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        (result.FinalSnapshot!.Gpr[(int)S0] & VblankIrq).Should().Be(0u);
    }

    // ---- device-originated RAM -------------------------------------------------

    private static readonly byte[] SectorPayload = [1, 2, 3, 4, 5, 6, 7, 8];

    private static void MakeCdRomDataReady(PsxDeviceGraph graph)
    {
        // Same preparation as CdRomDmaTransferTests: ReadN, its INT1 data, acknowledged.
        graph.CdRomDevice.WriteCommand(0x06);
        graph.CdRomDevice.LoadData(SectorPayload);
        graph.CdRomDevice.ReadRegister(1);
        graph.CdRomDevice.AcknowledgeInterrupt();
    }

    private static uint[] Dma3Program() => Program(
        [Lui(T0, 0x1F80)],
        Li(T1, 0x0765C321u), [Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Dpcr & 0xFFFF))],                     // DPCR: channel 3 enabled
        Li(T1, 0x00003000u), [Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Dma3Madr & 0xFFFF))],                // MADR3
        [Ori(T1, Zero, 2), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Dma3Bcr & 0xFFFF))],                    // BCR3: 2 words
        Li(T1, 0x11000000u), [Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Dma3Chcr & 0xFFFF))],                // CHCR3: start
        // Plain RAM loads, no MMIO: the data must already be in the artifact's RAM.
        Li(T2, 0x80003000u),
        Load(R3000aOpcode.Lw, S0, T2, 0),
        Load(R3000aOpcode.Lw, R3000aRegister.S1, T2, 4),
        Load(R3000aOpcode.Lw, R3000aRegister.S2, T0, (ushort)(Dma3Chcr & 0xFFFF)));

    [Fact]
    public void CdRomDma3_WritesTheSectorIntoArtifactRam_VisibleToTheNextGuestLoad()
    {
        using var dir = new TempDirectory();

        var result = Run(Dma3Program(), dir, withRuntime: true, configureDevices: MakeCdRomDataReady);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        var gpr = result.FinalSnapshot!.Gpr;
        gpr[(int)S0].Should().Be(0x04030201u);
        gpr[(int)R3000aRegister.S1].Should().Be(0x08070605u);
        (gpr[(int)R3000aRegister.S2] & ChcrBusy).Should().Be(0u, "the existing CD-ROM DMA bridge completed channel 3");
    }

    [Fact]
    public void CdRomDma3_WithoutSectorData_LeavesArtifactRamUntouched_AndTheChannelBusy()
    {
        using var dir = new TempDirectory();

        var result = Run(Dma3Program(), dir, withRuntime: true);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        var gpr = result.FinalSnapshot!.Gpr;
        gpr[(int)S0].Should().Be(0u);
        gpr[(int)R3000aRegister.S1].Should().Be(0u);
        (gpr[(int)R3000aRegister.S2] & ChcrBusy).Should().Be(ChcrBusy);
    }

    [Fact]
    public void DeviceGraph_DeviceOriginatedRamWrite_GoesToTheInjectedSeam_NotTheNativeCoreRam()
    {
        var seam = new RecordingRam();
        using var graph = new PsxDeviceGraph(seam);
        MakeCdRomDataReady(graph);
        var scheduler = new DeviceScheduler(
            graph.Core, graph.InterruptControllerAdapter, graph.GpuAdapter, graph.CdRomDevice, graph.CdRomDmaTransfer);
        graph.TryWrite(Dpcr, 4, 0x0765C321u);
        graph.TryWrite(Dma3Madr, 4, 0x3000u);
        graph.TryWrite(Dma3Bcr, 4, 2u);

        graph.TryWrite(Dma3Chcr, 4, 0x11000000u);
        scheduler.Advance(1);

        seam.Writes.Should().Equal(new KeyValuePair<uint, uint>(0x3000u, 0x04030201u), new KeyValuePair<uint, uint>(0x3004u, 0x08070605u));
        graph.Core.ReadMemory32(0x3000u).Should().Be(0u, "the graph's native RAM is not the generated-host guest RAM");
    }

    [Fact]
    public void DeviceGraph_WithoutASeam_KeepsTheInterpretersNativeRam()
    {
        using var graph = new PsxDeviceGraph();
        MakeCdRomDataReady(graph);
        var scheduler = new DeviceScheduler(
            graph.Core, graph.InterruptControllerAdapter, graph.GpuAdapter, graph.CdRomDevice, graph.CdRomDmaTransfer);
        graph.TryWrite(Dpcr, 4, 0x0765C321u);
        graph.TryWrite(Dma3Madr, 4, 0x3000u);
        graph.TryWrite(Dma3Bcr, 4, 2u);
        graph.TryWrite(Dma3Chcr, 4, 0x11000000u);

        scheduler.Advance(1);

        graph.Core.ReadMemory32(0x3000u).Should().Be(0x04030201u);
    }

    private sealed class RecordingRam : PSXRecomp.Core.Dma.IMemoryBus
    {
        public List<KeyValuePair<uint, uint>> Writes { get; } = [];

        public uint Read(uint address) => throw new InvalidOperationException("Not expected.");

        public void Write(uint address, uint value) => Writes.Add(new(address, value));
    }

    [Fact]
    public void DeviceRam_OutsideTheArtifactsServingWindow_FailsClosed()
    {
        var bytes = new Dictionary<uint, byte>();
        var ram = new ArtifactDeviceRam(address => bytes.GetValueOrDefault(address), (address, value) => bytes[address] = value);

        var write = () => ram.Write(0x3000u, 1u);
        var read = () => ram.Read(0x3000u);

        write.Should().Throw<ArtifactDeviceRam.UnroutableException>();
        read.Should().Throw<ArtifactDeviceRam.UnroutableException>();
        bytes.Should().BeEmpty("nothing reached a RAM the artifact is not serving");

        ram.BeginServing();
        ram.Write(0x3000u, 0x04030201u);
        ram.Read(0x3000u).Should().Be(0x04030201u);
        bytes.Should().Equal(new Dictionary<uint, byte> { [0x3000] = 1, [0x3001] = 2, [0x3002] = 3, [0x3003] = 4 });

        ram.EndServing();
        write.Should().Throw<ArtifactDeviceRam.UnroutableException>();
    }

    /// <summary>A stand-in child that answers the host's first RAM write request with <c>reply</c>.</summary>
    private sealed class BadWriteAckChildBuildService(string reply) : IGeneratedHostBuildService
    {
        public GeneratedHostBuildResult Build(GeneratedHostBuildRequest request)
        {
            var source =
                "#include <stdio.h>\n" +
                "int main(void) {\n" +
                "  char b[64]; unsigned long a, v;\n" +
                $"  printf(\"{RecompiledArtifactCodeGen.ProtocolInitLine}\\n\"); fflush(stdout);\n" +
                // The handshake: the Runtime's construction reads RAM bytes before it declines (N).
                "  for (;;) {\n" +
                "    if (scanf(\"%63s\", b) != 1) return 1;\n" +
                "    if (b[0] == 'N') break;\n" +
                "    if (b[0] == 'R' && scanf(\"%lu\", &a) == 1) { printf(\"RHOST_DATA 0\\n\"); fflush(stdout); }\n" +
                "    else if (b[0] == 'W' && scanf(\"%lu %lu\", &a, &v) == 2) { printf(\"RHOST_OK\\n\"); fflush(stdout); }\n" +
                "    else return 1;\n" +
                "  }\n" +
                $"  printf(\"{RecompiledArtifactCodeGen.ProtocolRetiredPrefix}1\\n\"); fflush(stdout);\n" +
                // Answer every RAM write with the same reply until the host sends anything else.
                "  while (scanf(\"%63s\", b) == 1 && b[0] == 'W') {\n" +
                "    if (scanf(\"%lu %lu\", &a, &v) != 2) return 1;\n" +
                $"    printf(\"{reply}\\n\"); fflush(stdout);\n" +
                "  }\n" +
                "  return 0;\n" +
                "}\n";
            return new GeneratedHostBuildService().Build(request with { Source = source });
        }
    }

    [Theory]
    [InlineData("RHOST_DATA 0")]
    [InlineData("Q")]
    [InlineData("A")]
    [InlineData("X")]
    [InlineData("OK")]
    [InlineData("RHOST_OKAY")]
    [InlineData("RHOST_OK extra")]
    [InlineData("")]
    public void Host_RamWriteAnsweredWithAnythingButRhostOk_IsAProtocolFailure(string reply)
    {
        using var dir = new TempDirectory();

        // CD-ROM DMA3 is armed host-side; the child's guest-time report makes the scheduler write
        // the sector into RAM through ArtifactDeviceRam -> WritePhysicalByte.
        var result = Run(
            Program([Nop]),
            dir,
            withRuntime: true,
            new BadWriteAckChildBuildService(reply),
            configureDevices: static graph =>
            {
                MakeCdRomDataReady(graph);
                graph.TryWrite(Dpcr, 4, 0x0765C321u);
                graph.TryWrite(Dma3Madr, 4, 0x3000u);
                graph.TryWrite(Dma3Bcr, 4, 2u);
                graph.TryWrite(Dma3Chcr, 4, 0x11000000u);
            });

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_HOST_PROTOCOL_FAILED");
        result.FinalSnapshot.Should().BeNull();
    }

    // ---- fail closed ---------------------------------------------------------------

    [Fact]
    public void SchedulerFailure_StopsTheRunWithAClassifiedDiagnostic()
    {
        using var dir = new TempDirectory();

        // The interrupt controller torn down under the scheduler: raising IRQ0 at the first VBlank throws.
        var result = Run(
            CountdownProgram(190_000),
            dir,
            withRuntime: true,
            configureDevices: static graph => graph.InterruptControllerAdapter.Dispose(),
            segmentBudget: 1_000_000);

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_SCHEDULER_FAILED");
    }

    [Theory]
    [InlineData("RHOST_RETIRED 0")]                      // the artifact never reports nothing
    [InlineData("RHOST_RETIRED")]
    [InlineData("RHOST_RETIRED -1")]
    [InlineData("RHOST_RETIRED four")]
    [InlineData("RHOST_RETIRED 1 2")]
    [InlineData("RHOST_RETIRED 18446744073709551615")]   // more than a 32-bit budget of fused blocks can retire
    [InlineData("RHOST_RETIRED 18446744073709551616")]   // not a 64-bit count
    public void Host_MalformedGuestTimeReport_IsAProtocolFailure(string line)
    {
        using var dir = new TempDirectory();

        var result = Run(Program([Nop]), dir, withRuntime: true, new FakeChildBuildService(line));

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_HOST_PROTOCOL_FAILED");
    }

    [Theory]
    [InlineData("X", RecompiledArtifactCodeGen.RetiredRefusedExitCode)]
    [InlineData("Q", RecompiledArtifactCodeGen.RetiredProtocolExitCode)]
    [InlineData("V 0", RecompiledArtifactCodeGen.RetiredProtocolExitCode)]
    [InlineData(null, RecompiledArtifactCodeGen.RetiredProtocolExitCode)]
    public void Artifact_RefusedOrMalformedReplyToAGuestTimeReport_StopsWithoutASnapshot(string? reply, int expectedExit)
    {
        using var dir = new TempDirectory();
        Run(Program([Ori(T1, Zero, 1)]), dir, withRuntime: false);

        var run = RunScripted(dir, _ => "V 0", _ => reply);

        run.ExitCode.Should().Be(expectedExit);
        run.HasSnapshot.Should().BeFalse("a refused guest-time report must not run on as if devices had advanced");
    }
}
