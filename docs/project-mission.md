# Project Mission: Commercial Re-release and Preservation

Status: Stable
Authority: Project direction (strategic goals, not an implementation or compatibility claim)

## Mission

PSXRecompStudio aims to help original publishers and authorized rights holders bring PlayStation 1-era games back to market on modern platforms, including when their original source code, build tools, or development environments have been lost.

The project develops an open-source binary analysis, static recompilation, and host-runtime foundation. Its long-term goal is to turn legally supplied retail game binaries into **maintainable, verifiable porting inputs**, rather than merely reproduce an emulator's gameplay experience.

This is a long-term objective, **not a claim that complete commercial-game recompilation or commercially shippable ports work today**.

## Why recompilation?

Traditional emulation is an excellent, often lower-cost solution for playing and re-releasing old games. Recompilation does not inherently outperform it and should not be pursued just to duplicate emulator functionality.

The intended differentiator is a **porting and maintenance workflow** for authorized developers:

- Recover executable behavior without requiring original source or obsolete toolchains.
- Generate host-targeted code/artifacts with traceable relationships to original executable regions.
- Allow game-specific fixes, instrumentation and features to be developed, tested, and maintained.
- Incrementally replace hardware-specific interfaces with portable implementations when useful.
- Support reproducible builds, differential verification, diagnostics, and future platform retargeting.
- Reduce the engineering cost and risk of authorized re-releases, where evidence shows it can.

A generated binary is not equivalent to recovered human-readable source code. Producing maintainable modification surfaces requires additional tooling, symbols/metadata, documentation and validation.

## Priorities and decision criteria

1. **Correctness and coverage first.** Expand real-game execution, multi-image/runtime-loaded code support, and BIOS/hardware compatibility with transparent coverage metrics.
2. **Reliable output.** Favor deterministic, reproducible builds and evidence-backed differential checks against reference behavior.
3. **Commercial adoption readiness.** Keep runtime and generated-artifact licenses and dependency provenance understandable to commercial teams; avoid unlicensed proprietary assets and undocumented redistribution requirements.
4. **Porting and extensibility.** Design interfaces for game-specific patches, platform services, controller/input, saves and optional modern enhancements without hard-coding a single game into the generic core.
5. **Maintainability.** Preserve mappings between source binary regions and generated code, diagnostics, and repeatable toolchains.
6. **Measured business value.** Eventually evaluate compatibility, required manual porting effort, QA burden, platform constraints, and ongoing maintenance cost against emulation and other alternatives.

Performance and smaller runtime footprints are welcome outcomes, **not the main project objective**, and require measured comparisons.

## Milestones (intent, not release commitments)

- **Near term (v0.1):** establish and validate a bounded, honest end-to-end recompilation/execution foundation. Do not market this as complete game portability.
- **Medium term:** broaden real-title compatibility, generated-code coverage, recompile/patch workflows and host-platform support.
- **Long term:** provide a licensable, documented, reproducible porting foundation that authorized publishers can evaluate for commercial re-releases.

## Boundaries

- PSXRecompStudio does not grant rights to game code, data, music, trademarks, BIOS, or platform SDKs. Commercial distribution requires independent rights clearance.
- Console deployment requires the platform holder's authorization, SDK/toolchain access, certification and any other applicable requirements.
- The project will not distribute commercial game images, firmware, or proprietary SDKs.
- Recompilation is not a guarantee of portability, performance improvement, source recovery or lower cost. Each title must be evaluated.
- Emulators remain a valuable reference and valid deployment option; they are not categorically inferior.

## Applying this direction

Use this mission when evaluating roadmap priorities, architectural choices, proposed Issues, and partnerships. Prefer work that moves the project toward **authorized, maintainable commercial ports** while preserving correctness, legal boundaries and the existing evidence-driven development process. Track specific work in GitHub Issues, not in this document.
