using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class OpenBiosFirmwareTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(OpenBiosFirmware.ImageSize - 1)]
    [InlineData(OpenBiosFirmware.ImageSize + 1)]
    public void Rejects_Incomplete_Or_Oversized_Rom(int length)
    {
        Assert.Throws<ArgumentException>(() => OpenBiosFirmware.FromBytes(new byte[length]));
    }

    [Fact]
    public void Converts_Complete_Image_And_Defensively_Copies()
    {
        var source = new byte[OpenBiosFirmware.ImageSize];
        source[0] = 0x34; source[1] = 0x12; source[2] = 0xAB; source[3] = 0xCD;
        var image = OpenBiosFirmware.FromBytes(source);
        source[0] = 0;
        Assert.Equal(0xCDAB1234u, image.Words[0]);
        Assert.Equal(OpenBiosFirmware.ImageSize / sizeof(uint), image.Words.Count);
    }

    [Fact]
    public void Boot_Backend_Executes_Rom_Words_On_Native_Interpreter_Without_Hle()
    {
        var source = new byte[OpenBiosFirmware.ImageSize];
        // addiu $v0, $zero, 42; no embedded Sony/OpenBIOS binary is needed for this fixture.
        source[0] = 0x2A; source[1] = 0x00; source[2] = 0x02; source[3] = 0x24;
        var backend = new OpenBiosBootBackend(OpenBiosFirmware.FromBytes(source));
        Assert.Equal(OpenBiosFirmware.ResetVector, backend.EntryPc);
        using var engine = backend.CreateEngine();
        var request = new TitleExecutionRequest(backend.EntryPc, new uint[32],
            0, 0, Array.Empty<RecompilerInitialMemoryItem>(), outerBudget: 1, segmentBudget: 1);
        var result = new ExecutionOrchestrator().Execute(engine, null, request);
        Assert.Equal(TitleExecutionState.BudgetExhausted, result.State);
        Assert.NotNull(result.FinalSnapshot);
        Assert.Equal(42u, result.FinalSnapshot!.Gpr[2]);
        Assert.Equal(OpenBiosFirmware.ResetVector + 4u, result.FinalSnapshot.PC);
    }
}
