using System.Diagnostics;
using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Production <see cref="IRecompiledExecutionEngine"/> that builds a runnable
/// native artifact through <see cref="IGeneratedHostBuildService"/> (Issue #458)
/// and launches it as a real process (Issue #459) — the non-test counterpart of
/// <c>PSXRecomp.Tests.Execution.HostTitleExecutionEngine</c>. When constructed
/// with a <c>biosRuntimeFactory</c>, an unresolved control transfer is relayed
/// to a real <see cref="IBiosRuntime"/> over the artifact's host-transfer
/// protocol (<see cref="RecompiledArtifactCodeGen"/>) instead of stopping the
/// run — the shared Runtime/BIOS HLE contract, reused rather than reimplemented
/// (ADR-014).
/// </summary>
/// <remarks>
/// This milestone's engine runs the whole guest program in one artifact launch:
/// it does not carry guest RAM across repeated <see cref="RunSegment"/> calls
/// (unlike the differential harness's per-segment full-RAM-dump technique),
/// so it supports exactly one <see cref="RunSegment"/> call per instance. A
/// caller drives it through <see cref="ExecutionOrchestrator"/> with
/// <c>TitleExecutionRequest.OuterBudget</c> of 1; multi-segment continuity
/// across repeated launches is deliberately out of scope for Issue #459 (see
/// its non-goals) and is follow-up work for the CLI/E2E issues that consume
/// this engine.
/// </remarks>
[Infrastructure]
public sealed class RecompiledHostExecutionEngine : IRecompiledExecutionEngine
{
    public const string EngineName = "recompiled-host-artifact";

    private const int RunTimeoutMs = 30000;
    private const int PumpDrainTimeoutMs = 5000;

    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
    private readonly IReadOnlySet<uint> _blockEntryPcs;
    private readonly string _binaryPath;
    private readonly string _inputPath;

    private bool _loaded;
    private bool _ran;

    /// <summary>
    /// Generates, builds, and stages the artifact for <paramref name="program"/>.
    /// Building happens here (not in <see cref="Load"/>) so a build failure — the
    /// only way this engine cannot be prepared — surfaces as soon as the engine
    /// exists, matching <see cref="IRecompiledExecutionEngine.Load"/>'s documented
    /// failure mode without deferring it past construction.
    /// </summary>
    /// <param name="program">The lowered Recompiler IR to build a runnable artifact from.</param>
    /// <param name="buildService">The production build substrate (Issue #458).</param>
    /// <param name="outputDirectory">
    /// Where the artifact's source, object, and binary are written. The caller owns
    /// this directory's lifecycle.
    /// </param>
    /// <param name="biosRuntimeFactory">
    /// Builds the Runtime the artifact's unresolved control transfers are relayed
    /// to, over the reader/writer this engine exposes for the artifact's own guest
    /// memory. Null runs the artifact independently, with no Runtime attached: any
    /// unresolved transfer then stops the run immediately, a legitimate classified
    /// boundary rather than a failure.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or
    /// <paramref name="buildService"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Code generation or the build failed.</exception>
    public RecompiledHostExecutionEngine(
        RecompilerIrProgram program,
        IGeneratedHostBuildService buildService,
        string outputDirectory,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        _biosRuntimeFactory = biosRuntimeFactory;
        _blockEntryPcs = program.Blocks.Select(static block => block.EntryPc).ToHashSet();
        _inputPath = Path.Combine(outputDirectory, "artifact-input.txt");

        var dispatch = RecompilerHostCodeGen.Generate(program);
        if (!dispatch.Success)
        {
            throw new InvalidOperationException(dispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        var artifact = RecompiledArtifactCodeGen.Generate(dispatch);
        if (!artifact.Success)
        {
            throw new InvalidOperationException(artifact.DiagnosticMessage ?? "Artifact entrypoint code generation failed.");
        }

        var build = buildService.Build(new GeneratedHostBuildRequest(artifact.Source!, outputDirectory, "recompiled-artifact"));
        if (build.Status != GeneratedHostBuildStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Artifact build failed ({build.Status})." +
                (string.IsNullOrEmpty(build.DiagnosticMessage) ? "" : "\n" + build.DiagnosticMessage));
        }

        _binaryPath = build.Artifact!.BinaryPath;
    }

    public string Name => EngineName;

    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WriteInputFile(request.EntryPc, request.InitialGpr, request.InitialHi, request.InitialLo, request.InitialMemory, request.SegmentBudget);
        _loaded = true;
    }

    public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
    {
        ArgumentNullException.ThrowIfNull(segmentRequest);
        if (!_loaded)
        {
            throw new InvalidOperationException("RunSegment called before Load.");
        }

        if (_ran)
        {
            // Honest, loud failure rather than a silent wrong answer: this engine
            // does not carry guest RAM across repeated launches (see the class
            // remarks), so a second call would restart from the first call's
            // initial memory, discarding whatever the guest wrote.
            throw new InvalidOperationException(
                "RecompiledHostExecutionEngine supports exactly one RunSegment call per instance " +
                "(Issue #459's minimal slice carries no guest RAM continuity across artifact launches).");
        }

        _ran = true;

        // ExecutionOrchestrator seeds this call's state directly from the same
        // TitleExecutionRequest that Load already wrote to the input file (entry
        // pc, initial registers, segment budget), so the file Load produced is
        // already this run's input — including InitialMemory, which a
        // TitleExecutionSegmentRequest carries no field for at all.
        var bridge = _biosRuntimeFactory is null ? null : new HostTransferBridge(_biosRuntimeFactory, _blockEntryPcs);
        var arguments = bridge is null ? $"\"{_inputPath}\"" : $"\"{_inputPath}\" {RecompiledArtifactCodeGen.HostTransferFlag}";

        var (exit, stdout, _) = RunProcess(_binaryPath, arguments, RunTimeoutMs, out var timedOut, bridge);

        if (timedOut)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.TimedOut, "ARTIFACT_TIMEOUT", "The runnable artifact exceeded its bounded execution timeout.");
        }

        if (exit < 0 || exit > byte.MaxValue || !stdout.Contains(RecompiledArtifactCodeGen.SnapshotBeginMarker, StringComparison.Ordinal))
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, "ARTIFACT_FAILED", $"The runnable artifact failed (exit {exit}).");
        }

        var snapshot = ArtifactSnapshotParser.Parse(stdout);
        if (snapshot is null)
        {
            var message = stdout.Length <= 2000 ? stdout : stdout[..2000] + "...";
            return RecompilerExecutionResult.Failed(RecompilerExecutionStatus.MalformedResult, "MALFORMED_ARTIFACT_SNAPSHOT", message);
        }

        return new RecompilerExecutionResult(RecompilerExecutionStatus.Completed, snapshot, bridge?.DiagnosticCode, bridge?.DiagnosticMessage);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private void WriteInputFile(
        uint pc, IReadOnlyList<uint> gpr, uint hi, uint lo, IReadOnlyList<RecompilerInitialMemoryItem> initialMemory, uint budget)
    {
        if (initialMemory.Count > RecompiledArtifactCodeGen.MaxInitEntries)
        {
            // The driver's own PSX_MAX_INIT bound (kept numerically in sync with
            // MaxInitEntries — a verbatim C string cannot interpolate a C#
            // constant) would otherwise reject this input file with an opaque
            // process exit code; fail with a clear message instead.
            throw new InvalidOperationException(
                $"Initial memory has {initialMemory.Count} entries, exceeding the artifact driver's " +
                $"{RecompiledArtifactCodeGen.MaxInitEntries}-entry limit.");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < gpr.Count; i++) sb.Append(gpr[i]).Append(' ');
        sb.Append('\n').Append(hi).Append('\n').Append(lo).Append('\n').Append(pc).Append('\n').Append(budget).Append('\n');
        sb.Append(initialMemory.Count).Append('\n');
        foreach (var item in initialMemory)
        {
            sb.Append(item.Address).Append(' ').Append(item.Value).Append('\n');
        }

        File.WriteAllText(_inputPath, sb.ToString());
    }

    /// <summary>Runs one process, optionally pumping <paramref name="bridge"/>'s host-transfer protocol.</summary>
    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string fileName, string arguments, int timeoutMs, out bool timedOut, HostTransferBridge? bridge)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = bridge is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (bridge is null)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                TryKillTree(process);
                timedOut = true;
                return (int.MinValue, stdoutTask.Result, stderrTask.Result);
            }

            process.WaitForExit();
            timedOut = false;
            return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }

        process.StandardInput.AutoFlush = true;
        bridge.Attach(process.StandardOutput, process.StandardInput);

        var output = new StringBuilder();
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!bridge.TryHandle(line))
                {
                    output.AppendLine(line);
                }
            }
        });

        bool pumpCompleted;
        try
        {
            pumpCompleted = pump.Wait(timeoutMs);
        }
        catch
        {
            TryKillTree(process);
            throw;
        }

        if (!pumpCompleted)
        {
            TryKillTree(process);
            try { pump.Wait(PumpDrainTimeoutMs); } catch { /* the timeout verdict below stands regardless */ }
            timedOut = true;
            return (int.MinValue, output.ToString(), stderrTask.Result);
        }

        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);
            timedOut = true;
            return (int.MinValue, output.ToString(), stderrTask.Result);
        }

        process.WaitForExit();
        timedOut = false;
        return (process.ExitCode, output.ToString(), stderrTask.Result);
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(true); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Relays one artifact run's host-transfer offers to a real
    /// <see cref="IBiosRuntime"/> — the production analogue of
    /// <c>PSXRecomp.Tests.Recompiler.RecompilerHostExecutor.HostTransferSession</c>,
    /// against <see cref="RecompiledArtifactCodeGen"/>'s protocol instead of the
    /// differential driver's.
    /// </summary>
    private sealed class HostTransferBridge
    {
        private const int TransferFieldCount = 33; // pc + 32 GPRs.

        private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> _biosRuntimeFactory;
        private readonly IReadOnlySet<uint> _blockEntryPcs;

        private TextReader? _fromArtifact;
        private TextWriter? _toArtifact;
        private IBiosRuntime? _biosRuntime;

        public string? DiagnosticCode { get; private set; }
        public string? DiagnosticMessage { get; private set; }

        public HostTransferBridge(
            Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory, IReadOnlySet<uint> blockEntryPcs)
        {
            _biosRuntimeFactory = biosRuntimeFactory;
            _blockEntryPcs = blockEntryPcs;
        }

        public void Attach(TextReader fromArtifact, TextWriter toArtifact)
        {
            _fromArtifact = fromArtifact;
            _toArtifact = toArtifact;
        }

        public bool TryHandle(string line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed == RecompiledArtifactCodeGen.ProtocolInitLine)
            {
                _biosRuntime = _biosRuntimeFactory(new GuestMemoryReader(ReadPhysicalByte), new GuestMemoryWriter(WritePhysicalByte));
                // The Runtime's construction above already issued whatever R/W
                // seeding it needed; this initial handshake itself claims no pc.
                Decline();
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolTransferPrefix, StringComparison.Ordinal))
            {
                HandleTransfer(trimmed[RecompiledArtifactCodeGen.ProtocolTransferPrefix.Length..]);
                return true;
            }

            return false;
        }

        private void HandleTransfer(string fields)
        {
            var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (_biosRuntime is null || parts.Length != TransferFieldCount)
            {
                Decline();
                return;
            }

            var pc = ParseUInt(parts[0]);
            var gpr = new uint[TitleExecutionRequest.GprCount];
            for (var i = 0; i < gpr.Length; i++) gpr[i] = ParseUInt(parts[i + 1]);

            if (!BiosJumpTables.TryResolveVectorFamily(pc, out var family))
            {
                Decline();
                return;
            }

            var outcome = BiosVectorDispatch.Dispatch(_biosRuntime, family, gpr);
            if (!outcome.ContinueExecution)
            {
                DiagnosticCode = outcome.DiagnosticCode;
                DiagnosticMessage = outcome.DiagnosticMessage;
                Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                return;
            }

            // The artifact can only enter a pc it compiled a block for. A patched
            // target outside that static block table is where this run-once launch
            // runs out of code; record why without treating it as this bridge's own
            // stop reason (Issue #379's parity: the interpreter reaches the same
            // unresolved pc for the same guest condition).
            if (outcome.IsPatchedTarget && !_blockEntryPcs.Contains(outcome.NextPc))
            {
                DiagnosticCode = "BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK";
                DiagnosticMessage =
                    $"Patched jump-table entry names guest address 0x{outcome.NextPc:X8}, which this artifact " +
                    "has no generated block for; the recompiled path stops there instead of entering it.";
            }

            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {outcome.NextPc} " +
                $"{(outcome.ReturnValue is null ? 0 : 1)} {outcome.ReturnValue ?? 0}"));
        }

        private void Decline() => Send(RecompiledArtifactCodeGen.ProtocolDeclineReply);

        private byte ReadPhysicalByte(uint physicalAddress)
        {
            Send($"{RecompiledArtifactCodeGen.ProtocolReadCommand} {physicalAddress.ToString(CultureInfo.InvariantCulture)}");
            var reply = ReadReply();
            if (!reply.StartsWith(RecompiledArtifactCodeGen.ProtocolDataPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a '{RecompiledArtifactCodeGen.ProtocolDataPrefix}' reply, received '{reply}'.");
            }

            return (byte)ParseUInt(reply[RecompiledArtifactCodeGen.ProtocolDataPrefix.Length..].Trim());
        }

        private void WritePhysicalByte(uint physicalAddress, byte value)
        {
            Send($"{RecompiledArtifactCodeGen.ProtocolWriteCommand} {physicalAddress.ToString(CultureInfo.InvariantCulture)} {value.ToString(CultureInfo.InvariantCulture)}");
            ReadReply();
        }

        private string ReadReply() =>
            _fromArtifact?.ReadLine() ?? throw new InvalidOperationException("The artifact closed its output mid-protocol.");

        private void Send(string line) =>
            (_toArtifact ?? throw new InvalidOperationException("The host-transfer bridge is not attached to a process.")).WriteLine(line);

        private static uint ParseUInt(string value) => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
