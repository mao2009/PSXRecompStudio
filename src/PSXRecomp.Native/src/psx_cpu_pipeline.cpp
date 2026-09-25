// PSXCpu step / fetch / pipeline / load-delay / branch-pending state
// transitions. Moved verbatim out of psx_cpu.cpp (Issue #524); owned by the
// pipeline Rust migration slice (#531).

#include "psx_cpu.h"
#include "psx_memory.h"
#include <cstdint>

bool PSXCpu::FetchInstruction(PSXMemory& memory, uint32_t& instruction) {
    uint32_t phys = TranslateAddress(pc_);
    // An instruction fetch from a misaligned or unmapped PC is an address error
    // (AdEL), not a silent NOP: continuing to execute past a corrupted control
    // transfer hides exactly the class of bug this fault exists to surface
    // (Issue #376). This is instruction fetch only -- an unmapped *data*
    // access stays a silent read-0/ignored-write by design (test_kseg_unmapped).
    if ((pc_ & 3u) != 0 || !IsMapped(phys)) {
        RaiseAddressError(0x04, pc_); // AdEL
        return false;
    }
    instruction = memory.Read32(phys);
    return true;
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
    // Double load delays to the same register: the last load wins. Step()
    // already recorded the cancelled load's pending commit as a Golden Trace
    // event this same call now cancels (it samples load_delay_reg_
    // unconditionally at the top of the step, before this instruction runs)
    // -- that recorded event's value is real in the trace but never reaches
    // the register file (golden_trace.h, Issue #202).
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
    // A load-delay write pending at the start of the step retires during this
    // step: the R3000A commits the load during the delay slot, before the
    // delay-slot instruction's own result write. Report it first so the trace
    // records retirement order (pending load commit, then instruction result).
    // A same-register immediate write later in the step supersedes the load in
    // the register file, but the load retirement itself still occurred and is
    // part of the architectural write stream. This must run unconditionally,
    // before any early return below (including the interrupt-preempt path),
    // because every such path still calls UpdateLoadDelay() and therefore
    // still commits this pending load into the register file (Issue #157 /
    // Issue #144 integration: an interrupt preempts the next instruction
    // fetch, not the previous instruction's already-in-flight load-delay
    // commit).
    if (gpr_write_trace_ != nullptr && load_delay_reg_ >= 0) {
        RecordGprWrite(load_delay_reg_, gpr_[load_delay_reg_], load_delay_value_);
    }

    // CAUSE.IP2 (bit 10) mirrors the Interrupt Controller's aggregate pending
    // line every step, independent of delay-slot state, so that CAUSE reads
    // (MFC0) stay live. Software IP[1:0] (bits 8-9, set via MTC0) are left
    // untouched (docs/cpu/cop0.md, Issue #144).
    uint32_t cause = cop0_[13];
    if (hardware_interrupt_pending_) {
        cause |= (1u << 10);
    } else {
        cause &= ~(1u << 10);
    }
    cop0_[13] = cause;

    // Interrupt exception check (Issue #144): only at an instruction-fetch
    // boundary that is not itself a pending branch's delay slot, so that a
    // branch + delay-slot pair always completes together before an interrupt
    // is serviced (ADR-004/ADR-005 delay-slot semantics; also matches
    // docs/cpu/exceptions.md "IEc=1 の場合、例外として処理").
    if (!branch_pending_) {
        uint32_t sr = cop0_[12];
        bool iec = (sr & 0x2u) != 0;
        uint32_t im = (sr >> 8) & 0xFFu;
        uint32_t ip = (cause >> 8) & 0xFFu;
        if (iec && (ip & im) != 0) {
            // No instruction is fetched/executed this step: the interrupt
            // preempts it. EPC/BD follow the same #141 exception model as any
            // other exception, anchored at the not-yet-executed instruction.
            executing_instr_addr_ = pc_;
            executing_in_delay_slot_ = false;
            RaiseException(0x00); // INT
            next_pc_ = pc_ + 4;
            UpdateLoadDelay();
            return 0;
        }
    }

    uint32_t instr_addr = pc_;
    bool in_delay_slot = branch_pending_;
    branch_issued_ = false;

    // Set the exception anchor before the fetch: a fetch address error raises
    // through the same RaiseException path and needs EPC/BD already resolved.
    executing_instr_addr_ = instr_addr;
    executing_in_delay_slot_ = in_delay_slot;
    exception_raised_ = false;
    last_exception_code_ = 0;
    last_exception_fault_pc_ = 0;
    last_exception_in_delay_slot_ = false;

    uint32_t instruction = 0;
    if (FetchInstruction(memory, instruction)) {
        ExecuteInstruction(instruction, memory);
    }

    if (exception_raised_) {
        // An exception occurred: pc_ was forced to the exception vector by
        // RaiseException, bypassing the normal delay-slot/branch PC update
        // (ADR-005: pc = exception vector; next_pc = pc + 4).
        next_pc_ = pc_ + 4;
        UpdateLoadDelay();
        return 0;
    }

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
