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
    public void Validator_RejectsUndefinedOperationKind()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[] { new RecompilerIrOperation((RecompilerIrOperationKind)255) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerIrValidator.Validate(program);

        result.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidOperationShape);
    }

    [Fact]
    public void Validator_RejectsUndefinedTerminationReason()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(), new RecompilerIrExit((RecompilerIrTerminationReason)255)),
        });

        var result = RecompilerIrValidator.Validate(program);

        result.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(RecompilerIrDiagnosticCode.IllegalTermination);
    }

    [Fact]
    public void StateSnapshot_EnforcesZeroAndHasDeterministicSerialization()
    {
        var registers = Enumerable.Range(0, 32).Select(value => (uint)value).ToArray();
        registers[0] = 0xdeadbeef;
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
        Action wrongMemoryAccess = () => new RecompilerMemoryObservation(0, 0, 4, (RecompilerMemoryAccessKind)255);
        Action wrongTermination = () => new RecompilerStateSnapshot(new uint[32], 0, 0, 0, termination: (RecompilerIrTerminationReason)255);

        wrongGprCount.Should().Throw<ArgumentException>();
        wrongMemoryWidth.Should().Throw<ArgumentOutOfRangeException>();
        wrongMemoryAccess.Should().Throw<ArgumentOutOfRangeException>();
        wrongTermination.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnsupportedExit_IsRepresentedWithoutImplicitFallback()
    {
        var block = new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(), new RecompilerIrExit(RecompilerIrTerminationReason.UnsupportedInstruction));
        var program = new RecompilerIrProgram(new[] { block });

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnsupportedInstruction);
    }

    [Fact]
    public void Validator_RequiresPriorDefinitionsWithinEachBlock()
    {
        var valid = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 1),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });
        var invalid = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 2),
                new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 3, inputValueA: 999, inputValueB: 1000),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        RecompilerIrValidator.Validate(valid).IsValid.Should().BeTrue();
        var first = RecompilerIrValidator.Validate(invalid);
        var second = RecompilerIrValidator.Validate(invalid);
        first.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Equal(
            RecompilerIrDiagnosticCode.MissingOperand,
            RecompilerIrDiagnosticCode.MissingOperand,
            RecompilerIrDiagnosticCode.MissingOperand);
        first.Diagnostics.Should().Equal(second.Diagnostics);
    }

    [Fact]
    public void Validator_RejectsSelfReference()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 1, inputValueA: 1, inputValueB: 1) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        RecompilerIrValidator.Validate(program).Diagnostics.Select(diagnostic => diagnostic.Code)
            .Should().Equal(RecompilerIrDiagnosticCode.MissingOperand, RecompilerIrDiagnosticCode.MissingOperand);
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
