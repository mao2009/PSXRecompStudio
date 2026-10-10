using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Issue #732: the dispatcher of RAM-placed code — every pre-generated version of an entry gets its own block function,
// and the dispatch case selects the version whose words guest memory holds, or takes the unknown-PC boundary.
public sealed class LoadedCodeDispatchCodegenTests
{
    private const uint Rom = 0xBFC00000u, Dest = 0x80010000u;
    private const byte T0 = 8, Ra = 31;
    private static readonly IReadOnlySet<uint> None = new HashSet<uint>();

    private static RecompilerIrProgram RomProgram() =>
        ReachableProgramBuilder.Build(Rom, [MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop], Rom);

    private static GuardedImageProgram Image(ushort imm) =>
        ReachableProgramBuilder.BuildLoadedImage(Dest, [MipsEncoding.I(0x0D, T0, 0, imm), MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop], [Dest], None);

    [Fact]
    public void WithoutLoadedCode_TheSourceIsUnchanged()
    {
        var plain = RecompilerHostCodeGen.Generate(RomProgram()).Source!;

        RecompilerHostCodeGen.Generate(RomProgram(), LoadedCodeTable.Empty).Source.Should().Be(plain);
        plain.Should().NotContain("recompiler_code_current");
    }

    [Fact]
    public void TwoVersionsAtOneAddress_GetTwoFunctions_AndAVersionSelectingCase()
    {
        var source = RecompilerHostCodeGen.Generate(RomProgram(), new LoadedCodeTable([Image(1), Image(2)])).Source!;

        source.Should().Contain("static int32_t recompiler_block_0x80010000_v0(").And.Contain("static int32_t recompiler_block_0x80010000_v1(");
        source.Should().Contain($"static const uint32_t code0[] = {{ 0x{MipsEncoding.I(0x0D, T0, 0, 1):X8}u }};");
        source.Should().Contain($"static const uint32_t code1[] = {{ 0x{MipsEncoding.I(0x0D, T0, 0, 2):X8}u }};");
        source.Should().Contain("goto recompiler_unknown_pc;").And.Contain("default: recompiler_unknown_pc: {");
    }

    [Fact]
    public void LoadedAlignedMemoryFaults_PreserveMetadataAndEnableGuestExceptionEntry()
    {
        var image = ReachableProgramBuilder.BuildLoadedImage(Dest,
            [MipsEncoding.I(0x23, T0, T0, 1), MipsEncoding.Nop, MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop], [Dest], None);
        image.Blocks.SelectMany(static b => b.Block.MemoryFaultSites).Should().ContainSingle();
        var source = RecompilerHostCodeGen.Generate(RomProgram(), new LoadedCodeTable([image])).Source!;
        source.Should().Contain("state->exception_code = 4u;");
        source.Should().Contain("retired = state->partial_retired;");
    }

    [Fact]
    public void LoadedCodeAtAStaticBlockEntry_IsRejected()
    {
        var clash = ReachableProgramBuilder.BuildLoadedImage(Rom, [MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop], [Rom], None);

        RecompilerHostCodeGen.Generate(RomProgram(), new LoadedCodeTable([clash])).DiagnosticCode.Should().Be("INVALID_LOADED_CODE");
    }
}
