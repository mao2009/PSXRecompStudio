using System.Globalization;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Execution;

#pragma warning disable AARC003

/// <summary>
/// An <see cref="IRecompiledExecutionEngine"/> that compiles the guest program
/// once and runs each segment as a separate generated-host process, keeping guest
/// RAM continuity between segments through the driver's full-RAM dump/preload
/// (#366). The hardware-register window — the guest-visible DMA/timer/interrupt
/// MMIO state the interpreter keeps in its persistent core's <c>hw_regs</c> — is
/// carried across the same process forks via the driver's HWREG dump/preload
/// (Issue #387). The host-transfer hook relays BIOS decisions to the shared
/// <c>BiosVectorDispatch</c> exactly like the differential harness (Issue #362).
/// </summary>
/// <remarks>
/// Like <c>RecompilerHostExecutor</c> this lives in the Test assembly: it
/// invokes gcc and manipulates files and processes, all forbidden in the Domain
/// layer by the architecture analyzer.
/// </remarks>
[Test]
internal sealed class HostTitleExecutionEngine : IRecompiledExecutionEngine
{
    public const string EngineName = "host-gcc-full-title";

    private const int RamSize = 2 * 1024 * 1024;
    private const int RamBlockSize = 512;
    private const int RunTimeoutMs = 30000;
    private const string RamLinePrefix = "RAMHEX ";

    /// <summary>
    /// The software-visible hardware-register window, mirroring the native core's
    /// 8 KiB <c>hw_regs</c> buffer at PSX_HW_REG_BASE (Issue #387). These two are
    /// the driver's <c>PSX_TEST_HW_BASE</c>/<c>PSX_TEST_HW_SIZE</c>, which in turn
    /// mirror the native core's <c>PSX_HW_REG_BASE</c>/<c>PSX_HW_REG_SIZE</c>:
    /// deliberately not <c>Ps1MemoryMap.HwRegBase</c>/<c>HwRegEnd</c>, whose end is
    /// the narrower 4 KiB I/O-port span and would route half this window nowhere.
    /// </summary>
    private const uint HwBase = 0x1F801000u;
    private const int HwSize = 8 * 1024;
    private const string HwLinePrefix = "HWREG ";

    private readonly RecompilerHostExecutor.CompiledBinary _binary;
    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> _biosRuntimeFactory;
    private readonly IReadOnlySet<uint> _blockEntryPcs;
    private readonly string _segmentInputPath;
    private readonly byte[] _ram;
    private readonly byte[] _hw;

    public HostTitleExecutionEngine(
        RecompilerDifferentialFixture fixture,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(biosRuntimeFactory);

        _binary = new RecompilerHostExecutor().CompileRecompiledBinary(fixture);
        _segmentInputPath = Path.Combine(_binary.DirectoryPath, "segment-input.txt");
        _blockEntryPcs = LowerProgram(fixture).Blocks.Select(static block => block.EntryPc).ToHashSet();
        _ram = new byte[RamSize];
        _hw = new byte[HwSize];
        _biosRuntimeFactory = biosRuntimeFactory;
    }

    public string Name => EngineName;

    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Array.Clear(_ram);
        Array.Clear(_hw);
        foreach (var item in request.InitialMemory)
        {
            // Route each seeded byte the same way the interpreter's core does
            // (PSXMemory::Write8): RAM to RAM, the hardware-register window to
            // hw_regs, everything else dropped. Dropping the window here left the
            // first segment's HWREG preload all zeros, so a request that seeds
            // MMIO state diverged from the interpreter (Issue #387).
            var physical = RecompilerGuestMemory.Translate(item.Address);
            if (physical < RamSize)
            {
                _ram[physical] = item.Value;
            }
            else if (physical >= HwBase && physical < HwBase + (uint)HwSize)
            {
                _hw[physical - HwBase] = item.Value;
            }
        }
    }

    public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
    {
        ArgumentNullException.ThrowIfNull(segmentRequest);

        WriteSegmentInput(segmentRequest);
        var session = new RecompilerHostExecutor.HostTransferSession(_biosRuntimeFactory, _blockEntryPcs);

        var (exit, stdout, _) = RecompilerHostExecutor.RunProcess(
            _binary.BinaryPath,
            $"\"{_segmentInputPath}\" {RecompilerHostExecutor.HostTransferArgument} {RecompilerHostExecutor.FullRamArgument}",
            RunTimeoutMs,
            out var timedOut,
            session);

        if (timedOut)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.TimedOut,
                "HOST_SEGMENT_TIMEOUT",
                "Generated executable exceeded the full-title segment timeout.");
        }

        if (exit != 0 && !stdout.Contains("RSNAPSHOT_BEGIN"))
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed,
                "HOST_SEGMENT_FAILED",
                $"Generated executable failed (exit {exit}).");
        }

        var snapshot = SnapshotParser.Parse(stdout);
        if (snapshot is null)
        {
            var message = stdout.Length <= 2000 ? stdout : stdout.Substring(0, 2000) + "...";
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.MalformedResult,
                "MALFORMED_SEGMENT_SNAPSHOT",
                message);
        }

        // Guest RAM and hardware-register continuity are part of this segment's
        // result: stage both dumps and only commit them once the whole output
        // parses, or the next segment would silently inherit stale bytes.
        var ramStaged = new byte[RamSize];
        if (TryParseHexDump(stdout, RamLinePrefix, RamBlockSize, ramStaged) is string ramError)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.MalformedResult,
                "MALFORMED_RAM_DUMP",
                ramError);
        }

        var hwStaged = new byte[HwSize];
        if (TryParseHexDump(stdout, HwLinePrefix, RamBlockSize, hwStaged) is string hwError)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.MalformedResult,
                "MALFORMED_HW_DUMP",
                hwError);
        }

        Array.Copy(ramStaged, _ram, RamSize);
        Array.Copy(hwStaged, _hw, HwSize);

        return new RecompilerExecutionResult(
            RecompilerExecutionStatus.Completed, snapshot, session.DiagnosticCode, session.DiagnosticMessage);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (Directory.Exists(_binary.DirectoryPath))
            {
                try
                {
                    Directory.Delete(_binary.DirectoryPath, true);
                }
                catch (IOException)
                {
                }
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private bool _disposed;

    private void WriteSegmentInput(TitleExecutionSegmentRequest segmentRequest)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < segmentRequest.Gpr.Count; i++)
        {
            sb.Append(segmentRequest.Gpr[i]).Append(' ');
        }
        sb.Append('\n');
        sb.Append(segmentRequest.Hi).Append('\n');
        sb.Append(segmentRequest.Lo).Append('\n');
        sb.Append(segmentRequest.Pc).Append('\n');
        sb.Append(segmentRequest.Budget).Append('\n');

        sb.Append(0).Append('\n'); // init count
        sb.Append(0).Append('\n'); // memory window count

        // Every segment carries the full RAM image through the driver's preload,
        // so guest writes and the Load-seeded initial memory survive the process
        // restart (the driver zeroes test_ram first, then applies this preload).
        sb.Append(RamSize / RamBlockSize).Append('\n');
        for (var offset = 0; offset < RamSize; offset += RamBlockSize)
        {
            sb.Append(offset).Append(' ');
            var hex = Convert.ToHexString(_ram, offset, RamBlockSize);
            sb.Append(hex.ToLowerInvariant()).Append('\n');
        }

        // And the hardware-register window, so DMA/timer/interrupt state written
        // by an earlier segment survives the fork into this one (Issue #387).
        sb.Append(HwSize / RamBlockSize).Append('\n');
        for (var offset = 0; offset < HwSize; offset += RamBlockSize)
        {
            sb.Append(offset).Append(' ');
            var hex = Convert.ToHexString(_hw, offset, RamBlockSize);
            sb.Append(hex.ToLowerInvariant()).Append('\n');
        }

        File.WriteAllText(_segmentInputPath, sb.ToString());
    }

    /// <summary>
    /// Parses one full <c>&lt;prefix&gt; &lt;offset&gt; &lt;hex&gt;</c> block dump
    /// (RAMHEX for guest RAM, HWREG for the hardware-register window) into
    /// <paramref name="staged"/>. The generated driver always emits exactly one
    /// block per offset, sequentially from 0; anything short of that — a truncated
    /// dump, a bad offset, a non-hex payload — is a malformed segment result and
    /// must not leave stale state for the next segment. The caller commits
    /// <paramref name="staged"/> only after every dump parses.
    /// </summary>
    /// <returns>An error message when the dump is not complete and valid, or
    /// null when it was fully parsed.</returns>
    private static string? TryParseHexDump(string stdout, string linePrefix, int blockSize, byte[] staged)
    {
        var blocks = 0;
        uint nextOffset = 0;
        var expected = staged.Length / blockSize;

        foreach (var line in stdout.Split('\n'))
        {
            if (!line.StartsWith(linePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var space = line.IndexOf(' ', linePrefix.Length);
            if (space < 0)
            {
                return $"{linePrefix}line without an offset.";
            }

            if (!uint.TryParse(
                    line.AsSpan(linePrefix.Length, space - linePrefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var offset)
                || offset >= staged.Length
                || offset != nextOffset)
            {
                return $"Expected {linePrefix}block at offset 0x{nextOffset:X}, found '{line}'.";
            }

            var hex = line.AsSpan(space + 1);
            if (hex.Length > 0 && hex[^1] == '\r')
            {
                hex = hex[..^1];
            }

            if (hex.Length != blockSize * 2)
            {
                return $"{linePrefix}block at offset 0x{offset:X} has {hex.Length} hex chars, expected {blockSize * 2}.";
            }

            for (var i = 0; i < blockSize; i++)
            {
                if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    return $"{linePrefix}block at offset 0x{offset:X} has a non-hex payload.";
                }

                staged[offset + i] = value;
            }

            blocks++;
            nextOffset += (uint)blockSize;
        }

        if (blocks != expected)
        {
            return $"{linePrefix}dump incomplete: {blocks} of {expected} blocks received.";
        }

        return null;
    }

    private static RecompilerIrProgram LowerProgram(RecompilerDifferentialFixture fixture)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            var instruction = R3000aDecoder.Decode(fixture.Instructions[i]);
            instructions.Add((instruction, fixture.PcOfInstruction(i)));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }
}

#pragma warning restore AARC003