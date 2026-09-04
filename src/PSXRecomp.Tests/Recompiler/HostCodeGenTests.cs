using System.Diagnostics;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public class HostCodeGenTests
{
    [Fact]
    public void Generation_Constant_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 42),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v0 = (42u);");
        result.Source.Should().Contain("state->gpr[8] = v0;");
    }

    [Fact]
    public void Generation_ReadGpr_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 5),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 10),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v0 = state->gpr[5];");
        result.Source.Should().Contain("state->gpr[10] = v0;");
    }

    [Fact]
    public void Generation_Add_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 2),
            new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 + v1;");
    }

    [Fact]
    public void Generation_Sub_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 10),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 3),
            new RecompilerIrOperation(RecompilerIrOperationKind.Subtract, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 - v1;");
    }

    [Fact]
    public void Generation_And_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xFF),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.And, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 & v1;");
    }

    [Fact]
    public void Generation_Or_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xF0),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.Or, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 | v1;");
    }

    [Fact]
    public void Generation_Xor_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xFF),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.Xor, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 ^ v1;");
    }

    [Fact]
    public void Generation_Nor_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xF0),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.Nor, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = ~(v0 | v1);");
    }

    [Fact]
    public void Generation_LUI_Equivalent_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1234u << 16),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v0 = (305397760u);");
    }

    [Fact]
    public void Generation_SLL_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftLeftLogical, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v1 = (uint32_t)v0 << (4u & 31u);");
    }

    [Fact]
    public void Generation_SRL_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 256),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightLogical, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v1 = (uint32_t)v0 >> (4u & 31u);");
    }

    [Fact]
    public void Generation_SRA_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightArithmetic, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v1 = recompiler_sra32(v0, 4u);");
    }

    // --- Phase 3B: Memory operations ---

    [Theory]
    [InlineData(RecompilerIrOperationKind.Load8)]
    [InlineData(RecompilerIrOperationKind.Load16)]
    [InlineData(RecompilerIrOperationKind.Load32)]
    public void Generation_Load_Op_Renders_Memory_Helper_Call(RecompilerIrOperationKind kind)
    {
        var helperName = kind switch
        {
            RecompilerIrOperationKind.Load8 => "recompiler_read_mem8",
            RecompilerIrOperationKind.Load16 => "recompiler_read_mem16",
            RecompilerIrOperationKind.Load32 => "recompiler_read_mem32",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1000),
            new RecompilerIrOperation(kind, resultValueId: 1, inputValueA: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain($"{helperName}(state->core, v0)");
        result.Source.Should().Contain("uint32_t v1 =");
        result.Source.Should().Contain("state->gpr[8] = v1;");
    }

    [Theory]
    [InlineData(RecompilerIrOperationKind.Store8)]
    [InlineData(RecompilerIrOperationKind.Store16)]
    [InlineData(RecompilerIrOperationKind.Store32)]
    public void Generation_Store_Op_Renders_Memory_Helper_Call(RecompilerIrOperationKind kind)
    {
        var helperName = kind switch
        {
            RecompilerIrOperationKind.Store8 => "recompiler_write_mem8",
            RecompilerIrOperationKind.Store16 => "recompiler_write_mem16",
            RecompilerIrOperationKind.Store32 => "recompiler_write_mem32",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x2000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 8),
            new RecompilerIrOperation(kind, inputValueA: 0, inputValueB: 1),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain($"{helperName}(state->core, v0,");
    }

    [Fact]
    public void Generation_Narrow_Store_Does_Not_Affect_Adjacent_Bytes()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.Store8, inputValueA: 0, inputValueB: 1),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        // Store8 must cast to uint8_t, not write 2 or 4 bytes.
        result.Source.Should().Contain("recompiler_write_mem8(state->core, v0, (uint8_t)v1)");
        result.Source.Should().NotContain("recompiler_write_mem16(state->core");
        result.Source.Should().NotContain("recompiler_write_mem32(state->core");
    }

    [Fact]
    public void Generation_Memory_Helper_Declarations_Are_Present()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1000),
            new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("extern uint8_t  recompiler_read_mem8");
        result.Source.Should().Contain("extern uint16_t recompiler_read_mem16");
        result.Source.Should().Contain("extern uint32_t recompiler_read_mem32");
        result.Source.Should().Contain("extern void     recompiler_write_mem8");
        result.Source.Should().Contain("extern void     recompiler_write_mem16");
        result.Source.Should().Contain("extern void     recompiler_write_mem32");
    }

    [Fact]
    public void Generation_Core_Field_Is_In_State_Struct()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("void* core;");
    }

    // --- Phase 3C: Compare operations ---

    [Fact]
    public void Generation_CompareEqual_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 10),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (v0 == v1) ? 1u : 0u;");
    }

    [Fact]
    public void Generation_CompareNotEqual_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareNotEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 10),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (v0 != v1) ? 1u : 0u;");
    }

    // --- Phase 3C: Control-flow operations ---

    [Fact]
    public void Generation_Branch_Taken_Sets_Target()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0x80000000u,
                new[]
                {
                    new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
                    new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
                    new RecompilerIrOperation(RecompilerIrOperationKind.CompareEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
                },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 0x80000008u,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x80000010u, conditionValueId: 2))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("if (v2 != 0u) { state->next_pc = (2147483664u); } else { state->next_pc = (2147483656u); }");
    }

    [Fact]
    public void Generation_Branch_NotTaken_Sets_Fallthrough()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0x80000000u,
                new[]
                {
                    new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
                    new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
                    new RecompilerIrOperation(RecompilerIrOperationKind.CompareNotEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
                },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 0x80000008u,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x80000010u, conditionValueId: 2))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        // The condition is CompareNotEqual, so when gpr[8] == gpr[9], v2 == 0,
        // and the else branch (fallthrough) is taken.
        result.Source.Should().Contain("if (v2 != 0u)");
        result.Source.Should().Contain("state->next_pc = (2147483656u);"); // fallthrough 0x80000008
        result.Source.Should().Contain("state->next_pc = (2147483664u);"); // taken 0x80000010
    }

    [Fact]
    public void Generation_Jump_Sets_Target()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0x80000000u,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target: 0x80000020u))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->next_pc = (2147483680u);");
        result.Source.Should().Contain("state->termination_reason = 0; return 0;");
    }

    [Fact]
    public void Generation_Call_Sets_Callee_Target()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0x80000000u,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 0x80000008u,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call, target: 0x80000100u))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        // Call sets next_pc to the callee target (0x80000100), not the return address.
        result.Source.Should().Contain("state->next_pc = (2147483904u);");
        result.Source.Should().Contain("state->termination_reason = 0; return 0;");
    }

    [Fact]
    public void Generation_Sequential_Flow_Sets_NextPc()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 4,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Sequential))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->next_pc = 4; state->termination_reason = 0; return 0;");
    }

    [Fact]
    public void Generation_Rejects_Return_Flow()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Return))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("IR_VALIDATION_FAILED");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Generation_Rejects_Unknown_Flow_Kind()
    {
        // The RecompilerIrFlow constructor rejects undefined enum values,
        // preventing unknown flow kinds from reaching codegen.
        var act = () => new RecompilerIrFlow((RecompilerIrFlowKind)255);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Generation_Rejects_UnresolvedIndirectFlow()
    {
        // UnresolvedIndirectFlow is a termination reason, not a flow kind.
        // The block should still generate (it's a non-Success termination).
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(RecompilerIrTerminationReason.UnresolvedIndirectFlow)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        var reason = (byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow;
        result.Source.Should().Contain($"state->termination_reason = {reason}; return (int32_t){reason}u;");
    }

    // --- Existing Phase 3A tests ---

    [Fact]
    public void Determinism_Same_IR_Produces_ByteIdentical_Source()
    {
        var program = CreateRepresentativeGprProgram();
        var first = RecompilerHostCodeGen.Generate(program);
        var second = RecompilerHostCodeGen.Generate(program);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        first.Source.Should().Be(second.Source);
    }

    [Fact]
    public void ABI_Generated_Function_Signature_Is_Stable()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("static int32_t recompiler_block_0x00000000(RecompilerState* state)");
    }

    [Fact]
    public void FixedWidth_Source_Uses_Uint32T_And_No_Long()
    {
        var program = CreateRepresentativeGprProgram();
        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        result.Source.Should().Contain("uint32_t");
        result.Source.Should().NotContain(" long ");
        result.Source.Should().NotContain(" long\t");
        result.Source.Should().Contain("recompiler_sra32");
    }

    [Fact]
    public void UB_AddU_SubU_Uses_Unsigned_Wrap_Forms()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 100),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 50),
            new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Subtract, resultValueId: 3, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 3, register: 9),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 + v1;");
        result.Source.Should().Contain("uint32_t v3 = (uint32_t)v0 - v1;");
    }

    [Fact]
    public void UB_SRA_Uses_Defined_Helper_Not_Bare_RShift()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightArithmetic, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        var sraLine = result.Source!.Split('\n').Single(line => line.Contains("recompiler_sra32(v0, 4u)"));
        sraLine.Should().NotContain("(int32_t)");
        result.Source.Should().Contain("recompiler_sra32(v0, 4u)");
    }

    [Fact]
    public void Unsupported_Unknown_Operation_Kind_Returns_Failure()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation((RecompilerIrOperationKind)255),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().NotBeNull();
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Unsupported_Validator_Invalid_IR_Is_Refused()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 999, inputValueB: 1000),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("IR_VALIDATION_FAILED");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Unsupported_Zero_Register_Write_Is_Refused()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 0),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("IR_VALIDATION_FAILED");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Compile_Representative_Gpr_Program_Passes_Host_Compiler()
    {
        var program = CreateRepresentativeGprProgram();
        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var exitCode = CompileWithHostCompiler(result.Source!);
        exitCode.Should().Be(0, "generated source must compile with the host compiler");
    }

    [Fact]
    public void Runtime_AddU_SubU_Wrap_SRL_SRA_EndToEnd()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xFFFFFFFF),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Subtract, resultValueId: 3, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightLogical, resultValueId: 4, inputValueA: 0, shiftAmount: 28),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightArithmetic, resultValueId: 5, inputValueA: 0, shiftAmount: 28),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 3, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 4, register: 10),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 5, register: 11),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    int ok = 1;
    if (state.gpr[8] != 0u) { ok = 0; }
    if (state.gpr[9] != 0xFFFFFFFEu) { ok = 0; }
    if (state.gpr[10] != 0x0Fu) { ok = 0; }
    if (state.gpr[11] != 0xFFFFFFFFu) { ok = 0; }
    return ok ? 0 : 1;
}");
        testResult.Should().Be(0, "runtime test must pass (ADD/SUB wrap, SRL, SRA correctness)");
    }

    [Fact]
    public void Runtime_CompareEqual_EndToEnd()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 42),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 42),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    return (state.gpr[8] == 1u) ? 0 : 1;
}");
        testResult.Should().Be(0, "CompareEqual must produce 1 when inputs are equal");
    }

    [Fact]
    public void Runtime_CompareNotEqual_EndToEnd()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 2),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareNotEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    return (state.gpr[8] == 1u) ? 0 : 1;
}");
        testResult.Should().Be(0, "CompareNotEqual must produce 1 when inputs differ");
    }

    [Fact]
    public void Runtime_Load32_EndToEnd()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000u),
            new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
#include <string.h>
int main() {
    /* Write a known value to test_ram[0] via the memory helpers. */
    extern void recompiler_write_mem32(void*, uint32_t, uint32_t);
    recompiler_write_mem32(0, 0x80000000u, 0xDEADBEEFu);
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    return (state.gpr[8] == 0xDEADBEEFu) ? 0 : 1;
}");
        testResult.Should().Be(0, "Load32 must read the 32-bit value written by the memory helper");
    }

    [Fact]
    public void Runtime_Store32_EndToEnd()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000u),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0xCAFEBABEu),
            new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
#include <string.h>
int main() {
    extern uint32_t recompiler_read_mem32(void*, uint32_t);
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    uint32_t readback = recompiler_read_mem32(0, 0x80000000u);
    return (readback == 0xCAFEBABEu) ? 0 : 1;
}");
        testResult.Should().Be(0, "Store32 must write the value to guest memory");
    }

    [Fact]
    public void Runtime_Store8_Narrow_Does_Not_Affect_Adjacent_Bytes()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, new[]
            {
                // First write a known 32-bit pattern.
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000u),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0xFFFFFFFF),
                new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1),
                // Then overwrite only the low byte.
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 2, immediate: 0x80000000u),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 3, immediate: 0xAA),
                new RecompilerIrOperation(RecompilerIrOperationKind.Store8, inputValueA: 2, inputValueB: 3),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000004u)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
#include <string.h>
int main() {
    extern uint32_t recompiler_read_mem32(void*, uint32_t);
    RecompilerState state = {0};
    state.pc = 0x80000000u;
    recompiler_dispatch(&state, 10);
    uint32_t readback = recompiler_read_mem32(0, 0x80000000u);
    /* Low byte overwritten to 0xAA, upper 3 bytes remain 0xFF. */
    return (readback == 0xFFFFFFAAu) ? 0 : 1;
}");
        testResult.Should().Be(0, "Store8 must only affect the addressed byte");
    }

    [Fact]
    public void Runtime_Branch_Taken_EndToEnd()
    {
        // Two blocks: block at 0x0 writes 1 to gpr[8], block at 0x10 writes 2 to gpr[9].
        // Block at 0x0 has a branch: if gpr[8] == gpr[8] (always true), go to 0x10.
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
                new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 8),
                new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 2, register: 8),
                new RecompilerIrOperation(RecompilerIrOperationKind.CompareEqual, resultValueId: 3, inputValueA: 1, inputValueB: 2),
            }, new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: 0x80000008u,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x80000010u, conditionValueId: 3))),
            new RecompilerIrBlock(0x80000010u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 2),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 9),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000018u)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    state.pc = 0x80000000u;
    recompiler_dispatch(&state, 10);
    /* Branch is taken, so gpr[9] should be 2 (written at 0x80000010). */
    return (state.gpr[9] == 2u) ? 0 : 1;
}");
        testResult.Should().Be(0, "Branch taken must transfer to the target block");
    }

    [Fact]
    public void Runtime_Jump_EndToEnd()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 5),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            }, new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target: 0x80000010u))),
            new RecompilerIrBlock(0x80000010u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 7),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 9),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000018u)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    state.pc = 0x80000000u;
    recompiler_dispatch(&state, 10);
    return (state.gpr[9] == 7u) ? 0 : 1;
}");
        testResult.Should().Be(0, "Jump must transfer to the target block");
    }

    [Fact]
    public void Runtime_Call_EndToEnd()
    {
        // JAL-like: block at 0x0 links (writes $ra = return address), then calls to 0x10.
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, new[]
            {
                // Link: $ra = return address (0x80000008 = pc + 8).
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000008u),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 31),
            }, new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                nextPc: 0x80000008u,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call, target: 0x80000010u))),
            new RecompilerIrBlock(0x80000010u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 42),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000018u)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    state.pc = 0x80000000u;
    recompiler_dispatch(&state, 10);
    /* gpr[8] should be 42 (written at the callee 0x80000010). */
    return (state.gpr[8] == 42u) ? 0 : 1;
}");
        testResult.Should().Be(0, "Call must transfer to the callee block");
    }

    [Fact]
    public void Exit_Success_Sets_NextPc_And_TerminationReason()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->next_pc = 4; state->termination_reason = 0; return 0;");
    }

    [Fact]
    public void Exit_NonSuccess_Sets_TerminationReason_And_Returns_Code()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(RecompilerIrTerminationReason.UnsupportedMemory)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->termination_reason = 3; return (int32_t)3u;");
    }

    [Fact]
    public void Unsupported_Empty_Program_Is_Refused()
    {
        var result = RecompilerHostCodeGen.Generate(new RecompilerIrProgram(Array.Empty<RecompilerIrBlock>()));
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("UNSUPPORTED_EMPTY_PROGRAM");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void MultiBlock_Program_Is_Generated_With_Budgeted_Dispatcher()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 5),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000004u)),
            new RecompilerIrBlock(0x80000004u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 7),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 9),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000008u)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("int32_t recompiler_dispatch(RecompilerState* state, uint32_t budget)");
        result.Source.Should().Contain("recompiler_block_0x80000000(state)");
        result.Source.Should().Contain("recompiler_block_0x80000004(state)");
        result.Source.Should().Contain("RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED");
    }

    [Fact]
    public void Unsupported_Duplicate_Result_Value_Id_Is_Refused()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 2),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("DUPLICATE_RESULT_VALUE_ID");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void RunHostProcess_TimesOut_When_Process_Exceeds_Budget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        #pragma warning disable PSXR005
        Action act = () => RunHostProcess("sleep", "30", 500);
        #pragma warning restore PSXR005
        act.Should().Throw<TimeoutException>();
    }

    #pragma warning disable PSXR005
    private static int CompileWithHostCompiler(string source)
    {
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "test.c");
            var outputPath = Path.Combine(tempDir, "test.o");
            File.WriteAllText(sourcePath, source);

            var (exitCode, _, stderr) = RunHostProcess(
                "gcc", $"-std=c11 -O0 -Wall -Wextra -c {sourcePath} -o {outputPath}", 30000);

            if (exitCode != 0)
                throw new InvalidOperationException($"Host compilation failed (exit {exitCode}):\n{stderr}");

            return exitCode;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
#pragma warning restore PSXR005

#pragma warning disable PSXR005
    private static int CompileAndRun(string generatedSource, string mainSource)
    {
        var tempDir = CreateTempDir();
        try
        {
            var combinedPath = Path.Combine(tempDir, "combined.c");
            var outputPath = Path.Combine(tempDir, "test_bin");

            File.WriteAllText(combinedPath, generatedSource + "\n" + MemoryHelperStubs + "\n" + mainSource);

            var (compileExit, _, compileErr) = RunHostProcess(
                "gcc", $"-std=c11 -O0 -Wall -Wextra {combinedPath} -o {outputPath}", 30000);

            if (compileExit != 0)
                throw new InvalidOperationException($"Compilation failed (exit {compileExit}):\n{compileErr}");

            var (runExit, _, _) = RunHostProcess(outputPath, "", 10000);
            return runExit;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunHostProcess(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);

            var timedOutOut = stdoutTask.Result;
            var timedOutErr = stderrTask.Result;
            throw new TimeoutException(
                $"Host process timed out after {timeoutMs} ms: {fileName} {arguments}\nstdout:\n{timedOutOut}\nstderr:\n{timedOutErr}");
        }

        process.WaitForExit();
        var exitCode = process.ExitCode;
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        return (exitCode, stdout, stderr);
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
#pragma warning restore PSXR005

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-host-codegen", Path.GetRandomFileName());
#pragma warning disable PSXR005
        Directory.CreateDirectory(tempDir);
#pragma warning restore PSXR005
        return tempDir;
    }

    private const string MemoryHelperStubs = @"
#define PSX_TEST_RAM_SIZE (2u * 1024u * 1024u)
static uint8_t test_ram[PSX_TEST_RAM_SIZE];
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
";

    private static RecompilerIrProgram CreateSingleBlockProgram(RecompilerIrOperation[] operations)
    {
        return new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, operations, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });
    }

    private static RecompilerIrProgram CreateRepresentativeGprProgram()
    {
        return new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 2),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 9),
                new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 2, register: 8),
                new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 3, register: 9),
                new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 4, inputValueA: 2, inputValueB: 3),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 4, register: 10),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });
    }
}
