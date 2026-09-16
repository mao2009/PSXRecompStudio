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
/// (#366). The host-transfer hook relays BIOS decisions to the shared
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

    private readonly RecompilerHostExecutor.CompiledBinary _binary;
    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> _biosRuntimeFactory;
    private readonly IReadOnlySet<uint> _blockEntryPcs;
    private readonly string _segmentInputPath;
    private readonly byte[] _ram;

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
        _biosRuntimeFactory = biosRuntimeFactory;
    }

    public string Name => EngineName;

    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Array.Clear(_ram);
        foreach (var item in request.InitialMemory)
        {
            var physical = RecompilerGuestMemory.Translate(item.Address);
            if (physical < RamSize)
            {
                _ram[physical] = item.Value;
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

        // Guest RAM continuity is part of this segment's result: commit the
        // dump only after the whole thing parses, or the next segment would
        // silently inherit stale bytes.
        if (TryApplyRamDump(stdout) is string ramError)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.MalformedResult,
                "MALFORMED_RAM_DUMP",
                ramError);
        }

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

        File.WriteAllText(_segmentInputPath, sb.ToString());
    }

    /// <summary>
    /// Parses the full RAMHEX dump into a staging buffer and copies it into
    /// <c>_ram</c> only when the complete dump is present and valid.
    /// </summary>
    /// <remarks>The generated driver always emits exactly one 512-byte block per
    /// offset, sequentially from 0. Anything short of that — a truncated dump, a
    /// bad offset, a non-hex payload — is a malformed segment result and must
    /// not leave stale RAM for the next segment.</remarks>
    /// <returns>An error message when the dump is not complete and valid, or
    /// null when it was fully applied.</returns>
    private string? TryApplyRamDump(string stdout)
    {
        var staged = new byte[RamSize];
        uint nextOffset = 0;
        var blocks = 0;

        foreach (var line in stdout.Split('\n'))
        {
            if (!line.StartsWith(RamLinePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var space = line.IndexOf(' ', RamLinePrefix.Length);
            if (space < 0)
            {
                return "RAMHEX line without an offset.";
            }

            if (!uint.TryParse(
                    line.AsSpan(RamLinePrefix.Length, space - RamLinePrefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var offset)
                || offset >= RamSize
                || offset != nextOffset)
            {
                return $"Expected RAMHEX block at offset 0x{nextOffset:X}, found '{line}'.";
            }

            var hex = line.AsSpan(space + 1);
            if (hex.Length > 0 && hex[^1] == '\r')
            {
                hex = hex[..^1];
            }

            if (hex.Length != RamBlockSize * 2)
            {
                return $"RAMHEX block at offset 0x{offset:X} has {hex.Length} hex chars, expected {RamBlockSize * 2}.";
            }

            for (var i = 0; i < hex.Length / 2; i++)
            {
                if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    return $"RAMHEX block at offset 0x{offset:X} has a non-hex payload.";
                }

                staged[offset + (uint)i] = value;
            }

            blocks++;
            nextOffset += RamBlockSize;
        }

        var expected = RamSize / RamBlockSize;
        if (blocks != expected)
        {
            return $"RAMHEX dump incomplete: {blocks} of {expected} blocks received.";
        }

        Array.Copy(staged, _ram, RamSize);
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