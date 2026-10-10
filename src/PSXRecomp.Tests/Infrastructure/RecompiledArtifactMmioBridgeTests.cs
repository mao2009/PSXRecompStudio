using System.Diagnostics;
using System.Globalization;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #678: the generated-host artifact keeps guest RAM local and relays every
/// other guest access, at its own width, to the existing Runtime device graph.
/// Real artifacts are built through the production build service; the child side
/// of the wire protocol is also driven by a scripted parent so the exact requests
/// (and the child's fail-closed exits) are observable.
/// </summary>
[Test]
public sealed class RecompiledArtifactMmioBridgeTests
{
    private const uint Entry = 0x80001000u;
    private const uint GpuStat = 0x1F801814u;
    private const uint Timer1Count = 0x1F801110u;
    private const uint Timer1Target = 0x1F801118u;
    private const uint InterruptMask = 0x1F801074u;
    private const uint CdRomIndex = 0x1F801800u;
    private const uint Scratchpad = 0x1F800010u;

    private const R3000aRegister T0 = R3000aRegister.T0;
    private const R3000aRegister T1 = R3000aRegister.T1;
    private const R3000aRegister Zero = R3000aRegister.Zero;

    // ---- instruction helpers ---------------------------------------------

    internal static uint Lui(R3000aRegister rt, ushort imm) => MipsEncoding.I(0x0F, (byte)rt, 0, imm);

    internal static uint Ori(R3000aRegister rt, R3000aRegister rs, ushort imm) => MipsEncoding.I(0x0D, (byte)rt, (byte)rs, imm);

    internal static uint Mem(R3000aOpcode opcode, R3000aRegister rt, R3000aRegister baseRegister, ushort offset) =>
        MipsEncoding.Load(opcode, (byte)rt, (byte)baseRegister, offset);

    /// <summary>Loads a 32-bit constant: LUI + ORI.</summary>
    internal static uint[] Li(R3000aRegister rt, uint value) => [Lui(rt, (ushort)(value >> 16)), Ori(rt, rt, (ushort)value)];

    /// <summary>One load with its delay slot filled, so the result is architecturally visible.</summary>
    internal static uint[] Load(R3000aOpcode opcode, R3000aRegister rt, R3000aRegister baseRegister, ushort offset) =>
        [Mem(opcode, rt, baseRegister, offset), MipsEncoding.Nop];

    internal static uint[] Program(params uint[][] parts) => [.. parts.SelectMany(static p => p), MipsEncoding.Nop];

    internal sealed class NullSink : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }

    internal sealed class ExitHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot snapshot) => TitleExecutionHandoffResult.Exit();
    }

    internal static TitleExecutionRequest Request(uint segmentBudget = 256) =>
        new(Entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: segmentBudget);

    internal static TitleExecutionResult Run(
        uint[] words,
        TempDirectory dir,
        bool withRuntime,
        IGeneratedHostBuildService? buildService = null,
        Action<PsxDeviceGraph>? configureDevices = null,
        uint segmentBudget = 256,
        BiosExceptionChain? exceptionChain = null,
        IEnumerable<uint>? additionalRoots = null,
        Action<RecompiledHostExecutionEngine>? observe = null)
    {
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, additionalRoots ?? []);
        using var engine = new RecompiledHostExecutionEngine(
            program,
            words,
            Entry,
            buildService ?? new GeneratedHostBuildService(),
            dir.FullPath,
            withRuntime ? (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer) : null,
            configureDevices,
            exceptionChain);
        var result = new ExecutionOrchestrator().Execute(engine, new ExitHandoff(), Request(segmentBudget));
        observe?.Invoke(engine);
        return result;
    }

    // ---- scripted parent ---------------------------------------------------

    internal sealed record ScriptedRun(
        int ExitCode,
        IReadOnlyList<string> Requests,
        IReadOnlyDictionary<string, uint> Gpr,
        bool HasSnapshot,
        IReadOnlyList<ulong> Retired,
        IReadOnlyDictionary<string, uint>? Snapshot = null,
        int SyscallOffers = 0);

    /// <summary>
    /// Re-launches the artifact an earlier <see cref="Run"/> built in <paramref name="dir"/>
    /// with the host-transfer protocol, playing the parent by hand. <paramref name="reply"/>
    /// answers each MMIO request line; null closes the child's stdin instead.
    /// </summary>
    internal static ScriptedRun RunScripted(
        TempDirectory dir, Func<string, string?> reply, Func<ulong, string?>? retiredReply = null, uint? syscallSr = null, uint? eventCredit = null, bool requireExactTime = false, bool sendCreditOnReports = true,
        Func<string, string>? transferReply = null, bool guestExceptions = false, bool closeAfterTransferReply = false)
    {
#pragma warning disable AARC003 // Test-only: drives the artifact's own wire protocol.
        var binary = File.Exists(dir.Combine("recompiled-artifact.exe"))
            ? dir.Combine("recompiled-artifact.exe")
            : dir.Combine("recompiled-artifact");
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(dir.Combine("artifact-input.txt"));
        psi.ArgumentList.Add(dir.Combine("artifact-image.bin"));
        psi.ArgumentList.Add(RecompiledArtifactCodeGen.HostTransferFlag);
        if (requireExactTime) psi.ArgumentList.Add(RecompiledArtifactCodeGen.ExactDeviceTimeFlag);
        if (guestExceptions) psi.ArgumentList.Add(RecompiledArtifactCodeGen.GuestExceptionsFlag);

        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;
        var requests = new List<string>();
        var retired = new List<ulong>();
        var gpr = new Dictionary<string, uint>();
        var snapshot = new Dictionary<string, uint>();
        var hasSnapshot = false;
        var inputClosed = false;
        var syscallOffers = 0;
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            var isRequest = line.StartsWith(RecompiledArtifactCodeGen.ProtocolMmioReadPrefix, StringComparison.Ordinal)
                || line.StartsWith(RecompiledArtifactCodeGen.ProtocolMmioWritePrefix, StringComparison.Ordinal);
            if (line == RecompiledArtifactCodeGen.ProtocolInitLine
                || line.StartsWith(RecompiledArtifactCodeGen.ProtocolTransferPrefix, StringComparison.Ordinal))
            {
                if (eventCredit is { } initialCredit)
                    process.StandardInput.WriteLine($"T {initialCredit}");
                // The handshake and the program's unresolved end: this parent claims no pc.
                process.StandardInput.WriteLine(transferReply?.Invoke(line) ?? RecompiledArtifactCodeGen.ProtocolDeclineReply);
                if (closeAfterTransferReply)
                {
                    process.StandardInput.Close();
                    inputClosed = true;
                }
            }
            else if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolSyscallPrefix, StringComparison.Ordinal))
            {
                syscallOffers++;
                // Issue #680 setup: a serviced SYSCALL, the way the Runtime answers SYS(02h) — SR as the exception
                // entry pushed it with IEp/IM2 set (or a caller-chosen SR) — and a resume after the SYSCALL.
                var parts = line[RecompiledArtifactCodeGen.ProtocolSyscallPrefix.Length..].Split(' ');
                var faultPc = uint.Parse(parts[0], CultureInfo.InvariantCulture);
                var sr = syscallSr ?? (uint.Parse(parts[2], CultureInfo.InvariantCulture) | 0x404u);
                process.StandardInput.WriteLine($"{RecompiledArtifactCodeGen.ProtocolCop0SrCommand} {sr}");
                if (eventCredit is { } syscallCredit) process.StandardInput.WriteLine($"T {syscallCredit}");
                process.StandardInput.WriteLine($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}0 {faultPc + 4} 0 0");
            }
            else if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolRetiredPrefix, StringComparison.Ordinal))
            {
                // Issue #679 guest-time report: counted, and accepted (no device behind this parent).
                retired.Add(ulong.Parse(line[RecompiledArtifactCodeGen.ProtocolRetiredPrefix.Length..], CultureInfo.InvariantCulture));
                if (inputClosed) continue;
                var retiredAnswer = retiredReply is null ? RecompiledArtifactCodeGen.ProtocolRetiredAckReply : retiredReply(retired[^1]);
                if (retiredAnswer is null)
                {
                    process.StandardInput.Close();
                }
                else
                {
                    if (sendCreditOnReports && eventCredit is { } credit) process.StandardInput.WriteLine($"T {credit}");
                    process.StandardInput.WriteLine(retiredAnswer);
                }
            }
            else if (isRequest)
            {
                requests.Add(line);
                var answer = reply(line);
                if (answer is null)
                {
                    process.StandardInput.Close();
                }
                else
                {
                    process.StandardInput.WriteLine(answer);
                }
            }
            else if (line == RecompiledArtifactCodeGen.SnapshotBeginMarker)
            {
                hasSnapshot = true;
            }
            else if (line.StartsWith("gpr[", StringComparison.Ordinal))
            {
                var parts = line.Split('=');
                gpr[parts[0]] = uint.Parse(parts[1][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else if (hasSnapshot && line.Contains("=0x", StringComparison.Ordinal))
            {
                var parts = line.Split('=');
                snapshot[parts[0]] = uint.Parse(parts[1][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        process.WaitForExit(10000).Should().BeTrue("the artifact must terminate");
        var exit = process.ExitCode;
#pragma warning restore AARC003
        return new ScriptedRun(exit, requests, gpr, hasSnapshot, retired, snapshot, syscallOffers);
    }

    internal static string Read(int width, uint pa) =>
        $"{RecompiledArtifactCodeGen.ProtocolMmioReadPrefix}{width} {pa}";

    internal static string Write(int width, uint pa, uint value) =>
        $"{RecompiledArtifactCodeGen.ProtocolMmioWritePrefix}{width} {pa} {value}";

    internal static uint G(ScriptedRun run, R3000aRegister r) => run.Gpr[$"gpr[{(int)r}]"];

    // ---- RAM stays local ---------------------------------------------------

    private static uint[] RamProgram() => Program(
        Li(T0, 0x80002000u),
        Li(T1, 0xDEADBEEFu),
        [Mem(R3000aOpcode.Sw, T1, T0, 0)],
        Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0),
        Load(R3000aOpcode.Lhu, R3000aRegister.S1, T0, 0),
        Load(R3000aOpcode.Lhu, R3000aRegister.S2, T0, 2),
        Load(R3000aOpcode.Lbu, R3000aRegister.S3, T0, 0),
        Load(R3000aOpcode.Lb, R3000aRegister.S4, T0, 3),
        Li(T1, 0xA55Au),
        [Mem(R3000aOpcode.Sh, T1, T0, 4)],
        Load(R3000aOpcode.Lw, R3000aRegister.S5, T0, 4),
        [Ori(T1, Zero, 0x7F), Mem(R3000aOpcode.Sb, T1, T0, 9)],
        Load(R3000aOpcode.Lw, R3000aRegister.S6, T0, 8));

    [Fact]
    public void Ram_ReadsAndWritesOfEveryWidth_StayInTheArtifact_AndNeverReachTheHost()
    {
        using var dir = new TempDirectory();
        var words = RamProgram();

        // No Runtime attached: any non-RAM access would stop the run, so completing
        // proves RAM never needed the host.
        var result = Run(words, dir, withRuntime: false);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        var snap = result.FinalSnapshot!;
        snap.Gpr[(int)R3000aRegister.S0].Should().Be(0xDEADBEEFu);
        snap.Gpr[(int)R3000aRegister.S1].Should().Be(0xBEEFu);
        snap.Gpr[(int)R3000aRegister.S2].Should().Be(0xDEADu);
        snap.Gpr[(int)R3000aRegister.S3].Should().Be(0xEFu);
        snap.Gpr[(int)R3000aRegister.S4].Should().Be(0xFFFFFFDEu, "LB sign-extends");
        snap.Gpr[(int)R3000aRegister.S5].Should().Be(0xA55Au);
        snap.Gpr[(int)R3000aRegister.S6].Should().Be(0x7F00u);

        // The same program with the host protocol attached emits no MMIO request.
        var scripted = RunScripted(dir, _ => throw new InvalidOperationException("RAM must not reach the host."));
        scripted.Requests.Should().BeEmpty();
        scripted.HasSnapshot.Should().BeTrue();
        G(scripted, R3000aRegister.S0).Should().Be(0xDEADBEEFu);
    }

    // ---- request wire format -------------------------------------------------

    [Fact]
    public void Mmio_EachGuestAccessIsOneRequestOfItsOwnWidth()
    {
        using var dir = new TempDirectory();
        var words = Program(
            [Lui(T0, 0x1F80)],
            Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0x1814),
            Load(R3000aOpcode.Lhu, R3000aRegister.S1, T0, 0x1110),
            Load(R3000aOpcode.Lbu, R3000aRegister.S2, T0, 0x1800),
            Load(R3000aOpcode.Lb, R3000aRegister.S3, T0, 0x1801),
            Li(T1, 0x12345678u),
            [Mem(R3000aOpcode.Sw, T1, T0, 0x1118), Mem(R3000aOpcode.Sh, T1, T0, 0x1074), Mem(R3000aOpcode.Sb, T1, T0, 0x1801)]);
        Run(words, dir, withRuntime: false); // builds the artifact and writes its input files

        var run = RunScripted(dir, request => request switch
        {
            _ when request == Read(4, GpuStat) => "V 2864434397",   // 0xAABBCCDD
            _ when request == Read(2, Timer1Count) => "V 43981",    // 0xABCD
            _ when request == Read(1, CdRomIndex) => "V 245",       // 0xF5
            _ when request == Read(1, CdRomIndex + 1) => "V 128",   // 0x80
            _ => "V 0",
        });

        // Width and order are preserved: one request per guest access, never a
        // byte-wise expansion of the word/halfword accesses.
        run.Requests.Should().Equal(
            Read(4, GpuStat),
            Read(2, Timer1Count),
            Read(1, CdRomIndex),
            Read(1, CdRomIndex + 1),
            Write(4, Timer1Target, 0x12345678u),
            Write(2, InterruptMask, 0x5678u),
            Write(1, CdRomIndex + 1, 0x78u));
        run.HasSnapshot.Should().BeTrue();
        G(run, R3000aRegister.S0).Should().Be(0xAABBCCDDu);
        G(run, R3000aRegister.S1).Should().Be(0xABCDu);
        G(run, R3000aRegister.S2).Should().Be(0xF5u);
        G(run, R3000aRegister.S3).Should().Be(0xFFFFFF80u, "LB sign-extends the host's byte");
    }

    [Fact]
    public void Mmio_UntranslatableAddress_IsOfferedToTheHost_NotAssumedToBeZero()
    {
        using var dir = new TempDirectory();
        var words = Program(Li(T0, 0xFFFE0130u), Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0));
        Run(words, dir, withRuntime: false);

        var run = RunScripted(dir, _ => "V 7");

        run.Requests.Should().Equal(Read(4, 0xFFFFFFFFu));
        G(run, R3000aRegister.S0).Should().Be(7u);
    }

    // ---- reaching the existing device implementations --------------------------

    [Fact]
    public void Mmio_ReadsAndWritesReachTheExistingRuntimeDevices_WithTheGuestsWidths()
    {
        using var dir = new TempDirectory();
        var words = Program(
            [Lui(T0, 0x1F80)],
            Li(T1, 0x00001234u),
            [Mem(R3000aOpcode.Sw, T1, T0, 0x1118)],
            Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0x1118),
            [Ori(T1, Zero, 0x00AB), Mem(R3000aOpcode.Sh, T1, T0, 0x1074)],
            Load(R3000aOpcode.Lhu, R3000aRegister.S1, T0, 0x1074),
            [Ori(T1, Zero, 0x0001), Mem(R3000aOpcode.Sb, T1, T0, 0x1800)],
            Load(R3000aOpcode.Lbu, R3000aRegister.S2, T0, 0x1800),
            Load(R3000aOpcode.Lw, R3000aRegister.S3, T0, 0x1814),
            Li(T1, 0x08000001u),
            [Mem(R3000aOpcode.Sw, T1, T0, 0x1814)],
            Load(R3000aOpcode.Lw, R3000aRegister.S4, T0, 0x1814),
            Li(T0, 0x1F800000u),
            Li(T1, 0xCAFEF00Du),
            [Mem(R3000aOpcode.Sw, T1, T0, 0x10)],
            Load(R3000aOpcode.Lw, R3000aRegister.S5, T0, 0x10),
            Load(R3000aOpcode.Lhu, R3000aRegister.S6, T0, 0x12),
            Load(R3000aOpcode.Lbu, R3000aRegister.S7, T0, 0x10),
            // 0x1F801020 (COM_DELAY): no device routes it, so it is the Runtime's flat
            // register store, which is what the interpreter reads and writes (the first
            // access Persona makes outside RAM).
            Li(T0, 0x1F801020u),
            Li(T1, 0x31125u),
            [Mem(R3000aOpcode.Sw, T1, T0, 0)],
            Load(R3000aOpcode.Lw, R3000aRegister.V0, T0, 0));

        var result = Run(words, dir, withRuntime: true);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        var snap = result.FinalSnapshot!;

        // The same accesses made directly on a fresh Runtime device graph are the
        // reference: the artifact must observe exactly the device state they produce.
        using var reference = new PsxDeviceGraph();
        void RefWrite(int width, uint pa, uint value) => reference.TryWrite(pa, width, value).Should().Be(PsxDeviceAccessStatus.Completed);
        uint RefRead(int width, uint pa)
        {
            reference.TryRead(pa, width, out var value).Should().Be(PsxDeviceAccessStatus.Completed);
            return value;
        }

        RefWrite(4, Timer1Target, 0x1234);
        var expectedTarget = RefRead(4, Timer1Target);
        RefWrite(2, InterruptMask, 0xAB);
        var expectedMask = RefRead(2, InterruptMask);
        RefWrite(1, CdRomIndex, 1);
        var expectedCdRom = RefRead(1, CdRomIndex);
        var expectedGpuStatBefore = RefRead(4, GpuStat);
        RefWrite(4, GpuStat, 0x08000001u);
        var expectedGpuStatAfter = RefRead(4, GpuStat);

        snap.Gpr[(int)R3000aRegister.S0].Should().Be(expectedTarget).And.Be(0x1234u);
        snap.Gpr[(int)R3000aRegister.S1].Should().Be(expectedMask).And.Be(0xABu);
        snap.Gpr[(int)R3000aRegister.S2].Should().Be(expectedCdRom);
        snap.Gpr[(int)R3000aRegister.S3].Should().Be(expectedGpuStatBefore).And.NotBe(0u, "GPUSTAT is device state, not a constant zero");
        snap.Gpr[(int)R3000aRegister.S4].Should().Be(expectedGpuStatAfter);
        snap.Gpr[(int)R3000aRegister.S4].Should().NotBe(expectedGpuStatBefore, "the GP1 display-mode write must reach the GPU");
        snap.Gpr[(int)R3000aRegister.S5].Should().Be(0xCAFEF00Du);
        snap.Gpr[(int)R3000aRegister.S6].Should().Be(0xCAFEu);
        snap.Gpr[(int)R3000aRegister.S7].Should().Be(0x0Du);
        snap.Gpr[(int)R3000aRegister.V0].Should().Be(0x31125u).And.Be(RefComDelay(reference));
    }

    private static uint RefComDelay(PsxDeviceGraph reference)
    {
        reference.TryWrite(0x1F801020u, 4, 0x31125u).Should().Be(PsxDeviceAccessStatus.Completed);
        reference.TryRead(0x1F801020u, 4, out var value).Should().Be(PsxDeviceAccessStatus.Completed);
        return value;
    }

    [Fact]
    public void DeviceGraph_AccessIsOneWidthAwareOperation_RoutedThroughTheGuestsOwnPath()
    {
        using var graph = new PsxDeviceGraph();

        // The GPU register window is a 32-bit device: a word read is the adapter's
        // register, which no sequence of byte requests reproduces.
        graph.TryRead(GpuStat, 4, out var word).Should().Be(PsxDeviceAccessStatus.Completed);
        word.Should().Be(graph.GpuAdapter.ReadRegister(GpuStat));

        graph.TryRead(GpuStat, 2, out _).Should().Be(PsxDeviceAccessStatus.Completed);
        graph.TryRead(GpuStat, 1, out _).Should().Be(PsxDeviceAccessStatus.Completed);
    }

    [Theory]
    [InlineData(0u)]          // main RAM is owned by the caller
    [InlineData(0x001FFFFCu)]
    [InlineData(0x1FC00000u)] // BIOS ROM: no image
    [InlineData(0x1FC7FFFCu)]
    public void DeviceGraph_RamAndBiosRom_AreUnsupported_NotZero(uint physical)
    {
        using var graph = new PsxDeviceGraph();

        graph.TryRead(physical, 4, out _).Should().Be(PsxDeviceAccessStatus.Unsupported);
        graph.TryWrite(physical, 4, 1).Should().Be(PsxDeviceAccessStatus.Unsupported);
    }

    [Theory]
    [InlineData(0x00800000u)] // first byte past the RAM mirror window
    [InlineData(0x1F803000u)] // past the native register window
    [InlineData(0xFFFFFFFFu)] // untranslatable
    public void DeviceGraph_AddressOutsideEveryRegion_IsOpenBus(uint physical)
    {
        using var graph = new PsxDeviceGraph();

        graph.TryWrite(physical, 4, 0xFFFFFFFFu).Should().Be(PsxDeviceAccessStatus.Completed);
        graph.TryRead(physical, 4, out var value).Should().Be(PsxDeviceAccessStatus.Completed);
        value.Should().Be(0u, "open bus reads 0 and a write to it is ignored");
    }

    [Fact]
    public void DeviceGraph_InvalidWidth_IsRejected()
    {
        using var graph = new PsxDeviceGraph();

        var read = () => graph.TryRead(GpuStat, 3, out _);
        var write = () => graph.TryWrite(GpuStat, 8, 0);

        read.Should().Throw<ArgumentOutOfRangeException>();
        write.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- fail closed ----------------------------------------------------------

    [Theory]
    [InlineData(0x1FC00000u, "ARTIFACT_MMIO_UNSUPPORTED")]
    [InlineData(0x1FC7FFFCu, "ARTIFACT_MMIO_UNSUPPORTED")]
    public void Mmio_UnsupportedRead_StopsWithAClassifiedDiagnostic_NeverZero(uint address, string code)
    {
        using var dir = new TempDirectory();
        var words = Program(
            Li(T0, address),
            Li(R3000aRegister.S0, 0x55u),
            Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0),
            Li(R3000aRegister.S1, 0x66u));

        var result = Run(words, dir, withRuntime: true);

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be(code);
        result.DiagnosticMessage.Should().Contain("MMIO read");
    }

    [Fact]
    public void Mmio_AddressOutsideEveryRegion_IsOpenBusAtTheRuntime()
    {
        using var dir = new TempDirectory();
        var words = Program(
            Li(T0, 0xFFFE0130u),
            Li(R3000aRegister.S0, 0x55u),
            Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0),
            Li(T1, 0x1234u),
            [Mem(R3000aOpcode.Sw, T1, T0, 0)],
            Li(T0, 0x80800000u),
            Li(R3000aRegister.S1, 0x66u),
            Load(R3000aOpcode.Lw, R3000aRegister.S1, T0, 0));

        var result = Run(words, dir, withRuntime: true);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S0].Should().Be(0u);
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    [Fact]
    public void Mmio_UnsupportedWrite_StopsWithAClassifiedDiagnostic_NeverDropped()
    {
        using var dir = new TempDirectory();
        var words = Program(Li(T0, 0x1FC00000u), Li(T1, 1), [Mem(R3000aOpcode.Sw, T1, T0, 0)]);

        var result = Run(words, dir, withRuntime: true);

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_MMIO_UNSUPPORTED");
        result.DiagnosticMessage.Should().Contain("MMIO write");
    }

    [Fact]
    public void Mmio_WithoutAHostBridge_StopsTheRunInsteadOfReadingZero()
    {
        using var dir = new TempDirectory();
        var words = Program([Lui(T0, 0x1F80)], Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0x1814));

        var result = Run(words, dir, withRuntime: false);

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_FAILED");
        result.DiagnosticMessage.Should().Contain($"exit {RecompiledArtifactCodeGen.MmioUnavailableExitCode}");
    }

    [Theory]
    [InlineData("X", RecompiledArtifactCodeGen.MmioRefusedExitCode)]
    [InlineData("Q", RecompiledArtifactCodeGen.MmioProtocolExitCode)]
    [InlineData("V notanumber", RecompiledArtifactCodeGen.MmioProtocolExitCode)]
    [InlineData(null, RecompiledArtifactCodeGen.MmioProtocolExitCode)]
    public void Artifact_RefusedOrMalformedHostReply_StopsWithoutASnapshot(string? reply, int expectedExit)
    {
        using var dir = new TempDirectory();
        var words = Program([Lui(T0, 0x1F80)], Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0x1814));
        Run(words, dir, withRuntime: false);

        var run = RunScripted(dir, _ => reply);

        run.ExitCode.Should().Be(expectedExit);
        run.HasSnapshot.Should().BeFalse("a refused access must not run on as if it had returned a value");
        run.Requests.Should().ContainSingle();
    }

    /// <summary>Builds a stand-in child that speaks the handshake, then sends <c>line</c>.</summary>
    internal sealed class FakeChildBuildService(string line) : IGeneratedHostBuildService
    {
        public GeneratedHostBuildResult Build(GeneratedHostBuildRequest request)
        {
            var source =
                "#include <stdio.h>\n" +
                "int main(void) {\n" +
                "  char b[64];\n" +
                $"  printf(\"{RecompiledArtifactCodeGen.ProtocolInitLine}\\n\"); fflush(stdout);\n" +
                "  if (scanf(\"%63s\", b) != 1) return 1;\n" +
                $"  printf(\"{line}\\n\"); fflush(stdout);\n" +
                "  while (getchar() != EOF) {}\n" +
                "  return 0;\n" +
                "}\n";
            return new GeneratedHostBuildService().Build(request with { Source = source });
        }
    }

    [Theory]
    [InlineData("RHOST_MMIO_READ 3 6144")]          // invalid width
    [InlineData("RHOST_MMIO_READ 0 6144")]
    [InlineData("RHOST_MMIO_READ 8 6144")]
    [InlineData("RHOST_MMIO_READ 4")]               // missing address
    [InlineData("RHOST_MMIO_READ 4 6144 1")]        // extra field
    [InlineData("RHOST_MMIO_READ four 6144")]       // not a number
    [InlineData("RHOST_MMIO_READ 4 -1")]            // not an unsigned address
    [InlineData("RHOST_MMIO_WRITE 4 6144")]         // missing value
    [InlineData("RHOST_MMIO_WRITE 1 6144 256")]     // value wider than the access
    [InlineData("RHOST_MMIO_WRITE 2 6144 65536")]
    [InlineData("RHOST_MMIO_WRITE 4 6144 4294967296")]
    public void Host_MalformedMmioRequest_IsAProtocolFailure_NotAGuestAccess(string line)
    {
        using var dir = new TempDirectory();
        var words = Program([MipsEncoding.Nop]);

        var result = Run(words, dir, withRuntime: true, new FakeChildBuildService(line));

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_HOST_PROTOCOL_FAILED");
    }

    // ---- BIOS RAM access is unchanged ------------------------------------------

    [Fact]
    public void BiosHle_StillReadsAndWritesTheArtifactOwnedRam_WhileMmioIsBridged()
    {
        // A0:3C putchar reads nothing from RAM, but BiosHleRuntime seeds its jump
        // tables through the byte R/W requests at the handshake and the guest then
        // calls it: both must keep working next to MMIO accesses.
        var words = Program(
            [Ori(R3000aRegister.T1, Zero, BiosHleRuntime.PutCharFunction)],
            [Ori(R3000aRegister.A0, Zero, (ushort)'P')],
            [MipsEncoding.JumpAndLink(BiosJumpTables.A0VectorAddress), MipsEncoding.Nop],
            [Lui(T0, 0x1F80)],
            Load(R3000aOpcode.Lw, R3000aRegister.S0, T0, 0x1814),
            [Ori(R3000aRegister.S1, Zero, 0x55)]);
        using var dir = new TempDirectory();
        var sink = new CollectingSink();
        var program = ReachableProgramBuilder.Build(Entry, words, Entry);
        using var engine = new RecompiledHostExecutionEngine(
            program, words, Entry, new GeneratedHostBuildService(), dir.FullPath,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var result = new ExecutionOrchestrator().Execute(engine, new ExitHandoff(), Request());

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        sink.Bytes.Should().Equal((byte)'P');
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().Be(0x55u);
        using var reference = new PsxDeviceGraph();
        reference.TryRead(GpuStat, 4, out var gpuStat);
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S0].Should().Be(gpuStat).And.NotBe(0u);
    }

    private sealed class CollectingSink : IRuntimeOutputSink
    {
        public List<byte> Bytes { get; } = [];

        public void WriteByte(byte value) => Bytes.Add(value);
    }
}
