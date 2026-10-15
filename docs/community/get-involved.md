# Get involved — technical collaboration

Status: Community onboarding / invitation, **not** a claim of production readiness.

PSXRecompStudio is an independent, research-stage PS1 recompilation project. We are looking for **critical human technical feedback**, not just endorsements. Small, evidence-based contributions are valuable.

## Choose an entry point

| You know about… | A useful first contribution | Start here |
|---|---|---|
| PS1 BIOS / R3000A / hardware | Correct a documented behavior, identify an inaccurate assumption, propose a synthetic reproduction | [PS1 runtime issues](https://github.com/mao2009/PSXRecompStudio/issues?q=is%3Aissue+is%3Aopen+BIOS) · [CPU docs](../cpu/) |
| Static recompilation / compilers | Review lowering or indirect control flow assumptions; compare with reference implementations | [Recompiler roadmap](https://github.com/mao2009/PSXRecompStudio/issues/205) · [IR contract](../development/recompiler-ir-contract.md) |
| Runtime / C++ / Rust / .NET | Review state-transfer, FFI contracts, deterministic behavior, packaging and diagnostics | [Architecture](../../ARCHITECTURE.md) · [Rust FFI](../development/rust-ffi-contract.md) |
| Tests / CI / tooling | Add a fully redistributable synthetic fixture, deterministic repro, or focused contract test | [Generated fixtures](../development/generated-psx-exe-fixtures.md) · [Entry-task review](../development/contributor-entry-tasks.md) |
| Game preservation | Critique rights-holder workflows, asset provenance, rebuildability and maintenance needs | [Project mission](../project-mission.md) · [RRS vision](rrs-vision.md) |

**These are starting areas, not assignments or promises that an issue is unclaimed.** Check current status, owners and active PRs before starting.

## Three ways to help without a pull request

1. **Ask or answer one precise technical question.** Open an Issue describing the behavior, links to a reference, and what is known versus assumed.
2. **Review one specific contract or PR.** State the expected behavior, an observable counterexample, and how you would test it.
3. **Improve a reproducible test.** Favor synthetic MIPS/PS-X EXE fixtures and public documentation; never require redistributing a commercial game.

A useful first message could be:

> I'm reviewing the PS1 runtime / recompilation implementation. Is there a short, reproducible example of [specific behavior] and a reference contract? I can contribute an independent review or a synthetic test.

## AI use: transparent expectations

The maintainer relies heavily on AI coding agents for implementation, investigations, testing, documentation, and preliminary review. This **does not mean AI-generated code is independently verified**. Automated tests and CI are evidence of bounded properties, not proof of complete PS1 correctness.

We welcome direct, concrete criticism of AI-generated designs. Contributors should disclose relevant AI involvement when submitting or reviewing work and remain accountable for the accuracy, license provenance, tests, and explanations of their contributions. No one is expected to accept unreviewed AI-generated patches or provide free, unlimited reviews.

## How to propose a change

- Read [CONTRIBUTING.md](../../CONTRIBUTING.md), the relevant subsystem's docs, and the Issue.
- For larger changes, discuss the design first; for small fixes, keep the scope independently reviewable.
- Include observed vs expected behavior, operating system/toolchain, smallest **redistributable** reproduction, tests actually run and known gaps.
- Avoid linking copyrighted or private dumps. Commercial real-game evidence may be described without uploading protected files.
- Maintain clear separation between **verified interpreter behavior**, **bounded AOT evidence**, and **future goals**.

### Current verification boundaries

The [Persona E2E status](../v0.1.0/persona-e2e-status.md) and [capability matrix](../status/capabilities.md) contain detailed, evolving evidence. Persona opening screenshots came from the **OpenBIOS interpreter path**; they do not demonstrate full-game or native AOT parity. Do not infer additional compatibility from them.

## Community norms

Be respectful and technically specific. Critique code, claims, and evidence rather than people. Be welcoming to new contributors while protecting reviewers' time. Attribution, provenance, and legally distributable inputs matter.

[Issues](https://github.com/mao2009/PSXRecompStudio/issues) · [Pull requests](https://github.com/mao2009/PSXRecompStudio/pulls) · [Documentation](../README.md)
