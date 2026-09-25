#pragma once

#include <cstdint>

/*
 * PSXCpu step / load-delay / branch-delay state transitions. Implemented in
 * Rust (`../rust/src/cpu_pipeline.rs`, Issue #531); this header only declares
 * that crate's internal C ABI for use by psx_cpu_pipeline.cpp.
 *
 * Every function takes a copy of the state by value and returns the next
 * state; Rust never allocates, dereferences a pointer, or retains state.
 * Every function is infallible and cannot panic. Flags are 0/1 uint32_t.
 * Each struct must match its cpu_pipeline.rs counterpart field-for-field.
 */
struct PSXCpuLoadDelayState {
    int32_t reg;         // -1: nothing commits this step
    uint32_t value;
    int32_t next_reg;    // -1: nothing queued for the next step
    uint32_t next_value;
};

struct PSXCpuLoadDelayCommit {
    PSXCpuLoadDelayState state;
    int32_t commit_reg;  // -1: no GPR write
    uint32_t commit_value;
};

struct PSXCpuBranchState {
    uint32_t pc;
    uint32_t next_pc;
    uint32_t delay_slot_pc;
    uint32_t pending_branch_target;
    uint32_t branch_pending;
    uint32_t branch_issued;
    uint32_t pending_branch_taken;
};

struct PSXCpuStepBegin {
    PSXCpuBranchState state;
    uint32_t instr_addr;
    uint32_t in_delay_slot;
};

extern "C" {
PSXCpuLoadDelayCommit psx_cpu_pipeline_update_load_delay(PSXCpuLoadDelayState s);
PSXCpuLoadDelayState psx_cpu_pipeline_queue_load(PSXCpuLoadDelayState s, int32_t index, uint32_t value);
PSXCpuLoadDelayCommit psx_cpu_pipeline_flush_load_delay(PSXCpuLoadDelayState s);
PSXCpuBranchState psx_cpu_pipeline_set_pending_branch(PSXCpuBranchState s, uint32_t target, uint32_t taken);
PSXCpuStepBegin psx_cpu_pipeline_begin_step(PSXCpuBranchState s);
PSXCpuBranchState psx_cpu_pipeline_advance(PSXCpuBranchState s, uint32_t instr_addr, uint32_t in_delay_slot,
                                           uint32_t exception_raised);
PSXCpuBranchState psx_cpu_pipeline_flush_branch(PSXCpuBranchState s);
}
