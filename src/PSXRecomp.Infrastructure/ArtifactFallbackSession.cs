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
    private uint? _lastEntryPc;
    private uint? _lastReturnPc;
    private readonly Stopwatch _transferClock = new();
    private readonly Stopwatch _fallbackClock = new();

    // Issue #806: the return classification of this run; the step loop and the predicate write it, the host reads a snapshot.
    private readonly FallbackReturnDiagnostics _returnStats = new();
    private readonly Func<uint, uint> _ramWordAtPhysical;

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
        LoadedCodeTable? loadedCode = null,
        LoadedCodeMismatchDiagnostics? loadedCodeMismatch = null,
        Func<bool>? stopNow = null)
    {
        _transitionObserver = transitionObserver;
        _guestFirmware = guestFirmware;
        _loadedCode = loadedCode ?? LoadedCodeTable.Empty;
        _loadedCodeMismatch = loadedCodeMismatch;
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
        _interpreter.ReturnDiagnostics = _returnStats;
        _ramWordAtPhysical = RamWordAtPhysical;
        if (fetchObserver is not null || stopNow is not null)
        {
            var interpreter = _interpreter;
            _interpreter.FetchObserver = pc =>
            {
                // Issue #829: the interpreter's step loop only sees its own StopRequested, which nothing else sets. A stop
                // reaches a running fallback segment here, before the next instruction executes, so the segment ends.
                if (stopNow is not null && stopNow())
                {
                    interpreter.StopRequested = true;
                }

                fetchObserver?.Invoke(interpreter, pc);
            };
        }
    }

    private readonly bool _guestFirmware;
    private readonly Action<MixedFallbackTransition>? _transitionObserver;

    /// <summary>The pre-generated versions of RAM-placed code (Issue #732).</summary>
    private readonly LoadedCodeTable _loadedCode;

    /// <summary>Opt-in record of refused RAM-placed code (Issue #773); null keeps the handoff path unchanged.</summary>
    private readonly LoadedCodeMismatchDiagnostics? _loadedCodeMismatch;

    /// <summary>
    /// Whether the artifact may resume at <paramref name="pc"/>: it has a static block there, or a version of RAM-placed
    /// code whose words the interpreter's RAM (equal to <c>artifact_ram</c> once written back) holds now. Stale or unknown
    /// RAM code is no return point; the artifact makes the same check and would only offer the PC back.
    /// </summary>
    private bool IsReturnPoint(uint pc)
    {
        var verdict = FallbackReturnCheck.Classify(pc, _blockEntryPcs, _loadedCode, _ramWordAtPhysical);
        _returnStats.NoteVerdict(pc, verdict);
        return FallbackReturnCheck.IsAccepted(verdict);
    }

    private uint RamWordAtPhysical(uint physical) => _devices.Core.ReadMemory32(physical);

    /// <summary>
    /// The guest word at a virtual address, through the artifact's translation (as in <see cref="IsReturnPoint"/>). Outside
    /// the artifact's RAM it reads 0; such a PC has no RAM to compare, so a 0 there is a diagnostic placeholder and not
    /// evidence that a version matched (a compiled <c>nop</c> is also 0).
    /// </summary>
    private uint LoadedCodeWordAt(uint address)
    {
        if (!FallbackReturnCheck.TryRamOffset(address, out var physical)) return 0;
        return physical + 4 > PSXCoreWrapper.RamSize ? 0 : _devices.Core.ReadMemory32(physical);
    }

    private FallbackEntryAttribution Attribute(uint pc, uint? previousReturnPc)
    {
        var versions = _loadedCode.VersionsAt(pc);
        var matched = -1;
        if (versions.Count != 0 && FallbackReturnCheck.TryRamOffset(pc, out var physical))
        {
            for (var v = 0; v < versions.Count && matched < 0; v++)
            {
                var words = versions[v].Words;
                if (words.Count == 0 || (ulong)words.Count * 4ul > PSXCoreWrapper.RamSize - physical) continue;
                var equal = true;
                for (var i = 0; i < words.Count && equal; i++)
                {
                    equal = _devices.Core.ReadMemory32(physical + (uint)i * 4u) == words[i];
                }

                if (equal) matched = v;
            }
        }

        return new FallbackEntryAttribution(versions.Count, matched, previousReturnPc);
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

    /// <summary>
    /// Why the last <see cref="Handle"/> call declined the transfer (<see cref="Decision.Ineligible"/>), one of
    /// <see cref="MixedFallbackDiagnostics"/>'s "ineligible" codes; null when it did not decline (Issue #763).
    /// </summary>
    public string? RejectReason { get; private set; }

    public MixedFallbackEvidence Evidence => new(
        _transitions,
        _returns,
        _fallbackInstructions,
        _pagesToInterpreter,
        _pagesToArtifact,
        _targets.OrderBy(static t => t.Key)
            .Select(static t => new MixedFallbackTarget(t.Key, t.Value.Entries, t.Value.Instructions, t.Value.LastReturnPc))
            .ToArray(),
        _lastEntryPc,
        _lastReturnPc);

    public MixedFallbackTimings Timings => new(_transferClock.Elapsed.TotalMilliseconds, _fallbackClock.Elapsed.TotalMilliseconds);

    /// <summary>The fallback segment in flight as published by its stepping thread (Issue #806); null when none runs.</summary>
    public FallbackSegmentProgress? InFlightSegment => _interpreter.FallbackProgress;

    /// <summary>The return classification of this run so far (Issue #806); safe to read while the pump owns the graph.</summary>
    public FallbackReturnStats ReturnStats => _returnStats.Snapshot();

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
        RejectReason = null;

        // Static eligibility: an aligned PC inside the PS-X EXE text image. The image is the only executable region a
        // fallback ever runs; RAM-generated code, out-of-image PCs and every vector stay with the existing fail-closed path.
        // A guest firmware (Issue #732) is the exception: it runs code it wrote to RAM, its own vectors and a loaded
        // executable by design, reached directly as well as indirectly, so any aligned PC without a block is eligible.
        var imageEnd = (ulong)_imageLoadAddress + (ulong)_imageWords.Count * 4UL;
        var inImage = pc >= _imageLoadAddress && pc < imageEnd;
        // RAM-placed code is never in _blockEntryPcs: the artifact offers it only when no version matched (Issue #732).
        if ((pc & 3u) != 0)
        {
            RejectReason = MixedFallbackDiagnostics.IneligibleUnaligned;
            NoteUnobserved(pc);
            return Decision.Ineligible;
        }

        if (!inImage && !_guestFirmware)
        {
            RejectReason = MixedFallbackDiagnostics.IneligibleOutOfImage;
            NoteUnobserved(pc);
            return Decision.Ineligible;
        }

        if (_blockEntryPcs.Contains(pc))
        {
            RejectReason = MixedFallbackDiagnostics.IneligibleHasBlock;
            NoteUnobserved(pc);
            return Decision.Ineligible;
        }

        // Set once the RAM comparison has been reached (or the handoff was counted as unobserved), so an exception that
        // escapes earlier counts the handoff as a gap and never twice.
        var comparisonReached = false;
        _transferClock.Start();
        try
        {
            _send($"{RecompiledArtifactCodeGen.ProtocolFallbackQueryCommand} {RecompiledArtifactCodeGen.ProtocolFallbackVersion}");
            var header = ReadHeader();
            if (header is null || (!header.Value.Indirect && !_guestFirmware))
            {
                // A refusal, or a transfer that did not come from JR/JALR: not a runtime-discovered target.
                RejectReason = header is null
                    ? MixedFallbackDiagnostics.IneligibleQueryRefused
                    : MixedFallbackDiagnostics.IneligibleNotIndirect;
                NoteUnobserved(pc);
                return Decision.Ineligible;
            }

            if (_transitions >= _options.MaxTransitions)
            {
                // Issue #786 review M-B: stopped before the comparison, so the handoff is counted as unobserved.
                NoteUnobserved(pc);
                return Stop(
                    MixedFallbackDiagnostics.TransitionBudgetExhausted,
                    $"The run already made {_transitions} artifact-to-interpreter handoffs (budget {_options.MaxTransitions}); " +
                    $"it stopped at pc 0x{pc:X8}.");
            }

            var incoming = ReadPages(header.Value.DirtyPages);
            ApplyToCore(incoming);
            // Issue #773: the core holds artifact RAM now, so the refused versions are read from the same memory the guard saw.
            if (_loadedCodeMismatch is not null && _loadedCode.Contains(pc))
            {
                _loadedCodeMismatch.Observe(pc, _loadedCode.VersionsAt(pc), LoadedCodeWordAt, _loadedCode.OriginsAt(pc));
            }

            comparisonReached = true;

            _transitions++;
            _pagesToInterpreter += (ulong)incoming.Count;

            var entry = new FallbackCpuState(
                gpr, header.Value.Hi, header.Value.Lo, pc, header.Value.Sr, header.Value.Cause, header.Value.Epc);

            // Attribution (opt-in): classified on entry, while core RAM equals the artifact's RAM.
            var attribution = _transitionObserver is null ? null : Attribute(pc, _lastReturnPc);

            _transferClock.Stop();
            // Record the accepted fallback entry before executing it: an interrupted
            // segment may never produce an outcome. A return is only observed after
            // its state was successfully committed back to the artifact.
            _lastEntryPc = pc;
            _lastReturnPc = null;
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
            _returnStats.NoteSegment(pc, outcome.State.Pc, outcome.RetiredInstructions, outcome.Status);
            var previous = _targets.GetValueOrDefault(pc);
            _targets[pc] = (
                previous.Entries + 1,
                previous.Instructions + outcome.RetiredInstructions,
                outcome.Status == FallbackSegmentStatus.Returned ? outcome.State.Pc : previous.LastReturnPc);
            _transitionObserver?.Invoke(new MixedFallbackTransition(
                pc, header.Value.Indirect, outcome.Status, outcome.State.Pc, outcome.RetiredInstructions, outcome.DiagnosticCode,
                Attribution: attribution));
            // Issue #774: the per-transfer record of the unit trace reads this. Only observed; nothing above changes.
            LastSegment = new SegmentRecord(
                outcome.Status == FallbackSegmentStatus.Returned ? "returned" : "stopped",
                outcome.RetiredInstructions, outcome.State.Pc, outcome.DiagnosticCode, entry, outcome.State);

            if (outcome.Status != FallbackSegmentStatus.Returned)
            {
                return Stop(outcome.DiagnosticCode!, outcome.DiagnosticMessage!);
            }

            if (!WriteBack(outcome.State))
            {
                return Decision.Stopped;
            }

            _lastReturnPc = outcome.State.Pc;
            return Decision.Resumed;
        }
        catch
        {
            // Issue #786 review (round 3, MEDIUM-2): a failure before the RAM comparison leaves this handoff unobserved.
            if (!comparisonReached) NoteUnobserved(pc);
            throw;
        }
        finally
        {
            _transferClock.Stop();
        }
    }

    /// <summary>One fallback segment that ran for the current transfer (Issue #774).</summary>
    /// <remarks>
    /// <see cref="Entry"/> and <see cref="Exit"/> are the CPU state the segment started and ended with (Issue #774, B-6b).
    /// They are carried only to the trace; the segment itself does not read them back.
    /// </remarks>
    public sealed record SegmentRecord(
        string Status, ulong Retired, uint EndPc, string? StopCode, FallbackCpuState? Entry = null, FallbackCpuState? Exit = null);

    /// <summary>The segment the most recent transfer ran, or null when the transfer ran no fallback segment.</summary>
    public SegmentRecord? LastSegment { get; private set; }

    /// <summary>Called before each transfer is served, so <see cref="LastSegment"/> describes only that transfer.</summary>
    public void BeginSegmentRecord() => LastSegment = null;

    /// <summary>Counts a handoff at a versioned PC that returns before the host compares its RAM (Issue #786 review M-B).</summary>
    private void NoteUnobserved(uint pc)
    {
        if (_loadedCodeMismatch is not null && _loadedCode.Contains(pc)) _loadedCodeMismatch.NoteUnobservedHandoff();
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
            throw new InvalidOperationException($"Expected a '{RecompiledArtifactCodeGen.ProtocolFallbackReplyPrefix}' reply, received a reply of {reply.Length} characters.");
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
            throw new InvalidOperationException($"Malformed fallback reply ({reply.Length} characters).");
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
                throw new InvalidOperationException($"Expected a '{RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix}' line, received a line of {line.Length} characters.");
            }

            var fields = line[RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix.Length..].Split(' ');
            if (fields.Length != 2
                || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index <= previous
                || index >= PageCount
                || fields[1].Length != PageSize * 2
                || pages.Count >= expected)
            {
                throw new InvalidOperationException($"Malformed fallback page line ({line.Length} characters).");
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

            throw new InvalidOperationException($"Expected '{RecompiledArtifactCodeGen.ProtocolWriteAck}' to a fallback commit, received a reply of {reply.Length} characters.");
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

}
