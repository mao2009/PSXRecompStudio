//! `PSXCpu` step / load-delay / branch-delay state transitions, migrated from
//! the C++ `PSXCpu::Step`/`UpdateLoadDelay`/`WriteRegDelayed`/
//! `SetPendingBranch`/`FlushPipeline` (Issue #531).
//!
//! Scope: only the deterministic state transitions of the pipeline — the
//! double-buffered load-delay commit/queue, the pending-branch record, the
//! per-step exception anchor, and the post-instruction PC / next-PC / branch
//! progression (including the exception path that discards branch state).
//! The caller (`src/psx_cpu_pipeline.cpp`) keeps owning the `PSXCpu` fields,
//! instruction fetch, dispatch (`ExecuteInstruction`), `RaiseException`, the
//! interrupt check, Golden Trace recording and the GPR write itself: every
//! function here takes a copy of the relevant state and returns the next
//! state (plus, for a load-delay commit, the register write the caller must
//! perform). Nothing here touches memory or retains state.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_pipeline.h`, called only from `psx_cpu_pipeline.cpp`), not
//! P/Invoked, so `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION`
//! are unaffected. Every export takes and returns only `#[repr(C)]` structs of
//! `u32`/`i32` fields by value, dereferences no pointer, and cannot panic (all
//! PC arithmetic is `wrapping_add`, matching C++ `uint32_t` wraparound), so
//! every export is infallible per `docs/development/rust-ffi-contract.md` §5.

/// Number of general-purpose registers (`PSX_GPR_COUNT`).
const GPR_COUNT: i32 = 32;

/// "No load-delay write" register sentinel, as used by `PSXCpu`.
pub const NO_REG: i32 = -1;

/// The double-buffered load delay (ADR-004).
///
/// Mirrored field-for-field by `PSXCpuLoadDelayState` in
/// `src/psx_cpu_pipeline.h`; a layout change there or here is an ABI break.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LoadDelayState {
    /// Register committed at the end of this step, or [`NO_REG`].
    pub reg: i32,
    /// Value committed to `reg`.
    pub value: u32,
    /// Register queued by the instruction executing this step, or [`NO_REG`].
    pub next_reg: i32,
    /// Value queued for `next_reg`.
    pub next_value: u32,
}

/// A load-delay transition plus the GPR write the caller must perform.
///
/// Mirrored by `PSXCpuLoadDelayCommit` in `src/psx_cpu_pipeline.h`.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LoadDelayCommit {
    /// The load-delay state after the transition.
    pub state: LoadDelayState,
    /// Register to write, or [`NO_REG`] when nothing commits.
    pub commit_reg: i32,
    /// Value to write to `commit_reg`.
    pub commit_value: u32,
}

/// The branch-delay / PC state (ADR-005). Flags are `0`/`1` (`bool` is not an
/// ABI-visible field type, FFI contract §2).
///
/// Mirrored field-for-field by `PSXCpuBranchState` in
/// `src/psx_cpu_pipeline.h`; a layout change there or here is an ABI break.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BranchState {
    /// Address of the instruction to execute next (ADR-005: `pc`).
    pub pc: u32,
    /// ADR-005 `next_pc`, maintained as `pc + 4`.
    pub next_pc: u32,
    /// Delay-slot address of the pending branch.
    pub delay_slot_pc: u32,
    /// Target of the pending (outermost) branch.
    pub pending_branch_target: u32,
    /// A branch executed and its delay slot has not completed yet.
    pub branch_pending: u32,
    /// The instruction executing this step is a branch/jump.
    pub branch_issued: u32,
    /// Whether the pending (outermost) branch is taken.
    pub pending_branch_taken: u32,
}

/// The exception anchor sampled at the start of a step, plus the branch state
/// with this step's `branch_issued` cleared.
///
/// Mirrored by `PSXCpuStepBegin` in `src/psx_cpu_pipeline.h`.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StepBegin {
    /// The branch state for the instruction about to execute.
    pub state: BranchState,
    /// Address of the instruction about to execute (EPC anchor).
    pub instr_addr: u32,
    /// `1` when that instruction is a pending branch's delay slot (BD anchor).
    pub in_delay_slot: u32,
}

const fn flag(b: bool) -> u32 {
    b as u32
}

/// `UpdateLoadDelay`: commit the load issued one instruction ago, then shift
/// the queued load into place. The delay-slot instruction has already read
/// the old register value. A same-register immediate write it made has
/// already cancelled `reg` (`PSXCpu::SetGPR`, which stays in C++), so the
/// immediate write wins; the caller performs the returned commit write.
#[must_use]
pub const fn update_load_delay(s: LoadDelayState) -> LoadDelayCommit {
    LoadDelayCommit {
        state: LoadDelayState {
            reg: s.next_reg,
            value: s.next_value,
            next_reg: NO_REG,
            next_value: 0,
        },
        commit_reg: if s.reg >= 0 { s.reg } else { NO_REG },
        commit_value: s.value,
    }
}

/// `WriteRegDelayed`: queue a load result for the next step. Out-of-range
/// indices and `$zero` are ignored (state returned unchanged). A load to the
/// register whose commit is pending this step cancels that commit, so the
/// last load wins (Golden Trace keeps the cancelled event, Issue #202).
#[must_use]
pub const fn queue_load(s: LoadDelayState, index: i32, value: u32) -> LoadDelayState {
    if index <= 0 || index >= GPR_COUNT {
        return s;
    }
    LoadDelayState {
        reg: if index == s.reg { NO_REG } else { s.reg },
        value: s.value,
        next_reg: index,
        next_value: value,
    }
}

/// `FlushPipeline` (load-delay half): commit any pending load immediately
/// and clear both slots. Values are left as-is, matching the C++ original.
#[must_use]
pub const fn flush_load_delay(s: LoadDelayState) -> LoadDelayCommit {
    LoadDelayCommit {
        state: LoadDelayState {
            reg: NO_REG,
            value: s.value,
            next_reg: NO_REG,
            next_value: s.next_value,
        },
        commit_reg: if s.reg >= 0 { s.reg } else { NO_REG },
        commit_value: s.value,
    }
}

/// `SetPendingBranch`: the primary branch records its target/taken; a branch
/// executed in a delay slot keeps the outer branch's target (docs/cpu/
/// pipeline.md, branch-in-delay-slot). Either way this step issued a branch.
#[must_use]
pub const fn set_pending_branch(s: BranchState, target: u32, taken: u32) -> BranchState {
    let mut out = s;
    if s.branch_pending == 0 {
        out.pending_branch_target = target;
        out.pending_branch_taken = flag(taken != 0);
    }
    out.branch_issued = 1;
    out
}

/// Start of a non-interrupted step: anchor the exception at the current PC
/// and delay-slot status (both sampled before the instruction runs) and
/// clear `branch_issued` for the instruction about to execute.
#[must_use]
pub const fn begin_step(s: BranchState) -> StepBegin {
    let mut state = s;
    state.branch_issued = 0;
    StepBegin {
        state,
        instr_addr: s.pc,
        in_delay_slot: flag(s.branch_pending != 0),
    }
}

/// End of a step: PC / next-PC / branch-delay progression.
///
/// - `exception_raised != 0`: `pc` already holds the exception vector; branch
///   state is discarded and `next_pc = pc + 4` (ADR-005). Also used for the
///   interrupt-preempt path.
/// - In a delay slot whose instruction issued another branch: the inner
///   branch's target is ignored; the shared delay slot is next.
/// - In a delay slot otherwise: apply the outermost branch (target, or fall
///   through past the delay slot) and clear `branch_pending`.
/// - Not in a delay slot after a branch: the next instruction is its delay
///   slot.
/// - Otherwise: sequential.
#[must_use]
pub const fn advance(
    s: BranchState,
    instr_addr: u32,
    in_delay_slot: u32,
    exception_raised: u32,
) -> BranchState {
    let mut out = s;
    if exception_raised != 0 {
        out.branch_pending = 0;
        out.branch_issued = 0;
        out.next_pc = s.pc.wrapping_add(4);
        return out;
    }
    let seq = instr_addr.wrapping_add(4);
    if in_delay_slot != 0 {
        if s.branch_issued != 0 {
            out.delay_slot_pc = seq;
            out.pc = seq;
        } else {
            out.pc = if s.pending_branch_taken != 0 {
                s.pending_branch_target
            } else {
                s.delay_slot_pc.wrapping_add(4)
            };
            out.branch_pending = 0;
        }
    } else if s.branch_issued != 0 {
        out.delay_slot_pc = seq;
        out.branch_pending = 1;
        out.pc = seq;
    } else {
        out.pc = seq;
    }
    out.next_pc = out.pc.wrapping_add(4);
    out
}

/// `FlushPipeline` (branch half): drop pending branch state; `next_pc = pc + 4`.
#[must_use]
pub const fn flush_branch(s: BranchState) -> BranchState {
    let mut out = s;
    out.branch_pending = 0;
    out.branch_issued = 0;
    out.next_pc = s.pc.wrapping_add(4);
    out
}

/// Returns [`update_load_delay`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_update_load_delay(s: LoadDelayState) -> LoadDelayCommit {
    update_load_delay(s)
}

/// Returns [`queue_load`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_queue_load(
    s: LoadDelayState,
    index: i32,
    value: u32,
) -> LoadDelayState {
    queue_load(s, index, value)
}

/// Returns [`flush_load_delay`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_flush_load_delay(s: LoadDelayState) -> LoadDelayCommit {
    flush_load_delay(s)
}

/// Returns [`set_pending_branch`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_set_pending_branch(
    s: BranchState,
    target: u32,
    taken: u32,
) -> BranchState {
    set_pending_branch(s, target, taken)
}

/// Returns [`begin_step`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_begin_step(s: BranchState) -> StepBegin {
    begin_step(s)
}

/// Returns [`advance`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_advance(
    s: BranchState,
    instr_addr: u32,
    in_delay_slot: u32,
    exception_raised: u32,
) -> BranchState {
    advance(s, instr_addr, in_delay_slot, exception_raised)
}

/// Returns [`flush_branch`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_pipeline_flush_branch(s: BranchState) -> BranchState {
    flush_branch(s)
}

#[cfg(test)]
mod tests {
    use super::*;

    const IDLE: LoadDelayState = LoadDelayState {
        reg: NO_REG,
        value: 0,
        next_reg: NO_REG,
        next_value: 0,
    };

    fn ld(reg: i32, value: u32, next_reg: i32, next_value: u32) -> LoadDelayState {
        LoadDelayState {
            reg,
            value,
            next_reg,
            next_value,
        }
    }

    fn br(pc: u32) -> BranchState {
        BranchState {
            pc,
            next_pc: pc.wrapping_add(4),
            delay_slot_pc: 0,
            pending_branch_target: 0,
            branch_pending: 0,
            branch_issued: 0,
            pending_branch_taken: 0,
        }
    }

    // --- load delay ------------------------------------------------------

    #[test]
    fn idle_update_commits_nothing() {
        let c = update_load_delay(IDLE);
        assert_eq!(c.commit_reg, NO_REG);
        assert_eq!(c.state, IDLE);
    }

    #[test]
    fn load_commits_one_instruction_later() {
        // Step N: LW $1 queues. Step N's own update only shifts it into place.
        let s = queue_load(IDLE, 1, 0x2222_2222);
        let c = update_load_delay(s);
        assert_eq!(
            c.commit_reg, NO_REG,
            "not committed at the end of the load's own step"
        );
        assert_eq!(c.state, ld(1, 0x2222_2222, NO_REG, 0));
        // Step N+1 (delay slot): commits at its end.
        let c = update_load_delay(c.state);
        assert_eq!((c.commit_reg, c.commit_value), (1, 0x2222_2222));
        assert_eq!(c.state, IDLE);
    }

    #[test]
    fn pending_and_new_load_to_different_registers_both_commit_in_order() {
        // $1 pending this step, delay-slot instruction loads $2.
        let s = queue_load(ld(1, 0xAAAA, NO_REG, 0), 2, 0xBBBB);
        assert_eq!(s, ld(1, 0xAAAA, 2, 0xBBBB));
        let c = update_load_delay(s);
        assert_eq!((c.commit_reg, c.commit_value), (1, 0xAAAA));
        let c = update_load_delay(c.state);
        assert_eq!((c.commit_reg, c.commit_value), (2, 0xBBBB));
    }

    #[test]
    fn same_register_back_to_back_load_cancels_pending_commit() {
        // Last load wins: the pending $1 commit is dropped (value retained,
        // but reg cleared so it never reaches the register file).
        let s = queue_load(ld(1, 0xAAAA, NO_REG, 0), 1, 0xBBBB);
        assert_eq!(s, ld(NO_REG, 0xAAAA, 1, 0xBBBB));
        let c = update_load_delay(s);
        assert_eq!(c.commit_reg, NO_REG);
        let c = update_load_delay(c.state);
        assert_eq!((c.commit_reg, c.commit_value), (1, 0xBBBB));
    }

    #[test]
    fn queue_ignores_zero_and_out_of_range_registers() {
        let s = ld(3, 7, NO_REG, 0);
        for index in [0, -1, -2, 32, 33, i32::MIN, i32::MAX] {
            assert_eq!(queue_load(s, index, 0xDEAD), s, "index = {index}");
        }
        // Nearest valid neighbors do queue.
        assert_eq!(queue_load(s, 1, 9).next_reg, 1);
        assert_eq!(queue_load(s, 31, 9).next_reg, 31);
    }

    #[test]
    fn second_queue_in_one_step_replaces_the_first() {
        let s = queue_load(queue_load(IDLE, 4, 1), 5, 2);
        assert_eq!(s, ld(NO_REG, 0, 5, 2));
    }

    #[test]
    fn flush_commits_pending_and_clears_both_slots() {
        let c = flush_load_delay(ld(6, 0x66, 7, 0x77));
        assert_eq!((c.commit_reg, c.commit_value), (6, 0x66));
        assert_eq!(c.state, ld(NO_REG, 0x66, NO_REG, 0x77));
        assert_eq!(flush_load_delay(IDLE).commit_reg, NO_REG);
    }

    // --- branch ----------------------------------------------------------

    #[test]
    fn sequential_step() {
        let b = begin_step(br(0x100));
        assert_eq!((b.instr_addr, b.in_delay_slot), (0x100, 0));
        let s = advance(b.state, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!((s.pc, s.next_pc, s.branch_pending), (0x104, 0x108, 0));
    }

    #[test]
    fn taken_branch_applies_after_delay_slot() {
        let b = begin_step(br(0));
        let s = set_pending_branch(b.state, 8, 1);
        let s = advance(s, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!((s.pc, s.delay_slot_pc, s.branch_pending), (4, 4, 1));
        let b = begin_step(s);
        assert_eq!(
            (b.instr_addr, b.in_delay_slot, b.state.branch_issued),
            (4, 1, 0)
        );
        let s = advance(b.state, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!((s.pc, s.next_pc, s.branch_pending), (8, 12, 0));
    }

    #[test]
    fn not_taken_branch_falls_through_past_delay_slot() {
        let b = begin_step(br(0));
        let s = advance(set_pending_branch(b.state, 0x40, 0), 0, 0, 0);
        let b = begin_step(s);
        let s = advance(b.state, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!((s.pc, s.branch_pending), (8, 0));
    }

    #[test]
    fn branch_in_delay_slot_keeps_outer_target() {
        // Outer BEQ at 0 (taken -> 16); inner BNE at 4 (-> 12, ignored).
        let b = begin_step(br(0));
        let s = advance(set_pending_branch(b.state, 16, 1), 0, 0, 0);
        let b = begin_step(s);
        let s = set_pending_branch(b.state, 12, 1);
        assert_eq!((s.pending_branch_target, s.pending_branch_taken), (16, 1));
        let s = advance(s, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!((s.pc, s.delay_slot_pc, s.branch_pending), (8, 8, 1));
        let b = begin_step(s);
        let s = advance(b.state, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!((s.pc, s.branch_pending), (16, 0), "outer branch applies");
    }

    #[test]
    fn not_taken_outer_with_branch_in_delay_slot_falls_through_shared_slot() {
        let b = begin_step(br(0));
        let s = advance(set_pending_branch(b.state, 16, 0), 0, 0, 0);
        let b = begin_step(s);
        let s = advance(set_pending_branch(b.state, 12, 1), 4, 1, 0);
        let b = begin_step(s);
        let s = advance(b.state, b.instr_addr, b.in_delay_slot, 0);
        assert_eq!(
            s.pc, 12,
            "falls through after the shared delay slot (8 + 4)"
        );
    }

    #[test]
    fn exception_discards_branch_state_and_keeps_vector() {
        // Exception in a delay slot: pc already holds the vector.
        let mut s = br(0x8000_0080);
        s.branch_pending = 1;
        s.branch_issued = 1;
        s.pending_branch_target = 0x1234;
        s.pending_branch_taken = 1;
        s.delay_slot_pc = 0x104;
        let out = advance(s, 0x104, 1, 1);
        assert_eq!((out.pc, out.next_pc), (0x8000_0080, 0x8000_0084));
        assert_eq!((out.branch_pending, out.branch_issued), (0, 0));
        assert_eq!(out.delay_slot_pc, 0x104);
    }

    #[test]
    fn pc_arithmetic_wraps_like_uint32() {
        let s = advance(br(0xFFFF_FFFC), 0xFFFF_FFFC, 0, 0);
        assert_eq!((s.pc, s.next_pc), (0, 4));
        let s = flush_branch(br(0xFFFF_FFFC));
        assert_eq!(s.next_pc, 0);
        let mut e = br(0xFFFF_FFFC);
        e.next_pc = 0;
        assert_eq!(advance(e, 0, 0, 1).next_pc, 0);
    }

    #[test]
    fn flush_branch_drops_pending_state() {
        let mut s = br(0x200);
        s.branch_pending = 1;
        s.branch_issued = 1;
        s.next_pc = 0;
        let out = flush_branch(s);
        assert_eq!(
            (out.pc, out.next_pc, out.branch_pending, out.branch_issued),
            (0x200, 0x204, 0, 0)
        );
    }

    #[test]
    fn exports_match_pure_functions() {
        let s = ld(1, 2, 3, 4);
        assert_eq!(psx_cpu_pipeline_update_load_delay(s), update_load_delay(s));
        assert_eq!(psx_cpu_pipeline_queue_load(s, 5, 6), queue_load(s, 5, 6));
        assert_eq!(psx_cpu_pipeline_flush_load_delay(s), flush_load_delay(s));
        let b = br(0x10);
        assert_eq!(
            psx_cpu_pipeline_set_pending_branch(b, 1, 2),
            set_pending_branch(b, 1, 2)
        );
        assert_eq!(psx_cpu_pipeline_begin_step(b), begin_step(b));
        assert_eq!(
            psx_cpu_pipeline_advance(b, 0x10, 0, 0),
            advance(b, 0x10, 0, 0)
        );
        assert_eq!(psx_cpu_pipeline_flush_branch(b), flush_branch(b));
    }
}
