using System.Diagnostics;
using System.Globalization;
using System.Text;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

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
    /// host can only enter a block it already compiled, so redirecting there
    /// would silently fall off the end of the program. Compiling a target found
    /// only at runtime is dynamic overlay recompilation (Issue #249), out of
    /// scope here; this fails loudly instead (Issue #279).
    /// </summary>
    public const string BiosPatchedTargetHasNoGeneratedBlockDiagnosticCode =
        "BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK";

    private const string Compiler = "gcc";
    private const string CompilerArgs = "-std=c11 -O0 -Wall -Wextra";
    private const string CheckpointCompileFlag = "-DRECOMPILER_CHECKPOINTS";
    private const int BuildTimeoutMs = 30000;
    private const int RunTimeoutMs = 5000;

    /// <summary>Command-line switch that turns the driver's host-transfer protocol on.</summary>
    private const string HostTransferArgument = "--host-transfer";

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

            var sourcePath = Path.Combine(tempDir, "program.c");
            var inputPath = Path.Combine(tempDir, "input.txt");

            File.WriteAllText(sourcePath, generated.Source + "\n" + DriverSource);
            WriteInputFile(inputPath, fixture);

            var (exit, _, stderr) = RunProcess(
                Compiler, $"{CompilerArgs} {sourcePath} -o {ResolveBinaryCandidate(tempDir)}", BuildTimeoutMs, out var timedOut);
            if (timedOut || exit != 0)
            {
                throw new InvalidOperationException(
                    $"Host compilation failed." + (string.IsNullOrEmpty(stderr) ? "" : "\n" + Truncate(stderr, 2000)));
            }

            return new CompiledBinary(ResolveBinaryPath(tempDir), inputPath, tempDir);
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
                Compiler, $"{CompilerArgs} {CheckpointCompileFlag} {sourcePath} -o {outputPath}", BuildTimeoutMs, out var buildTimedOut);

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

            var (runExit, runOut, runErr) = RunProcess(
                ResolveBinaryPath(tempDir),
                bios is null ? inputPath : $"{inputPath} {HostTransferArgument}",
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

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
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

        var output = new StringBuilder();
        try
        {
            process.StandardInput.AutoFlush = true;
            session.Attach(process.StandardOutput, process.StandardInput);

            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!session.TryHandle(line))
                {
                    output.AppendLine(line);
                }
            }
        }
        catch
        {
            TryKillTree(process);
            throw;
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
    private sealed class HostTransferSession
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

        public HostTransferSession(
            Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory,
            IReadOnlySet<uint> blockEntryPcs)
        {
            _biosRuntimeFactory = biosRuntimeFactory;
            _blockEntryPcs = blockEntryPcs;
        }

        /// <summary>The diagnostic of the dispatch that stopped the run, when one did.</summary>
        public string? DiagnosticCode { get; private set; }

        /// <inheritdoc cref="DiagnosticCode" />
        public string? DiagnosticMessage { get; private set; }

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

            // The generated host can only enter a PC it compiled a block for. A
            // patched target outside the static block table is reported, never
            // silently jumped to and lost at the next unknown-PC boundary.
            if (outcome.IsPatchedTarget && !_blockEntryPcs.Contains(outcome.NextPc))
            {
                var functionNumber = (byte)(gpr[(int)R3000aRegister.T1] & 0xFFu);
                Stop(
                    BiosPatchedTargetHasNoGeneratedBlockDiagnosticCode,
                    $"{family}:{functionNumber:X2}: patched jump-table entry names guest address " +
                    $"0x{outcome.NextPc:X8}, which this program has no generated block for, so the " +
                    "recompiled path cannot transfer control to it. Compiling a target discovered at " +
                    "runtime is dynamic overlay recompilation (Issue #249), out of scope here.");
                return;
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
    // KSEG0/KSEG1 masked). Out-of-range reads return 0; out-of-range writes are
    // silently dropped.
    private const string DriverSource = @"
#include <stdio.h>
#include <string.h>

#define PSX_TEST_RAM_SIZE (2u * 1024u * 1024u)
#define PSX_TEST_MAX_INIT 256u
#define PSX_TEST_MAX_WINDOW 256u
static uint8_t test_ram[PSX_TEST_RAM_SIZE];
static uint32_t init_addrs[PSX_TEST_MAX_INIT];
static uint32_t init_vals[PSX_TEST_MAX_INIT];
static uint32_t window_addrs[PSX_TEST_MAX_WINDOW];

static uint32_t test_translate(uint32_t va) {
    if (va <= 0x7FFFFFFFu) return va;
    if (va <= 0xBFFFFFFFu) return va & 0x1FFFFFFFu;
    return 0xFFFFFFFFu;
}

uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa >= PSX_TEST_RAM_SIZE) return 0;
    return test_ram[pa];
}

uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 2) return 0;
    return (uint16_t)(test_ram[pa] | ((uint16_t)test_ram[pa + 1] << 8));
}

uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 4) return 0;
    return (uint32_t)(test_ram[pa]
        | ((uint32_t)test_ram[pa + 1] << 8)
        | ((uint32_t)test_ram[pa + 2] << 16)
        | ((uint32_t)test_ram[pa + 3] << 24));
}

void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa >= PSX_TEST_RAM_SIZE) return;
    test_ram[pa] = value;
}

void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 2) return;
    test_ram[pa] = (uint8_t)value;
    test_ram[pa + 1] = (uint8_t)(value >> 8);
}

void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 4) return;
    test_ram[pa] = (uint8_t)value;
    test_ram[pa + 1] = (uint8_t)(value >> 8);
    test_ram[pa + 2] = (uint8_t)(value >> 16);
    test_ram[pa + 3] = (uint8_t)(value >> 24);
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
    fclose(in);

    state.gpr[0] = 0;
    state.core = (void*)0;
    memset(test_ram, 0, sizeof(test_ram));
    for (i = 0; i < (int)init_count; i++) {
        recompiler_write_mem8((void*)0, init_addrs[i], (uint8_t)init_vals[i]);
    }

    /* A second argument turns the host-transfer protocol on. The handshake runs
       after the initial memory has landed and before the first block retires, so
       whatever the parent's Runtime seeds into guest RAM is visible to the
       generated program exactly as it is to the interpreter. */
    if (argc >= 3) {
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
    for (i = 0; i < (int)window_count; i++)
        printf(""mem[0x%08X]=0x%02X\n"", window_addrs[i],
               (unsigned)recompiler_read_mem8((void*)0, window_addrs[i]));
    printf(""RSNAPSHOT_END\n"");
    return (int)state.termination_reason;
}
";
}
#pragma warning restore AARC003