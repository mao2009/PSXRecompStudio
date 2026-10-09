#pragma once

#include <cstdint>

static constexpr int PSX_GPR_COUNT = 32;
static constexpr int PSX_COP0_COUNT = 32;
static constexpr uint32_t PSX_RAM_SIZE = 2 * 1024 * 1024;
static constexpr uint32_t PSX_BIOS_SIZE = 512 * 1024;
static constexpr uint32_t PSX_HW_REG_SIZE = 8 * 1024;

class PSXMemory;

// GTE (COP2) bridge (Issue #447). The GTE register file and command arithmetic
// have exactly one owner, the managed GteRegisterBank; the CPU owns only the
// COP2 instruction semantics (SR.CU2, LWC2/SWC2 addressing and faults, the
// MFC2/CFC2 load delay). `reg` is 0-31 for data and 32-63 for control
// registers. The command callback returns 0 when it executed the command and
// non-zero when the command is not implemented (no GTE state changed).
using PSXGteReadCallback = uint32_t (*)(void* context, uint32_t reg);
using PSXGteWriteCallback = void (*)(void* context, uint32_t reg, uint32_t value);
using PSXGteCommandCallback = int32_t (*)(void* context, uint32_t command);

// Step() status for a GTE command the attached GTE does not implement
// (Issue #447): fail closed, nothing retired, the PC stays on the command.
static constexpr int kPsxStepGteCommandUnsupported = -2; // = PSX_STEP_GTE_COMMAND_UNSUPPORTED (psx_core.h)

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

    // Interrupt controller integration (docs/cpu/exceptions.md, Issue #144).
    // Reflects the Interrupt Controller's aggregate pending line (I_STAT &
    // I_MASK), matching PSX hardware where the controller drives a single
    // CPU interrupt input (CAUSE.IP2, bit 10). The caller (PSXCore_Step /
    // PSXCore_Run) is responsible for sampling the controller and calling
    // this before every individual Step(); PSXCpu itself has no dependency
    // on the Interrupt Controller so it stays independently testable.
    void SetHardwareInterruptPending(bool pending);

    // Instruction execution. There is deliberately no PSXCpu::Run/multi-step
    // loop here: any caller stepping more than one instruction must re-sample
    // the Interrupt Controller and call SetHardwareInterruptPending() before
    // every individual Step() (Issue #144's per-instruction sampling
    // contract, see above). PSXCore_Run (psx_api.cpp) is that caller for the
    // C ABI; a class-level Run() here could not honor the contract and would
    // silently run with stale interrupt state, so it was removed rather than
    // kept as a foot-gun (CodeRabbit, PR #198).
    int Step(PSXMemory& memory);

    // True when the most recent Step() raised an exception (Issue #377). Step()
    // deliberately still returns 0 in that case: an architectural exception is a
    // normal, continuable hardware event (INT, SYSCALL), not an emulator error,
    // and PSXCore_Run must keep executing into the handler. A caller that treats
    // a fault as the end of its run — the differential reference oracle and the
    // interpreter-backed title engine, which have no exception handler installed
    // and must never report a faulted run as a clean completion — asks this
    // instead. Reset at the start of every Step().
    bool ExceptionRaised() const { return exception_raised_; }

    // Exception resolution captured when the most recent Step() raised an
    // exception (Issue #481). Meaningful only while ExceptionRaised() is true:
    // an unraised step leaves them at their last cleared value.
    uint32_t GetLastExceptionCode() const { return last_exception_code_; }
    uint32_t GetLastExceptionFaultPc() const { return last_exception_fault_pc_; }
    bool GetLastExceptionInDelaySlot() const { return last_exception_in_delay_slot_; }

    // True when the most recent Step() executed RFE (PR #502): the guest popped
    // the SR exception stack, so a caller tracking an exception handler it let
    // run knows the handler returned. Reset at the start of every Step().
    bool RfeExecuted() const { return rfe_executed_; }

    // True while a branch/jump has executed but its delay slot has not (Issue #693). The
    // next instruction is that delay slot, so the PC is not an architectural resume point.
    bool BranchDelayPending() const { return branch_pending_; }

    // True while a load's result has not yet been committed to the register file (Issue #693):
    // either the load issued by the previous instruction (queued) or one committing in the
    // next step. A register read at this boundary is the load-delay (old) value.
    bool LoadDelayPending() const { return load_delay_reg_ >= 0 || next_load_delay_reg_ >= 0; }

    // Pops the SR 3-level KU/IE stack exactly as RFE does, without executing an
    // instruction: for a host that completes an exception handler in HLE (Issue
    // #663). Unlike ExecRfe it does not set RfeExecuted().
    void PopExceptionSrStack();

    // Golden Trace GPR write-event recording (Issue #157). A single MIPS I step
    // retires at most kMaxGprWritesPerStep writes: one instruction-result write
    // (SetGPR) plus at most one load-delay commit (ADR-004), so the recorder
    // and the trace share this upper bound. Any step exceeding it is a model
    // violation and is reported loudly by RecordGprWrite.
    static constexpr int kMaxGprWritesPerStep = 2;

    struct GprWriteEvent {
        int index = -1;         // GPR index written; -1 when unused.
        uint32_t before = 0;    // Register value immediately before this write.
        uint32_t value = 0;     // Value written by this retirement.
    };

    struct GprWriteTrace {
        int count = 0;
        GprWriteEvent events[kMaxGprWritesPerStep] = {};
    };

    // Attaches/detaches the write-event recorder (trace harness only). Null by
    // default, so the production execution path is unchanged; the harness
    // passes a stack-allocated recorder around a single Step() call.
    void SetGprWriteTrace(GprWriteTrace* trace) { gpr_write_trace_ = trace; }

    // Attaches (or, with null callbacks, detaches) the GTE. Without a GTE every
    // COP2/LWC2/SWC2 raises CpU (CE = 2), as before Issue #447.
    void AttachGte(void* context, PSXGteReadCallback read, PSXGteWriteCallback write, PSXGteCommandCallback command) {
        gte_context_ = context;
        gte_read_ = read;
        gte_write_ = write;
        gte_command_ = command;
    }

private:
    uint32_t gpr_[PSX_GPR_COUNT];
    uint32_t pc_;          // Address of the instruction currently being executed (ADR-005: pc)
    uint32_t next_pc_;     // ADR-005: next_pc. Maintained (= pc_ + 4) for model parity;
                           // the interpreter fetches from pc_ directly.
    uint32_t delay_slot_pc_; // Delay slot address of a pending branch (ADR-005: delay_slot_pc)
    uint32_t hi_;
    uint32_t lo_;
    uint32_t cop0_[PSX_COP0_COUNT]; // COP0 registers (docs/cpu/cop0.md).

    // Pending branch state (branch delay slot, ADR-004/005).
    bool branch_pending_;        // A branch was executed; its delay slot has not completed yet.
    bool branch_issued_;         // The instruction currently being executed is a branch/jump.
    uint32_t pending_branch_target_; // Target of the pending (outermost) branch.
    bool pending_branch_taken_;      // Whether the pending (outermost) branch is taken.

    // Exception state (docs/cpu/exceptions.md, ADR-005).
    bool exception_raised_;          // An exception was raised during the current step.
    uint32_t executing_instr_addr_;  // Address of the instruction currently executing.
    bool executing_in_delay_slot_;   // Whether the current instruction is in a branch delay slot.

    // Exception resolution captured at raise time (Issue #481): the CAUSE
    // Excode, the faulting PC (EPC = the branch PC when the faulting instruction
    // was in a delay slot, else the faulting instruction's own PC) and whether
    // the faulting instruction was in a delay slot. Kept as stable snapshots
    // because the per-step fields they derive from are reused by the next step;
    // they are meaningful only while ExceptionRaised() is true and are cleared
    // at the start of every Step() and in Reset().
    uint32_t last_exception_code_ = 0;
    uint32_t last_exception_fault_pc_ = 0;
    bool last_exception_in_delay_slot_ = false;
    bool rfe_executed_ = false;

    // Interrupt controller state (Issue #144). Sampled by the caller via
    // SetHardwareInterruptPending() before each Step() call; mirrored onto
    // CAUSE.IP2 (bit 10).
    bool hardware_interrupt_pending_;

    // Load delay state (ADR-004). Double buffered so that the value loaded by an
    // instruction is only committed to the register file one instruction later.
    int load_delay_reg_;         // -1 when no load-delay write is pending for this step.
    uint32_t load_delay_value_;
    int next_load_delay_reg_;    // -1 when no load-delay write is queued for the next step.
    uint32_t next_load_delay_value_;

    // Optional per-instance recorder for GPR retirement write events (null in
    // production; attached by the Golden Trace harness around a single step).
    GprWriteTrace* gpr_write_trace_ = nullptr;
    void RecordGprWrite(int index, uint32_t before, uint32_t value);

    void* gte_context_ = nullptr;
    PSXGteReadCallback gte_read_ = nullptr;
    PSXGteWriteCallback gte_write_ = nullptr;
    PSXGteCommandCallback gte_command_ = nullptr;
    bool gte_command_unsupported_ = false; // set by ExecCop2Command for this step

    // Instruction decode helpers. FetchInstruction returns false when the PC is
    // misaligned or unmapped; it has already raised AdEL in that case and no
    // instruction is executed this step (docs/cpu/exceptions.md, Issue #376).
    bool FetchInstruction(PSXMemory& memory, uint32_t& instruction);
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
    // COP0 SR.IsC (bit 16, isolate cache): a store through KUSEG/KSEG0 goes to the data cache,
    // not memory. Firmware flushes the I-cache this way (stores of zero over low RAM), so the
    // store must not reach RAM. KSEG1 is uncached and unaffected.
    bool StoreIsCacheIsolated(uint32_t vaddr) const { return (cop0_[12] & 0x00010000u) != 0 && vaddr < 0xA0000000u; }
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

    // Coprocessor 2 (GTE, Issue #447), src/psx_cpu_cop2.cpp. Cop2Usable raises
    // CpU (CE = 2) and returns false when SR.CU2 is clear or no GTE is attached.
    bool Cop2Usable();
    void ExecMfc2(uint32_t rt, uint32_t rd);
    void ExecCfc2(uint32_t rt, uint32_t rd);
    void ExecMtc2(uint32_t rt, uint32_t rd);
    void ExecCtc2(uint32_t rt, uint32_t rd);
    void ExecCop2Command(uint32_t command);
    void ExecLwc2(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    void ExecSwc2(uint32_t rt, uint32_t rs, int16_t offset, PSXMemory& memory);
    // Raises an exception (docs/cpu/exceptions.md). `ce` is the coprocessor
    // number written to CAUSE.CE (bits 28-29); it is only meaningful for CpU
    // (0x0B) and is cleared to 0 for every other exception.
    void RaiseException(uint32_t excode, uint32_t ce = 0);
    // Raises AdEL (load/fetch) or AdES (store) for `addr`, recording it in
    // BadVaddr (cop0r8) as documented in docs/cpu/cop0.md.
    void RaiseAddressError(uint32_t excode, uint32_t addr);

    // Helpers
    uint32_t TranslateAddress(uint32_t virt) const;
    bool IsMapped(uint32_t phys) const;
    uint32_t SignExtend16(int16_t value) const;
    uint32_t ZeroExtend16(uint16_t value) const;
    int32_t ToSigned(uint32_t value) const;
};
