# Contributing to PSXRecompStudio

Thanks for contributing to PSXRecompStudio. This project spans managed C#, native C++/Rust, PS1 runtime hardware, recompilation, diagnostics, tooling, and documentation, so a small-looking change can cross architectural boundaries unexpectedly.

This guide is for **human contributors**. AI development agents use [AGENTS.md](AGENTS.md) and the repository Skills instead; those agent-only operating rules are intentionally not duplicated here.

## Before starting

1. Read the Issue completely and confirm that it is still open.
2. Check whether an implementation PR already exists or whether the requested behavior is already present on current `main`.
3. Identify the owning subsystem and the relevant architecture or contract documentation.
4. Keep the change inside the Issue's stated scope and non-goals.
5. Prefer a focused branch and PR for one independently reviewable change.

If an Issue does not clearly state its scope, affected paths, acceptance criteria, or required validation, ask for clarification or improve the Issue before starting implementation.

## Architecture and contracts

Start with:

- [ARCHITECTURE.md](ARCHITECTURE.md)
- [Documentation index](docs/README.md)
- [Architecture documentation](docs/architecture/README.md)
- [ADRs](docs/adr/)

The nearest subsystem documentation and tests are part of the contract. Do not introduce title-specific behavior into generic runtime or recompilation code unless the architecture explicitly calls for it.

## Validation

A contributor-ready task should state the validation it requires. Use the smallest test set that proves the change, then run the broader checks required by the touched subsystem or CI.

Typical evidence levels are:

- **Synthetic** — repository-owned unit, contract, differential, or generated-fixture evidence.
- **Fixture** — deterministic repository-owned generated input that exercises a production path.
- **Legal real input** — user-supplied, legally owned PS1 input used only where real-title evidence is necessary.

Never commit ROMs, disc images, PS-X executables extracted from commercial software, Sony BIOS images, save data, credentials, or other private/copyrighted artifacts.

## Pull requests

A focused PR should:

- explain the problem and the implemented boundary;
- link the Issue it resolves;
- list the targeted tests or checks actually run;
- call out intentionally deferred work and non-goals;
- avoid unrelated cleanup or refactoring;
- preserve fail-closed behavior when unsupported hardware or instructions remain.

Keep implementation, tests, and documentation synchronized when the change modifies a public or architectural contract.

## Contributor-ready Issues

When authoring or refining a bounded implementation task, use the
[Contributor-ready task template](.github/ISSUE_TEMPLATE/contributor-ready-task.md).

The template asks for:

- context and motivation;
- allowed scope and explicit non-goals;
- relevant source paths and normative documentation;
- acceptance criteria;
- targeted and broader validation;
- required evidence level;
- dependencies/blockers;
- expected complexity and prerequisite knowledge.

A `good first issue` label should only be used when the architecture surface is genuinely bounded and a new contributor can complete the task without reconstructing hidden project context. Large roadmap, hardware-correctness, or cross-layer issues should not receive that label merely because an individual code edit looks small.

See [Contributor entry-task review](docs/development/contributor-entry-tasks.md) for examples of how current open Issues are evaluated against this standard.
