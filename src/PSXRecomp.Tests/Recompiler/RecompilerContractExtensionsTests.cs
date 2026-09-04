using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public class RecompilerContractExtensionsTests
{
    [Fact]
    public void Load32_ProducesResultFromAddressOperand()
    {
        var ops = new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        };
        var block = new RecompilerIrBlock(0, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));

        ValidateProgram(block);

        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Load32);
        block.Operations[1].InputValueA.Should().Be(0);
        block.Operations[1].ResultValueId.Should().Be(1);
    }

    [Fact]
    public void Store32_TakesAddressAndValueOperandsWithoutResult()
    {
        var ops = new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x11223344),
            new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1),
        };
        var block = new RecompilerIrBlock(0, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));

        ValidateProgram(block);

        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Store32);
        block.Operations[2].ResultValueId.Should().Be(-1);
        block.Operations[2].InputValueA.Should().Be(0);
        block.Operations[2].InputValueB.Should().Be(1);
    }

    [Theory]
    [InlineData(RecompilerIrOperationKind.Load8)]
    [InlineData(RecompilerIrOperationKind.Load16)]
    [InlineData(RecompilerIrOperationKind.Load32)]
    [InlineData(RecompilerIrOperationKind.Store8)]
    [InlineData(RecompilerIrOperationKind.Store16)]
    [InlineData(RecompilerIrOperationKind.Store32)]
    [InlineData(RecompilerIrOperationKind.CompareEqual)]
    [InlineData(RecompilerIrOperationKind.CompareNotEqual)]
    public void AllNewOperationKinds_AreDefinedAndProduceValidProgram(RecompilerIrOperationKind kind)
    {
        var isStore = kind is RecompilerIrOperationKind.Store8 or RecompilerIrOperationKind.Store16 or RecompilerIrOperationKind.Store32;
        var isCompare = kind is RecompilerIrOperationKind.CompareEqual or RecompilerIrOperationKind.CompareNotEqual;
        var hasResult = !isStore;
        var hasB = isStore || isCompare;

        var ops = new List<RecompilerIrOperation>
        {
            new(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x1),
        };
        ops.Add(new RecompilerIrOperation(kind, resultValueId: hasResult ? 2 : -1, inputValueA: 0, inputValueB: hasB ? 1 : -1));

        var block = new RecompilerIrBlock(0, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));
        ValidateProgram(block);
        Enum.IsDefined(kind).Should().BeTrue();
    }

    [Fact]
    public void CompareEqual_ProducesConditionValueConsumedByBranch()
    {
        var ops = new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
        };
        var exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: 0x80000010,
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x80000020, conditionValueId: 2));
        var block = new RecompilerIrBlock(0, ops, exit);

        var program = new RecompilerIrProgram(new[] { block });
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        block.Exit.Flow.Target.Should().Be(0x80000020u);
        block.Exit.Flow.ConditionValueId.Should().Be(2);
    }

    [Fact]
    public void JumpFlow_RequiresTargetAndNoNextPc()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target: 0x80002000)));
        var program = new RecompilerIrProgram(new[] { block });

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        block.Exit.NextPc.Should().BeNull();
        block.Exit.Flow!.Target.Should().Be(0x80002000u);
    }

    [Fact]
    public void BranchFlow_CarriesTakenTargetAndFallThroughNextPc()
    {
        var ops = new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
        };
        var block = new RecompilerIrBlock(
            0,
            ops,
            new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: 0x80000008,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x80000100, conditionValueId: 0)));

        ValidateProgram(block);
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        block.Exit.Flow.Target.Should().Be(0x80000100u);
        block.Exit.NextPc.Should().Be(0x80000008u);
    }

    [Fact]
    public void SequentialFlow_MatchesSuccessWithNextPc()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: 4,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Sequential)));

        ValidateProgram(block);
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Sequential);
    }

    [Fact]
    public void CallFlow_CarriesACalleeTargetAndAReturnAddress()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: 8,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call, target: 0x80000000)));

        ValidateProgram(block);
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Call);
    }

    [Fact]
    public void CallFlow_WithoutAReturnAddress_FailsFast()
    {
        // The exit's next PC is the address control resumes at after the callee
        // returns; without it a call would erase reachability of the code after it.
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call, target: 0x80000000)));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidFlow);
    }

    [Fact]
    public void CallFlow_WithoutATarget_FailsFast()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc: 8, flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call)));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidFlow);
    }

    [Fact]
    public void ReturnFlow_RemainsReservedAndFailsFast()
    {
        // A return target lives in a register, and RecompilerIrFlow.Target is a
        // static address, so Return stays closed until a stage can express one.
        var returnBlock = new RecompilerIrBlock(
            8,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, flow: new RecompilerIrFlow(RecompilerIrFlowKind.Return)));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { returnBlock }));

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.ReservedFlow);
    }

    [Fact]
    public void Function_WithBlocksAndMetadata_Validates()
    {
        var block = new RecompilerIrBlock(
            0,
            new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));
        var function = new RecompilerIrFunction(
            0,
            new[] { block },
            new[]
            {
                new RecompilerIrMetadataEntry("region", stringValue: "kseg0"),
                new RecompilerIrMetadataEntry("endian", stringValue: "little"),
            });
        var program = new RecompilerIrProgram(new[] { block }, new[] { function });

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        program.Functions.Should().ContainSingle();
        program.Functions[0].EntryPc.Should().Be(0u);
        program.Functions[0].Metadata.Should().HaveCount(2);
    }

    [Fact]
    public void Function_SerializesDeterministically()
    {
        var block = new RecompilerIrBlock(
            0,
            new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));
        var function = new RecompilerIrFunction(0, new[] { block }, new[] { new RecompilerIrMetadataEntry("region", stringValue: "kseg0") });
        var program = new RecompilerIrProgram(new[] { block }, new[] { function });

        var first = RecompilerIrSerializer.Serialize(program);
        var second = RecompilerIrSerializer.Serialize(program);
        first.Should().Be(second);
    }

    [Fact]
    public void MemoryOp_InvalidShapes_FailFast()
    {
        RecompilerIrProgram Program(params RecompilerIrOperation[] ops) =>
            new(new[] { new RecompilerIrBlock(0, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)) });

        var loadWithoutResult = Program(new RecompilerIrOperation(RecompilerIrOperationKind.Load32, inputValueA: 0));
        var loadWithTwoInputs = Program(new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0, inputValueB: 1));
        var storeWithResult = Program(new RecompilerIrOperation(RecompilerIrOperationKind.Store32, resultValueId: 1, inputValueA: 0, inputValueB: 1));

        RecompilerIrValidator.Validate(loadWithoutResult).Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidOperationShape);
        RecompilerIrValidator.Validate(loadWithTwoInputs).Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidOperationShape);
        RecompilerIrValidator.Validate(storeWithResult).Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidOperationShape);
    }

    [Fact]
    public void BranchFlow_WithUndefinedCondition_FailsFast()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: 4,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 8, conditionValueId: 999)));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.MissingOperand);
    }

    [Fact]
    public void JumpFlow_WithNextPc_FailsFast()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc: 4, flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target: 8)));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidFlow);
    }

    [Fact]
    public void Function_WithUnknownEntryPc_FailsFast()
    {
        var block = new RecompilerIrBlock(
            8,
            new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 12));
        var function = new RecompilerIrFunction(0x80001000, new[] { block });
        var program = new RecompilerIrProgram(new[] { block }, new[] { function });

        var result = RecompilerIrValidator.Validate(program);
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidFunction);
    }

    [Fact]
    public void Function_WithDuplicateEntryPc_FailsFast()
    {
        var block = new RecompilerIrBlock(
            0,
            new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));
        var function = new RecompilerIrFunction(0, new[] { block });
        var program = new RecompilerIrProgram(new[] { block }, new[] { function, function });

        var result = RecompilerIrValidator.Validate(program);
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.DuplicateFunction);
    }

    [Fact]
    public void MetadataEntry_InvalidValues_FailFast()
    {
        Action emptyKey = () => new RecompilerIrMetadataEntry(" ", stringValue: "x");
        Action bothValues = () => new RecompilerIrMetadataEntry("k", uintValue: 1, stringValue: "x");
        Action noValue = () => new RecompilerIrMetadataEntry("k");

        emptyKey.Should().Throw<ArgumentException>();
        bothValues.Should().Throw<ArgumentException>();
        noValue.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FlowKind_InvalidValue_FailsFast()
    {
        Action invalidFlow = () => new RecompilerIrFlow((RecompilerIrFlowKind)255);
        invalidFlow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Flow_OnNonSuccessExit_FailsFast()
    {
        var block = new RecompilerIrBlock(
            0,
            Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.UnsupportedInstruction, flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target: 8)));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidFlow);
    }

    [Fact]
    public void Program_WithoutFunctions_RemainsPhase2ACompatible()
    {
        var block = new RecompilerIrBlock(
            0,
            new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4));
        var program = new RecompilerIrProgram(new[] { block });

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        program.Functions.Should().BeEmpty();
        RecompilerIrSerializer.Serialize(program).Should().NotBeNullOrEmpty();
    }

    private static void ValidateProgram(RecompilerIrBlock block)
    {
        var program = new RecompilerIrProgram(new[] { block });
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }
}
