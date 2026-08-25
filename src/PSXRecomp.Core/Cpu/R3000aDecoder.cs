using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public static class R3000aDecoder
{
    private const uint SpecialOpcodeField = 0x00;
    private const uint RegimmOpcodeField = 0x01;
    private const uint JumpOpcodeField = 0x02;
    private const uint JumpAndLinkOpcodeField = 0x03;
    private const uint BranchEqualOpcodeField = 0x04;
    private const uint BranchNotEqualOpcodeField = 0x05;
    private const uint BranchLessOrEqualZeroOpcodeField = 0x06;
    private const uint BranchGreaterZeroOpcodeField = 0x07;
    private const uint AddImmediateOpcodeField = 0x08;
    private const uint AddImmediateUnsignedOpcodeField = 0x09;
    private const uint SetLessThanImmediateOpcodeField = 0x0A;
    private const uint SetLessThanImmediateUnsignedOpcodeField = 0x0B;
    private const uint AndImmediateOpcodeField = 0x0C;
    private const uint OrImmediateOpcodeField = 0x0D;
    private const uint XorImmediateOpcodeField = 0x0E;
    private const uint LoadUpperImmediateOpcodeField = 0x0F;
    private const uint CoprocessorZeroOpcodeField = 0x10;
    private const uint CoprocessorOneOpcodeField = 0x11;
    private const uint CoprocessorTwoOpcodeField = 0x12;
    private const uint CoprocessorThreeOpcodeField = 0x13;
    private const uint LoadByteOpcodeField = 0x20;
    private const uint LoadHalfwordOpcodeField = 0x21;
    private const uint LoadWordLeftOpcodeField = 0x22;
    private const uint LoadWordOpcodeField = 0x23;
    private const uint LoadByteUnsignedOpcodeField = 0x24;
    private const uint LoadHalfwordUnsignedOpcodeField = 0x25;
    private const uint LoadWordRightOpcodeField = 0x26;
    private const uint StoreByteOpcodeField = 0x28;
    private const uint StoreHalfwordOpcodeField = 0x29;
    private const uint StoreWordLeftOpcodeField = 0x2A;
    private const uint StoreWordOpcodeField = 0x2B;
    private const uint StoreWordRightOpcodeField = 0x2E;
    private const uint LoadWordCoprocessorTwoOpcodeField = 0x32;
    private const uint StoreWordCoprocessorTwoOpcodeField = 0x3A;

    private const int OpcodeShift = 26;
    private const int RsShift = 21;
    private const int RtShift = 16;
    private const int RdShift = 11;
    private const int ShamtShift = 6;

    private const uint FiveBitFieldMask = 0x1F;
    private const uint SixBitFieldMask = 0x3F;
    private const uint HalfwordFieldMask = 0xFFFF;
    private const uint JumpIndexFieldMask = 0x03FFFFFF;
    private const uint CoFunFieldMask = 0x01FFFFFF;

    private const byte CoprocessorZeroId = 0;
    private const byte CoprocessorTwoId = 2;

    private const byte MoveFromCoprocessorSelector = 0x00;
    private const byte RegimmBranchLessThanZeroSelector = 0x00;
    private const byte RegimmBranchGreaterOrEqualZeroSelector = 0x01;
    private const byte MoveControlFromCoprocessorSelector = 0x02;
    private const byte MoveToCoprocessorSelector = 0x04;
    private const byte MoveControlToCoprocessorSelector = 0x06;
    private const byte ReturnFromExceptionSelector = 0x10;
    private const byte ReturnFromExceptionFunct = 0x10;
    private const byte RegimmBranchLessThanZeroAndLinkSelector = 0x10;
    private const byte RegimmBranchGreaterOrEqualZeroAndLinkSelector = 0x11;
    private const byte CoprocessorOperationSelectorMask = 0x10;

    public static R3000aInstruction Decode(uint encodedWord)
    {
        var _opcodeField = ExtractOpcode(encodedWord);
        return _opcodeField switch
        {
            SpecialOpcodeField => DecodeSpecial(encodedWord),
            RegimmOpcodeField => DecodeRegimm(encodedWord),
            JumpOpcodeField => DecodeJump(encodedWord, R3000aOpcode.J),
            JumpAndLinkOpcodeField => DecodeJumpAndLink(encodedWord),
            BranchEqualOpcodeField => DecodeThreeOperandBranch(encodedWord, R3000aOpcode.Beq),
            BranchNotEqualOpcodeField => DecodeThreeOperandBranch(encodedWord, R3000aOpcode.Bne),
            BranchLessOrEqualZeroOpcodeField => DecodeTwoOperandBranch(encodedWord, R3000aOpcode.Blez),
            BranchGreaterZeroOpcodeField => DecodeTwoOperandBranch(encodedWord, R3000aOpcode.Bgtz),
            AddImmediateOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Addi),
            AddImmediateUnsignedOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Addiu),
            SetLessThanImmediateOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Slti),
            SetLessThanImmediateUnsignedOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Sltiu),
            AndImmediateOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Andi),
            OrImmediateOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Ori),
            XorImmediateOpcodeField => DecodeImmediateArithmetic(encodedWord, R3000aOpcode.Xori),
            LoadUpperImmediateOpcodeField => DecodeLoadUpperImmediate(encodedWord),
            CoprocessorZeroOpcodeField => DecodeCoprocessorZero(encodedWord),
            CoprocessorOneOpcodeField => DecodeUnusableCoprocessor(encodedWord, R3000aOpcode.Cop1Unusable),
            CoprocessorTwoOpcodeField => DecodeCoprocessorTwo(encodedWord),
            CoprocessorThreeOpcodeField => DecodeUnusableCoprocessor(encodedWord, R3000aOpcode.Cop3Unusable),
            LoadByteOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lb, pairSpecial: false),
            LoadHalfwordOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lh, pairSpecial: false),
            LoadWordLeftOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lwl, pairSpecial: true),
            LoadWordOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lw, pairSpecial: false),
            LoadByteUnsignedOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lbu, pairSpecial: false),
            LoadHalfwordUnsignedOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lhu, pairSpecial: false),
            LoadWordRightOpcodeField => DecodeLoad(encodedWord, R3000aOpcode.Lwr, pairSpecial: true),
            StoreByteOpcodeField => DecodeMemoryAccess(encodedWord, R3000aOpcode.Sb),
            StoreHalfwordOpcodeField => DecodeMemoryAccess(encodedWord, R3000aOpcode.Sh),
            StoreWordLeftOpcodeField => DecodeMemoryAccess(encodedWord, R3000aOpcode.Swl),
            StoreWordOpcodeField => DecodeMemoryAccess(encodedWord, R3000aOpcode.Sw),
            StoreWordRightOpcodeField => DecodeMemoryAccess(encodedWord, R3000aOpcode.Swr),
            LoadWordCoprocessorTwoOpcodeField => DecodeCoprocessorDataTransfer(encodedWord, R3000aOpcode.Lwc2),
            StoreWordCoprocessorTwoOpcodeField => DecodeCoprocessorDataTransfer(encodedWord, R3000aOpcode.Swc2),
            _ => CreateReserved(encodedWord),
        };
    }

    private static R3000aInstruction DecodeSpecial(uint encodedWord)
    {
        var _funct = ExtractFunct(encodedWord);
        return _funct switch
        {
            0x00 => DecodeShiftByImmediate(encodedWord, R3000aOpcode.Sll),
            0x02 => DecodeShiftByImmediate(encodedWord, R3000aOpcode.Srl),
            0x03 => DecodeShiftByImmediate(encodedWord, R3000aOpcode.Sra),
            0x04 => DecodeShiftByRegister(encodedWord, R3000aOpcode.Sllv),
            0x06 => DecodeShiftByRegister(encodedWord, R3000aOpcode.Srlv),
            0x07 => DecodeShiftByRegister(encodedWord, R3000aOpcode.Srav),
            0x08 => DecodeJumpRegister(encodedWord),
            0x09 => DecodeJumpAndLinkRegister(encodedWord),
            0x0C => DecodeTrap(encodedWord, R3000aOpcode.Syscall),
            0x0D => DecodeTrap(encodedWord, R3000aOpcode.Break),
            0x10 => DecodeMoveFromHiLo(encodedWord, R3000aOpcode.Mfhi),
            0x11 => DecodeMoveToHiLo(encodedWord, R3000aOpcode.Mthi),
            0x12 => DecodeMoveFromHiLo(encodedWord, R3000aOpcode.Mflo),
            0x13 => DecodeMoveToHiLo(encodedWord, R3000aOpcode.Mtlo),
            0x18 => DecodeMultiplyDivide(encodedWord, R3000aOpcode.Mult),
            0x19 => DecodeMultiplyDivide(encodedWord, R3000aOpcode.Multu),
            0x1A => DecodeMultiplyDivide(encodedWord, R3000aOpcode.Div),
            0x1B => DecodeMultiplyDivide(encodedWord, R3000aOpcode.Divu),
            0x20 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Add),
            0x21 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Addu),
            0x22 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Sub),
            0x23 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Subu),
            0x24 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.And),
            0x25 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Or),
            0x26 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Xor),
            0x27 => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Nor),
            0x2A => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Slt),
            0x2B => DecodeThreeRegisterArithmetic(encodedWord, R3000aOpcode.Sltu),
            _ => CreateReserved(encodedWord),
        };
    }

    private static R3000aInstruction DecodeRegimm(uint encodedWord)
    {
        var _selector = ExtractRt(encodedWord);
        return _selector switch
        {
            RegimmBranchLessThanZeroSelector => DecodeCompareWithZeroBranch(
                encodedWord, R3000aOpcode.Bltz, R3000aControlFlowKind.ConditionalBranch),
            RegimmBranchGreaterOrEqualZeroSelector => DecodeCompareWithZeroBranch(
                encodedWord, R3000aOpcode.Bgez, R3000aControlFlowKind.ConditionalBranch),
            RegimmBranchLessThanZeroAndLinkSelector => DecodeCompareWithZeroBranch(
                encodedWord, R3000aOpcode.Bltzal, R3000aControlFlowKind.LinkBranch),
            RegimmBranchGreaterOrEqualZeroAndLinkSelector => DecodeCompareWithZeroBranch(
                encodedWord, R3000aOpcode.Bgezal, R3000aControlFlowKind.LinkBranch),
            _ => CreateReserved(encodedWord),
        };
    }

    private static R3000aInstruction DecodeJump(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.J,
            R3000aOperand.CreateJumpIndex(ExtractJumpIndex(encodedWord)),
            default,
            default,
            operandCount: 1,
            R3000aControlFlowKind.JumpAbsolute,
            R3000aDelaySlotKind.Unconditional);
    }

    private static R3000aInstruction DecodeJumpAndLink(uint encodedWord)
    {
        return new R3000aInstruction(
            encodedWord,
            R3000aOpcode.Jal,
            R3000aInstructionFormat.J,
            R3000aOperand.CreateJumpIndex(ExtractJumpIndex(encodedWord)),
            default,
            default,
            operandCount: 1,
            R3000aControlFlowKind.LinkBranch,
            R3000aDelaySlotKind.Unconditional,
            linkInfo: R3000aLinkInfo.CreateRa());
    }

    private static R3000aInstruction DecodeThreeOperandBranch(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            R3000aOperand.CreateImmediate(ExtractImmediate(encodedWord)),
            operandCount: 3,
            R3000aControlFlowKind.ConditionalBranch,
            R3000aDelaySlotKind.Conditional);
    }

    private static R3000aInstruction DecodeTwoOperandBranch(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            R3000aOperand.CreateImmediate(ExtractImmediate(encodedWord)),
            default,
            operandCount: 2,
            R3000aControlFlowKind.ConditionalBranch,
            R3000aDelaySlotKind.Conditional);
    }

    private static R3000aInstruction DecodeCompareWithZeroBranch(
        uint encodedWord, R3000aOpcode opcode, R3000aControlFlowKind controlFlow)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.Regimm,
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            R3000aOperand.CreateImmediate(ExtractImmediate(encodedWord)),
            default,
            operandCount: 2,
            controlFlow,
            R3000aDelaySlotKind.Conditional,
            linkInfo: controlFlow == R3000aControlFlowKind.LinkBranch
                ? R3000aLinkInfo.CreateRa()
                : R3000aLinkInfo.None);
    }

    private static R3000aInstruction DecodeImmediateArithmetic(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            R3000aOperand.CreateImmediate(ExtractImmediate(encodedWord)),
            operandCount: 3);
    }

    private static R3000aInstruction DecodeLoadUpperImmediate(uint encodedWord)
    {
        return new R3000aInstruction(
            encodedWord,
            R3000aOpcode.Lui,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            R3000aOperand.CreateImmediate(ExtractImmediate(encodedWord)),
            default,
            operandCount: 2);
    }

    private static R3000aInstruction DecodeThreeRegisterArithmetic(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRd(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            operandCount: 3);
    }

    private static R3000aInstruction DecodeShiftByImmediate(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRd(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            R3000aOperand.CreateShamt((byte)ExtractShamt(encodedWord)),
            operandCount: 3);
    }

    private static R3000aInstruction DecodeShiftByRegister(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRd(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            operandCount: 3);
    }

    private static R3000aInstruction DecodeMultiplyDivide(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            default,
            operandCount: 2);
    }

    private static R3000aInstruction DecodeMoveFromHiLo(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRd(encodedWord)),
            default,
            default,
            operandCount: 1);
    }

    private static R3000aInstruction DecodeMoveToHiLo(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            default,
            default,
            operandCount: 1);
    }

    private static R3000aInstruction DecodeJumpRegister(uint encodedWord)
    {
        return new R3000aInstruction(
            encodedWord,
            R3000aOpcode.Jr,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            default,
            default,
            operandCount: 1,
            R3000aControlFlowKind.JumpRegister,
            R3000aDelaySlotKind.Unconditional);
    }

    private static R3000aInstruction DecodeJumpAndLinkRegister(uint encodedWord)
    {
        var _rd = ExtractRd(encodedWord);

        return new R3000aInstruction(
            encodedWord,
            R3000aOpcode.Jalr,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(_rd),
            R3000aOperand.CreateRegister(ExtractRs(encodedWord)),
            default,
            operandCount: 2,
            R3000aControlFlowKind.JumpRegister,
            R3000aDelaySlotKind.Unconditional,
            linkInfo: R3000aLinkInfo.Create((byte)_rd));
    }

    private static R3000aInstruction DecodeTrap(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.R,
            default,
            default,
            default,
            operandCount: 0,
            R3000aControlFlowKind.Trap);
    }

    private static R3000aInstruction DecodeLoad(uint encodedWord, R3000aOpcode opcode, bool pairSpecial)
    {
        var _targetRegister = ExtractRt(encodedWord);
        var _loadDelayInfo = pairSpecial
            ? R3000aLoadDelayInfo.CreateLwlLwrPair(_targetRegister)
            : R3000aLoadDelayInfo.Create(_targetRegister);

        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(_targetRegister),
            R3000aOperand.CreateMemoryOffset(ExtractRs(encodedWord), ExtractImmediate(encodedWord)),
            default,
            operandCount: 2,
            loadDelayInfo: _loadDelayInfo);
    }

    private static R3000aInstruction DecodeMemoryAccess(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(ExtractRt(encodedWord)),
            R3000aOperand.CreateMemoryOffset(ExtractRs(encodedWord), ExtractImmediate(encodedWord)),
            default,
            operandCount: 2);
    }

    private static R3000aInstruction DecodeCoprocessorZero(uint encodedWord)
    {
        var _selector = ExtractRs(encodedWord);
        if (_selector != MoveFromCoprocessorSelector
            && _selector != MoveToCoprocessorSelector
            && !(_selector == ReturnFromExceptionSelector && ExtractFunct(encodedWord) == ReturnFromExceptionFunct))
        {
            return CreateReserved(encodedWord);
        }

        var _opcode = _selector switch
        {
            MoveFromCoprocessorSelector => R3000aOpcode.Mfc0,
            MoveToCoprocessorSelector => R3000aOpcode.Mtc0,
            _ => R3000aOpcode.Rfe,
        };

        var _copInfo = _selector switch
        {
            MoveFromCoprocessorSelector => R3000aCopInfo.CreateMoveFromCoprocessor(CoprocessorZeroId, ExtractRd(encodedWord)),
            MoveToCoprocessorSelector => R3000aCopInfo.CreateMoveToCoprocessor(CoprocessorZeroId, ExtractRd(encodedWord)),
            _ => R3000aCopInfo.CreateReturnFromException(),
        };

        return CreateCoprocessorInstruction(encodedWord, _opcode, _copInfo);
    }

    private static R3000aInstruction DecodeUnusableCoprocessor(uint encodedWord, R3000aOpcode opcode)
    {
        return CreateCoprocessorInstruction(encodedWord, opcode, R3000aCopInfo.None);
    }

    private static R3000aInstruction DecodeCoprocessorTwo(uint encodedWord)
    {
        var _selector = ExtractRs(encodedWord);
        if ((_selector & CoprocessorOperationSelectorMask) != 0)
        {
            return CreateCoprocessorInstruction(
                encodedWord,
                R3000aOpcode.Cop2Command,
                R3000aCopInfo.CreateExecuteCommand(CoprocessorTwoId, ExtractCoFun(encodedWord)));
        }

        var _copInfo = _selector switch
        {
            MoveFromCoprocessorSelector => R3000aCopInfo.CreateMoveFromCoprocessor(CoprocessorTwoId, ExtractRd(encodedWord)),
            MoveControlFromCoprocessorSelector => R3000aCopInfo.CreateMoveControlFromCoprocessor(CoprocessorTwoId, ExtractRd(encodedWord)),
            MoveToCoprocessorSelector => R3000aCopInfo.CreateMoveToCoprocessor(CoprocessorTwoId, ExtractRd(encodedWord)),
            MoveControlToCoprocessorSelector => R3000aCopInfo.CreateMoveControlToCoprocessor(CoprocessorTwoId, ExtractRd(encodedWord)),
            _ => (R3000aCopInfo?)null,
        };

        if (_copInfo is null)
        {
            return CreateReserved(encodedWord);
        }

        return CreateCoprocessorInstruction(encodedWord, R3000aOpcode.Cop2Command, _copInfo.Value);
    }

    private static R3000aInstruction DecodeCoprocessorDataTransfer(uint encodedWord, R3000aOpcode opcode)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateCopReg(CoprocessorTwoId, ExtractRt(encodedWord)),
            R3000aOperand.CreateMemoryOffset(ExtractRs(encodedWord), ExtractImmediate(encodedWord)),
            default,
            operandCount: 2);
    }

    private static R3000aInstruction CreateCoprocessorInstruction(
        uint encodedWord, R3000aOpcode opcode, R3000aCopInfo copInfo)
    {
        return new R3000aInstruction(
            encodedWord,
            opcode,
            R3000aInstructionFormat.Cop,
            default,
            default,
            default,
            operandCount: 0,
            R3000aControlFlowKind.Coprocessor,
            copInfo: copInfo);
    }

    private static R3000aInstruction CreateReserved(uint encodedWord)
    {
        return new R3000aInstruction(
            encodedWord,
            R3000aOpcode.Reserved,
            R3000aInstructionFormat.None,
            default,
            default,
            default,
            operandCount: 0,
            R3000aControlFlowKind.Reserved);
    }

    private static uint ExtractOpcode(uint encodedWord) => encodedWord >> OpcodeShift;

    private static byte ExtractRs(uint encodedWord) => (byte)((encodedWord >> RsShift) & FiveBitFieldMask);

    private static byte ExtractRt(uint encodedWord) => (byte)((encodedWord >> RtShift) & FiveBitFieldMask);

    private static byte ExtractRd(uint encodedWord) => (byte)((encodedWord >> RdShift) & FiveBitFieldMask);

    private static uint ExtractShamt(uint encodedWord) => (encodedWord >> ShamtShift) & FiveBitFieldMask;

    private static uint ExtractFunct(uint encodedWord) => encodedWord & SixBitFieldMask;

    private static ushort ExtractImmediate(uint encodedWord) => (ushort)(encodedWord & HalfwordFieldMask);

    private static uint ExtractJumpIndex(uint encodedWord) => encodedWord & JumpIndexFieldMask;

    private static uint ExtractCoFun(uint encodedWord) => encodedWord & CoFunFieldMask;
}
