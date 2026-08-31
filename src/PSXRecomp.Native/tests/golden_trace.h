#pragma once

#include <cassert>
#include <cstdint>
#include <string>

#include "psx_core.h"
#include "psx_cpu.h"
#include "psx_core_test_hooks.h"

// Golden Trace format (Issue #157).
//
// Captures per-instruction execution state so two execution engines can be
// compared instruction-by-instruction against the same fixture -- the current
// native interpreter and future recompiler backends (ARCHITECTURE.md).
//
// GPR writes are captured as retirement WRITE EVENTS, not as net register
// changes: the CPU reports each register-file write at the moment it retires,
// in retirement order (pending load-delay commit first, then the current
// instruction's result write). The harness attaches the CPU's write-event
// recorder around the single step it executes (see PSXCpu::SetGprWriteTrace);
// a recompiler backend participating in a comparison would surface the same
// per-step write stream through its own channel.
//
// Fields mirror the ADR-005 PC model (pc / next_pc) and the minimal state
// needed to detect a divergence between two execution engines: the fetched
// instruction word, every GPR retirement write the step produced, and HI/LO.
//
// A single MIPS I step retires at most kMaxGprWritesPerStep writes: the current
// instruction's destination plus one load-delay commit (ADR-004). All of them
// are recorded so a comparison can never silently lose a write; the load-delay
// slot is where a step most often produces both.
struct GoldenTraceEntry {
    uint32_t pc = 0;  // Address of the instruction that was executed.
    uint32_t instruction = 0;  // Raw instruction word, fetched via the memory bus.
    std::string mnemonic;  // Decoded mnemonic (trace readability only).

    // GPR retirement writes produced by this step (up to kMaxGprWritesPerStep),
    // recorded in the order the CPU retired them. Multiple writes to the same
    // register in one step are kept as separate events, and a write whose value
    // equals the register's prior value is still recorded -- the stream reflects
    // each retirement, not the step's net register changes.
    static constexpr int kMaxGprWritesPerStep = PSXCpu::kMaxGprWritesPerStep;
    int reg_count = 0;                                  // # of GPR retirement writes in this step.
    int reg_index[kMaxGprWritesPerStep] = {-1, -1};     // GPR index written, -1 if unused.
    uint32_t reg_before[kMaxGprWritesPerStep] = {};     // Value immediately before this write in the retirement stream.
    uint32_t reg_after[kMaxGprWritesPerStep] = {};      // Value written by this retirement write.

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

// Executes one PSXCore_Step() and records a Golden Trace entry. The harness
// attaches the CPU's GPR write-event recorder for the lifetime of the single
// step, so each register-file write is captured at the moment the CPU retires
// it -- instruction-result writes at their write site and the pending load-delay
// commit at the start of the step (retirement order). External writes performed
// before/after the capture (e.g. PSXCore_SetGPR seeding) never reach the
// recorder, keeping the trace a record of the step alone.
inline GoldenTraceEntry CaptureGoldenTraceStep(PSXCore* core) {
    GoldenTraceEntry entry;
    entry.pc = PSXCore_GetPC(core);
    entry.instruction = PSXCore_ReadMemory32(core, entry.pc);
    entry.mnemonic = DecodeMnemonicForTrace(entry.instruction);
    entry.hi_before = PSXCore_GetHI(core);
    entry.lo_before = PSXCore_GetLO(core);

    PSXCpu* cpu = static_cast<PSXCpu*>(PSXCoreGetCpuForTrace(core));
    PSXCpu::GprWriteTrace writes;
    cpu->SetGprWriteTrace(&writes);

    PSXCore_Step(core);

    cpu->SetGprWriteTrace(nullptr);

    // Copy the recorded retirement events into the trace entry. reg_count can
    // never exceed kMaxGprWritesPerStep: the CPU enforces that bound (see
    // PSXCpu::RecordGprWrite), so the copy below stays within the entry arrays.
    entry.reg_count = writes.count;
    for (int w = 0; w < writes.count; w++) {
        entry.reg_index[w] = writes.events[w].index;
        entry.reg_before[w] = writes.events[w].before;
        entry.reg_after[w] = writes.events[w].value;
    }
    entry.hi_after = PSXCore_GetHI(core);
    entry.lo_after = PSXCore_GetLO(core);
    entry.next_pc = PSXCore_GetPC(core);
    return entry;
}
