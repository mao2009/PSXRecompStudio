using System.Diagnostics;
using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The host side of mixed execution for one artifact run (Issue #693, ADR-012/014/015/016/025 amendments): when the
/// artifact offers a transfer to an in-image PC it has no block for, hand the CPU to the interpreter on the host's own
/// device graph core, and give it back at a clean compiled block entry.
/// </summary>
/// <remarks>
/// <para>
/// Ownership. Devices, interrupt controller, DMA, timers, GPU, CD-ROM and the <see cref="DeviceScheduler"/> are the
/// host's single graph; the interpreter steps that graph's own core, so none of them is copied. Guest RAM is the
/// artifact's <c>artifact_ram</c>; the core's RAM is a working copy kept in step by page-granular copy-sync. CPU state
/// (GPR, HI/LO, SR/CAUSE/EPC) is copied in and out explicitly.
/// </para>
/// <para>
/// Sync invariant. <c>_synced</c> is the RAM image the artifact and this session last agreed on (all zero at first, like a
/// fresh core). Entering: the artifact sends the pages that differ from it; before they are applied the core's RAM is
/// first restored to <c>_synced</c> where it drifted (a device the artifact path never intended to write the core), so
/// the core equals artifact RAM when the segment starts. Returning: the pages the segment changed are staged in the
/// artifact and applied only by a verified commit, so artifact RAM is never half-updated; every other outcome leaves it
/// untouched.
/// </para>
/// </remarks>
[Infrastructure]
internal sealed class ArtifactFallbackSession : IDisposable
{
    private const int PageSize = RecompiledArtifactCodeGen.FallbackPageSize;
    private const int PageCount = (int)(PSXCoreWrapper.RamSize / PageSize);
    private const int ReplyFieldCount = 9;
    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;

    private readonly MixedFallbackOptions _options;
    private readonly IReadOnlyList<uint> _imageWords;
    private readonly uint _imageLoadAddress;
    private readonly IReadOnlySet<uint> _blockEntryPcs;
    private readonly PsxDeviceGraph _devices;
    private readonly ArtifactDeviceRam _deviceRam;
    private readonly InterpreterTitleExecutionEngine _interpreter;
    private readonly Action<string> _send;
    private readonly Func<string> _readReply;
    private readonly byte[] _synced = new byte[PSXCoreWrapper.RamSize];
    private readonly byte[] _scratch = new byte[PSXCoreWrapper.RamSize];
    private readonly Dictionary<uint, (ulong Entries, ulong Instructions, uint LastReturnPc)> _targets = [];
    private readonly Stopwatch _transferClock = new();
    private readonly Stopwatch _fallbackClock = new();

    private uint _transitions;
    private uint _returns;
    private ulong _fallbackInstructions;
    private ulong _pagesToInterpreter;
    private ulong _pagesToArtifact;

    public ArtifactFallbackSession(
        MixedFallbackOptions options,
        IReadOnlyList<uint> imageWords,
        uint imageLoadAddress,
        IReadOnlySet<uint> blockEntryPcs,
        PsxDeviceGraph devices,
        DeviceScheduler scheduler,
        ArtifactDeviceRam deviceRam,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory,
        BiosExceptionChain? exceptionChain,
        Action<string> send,
        Func<string> readReply,
        bool guestFirmware = false,
        Action<InterpreterTitleExecutionEngine, uint>? fetchObserver = null,
        Action<MixedFallbackTransition>? transitionObserver = null,
        LoadedCodeTable? loadedCode = null)
    {
        _transitionObserver = transitionObserver;
        _guestFirmware = guestFirmware;
        _loadedCode = loadedCode ?? LoadedCodeTable.Empty;
        _options = options;
        _imageWords = imageWords;
        _imageLoadAddress = imageLoadAddress;
        _blockEntryPcs = blockEntryPcs;
        _devices = devices;
        _deviceRam = deviceRam;
        _send = send;
        _readReply = readReply;
        _interpreter = InterpreterTitleExecutionEngine.Attach(
            imageWords, imageLoadAddress, devices, scheduler, biosRuntimeFactory, exceptionChain, guestFirmware);
        if (fetchObserver is not null)
        {
            var interpreter = _interpreter;
            _interpreter.FetchObserver = pc => fetchObserver(interpreter, pc);
        }
    }

    private readonly bool _guestFirmware;
    private readonly Action<MixedFallbackTransition>? _transitionObserver;

    /// <summary>The pre-generated versions of RAM-placed code (Issue #732).</summary>
    private readonly LoadedCodeTable _loadedCode;

    /// <summary>
    /// Whether the artifact may resume at <paramref name="pc"/>: it has a static block there, or a version of RAM-placed
    /// code whose words the interpreter's RAM (equal to <c>artifact_ram</c> once written back) holds now. Stale or unknown
    /// RAM code is no return point; the artifact makes the same check and would only offer the PC back.
    /// </summary>
    private bool IsReturnPoint(uint pc)
    {
        if (_blockEntryPcs.Contains(pc)) return true;
        if (!_loadedCode.Contains(pc)) return false;
        // The artifact's own translation (artifact_translate / artifact_ram_offset): KUSEG as is, KSEG0/1 masked, the
        // low 8 MiB mirroring the 2 MiB RAM; a unit running off the end of RAM is not RAM code.
        var physical = pc <= 0x7FFFFFFFu ? pc : pc & 0x1FFFFFFFu;
        if (pc >= 0xC0000000u || physical >= 0x00800000u) return false;
        physical &= PSXCoreWrapper.RamSize - 1;
        foreach (var version in _loadedCode.VersionsAt(pc))
        {
            // Match the artifact's per-version byte span without overflowing the size calculation. A longer
            // version must not hide a current shorter version at the same entry.
            if (version.Words.Count == 0 || (ulong)version.Words.Count * 4ul > PSXCoreWrapper.RamSize - physical) continue;
            var current = true;
            for (var i = 0; i < version.Words.Count && current; i++)
            {
                current = _devices.Core.ReadMemory32(physical + (uint)i * 4u) == version.Words[i];
            }

            if (current) return true;
        }

        return false;
    }

    /// <summary>What <see cref="Handle"/> decided about one offered transfer.</summary>
    public enum Decision
    {
        /// <summary>Not a fallback case: the caller declines the transfer exactly as without mixed execution.</summary>
        Ineligible,

        /// <summary>The segment ran and the artifact was given back control: a full <c>D</c> decision was sent.</summary>
        Resumed,

        /// <summary>Fail closed: the caller stops the run with <see cref="StopCode"/> / <see cref="StopMessage"/>.</summary>
        Stopped,
    }

    public string? StopCode { get; private set; }

    public string? StopMessage { get; private set; }

    public MixedFallbackEvidence Evidence => new(
        _transitions,
        _returns,
        _fallbackInstructions,
        _pagesToInterpreter,
        _pagesToArtifact,
        _targets.OrderBy(static t => t.Key)
            .Select(static t => new MixedFallbackTarget(t.Key, t.Value.Entries, t.Value.Instructions, t.Value.LastReturnPc))
            .ToArray());

    public MixedFallbackTimings Timings => new(_transferClock.Elapsed.TotalMilliseconds, _fallbackClock.Elapsed.TotalMilliseconds);

    public void Dispose() => _interpreter.Dispose();

    /// <summary>
    /// Decides one transfer the artifact offered for <paramref name="pc"/> (no block, not a BIOS or exception vector —
    /// the caller has already routed those). A protocol violation throws, which the pump classifies as
    /// <c>ARTIFACT_HOST_PROTOCOL_FAILED</c>.
    /// </summary>
    public Decision Handle(uint pc, uint[] gpr)
    {
        StopCode = null;
        StopMessage = null;

        // Static eligibility: an aligned PC inside the PS-X EXE text image. The image is the only executable region a
        // fallback ever runs; RAM-generated code, out-of-image PCs and every vector stay with the existing fail-closed path.
        // A guest firmware (Issue #732) is the exception: it runs code it wrote to RAM, its own vectors and a loaded
        // executable by design, reached directly as well as indirectly, so any aligned PC without a block is eligible.
        var imageEnd = (ulong)_imageLoadAddress + (ulong)_imageWords.Count * 4UL;
        var inImage = pc >= _imageLoadAddress && pc < imageEnd;
        // RAM-placed code is never in _blockEntryPcs: the artifact offers it only when no version matched (Issue #732).
        if ((pc & 3u) != 0 || (!inImage && !_guestFirmware) || _blockEntryPcs.Contains(pc))
        {
            return Decision.Ineligible;
        }

        _transferClock.Start();
        try
        {
            _send($"{RecompiledArtifactCodeGen.ProtocolFallbackQueryCommand} {RecompiledArtifactCodeGen.ProtocolFallbackVersion}");
            var header = ReadHeader();
            if (header is null || (!header.Value.Indirect && !_guestFirmware))
            {
                // A refusal, or a transfer that did not come from JR/JALR: not a runtime-discovered target.
                return Decision.Ineligible;
            }

            if (_transitions >= _options.MaxTransitions)
            {
                return Stop(
                    MixedFallbackDiagnostics.TransitionBudgetExhausted,
                    $"The run already made {_transitions} artifact-to-interpreter handoffs (budget {_options.MaxTransitions}); " +
                    $"it stopped at pc 0x{pc:X8}.");
            }

            var incoming = ReadPages(header.Value.DirtyPages);
            ApplyToCore(incoming);
            _transitions++;
            _pagesToInterpreter += (ulong)incoming.Count;

            var entry = new FallbackCpuState(
                gpr, header.Value.Hi, header.Value.Lo, pc, header.Value.Sr, header.Value.Cause, header.Value.Epc);

            _transferClock.Stop();
            FallbackSegmentOutcome outcome;
            _deviceRam.RedirectTo(_devices.Bus);
            _fallbackClock.Start();
            try
            {
                outcome = _interpreter.RunFallbackSegment(entry, IsReturnPoint, _options.SegmentInstructionBudget);
            }
            finally
            {
                _fallbackClock.Stop();
                _deviceRam.RedirectTo(null);
            }

            _transferClock.Start();
            _fallbackInstructions += outcome.RetiredInstructions;
            var previous = _targets.GetValueOrDefault(pc);
            _targets[pc] = (
                previous.Entries + 1,
                previous.Instructions + outcome.RetiredInstructions,
                outcome.Status == FallbackSegmentStatus.Returned ? outcome.State.Pc : previous.LastReturnPc);
            _transitionObserver?.Invoke(new MixedFallbackTransition(
                pc, header.Value.Indirect, outcome.Status, outcome.State.Pc, outcome.RetiredInstructions, outcome.DiagnosticCode));

            if (outcome.Status != FallbackSegmentStatus.Returned)
            {
                return Stop(outcome.DiagnosticCode!, outcome.DiagnosticMessage!);
            }

            return WriteBack(outcome.State) ? Decision.Resumed : Decision.Stopped;
        }
        finally
        {
            _transferClock.Stop();
        }
    }

    private Decision Stop(string code, string message)
    {
        StopCode = code;
        StopMessage = message;
        return Decision.Stopped;
    }

    private readonly record struct Header(bool Indirect, uint Irq, uint Hi, uint Lo, uint Sr, uint Cause, uint Epc, int DirtyPages);

    /// <summary>Reads the reply to <c>F</c>; null when the artifact refused (an unsupported version).</summary>
    private Header? ReadHeader()
    {
        var reply = _readReply();
        if (reply.StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackRefusedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!reply.StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackReplyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected a '{RecompiledArtifactCodeGen.ProtocolFallbackReplyPrefix}' reply, received '{Truncate(reply)}'.");
        }

        var parts = reply[RecompiledArtifactCodeGen.ProtocolFallbackReplyPrefix.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != ReplyFieldCount
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != RecompiledArtifactCodeGen.ProtocolFallbackVersion
            || parts[1] is not ("0" or "1")
            || parts[2] is not ("0" or "1")
            || !TryParseU32(parts[3], out var hi) || !TryParseU32(parts[4], out var lo)
            || !TryParseU32(parts[5], out var sr) || !TryParseU32(parts[6], out var cause)
            || !TryParseU32(parts[7], out var epc)
            || !int.TryParse(parts[8], NumberStyles.None, CultureInfo.InvariantCulture, out var dirty)
            || dirty > PageCount)
        {
            throw new InvalidOperationException($"Malformed fallback reply '{Truncate(reply)}'.");
        }

        return new Header(parts[1] == "1", uint.Parse(parts[2], CultureInfo.InvariantCulture), hi, lo, sr, cause, epc, dirty);
    }

    /// <summary>Pulls the artifact's dirty pages and validates every one before anything is applied.</summary>
    private List<(int Index, byte[] Data)> ReadPages(int expected)
    {
        _send(RecompiledArtifactCodeGen.ProtocolFallbackPagesCommand);
        var pages = new List<(int, byte[])>(expected);
        var hash = FnvOffset;
        var previous = -1;
        while (true)
        {
            var line = _readReply();
            if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackPagesEndPrefix, StringComparison.Ordinal))
            {
                if (!TryParseU32(line[RecompiledArtifactCodeGen.ProtocolFallbackPagesEndPrefix.Length..].Trim(), out var sent)
                    || sent != hash
                    || pages.Count != expected)
                {
                    throw new InvalidOperationException("The fallback page stream failed its count or hash check.");
                }

                return pages;
            }

            if (!line.StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a '{RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix}' line, received '{Truncate(line)}'.");
            }

            var fields = line[RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix.Length..].Split(' ');
            if (fields.Length != 2
                || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index <= previous
                || index >= PageCount
                || fields[1].Length != PageSize * 2
                || pages.Count >= expected)
            {
                throw new InvalidOperationException($"Malformed fallback page line '{Truncate(line)}'.");
            }

            var data = Convert.FromHexString(fields[1]);
            hash = HashPage(hash, index, data);
            pages.Add((index, data));
            previous = index;
        }
    }

    /// <summary>Restores core RAM to the agreed image where it drifted, then applies the artifact's dirty pages.</summary>
    private void ApplyToCore(List<(int Index, byte[] Data)> incoming)
    {
        var core = _devices.Core;
        core.CopyRamTo(_scratch);
        for (var page = 0; page < PageCount; page++)
        {
            var offset = page * PageSize;
            if (!_scratch.AsSpan(offset, PageSize).SequenceEqual(_synced.AsSpan(offset, PageSize)))
            {
                core.CopyRamFrom((uint)offset, _synced.AsSpan(offset, PageSize));
            }
        }

        foreach (var (index, data) in incoming)
        {
            core.CopyRamFrom((uint)(index * PageSize), data);
            data.CopyTo(_synced, index * PageSize);
        }
    }

    /// <summary>
    /// Gives control back: stages the pages the segment changed, commits them (verified), writes the CPU state, and
    /// sends the decision. False when the artifact refused the commit; it then holds its old RAM and the run stops.
    /// </summary>
    private bool WriteBack(FallbackCpuState state)
    {
        var core = _devices.Core;
        core.CopyRamTo(_scratch);
        var dirty = new List<int>();
        var hash = FnvOffset;
        for (var page = 0; page < PageCount; page++)
        {
            var offset = page * PageSize;
            if (!_scratch.AsSpan(offset, PageSize).SequenceEqual(_synced.AsSpan(offset, PageSize)))
            {
                dirty.Add(page);
                hash = HashPage(hash, page, _scratch.AsSpan(offset, PageSize));
            }
        }

        foreach (var page in dirty)
        {
            _send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolFallbackStageCommand} {page} {Convert.ToHexStringLower(_scratch.AsSpan(page * PageSize, PageSize))}"));
        }

        _send(string.Create(
            CultureInfo.InvariantCulture,
            $"{RecompiledArtifactCodeGen.ProtocolFallbackCommitCommand} {dirty.Count} {hash}"));
        var reply = _readReply();
        if (!string.Equals(reply, RecompiledArtifactCodeGen.ProtocolWriteAck, StringComparison.Ordinal))
        {
            if (reply.StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackRefusedPrefix, StringComparison.Ordinal))
            {
                Stop(
                    MixedFallbackDiagnostics.SyncFailed,
                    "The artifact refused the RAM write-back commit (count or hash mismatch); its RAM was left unchanged.");
                return false;
            }

            throw new InvalidOperationException($"Expected '{RecompiledArtifactCodeGen.ProtocolWriteAck}' to a fallback commit, received '{Truncate(reply)}'.");
        }

        foreach (var page in dirty)
        {
            _scratch.AsSpan(page * PageSize, PageSize).CopyTo(_synced.AsSpan(page * PageSize, PageSize));
        }

        _pagesToArtifact += (ulong)dirty.Count;
        _returns++;

        var line = new System.Text.StringBuilder();
        line.Append(RecompiledArtifactCodeGen.ProtocolFallbackStateCommand);
        var irq = _devices.InterruptControllerAdapter.HasPendingInterrupts ? 1 : 0;
        line.Append(CultureInfo.InvariantCulture, $" {state.Hi} {state.Lo} {state.Sr} {state.Cause} {state.Epc} {irq}");
        for (var i = 1; i < state.Gpr.Count; i++)
        {
            line.Append(CultureInfo.InvariantCulture, $" {state.Gpr[i]}");
        }

        _send(line.ToString());
        _send(string.Create(
            CultureInfo.InvariantCulture,
            $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {state.Pc} 0 0"));
        return true;
    }

    private static uint HashPage(uint hash, int page, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < 4; i++)
        {
            hash = (hash ^ (byte)(page >> (8 * i))) * FnvPrime;
        }

        foreach (var value in data)
        {
            hash = (hash ^ value) * FnvPrime;
        }

        return hash;
    }

    private static bool TryParseU32(string text, out uint value) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..120] + "...";
}
