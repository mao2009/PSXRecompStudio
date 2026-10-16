# Contributing to PSXRecompStudio

Thanks for contributing to PSXRecompStudio. This project spans managed C#, native C++/Rust, PS1 runtime hardware, recompilation, diagnostics, tooling, and documentation, so a small-looking change can cross architectural boundaries unexpectedly.

This guide is for **human contributors**. AI development agents use [AGENTS.md](AGENTS.md) and the repository Skills instead; those agent-only operating rules are intentionally not duplicated here.

## First time here?

You do **not** need to submit a pull request to help. Start with the [human collaboration guide](docs/community/get-involved.md), which maps expertise to specific subsystems and gives examples of useful technical feedback. For long-term architecture discussions, see the [proposed RRS vision](docs/community/rrs-vision.md).

This is a heavily AI-assisted project. Implementation and preliminary review often rely on coding agents. Human contributors are invited to challenge assumptions and request reproducible evidence; nobody is expected to validate large AI-generated patches without a bounded, agreed scope. State AI involvement, provenance, and validation limits in your contributions.

## Before starting

1. Read the Issue completely and confirm that it is still open.
2. Check whether an implementation PR already exists or whether the requested behavior is already present on current `main`.
3. Before creating new code, check appropriate existing OSS implementations. Prefer unchanged licensed reuse, then licensed adaptation, then behavior/reference tests; implement anew only when reuse is unsuitable. Require source-file-level provenance, dependency and license approval before incorporating third-party code (see [References and reuse policy](docs/REFERENCES.md)).
4. Identify the owning subsystem and the relevant architecture or contract documentation.
5. Keep the change inside the Issue's stated scope and non-goals.
6. Prefer a focused branch and PR for one independently reviewable change.

If an Issue does not clearly state its scope, affected paths, acceptance criteria, or required validation, ask for clarification or improve the Issue before starting implementation.

## Architecture and contracts

Start with:

- [ARCHITECTURE.md](ARCHITECTURE.md)
- [Documentation index](docs/README.md)
- [Architecture documentation](docs/architecture/README.md)
- [ADRs](docs/adr/)

The nearest subsystem documentation and tests are part of the contract. Do not introduce title-specific behavior into generic runtime or recompilation code unless the architecture explicitly calls for it.

## Development setup

Install these tools (versions and details: [Native library build](docs/development/native-library-build.md)):

| Tool | Notes |
|---|---|
| .NET SDK 10 | CI uses `10.x`. |
| CMake and Ninja | The native library is configured with `-G Ninja`. |
| Rust (`cargo`) | Required for the native build. `src/PSXRecomp.Native/rust/rust-toolchain.toml` pins the toolchain. |
| C/C++ compiler | Windows: MSVC or MinGW-w64 GCC. Linux: GCC or Clang. macOS: Apple Clang. |

Platform coverage in CI:

- **Linux (Ubuntu 24.04):** for a pull request that changes code, the native build and tests, the .NET build and tests, and the headless GUI tests run. For a documentation-only pull request, the native and .NET build and test steps are skipped and the GUI tests do not run. The artifact policy check and the aggregate `CI Gate` run on every pull request.
- **Windows and macOS:** the cross-platform workflow runs on a schedule, on manual dispatch, and on a pull request that has the `ci:cross-platform` label.
- Other platforms are not covered by CI. If you test on one, say so in your PR and do not claim support.

Build and test from the repository root:

```bash
# Native library (C++ / Rust)
cmake -S src/PSXRecomp.Native -B build/native -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build/native --parallel
ctest --test-dir build/native --output-on-failure

# .NET solution and tests
dotnet build src/PSXRecompStudio.slnx --configuration Release
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release
```

The `build/` and `out/` directories are git-ignored and must not be committed (see the [artifact policy](docs/development/artifact-policy.md)).

Checks to run before opening a PR:

```bash
git diff --check
pwsh ./scripts/ci/check-artifact-policy.ps1               # before committing binary-like files
python scripts/ci/check-community-links.py                # when touching the community docs listed in the script
pwsh ./scripts/ci/check-documentation-translations.ps1    # when touching README*.md or docs
```

`check-community-links.py` validates local links and anchors only in the 10 community-facing files listed in its `FILES` array (README, CONTRIBUTING, the `get-involved` and RRS pages, and their Pages counterparts). If you change a link in any other document, check that link by hand.

Documentation-only changes (`docs/`, `README*.md`, `CONTRIBUTING.md`, `.github/ISSUE_TEMPLATE/*.md`) skip the native and .NET build and test steps and the GUI tests by design.

## Validation

A contributor-ready task should state the validation it requires. Use the smallest test set that proves the change, then run the broader checks required by the touched subsystem or CI.

Typical evidence levels are:

- **Synthetic** — repository-owned unit, contract, differential, or generated-fixture evidence.
- **Fixture** — deterministic repository-owned generated input that exercises a production path. For PS-X EXE fixtures, see [Source-generated PS-X EXE fixtures](docs/development/generated-psx-exe-fixtures.md).
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

Prefer `jj` over `git` locally? It is optional and does not change the GitHub flow; see the [Optional Jujutsu workflow](docs/development/jujutsu.md).

Keep implementation, tests, and documentation synchronized when the change modifies a public or architectural contract.

Workflow notes:

- Open a **draft** PR as soon as you want early feedback; mark it ready once the PR template's validation section is filled in. The [PR template](.github/PULL_REQUEST_TEMPLATE.md) asks only for what reviewers need.
- The `CI Gate` job in [ci.yml](.github/workflows/ci.yml) aggregates the Linux checks.
- Commit subjects follow the `type: subject` style used in the history (`feat`, `fix`, `docs`, `test`, `ci`, `refactor`), for example `docs: make first five minutes actionable for human contributors (#778)`. Put the issue number in the subject or body when there is one.
- Report AI involvement and the validation you personally ran. Reviewers rely on that record.

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
