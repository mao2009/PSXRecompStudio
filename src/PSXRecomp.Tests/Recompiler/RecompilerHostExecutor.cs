using System.Diagnostics;
using System.Globalization;
using System.Text;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Host-side build/run executor for the differential harness (Issue #211).
// It lowers a fixture to Recompiler IR, generates deterministic C with
// RecompilerHostCodeGen, compiles it with the fixed recipe, and runs it
// bounded, then parses the emitted state snapshot back.
//
// This class intentionally lives in the Test assembly rather than the Domain
// layer (PSXRecomp.Core/Recompiler): compiler invocation and file I/O are
// forbidden in the Domain layer by the architecture analyzer, and the existing
// host-compilation tests already follow this pattern (pragma-disable AARC003).
public sealed class RecompilerHostExecutor : IRecompilerExecutor
{
    public const string ExecutorName = "recompiled-host-gcc";

    /// <summary>
    /// Reported when a patched jump-table entry names a translatable guest
    /// address that this execution path has no generated block for. Unlike the
    /// interpreter — which fetches arbitrary MIPS from guest RAM — the generated
    /// host can only enter a block it already compiled, so control stops at that
    /// address instead of continuing there. Compiling a target found only at
    /// runtime is dynamic overlay recompilation (Issue #249), out of scope here.
    /// </summary>
    /// <remarks>
    /// This is an <em>advisory</em> diagnostic, not a stop reason: the segment
    /// still ends with <see cref="RecompilerIrTerminationReason.Success"/> at the
    /// patched target, which is the same segment-level outcome the interpreter
    /// produces for the same guest condition, so both backends reach the
    /// full-title handoff identically (Issue #379). It is carried so the reason
    /// the recompiled path could go no further is never lost (Issue #279).
    /// </remarks>
    public const string BiosPatchedTargetHasNoGeneratedBlockDiagnosticCode =
        "BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK";

    private const string Compiler = "gcc";
    private const string CompilerArgs = "-std=c11 -O0 -Wall -Wextra";
    private const string CheckpointCompileFlag = "-DRECOMPILER_CHECKPOINTS";
    private const int BuildTimeoutMs = 30000;
    private const int RunTimeoutMs = 5000;

    /// <summary>
    /// Command-line switch that turns the driver's host-transfer protocol on.
    /// The driver matches this literal by name in <see cref="DriverSource"/>'s
    /// <c>main</c>; the two must stay in step (the driver is a verbatim C string,
    /// so it cannot interpolate this constant).
    /// </summary>
    internal const string HostTransferArgument = "--host-transfer";

    /// <summary>
    /// Command-line switch that makes the driver dump its full guest RAM as
    /// <c>RAMHEX</c> lines and the hardware-register window as <c>HWREG</c> lines
    /// after the state snapshot (full-title segments, #366; device-state round-trip
    /// across segments, #387). The literal lives in C and cannot interpolate this
    /// constant either, so the two must stay in step.
    /// </summary>
    internal const string FullRamArgument = "--full-ram";

    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;

    /// <summary>
    /// Creates an executor with no BIOS boundary: the generated dispatch offers no
    /// unresolved PC to a host, exactly as before this executor knew about BIOS
    /// calls at all.
    /// </summary>
    public RecompilerHostExecutor()
    {
    }

    /// <summary>
    /// Creates an executor that dispatches the generated program's unresolved
    /// control transfers through an <see cref="IBiosRuntime"/> (Issue #362) — the
    /// recompiled-path counterpart of
    /// <see cref="RecompilerInterpreterExecutor(Func{IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime})"/>,
    /// and deliberately the same factory shape.
    /// </summary>
    /// <param name="biosRuntimeFactory">
    /// Builds the Runtime over the generated program's own guest memory. The
    /// reader and writer handed to it read and write the running host process's
    /// RAM, which is why a constructed instance cannot be injected directly: the
    /// Runtime must observe the very jump-table bytes the generated code patches,
    /// and that memory only exists for the duration of one run. It is invoked
    /// after the fixture's initial memory is in place and before the first block
    /// retires, matching the interpreter's ordering exactly.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="biosRuntimeFactory"/> is null.</exception>
    public RecompilerHostExecutor(Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory)
    {
        ArgumentNullException.ThrowIfNull(biosRuntimeFactory);
        _biosRuntimeFactory = biosRuntimeFactory;
    }

    public string Name => ExecutorName;

    /// <summary>The generated C source from the most recent build/run (B6 artifact).</summary>
    public string? LastGeneratedSource { get; private set; }

    /// <summary>The lowered Recompiler IR of the most recent run (B6 artifact snapshot).</summary>
    public RecompilerIrProgram? LastGeneratedProgram { get; private set; }

    /// <summary>
    /// The preserved build directory of the most recent failed build/run (B6
    /// artifact), or null when the last run succeeded. Contains program.c,
    /// program.stdout.txt and program.stderr.txt. Retained only on failure so
    /// successful runs keep their temp dirs. The caller owns its lifetime.
    /// </summary>
    public string? LastArtifactsPath { get; private set; }

    public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        RecompilerIrProgram? program;
        try
        {
            program = LowerFixture(fixture);
            LastGeneratedProgram = program;
        }
        catch (Exception ex)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.GenerationFailed,
                "LOWER_FAILED",
                ex.Message);
        }

        var validation = RecompilerIrValidator.Validate(program);
        if (!validation.IsValid)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.GenerationFailed,
                "IR_VALIDATION_FAILED",
                $"IR validation failed with {validation.Diagnostics.Count} diagnostic(s).");
        }

        var generated = RecompilerHostCodeGen.Generate(program);
        if (!generated.Success)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.GenerationFailed,
                generated.DiagnosticCode ?? "HOST_CODEGEN_FAILED",
                generated.DiagnosticMessage ?? "Host code generation failed.");
        }

        var run = BuildAndRun(fixture, generated.Source!, program.Blocks.Select(block => block.EntryPc).ToHashSet());
        return run;
    }

    /// <summary>The on-disk product of a recompiled fixture (Issue #209 vertical slice).</summary>
    public readonly record struct CompiledBinary(string BinaryPath, string InputPath, string DirectoryPath);

    /// <summary>
    /// Compiles a fixture into a reusable executable and returns the artifact
    /// locations. The caller owns the returned directory's lifetime. Enables the
    /// #209 vertical-slice "compare the produced test binary" check.
    /// </summary>
    /// <remarks>
    /// Delegates the actual compile/link to the production
    /// <see cref="GeneratedHostBuildService"/> (Issue #458): this helper only
    /// owns fixture-specific concerns (lowering, the differential driver, the
    /// input file, and this temp directory's lifetime).
    /// </remarks>
    public CompiledBinary CompileRecompiledBinary(RecompilerDifferentialFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var tempDir = CreateTempDir();

        try
        {
            var program = LowerFixture(fixture);
            var validation = RecompilerIrValidator.Validate(program);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    $"IR validation failed with {validation.Diagnostics.Count} diagnostic(s).");
            }

            var generated = RecompilerHostCodeGen.Generate(program);
            if (!generated.Success)
            {
                throw new InvalidOperationException(
                    generated.DiagnosticMessage ?? "Host code generation failed.");
            }

            var inputPath = Path.Combine(tempDir, "input.txt");
            WriteInputFile(inputPath, fixture);

            var build = new GeneratedHostBuildService().Build(new GeneratedHostBuildRequest(
                generated.Source + "\n" + DriverSource, tempDir, "program"));
            if (build.Status != GeneratedHostBuildStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Host compilation failed." +
                    (string.IsNullOrEmpty(build.DiagnosticMessage) ? "" : "\n" + build.DiagnosticMessage));
            }

            return new CompiledBinary(build.Artifact!.BinaryPath, inputPath, tempDir);
        }
        catch
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            throw;
        }
    }

    /// <summary>Runs a previously compiled recompiled binary against its fixture input and returns stdout.</summary>
    public string RunRecompiledBinary(CompiledBinary binary)
    {
        var (exit, stdout, _) = RunProcess(binary.BinaryPath, binary.InputPath, RunTimeoutMs, out var timedOut);
        if (timedOut)
        {
            throw new InvalidOperationException("Generated executable exceeded the bounded execution budget.");
        }
        if (exit != 0 && !stdout.Contains("RSNAPSHOT_BEGIN"))
        {
            throw new InvalidOperationException($"Generated executable failed (exit {exit}).");
        }
        return stdout;
    }

    private static RecompilerIrProgram LowerFixture(RecompilerDifferentialFixture fixture)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            var instruction = R3000aDecoder.Decode(fixture.Instructions[i]);
            instructions.Add((instruction, fixture.PcOfInstruction(i)));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    private RecompilerExecutionResult BuildAndRun(
        RecompilerDifferentialFixture fixture,
        string generatedSource,
        IReadOnlySet<uint> blockEntryPcs)
    {
        LastGeneratedSource = generatedSource;
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "program.c");
            var inputPath = Path.Combine(tempDir, "input.txt");
            var outputPath = Path.Combine(tempDir, "program");
            var stdoutPath = Path.Combine(tempDir, "program.stdout.txt");
            var stderrPath = Path.Combine(tempDir, "program.stderr.txt");

            File.WriteAllText(sourcePath, generatedSource + "\n" + DriverSource);
            WriteInputFile(inputPath, fixture);

            var (compileExit, _, compileErr) = RunProcess(
                Compiler, $"{CompilerArgs} {CheckpointCompileFlag} \"{sourcePath}\" -o \"{outputPath}\"", BuildTimeoutMs, out var buildTimedOut);

            if (buildTimedOut)
            {
                File.WriteAllText(stderrPath, compileErr);
                return FailAndPreserve(tempDir, "BUILD_TIMEOUT", "Host compilation exceeded the build timeout.");
            }

            if (compileExit != 0)
            {
                File.WriteAllText(stderrPath, compileErr);
                return FailAndPreserve(tempDir, "BUILD_FAILED",
                    $"Host compilation failed (exit {compileExit}):\n{Truncate(compileErr, 2000)}");
            }

            // With a Runtime configured, the generated program's unresolved
            // control transfers are relayed to this process so the very same
            // IBiosRuntime the interpreter uses decides what they mean.
            var bios = _biosRuntimeFactory is null
                ? null
                : new HostTransferSession(_biosRuntimeFactory, blockEntryPcs);

            // The input path is quoted so a temp directory containing a space
            // cannot split it into two arguments — which would both break the
            // file open and, with the protocol switch appended, misplace it.
            var runArguments = bios is null
                ? $"\"{inputPath}\""
                : $"\"{inputPath}\" {HostTransferArgument}";

            var (runExit, runOut, runErr) = RunProcess(
                ResolveBinaryPath(tempDir),
                runArguments,
                RunTimeoutMs,
                out var runTimedOut,
                bios);

            if (runTimedOut)
            {
                File.WriteAllText(stdoutPath, runOut);
                File.WriteAllText(stderrPath, runErr);
                return FailAndPreserve(tempDir, "EXECUTION_TIMEOUT",
                    "Generated executable exceeded the bounded execution budget.");
            }

            if (runExit != 0 && !runOut.Contains("RSNAPSHOT_BEGIN"))
            {
                File.WriteAllText(stdoutPath, runOut);
                File.WriteAllText(stderrPath, runErr);
                return FailAndPreserve(tempDir, "EXECUTION_FAILED",
                    $"Generated executable failed (exit {runExit}).");
            }

            var snapshot = SnapshotParser.Parse(runOut);
            if (snapshot is null)
            {
                File.WriteAllText(stdoutPath, runOut);
                return FailAndPreserve(tempDir, "MALFORMED_SNAPSHOT", Truncate(runOut, 2000));
            }

            // The snapshot is always produced — the executor mechanism itself did
            // not fail — but an unresolved BIOS dispatch carries its diagnostic
            // alongside it, exactly as the interpreter executor reports one.
            return new RecompilerExecutionResult(
                RecompilerExecutionStatus.Completed,
                snapshot,
                bios?.DiagnosticCode,
                bios?.DiagnosticMessage);
        }
        finally
        {
            // Success paths own their temp dir; failure paths keep it for the
            // failure artifacts (B6) and expose it via LastArtifactsPath.
            if (Directory.Exists(tempDir) && tempDir != LastArtifactsPath)
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private RecompilerExecutionResult FailAndPreserve(string tempDir, string code, string message)
    {
        LastArtifactsPath = tempDir;
        return RecompilerExecutionResult.Failed(RecompilerExecutionStatusFromCode(code), code, message);
    }

    private static RecompilerExecutionStatus RecompilerExecutionStatusFromCode(string code) => code switch
    {
        "BUILD_TIMEOUT" or "BUILD_FAILED" => RecompilerExecutionStatus.BuildFailed,
        "EXECUTION_TIMEOUT" => RecompilerExecutionStatus.TimedOut,
        "EXECUTION_FAILED" => RecompilerExecutionStatus.ExecutionFailed,
        _ => RecompilerExecutionStatus.MalformedResult,
    };

    private static void WriteInputFile(string path, RecompilerDifferentialFixture fixture)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fixture.InitialGpr.Count; i++)
        {
            sb.Append(fixture.InitialGpr[i]).Append(' ');
        }
        sb.Append('\n');
        sb.Append(fixture.InitialHi).Append('\n');
        sb.Append(fixture.InitialLo).Append('\n');
        sb.Append(fixture.EntryPc).Append('\n');
        sb.Append(fixture.StepBudget).Append('\n');

        sb.Append(fixture.InitialMemory.Count).Append('\n');
        foreach (var item in fixture.InitialMemory)
        {
            sb.Append(item.Address).Append(' ').Append(item.Value).Append('\n');
        }

        sb.Append(fixture.MemoryWindow.Count).Append('\n');
        foreach (var address in fixture.MemoryWindow)
        {
            sb.Append(address).Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    internal static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string fileName, string arguments, int timeoutMs, out bool timedOut,
        HostTransferSession? session = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = session is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Without a session the child never reads stdin, so the whole of stdout
        // can be drained at once exactly as before. With one, stdout carries an
        // interleaved request/response protocol that has to be pumped line by
        // line while the child is still running.
        if (session is null)
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

        // The pump runs off the calling thread so the same bounded budget that
        // covers an ordinary run also covers the protocol. A blocking ReadLine
        // cannot be cancelled, so the timeout path kills the child instead:
        // that closes the pipe, the pending read returns, and the pump ends.
        // Without this a child that stops mid-line — or stops answering — would
        // hang the test run indefinitely rather than reporting a timeout.
        process.StandardInput.AutoFlush = true;
        session.Attach(process.StandardOutput, process.StandardInput);

        var output = new StringBuilder();
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!session.TryHandle(line))
                {
                    output.AppendLine(line);
                }
            }
        });

        if (!WaitForPump(pump, process, timeoutMs))
        {
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

    /// <summary>
    /// Waits for the protocol pump, bounded by <paramref name="timeoutMs"/>.
    /// Returns false when the budget expired; the child is killed either way so
    /// no orphan survives, and the pump is drained before its output buffer is
    /// read back.
    /// </summary>
    /// <exception cref="Exception">Whatever the pump threw, once the child is killed.</exception>
    private static bool WaitForPump(Task pump, Process process, int timeoutMs)
    {
        bool completed;
        try
        {
            completed = pump.Wait(timeoutMs);
        }
        catch
        {
            // A protocol failure inside the pump: stop the child before surfacing it.
            TryKillTree(process);
            throw;
        }

        if (completed)
        {
            return true;
        }

        // Killing the child closes the pipe the pump is blocked on, so the wait
        // below is what makes the output buffer safe to read from this thread.
        TryKillTree(process);
        try
        {
            pump.Wait(PumpDrainTimeoutMs);
        }
        catch
        {
            // The run already failed on the timeout; a pump fault reported on the
            // way down must not replace that verdict.
        }

        return false;
    }

    /// <summary>How long a killed child's pump is given to observe its closed pipe.</summary>
    private const int PumpDrainTimeoutMs = 5000;

    /// <summary>
    /// One run's binding between the generated host's optional control-transfer
    /// hook and this process's <see cref="IBiosRuntime"/> (Issue #362).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated program runs as a separate process, so the hook's offer is
    /// relayed over a line protocol on the child's stdout/stdin. The protocol
    /// carries only registers, single memory bytes, and a decision: it holds no
    /// BIOS knowledge whatsoever, which is what keeps every BIOS semantic on this
    /// side of the boundary, inside the same <see cref="BiosVectorDispatch"/> and
    /// the same <see cref="IBiosRuntime"/> the interpreter path uses (ADR-014's
    /// rejection of BIOS behavior embedded in generated C).
    /// </para>
    /// <para>
    /// The Runtime is built on the child's <c>RHOST_INIT</c> line — after the
    /// fixture's initial memory has landed and before the first block retires —
    /// so its jump-table seeding is observed by the generated program exactly as
    /// the interpreter's is.
    /// </para>
    /// </remarks>
    internal sealed class HostTransferSession
    {
        private const string InitLine = "RHOST_INIT";
        private const string TransferPrefix = "RHOST_TRANSFER ";
        private const string DataPrefix = "RHOST_DATA ";

        /// <summary>The <c>pc</c> field plus the 32 general-purpose registers.</summary>
        private const int TransferFieldCount = 33;

        private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> _biosRuntimeFactory;
        private readonly IReadOnlySet<uint> _blockEntryPcs;

        private TextReader? _fromHost;
        private TextWriter? _toHost;
        private IBiosRuntime? _biosRuntime;

        /// <summary>
        /// The diagnostic of the dispatch that stopped the run, when one did —
        /// or the advisory
        /// <see cref="BiosPatchedTargetHasNoGeneratedBlockDiagnosticCode"/>, which
        /// explains where the recompiled path ran out of code without itself
        /// being a stop reason.
        /// </summary>
        public string? DiagnosticCode { get; private set; }

        /// <inheritdoc cref="DiagnosticCode" />
        public string? DiagnosticMessage { get; private set; }

        /// <summary>
        /// Constructs the session. The Runtime is built per run (the full-title
        /// host engine builds one per segment so each observes that segment's RAM),
        /// hence the factory rather than an instance.
        /// </summary>
        /// <param name="biosRuntimeFactory">Builds the Runtime over the child's guest memory.</param>
        /// <param name="blockEntryPcs">The generated program's block entry PCs, used to reject
        /// patched targets the generated host has no block for.</param>
        public HostTransferSession(
            Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory,
            IReadOnlySet<uint> blockEntryPcs)
        {
            _biosRuntimeFactory = biosRuntimeFactory;
            _blockEntryPcs = blockEntryPcs;
        }

        public void Attach(TextReader fromHost, TextWriter toHost)
        {
            _fromHost = fromHost;
            _toHost = toHost;
        }

        /// <summary>Handles one stdout line; false when the line is not protocol traffic.</summary>
        public bool TryHandle(string line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed == InitLine)
            {
                _biosRuntime = _biosRuntimeFactory(
                    new GuestMemoryReader(ReadPhysicalByte),
                    new GuestMemoryWriter(WritePhysicalByte));
                Decline();
                return true;
            }

            if (trimmed.StartsWith(TransferPrefix, StringComparison.Ordinal))
            {
                HandleTransfer(trimmed[TransferPrefix.Length..]);
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
            var gpr = new uint[RecompilerDifferentialFixture.GprCount];
            for (var i = 0; i < gpr.Length; i++)
            {
                gpr[i] = ParseUInt(parts[i + 1]);
            }

            // Only a BIOS trampoline vector is claimed. Any other unresolved PC is
            // declined, so the generated dispatch keeps its existing behavior for it.
            if (!BiosJumpTables.TryResolveVectorFamily(pc, out var family))
            {
                Decline();
                return;
            }

            var outcome = BiosVectorDispatch.Dispatch(_biosRuntime, family, gpr);
            if (!outcome.ContinueExecution)
            {
                Stop(outcome.DiagnosticCode, outcome.DiagnosticMessage);
                return;
            }

            // The generated host can only enter a PC it compiled a block for, so
            // a patched target outside the static block table is where this path
            // runs out of code. Control is still transferred there: the generated
            // dispatch finds no block at the next boundary and ends the segment
            // cleanly at that PC — exactly what the interpreter does when a
            // patched target lies outside its program image — so both backends
            // hand the identical unresolved PC to the same outer resolution step
            // (Issue #379). The reason is recorded as an advisory so it is still
            // visible to a caller that has no handoff, rather than lost (#279).
            if (outcome.IsPatchedTarget && !_blockEntryPcs.Contains(outcome.NextPc))
            {
                var functionNumber = (byte)(gpr[(int)R3000aRegister.T1] & 0xFFu);
                DiagnosticCode = BiosPatchedTargetHasNoGeneratedBlockDiagnosticCode;
                DiagnosticMessage =
                    $"{family}:{functionNumber:X2}: patched jump-table entry names guest address " +
                    $"0x{outcome.NextPc:X8}, which this program has no generated block for, so the " +
                    "recompiled path stops there instead of entering it. Resolving such a target is " +
                    "the caller's (a full-title handoff's) decision, and compiling one discovered at " +
                    "runtime is dynamic overlay recompilation (Issue #249), out of scope here.";
            }

            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"D {(byte)RecompilerIrTerminationReason.Success} {outcome.NextPc} " +
                $"{(outcome.ReturnValue is null ? 0 : 1)} {outcome.ReturnValue ?? 0}"));
        }

        private void Stop(string? diagnosticCode, string? diagnosticMessage)
        {
            DiagnosticCode = diagnosticCode;
            DiagnosticMessage = diagnosticMessage;
            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"D {(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0"));
        }

        private void Decline() => Send("N");

        private byte ReadPhysicalByte(uint physicalAddress)
        {
            Send(string.Create(CultureInfo.InvariantCulture, $"R {physicalAddress}"));
            var reply = ReadReply();
            if (!reply.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a '{DataPrefix}' reply, received '{reply}'.");
            }

            return (byte)ParseUInt(reply[DataPrefix.Length..].Trim());
        }

        private void WritePhysicalByte(uint physicalAddress, byte value)
        {
            Send(string.Create(CultureInfo.InvariantCulture, $"W {physicalAddress} {value}"));
            ReadReply();
        }

        private string ReadReply() =>
            _fromHost?.ReadLine() ??
            throw new InvalidOperationException("The generated host closed its output mid-protocol.");

        private void Send(string line)
        {
            if (_toHost is null)
            {
                throw new InvalidOperationException("The host-transfer session is not attached to a process.");
            }

            _toHost.WriteLine(line);
        }

        private static uint ParseUInt(string value) =>
            uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-differential", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    // The MinGW toolchain appends ".exe" on Windows; POSIX builds produce the name
    // given to -o. The driver therefore asks gcc for the extensionless name and
    // resolves whatever the platform actually produced before running it.
    private static string ResolveBinaryCandidate(string dir) => Path.Combine(dir, "program");

    private static string ResolveBinaryPath(string dir)
    {
        var candidate = ResolveBinaryCandidate(dir);
        if (File.Exists(candidate)) return candidate;
        var exe = candidate + ".exe";
        return File.Exists(exe) ? exe : candidate;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "...";

    // Deterministic, self-contained C driver appended to the generated source.
    // The input file carries <gpr[0..31]> <hi> <lo> <pc> <host block budget>, then
    // the fixture's initial memory as (address, byte) writes and the memory window
    // addresses to sample into the snapshot. The driver zeroes guest RAM, applies
    // the initial memory, runs the bounded dispatch, then prints a stable,
    // parseable state snapshot including each sampled window byte.
    //
    // The driver also provides minimal memory helper implementations backed by
    // a 2 MiB RAM buffer, matching RecompilerGuestMemory (KUSEG physical,
    // KSEG0/KSEG1 masked). The hardware-register window [0x1F801000, +8 KiB) —
    // the guest-visible MMIO state the interpreter keeps in its persistent
    // core's hw_regs — is backed by a second buffer so DMA/timer/interrupt
    // registers can round-trip across the per-segment process forks (Issue
    // #387); the driver models no controller semantics, it only carries the
    // bytes. Out-of-range reads return 0; out-of-range writes are dropped.
    private const string DriverSource = @"
#include <stdio.h>
#include <string.h>

#define PSX_TEST_RAM_SIZE (2u * 1024u * 1024u)
#define PSX_TEST_MAX_INIT 256u
#define PSX_TEST_MAX_WINDOW 256u
#define PSX_TEST_RAM_BLOCK_SIZE 512u
#define PSX_TEST_MAX_RAM_BLOCKS 4096u
static uint8_t test_ram[PSX_TEST_RAM_SIZE];

/* The hardware-register window (Issue #387). The interpreter keeps the guest's
   software-visible MMIO state in the native core's 8 KiB hw_regs buffer at
   PSX_HW_REG_BASE 0x1F801000, and it persists across title segments because the
   interpreter holds one core. The generated host forks one process per segment,
   so that state has to be carried across the fork just like guest RAM: the
   driver holds a byte buffer of the same size, routes [0x1F801000, 0x1F802000)
   to it from the CPU memory helpers, resets it to zero, applies an optional
   HWREG preload from the segment input, and dumps it back as HWREG lines under
   --full-ram. This mirrors the interpreter's guest-visible contract without
   duplicating any DMA/timer/interrupt controller semantics (Issue #386). */
#define PSX_TEST_HW_BASE 0x1F801000u
#define PSX_TEST_HW_SIZE (8u * 1024u)
#define PSX_TEST_MAX_HW_BLOCKS (PSX_TEST_HW_SIZE / PSX_TEST_RAM_BLOCK_SIZE)
static uint8_t test_hw[PSX_TEST_HW_SIZE];
static uint32_t hw_offsets[PSX_TEST_MAX_HW_BLOCKS];
static char hw_hex[PSX_TEST_MAX_HW_BLOCKS][PSX_TEST_RAM_BLOCK_SIZE * 2u + 1u];

static uint32_t init_addrs[PSX_TEST_MAX_INIT];
static uint32_t init_vals[PSX_TEST_MAX_INIT];
static uint32_t window_addrs[PSX_TEST_MAX_WINDOW];
static uint32_t ram_offsets[PSX_TEST_MAX_RAM_BLOCKS];
static char ram_hex[PSX_TEST_MAX_RAM_BLOCKS][PSX_TEST_RAM_BLOCK_SIZE * 2u + 1u];

static int hex_val(char c) {
    if ((unsigned)(c - '0') <= 9u) return c - '0';
    if ((unsigned)(c - 'a') <= 5u) return c - 'a' + 10;
    if ((unsigned)(c - 'A') <= 5u) return c - 'A' + 10;
    return -1;
}

static uint32_t test_translate(uint32_t va) {
    if (va <= 0x7FFFFFFFu) return va;
    if (va <= 0xBFFFFFFFu) return va & 0x1FFFFFFFu;
    return 0xFFFFFFFFu;
}

/* The single read/write path for guest memory: below PSX_TEST_RAM_SIZE it is
   the RAM buffer, in the hardware-register window it is test_hw, and anywhere
   else reads return 0 and writes are dropped (the pre-#387 behavior). Widths
   are little-endian; a wide access must fit inside the buffer or it is
   out of range, exactly as the pre-#387 RAM-only helpers required. */
static uint32_t test_read(uint32_t pa, uint32_t width) {
    uint32_t v = 0, k, base = pa;
    if (pa >= PSX_TEST_RAM_SIZE) {
        if (pa < PSX_TEST_HW_BASE) return 0;
        base = pa - PSX_TEST_HW_BASE;
        if (base > PSX_TEST_HW_SIZE - width) return 0;
        for (k = 0; k < width; k++) v |= (uint32_t)test_hw[base + k] << (8u * k);
        return v;
    }
    if (pa > PSX_TEST_RAM_SIZE - width) return 0;
    for (k = 0; k < width; k++) v |= (uint32_t)test_ram[pa + k] << (8u * k);
    return v;
}

static void test_write(uint32_t pa, uint32_t width, uint32_t value) {
    uint32_t k, base = pa;
    if (pa >= PSX_TEST_RAM_SIZE) {
        if (pa < PSX_TEST_HW_BASE) return;
        base = pa - PSX_TEST_HW_BASE;
        if (base > PSX_TEST_HW_SIZE - width) return;
        for (k = 0; k < width; k++) test_hw[base + k] = (uint8_t)(value >> (8u * k));
        return;
    }
    if (pa > PSX_TEST_RAM_SIZE - width) return;
    for (k = 0; k < width; k++) test_ram[pa + k] = (uint8_t)(value >> (8u * k));
}

uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    return (uint8_t)test_read(test_translate(address), 1);
}

uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    return (uint16_t)test_read(test_translate(address), 2);
}

uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    return test_read(test_translate(address), 4);
}

void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    test_write(test_translate(address), 1, value);
}

void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    test_write(test_translate(address), 2, value);
}

void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    test_write(test_translate(address), 4, value);
}

/* Host control-transfer protocol (Issue #362).
   The generated dispatch offers any PC it has no block for to state->host_transfer.
   This driver only relays that offer to the parent process and applies the answer:
   it knows nothing about the BIOS, about jump tables, or about services, so every
   BIOS semantic stays on the Runtime side of the boundary (ADR-014).

   child -> parent:  RHOST_INIT
                     RHOST_TRANSFER <pc> <gpr0> .. <gpr31>
                     RHOST_DATA <byte>
                     RHOST_OK
   parent -> child:  R <physical address>
                     W <physical address> <byte>
                     N                                   (pc not claimed)
                     D <termination> <next pc> <has v0> <v0>  */
#define PSX_REG_V0 2

static int32_t host_serve(RecompilerState* state) {
    for (;;) {
        char cmd[8];
        unsigned long a, v, t, np, has_v0;
        if (scanf(""%7s"", cmd) != 1) return 1;
        if (cmd[0] == 'R') {
            if (scanf(""%lu"", &a) != 1) return 1;
            printf(""RHOST_DATA %u\n"", (unsigned)recompiler_read_mem8((void*)0, (uint32_t)a));
            fflush(stdout);
        } else if (cmd[0] == 'W') {
            if (scanf(""%lu %lu"", &a, &v) != 2) return 1;
            recompiler_write_mem8((void*)0, (uint32_t)a, (uint8_t)v);
            printf(""RHOST_OK\n"");
            fflush(stdout);
        } else if (cmd[0] == 'D') {
            if (scanf(""%lu %lu %lu %lu"", &t, &np, &has_v0, &v) != 4) return 1;
            state->termination_reason = (int32_t)t;
            state->next_pc = (uint32_t)np;
            if (has_v0 != 0ul) state->gpr[PSX_REG_V0] = (uint32_t)v;
            return 0;
        } else {
            return 1; /* 'N': the parent does not claim this pc. */
        }
    }
}

static int32_t host_transfer(RecompilerState* state) {
    int i;
    printf(""RHOST_TRANSFER %lu"", (unsigned long)state->pc);
    for (i = 0; i < 32; i++) printf("" %lu"", (unsigned long)state->gpr[i]);
    printf(""\n"");
    fflush(stdout);
    return host_serve(state);
}

int main(int argc, char** argv) {
    if (argc < 2) return 90; /* MissingInput */
    FILE* in = fopen(argv[1], ""r"");
    if (!in) return 91;      /* CannotOpenInput */
    RecompilerState state;
    memset(&state, 0, sizeof(state));
    unsigned long u, a, v;
    int i;
    for (i = 0; i < 32; i++) { if (fscanf(in, ""%lu"", &u) != 1) return 92; state.gpr[i] = (uint32_t)u; }
    if (fscanf(in, ""%lu"", &u) != 1) return 92; state.hi = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) return 92; state.lo = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) return 92; state.pc = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) return 92; unsigned long budget = u;

    if (fscanf(in, ""%lu"", &u) != 1) return 92;
    if (u > PSX_TEST_MAX_INIT) return 93;       /* TooManyInits */
    unsigned long init_count = u;
    for (i = 0; i < (int)init_count; i++) {
        if (fscanf(in, ""%lu %lu"", &a, &v) != 2) return 92;
        init_addrs[i] = (uint32_t)a;
        init_vals[i] = (uint32_t)v;
    }

    if (fscanf(in, ""%lu"", &u) != 1) return 92;
    if (u > PSX_TEST_MAX_WINDOW) return 94;     /* TooManyWindowAddresses */
    unsigned long window_count = u;
    for (i = 0; i < (int)window_count; i++) {
        if (fscanf(in, ""%lu"", &a) != 1) return 92;
        window_addrs[i] = (uint32_t)a;
    }

    /* Optional full-RAM preload (full-title segments 2+, Issue #366). A
       missing section means 0 blocks, so every pre-#366 input stays valid. */
    unsigned long ram_blocks = 0;
    if (fscanf(in, ""%lu"", &u) == 1) {
        if (u > PSX_TEST_MAX_RAM_BLOCKS) return 95;  /* TooManyRamBlocks */
        ram_blocks = u;
        for (i = 0; i < (int)ram_blocks; i++) {
            /* Width 1024 == PSX_TEST_RAM_BLOCK_SIZE * 2: a full-size block's hex
               payload, leaving the +1 row byte for the NUL terminator. */
            if (fscanf(in, ""%lu %1024s"", &a, ram_hex[i]) != 2) return 92;
            ram_offsets[i] = (uint32_t)a;
            if (strlen(ram_hex[i]) != PSX_TEST_RAM_BLOCK_SIZE * 2u) return 92;
        }
    }

    /* Optional hardware-register preload (full-title segments 2+, Issue #387),
       trailing after the RAM section so every existing input stays valid. */
    unsigned long hw_blocks = 0;
    if (fscanf(in, ""%lu"", &u) == 1) {
        if (u > PSX_TEST_MAX_HW_BLOCKS) return 96;   /* TooManyHwBlocks */
        hw_blocks = u;
        for (i = 0; i < (int)hw_blocks; i++) {
            if (fscanf(in, ""%lu %1024s"", &a, hw_hex[i]) != 2) return 92;
            hw_offsets[i] = (uint32_t)a;
            if (strlen(hw_hex[i]) != PSX_TEST_RAM_BLOCK_SIZE * 2u) return 92;
        }
    }
    fclose(in);

    state.gpr[0] = 0;
    state.core = (void*)0;
    memset(test_ram, 0, sizeof(test_ram));
    memset(test_hw, 0, sizeof(test_hw));
    for (i = 0; i < (int)init_count; i++) {
        recompiler_write_mem8((void*)0, init_addrs[i], (uint8_t)init_vals[i]);
    }

    /* Full-title segments 2+ carry the previous segment's RAM so continuity
       survives the process restart. Hex pairs decode straight into test_ram. */
    for (i = 0; i < (int)ram_blocks; i++) {
        uint32_t base = ram_offsets[i];
        int j;
        for (j = 0; j < (int)PSX_TEST_RAM_BLOCK_SIZE; j++) {
            int hi2 = hex_val(ram_hex[i][j * 2]);
            int lo2 = hex_val(ram_hex[i][j * 2 + 1]);
            if (hi2 < 0 || lo2 < 0) return 92;
            if (base + (uint32_t)j < PSX_TEST_RAM_SIZE)
                test_ram[base + (uint32_t)j] = (uint8_t)((hi2 << 4) | lo2);
        }
    }

    /* And the same continuity for the software-visible hardware-register state
       (Issue #387), so MMIO writes from an earlier segment survive the fork. */
    for (i = 0; i < (int)hw_blocks; i++) {
        uint32_t base = hw_offsets[i];
        int j;
        for (j = 0; j < (int)PSX_TEST_RAM_BLOCK_SIZE; j++) {
            int hi2 = hex_val(hw_hex[i][j * 2]);
            int lo2 = hex_val(hw_hex[i][j * 2 + 1]);
            if (hi2 < 0 || lo2 < 0) return 92;
            if (base + (uint32_t)j < PSX_TEST_HW_SIZE)
                test_hw[base + (uint32_t)j] = (uint8_t)((hi2 << 4) | lo2);
        }
    }

    /* The host-transfer protocol is opt-in and must be requested by name: a
       stray extra argument never enables it, so a run with no parent listening
       can never block on the handshake. The handshake runs after the initial
       memory has landed and before the first block retires, so whatever the
       parent's Runtime seeds into guest RAM is visible to the generated program
       exactly as it is to the interpreter. */
    if (argc >= 3 && strcmp(argv[2], ""--host-transfer"") == 0) {
        state.host_transfer = &host_transfer;
        printf(""RHOST_INIT\n"");
        fflush(stdout);
        host_serve(&state);
        state.termination_reason = 0;
        state.next_pc = 0;
    }

    recompiler_dispatch(&state, (uint32_t)budget);
    printf(""RSNAPSHOT_BEGIN\n"");
    printf(""termination=%d\n"", (int)state.termination_reason);
    printf(""pc=0x%08X\n"", state.pc);
    printf(""hi=0x%08X\n"", state.hi);
    printf(""lo=0x%08X\n"", state.lo);
    for (i = 0; i < 32; i++) printf(""gpr[%d]=0x%08X\n"", i, state.gpr[i]);
    printf(""exception.raised=%d\n"", (int)state.exception_raised);
    printf(""exception.code=0x%08X\n"", state.exception_code);
    printf(""exception.faultPc=0x%08X\n"", state.exception_fault_pc);
    printf(""exception.inDelaySlot=%d\n"", (int)state.exception_in_delay_slot);
    for (i = 0; i < (int)window_count; i++)
        printf(""mem[0x%08X]=0x%02X\n"", window_addrs[i],
               (unsigned)recompiler_read_mem8((void*)0, window_addrs[i]));
    printf(""RSNAPSHOT_END\n"");

    /* Full-title segments (Issue #366): dump the whole guest RAM as hex blocks
       so the parent process can keep the image across process restarts. */
    if (argc >= 4 && strcmp(argv[3], ""--full-ram"") == 0) {
        for (i = 0; i < (int)(PSX_TEST_RAM_SIZE / PSX_TEST_RAM_BLOCK_SIZE); i++) {
            unsigned long base = (unsigned long)i * PSX_TEST_RAM_BLOCK_SIZE;
            int j;
            printf(""RAMHEX %lu "", base);
            for (j = 0; j < (int)PSX_TEST_RAM_BLOCK_SIZE; j++)
                printf(""%02X"", (unsigned)test_ram[base + (unsigned)j]);
            printf(""\n"");
        }
    }

    /* Full-title segments (Issue #387): the same dump for the
       hardware-register window, so MMIO state survives the fork too. */
    if (argc >= 4 && strcmp(argv[3], ""--full-ram"") == 0) {
        for (i = 0; i < (int)(PSX_TEST_HW_SIZE / PSX_TEST_RAM_BLOCK_SIZE); i++) {
            unsigned long base = (unsigned long)i * PSX_TEST_RAM_BLOCK_SIZE;
            int j;
            printf(""HWREG %lu "", base);
            for (j = 0; j < (int)PSX_TEST_RAM_BLOCK_SIZE; j++)
                printf(""%02X"", (unsigned)test_hw[base + (unsigned)j]);
            printf(""\n"");
        }
    }

    return (int)state.termination_reason;
}
";
}
#pragma warning restore AARC003