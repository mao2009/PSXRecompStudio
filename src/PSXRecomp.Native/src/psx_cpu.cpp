#include "psx_cpu.h"
#include "psx_memory.h"
#include <cstdint>

PSXCpu::PSXCpu() {
    Reset();
}

void PSXCpu::Reset() {
    for (int i = 0; i < PSX_GPR_COUNT; i++) {
        gpr_[i] = 0;
    }
    pc_ = 0;
    next_pc_ = 4;
    delay_slot_pc_ = 0;
    hi_ = 0;
    lo_ = 0;
    branch_pending_ = false;
    branch_issued_ = false;
    pending_branch_target_ = 0;
    pending_branch_taken_ = false;
    load_delay_reg_ = -1;
    load_delay_value_ = 0;
    next_load_delay_reg_ = -1;
    next_load_delay_value_ = 0;
}

uint32_t PSXCpu::GetGPR(int index) const {
    if (index < 0 || index >= PSX_GPR_COUNT) return 0;
    if (index == 0) return 0;
    return gpr_[index];
}

void PSXCpu::SetGPR(int index, uint32_t value) {
    if (index < 0 || index >= PSX_GPR_COUNT) return;
    if (index == 0) return;
    gpr_[index] = value;
    // An immediate write beats a pending load-delay write to the same register.
    // The R3000A writes the register in-order, so the later (immediate) write wins.
    if (index == load_delay_reg_) {
        load_delay_reg_ = -1;
    }
    if (index == next_load_delay_reg_) {
        next_load_delay_reg_ = -1;
    }
}

uint32_t PSXCpu::GetPC() const { return pc_; }
void PSXCpu::SetPC(uint32_t value) {
    pc_ = value;
    FlushPipeline();
}

uint32_t PSXCpu::GetHI() const { return hi_; }
void PSXCpu::SetHI(uint32_t value) { hi_ = value; }

uint32_t PSXCpu::GetLO() const { return lo_; }
void PSXCpu::SetLO(uint32_t value) { lo_ = value; }

uint32_t PSXCpu::FetchInstruction(PSXMemory& memory) {
    uint32_t addr = pc_;
    uint8_t* ram = memory.GetRAM();
    uint32_t instruction = 
        (static_cast<uint32_t>(ram[addr]) << 24) |
        (static_cast<uint32_t>(ram[addr + 1]) << 16) |
        (static_cast<uint32_t>(ram[addr + 2]) << 8) |
        static_cast<uint32_t>(ram[addr + 3]);
    return instruction;
}

void PSXCpu::FlushPipeline() {
    // Commit any pending load result immediately and clear all pending state.
    if (load_delay_reg_ >= 0) {
        gpr_[load_delay_reg_] = load_delay_value_;
    }
    load_delay_reg_ = -1;
    next_load_delay_reg_ = -1;

    branch_pending_ = false;
    branch_issued_ = false;
    next_pc_ = pc_ + 4;
}

void PSXCpu::UpdateLoadDelay() {
    // Commit the value loaded one instruction ago (the delay-slot instruction has
    // already read the old value), then shift the queued load into place. Writing
    // the register in-order ensures an immediate write in the delay slot wins.
    if (load_delay_reg_ >= 0) {
        gpr_[load_delay_reg_] = load_delay_value_;
    }
    load_delay_reg_ = next_load_delay_reg_;
    load_delay_value_ = next_load_delay_value_;
    next_load_delay_reg_ = -1;
    next_load_delay_value_ = 0;
}

void PSXCpu::WriteRegDelayed(int index, uint32_t value) {
    if (index < 0 || index >= PSX_GPR_COUNT) return;
    if (index == 0) return;
    // Double load delays to the same register: the last load wins.
    if (index == load_delay_reg_) {
        load_delay_reg_ = -1;
    }
    next_load_delay_reg_ = index;
    next_load_delay_value_ = value;
}

void PSXCpu::SetPendingBranch(uint32_t target, bool taken) {
    if (!branch_pending_) {
        // Primary branch: record the pending control transfer. The delay slot is
        // executed before this target is applied (ADR-005).
        pending_branch_target_ = target;
        pending_branch_taken_ = taken;
    }
    // Branch in a delay slot: the inner branch executes (and consumes its own
    // delay slot) but its target is ignored; the outer branch is applied instead
    // (docs/cpu/pipeline.md, branch-in-delay-slot).
    branch_issued_ = true;
}

int PSXCpu::Step(PSXMemory& memory) {
    uint32_t instr_addr = pc_;
    uint32_t instruction = FetchInstruction(memory);

    bool in_delay_slot = branch_pending_;
    branch_issued_ = false;

    ExecuteInstruction(instruction, memory);

    if (in_delay_slot) {
        // This instruction is the delay slot of a pending branch.
        if (branch_issued_) {
            // Branch in a delay slot: the inner branch executes (and consumes its
            // own delay slot) but its target is ignored; the outer branch applies
            // afterwards (docs/cpu/pipeline.md). Track the shared delay slot.
            delay_slot_pc_ = instr_addr + 4;
            pc_ = instr_addr + 4;
        } else {
            // Apply the completed (outermost) branch (ADR-005).
            if (pending_branch_taken_) {
                pc_ = pending_branch_target_;
            } else {
                pc_ = delay_slot_pc_ + 4;
            }
            branch_pending_ = false;
        }
    } else if (branch_issued_) {
        // A branch/jump just executed: the next instruction is its delay slot.
        delay_slot_pc_ = instr_addr + 4;
        branch_pending_ = true;
        pc_ = instr_addr + 4;
    } else {
        pc_ = instr_addr + 4;
    }

    next_pc_ = pc_ + 4;
    UpdateLoadDelay();
    return 0;
}

int PSXCpu::Run(PSXMemory& memory, uint32_t maxInstructions) {
    for (uint32_t i = 0; i < maxInstructions; i++) {
        int result = Step(memory);
        if (result != 0) {
            return result;
        }
    }
    return 0;
}

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
                default: break; // Reserved/Unimplemented
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
                default: break;
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
            }
            break;
        }
        default:
            break; // Reserved/Unimplemented
    }
}

// Arithmetic/Logical
void PSXCpu::ExecAdd(uint32_t rd, uint32_t rs, uint32_t rt) {
    int32_t result = ToSigned(gpr_[rs]) + ToSigned(gpr_[rt]);
    SetGPR(rd, static_cast<uint32_t>(result));
}

void PSXCpu::ExecAddu(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, gpr_[rs] + gpr_[rt]);
}

void PSXCpu::ExecSub(uint32_t rd, uint32_t rs, uint32_t rt) {
    int32_t result = ToSigned(gpr_[rs]) - ToSigned(gpr_[rt]);
    SetGPR(rd, static_cast<uint32_t>(result));
}

void PSXCpu::ExecSubu(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, gpr_[rs] - gpr_[rt]);
}

void PSXCpu::ExecAnd(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, gpr_[rs] & gpr_[rt]);
}

void PSXCpu::ExecOr(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, gpr_[rs] | gpr_[rt]);
}

void PSXCpu::ExecXor(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, gpr_[rs] ^ gpr_[rt]);
}

void PSXCpu::ExecNor(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, ~(gpr_[rs] | gpr_[rt]));
}

void PSXCpu::ExecSlt(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, ToSigned(gpr_[rs]) < ToSigned(gpr_[rt]) ? 1 : 0);
}

void PSXCpu::ExecSltu(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, gpr_[rs] < gpr_[rt] ? 1 : 0);
}

// Immediate arithmetic
void PSXCpu::ExecAddi(uint32_t rt, uint32_t rs, int16_t imm) {
    int32_t result = ToSigned(gpr_[rs]) + imm;
    SetGPR(rt, static_cast<uint32_t>(result));
}

void PSXCpu::ExecAddiu(uint32_t rt, uint32_t rs, int16_t imm) {
    SetGPR(rt, gpr_[rs] + SignExtend16(imm));
}

void PSXCpu::ExecAndi(uint32_t rt, uint32_t rs, uint16_t imm) {
    SetGPR(rt, gpr_[rs] & ZeroExtend16(imm));
}

void PSXCpu::ExecOri(uint32_t rt, uint32_t rs, uint16_t imm) {
    SetGPR(rt, gpr_[rs] | ZeroExtend16(imm));
}

void PSXCpu::ExecXori(uint32_t rt, uint32_t rs, uint16_t imm) {
    SetGPR(rt, gpr_[rs] ^ ZeroExtend16(imm));
}

void PSXCpu::ExecLui(uint32_t rt, uint16_t imm) {
    SetGPR(rt, static_cast<uint32_t>(imm) << 16);
}

void PSXCpu::ExecSlti(uint32_t rt, uint32_t rs, int16_t imm) {
    SetGPR(rt, ToSigned(gpr_[rs]) < imm ? 1 : 0);
}

void PSXCpu::ExecSltiu(uint32_t rt, uint32_t rs, int16_t imm) {
    SetGPR(rt, gpr_[rs] < ZeroExtend16(imm) ? 1 : 0);
}

// Shift
void PSXCpu::ExecSll(uint32_t rd, uint32_t rt, uint32_t shamt) {
    SetGPR(rd, gpr_[rt] << shamt);
}

void PSXCpu::ExecSrl(uint32_t rd, uint32_t rt, uint32_t shamt) {
    SetGPR(rd, gpr_[rt] >> shamt);
}

void PSXCpu::ExecSra(uint32_t rd, uint32_t rt, uint32_t shamt) {
    SetGPR(rd, static_cast<uint32_t>(static_cast<int32_t>(gpr_[rt]) >> shamt));
}

void PSXCpu::ExecSllv(uint32_t rd, uint32_t rt, uint32_t rs) {
    SetGPR(rd, gpr_[rt] << (gpr_[rs] & 0x1F));
}

void PSXCpu::ExecSrlv(uint32_t rd, uint32_t rt, uint32_t rs) {
    SetGPR(rd, gpr_[rt] >> (gpr_[rs] & 0x1F));
}

void PSXCpu::ExecSrav(uint32_t rd, uint32_t rt, uint32_t rs) {
    SetGPR(rd, static_cast<uint32_t>(static_cast<int32_t>(gpr_[rt]) >> (gpr_[rs] & 0x1F)));
}

// Multiply/Divide
void PSXCpu::ExecMult(uint32_t rs, uint32_t rt) {
    int64_t result = static_cast<int64_t>(ToSigned(gpr_[rs])) * static_cast<int64_t>(ToSigned(gpr_[rt]));
    hi_ = static_cast<uint32_t>(result >> 32);
    lo_ = static_cast<uint32_t>(result & 0xFFFFFFFF);
}

void PSXCpu::ExecMultu(uint32_t rs, uint32_t rt) {
    uint64_t result = static_cast<uint64_t>(gpr_[rs]) * static_cast<uint64_t>(gpr_[rt]);
    hi_ = static_cast<uint32_t>(result >> 32);
    lo_ = static_cast<uint32_t>(result & 0xFFFFFFFF);
}

void PSXCpu::ExecDiv(uint32_t rs, uint32_t rt) {
    int32_t dividend = ToSigned(gpr_[rs]);
    int32_t divisor = ToSigned(gpr_[rt]);
    if (divisor == 0) {
        // PS1-specific: division by zero
        if (dividend >= 0) {
            lo_ = 0xFFFFFFFF;
        } else {
            lo_ = 1;
        }
        hi_ = static_cast<uint32_t>(dividend);
    } else if (dividend == static_cast<int32_t>(0x80000000) && divisor == -1) {
        // PS1-specific: overflow
        lo_ = 0x80000000;
        hi_ = 0;
    } else {
        lo_ = static_cast<uint32_t>(dividend / divisor);
        hi_ = static_cast<uint32_t>(dividend % divisor);
    }
}

void PSXCpu::ExecDivu(uint32_t rs, uint32_t rt) {
    uint32_t dividend = gpr_[rs];
    uint32_t divisor = gpr_[rt];
    if (divisor == 0) {
        // PS1-specific: division by zero
        lo_ = 0xFFFFFFFF;
        hi_ = dividend;
    } else {
        lo_ = dividend / divisor;
        hi_ = dividend % divisor;
    }
}

void PSXCpu::ExecMfhi(uint32_t rd) {
    SetGPR(rd, hi_);
}

void PSXCpu::ExecMflo(uint32_t rd) {
    SetGPR(rd, lo_);
}

void PSXCpu::ExecMthi(uint32_t rs) {
    hi_ = gpr_[rs];
}

void PSXCpu::ExecMtlo(uint32_t rs) {
    lo_ = gpr_[rs];
}

// Memory
void PSXCpu::ExecLb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    int8_t value = static_cast<int8_t>(ram[addr]);
    WriteRegDelayed(rt, SignExtend16(value));
}

void PSXCpu::ExecLbu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    WriteRegDelayed(rt, ram[addr]);
}

void PSXCpu::ExecLh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    int16_t value = static_cast<int16_t>((ram[addr] << 8) | ram[addr + 1]);
    WriteRegDelayed(rt, SignExtend16(value));
}

void PSXCpu::ExecLhu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    uint16_t value = (ram[addr] << 8) | ram[addr + 1];
    WriteRegDelayed(rt, value);
}

void PSXCpu::ExecLw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    uint32_t value = (ram[addr] << 24) | (ram[addr + 1] << 16) | (ram[addr + 2] << 8) | ram[addr + 3];
    WriteRegDelayed(rt, value);
}

void PSXCpu::ExecSb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    ram[addr] = static_cast<uint8_t>(gpr_[rt] & 0xFF);
}

void PSXCpu::ExecSh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    ram[addr] = static_cast<uint8_t>(gpr_[rt] >> 8);
    ram[addr + 1] = static_cast<uint8_t>(gpr_[rt] & 0xFF);
}

void PSXCpu::ExecSw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    ram[addr] = static_cast<uint8_t>(gpr_[rt] >> 24);
    ram[addr + 1] = static_cast<uint8_t>(gpr_[rt] >> 16);
    ram[addr + 2] = static_cast<uint8_t>(gpr_[rt] >> 8);
    ram[addr + 3] = static_cast<uint8_t>(gpr_[rt] & 0xFF);
}

// Load Word Left - unaligned load, left bytes
void PSXCpu::ExecLwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    uint32_t reg = gpr_[rt];
    uint32_t shift = (addr & 3) * 8;
    uint32_t mem_val = (ram[addr - (addr & 3)] << 24) |
                       (ram[addr - (addr & 3) + 1] << 16) |
                       (ram[addr - (addr & 3) + 2] << 8) |
                       ram[addr - (addr & 3) + 3];
    uint32_t mask = 0xFFFFFFFF << shift;
    WriteRegDelayed(rt, (mem_val << shift) | (reg & ~mask));
}

// Load Word Right - unaligned load, right bytes
void PSXCpu::ExecLwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    uint32_t reg = gpr_[rt];
    uint32_t shift = (3 - (addr & 3)) * 8;
    uint32_t mem_val = (ram[addr - (addr & 3)] << 24) |
                       (ram[addr - (addr & 3) + 1] << 16) |
                       (ram[addr - (addr & 3) + 2] << 8) |
                       ram[addr - (addr & 3) + 3];
    uint32_t mask = 0xFFFFFFFF >> shift;
    WriteRegDelayed(rt, (reg & ~mask) | (mem_val >> shift));
}

// Store Word Left - unaligned store, left bytes
void PSXCpu::ExecSwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    uint32_t reg = gpr_[rt];
    uint32_t shift = (addr & 3) * 8;
    uint32_t mem_val = (ram[addr - (addr & 3)] << 24) |
                       (ram[addr - (addr & 3) + 1] << 16) |
                       (ram[addr - (addr & 3) + 2] << 8) |
                       ram[addr - (addr & 3) + 3];
    uint32_t mask = 0xFFFFFFFF >> shift;
    uint32_t result = (reg >> shift) | (mem_val & ~mask);
    ram[addr - (addr & 3)] = static_cast<uint8_t>(result >> 24);
    ram[addr - (addr & 3) + 1] = static_cast<uint8_t>(result >> 16);
    ram[addr - (addr & 3) + 2] = static_cast<uint8_t>(result >> 8);
    ram[addr - (addr & 3) + 3] = static_cast<uint8_t>(result & 0xFF);
}

// Store Word Right - unaligned store, right bytes
void PSXCpu::ExecSwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory) {
    uint32_t addr = gpr_[rs] + SignExtend16(offset);
    uint8_t* ram = memory.GetRAM();
    uint32_t reg = gpr_[rt];
    uint32_t shift = (3 - (addr & 3)) * 8;
    uint32_t mem_val = (ram[addr - (addr & 3)] << 24) |
                       (ram[addr - (addr & 3) + 1] << 16) |
                       (ram[addr - (addr & 3) + 2] << 8) |
                       ram[addr - (addr & 3) + 3];
    uint32_t mask = 0xFFFFFFFF << shift;
    uint32_t result = (mem_val & ~mask) | (reg << shift);
    ram[addr - (addr & 3)] = static_cast<uint8_t>(result >> 24);
    ram[addr - (addr & 3) + 1] = static_cast<uint8_t>(result >> 16);
    ram[addr - (addr & 3) + 2] = static_cast<uint8_t>(result >> 8);
    ram[addr - (addr & 3) + 3] = static_cast<uint8_t>(result & 0xFF);
}

// Branch (branch delay slot per ADR-004/005)
void PSXCpu::ExecBeq(uint32_t rs, uint32_t rt, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, gpr_[rs] == gpr_[rt]);
}

void PSXCpu::ExecBne(uint32_t rs, uint32_t rt, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, gpr_[rs] != gpr_[rt]);
}

void PSXCpu::ExecBlez(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) <= 0);
}

void PSXCpu::ExecBgtz(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) > 0);
}

void PSXCpu::ExecBltz(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) < 0);
}

void PSXCpu::ExecBgez(uint32_t rs, int16_t offset) {
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, ToSigned(gpr_[rs]) >= 0);
}

void PSXCpu::ExecBltzal(uint32_t rs, int16_t offset) {
    // Return address is always linked, using the value of rs before linking.
    bool taken = ToSigned(gpr_[rs]) < 0;
    SetGPR(31, pc_ + 8);
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, taken);
}

void PSXCpu::ExecBgezal(uint32_t rs, int16_t offset) {
    bool taken = ToSigned(gpr_[rs]) >= 0;
    SetGPR(31, pc_ + 8);
    uint32_t target = pc_ + 4 + (static_cast<int32_t>(offset) << 2);
    SetPendingBranch(target, taken);
}

// Jump
void PSXCpu::ExecJ(uint32_t target) {
    uint32_t addr = (pc_ + 4) & 0xF0000000;
    SetPendingBranch(addr | (target << 2), true);
}

void PSXCpu::ExecJal(uint32_t target) {
    SetGPR(31, pc_ + 8);
    uint32_t addr = (pc_ + 4) & 0xF0000000;
    SetPendingBranch(addr | (target << 2), true);
}

void PSXCpu::ExecJr(uint32_t rs) {
    SetPendingBranch(gpr_[rs], true);
}

void PSXCpu::ExecJalr(uint32_t rd, uint32_t rs) {
    // Capture the target before linking so that jalr rd, rd uses the old value.
    uint32_t target = gpr_[rs];
    SetGPR(rd, pc_ + 8);
    SetPendingBranch(target, true);
}

// System
void PSXCpu::ExecSyscall() {
    // For now, just continue execution
}

void PSXCpu::ExecBreak() {
    // For now, just continue execution
}

// Coprocessor 0
void PSXCpu::ExecMfc0(uint32_t rt, uint32_t rd) {
    // Simplified: only implement Status (12) and Cause (13) for now
    switch (rd) {
        case 12: SetGPR(rt, 0); break; // Status
        case 13: SetGPR(rt, 0); break; // Cause
        default: SetGPR(rt, 0); break;
    }
}

void PSXCpu::ExecMtc0(uint32_t rt, uint32_t rd) {
    // Simplified: ignore for now
}

void PSXCpu::ExecRfe() {
    // Simplified: ignore for now
}

// Helpers
uint32_t PSXCpu::SignExtend16(int16_t value) const {
    return static_cast<uint32_t>(static_cast<int32_t>(value));
}

uint32_t PSXCpu::ZeroExtend16(uint16_t value) const {
    return static_cast<uint32_t>(value);
}

int32_t PSXCpu::ToSigned(uint32_t value) const {
    return static_cast<int32_t>(value);
}