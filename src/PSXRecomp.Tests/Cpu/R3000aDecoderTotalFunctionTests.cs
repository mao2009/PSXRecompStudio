using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aDecoderTotalFunctionTests
{
    private static readonly uint[] DefinedOpcodeFields =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
        0x28, 0x29, 0x2A, 0x2B, 0x2E,
        0x32,
        0x3A,
    };

    private static readonly uint[] DefinedSpecialFuncts =
    {
        0x00, 0x02, 0x03, 0x04, 0x06, 0x07,
        0x08, 0x09, 0x0C, 0x0D,
        0x10, 0x11, 0x12, 0x13,
        0x18, 0x19, 0x1A, 0x1B,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x2A, 0x2B,
    };

    private static readonly byte[] DefinedRegimmSelectors = { 0x00, 0x01, 0x10, 0x11 };

    private static readonly byte[] DefinedCoprocessorZeroSelectors = { 0x00, 0x04, 0x10 };

    private static readonly byte[] DefinedCoprocessorTwoSelectors = { 0x00, 0x02, 0x04, 0x06 };

    private const byte CoprocessorOperationSelectorMask = 0x10;

    private const uint CoFunFieldMask = 0x01FFFFFF;

    [Fact]
    public void Decode_AllSixtyFourOpcodeFields_ReturnWithoutThrowing()
    {
        var representativeBodies = new[]
        {
            0x00000000u,
            (31u << 21) | (31u << 16) | (31u << 11) | (31u << 6) | 63u,
            (31u << 21) | (31u << 16) | 0xFFFFu,
        };

        foreach (var body in representativeBodies)
        {
            for (uint opcodeField = 0x00; opcodeField <= 0x3F; opcodeField++)
            {
                var word = body | (opcodeField << 26);
                var act = () => R3000aDecoder.Decode(word);
                act.Should().NotThrow();
                R3000aDecoder.Decode(word).EncodedWord.Should().Be(word);
            }
        }
    }

    [Fact]
    public void Decode_AllSixtyFourSpecialFunctValues_ReturnWithoutThrowing()
    {
        for (var funct = 0; funct <= 63; funct++)
        {
            var word = (1u << 21) | (2u << 16) | (3u << 11) | (4u << 6) | (uint)funct;
            var act = () => R3000aDecoder.Decode(word);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Decode_AllThirtyTwoRegimmSelectors_ReturnWithoutThrowing()
    {
        for (var selector = 0; selector <= 31; selector++)
        {
            var word = IType(0x01, 4, (ushort)selector, 8);
            var act = () => R3000aDecoder.Decode(word);
            act.Should().NotThrow();
        }
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x13)]
    public void Decode_AllThirtyTwoCoprocessorSelectors_ReturnWithoutThrowing(uint coprocessorOpcodeField)
    {
        for (var selector = 0; selector <= 31; selector++)
        {
            var word = (coprocessorOpcodeField << 26) | ((uint)selector << 21)
                | (5u << 16) | (6u << 11) | 0x1234u;
            var act = () => R3000aDecoder.Decode(word);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Decode_RepresentativeFieldCombinations_ReturnWithoutThrowing()
    {
        var words = new[]
        {
            0xFFFFFFFFu,
            0x00000000u,
            0xFC000000u,
            0x03E00008u,
            0x03FFFFFFu,
            0xFFFFFFFCu,
            0x70000000u,
            0xB1234567u,
            0xC0000000u,
            0x4FFFFFFFu,
            0x48034825u,
            0x8FA80008u,
        };

        foreach (var word in words)
        {
            var act = () => R3000aDecoder.Decode(word);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Decode_KnownOpcodeFields_NeverClassifyAsReserved()
    {
        foreach (var opcodeField in DefinedOpcodeFields)
        {
            var word = BuildRepresentativeWord(opcodeField);
            R3000aDecoder.Decode(word).Opcode.Should().NotBe(R3000aOpcode.Reserved,
                "opcode field 0x{0:X2} maps to a YAML or structural classification", opcodeField);
        }
    }

    [Fact]
    public void Decode_UnknownOpcodeFields_AlwaysClassifyAsReserved()
    {
        for (uint opcodeField = 0x00; opcodeField <= 0x3F; opcodeField++)
        {
            if (DefinedOpcodeFields.Contains(opcodeField))
            {
                continue;
            }

            var word = opcodeField << 26;
            R3000aDecoder.Decode(word).Opcode.Should().Be(R3000aOpcode.Reserved,
                "opcode field 0x{0:X2} is absent from the SSOT", opcodeField);
        }
    }

    [Fact]
    public void Decode_UndefinedSpecialFuncts_AlwaysClassifyAsReserved()
    {
        for (var funct = 0; funct <= 63; funct++)
        {
            if (DefinedSpecialFuncts.Contains((uint)funct))
            {
                continue;
            }

            var word = (1u << 21) | (2u << 16) | (3u << 11) | (4u << 6) | (uint)funct;
            R3000aDecoder.Decode(word).Opcode.Should().Be(R3000aOpcode.Reserved,
                "SPECIAL funct 0x{0:X2} is absent from the SSOT", funct);
        }
    }

    [Fact]
    public void Decode_UndefinedRegimmSelectors_AlwaysClassifyAsReserved()
    {
        for (var selector = 0; selector <= 31; selector++)
        {
            if (DefinedRegimmSelectors.Contains((byte)selector))
            {
                continue;
            }

            var word = IType(0x01, 4, (ushort)selector, 8);
            R3000aDecoder.Decode(word).Opcode.Should().Be(R3000aOpcode.Reserved,
                "REGIMM selector 0x{0:X2} is absent from the SSOT", selector);
        }
    }

    [Fact]
    public void Decode_UndefinedCoprocessorZeroSelectors_AlwaysClassifyAsReserved()
    {
        for (var selector = 0; selector <= 31; selector++)
        {
            if (DefinedCoprocessorZeroSelectors.Contains((byte)selector))
            {
                continue;
            }

            var word = (0x10u << 26) | ((uint)selector << 21) | (8u << 16) | (12u << 11);
            R3000aDecoder.Decode(word).Opcode.Should().Be(R3000aOpcode.Reserved,
                "COP0 selector 0x{0:X2} is absent from the SSOT", selector);
        }
    }

    [Fact]
    public void Decode_UndefinedCoprocessorTwoSelectors_AlwaysClassifyAsReserved()
    {
        for (var selector = 0; selector <= 31; selector++)
        {
            if (DefinedCoprocessorTwoSelectors.Contains((byte)selector)
                || ((byte)selector & CoprocessorOperationSelectorMask) != 0)
            {
                continue;
            }

            var word = (0x12u << 26) | ((uint)selector << 21) | (14u << 11);
            R3000aDecoder.Decode(word).Opcode.Should().Be(R3000aOpcode.Reserved,
                "COP2 form with rs=0x{0:X2} is absent from the SSOT", selector);
        }
    }

    [Fact]
    public void Decode_CoprocessorTwoOperationSelectors_AlwaysDecodeAsCommands()
    {
        for (var selector = 0x10; selector <= 31; selector++)
        {
            foreach (var cofunPattern in new[] { 0u, 1u, 0x12345u, CoFunFieldMask })
            {
                var word = (0x12u << 26) | ((uint)selector << 21) | cofunPattern;
                var instruction = R3000aDecoder.Decode(word);
                instruction.Opcode.Should().Be(R3000aOpcode.Cop2Command,
                    "rs=0x{0:X2} lies in the COP2 operation range", selector);
                instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.ExecuteCommand);
                instruction.CopInfo.Command.Should().Be(word & CoFunFieldMask);
            }
        }
    }

    [Fact]
    public void Decode_CoprocessorZeroOperationSpace_OnlyRfeFunctionDecodesAsRfe()
    {
        for (var funct = 0; funct <= 63; funct++)
        {
            var word = (0x10u << 26) | (0x10u << 21) | (uint)funct;
            var instruction = R3000aDecoder.Decode(word);

            if (funct == 0x10)
            {
                instruction.Opcode.Should().Be(R3000aOpcode.Rfe,
                    "funct=0x10 with rs=0x10 is the RFE encoding");
            }
            else
            {
                instruction.Opcode.Should().Be(R3000aOpcode.Reserved,
                    "COP0 operation funct 0x{0:X2} is absent from the SSOT", funct);
            }
        }
    }

    [Fact]
    public void Decode_SameInput_ProducesEqualInstruction()
    {
        var first = R3000aDecoder.Decode(0x8FA80008u);
        var second = R3000aDecoder.Decode(0x8FA80008u);
        second.Should().Be(first);
    }

    [Fact]
    public void Decode_DelaySlotMetadata_AppearsOnExactlyTheTwelveControlFlowInstructions()
    {
        var unconditionalWords = new[]
        {
            JType(0x02, 0x100),
            JType(0x03, 0x100),
            RType(0x00, 9, 0, 0, 0, 0x08),
            RType(0x00, 9, 0, 10, 0, 0x09),
        };
        var conditionalWords = new[]
        {
            IType(0x04, 1, 2, 4),
            IType(0x05, 1, 2, 4),
            IType(0x06, 1, 2, 4),
            IType(0x07, 1, 2, 4),
            IType(0x01, 1, 0x00, 4),
            IType(0x01, 1, 0x01, 4),
            IType(0x01, 1, 0x10, 4),
            IType(0x01, 1, 0x11, 4),
        };

        foreach (var word in unconditionalWords)
        {
            R3000aDecoder.Decode(word).DelaySlot.Should().Be(R3000aDelaySlotKind.Unconditional);
        }

        foreach (var word in conditionalWords)
        {
            R3000aDecoder.Decode(word).DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
        }
    }

    [Fact]
    public void Decode_LinkMetadata_IsPresentOnExactlyTheFourLinkInstructions()
    {
        var linkWords = new[]
        {
            JType(0x03, 0x100),
            RType(0x00, 9, 0, 10, 0, 0x09),
            IType(0x01, 1, 0x10, 4),
            IType(0x01, 1, 0x11, 4),
        };

        foreach (var word in linkWords)
        {
            R3000aDecoder.Decode(word).LinkInfo.WritesLink.Should().BeTrue();
        }

        var nonLinkWords = new[]
        {
            JType(0x02, 0x100),
            RType(0x00, 9, 0, 0, 0, 0x08),
            IType(0x01, 1, 0x00, 4),
            IType(0x01, 1, 0x01, 4),
        };

        foreach (var word in nonLinkWords)
        {
            R3000aDecoder.Decode(word).LinkInfo.WritesLink.Should().BeFalse();
        }
    }

    [Fact]
    public void Decode_LoadDelayMetadata_CoversAllSevenGprLoadOpcodes()
    {
        var loadOpcodeFields = new (uint OpcodeField, bool PairSpecial)[]
        {
            (0x20, false),
            (0x21, false),
            (0x22, true),
            (0x23, false),
            (0x24, false),
            (0x25, false),
            (0x26, true),
        };

        foreach (var (opcodeField, pairSpecial) in loadOpcodeFields)
        {
            var instruction = R3000aDecoder.Decode(IType(opcodeField, 29, 7, 4));
            instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeTrue();
            instruction.LoadDelayInfo.TargetRegister.Should().Be(7);
            instruction.LoadDelayInfo.LwlLwrPairSpecial.Should().Be(pairSpecial);
        }
    }

    private static uint BuildRepresentativeWord(uint opcodeField)
    {
        return opcodeField switch
        {
            0x00 => (1u << 21) | (2u << 16) | (3u << 11) | 0x20u,
            0x01 => IType(0x01, 1, 0x00, 4),
            0x02 => IType(0x02, 0, 0, 0),
            0x03 => IType(0x03, 0, 0, 0),
            0x10 => (0x10u << 26) | (8u << 16) | (12u << 11),
            0x11 => (0x11u << 26) | (8u << 16) | (12u << 11),
            0x12 => (0x12u << 26) | (2u << 21) | 1u,
            0x13 => (0x13u << 26) | (8u << 16) | (12u << 11),
            _ => IType(opcodeField, 1, 2, 0x1234),
        };
    }

    private static uint RType(uint opcode, uint rs, uint rt, uint rd, uint shamt, uint funct)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | (rd << 11) | (shamt << 6) | funct;
    }

    private static uint IType(uint opcode, uint rs, uint rt, ushort immediate)
    {
        return (opcode << 26) | (rs << 21) | (rt << 16) | immediate;
    }

    private static uint JType(uint opcode, uint index)
    {
        return (opcode << 26) | index;
    }
}
