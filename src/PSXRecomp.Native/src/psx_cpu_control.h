#pragma once

#include <cstdint>

/*
 * PSXCpu branch / jump decision and target arithmetic (BEQ..BGEZAL,
 * J/JAL/JR/JALR). Implemented in Rust (`../rust/src/cpu_control.rs`, Issue
 * #526); this header only declares that crate's internal C ABI for use by
 * psx_cpu_control.cpp.
 *
 * Every function takes the instruction's PC and register *values* and returns
 * the result by value; Rust never allocates or retains state, and no function
 * can panic. The linking forms reuse the non-linking function (BLTZAL = bltz,
 * BGEZAL = bgez, JAL = j, JALR = jr) and the caller writes `link`. Must match
 * `ControlResult` in cpu_control.rs field-for-field.
 */
struct PSXControlResult {
    uint32_t target;
    uint32_t taken; // 0 or 1
    uint32_t link;  // PC + 8
};

extern "C" {
PSXControlResult psx_cpu_control_beq(uint32_t pc, uint32_t rs, uint32_t rt, int16_t offset);
PSXControlResult psx_cpu_control_bne(uint32_t pc, uint32_t rs, uint32_t rt, int16_t offset);
PSXControlResult psx_cpu_control_blez(uint32_t pc, uint32_t rs, int16_t offset);
PSXControlResult psx_cpu_control_bgtz(uint32_t pc, uint32_t rs, int16_t offset);
PSXControlResult psx_cpu_control_bltz(uint32_t pc, uint32_t rs, int16_t offset);
PSXControlResult psx_cpu_control_bgez(uint32_t pc, uint32_t rs, int16_t offset);
PSXControlResult psx_cpu_control_j(uint32_t pc, uint32_t index);
PSXControlResult psx_cpu_control_jr(uint32_t pc, uint32_t rs);
}
