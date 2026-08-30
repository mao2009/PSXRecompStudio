#pragma once

#include <cstdint>

static constexpr int PSX_GPR_COUNT = 32;
static constexpr uint32_t PSX_RAM_SIZE = 2 * 1024 * 1024;
static constexpr uint32_t PSX_BIOS_SIZE = 512 * 1024;
static constexpr uint32_t PSX_HW_REG_SIZE = 8 * 1024;

class PSXMemory;

class PSXCpu {
public:
    PSXCpu();

    void Reset();

    uint32_t GetGPR(int index) const;
    void SetGPR(int index, uint32_t value);

    uint32_t GetPC() const;
    void SetPC(uint32_t value);

    uint32_t GetHI() const;
    void SetHI(uint32_t value);

    uint32_t GetLO() const;
    void SetLO(uint32_t value);

    uint32_t GetCop0(int index) const;
    void SetCop0(int index, uint32_t value);

    // Instruction execution
    int Step(PSXMemory& memory);
    int Run(PSXMemory& memory, uint32_t maxInstructions);

private:
    uint32_t gpr_[PSX_GPR_COUNT];
    uint32_t pc_;          // Address of the instruction currently being executed (ADR-005: pc)
    uint32_t next_pc_;     // ADR-005: next_pc. Maintained (= pc_ + 4) for model parity;
                           // the interpreter fetches from pc_ directly.
    uint32_t delay_slot_pc_; // Delay slot address of a pending branch (ADR-005: delay_slot_pc)
    uint32_t hi_;
    uint32_t lo_;
    uint32_t cop0_[32];      // COP0 registers (docs/cpu/cop0.md).

    // Pending branch state (branch delay slot, ADR-004/005).
    bool branch_pending_;        // A branch was executed; its delay slot has not completed yet.
    bool branch_issued_;         // The instruction currently being executed is a branch/jump.
    uint32_t pending_branch_target_; // Target of the pending (outermost) branch.
    bool pending_branch_taken_;      // Whether the pending (outermost) branch is taken.

    // Exception state (docs/cpu/exceptions.md, ADR-005).
    bool exception_raised_;          // An exception was raised during the current step.
    uint32_t executing_instr_addr_;  // Address of the instruction currently executing.
    bool executing_in_delay_slot_;   // Whether the current instruction is in a branch delay slot.

    // Load delay state (ADR-004). Double buffered so that the value loaded by an
    // instruction is only committed to the register file one instruction later.
    int load_delay_reg_;         // -1 when no load-delay write is pending for this step.
    uint32_t load_delay_value_;
    int next_load_delay_reg_;    // -1 when no load-delay write is queued for the next step.
    uint32_t next_load_delay_value_;

    // Instruction decode helpers
    uint32_t FetchInstruction(PSXMemory& memory);
    void ExecuteInstruction(uint32_t instruction, PSXMemory& memory);
    void FlushPipeline();
    void UpdateLoadDelay();
    void WriteRegDelayed(int index, uint32_t value);
    void SetPendingBranch(uint32_t target, bool taken);
    
    // Arithmetic/Logical
    void ExecAdd(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecAddu(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecSub(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecSubu(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecAnd(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecOr(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecXor(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecNor(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecSlt(uint32_t rd, uint32_t rs, uint32_t rt);
    void ExecSltu(uint32_t rd, uint32_t rs, uint32_t rt);
    
    // Immediate arithmetic
    void ExecAddi(uint32_t rt, uint32_t rs, int16_t imm);
    void ExecAddiu(uint32_t rt, uint32_t rs, int16_t imm);
    void ExecAndi(uint32_t rt, uint32_t rs, uint16_t imm);
    void ExecOri(uint32_t rt, uint32_t rs, uint16_t imm);
    void ExecXori(uint32_t rt, uint32_t rs, uint16_t imm);
    void ExecLui(uint32_t rt, uint16_t imm);
    void ExecSlti(uint32_t rt, uint32_t rs, int16_t imm);
    void ExecSltiu(uint32_t rt, uint32_t rs, int16_t imm);
    
    // Shift
    void ExecSll(uint32_t rd, uint32_t rt, uint32_t shamt);
    void ExecSrl(uint32_t rd, uint32_t rt, uint32_t shamt);
    void ExecSra(uint32_t rd, uint32_t rt, uint32_t shamt);
    void ExecSllv(uint32_t rd, uint32_t rt, uint32_t rs);
    void ExecSrlv(uint32_t rd, uint32_t rt, uint32_t rs);
    void ExecSrav(uint32_t rd, uint32_t rt, uint32_t rs);
    
    // Multiply/Divide
    void ExecMult(uint32_t rs, uint32_t rt);
    void ExecMultu(uint32_t rs, uint32_t rt);
    void ExecDiv(uint32_t rs, uint32_t rt);
    void ExecDivu(uint32_t rs, uint32_t rt);
    void ExecMfhi(uint32_t rd);
    void ExecMflo(uint32_t rd);
    void ExecMthi(uint32_t rs);
    void ExecMtlo(uint32_t rs);
    
    // Memory
    void ExecLb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecLbu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecLh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecLhu(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecLw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecLwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecLwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecSb(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecSh(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecSw(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecSwl(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecSwr(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    
    // Branch
    void ExecBeq(uint32_t rs, uint32_t rt, int16_t offset);
    void ExecBne(uint32_t rs, uint32_t rt, int16_t offset);
    void ExecBlez(uint32_t rs, int16_t offset);
    void ExecBgtz(uint32_t rs, int16_t offset);
    void ExecBltz(uint32_t rs, int16_t offset);
    void ExecBgez(uint32_t rs, int16_t offset);
    void ExecBltzal(uint32_t rs, int16_t offset);
    void ExecBgezal(uint32_t rs, int16_t offset);
    
    // Jump
    void ExecJ(uint32_t target);
    void ExecJal(uint32_t target);
    void ExecJr(uint32_t rs);
    void ExecJalr(uint32_t rd, uint32_t rs);
    
    // System
    void ExecSyscall();
    void ExecBreak();
    
    // Coprocessor 0
    void ExecMfc0(uint32_t rt, uint32_t rd);
    void ExecMtc0(uint32_t rt, uint32_t rd);
    void ExecRfe();
    void RaiseException(uint32_t excode);
    
    // Helpers
    uint32_t TranslateAddress(uint32_t virt) const;
    bool IsMapped(uint32_t phys) const;
    uint32_t SignExtend16(int16_t value) const;
    uint32_t ZeroExtend16(uint16_t value) const;
    int32_t ToSigned(uint32_t value) const;
};
