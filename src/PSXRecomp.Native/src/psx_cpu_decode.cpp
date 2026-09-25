// PSXCpu instruction decode / dispatch. Moved verbatim out of psx_cpu.cpp
// (Issue #524); owned by the decode Rust migration slice (#525).

#include "psx_cpu.h"
#include <cstdint>

void PSXCpu::ExecuteInstruction(uint32_t instruction, PSXMemory& memory) {
    uint32_t opcode = instruction >> 26;
    
    switch (opcode) {
        case 0x00: { // SPECIAL
            uint32_t funct = instruction & 0x3F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rd = (instruction >> 11) & 0x1F;
            uint32_t shamt = (instruction >> 6) & 0x1F;
            
            switch (funct) {
                case 0x20: ExecAdd(rd, rs, rt); break;
                case 0x21: ExecAddu(rd, rs, rt); break;
                case 0x22: ExecSub(rd, rs, rt); break;
                case 0x23: ExecSubu(rd, rs, rt); break;
                case 0x24: ExecAnd(rd, rs, rt); break;
                case 0x25: ExecOr(rd, rs, rt); break;
                case 0x26: ExecXor(rd, rs, rt); break;
                case 0x27: ExecNor(rd, rs, rt); break;
                case 0x2A: ExecSlt(rd, rs, rt); break;
                case 0x2B: ExecSltu(rd, rs, rt); break;
                case 0x00: ExecSll(rd, rt, shamt); break;
                case 0x02: ExecSrl(rd, rt, shamt); break;
                case 0x03: ExecSra(rd, rt, shamt); break;
                case 0x04: ExecSllv(rd, rt, rs); break;
                case 0x06: ExecSrlv(rd, rt, rs); break;
                case 0x07: ExecSrav(rd, rt, rs); break;
                case 0x18: ExecMult(rs, rt); break;
                case 0x19: ExecMultu(rs, rt); break;
                case 0x1A: ExecDiv(rs, rt); break;
                case 0x1B: ExecDivu(rs, rt); break;
                case 0x10: ExecMfhi(rd); break;
                case 0x11: ExecMthi(rs); break;
                case 0x12: ExecMflo(rd); break;
                case 0x13: ExecMtlo(rs); break;
                case 0x08: ExecJr(rs); break;
                case 0x09: ExecJalr(rd, rs); break;
                case 0x0C: ExecSyscall(); break;
                case 0x0D: ExecBreak(); break;
                default: RaiseException(0x0A); break; // RI: reserved SPECIAL funct
            }
            break;
        }
        case 0x01: { // REGIMM
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint32_t rt = (instruction >> 16) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            
            switch (rt) {
                case 0x00: ExecBltz(rs, offset); break;
                case 0x01: ExecBgez(rs, offset); break;
                case 0x10: ExecBltzal(rs, offset); break;
                case 0x11: ExecBgezal(rs, offset); break;
                default: RaiseException(0x0A); break; // RI: reserved REGIMM rt
            }
            break;
        }
        case 0x02: { // J
            uint32_t target = instruction & 0x03FFFFFF;
            ExecJ(target);
            break;
        }
        case 0x03: { // JAL
            uint32_t target = instruction & 0x03FFFFFF;
            ExecJal(target);
            break;
        }
        case 0x04: { // BEQ
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint32_t rt = (instruction >> 16) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecBeq(rs, rt, offset);
            break;
        }
        case 0x05: { // BNE
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint32_t rt = (instruction >> 16) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecBne(rs, rt, offset);
            break;
        }
        case 0x06: { // BLEZ
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecBlez(rs, offset);
            break;
        }
        case 0x07: { // BGTZ
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecBgtz(rs, offset);
            break;
        }
        case 0x08: { // ADDI
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t imm = static_cast<int16_t>(instruction & 0xFFFF);
            ExecAddi(rt, rs, imm);
            break;
        }
        case 0x09: { // ADDIU
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t imm = static_cast<int16_t>(instruction & 0xFFFF);
            ExecAddiu(rt, rs, imm);
            break;
        }
        case 0x0A: { // SLTI
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t imm = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSlti(rt, rs, imm);
            break;
        }
        case 0x0B: { // SLTIU
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t imm = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSltiu(rt, rs, imm);
            break;
        }
        case 0x0C: { // ANDI
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint16_t imm = static_cast<uint16_t>(instruction & 0xFFFF);
            ExecAndi(rt, rs, imm);
            break;
        }
        case 0x0D: { // ORI
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint16_t imm = static_cast<uint16_t>(instruction & 0xFFFF);
            ExecOri(rt, rs, imm);
            break;
        }
        case 0x0E: { // XORI
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint16_t imm = static_cast<uint16_t>(instruction & 0xFFFF);
            ExecXori(rt, rs, imm);
            break;
        }
        case 0x0F: { // LUI
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint16_t imm = static_cast<uint16_t>(instruction & 0xFFFF);
            ExecLui(rt, imm);
            break;
        }
        case 0x20: { // LB
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLb(rt, rs, offset, memory);
            break;
        }
        case 0x21: { // LH
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLh(rt, rs, offset, memory);
            break;
        }
        case 0x23: { // LW
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLw(rt, rs, offset, memory);
            break;
        }
        case 0x22: { // LWL
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLwl(rt, rs, offset, memory);
            break;
        }
        case 0x24: { // LBU
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLbu(rt, rs, offset, memory);
            break;
        }
        case 0x25: { // LHU
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLhu(rt, rs, offset, memory);
            break;
        }
        case 0x26: { // LWR
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecLwr(rt, rs, offset, memory);
            break;
        }
        case 0x28: { // SB
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSb(rt, rs, offset, memory);
            break;
        }
        case 0x29: { // SH
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSh(rt, rs, offset, memory);
            break;
        }
        case 0x2B: { // SW
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSw(rt, rs, offset, memory);
            break;
        }
        case 0x2A: { // SWL
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSwl(rt, rs, offset, memory);
            break;
        }
        case 0x2E: { // SWR
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rs = (instruction >> 21) & 0x1F;
            int16_t offset = static_cast<int16_t>(instruction & 0xFFFF);
            ExecSwr(rt, rs, offset, memory);
            break;
        }
        case 0x10: { // COP0
            uint32_t rs = (instruction >> 21) & 0x1F;
            uint32_t rt = (instruction >> 16) & 0x1F;
            uint32_t rd = (instruction >> 11) & 0x1F;
            uint32_t funct = instruction & 0x3F;
            
            if (rs == 0x00) { // MFC0
                ExecMfc0(rt, rd);
            } else if (rs == 0x04) { // MTC0
                ExecMtc0(rt, rd);
            } else if (rs == 0x10 && funct == 0x10) { // RFE
                ExecRfe();
            } else {
                // COP0 itself is usable (CU0 is implicitly set in kernel mode on
                // the PSX), so an unrecognised COP0 form is a *reserved
                // instruction*, not a coprocessor-unusable one: CFC0/CTC0 have no
                // control registers to address on the R3000A, and the TLB forms
                // (TLBR/TLBWI/TLBP/TLBWR) address a TLB the PSX does not have.
                // RI is therefore the architecturally correct code here, not CpU.
                RaiseException(0x0A); // RI
            }
            break;
        }
        // Coprocessors 1 (FPU), 2 (GTE) and 3 are not implemented, so every
        // access to them is unusable: CpU with CAUSE.CE = the coprocessor number
        // (docs/cpu/cop0.md, CAUSE bits 28-29 = opcode bits 26-27). This covers
        // COPz, LWCz and SWCz alike; for all three families the coprocessor
        // number is the low two bits of the opcode. LWC0/SWC0 (0x30/0x38) are
        // deliberately absent: COP0 has no load/store forms on the R3000A, so
        // they fall through to RI below. GTE *command* execution stays
        // unimplemented (Issue #377) -- this only makes it fault loudly.
        case 0x11: // COP1
        case 0x12: // COP2 (GTE)
        case 0x13: // COP3
        case 0x31: // LWC1
        case 0x32: // LWC2
        case 0x33: // LWC3
        case 0x39: // SWC1
        case 0x3A: // SWC2
        case 0x3B: // SWC3
            RaiseException(0x0B, opcode & 3u); // CpU
            break;
        default:
            RaiseException(0x0A); // RI: reserved/undefined opcode
            break;
    }
}
