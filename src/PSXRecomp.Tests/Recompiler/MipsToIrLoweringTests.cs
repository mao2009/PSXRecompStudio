using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public class MipsToIrLoweringTests
{
    [Fact]
    public void Nop_ProducesSingleNopOperation()
    {
        var instruction = R3000aDecoder.Decode(0x00000000);
        instruction.Opcode.Should().Be(R3000aOpcode.Sll);

        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        result.IsSupported.Should().BeTrue();
        result.Block.Should().NotBeNull();

        var block = result.Block!;
        block.EntryPc.Should().Be(0x80000000);
        block.Operations.Should().HaveCount(1);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.Nop);
        block.Operations[0].ResultValueId.Should().Be(-1);
        block.Operations[0].InputValueA.Should().Be(-1);
        block.Operations[0].InputValueB.Should().Be(-1);
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        block.Exit.NextPc.Should().Be(0x80000004u);

        ValidateProgram(block);
    }

    [Fact]
    public void Addu_ProducesReadReadAddWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x21, 10, 8, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Addu);

        var result = MipsToIrLowerer.Lower(instruction, 0x80001000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(8);
        block.Operations[0].ResultValueId.Should().Be(0);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[1].Register.Should().Be(9);
        block.Operations[1].ResultValueId.Should().Be(1);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Add);
        block.Operations[2].InputValueA.Should().Be(0);
        block.Operations[2].InputValueB.Should().Be(1);
        block.Operations[2].ResultValueId.Should().Be(2);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[3].Register.Should().Be(10);
        block.Operations[3].InputValueA.Should().Be(2);

        ValidateProgram(block);
    }

    [Fact]
    public void Subu_ProducesReadReadSubtractWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x23, 5, 6, 7, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Subu);

        var result = MipsToIrLowerer.Lower(instruction, 0x80002000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Subtract);

        ValidateProgram(block);
    }

    [Fact]
    public void Addiu_PositiveImmediate_SignExtended()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 1));
        instruction.Opcode.Should().Be(R3000aOpcode.Addiu);

        var result = MipsToIrLowerer.Lower(instruction, 0x80003000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(0);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[1].Immediate.Should().Be(1u);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Add);
        block.Operations[3].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[3].Register.Should().Be(8);

        ValidateProgram(block);
    }

    [Fact]
    public void Addiu_NegativeImmediate_SignExtended()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0xFFFF));
        instruction.Opcode.Should().Be(R3000aOpcode.Addiu);

        var result = MipsToIrLowerer.Lower(instruction, 0x80004000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[1].Immediate.Should().Be(0xFFFFFFFF);

        ValidateProgram(block);
    }

    [Fact]
    public void And_ProducesReadReadAndWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x24, 10, 8, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.And);

        var result = MipsToIrLowerer.Lower(instruction, 0x80005000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.And);

        ValidateProgram(block);
    }

    [Fact]
    public void Or_ProducesReadReadOrWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x25, 10, 8, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Or);

        var result = MipsToIrLowerer.Lower(instruction, 0x80006000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Or);

        ValidateProgram(block);
    }

    [Fact]
    public void Xor_ProducesReadReadXorWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x26, 10, 8, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Xor);

        var result = MipsToIrLowerer.Lower(instruction, 0x80007000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Xor);

        ValidateProgram(block);
    }

    [Fact]
    public void Nor_ProducesReadReadNorWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x27, 10, 8, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Nor);

        var result = MipsToIrLowerer.Lower(instruction, 0x80008000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Nor);

        ValidateProgram(block);
    }

    [Fact]
    public void Lui_ProducesConstantWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x0F, 8, 0, 0x1234));
        instruction.Opcode.Should().Be(R3000aOpcode.Lui);

        var result = MipsToIrLowerer.Lower(instruction, 0x80009000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(2);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[0].Immediate.Should().Be(0x12340000u);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[1].Register.Should().Be(8);

        ValidateProgram(block);
    }

    [Fact]
    public void Sll_ProducesReadShiftLeftLogicalWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x00, 10, 0, 8, 3));
        instruction.Opcode.Should().Be(R3000aOpcode.Sll);

        var result = MipsToIrLowerer.Lower(instruction, 0x8000A000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(3);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(8);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ShiftLeftLogical);
        block.Operations[1].ShiftAmount.Should().Be(3);
        block.Operations[1].InputValueA.Should().Be(0);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.WriteGpr);
        block.Operations[2].Register.Should().Be(10);

        ValidateProgram(block);
    }

    [Fact]
    public void Srl_ProducesReadShiftRightLogicalWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x02, 10, 0, 8, 5));
        instruction.Opcode.Should().Be(R3000aOpcode.Srl);

        var result = MipsToIrLowerer.Lower(instruction, 0x8000B000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(3);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ShiftRightLogical);
        block.Operations[1].ShiftAmount.Should().Be(5);

        ValidateProgram(block);
    }

    [Fact]
    public void Sra_ProducesReadShiftRightArithmeticWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x03, 10, 0, 8, 4));
        instruction.Opcode.Should().Be(R3000aOpcode.Sra);

        var result = MipsToIrLowerer.Lower(instruction, 0x8000C000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(3);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ShiftRightArithmetic);
        block.Operations[1].ShiftAmount.Should().Be(4);

        ValidateProgram(block);
    }

    [Fact]
    public void ZeroRegisterRead_ProducesValidReadGpr()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x21, 10, 0, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Addu);

        var result = MipsToIrLowerer.Lower(instruction, 0x8000D000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(0);

        ValidateProgram(block);
    }

    [Fact]
    public void Addiu_ZeroRegisterDestination_ProducesNoWriteGpr()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 0, 0, 1));
        instruction.Opcode.Should().Be(R3000aOpcode.Addiu);

        var result = MipsToIrLowerer.Lower(instruction, 0x8000E000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        var writeOps = block.Operations.Where(op => op.Kind == RecompilerIrOperationKind.WriteGpr).ToList();
        writeOps.Should().BeEmpty();

        ValidateProgram(block);
    }

    [Fact]
    public void Addiu_ZeroRegZeroReg_ProducesNoArchitecturalWrite()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 0, 0, 1));
        var result = MipsToIrLowerer.Lower(instruction, 0x8000F000);
        result.IsSupported.Should().BeTrue();

        var block = result.Block!;
        block.Operations.Should().HaveCount(3);
        block.Operations[0].Kind.Should().Be(RecompilerIrOperationKind.ReadGpr);
        block.Operations[0].Register.Should().Be(0);
        block.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.Constant);
        block.Operations[1].Immediate.Should().Be(1u);
        block.Operations[2].Kind.Should().Be(RecompilerIrOperationKind.Add);

        ValidateProgram(block);
    }

    [Fact]
    public void Addiu_NegativeImmediate_BitPattern()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0x8000));
        var result = MipsToIrLowerer.Lower(instruction, 0x80010000);
        result.IsSupported.Should().BeTrue();

        var constantOp = result.Block!.Operations.First(op => op.Kind == RecompilerIrOperationKind.Constant);
        constantOp.Immediate.Should().Be(0xFFFF8000u);

        ValidateProgram(result.Block!);
    }

    [Fact]
    public void Sra_RightShiftNegativeValue_PreservesShiftKind()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x03, 10, 0, 8, 1));
        var result = MipsToIrLowerer.Lower(instruction, 0x80011000);
        result.IsSupported.Should().BeTrue();

        result.Block!.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ShiftRightArithmetic);
        result.Block!.Operations[1].ShiftAmount.Should().Be(1);

        ValidateProgram(result.Block!);
    }

    [Fact]
    public void Srl_RightShiftHighBit_LogicalShift()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x02, 10, 0, 8, 1));
        var result = MipsToIrLowerer.Lower(instruction, 0x80012000);
        result.IsSupported.Should().BeTrue();

        result.Block!.Operations[1].Kind.Should().Be(RecompilerIrOperationKind.ShiftRightLogical);
        result.Block!.Operations[1].ShiftAmount.Should().Be(1);

        ValidateProgram(result.Block!);
    }

    [Fact]
    public void Addu_OperationCountAndShape()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x21, 10, 8, 9, 0));
        var result = MipsToIrLowerer.Lower(instruction, 0x80013000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        block.Operations.Should().HaveCount(4);
        block.Operations.Should().ContainSingle(op => op.Kind == RecompilerIrOperationKind.Add);
        block.Operations.Count(op => op.Kind == RecompilerIrOperationKind.ReadGpr).Should().Be(2);
        block.Operations.Count(op => op.Kind == RecompilerIrOperationKind.WriteGpr).Should().Be(1);

        ValidateProgram(block);
    }

    [Fact]
    public void Addu_Wraparound_IROperandShape()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x21, 10, 8, 9, 0));
        var result = MipsToIrLowerer.Lower(instruction, 0x80014000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        var addOp = block.Operations.First(op => op.Kind == RecompilerIrOperationKind.Add);
        addOp.InputValueA.Should().Be(0);
        addOp.InputValueB.Should().Be(1);
        addOp.ResultValueId.Should().Be(2);

        ValidateProgram(block);
    }

    [Fact]
    public void Subu_Wraparound_IROperandShape()
    {
        var instruction = R3000aDecoder.Decode(EncodeR(0x23, 10, 8, 9, 0));
        var result = MipsToIrLowerer.Lower(instruction, 0x80015000);
        result.IsSupported.Should().BeTrue();
        var block = result.Block!;

        var subOp = block.Operations.First(op => op.Kind == RecompilerIrOperationKind.Subtract);
        subOp.InputValueA.Should().Be(0);
        subOp.InputValueB.Should().Be(1);
        subOp.ResultValueId.Should().Be(2);

        ValidateProgram(block);
    }

    [Fact]
    public void Determinism_SameFixtureProducesIdenticalSerialization()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 1));
        var first = MipsToIrLowerer.Lower(instruction, 0x80000000);
        var second = MipsToIrLowerer.Lower(instruction, 0x80000000);

        first.IsSupported.Should().BeTrue();
        second.IsSupported.Should().BeTrue();

        var program1 = new RecompilerIrProgram(new[] { first.Block! });
        var program2 = new RecompilerIrProgram(new[] { second.Block! });

        RecompilerIrSerializer.Serialize(program1).Should().Be(RecompilerIrSerializer.Serialize(program2));
    }

    [Fact]
    public void Unsupported_Lw_ReturnsDiagnostic()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x23, 8, 9, 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Lw);

        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        result.IsSupported.Should().BeFalse();
        result.Block.Should().BeNull();
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidOperationShape);
        result.DiagnosticMessage.Should().NotBeNullOrEmpty();
        result.UnsupportedOpcode.Should().Be(R3000aOpcode.Lw);
    }

    [Fact]
    public void Unsupported_Beq_ReturnsDiagnostic()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x04, 8, 9, 0x100));
        instruction.Opcode.Should().Be(R3000aOpcode.Beq);

        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        result.IsSupported.Should().BeFalse();
        result.DiagnosticCode.Should().Be(RecompilerIrDiagnosticCode.InvalidOperationShape);
        result.UnsupportedOpcode.Should().Be(R3000aOpcode.Beq);
    }

    [Fact]
    public void Unsupported_J_ReturnsDiagnostic()
    {
        var encodedWord = 0x08000100u;
        var instruction = R3000aDecoder.Decode(encodedWord);
        instruction.Opcode.Should().Be(R3000aOpcode.J);

        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        result.IsSupported.Should().BeFalse();
        result.UnsupportedOpcode.Should().Be(R3000aOpcode.J);
    }

    [Fact]
    public void AllSupportedOpcodes_ReturnSupported()
    {
        var opcodes = new (R3000aOpcode Opcode, uint Encoded)[]
        {
            (R3000aOpcode.Sll, EncodeR(0x00, 10, 0, 8, 1)),
            (R3000aOpcode.Srl, EncodeR(0x02, 10, 0, 8, 1)),
            (R3000aOpcode.Sra, EncodeR(0x03, 10, 0, 8, 1)),
            (R3000aOpcode.Addu, EncodeR(0x21, 10, 8, 9, 0)),
            (R3000aOpcode.Subu, EncodeR(0x23, 10, 8, 9, 0)),
            (R3000aOpcode.And, EncodeR(0x24, 10, 8, 9, 0)),
            (R3000aOpcode.Or, EncodeR(0x25, 10, 8, 9, 0)),
            (R3000aOpcode.Xor, EncodeR(0x26, 10, 8, 9, 0)),
            (R3000aOpcode.Nor, EncodeR(0x27, 10, 8, 9, 0)),
            (R3000aOpcode.Addiu, EncodeI(0x09, 8, 0, 42)),
            (R3000aOpcode.Lui, EncodeI(0x0F, 8, 0, 0x1234)),
        };

        foreach (var (opcode, encoded) in opcodes)
        {
            var instruction = R3000aDecoder.Decode(encoded);
            var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
            result.IsSupported.Should().BeTrue($"opcode {opcode} should be supported");
        }
    }

    [Fact]
    public void Scenario_MiniFixture_ThreeInstructions()
    {
        var addiu1 = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 1));
        var addiu2 = R3000aDecoder.Decode(EncodeI(0x09, 9, 0, 2));
        var addu = R3000aDecoder.Decode(EncodeR(0x21, 10, 8, 9, 0));

        var instructions = new (R3000aInstruction Instruction, uint EntryPc)[]
        {
            (addiu1, 0x80000000),
            (addiu2, 0x80000004),
            (addu, 0x80000008),
        };

        var program = MipsToIrLowerer.LowerProgram(instructions);

        program.Blocks.Should().HaveCount(3);
        program.Blocks.Select(b => b.EntryPc).Should().Equal(0x80000000u, 0x80000004u, 0x80000008u);

        program.Blocks[0].Operations.Should().HaveCount(4);
        program.Blocks[0].Exit.NextPc.Should().Be(0x80000004u);

        program.Blocks[1].Operations.Should().HaveCount(4);
        program.Blocks[1].Exit.NextPc.Should().Be(0x80000008u);

        program.Blocks[2].Operations.Should().HaveCount(4);
        program.Blocks[2].Exit.NextPc.Should().Be(0x8000000Cu);

        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Scenario_MiniFixture_DeterministicSerialization()
    {
        var addiu1 = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 1));
        var addiu2 = R3000aDecoder.Decode(EncodeI(0x09, 9, 0, 2));
        var addu = R3000aDecoder.Decode(EncodeR(0x21, 10, 8, 9, 0));

        var instructions = new (R3000aInstruction Instruction, uint EntryPc)[]
        {
            (addiu1, 0x80000000),
            (addiu2, 0x80000004),
            (addu, 0x80000008),
        };

        var first = MipsToIrLowerer.LowerProgram(instructions);
        var second = MipsToIrLowerer.LowerProgram(instructions);

        RecompilerIrSerializer.Serialize(first).Should().Be(RecompilerIrSerializer.Serialize(second));
    }

    [Fact]
    public void Validator_AllSupportedOpcodes_PassValidation()
    {
        var opcodes = new (uint Encoded, string Name)[]
        {
            (EncodeR(0x00, 10, 0, 8, 1), "SLL"),
            (EncodeR(0x02, 10, 0, 8, 1), "SRL"),
            (EncodeR(0x03, 10, 0, 8, 1), "SRA"),
            (EncodeR(0x21, 10, 8, 9, 0), "ADDU"),
            (EncodeR(0x23, 10, 8, 9, 0), "SUBU"),
            (EncodeR(0x24, 10, 8, 9, 0), "AND"),
            (EncodeR(0x25, 10, 8, 9, 0), "OR"),
            (EncodeR(0x26, 10, 8, 9, 0), "XOR"),
            (EncodeR(0x27, 10, 8, 9, 0), "NOR"),
            (EncodeI(0x09, 8, 0, 42), "ADDIU"),
            (EncodeI(0x0F, 8, 0, 0x1234), "LUI"),
        };

        foreach (var (encoded, name) in opcodes)
        {
            var instruction = R3000aDecoder.Decode(encoded);
            var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
            result.IsSupported.Should().BeTrue();
            var program = new RecompilerIrProgram(new[] { result.Block! });
            RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue($"validation failed for {name}");
        }
    }

    [Fact]
    public void Reference_SignExtension_ByteBoundary()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0x007F));
        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        var constantOp = result.Block!.Operations.First(op => op.Kind == RecompilerIrOperationKind.Constant);
        constantOp.Immediate.Should().Be(0x0000007Fu);
    }

    [Fact]
    public void Reference_SignExtension_Bit15Set()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0x8000));
        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        var constantOp = result.Block!.Operations.First(op => op.Kind == RecompilerIrOperationKind.Constant);
        constantOp.Immediate.Should().Be(0xFFFF8000u);
    }

    [Fact]
    public void Reference_SignExtension_MaxPositive()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0x7FFF));
        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        var constantOp = result.Block!.Operations.First(op => op.Kind == RecompilerIrOperationKind.Constant);
        constantOp.Immediate.Should().Be(0x00007FFFu);
    }

    [Fact]
    public void Reference_SignExtension_NegativeOne()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0xFFFF));
        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        var constantOp = result.Block!.Operations.First(op => op.Kind == RecompilerIrOperationKind.Constant);
        constantOp.Immediate.Should().Be(0xFFFFFFFF);
    }

    [Fact]
    public void Reference_SignExtension_Zero()
    {
        var instruction = R3000aDecoder.Decode(EncodeI(0x09, 8, 0, 0x0000));
        var result = MipsToIrLowerer.Lower(instruction, 0x80000000);
        var constantOp = result.Block!.Operations.First(op => op.Kind == RecompilerIrOperationKind.Constant);
        constantOp.Immediate.Should().Be(0x00000000u);
    }

    private static void ValidateProgram(RecompilerIrBlock block)
    {
        var program = new RecompilerIrProgram(new[] { block });
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
    }

    private static uint EncodeR(byte funct, byte rd, byte rs, byte rt, byte shamt) =>
        (0u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | ((uint)rd << 11) | ((uint)shamt << 6) | funct;

    private static uint EncodeI(byte opcode, byte rt, byte rs, ushort immediate) =>
        ((uint)opcode << 26) | ((uint)rs << 21) | ((uint)rt << 16) | immediate;
}
