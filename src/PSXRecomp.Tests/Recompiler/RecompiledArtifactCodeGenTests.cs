using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public sealed class RecompiledArtifactCodeGenTests
{
    private static RecompilerHostCodeGenResult GenerateDispatch()
    {
        var instruction = R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = MipsToIrLowerer.LowerProgram([(instruction, 0x80010000u)]);
        return RecompilerHostCodeGen.Generate(program);
    }

    [Fact]
    public void Generate_ValidDispatch_AppendsProductionDriver()
    {
        var dispatch = GenerateDispatch();
        dispatch.Success.Should().BeTrue();

        var result = RecompiledArtifactCodeGen.Generate(dispatch);

        result.Success.Should().BeTrue();
        result.Source.Should().StartWith(dispatch.Source!);
        result.Source.Should().Contain("int main(int argc, char** argv)");
        result.Source.Should().Contain(RecompiledArtifactCodeGen.SnapshotBeginMarker);
        result.Source.Should().Contain(RecompiledArtifactCodeGen.SnapshotEndMarker);
        result.Source.Should().Contain(RecompiledArtifactCodeGen.HostTransferFlag);
        result.Source.Should().Contain("return (int)state.termination_reason;");
    }

    [Fact]
    public void GenerateStandalone_ValidInputs_ProducesCompleteSourceWithEmbeddedData()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[32];
        initialGpr[2] = 0x12345678u; // V0
        var initialMemory = new (uint Address, uint Value)[] { (0x80010000u, 0xDEADBEEFu) };
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 }; // ori $v0, $zero, 0

        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            initialGpr,
            0xABCDABCDu,
            0xEFEFEFEFu,
            0x80010000u,
            1000,
            initialMemory,
            0x80010000u,
            programImage);

        result.Success.Should().BeTrue();
        result.Source.Should().NotBeNull();

        var source = result.Source!;
        // Should contain embedded data
        source.Should().Contain("static const uint32_t embedded_gpr[32] = {");
        source.Should().Contain("0x12345678u"); // V0 value
        source.Should().Contain("static const uint32_t embedded_hi = 0xABCDABCDu;");
        source.Should().Contain("static const uint32_t embedded_lo = 0xEFEFEFEFu;");
        source.Should().Contain("static const uint32_t embedded_pc = 0x80010000u;");
        source.Should().Contain("static const uint32_t embedded_budget = 1000u;");
        source.Should().Contain("static const uint32_t embedded_init_count = 1u;");
        source.Should().Contain("static const uint32_t embedded_init_addrs[] = {");
        source.Should().Contain("0x80010000u");
        source.Should().Contain("static const uint32_t embedded_init_vals[] = {");
        source.Should().Contain("0xDEADBEEFu");
        source.Should().Contain("static const uint32_t embedded_image_load_address = 0x80010000u;");
        source.Should().Contain("static const uint32_t embedded_image_len = 4u;");
        source.Should().Contain("static const uint8_t embedded_image[] = {");
        source.Should().Contain("0x00, 0x00, 0x02, 0x34");
        // Should contain standalone main (no argc/argv file reading)
        source.Should().Contain("int main(int argc, char** argv) {");
        source.Should().Contain("(void)argc; (void)argv;");
        source.Should().Contain("state.gpr[i] = embedded_gpr[i];");
        source.Should().Contain("artifact_ram_write8(embedded_init_addrs[i]");
        source.Should().Contain("embedded_image[image_i]");
        // Should NOT contain file I/O
        source.Should().NotContain("fopen");
        source.Should().NotContain("fscanf");
        source.Should().NotContain("argv[1]");
        source.Should().NotContain("argv[2]");
    }

    [Fact]
    public void GenerateStandalone_WithHostBridge_ProducesBridgeCapableEmbeddedMain()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[32];
        var initialMemory = new (uint Address, uint Value)[] { (0x80010000u, 0xDEADBEEFu) };
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 };

        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            initialGpr,
            0xABCDABCDu,
            0xEFEFEFEFu,
            0x80010000u,
            1000,
            initialMemory,
            0x80010000u,
            programImage,
            hostBridge: true);

        result.Success.Should().BeTrue();
        var source = result.Source!;

        // Phase 2 (Issue #757): the embedded-image main can attach the shared
        // Runtime host bridge over the existing --host-transfer path.
        source.Should().Contain("int main(int argc, char** argv) {");
        source.Should().Contain("artifact_attach_host_bridge(&state);");
        source.Should().Contain("--host-transfer");
        source.Should().Contain("--guest-exceptions");
        source.Should().Contain("--exact-device-time");
        // The bridge is opt-in: without the flag the run stays parentless, so
        // the flag must never be enabled unconditionally.
        source.Should().NotContain("(void)argc;");
        // Flag order cannot change the result: every flag is an independent
        // `if (strcmp(argv[argi], ...))` inside one position-scanning loop, and
        // the attach happens only after the whole scan (no positional argv[3]
        // shortcut, no else-if chain, no early break).
        source.Should().Contain("for (int argi = 1; argi < argc; argi++) {");
        source.Should().Contain("if (strcmp(argv[argi], \"--guest-exceptions\") == 0) state.guest_exceptions = 1u;");
        source.Should().Contain("if (strcmp(argv[argi], \"--exact-device-time\") == 0) artifact_exact_time = 1;");
        source.Should().Contain("if (strcmp(argv[argi], \"--host-transfer\") == 0) attach_bridge = 1;");
        source.Should().Contain("if (attach_bridge) artifact_attach_host_bridge(&state);");
        source.Should().NotContain("else if (strcmp(argv[argi]");
        source.Should().NotContain("argv[3]");
        // Still a self-contained embedded artifact: no file reading leaks in.
        source.Should().NotContain("fopen");
        source.Should().NotContain("fscanf");
        source.Should().NotContain("argv[1]");
        source.Should().NotContain("argv[2]");
    }

    [Fact]
    public void GenerateStandalone_WithHostBridge_IsDeterministic_ForIdenticalInput()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[32];
        initialGpr[2] = 0x12345678u;
        var initialMemory = new (uint Address, uint Value)[] { (0x80010000u, 0xDEADBEEFu) };
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 };

        var first = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, initialGpr, 0xABCDABCDu, 0xEFEFEFEFu, 0x80010000u, 1000, initialMemory, 0x80010000u, programImage, hostBridge: true);
        var second = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, initialGpr, 0xABCDABCDu, 0xEFEFEFEFu, 0x80010000u, 1000, initialMemory, 0x80010000u, programImage, hostBridge: true);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        first.Source.Should().Be(second.Source);
    }

    [Fact]
    public void GenerateStandalone_InvalidGprCount_ReturnsFailure()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[31]; // Wrong count
        var initialMemory = Array.Empty<(uint Address, uint Value)>();
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 };

        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            initialGpr,
            0u, 0u, 0x80010000u, 1000,
            initialMemory,
            0x80010000u,
            programImage);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("INVALID_GPR_COUNT");
    }

    [Fact]
    public void GenerateStandalone_TooManyInitEntries_ReturnsFailure()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[32];
        var initialMemory = new (uint Address, uint Value)[RecompiledArtifactCodeGen.MaxInitEntries + 1];
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 };

        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            initialGpr,
            0u, 0u, 0x80010000u, 1000,
            initialMemory,
            0x80010000u,
            programImage);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("TOO_MANY_INIT_ENTRIES");
    }

    [Fact]
    public void GenerateStandalone_UpstreamCodegenFailure_PropagatesAsFailure()
    {
        var failed = new RecompilerHostCodeGenResult(false, null, "SOME_CODE", "some message");
        var initialGpr = new uint[32];
        var initialMemory = Array.Empty<(uint Address, uint Value)>();
        var programImage = new byte[] { 0x00 };

        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            failed,
            initialGpr,
            0u, 0u, 0x80010000u, 1000,
            initialMemory,
            0x80010000u,
            programImage);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("SOME_CODE");
    }

    [Fact]
    public void GenerateStandalone_IsDeterministic_ForIdenticalInput()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[32];
        initialGpr[2] = 0x12345678u;
        var initialMemory = new (uint Address, uint Value)[] { (0x80010000u, 0xDEADBEEFu) };
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 };

        var first = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, initialGpr, 0xABCDABCDu, 0xEFEFEFEFu, 0x80010000u, 1000,
            initialMemory, 0x80010000u, programImage);
        var second = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, initialGpr, 0xABCDABCDu, 0xEFEFEFEFu, 0x80010000u, 1000,
            initialMemory, 0x80010000u, programImage);

        first.Source.Should().Be(second.Source);
    }

    [Fact]
    public void Generate_ExactDeviceTimeFlag_IsEmittedFromSharedConstant()
    {
        var source = RecompiledArtifactCodeGen.Generate(GenerateDispatch()).Source!;

        source.Should().Contain(RecompiledArtifactCodeGen.ExactDeviceTimeFlag);
        source.Should().NotContain("@EXACT_DEVICE_TIME@");
        source.Should().Contain($"if (strcmp(argv[i], \"{RecompiledArtifactCodeGen.ExactDeviceTimeFlag}\") == 0) artifact_exact_time = 1;");
    }

    [Fact]
    public void Generate_IsDeterministic_ForIdenticalInput()
    {
        var dispatch = GenerateDispatch();

        var first = RecompiledArtifactCodeGen.Generate(dispatch);
        var second = RecompiledArtifactCodeGen.Generate(dispatch);

        first.Source.Should().Be(second.Source);
    }

    [Fact]
    public void Generate_UpstreamCodegenFailure_PropagatesAsFailure()
    {
        var failed = new RecompilerHostCodeGenResult(false, null, "SOME_CODE", "some message");

        var result = RecompiledArtifactCodeGen.Generate(failed);

        result.Success.Should().BeFalse();
        result.Source.Should().BeNull();
        result.DiagnosticCode.Should().Be("SOME_CODE");
    }

    [Fact]
    public void Generate_DriverInitLimit_IsEmittedFromMaxInitEntries_SoTheCBoundCannotDrift()
    {
        var dispatch = GenerateDispatch();

        var source = RecompiledArtifactCodeGen.Generate(dispatch).Source!;

        // The driver's PSX_MAX_INIT is emitted from MaxInitEntries (a verbatim
        // string cannot interpolate, so Generate substitutes it) — the emitted C
        // bound must equal the C# constant, in both directions, or this test
        // fails and forces the drift to be resolved deliberately.
        var emitted = Regex.Match(source, @"#define PSX_MAX_INIT (\d+)u");
        emitted.Success.Should().BeTrue("the driver must define PSX_MAX_INIT in the generated source");
        uint.Parse(emitted.Groups[1].Value, CultureInfo.InvariantCulture)
            .Should().Be((uint)RecompiledArtifactCodeGen.MaxInitEntries);
        source.Should().Contain($"#define PSX_MAX_INIT {RecompiledArtifactCodeGen.MaxInitEntries}u");
    }

    [Fact]
    public void Generate_SyscallHook_CommitsTheExceptionEntrySr_BeforeTheOffer_AndPopsOnlyOnAServicedReturn()
    {
        var dispatch = GenerateDispatch();

        var source = RecompiledArtifactCodeGen.Generate(dispatch).Source!;

        // The SYSCALL exception entry is state, not just the value offered to the
        // host: the artifact is the CPU, so it must commit the pushed KU/IE to
        // cop0_sr the way the interpreter's CPU does. The commit has to land before
        // the offer, and the RFE pop has to stay gated on the serviced-return
        // branch, so an unsupported SYS, a host decline, and a fail-closed decision
        // all snapshot the post-entry SR instead of the pre-exception one.
        var hook = Regex.Match(
            source,
            @"static int32_t artifact_host_syscall\(RecompilerState\* state\) \{(?<body>.*?)\n\}",
            RegexOptions.Singleline);
        hook.Success.Should().BeTrue("the driver must define the SYSCALL hook");
        var body = hook.Groups["body"].Value;

        var commit = body.IndexOf("recompiler_exception_entry(state, 8u, state->exception_fault_pc, 0u)", StringComparison.Ordinal);
        var offer = body.IndexOf(RecompiledArtifactCodeGen.ProtocolSyscallPrefix, StringComparison.Ordinal);
        var serve = body.IndexOf("artifact_host_serve(state)", StringComparison.Ordinal);
        var pop = body.IndexOf(
            "state->cop0_sr = (state->cop0_sr & ~0xFu) | ((state->cop0_sr >> 2) & 0xFu);",
            StringComparison.Ordinal);

        commit.Should().BeGreaterThanOrEqualTo(0, "the exception entry must be written back to state, not only offered");
        commit.Should().BeLessThan(offer, "the offer reports the post-entry SR, so the state must already hold it");
        offer.Should().BeLessThan(serve, "the host decision is the last thing that happens on the way out");
        pop.Should().BeGreaterThan(serve);
        body[serve..pop].Should().Contain(
            "if (declined == 0 && state->termination_reason == 0) {",
            "the RFE pop must stay gated on the serviced-return branch so a declined, unsupported, or fail-closed path keeps the post-entry SR");
    }

    /// <summary>
    /// The standalone builder rewrites the dispatch's legacy unknown-PC-with-steps
    /// branch so that stopping at an unresolved transfer is never reported as
    /// success — see #757. This test is source-level: the C text produced by
    /// GenerateStandalone is asserted against the exact patch target, and the
    /// compiled-run case is covered by GenerateStandalone_UnknownTransfer_LeavesELFWithTermination5AndExitCode5.
    /// </summary>
    [Fact]
    public void GenerateStandalone_UnknownTransferBranch_ReplacedWithUnsupportedTransferReason()
    {
        // A JAL to an uncompiled address produces the dispatch's unknown-PC branch,
        // which the standalone builder rewrites to RECOMPILER_REASON_UNSUPPORTED_TRANSFER
        // (value 5, matching RecompilerIrTerminationReason.UnresolvedIndirectFlow)
        // instead of RECOMPILER_REASON_SUCCESS.
        var instr = R3000aDecoder.Decode(0x3000048Du); // jal 0x80001234 from 0x80001000
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80001000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        var initialGpr = new uint[32];
        var initialMemory = Array.Empty<(uint Address, uint Value)>();
        var programImage = new byte[] { 0x30, 0x00, 0x04, 0x8D }; // the jal word, little-endian
        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            initialGpr,
            0u, 0u, 0x80001000u,
            1000u,
            initialMemory,
            0x80001000u,
            programImage);

        result.Success.Should().BeTrue();
        result.Source.Should().NotBeNull();
        var source = result.Source!;

        source.Should().Contain("#define RECOMPILER_REASON_UNSUPPORTED_TRANSFER 5");
        source.Should().Contain(
            "if (steps > 0) { state->termination_reason = RECOMPILER_REASON_UNSUPPORTED_TRANSFER; return 0; }");
        // The legacy rewrite must not still label the unresolved PC as success.
        source.Should().NotContain(
            "if (steps > 0) { state->termination_reason = RECOMPILER_REASON_SUCCESS; return 0; }");
    }

    [SkippableFact]
    public void GenerateStandalone_UnknownTransfer_LeavesELFWithTermination5AndExitCode5()
    {
#pragma warning disable AARC003
        // The full pipeline: generate standalone C, compile it with gcc, launch the ELF,
        // and assert the stop reason is the unsupported-transfer boundary rather than
        // success. Skips only if no C compiler is present; the source-level assertions
        // above still hold without it.
        var gccPath = File.Exists("/usr/bin/gcc") ? "/usr/bin/gcc" : "/usr/bin/cc";
        if (!File.Exists(gccPath))
        {
            Skip.If(true, "no C compiler available on this host");
        }

        var instr = R3000aDecoder.Decode(0x3000048Du); // jal 0x80001234 from 0x80001000
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80001000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        var initialGpr = new uint[32];
        var initialMemory = Array.Empty<(uint Address, uint Value)>();
        var programImage = new byte[] { 0x30, 0x00, 0x04, 0x8D }; // the jal word, little-endian
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-standalone-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = RecompiledArtifactCodeGen.GenerateStandalone(
                dispatch,
                initialGpr,
                0u, 0u, 0x80001000u,
                1000u,
                initialMemory,
                0x80001000u,
                programImage);

            result.Success.Should().BeTrue();
            result.Source.Should().NotBeNull();

            var cPath = Path.Combine(tempDir, "test.c");
            File.WriteAllText(cPath, result.Source!);
            var exePath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "test.exe" : "test");

            var compile = RunCompiler(gccPath, cPath, exePath);
            compile.ExitCode.Should().Be(0, "the self-contained generated artifact must compile");

            var run = RunProcess(exePath);
            run.ExitCode.Should().Be(5, "the process exit code must equal the stop reason (5 = unsupported transfer)");
            run.Output.Should().Contain("termination=5");
            run.Output.Should().Contain("pc=");
            run.Output.Should().Contain("gpr[0]=0x00000000");
            run.Output.Should().Contain("RSNAPSHOT_BEGIN");
            run.Output.Should().Contain("RSNAPSHOT_END");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void GenerateStandalone_WithHostBridge_WithoutAttachFlag_NeitherWaitsForHandshake_NorDependsOnFlagOrder()
    {
#pragma warning disable AARC003
        // The full pipeline with the bridge-capable main, launched WITHOUT
        // --host-transfer: the run must stay parentless (termination 5 / exit 5)
        // and must never block on a handshake — if it did, RunProcess's bounded
        // timeout would throw and fail this test. The optional flags must also
        // produce identical results in either order.
        var gccPath = File.Exists("/usr/bin/gcc") ? "/usr/bin/gcc" : "/usr/bin/cc";
        if (!File.Exists(gccPath))
        {
            Skip.If(true, "no C compiler available on this host");
        }

        var instr = R3000aDecoder.Decode(0x3000048Du); // jal 0x80001234 from 0x80001000
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80001000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        var initialGpr = new uint[32];
        var initialMemory = Array.Empty<(uint Address, uint Value)>();
        var programImage = new byte[] { 0x30, 0x00, 0x04, 0x8D }; // the jal word, little-endian
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-standalone-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = RecompiledArtifactCodeGen.GenerateStandalone(
                dispatch,
                initialGpr,
                0u, 0u, 0x80001000u,
                1000u,
                initialMemory,
                0x80001000u,
                programImage,
                hostBridge: true);

            result.Success.Should().BeTrue();
            result.Source.Should().NotBeNull();

            var cPath = Path.Combine(tempDir, "bridge.c");
            File.WriteAllText(cPath, result.Source!);
            var exePath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "bridge.exe" : "bridge");

            var compile = RunCompiler(gccPath, cPath, exePath);
            compile.ExitCode.Should().Be(0, "the bridge-capable generated artifact must compile");

            var parentless = RunProcess(exePath);
            parentless.ExitCode.Should().Be(5, "without the opt-in flag the run stays parentless (5 = unsupported transfer)");
            parentless.Output.Should().Contain("termination=5");

            var orderA = RunProcess(exePath, "--guest-exceptions --exact-device-time", 60000);
            var orderB = RunProcess(exePath, "--exact-device-time --guest-exceptions", 60000);
            orderA.ExitCode.Should().Be(5);
            orderB.ExitCode.Should().Be(5);
            orderA.Output.Should().Be(orderB.Output, "the flags parse position-independently, so order must not change the result");
            orderA.Output.Should().Contain("termination=5");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
#pragma warning restore AARC003
    }

    private static string GetOriginalDriverTemplate()
    {
        // DriverSource is a private const in the class; reflection reads the literal
        // so the failure-mode tests can inject modified templates without changing
        // production visibility of the generation machinery.
        var field = typeof(RecompiledArtifactCodeGen)
            .GetField("DriverSource", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)field.GetValue(null)!;
    }

    [Fact]
    public void GenerateStandalone_MainMarkerMissing_ReturnsFailure()
    {
        var instr = R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80010000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        var template = GetOriginalDriverTemplate()
            .Replace("/* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */", string.Empty);
        var initialGpr = new uint[32];
        var result = RecompiledArtifactCodeGen.BuildStandaloneSource(
            dispatch.Source!,
            template,
            initialGpr,
            0u, 0u, 0x80010000u,
            1000u,
            Array.Empty<(uint Address, uint Value)>(),
            0x80010000u,
            Array.Empty<byte>());

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("STANDALONE_MAIN_MISSING");
    }

    [Fact]
    public void GenerateStandalone_MainMarkerDuplicated_ReturnsFailure()
    {
        var instr = R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80010000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        var template = GetOriginalDriverTemplate()
            .Replace("/* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */",
                "/* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */ /* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */");
        var initialGpr = new uint[32];
        var result = RecompiledArtifactCodeGen.BuildStandaloneSource(
            dispatch.Source!,
            template,
            initialGpr,
            0u, 0u, 0x80010000u,
            1000u,
            Array.Empty<(uint Address, uint Value)>(),
            0x80010000u,
            Array.Empty<byte>());

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("STANDALONE_MAIN_DUPLICATED");
    }

    [Fact]
    public void GenerateStandalone_FileIOLeak_ReturnsFailure()
    {
        var instr = R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80010000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        // The leak check must not pass if the original driver's file-reading body
        // survives. We inject an fopen immediately after the original main's closing
        // brace so the region replacement (marker .. last '}') leaves it in the output.
        var template = GetOriginalDriverTemplate();
        var brace = template.LastIndexOf("}");
        template = template.Insert(brace + 1, "\nFILE* leak = fopen(\"/dev/null\", \"r\");\n");
        var initialGpr = new uint[32];
        var result = RecompiledArtifactCodeGen.BuildStandaloneSource(
            dispatch.Source!,
            template,
            initialGpr,
            0u, 0u, 0x80010000u,
            1000u,
            Array.Empty<(uint Address, uint Value)>(),
            0x80010000u,
            Array.Empty<byte>());

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("STANDALONE_FILE_IO_LEAK");
    }

    [Fact]
    public void GenerateStandalone_InitialState_EmbedsGpr0HiLoPcAndMemoryWithByteLevelWrap()
    {
        var dispatch = GenerateDispatch();
        var initialGpr = new uint[32];
        initialGpr[2] = 0x12345678u; // V0
        var initialMemory = new (uint Address, uint Value)[] { (0x80010000u, 0xDEADBEEFu) };
        var programImage = new byte[] { 0x00, 0x00, 0x02, 0x34 }; // ori $v0, $zero, 0
        var result = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            initialGpr,
            0xABCDABCDu,
            0xEFEFEFEFu,
            0x80010000u,
            1000u,
            initialMemory,
            0x80010000u,
            programImage);

        result.Success.Should().BeTrue();
        var source = result.Source!;

        // gpr[0] is explicitly forced to zero — a requirement of the initial CPU state.
        source.Should().Contain("state.gpr[0] = 0;");
        source.Should().Contain("state.hi = embedded_hi;");
        source.Should().Contain("state.lo = embedded_lo;");
        source.Should().Contain("state.pc = embedded_pc;");
        // Initial RAM starts zeroed, then embedded init values are written byte-wise.
        source.Should().Contain("memset(artifact_ram, 0, sizeof(artifact_ram));");
        source.Should().Contain("artifact_ram_write8(embedded_init_addrs[i], (uint8_t)embedded_init_vals[i]);");
        // Image bytes are written one at a time and wrap inside the low 2 MiB RAM window.
        source.Should().Contain("artifact_rom_loaded = image_in_rom;");
        source.Should().Contain("(image_pa + image_i) & (PSX_RAM_SIZE - 1u)");
        source.Should().Contain("PSX_ROM_BASE");
        source.Should().Contain("PSX_ROM_SIZE");
        source.Should().Contain("PSX_RAM_MIRROR_END");
    }

    #pragma warning disable AARC003

    // Track the exact child this runner started on the calling thread, so the
    // timeout test can verify it was killed rather than scanning the whole
    // machine for processes named "sleep". Each concurrent test thread keeps
    // its own value.
    private static readonly ThreadLocal<int> s_lastSpawnedProcessId = new();

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, draining
    /// stdout/stderr asynchronously while it runs so a large or blocking child
    /// cannot fill the kernel pipe and hang the test. On timeout the entire
    /// process tree is killed, the pipe readers are drained, and a
    /// <see cref="TimeoutException"/> is thrown.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RunProcessVerbose(
        string fileName, string arguments, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)!;

        // Track the exact child this runner started on this thread so the timeout
        // test can verify it was killed rather than scanning the whole machine.
        s_lastSpawnedProcessId.Value = process.Id;

        // Drain both pipes asynchronously while the process runs. Without this, a
        // child that writes more than the pipe buffer can hold blocks on write and
        // the test deadlocks inside WaitForExit; with it, the kernel pipe never
        // fills regardless of output volume.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            // The child exceeded its budget: kill the whole tree so no orphan
            // survives, then wait for the readers to complete so the captured
            // output is complete. The reads return immediately once the pipes are
            // closed by the killed child.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            try { Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromMilliseconds(500)); } catch { }
            throw new TimeoutException($"host process timed out: {fileName} {arguments}");
        }

        process.WaitForExit();
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// True when a live process with the given id exists. A terminated and reaped
    /// child makes <see cref="Process.GetProcessById(int)"/> throw
    /// <see cref="ArgumentException"/>, which is reported here as "not alive" so
    /// the timeout test can verify the exact child it started has stopped without
    /// scanning the whole machine for unrelated processes.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return !probe.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    #pragma warning restore AARC003

    private static (int ExitCode, string Output) RunProcess(string filePath) =>
        RunProcess(filePath, string.Empty, 60000);

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments, int timeoutMs)
    {
        var (exit, stdout, stderr) = RunProcessVerbose(fileName, arguments, timeoutMs);
        return (exit, stdout + stderr);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCompiler(
        string gccPath, string sourcePath, string outputPath)
    {
        var (exit, stdout, stderr) = RunProcessVerbose(
            gccPath, $"-std=c11 -O0 -Wall -Wextra \"{sourcePath}\" -o \"{outputPath}\"", 30000);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"generated artifact failed to compile:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        return (exit, stdout, stderr);
    }

    #pragma warning disable AARC003

    /// <summary>
    /// Verifies that the stress-capable <see cref="RunProcessVerbose"/> drains
    /// stdout/stderr without hanging, correctly returns separate streams,
    /// honours timeouts by killing the tree, and keeps compiler diagnostics.
    /// </summary>
    [SkippableFact]
    public void RunProcessVerbose_LargeOutputDoesNotHangAndRetainsSeparateStreams()
    {
        // Compile a self-contained C program that flushes several megabytes to
        // stdout and stderr independently — this exercises the async drains.
        var gccPath = File.Exists("/usr/bin/gcc")
            ? "/usr/bin/gcc"
            : File.Exists("/usr/bin/cc")
                ? "/usr/bin/cc"
                : null;
        if (gccPath is null)
        {
            Skip.If(true,
                "no C compiler (gcc or cc) available on this host; large-output test skipped");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-standalone-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var cPath = Path.Combine(tempDir, "flood.c");
            var exePath = Path.Combine(tempDir, "flood");
            // Write ~3 MiB to stdout, ~0.5 MiB to stderr, then exit 0.
            var program =
                "#include <stdio.h>\n" +
                "#include <string.h>\n" +
                "int main(void) {\n" +
                "    unsigned char buf[4096];\n" +
                "    memset(buf, 'X', sizeof(buf));\n" +
                "    for (int i = 0; i < 768; i++) fwrite(buf, 1, sizeof(buf), stdout); /* ~3 MiB */\n" +
                "    memset(buf, 'E', sizeof(buf));\n" +
                "    for (int i = 0; i < 128; i++) fwrite(buf, 1, sizeof(buf), stderr); /* ~0.5 MiB */\n" +
                "    return 0;\n" +
                "}\n";
            File.WriteAllText(cPath, program);

            var (exit, stdout, stderr) = RunCompiler(gccPath, cPath, exePath);
            exit.Should().Be(0, "the flood program must compile");

            var (runExit, runStdout, runStderr) = RunProcessVerbose(exePath, "", 60000);
            runExit.Should().Be(0, "the flood program must exit cleanly");
            runStdout.Length.Should().BeGreaterThanOrEqualTo(3 * 1024 * 1024, "stdout must carry the full flood");
            runStderr.Length.Should().BeGreaterThanOrEqualTo(512 * 1024, "stderr must carry the full flood");
            runStdout.Should().Contain("XXXXXXXX");
            runStderr.Should().Contain("EEEEEEEEE");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [SkippableFact]
    public void RunProcessVerbose_TimeoutKillsTreeAndThrows()
    {
        // RunProcessVerbose is designed to terminate the process tree on timeout and
        // surface the timeout as an exception (the companion RecompilerHostExecutor
        // runner instead returns int.MinValue with timedOut = true).
        // sleep is a POSIX utility, so this verification runs only on platforms that
        // provide it.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true,
                "the sleep utility is unavailable on this platform; timeout/tree-kill verification skipped");
        }

        // ThreadLocal tracks the exact child this runner started on this thread, so
        // the verification targets this test's process tree rather than scanning the
        // whole machine for processes named "sleep" (which could match unrelated
        // sleep invocations on the host).
        Assert.Throws<TimeoutException>(
            () => RunProcessVerbose("sleep", "999", 1000));

        var pid = s_lastSpawnedProcessId.Value;
        pid.Should().BeGreaterThan(0,
            "the runner must have tracked the child process it started");
        IsProcessAlive(pid).Should().BeFalse(
            "the child process the runner started must have been terminated on timeout");
    }

    [SkippableFact]
    public void RunProcessVerbose_CompilerFailureRetainsStderrDiagnostics()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-standalone-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var cPath = Path.Combine(tempDir, "bad.c");
            var exePath = Path.Combine(tempDir, "bad");
            File.WriteAllText(cPath, "int main(void) { return broken_undefined_identifier; }\n");
            var gccPath = File.Exists("/usr/bin/gcc")
                ? "/usr/bin/gcc"
                : File.Exists("/usr/bin/cc")
                    ? "/usr/bin/cc"
                    : null;
            if (gccPath is null)
            {
                Skip.If(true,
                    "no C compiler (gcc or cc) available on this host; compiler-diagnostics test skipped");
            }
            Assert.Throws<InvalidOperationException>(
                () => RunCompiler(gccPath, cPath, exePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildStandaloneSource_DispatchTextChanged_MissingUnknownPcBranch_ReturnsStructuredFailure()
    {
        // Fabricated dispatch source: contains the budget-exceeded define but no
        // unknown-PC-with-steps success line — mimics RecompilerHostCodeGen output
        // that no longer matches the standalone rewrite target.
        var template = GetOriginalDriverTemplate();
        var dispatchSource = "#define RECOMPILER_REASON_SUCCESS 0\n#define RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED 7\n";
        var initialGpr = new uint[32];
        var result = RecompiledArtifactCodeGen.BuildStandaloneSource(
            dispatchSource, template, initialGpr, 0u, 0u, 0u, 1000u,
            Array.Empty<(uint Address, uint Value)>(), 0u, Array.Empty<byte>());
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("STANDALONE_DISPATCH_SEMANTICS_MISSING_TARGET");
    }

    [Fact]
    public void BuildStandaloneSource_DuplicatedUnknownPcBranch_ReturnsStructuredFailure()
    {
        // Fabricated dispatch source with the unknown-PC target duplicated.
        var template = GetOriginalDriverTemplate();
        var unknownPcSuccessLine =
            "        if (steps > 0) { state->termination_reason = RECOMPILER_REASON_SUCCESS; return 0; }";
        var dispatchSource = "#define RECOMPILER_REASON_SUCCESS 0\n#define RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED 7\n" +
                             unknownPcSuccessLine + "\n" + unknownPcSuccessLine + "\n";
        var initialGpr = new uint[32];
        var result = RecompiledArtifactCodeGen.BuildStandaloneSource(
            dispatchSource, template, initialGpr, 0u, 0u, 0u, 1000u,
            Array.Empty<(uint Address, uint Value)>(), 0u, Array.Empty<byte>());
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("STANDALONE_DISPATCH_SEMANTICS_DUPLICATE_TARGET");
    }

    [Fact]
    public void Generate_NotStandalone_KeepsSuccessUnknownPcBranch()
    {
        // The host-transfer (non-standalone) path must keep the legacy unknown-PC
        // branch semantics (success when steps > 0) so the standalone rewrite
        // does not leak into ordinary artifacts.
        var instr = R3000aDecoder.Decode(0x3000048Du); // jal 0x80001234 from 0x80001000
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80001000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();
        var source = RecompiledArtifactCodeGen.Generate(dispatch).Source!;
        // The legacy path must still treat the unknown-PC-with-steps branch as success.
        source.Should().Contain(
            "if (steps > 0) { state->termination_reason = RECOMPILER_REASON_SUCCESS; return 0; }");
        // Whereas the standalone path must not.
        var initialGpr = new uint[32];
        var image = new byte[] { 0x30, 0x00, 0x04, 0x8D };
        var standalone = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, initialGpr, 0u, 0u, 0x80001000u, 1000u,
            Array.Empty<(uint Address, uint Value)>(), 0x80001000u, image);
        standalone.Success.Should().BeTrue();
        standalone.Source.Should().NotContain(
            "if (steps > 0) { state->termination_reason = RECOMPILER_REASON_SUCCESS; return 0; }");
        standalone.Source.Should().Contain(
            "if (steps > 0) { state->termination_reason = RECOMPILER_REASON_UNSUPPORTED_TRANSFER; return 0; }");
    }

    [SkippableFact]
    public void GenerateStandalone_WindowsPE32Plus_BuiltAndExecuted()
    {
        // Windows x64 (PE32+) verification for Issue #757.
        const string gcc64Name = "x86_64-w64-mingw32-gcc";
        // Resolve from PATH the same way the existing ELF E2E resolves gcc.
        var gcc64Path = File.Exists(Path.Combine("/usr/bin", gcc64Name))
            ? Path.Combine("/usr/bin", gcc64Name)
            : File.Exists(gcc64Name)
                ? gcc64Name
                : (File.Exists("/bin/" + gcc64Name) ? "/bin/" + gcc64Name : null);
        if (gcc64Path is null)
        {
            Skip.If(true, "x86_64-w64-mingw32-gcc not available on this host; x64 PE32+ build unverified.");
        }

        var instr = R3000aDecoder.Decode(0x3000048Du); // jal 0x80001234 from 0x80001000
        var program = MipsToIrLowerer.LowerProgram([(instr, 0x80001000u)]);
        var dispatch = RecompilerHostCodeGen.Generate(program);
        dispatch.Success.Should().BeTrue();

        var initialGpr = new uint[32];
        var initialMemory = Array.Empty<(uint Address, uint Value)>();
        var programImage = new byte[] { 0x30, 0x00, 0x04, 0x8D }; // the jal word, little-endian
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-standalone-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = RecompiledArtifactCodeGen.GenerateStandalone(
                dispatch,
                initialGpr,
                0u, 0u, 0x80001000u,
                1000u,
                initialMemory,
                0x80001000u,
                programImage);
            result.Success.Should().BeTrue();
            result.Source.Should().NotBeNull();

            var cPath = Path.Combine(tempDir, "test.c");
            File.WriteAllText(cPath, result.Source!);
            var exePath = Path.Combine(tempDir, "test.exe");

            var (compileExit, compileStdout, compileStderr) = RunCompiler(gcc64Path, cPath, exePath);
            compileExit.Should().Be(0, $"the self-contained generated artifact must compile to a Windows PE: {compileStdout}{compileStderr}");

            // Verify the artifact is really PE32+ x86-64: DOS e_lfanew -> PE signature,
            // Machine = 0x8664 (AMD64), optional-header magic = 0x020B (PE32+).
            var peBytes = File.ReadAllBytes(exePath);
            peBytes.Length.Should().BeGreaterThanOrEqualTo(0x1A0, "a valid PE must carry a full optional header");
            var eLfanew = BitConverter.ToInt32(peBytes.AsSpan(0x3C, 4).ToArray(), 0);
            peBytes[eLfanew..(eLfanew + 4)].Should().Equal((byte)'P', (byte)'E', 0, 0);
            var machine = BitConverter.ToUInt16(peBytes.AsSpan(eLfanew + 4).ToArray(), 0);
            machine.Should().Be(0x8664, "the artifact must be 64-bit (AMD64), not 32-bit");
            var optMagic = BitConverter.ToUInt16(peBytes.AsSpan(eLfanew + 0x18).ToArray(), 0);
            optMagic.Should().Be(0x020B, "the artifact must be PE32+ (64-bit optional header)");

            // Execution is possible only when wine can run 64-bit PE on this x86_64 host.
            var winePath = File.Exists("/usr/bin/wine")
                ? "/usr/bin/wine"
                : File.Exists("wine")
                    ? "wine"
                    : null;
            if (winePath is null)
            {
                Skip.If(true, "wine not available; PE32+ x86-64 build verified but execution NOT RUN.");
            }
            var (runExit, runOut, runErr) = RunProcessVerbose(winePath, exePath, 60000);
            runExit.Should().Be(5, "the PE exit code must equal the unsupported-transfer reason (5)");
            runOut.Should().Contain("termination=5");
            runOut.Should().Contain("RSNAPSHOT_BEGIN");
            runOut.Should().Contain("RSNAPSHOT_END");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
    #pragma warning restore AARC003
}
