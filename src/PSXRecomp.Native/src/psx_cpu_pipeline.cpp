// PSXCpu step / fetch / pipeline / load-delay / branch-pending state
// transitions. Moved verbatim out of psx_cpu.cpp (Issue #524); owned by the
// pipeline Rust migration slice (#531).

#include "psx_cpu.h"
#include "psx_cpu_pipeline.h"
#include "psx_memory.h"
#include <cstdint>

// The load-delay / branch-delay state transitions are computed in Rust
// (psx_cpu_pipeline.h, Issue #531) over POD copies of the PSXCpu fields below;
// this file keeps owning those fields, fetch, dispatch, the interrupt check,
// Golden Trace recording and the GPR commit write. These helpers copy the
// branch fields out and back so each transition stays a single by-value call.
static PSXCpuBranchState ToBranch(uint32_t pc, uint32_t next_pc, uint32_t delay_slot_pc, uint32_t target,
                                  bool pending, bool issued, bool taken) {
    return {pc, next_pc, delay_slot_pc, target, pending ? 1u : 0u, issued ? 1u : 0u, taken ? 1u : 0u};
}

static void FromBranch(const PSXCpuBranchState& s, uint32_t& pc, uint32_t& next_pc, uint32_t& delay_slot_pc,
                       uint32_t& target, bool& pending, bool& issued, bool& taken) {
    pc = s.pc;
    next_pc = s.next_pc;
    delay_slot_pc = s.delay_slot_pc;
    target = s.pending_branch_target;
    pending = s.branch_pending != 0;
    issued = s.branch_issued != 0;
    taken = s.pending_branch_taken != 0;
}

#define PSX_BRANCH_FIELDS pc_, next_pc_, delay_slot_pc_, pending_branch_target_, branch_pending_, branch_issued_, \
                          pending_branch_taken_

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
    PSXCpuLoadDelayCommit c = psx_cpu_pipeline_flush_load_delay(
        {load_delay_reg_, load_delay_value_, next_load_delay_reg_, next_load_delay_value_});
    if (c.commit_reg >= 0) {
        gpr_[c.commit_reg] = c.commit_value;
    }
    load_delay_reg_ = c.state.reg;
    next_load_delay_reg_ = c.state.next_reg;

    FromBranch(psx_cpu_pipeline_flush_branch(ToBranch(PSX_BRANCH_FIELDS)), PSX_BRANCH_FIELDS);
}

void PSXCpu::UpdateLoadDelay() {
    // Commit the value loaded one instruction ago (the delay-slot instruction has
    // already read the old value), then shift the queued load into place. Writing
    // the register in-order ensures an immediate write in the delay slot wins.
    PSXCpuLoadDelayCommit c = psx_cpu_pipeline_update_load_delay(
        {load_delay_reg_, load_delay_value_, next_load_delay_reg_, next_load_delay_value_});
    if (c.commit_reg >= 0) {
        gpr_[c.commit_reg] = c.commit_value;
    }
    load_delay_reg_ = c.state.reg;
    load_delay_value_ = c.state.value;
    next_load_delay_reg_ = c.state.next_reg;
    next_load_delay_value_ = c.state.next_value;
}

void PSXCpu::WriteRegDelayed(int index, uint32_t value) {
    // Double load delays to the same register: the last load wins. Step()
    // already recorded the cancelled load's pending commit as a Golden Trace
    // event this same call now cancels (it samples load_delay_reg_
    // unconditionally at the top of the step, before this instruction runs)
    // -- that recorded event's value is real in the trace but never reaches
    // the register file (golden_trace.h, Issue #202). Out-of-range and $zero
    // indices are ignored by the Rust transition.
    PSXCpuLoadDelayState s = psx_cpu_pipeline_queue_load(
        {load_delay_reg_, load_delay_value_, next_load_delay_reg_, next_load_delay_value_}, index, value);
    load_delay_reg_ = s.reg;
    next_load_delay_reg_ = s.next_reg;
    next_load_delay_value_ = s.next_value;
}

void PSXCpu::SetPendingBranch(uint32_t target, bool taken) {
    // Primary branch: record the pending control transfer; the delay slot is
    // executed before this target is applied (ADR-005). Branch in a delay
    // slot: the inner branch executes (and consumes its own delay slot) but
    // its target is ignored; the outer branch is applied instead
    // (docs/cpu/pipeline.md, branch-in-delay-slot).
    FromBranch(psx_cpu_pipeline_set_pending_branch(ToBranch(PSX_BRANCH_FIELDS), target, taken ? 1u : 0u),
               PSX_BRANCH_FIELDS);
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
            // Exception path: next_pc = pc + 4, branch state discarded.
            FromBranch(psx_cpu_pipeline_advance(ToBranch(PSX_BRANCH_FIELDS), pc_, 0u, 1u), PSX_BRANCH_FIELDS);
            UpdateLoadDelay();
            return 0;
        }
    }

    PSXCpuStepBegin begin = psx_cpu_pipeline_begin_step(ToBranch(PSX_BRANCH_FIELDS));
    FromBranch(begin.state, PSX_BRANCH_FIELDS); // branch_issued_ = false
    uint32_t instr_addr = begin.instr_addr;
    bool in_delay_slot = begin.in_delay_slot != 0;

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
        // (ADR-005: pc = exception vector; next_pc = pc + 4). Branch state
        // stays discarded.
        FromBranch(psx_cpu_pipeline_advance(ToBranch(PSX_BRANCH_FIELDS), instr_addr, in_delay_slot ? 1u : 0u, 1u),
                   PSX_BRANCH_FIELDS);
        UpdateLoadDelay();
        return 0;
    }

    // Delay slot of a pending branch: apply the outermost branch, or (branch
    // in a delay slot) ignore the inner target and run the shared delay slot
    // (docs/cpu/pipeline.md). After a branch/jump: its delay slot is next.
    // Otherwise sequential (ADR-005).
    FromBranch(psx_cpu_pipeline_advance(ToBranch(PSX_BRANCH_FIELDS), instr_addr, in_delay_slot ? 1u : 0u, 0u),
               PSX_BRANCH_FIELDS);
    UpdateLoadDelay();
    return 0;
}
