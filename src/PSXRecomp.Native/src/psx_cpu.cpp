#include "psx_cpu.h"
#include "psx_cpu_alu.h"
#include "psx_cpu_hilo.h"
#include "psx_memory.h"
#include "psx_cpu_ops.h"
#include <cassert>
#include <cstdint>

PSXCpu::PSXCpu() {
    Reset();
}

void PSXCpu::Reset() {
    for (int i = 0; i < PSX_GPR_COUNT; i++) {
        gpr_[i] = 0;
    }
    for (int i = 0; i < PSX_COP0_COUNT; i++) {
        cop0_[i] = 0;
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
    exception_raised_ = false;
    executing_instr_addr_ = 0;
    executing_in_delay_slot_ = false;
    last_exception_code_ = 0;
    last_exception_fault_pc_ = 0;
    last_exception_in_delay_slot_ = false;
    hardware_interrupt_pending_ = false;
}

void PSXCpu::SetHardwareInterruptPending(bool pending) {
    hardware_interrupt_pending_ = pending;
}

uint32_t PSXCpu::GetGPR(int index) const {
    if (index < 0 || index >= PSX_GPR_COUNT) return 0;
    if (index == 0) return 0;
    return gpr_[index];
}

void PSXCpu::SetGPR(int index, uint32_t value) {
    if (index < 0 || index >= PSX_GPR_COUNT) return;
    if (index == 0) return;
    uint32_t before = gpr_[index];
    // In retirement order a pending load write retires just before this
    // instruction-result write to the same register; its value is this write's
    // immediate prior value, even though the load is net-cancelled from the
    // register file. Report that value so the trace's before/after chain is
    // consistent with the retirement stream (CodeRabbit, PR #198).
    if (gpr_write_trace_ != nullptr && index == load_delay_reg_) {
        before = load_delay_value_;
    }
    RecordGprWrite(index, before, value);
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

void PSXCpu::RecordGprWrite(int index, uint32_t before, uint32_t value) {
    if (gpr_write_trace_ == nullptr) return;
    if (index < 0 || index >= PSX_GPR_COUNT) return;
    if (index == 0) return;
    // Model invariant: a step retires at most kMaxGprWritesPerStep writes (one
    // instruction result + one load-delay commit). A trace must never silently
    // lose a write, so overflow is reported loudly; it is unreachable by the
    // model and caught by the golden-trace tests if the invariant is broken.
    if (gpr_write_trace_->count >= kMaxGprWritesPerStep) {
        assert(false && "GPR write trace overflow: a step retired more than kMaxGprWritesPerStep writes");
        return;
    }
    gpr_write_trace_->events[gpr_write_trace_->count].index = index;
    gpr_write_trace_->events[gpr_write_trace_->count].before = before;
    gpr_write_trace_->events[gpr_write_trace_->count].value = value;
    gpr_write_trace_->count++;
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

uint32_t PSXCpu::GetCop0(int index) const {
    if (index < 0 || index >= PSX_COP0_COUNT) return 0;
    return cop0_[index];
}

void PSXCpu::SetCop0(int index, uint32_t value) {
    if (index < 0 || index >= PSX_COP0_COUNT) return;
    cop0_[index] = value;
}

// Arithmetic/Logical
void PSXCpu::ExecAdd(uint32_t rd, uint32_t rs, uint32_t rt) {
    // Overflow-checked sum computed by Rust (Issue #495); C++ still owns the
    // GPR read/write and exception raising.
    PSXAluResult r = psx_cpu_alu_add(gpr_[rs], gpr_[rt]);
    if (r.overflow) {
        RaiseException(0x0C); // Ov
        return; // result is NOT written to the GPR
    }
    SetGPR(rd, r.result);
}

void PSXCpu::ExecAddu(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_addu(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecSub(uint32_t rd, uint32_t rs, uint32_t rt) {
    // Overflow-checked difference computed by Rust (Issue #495); C++ still
    // owns the GPR read/write and exception raising.
    PSXAluResult r = psx_cpu_alu_sub(gpr_[rs], gpr_[rt]);
    if (r.overflow) {
        RaiseException(0x0C); // Ov
        return; // result is NOT written to the GPR
    }
    SetGPR(rd, r.result);
}

void PSXCpu::ExecSubu(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_subu(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecAnd(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_and(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecOr(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_or(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecXor(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_xor(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecNor(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_nor(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecSlt(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_slt(gpr_[rs], gpr_[rt]));
}

void PSXCpu::ExecSltu(uint32_t rd, uint32_t rs, uint32_t rt) {
    SetGPR(rd, psx_cpu_ops_sltu(gpr_[rs], gpr_[rt]));
}

// Immediate arithmetic
void PSXCpu::ExecAddi(uint32_t rt, uint32_t rs, int16_t imm) {
    // ADDI is ADD with a sign-extended immediate (Issue #495); C++ still
    // owns the sign extension, GPR read/write, and exception raising.
    PSXAluResult r = psx_cpu_alu_add(gpr_[rs], SignExtend16(imm));
    if (r.overflow) {
        RaiseException(0x0C); // Ov
        return; // result is NOT written to the GPR
    }
    SetGPR(rt, r.result);
}

void PSXCpu::ExecAddiu(uint32_t rt, uint32_t rs, int16_t imm) {
    SetGPR(rt, psx_cpu_ops_addu(gpr_[rs], SignExtend16(imm)));
}

void PSXCpu::ExecAndi(uint32_t rt, uint32_t rs, uint16_t imm) {
    SetGPR(rt, psx_cpu_ops_and(gpr_[rs], ZeroExtend16(imm)));
}

void PSXCpu::ExecOri(uint32_t rt, uint32_t rs, uint16_t imm) {
    SetGPR(rt, psx_cpu_ops_or(gpr_[rs], ZeroExtend16(imm)));
}

void PSXCpu::ExecXori(uint32_t rt, uint32_t rs, uint16_t imm) {
    SetGPR(rt, psx_cpu_ops_xor(gpr_[rs], ZeroExtend16(imm)));
}

void PSXCpu::ExecLui(uint32_t rt, uint16_t imm) {
    SetGPR(rt, psx_cpu_ops_sll(ZeroExtend16(imm), 16));
}

void PSXCpu::ExecSlti(uint32_t rt, uint32_t rs, int16_t imm) {
    SetGPR(rt, psx_cpu_ops_slt(gpr_[rs], SignExtend16(imm)));
}

void PSXCpu::ExecSltiu(uint32_t rt, uint32_t rs, int16_t imm) {
    // SLTIU sign-extends the 16-bit immediate to 32 bits, then compares unsigned
    // (MIPS I semantics) — it does not zero-extend.
    SetGPR(rt, psx_cpu_ops_sltu(gpr_[rs], SignExtend16(imm)));
}

// Shift
void PSXCpu::ExecSll(uint32_t rd, uint32_t rt, uint32_t shamt) {
    SetGPR(rd, psx_cpu_ops_sll(gpr_[rt], shamt));
}

void PSXCpu::ExecSrl(uint32_t rd, uint32_t rt, uint32_t shamt) {
    SetGPR(rd, psx_cpu_ops_srl(gpr_[rt], shamt));
}

void PSXCpu::ExecSra(uint32_t rd, uint32_t rt, uint32_t shamt) {
    SetGPR(rd, psx_cpu_ops_sra(gpr_[rt], shamt));
}

void PSXCpu::ExecSllv(uint32_t rd, uint32_t rt, uint32_t rs) {
    SetGPR(rd, psx_cpu_ops_sll(gpr_[rt], gpr_[rs])); // Rust masks to low 5 bits
}

void PSXCpu::ExecSrlv(uint32_t rd, uint32_t rt, uint32_t rs) {
    SetGPR(rd, psx_cpu_ops_srl(gpr_[rt], gpr_[rs])); // Rust masks to low 5 bits
}

void PSXCpu::ExecSrav(uint32_t rd, uint32_t rt, uint32_t rs) {
    SetGPR(rd, psx_cpu_ops_sra(gpr_[rt], gpr_[rs])); // Rust masks to low 5 bits
}

// Multiply/Divide. The 64-bit product / division arithmetic is implemented in
// Rust (psx_cpu_hilo.h, Issue #497); this class keeps owning the GPR reads
// and HI/LO assignment.
void PSXCpu::ExecMult(uint32_t rs, uint32_t rt) {
    PSXMulDivResult result = psx_cpu_hilo_mult(gpr_[rs], gpr_[rt]);
    hi_ = result.hi;
    lo_ = result.lo;
}

void PSXCpu::ExecMultu(uint32_t rs, uint32_t rt) {
    PSXMulDivResult result = psx_cpu_hilo_multu(gpr_[rs], gpr_[rt]);
    hi_ = result.hi;
    lo_ = result.lo;
}

void PSXCpu::ExecDiv(uint32_t rs, uint32_t rt) {
    PSXMulDivResult result = psx_cpu_hilo_div(gpr_[rs], gpr_[rt]);
    hi_ = result.hi;
    lo_ = result.lo;
}

void PSXCpu::ExecDivu(uint32_t rs, uint32_t rt) {
    PSXMulDivResult result = psx_cpu_hilo_divu(gpr_[rs], gpr_[rt]);
    hi_ = result.hi;
    lo_ = result.lo;
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