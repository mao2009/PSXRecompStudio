#pragma once

#include <cassert>
#include <cstdint>
#include <string>

#include "psx_core.h"

// Golden Trace format (Issue #157).
//
// Captures per-instruction execution state purely through the public C ABI
// (PSXCore_*), so the same capture logic can later be pointed at any
// execution engine that exposes this ABI -- the current native interpreter,
// and future recompiler backends (ARCHITECTURE.md) -- to compare their
// instruction-by-instruction traces against the same fixture.
//
// Fields intentionally mirror the ADR-005 PC model (pc / next_pc) and the
// minimal state needed to detect a divergence between two execution engines:
// the fetched instruction word, every GPR write the step produced, and HI/LO
// for multiply/divide instructions.
//
// A single MIPS I step can retire at most two GPR writes: the current
// instruction's destination plus one load-delay commit (ADR-004). The trace
// records all of them so a comparison can never silently lose a write; the
// load-delay slot is where a step most often produces both.
struct GoldenTraceEntry {
    uint32_t pc = 0;  // Address of the instruction that was executed.
    uint32_t instruction = 0;  // Raw instruction word, fetched via the memory bus.
    std::string mnemonic;  // Decoded mnemonic (trace readability only).

    // GPR writes produced by this step (up to kMaxGprWritesPerStep). Each
    // entry records the register's net before/after value observed across the
    // step, so a register written more than once (e.g. an immediate write that
    // overrides a pending load to the same register) is collapsed to its final
    // value, matching the register file's architectural end-of-step state.
    static constexpr int kMaxGprWritesPerStep = 2;
    int reg_count = 0;                                  // # of GPR writes in this step.
    int reg_index[kMaxGprWritesPerStep] = {-1, -1};     // GPR index written, -1 if unused.
    uint32_t reg_before[kMaxGprWritesPerStep] = {};
    uint32_t reg_after[kMaxGprWritesPerStep] = {};

    uint32_t hi_before = 0;
    uint32_t hi_after = 0;
    uint32_t lo_before = 0;
    uint32_t lo_after = 0;
    uint32_t next_pc = 0;  // PC after Step() (ADR-005: next_pc).
};

// Minimal mnemonic decoder for trace labeling. Not a substitute for the
// project's instruction decoder/semantics (docs/adr/002); it exists only to
// make a Golden Trace readable, and covers the opcodes exercised by the
// current fixtures. Extend as new instructions gain trace coverage.
inline std::string DecodeMnemonicForTrace(uint32_t instruction) {
    uint32_t opcode = instruction >> 26;
    if (opcode == 0x00) { // SPECIAL
        uint32_t funct = instruction & 0x3F;
        switch (funct) {
            case 0x20: return "ADD";
            case 0x21: return "ADDU";
            case 0x00: return (instruction == 0) ? "NOP" : "SLL";
            default:   return "SPECIAL";
        }
    }
    switch (opcode) {
        case 0x08: return "ADDI";
        case 0x09: return "ADDIU";
        case 0x23: return "LW";
        default:   return "UNKNOWN";
    }
}

// Executes one PSXCore_Step() and records a Golden Trace entry by diffing
// GPR/HI/LO state observed strictly via the public C ABI. Because it never
// touches native-only state, the exact same capture function can drive a
// future native-vs-recompiler comparison test.
inline GoldenTraceEntry CaptureGoldenTraceStep(PSXCore* core) {
    GoldenTraceEntry entry;
    entry.pc = PSXCore_GetPC(core);
    entry.instruction = PSXCore_ReadMemory32(core, entry.pc);
    entry.mnemonic = DecodeMnemonicForTrace(entry.instruction);

    uint32_t gpr_before[32];
    for (int i = 0; i < 32; i++) {
        gpr_before[i] = PSXCore_GetGPR(core, i);
    }
    entry.hi_before = PSXCore_GetHI(core);
    entry.lo_before = PSXCore_GetLO(core);

    PSXCore_Step(core);

    // Record every register written during the step, not just the first: the
    // load-delay slot of a load commits the pending load *and* can write the
    // current instruction's own destination in the same step. GPR[0] ($zero)
    // never changes by definition. The MIPS I model retires at most
    // kMaxGprWritesPerStep writes per step; if that invariant is ever broken,
    // the assert fails loudly instead of silently dropping a trace entry.
    for (int i = 1; i < 32; i++) {
        uint32_t after = PSXCore_GetGPR(core, i);
        if (after != gpr_before[i]) {
            assert(entry.reg_count < GoldenTraceEntry::kMaxGprWritesPerStep);
            entry.reg_index[entry.reg_count] = i;
            entry.reg_before[entry.reg_count] = gpr_before[i];
            entry.reg_after[entry.reg_count] = after;
            entry.reg_count++;
        }
    }
    entry.hi_after = PSXCore_GetHI(core);
    entry.lo_after = PSXCore_GetLO(core);
    entry.next_pc = PSXCore_GetPC(core);
    return entry;
}
