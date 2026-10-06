# ADR-012: Function discovery and CFG hand-off

Status: accepted for Issue #210

## Decision

Function discovery is a projection over the existing PS-X EXE analysis result. The
`R3000aDecoder`, `R3000aBranchSemantics`, `R3000aJumpSemantics`, `BasicBlockBuilder`,
and deterministic artifact serializer remain the single sources of truth. Issue #210
adds no decoder, executable parser, or second basic-block builder.

`FunctionDiscoveryArtifact` carries the executable text-region identity, entry point,
stable function identities, block lists, CFG edges, direct call targets, recognized
returns, and unresolved indirect-flow source addresses. Candidates are seeded by the
PS-X EXE entry point, direct link targets, and optional explicit entries. Traversal
does not guess indirect targets, does not cross a direct call into the callee, and
keeps delay-slot instructions in the source block.

Functions and blocks are ordered by entry/start address. Edges are ordered by source,
target, then kind. The artifact is canonical UTF-8/LF JSON and exposes a SHA-256 for
two-run reproducibility checks. It is an input contract for later #207 lowering; it
does not contain generated code or CPU-lowering policy.

The existing real-ROM report remains backward-compatible: its function projection is
optional for callers constructing reports directly, and the existing four-file
real-ROM artifact format remains unchanged for reports without the projection.

## Amendment: reachable-program roots (Issue #644)

`ReachableProgramBuilder` (the recompiler's statically reachable-program discovery) is a
separate consumer of the same decoder and jump/branch semantics; it does not consume
`FunctionDiscoveryArtifact`. Its roots are exactly:

1. the PS-X EXE entry point, and
2. caller-supplied explicit roots (`ReachableProgramBuilder.Build(..., additionalRoots)`,
   `psxrecomp run|recompile --entry-root 0xPC`).

All roots are discovered in one shared pass, so leaders, delay slots, load-delay pairs and
conflict detection are common to every root. Each explicit root must be a 4-byte-aligned PC
naming a complete word inside the text image, otherwise the build fails closed with an
`InvalidFlow` diagnostic naming the root. Duplicate roots, the entry point, and PCs already
reached are accepted. With no explicit roots the result is identical to entry-only discovery.

Roots are an input, never a guess. The builder does not derive roots from
`FunctionDiscoveryArtifact` entries, does not scan data words for pointers, and does not treat
the whole text region as block candidates; #639 measured each of those (function entries added
0 blocks; blanket pointer harvesting fail-closed 432 of 562 candidates and added 9,170 blocks;
11.8% of Persona's text words decode as Reserved). A transfer that lands on an in-image PC with
no compiled block still stops the run (`UNRESOLVED_TRANSFER_IN_IMAGE`); the runtime does not
claim or compile it. Interpreter fallback and mixed execution remain out of scope (ADR-015, #249).

## Amendment: opt-in mixed execution for runtime-discovered in-image indirect targets (Issue #693)

The #644 amendment above kept two things out of scope: a transfer that lands on an in-image PC with no compiled block stops the run, and interpreter fallback / mixed execution is deferred. #639 and #693 measured why that is not enough for a real title: the guest registers a callback in its own RAM table (an ordinary store, no Runtime-visible registration event), the table is read at run time, and a register-indirect `JALR` enters the callback. The target exists in the executable text image and is only *discovered at run time*; no static root source (function entries, whole text, data-pointer or immediate harvesting) is both correct and fail-closed (#639: 432 of 562 data-pointer candidates fail closed; #693: 70 of 85 immediate-constant candidates fail closed).

**Decision.** Mixed execution is permitted for the **static main executable image only**, under a strict, opt-in contract. This is a limited exception, not a reversal of the root policy:

1. **Roots stay inputs.** `ReachableProgramBuilder` still has exactly the entry point and caller-supplied explicit roots; nothing here guesses a root, adds a root, or changes any generated block. Explicit `--entry-root` remains the debugging/reproduction mechanism and is unchanged.
2. **Opt-in.** Mixed execution is off by default (`RecompiledHostExecutionEngine` / `RecompiledArtifactLauncher` take a null `MixedFallbackOptions`; the CLI gate is `psxrecomp run --mixed-fallback`). Off, the stop is exactly the `UNRESOLVED_TRANSFER_IN_IMAGE` described above.
3. **Eligibility is strict and all-of.** The artifact offers a transfer with no compiled block *and* the target is a 4-byte-aligned PC inside the PS-X EXE text image, *and* the offered transfer is the runtime target of the artifact's most recent register-indirect (`JR`/`JALR`) block exit, *and* it is neither a BIOS vector nor an exception vector, *and* mixed execution was requested. Anything else (unaligned, outside the image, RAM-generated or self-modifying code, a direct transfer, a vector) keeps the existing fail-closed diagnostics. Arbitrary RAM execution is never permitted.
4. **The interpreter runs; it does not compile.** The fallback executes with the interpreter's own semantics (ADR-015: the same step loop, BIOS vector dispatch, SYSCALL service and kernel exception handler), never a second CPU implementation, and records the target as evidence (count, instructions, return PC) so a target can later be promoted to an explicit root.
5. **Return only at a clean compiled block entry.** Control goes back to the artifact when the interpreter is about to execute a PC the artifact compiled **and** the native CPU reports no pending branch delay slot and no uncommitted load (a read-only native query; nothing is inferred from the previous instruction) and no guest interrupt handler is running.
6. **Fail closed.** A budget exhaustion, an exception the interpreter loop does not service, a PC outside the image, a failed RAM write-back check, or any malformed protocol message stops the run with a dedicated diagnostic (`ARTIFACT_FALLBACK_*`); nothing continues silently, and no side is left half-updated.

The state ownership, RAM synchronization, device/time continuity, budgets and protocol are defined in the ADR-025 amendment; the engine ownership in ADR-015; the budget unit in ADR-016; and ADR-014 decision 3(f) is superseded in part.

**Known limitation.** The "text image" of a PS-X EXE is its whole loaded segment, which includes the EXE's own data (a guest callback table lives inside it). Eligibility is therefore a *region* test, not a content test: code the guest writes into the image range after load is neither detected nor excluded, exactly as it is not for the compiled blocks, which are static. Code outside the image range (RAM-generated code, overlays) is never executed by the fallback.

**Relationship to #249 Stage 5.** #249 says the static main EXE is not sent to an interpreter and fallback is for runtime-loaded code. This amendment is a limited exception for one class: a **runtime-discovered, in-image, indirect** target of the static main EXE, because that class cannot be rooted ahead of time and the alternative is stopping a correct guest. It keeps #249's principles (correctness before coverage; machine-readable evidence of every fallback; no second CPU semantics) and does not touch runtime-loaded overlays, content identity or native overlay compilation, which stay with #249.
