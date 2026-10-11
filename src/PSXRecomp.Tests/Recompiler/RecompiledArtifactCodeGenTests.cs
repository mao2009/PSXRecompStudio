using System.Globalization;
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
}
