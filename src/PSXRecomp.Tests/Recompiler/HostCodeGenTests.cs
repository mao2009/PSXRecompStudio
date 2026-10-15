using System.Diagnostics;
using System.Text.Json;
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

    // --- Issue #362: the host control-transfer hook ---

    [Fact]
    public void Generation_HostTransfer_Hook_Is_In_State_Struct()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("typedef int32_t (*recompiler_host_transfer_fn)(struct RecompilerState*);");
        result.Source.Should().Contain("recompiler_host_transfer_fn host_transfer;");
    }

    [Fact]
    public void Generation_Dispatch_Offers_An_Unknown_Pc_To_The_Host_Before_Giving_Up()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        // A null hook falls straight through to the pre-existing end-of-program /
        // unsupported-entry behavior. A non-null hook is guarded by the strict
        // dispatch budget (Issue #375) before it can be invoked, so it never
        // mutates guest state past the limit; only once that guard has passed is
        // the hook actually consulted.
        result.Source.Should().Contain("if (state->host_transfer == 0) {");
        result.Source.Should().Contain("int32_t hosted = state->host_transfer(state);");
        result.Source.Should().Contain("if (hosted != 0) {");

        // The backend carries no BIOS knowledge: it never names a vector address,
        // a function number, or a Runtime service (ADR-014).
        result.Source.Should().NotContain("bios");
        result.Source.Should().NotContain("BIOS");
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

    [Fact]
    public void Generation_CompareLessThanSigned_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 10),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = ((v0 ^ 0x80000000u) < (v1 ^ 0x80000000u)) ? 1u : 0u;");
    }

    [Fact]
    public void Generation_CompareLessThanUnsigned_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanUnsigned, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 10),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (v0 < v1) ? 1u : 0u;");
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
    public void Runtime_CompareLessThanSigned_EndToEnd()
    {
        // Portable ordering must hold across the full 32-bit signed range,
        // including the out-of-range-for-int32_t bit patterns (0x80000000,
        // 0x7FFFFFFF) a naive (int32_t) cast would leave implementation-defined.
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000), // INT32_MIN
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 2, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 3, immediate: 0xFFFFFFFF), // -1
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 4, immediate: 0x7FFFFFFF), // INT32_MAX
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 5, immediate: 5),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 6, inputValueA: 0, inputValueB: 1), // INT32_MIN < 0
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 7, inputValueA: 1, inputValueB: 0), // 0 < INT32_MIN
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 8, inputValueA: 3, inputValueB: 2), // -1 < 1
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 9, inputValueA: 4, inputValueB: 0), // INT32_MAX < INT32_MIN
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 10, inputValueA: 5, inputValueB: 5), // equal
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 6, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 7, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 8, register: 10),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 9, register: 11),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 10, register: 12),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    int ok = 1;
    if (state.gpr[8] != 1u) { ok = 0; } /* INT32_MIN < 0 -> true */
    if (state.gpr[9] != 0u) { ok = 0; } /* 0 < INT32_MIN -> false */
    if (state.gpr[10] != 1u) { ok = 0; } /* -1 < 1 -> true */
    if (state.gpr[11] != 0u) { ok = 0; } /* INT32_MAX < INT32_MIN -> false */
    if (state.gpr[12] != 0u) { ok = 0; } /* equal -> false */
    return ok ? 0 : 1;
}");
        testResult.Should().Be(0, "CompareLessThanSigned must compare as 32-bit two's-complement, with no implementation-defined conversion");
    }

    [Fact]
    public void Runtime_CompareLessThanUnsigned_EndToEnd()
    {
        // The same bit patterns order differently unsigned: 0x80000000 is huge,
        // so neither 0x80000000 < 0 nor 0x80000000 < 1 holds.
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanUnsigned, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 3, immediate: 0x10000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanUnsigned, resultValueId: 4, inputValueA: 3, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 4, register: 9),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    int ok = 1;
    if (state.gpr[8] != 0u) { ok = 0; } /* 0x80000000 < 0 is false unsigned */
    if (state.gpr[9] != 0u) { ok = 0; } /* 0x10000000 < 0 is false unsigned */
    return ok ? 0 : 1;
}");
        testResult.Should().Be(0, "CompareLessThanUnsigned must compare as raw unsigned 32-bit values");
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
    public void Exit_Exception_CarryingResolution_EmitsExceptionFields_BeforeTermination()
    {
        // Issue #481: a BREAK exit carries its statically-resolvable exception
        // resolution; the emitted exit must reproduce Excode/EPC/BD in the host
        // state so the runtime sees the faulting instruction without re-deriving
        // it from the parked dispatch PC.
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Exception,
                    exception: new RecompilerExceptionState(isRaised: true, code: 0x09, faultPc: 0x80000000u, inDelaySlot: false))),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->exception_raised = 1u;");
        result.Source.Should().Contain("state->exception_code = 9;");
        // The immediate formatter renders 0x80000000 as decimal, like every other
        // large PC/address constant the code generator emits.
        result.Source.Should().Contain("state->exception_fault_pc = (2147483648u);");
        result.Source.Should().Contain("state->exception_in_delay_slot = 0u;");
        result.Source.Should().Contain("state->termination_reason = 6; return (int32_t)6u;");
    }

    [Fact]
    public void Exit_Exception_WithoutResolution_EmitsTerminationOnly()
    {
        // The generic trapping exit (AddSigned overflow and friends) resolves at
        // runtime, not at lowering time, so it writes no exception fields and
        // keeps the historical shape: termination only. The runtime still derives
        // the fault from the parked dispatch PC, unchanged by Issue #481.
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(RecompilerIrTerminationReason.Exception)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->termination_reason = 6; return (int32_t)6u;");
        result.Source.Should().NotContain("state->exception_raised");
        result.Source.Should().NotContain("state->exception_code");
        result.Source.Should().NotContain("state->exception_fault_pc");
        result.Source.Should().NotContain("state->exception_in_delay_slot");
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

    // Issue #732: firmware mode. A SYSCALL/BREAK is delivered to the guest exception vector by the generated
    // dispatch itself, with the interpreter's EPC / CAUSE (Excode, BD) / SR-stack / BEV-vector resolution.
    [Theory]
    [InlineData("syscall", 0x00000001u)]
    [InlineData("break", 0x00000001u)]
    [InlineData("syscall-in-delay-slot", 0x00000005u)]
    [InlineData("syscall-bev", 0x00400001u)]
    public void FirmwareMode_TrapEntersTheGuestVector_ExactlyLikeTheInterpreter(string shape, uint sr)
    {
        const uint Entry = 0x80000000u;
        var words = shape switch
        {
            "break" => new[] { MipsEncoding.Break() },
            "syscall-in-delay-slot" => new[] { MipsEncoding.Jump(0x80000040u), MipsEncoding.Syscall() },
            _ => new[] { MipsEncoding.Syscall() },
        };

        // The oracle: the native interpreter takes the same exception.
        using var core = new PSXRecomp.Core.PSXCoreWrapper();
        core.Reset();
        for (var i = 0; i < words.Length; i++) core.WriteMemory32((uint)(i * 4), words[i]);
        core.SetCop0(RecompilerCop0.Status, sr);
        core.Pc = Entry;
        for (var i = 0; i < words.Length; i++) core.Step();
        core.ExceptionRaised.Should().BeTrue();
        var (pc, epc, cause, newSr) = (core.Pc, core.GetCop0(RecompilerCop0.Epc), core.GetCop0(RecompilerCop0.Cause), core.GetCop0(RecompilerCop0.Status));

        var program = MipsToIrLowerer.LowerProgram(words
            .Select((word, index) => (PSXRecomp.Core.Cpu.R3000aDecoder.Decode(word), Entry + (uint)(index * 4)))
            .ToArray());
        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var exit = CompileAndRun(result.Source!, $@"
int main() {{
    RecompilerState state = {{0}};
    state.pc = {Entry}u;
    state.cop0_sr = {sr}u;
    state.guest_exceptions = 1u;
    if (recompiler_dispatch(&state, 4u) != 0) return 10;
    if (state.pc != {pc}u) return 11;
    if (state.cop0_epc != {epc}u) return 12;
    if (state.cop0_cause != {cause}u) return 13;
    if (state.cop0_sr != {newSr}u) return 14;
    if (state.exception_raised != 0u) return 15;
    return 0;
}}");
        exit.Should().Be(0, $"the generated host must enter the vector like the interpreter (pc 0x{pc:X8}, epc 0x{epc:X8}, cause 0x{cause:X8}, sr 0x{newSr:X8})");
    }

    [Fact]
    public void HleMode_TrapStillStopsTheRun_WithItsExceptionState()
    {
        var program = MipsToIrLowerer.LowerProgram(new[] { (PSXRecomp.Core.Cpu.R3000aDecoder.Decode(MipsEncoding.Syscall()), 0x80000000u) });
        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var exit = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.pc = 0x80000000u;
    state.cop0_sr = 1u;
    if (recompiler_dispatch(&state, 4u) != RECOMPILER_REASON_EXCEPTION) return 10;
    if (state.exception_raised != 1u || state.exception_code != 8u) return 11;
    if (state.cop0_sr != 1u || state.cop0_epc != 0u) return 12;
    return 0;
}");
        exit.Should().Be(0, "without firmware mode a trap keeps the pre-existing HLE stop");
    }

    [SkippableFact]
    public void RunHostProcess_TimesOut_When_Process_Exceeds_Budget()
    {
        var sleep = TestToolchain.LongSleep(30);
        if (sleep is null)
        {
            Skip.If(true, "no long-running utility (ping on Windows, sleep elsewhere) found; timeout/tree-kill verification skipped");
        }

        #pragma warning disable AARC003
        Action act = () => RunHostProcess(sleep!.Value.FileName, sleep.Value.Arguments, 500);
        #pragma warning restore AARC003
        act.Should().Throw<TimeoutException>();

        // Verify the exact process this test started has been terminated.
        var pid = s_lastSpawnedProcessId.Value;
        pid.Should().BeGreaterThan(0,
            "the helper must have tracked the child process it started");
        IsProcessAlive(pid).Should().BeFalse(
            "the child process the helper started must have been terminated on timeout");
    }

    // --- Issue #774: opt-in unit retirement trace ---

    [Fact]
    public void UnitTrace_Off_Generates_The_Same_Source_As_The_Default()
    {
        var program = UnitTraceProgram();

        var defaults = RecompilerHostCodeGen.Generate(program, loadedCode: null);
        var explicitlyOff = RecompilerHostCodeGen.Generate(program, loadedCode: null, traceUnits: false);

        defaults.Success.Should().BeTrue();
        explicitlyOff.Source.Should().Be(defaults.Source);
        defaults.Source.Should().NotContain("host_unit_trace");
        defaults.Source.Should().NotContain("unit_entry_pc");
    }

    [Fact]
    public void UnitTrace_On_Reports_Each_Unit_Once_With_Its_Credit_And_Cumulative_Total()
    {
        var result = RecompilerHostCodeGen.Generate(UnitTraceProgram(), loadedCode: null, traceUnits: true);
        result.Success.Should().BeTrue();

        // Three units: a single instruction, a single instruction, then a fused unit of two. The hook must see
        // each unit's entry PC, next PC, credit, cumulative total after the credit, and an uninterrupted flag.
        var testResult = CompileAndRun(result.Source!, @"
static int seen = 0;
static const uint32_t want_entry[] = {0x80000000u, 0x80000004u, 0x8000000Cu};
static const uint32_t want_next[] = {0x80000004u, 0x8000000Cu, 0x80000014u};
static const uint64_t want_start[] = {0ull, 1ull, 2ull};
static const uint64_t want_end[] = {1ull, 2ull, 4ull};
static void hook(RecompilerState* s, uint32_t entry, uint32_t next, uint64_t start, uint64_t end, uint32_t flags, int32_t termination) {
    (void)s;
    if (seen < 3 && entry == want_entry[seen] && next == want_next[seen] && start == want_start[seen] &&
        end == want_end[seen] && flags == 0u && termination == 0) seen++;
    else seen = 99;
}
int main() {
    RecompilerState state = {0};
    state.pc = 0x80000000u;
    state.host_unit_trace = hook;
    recompiler_dispatch(&state, 10);
    return seen == 3 ? 0 : 1;
}");
        testResult.Should().Be(0, "every unit must be traced exactly once, with its own entry PC and credit");
    }

    [Fact]
    public void UnitTrace_Off_Source_Has_No_Hook_Call_Even_With_A_Fused_Unit()
    {
        var result = RecompilerHostCodeGen.Generate(UnitTraceProgram(), loadedCode: null, traceUnits: false);

        result.Success.Should().BeTrue();
        result.Source.Should().NotContain("host_unit_trace(");
    }

    // A straight-line chain: 0x80000000 -> 0x80000004 -> fused 0x8000000C (two instructions, boundary after the first).
    private static RecompilerIrProgram UnitTraceProgram() => new(new[]
    {
        new RecompilerIrBlock(0x80000000u, Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000004u)),
        new RecompilerIrBlock(0x80000004u, Array.Empty<RecompilerIrOperation>(),
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x8000000Cu)),
        new RecompilerIrBlock(0x8000000Cu,
            new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1u),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 2u),
            },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000014u),
            retiredInstructionCount: 2, instructionBoundaries: new[] { 1 }),
    });


    // --- Issue #774 (B-2): the trace file ---
    // The trace path reaches the child through its own environment, set by the generated main, so the Test layer
    // never mutates the process environment. File reads and the scratch directory stay behind the same
    // AARC003 suppression the other gcc-driven tests in this file use.

    [Fact]
    public void UnitTrace_File_Records_Each_Unit_With_Start_And_End_Then_A_Complete_End_Line()
    {
        var result = RecompilerHostCodeGen.Generate(UnitTraceProgram(), loadedCode: null, traceUnits: true);
        result.Success.Should().BeTrue();
#pragma warning disable AARC003
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "unit.jsonl");

            RunTraced(result.Source!, path, maxBytes: null, "return state.retired_total == 4 ? 0 : 1;").Should().Be(0);

            var lines = ReadTraceLines(path);
            lines[0].GetProperty("kind").GetString().Should().Be("header");
            var units = lines.Where(line => line.GetProperty("kind").GetString() == "unit").ToList();
            units.Select(u => u.GetProperty("seq").GetUInt64()).Should().Equal(new ulong[] { 0, 1, 2 });
            units.Select(u => u.GetProperty("entry").GetString()).Should().Equal(new[] { "0x80000000", "0x80000004", "0x8000000C" });
            units.Select(u => u.GetProperty("next").GetString()).Should().Equal(new[] { "0x80000004", "0x8000000C", "0x80000014" });
            units.Select(u => u.GetProperty("start").GetUInt64()).Should().Equal(new ulong[] { 0, 1, 2 });
            units.Select(u => u.GetProperty("end").GetUInt64()).Should().Equal(new ulong[] { 1, 2, 4 });
            units.Select(u => u.GetProperty("retired").GetUInt64()).Should().Equal(new ulong[] { 1, 1, 2 },
                "the fused unit completes two instructions, and the record says so");
            lines[^1].GetProperty("kind").GetString().Should().Be("end");
            lines[^1].GetProperty("complete").GetBoolean().Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
#pragma warning restore AARC003
    }

    // Issue #774 (B-6b): a unit record carries the 32-register state, so two records and a marker fit in 2200 bytes.
    [Fact]
    public void UnitTrace_File_Truncates_At_The_Cap_With_A_Marker_And_An_Incomplete_End()
    {
        var result = RecompilerHostCodeGen.Generate(UnitTraceProgram(), loadedCode: null, traceUnits: true);
        result.Success.Should().BeTrue();
#pragma warning disable AARC003
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "capped.jsonl");

            // 900 admits the header and two units with the reserved tail still free, but not a third unit; the third
            // unit is dropped and the trace stops there.
            RunTraced(result.Source!, path, maxBytes: "2200", "return state.retired_total == 4 ? 0 : 1;").Should().Be(0,
                "a full trace file must not change the guest run");

            new FileInfo(path).Length.Should().BeLessThanOrEqualTo(2200);
            var lines = ReadTraceLines(path);
            lines.Where(line => line.GetProperty("kind").GetString() == "unit")
                .Select(u => u.GetProperty("seq").GetUInt64()).Should().Equal(new ulong[] { 0, 1 });
            var marker = lines.Single(line => line.GetProperty("kind").GetString() == "truncated");
            marker.GetProperty("seq").GetUInt64().Should().Be(2UL, "the first dropped record");
            lines[^1].GetProperty("kind").GetString().Should().Be("end");
            lines[^1].GetProperty("complete").GetBoolean().Should().BeFalse("a reader must see the trace is incomplete");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
#pragma warning restore AARC003
    }

    [Fact]
    public void UnitTrace_Open_Failure_Leaves_The_Run_Untraced_And_Unchanged()
    {
        var result = RecompilerHostCodeGen.Generate(UnitTraceProgram(), loadedCode: null, traceUnits: true);
        result.Success.Should().BeTrue();
#pragma warning disable AARC003
        var dir = CreateTempDir();
        try
        {
            var missing = Path.Combine(dir, "no-such-folder", "unit.jsonl");

            // The attach reports -1 and leaves the hook null, so the dispatch runs exactly as an untraced one.
            RunTraced(result.Source!, missing, maxBytes: null,
                "return state.retired_total == 4 && state.host_unit_trace == 0 ? 0 : 1;").Should().Be(0);
            File.Exists(missing).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
#pragma warning restore AARC003
    }

    [Fact]
    public void UnitTrace_Stopping_Unit_Is_Recorded_With_Its_Termination_And_Credits_Nothing()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(RecompilerIrTerminationReason.Exception)),
        });
        var result = RecompilerHostCodeGen.Generate(program, loadedCode: null, traceUnits: true);
        result.Success.Should().BeTrue();
#pragma warning disable AARC003
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "stop.jsonl");

            RunTraced(result.Source!, path, maxBytes: null,
                "return state.termination_reason == RECOMPILER_REASON_EXCEPTION ? 0 : 1;").Should().Be(0);

            var unit = ReadTraceLines(path).Single(line => line.GetProperty("kind").GetString() == "unit");
            unit.GetProperty("entry").GetString().Should().Be("0x80000000");
            unit.GetProperty("retired").GetUInt64().Should().Be(0UL, "the stopping unit completed no instruction");
            unit.GetProperty("termination").GetInt32().Should().NotBe(0, "the stop reason is recorded");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
#pragma warning restore AARC003
    }

    [Fact]
    public void UnitTrace_File_Records_A_Taken_Branch_With_The_Taken_Target_As_Next_PC()
    {
        // 0x80000000 branches on a non-zero condition to 0x80000010 (the taken target); 0x80000010 then falls out.
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1u) },
                new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000008u,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x80000010u, conditionValueId: 0))),
            new RecompilerIrBlock(0x80000010u, Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000018u)),
        });
        var result = RecompilerHostCodeGen.Generate(program, loadedCode: null, traceUnits: true);
        result.Success.Should().BeTrue();
#pragma warning disable AARC003
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "branch.jsonl");

            RunTraced(result.Source!, path, maxBytes: null, "return state.retired_total == 2 ? 0 : 1;").Should().Be(0);

            var units = ReadTraceLines(path).Where(line => line.GetProperty("kind").GetString() == "unit").ToList();
            units.Select(u => u.GetProperty("entry").GetString()).Should().Equal(new[] { "0x80000000", "0x80000010" });
            units.Select(u => u.GetProperty("next").GetString()).Should().Equal(new[] { "0x80000010", "0x80000018" },
                "the taken target is the next PC of the branching unit");
            units.Select(u => u.GetProperty("retired").GetUInt64()).Should().Equal(new ulong[] { 1, 1 });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
#pragma warning restore AARC003
    }

    // Builds the C main: it points the trace at tracePath (and optionally a cap) through its own environment, attaches,
    // dispatches the program from 0x80000000 with a budget of 10, then runs the given return statement.
    private static string RunTracedMain(string tracePath, string? maxBytes, string returnStatement)
    {
        var path = EscapeCString(tracePath);
        var cap = maxBytes is null ? string.Empty : $"    SET_ENV(\"PSX_UNIT_TRACE_MAX_BYTES\", \"{EscapeCString(maxBytes)}\");\n";
        return $@"
#include <stdlib.h>
#ifdef _WIN32
#define SET_ENV(name, value) _putenv(name ""="" value)
#else
#define SET_ENV(name, value) putenv((char*)(name ""="" value))
#endif
int main() {{
    RecompilerState state = {{0}};
    state.pc = 0x80000000u;
    SET_ENV(""PSX_UNIT_TRACE_FILE"", ""{path}"");
{cap}    if (recompiler_trace_attach(&state) == 1) {{}} else {{ if (state.host_unit_trace != 0) return 4; }}
    recompiler_dispatch(&state, 10);
    {returnStatement}
}}";
    }

    private static int RunTraced(string generatedSource, string tracePath, string? maxBytes, string returnStatement) =>
        CompileAndRun(generatedSource, RunTracedMain(tracePath, maxBytes, returnStatement));

    private static string EscapeCString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

#pragma warning disable AARC003
    private static List<JsonElement> ReadTraceLines(string path) =>
        File.ReadAllLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToList();
#pragma warning restore AARC003

    #pragma warning disable AARC003
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
#pragma warning restore AARC003

#pragma warning disable AARC003
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

    // Track the exact child this helper started on the calling thread, so the
    // timeout test can verify it was killed rather than scanning the whole
    // machine for processes named "sleep". Each concurrent test thread keeps
    // its own value.
    private static readonly ThreadLocal<int> s_lastSpawnedProcessId = new();

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

        // Track the exact child this helper started on this thread so the timeout
        // test can verify it was killed rather than scanning the whole machine.
        s_lastSpawnedProcessId.Value = process.Id;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);

            // Reap the killed child so it cannot linger as a zombie and so the
            // exact pid the timeout test tracks is observably gone.
            process.WaitForExit();

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

    /// <summary>
    /// True when a live process with the given id exists. A terminated and reaped
    /// child makes <see cref="Process.GetProcessById(int)"/> throw
    /// <see cref="ArgumentException"/>, which is reported here as "not alive".
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

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-host-codegen", Path.GetRandomFileName());
#pragma warning disable AARC003
        Directory.CreateDirectory(tempDir);
#pragma warning restore AARC003
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
                new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanSigned, resultValueId: 5, inputValueA: 2, inputValueB: 3),
                new RecompilerIrOperation(RecompilerIrOperationKind.CompareLessThanUnsigned, resultValueId: 6, inputValueA: 3, inputValueB: 2),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 5, register: 11),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 6, register: 23),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });
    }
}
