using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public class RecompilerContractTests
{
    [Fact]
    public void ValidGprBlock_ValidatesAndSerializesDeterministically()
    {
        var first = CreateProgram();
        var second = CreateProgram();

        RecompilerIrValidator.Validate(first).IsValid.Should().BeTrue();
        RecompilerIrSerializer.Serialize(first).Should().Be(RecompilerIrSerializer.Serialize(second));
    }

    [Fact]
    public void Program_SortsBlocksByEntryPc()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(8, new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 12)),
            new RecompilerIrBlock(0, new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        program.Blocks.Select(block => block.EntryPc).Should().Equal(0u, 8u);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsZeroRegisterWriteAndMalformedOperation()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 0),
                new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 1),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerIrValidator.Validate(program);
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(RecompilerIrDiagnosticCode.ZeroRegisterWrite);
        result.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidOperationShape);
    }

    [Fact]
    public void StateSnapshot_EnforcesZeroAndHasDeterministicSerialization()
    {
        var registers = Enumerable.Range(0, 32).Select(value => (uint)value).ToArray();
        var snapshot = new RecompilerStateSnapshot(registers, 0x11223344, 0x55667788, 0x80010000);

        snapshot.Gpr[0].Should().Be(0u);
        RecompilerIrSerializer.Serialize(snapshot).Should().Be(RecompilerIrSerializer.Serialize(
            new RecompilerStateSnapshot(registers, 0x11223344, 0x55667788, 0x80010000)));
    }

    [Fact]
    public void StateSnapshot_RejectsWrongRegisterCountAndInvalidMemoryWidth()
    {
        Action wrongGprCount = () => new RecompilerStateSnapshot(new uint[31], 0, 0, 0);
        Action wrongMemoryWidth = () => new RecompilerMemoryObservation(0, 0, 3, RecompilerMemoryAccessKind.Read);

        wrongGprCount.Should().Throw<ArgumentException>();
        wrongMemoryWidth.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnsupportedExit_IsRepresentedWithoutImplicitFallback()
    {
        var block = new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(), new RecompilerIrExit(RecompilerIrTerminationReason.UnsupportedInstruction));
        var program = new RecompilerIrProgram(new[] { block });

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnsupportedInstruction);
    }

    private static RecompilerIrProgram CreateProgram() => new(new[]
    {
        new RecompilerIrBlock(0, new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
    });
}
