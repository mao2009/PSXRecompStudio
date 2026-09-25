// PSXCpu instruction dispatch. The opcode/funct/REGIMM/COP0 classification and
// operand-field extraction are done in Rust (psx_cpu_decode, Issue #525); this
// file only maps the classification onto the Exec* handlers and raises RI/CpU.

#include "psx_cpu.h"
#include "psx_cpu_decode.h"
#include <cstdint>

void PSXCpu::ExecuteInstruction(uint32_t instruction, PSXMemory& memory) {
    const PSXDecodedInstruction d = psx_cpu_decode(instruction);
    const uint32_t rs = d.rs;
    const uint32_t rt = d.rt;
    const uint32_t rd = d.rd;
    const int16_t simm = static_cast<int16_t>(d.imm);
    const uint16_t uimm = static_cast<uint16_t>(d.imm);

    switch (d.op) {
        // SPECIAL
        case PSXDecodeOp::Add: ExecAdd(rd, rs, rt); break;
        case PSXDecodeOp::Addu: ExecAddu(rd, rs, rt); break;
        case PSXDecodeOp::Sub: ExecSub(rd, rs, rt); break;
        case PSXDecodeOp::Subu: ExecSubu(rd, rs, rt); break;
        case PSXDecodeOp::And: ExecAnd(rd, rs, rt); break;
        case PSXDecodeOp::Or: ExecOr(rd, rs, rt); break;
        case PSXDecodeOp::Xor: ExecXor(rd, rs, rt); break;
        case PSXDecodeOp::Nor: ExecNor(rd, rs, rt); break;
        case PSXDecodeOp::Slt: ExecSlt(rd, rs, rt); break;
        case PSXDecodeOp::Sltu: ExecSltu(rd, rs, rt); break;
        case PSXDecodeOp::Sll: ExecSll(rd, rt, d.shamt); break;
        case PSXDecodeOp::Srl: ExecSrl(rd, rt, d.shamt); break;
        case PSXDecodeOp::Sra: ExecSra(rd, rt, d.shamt); break;
        case PSXDecodeOp::Sllv: ExecSllv(rd, rt, rs); break;
        case PSXDecodeOp::Srlv: ExecSrlv(rd, rt, rs); break;
        case PSXDecodeOp::Srav: ExecSrav(rd, rt, rs); break;
        case PSXDecodeOp::Mult: ExecMult(rs, rt); break;
        case PSXDecodeOp::Multu: ExecMultu(rs, rt); break;
        case PSXDecodeOp::Div: ExecDiv(rs, rt); break;
        case PSXDecodeOp::Divu: ExecDivu(rs, rt); break;
        case PSXDecodeOp::Mfhi: ExecMfhi(rd); break;
        case PSXDecodeOp::Mthi: ExecMthi(rs); break;
        case PSXDecodeOp::Mflo: ExecMflo(rd); break;
        case PSXDecodeOp::Mtlo: ExecMtlo(rs); break;
        case PSXDecodeOp::Jr: ExecJr(rs); break;
        case PSXDecodeOp::Jalr: ExecJalr(rd, rs); break;
        case PSXDecodeOp::Syscall: ExecSyscall(); break;
        case PSXDecodeOp::Break: ExecBreak(); break;
        // REGIMM
        case PSXDecodeOp::Bltz: ExecBltz(rs, simm); break;
        case PSXDecodeOp::Bgez: ExecBgez(rs, simm); break;
        case PSXDecodeOp::Bltzal: ExecBltzal(rs, simm); break;
        case PSXDecodeOp::Bgezal: ExecBgezal(rs, simm); break;
        // Jumps / branches
        case PSXDecodeOp::J: ExecJ(d.target); break;
        case PSXDecodeOp::Jal: ExecJal(d.target); break;
        case PSXDecodeOp::Beq: ExecBeq(rs, rt, simm); break;
        case PSXDecodeOp::Bne: ExecBne(rs, rt, simm); break;
        case PSXDecodeOp::Blez: ExecBlez(rs, simm); break;
        case PSXDecodeOp::Bgtz: ExecBgtz(rs, simm); break;
        // Immediate ALU
        case PSXDecodeOp::Addi: ExecAddi(rt, rs, simm); break;
        case PSXDecodeOp::Addiu: ExecAddiu(rt, rs, simm); break;
        case PSXDecodeOp::Slti: ExecSlti(rt, rs, simm); break;
        case PSXDecodeOp::Sltiu: ExecSltiu(rt, rs, simm); break;
        case PSXDecodeOp::Andi: ExecAndi(rt, rs, uimm); break;
        case PSXDecodeOp::Ori: ExecOri(rt, rs, uimm); break;
        case PSXDecodeOp::Xori: ExecXori(rt, rs, uimm); break;
        case PSXDecodeOp::Lui: ExecLui(rt, uimm); break;
        // Loads / stores
        case PSXDecodeOp::Lb: ExecLb(rt, rs, simm, memory); break;
        case PSXDecodeOp::Lh: ExecLh(rt, rs, simm, memory); break;
        case PSXDecodeOp::Lwl: ExecLwl(rt, rs, simm, memory); break;
        case PSXDecodeOp::Lw: ExecLw(rt, rs, simm, memory); break;
        case PSXDecodeOp::Lbu: ExecLbu(rt, rs, simm, memory); break;
        case PSXDecodeOp::Lhu: ExecLhu(rt, rs, simm, memory); break;
        case PSXDecodeOp::Lwr: ExecLwr(rt, rs, simm, memory); break;
        case PSXDecodeOp::Sb: ExecSb(rt, rs, simm, memory); break;
        case PSXDecodeOp::Sh: ExecSh(rt, rs, simm, memory); break;
        case PSXDecodeOp::Swl: ExecSwl(rt, rs, simm, memory); break;
        case PSXDecodeOp::Sw: ExecSw(rt, rs, simm, memory); break;
        case PSXDecodeOp::Swr: ExecSwr(rt, rs, simm, memory); break;
        // COP0
        case PSXDecodeOp::Mfc0: ExecMfc0(rt, rd); break;
        case PSXDecodeOp::Mtc0: ExecMtc0(rt, rd); break;
        case PSXDecodeOp::Rfe: ExecRfe(); break;
        // COP1/2/3 and LWC1-3/SWC1-3 are unusable (docs/cpu/cop0.md, CAUSE.CE
        // = coprocessor number). GTE command execution stays unimplemented
        // (Issue #377) -- this only makes it fault loudly.
        case PSXDecodeOp::CopUnusable: RaiseException(0x0B, d.cop); break; // CpU
        // Reserved encodings, including unrecognised COP0 forms (COP0 is
        // usable, so CFC0/CTC0/TLB ops are RI, not CpU) and LWC0/SWC0. Any
        // value outside the enum also fails closed here.
        case PSXDecodeOp::Reserved:
        default: RaiseException(0x0A); break; // RI
    }
}
