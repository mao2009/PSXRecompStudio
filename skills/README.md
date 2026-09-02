# Skills

**Status:** Draft

**Authority:** Reference

**Related Issues:** #85, #84, #81, #88, #89, #94, #174

## Purpose

Reusable development-process knowledge for AI agents, kept as platform-agnostic
markdown so any agent (OpenCode / Codex / Claude Code / other) can read it
directly.

Content is deliberately separated into three kinds:

```text
skills/
├── common/            # Generic, portable skills (no project specifics)
│   ├── process/       #   Gate / execution / reporting skills
│   │   ├── adr/         #   ADR authoring / maintenance (#84)
│   │   ├── batch/       #   Parallel Issue execution Orchestrator (#155)
│   │   ├── commit-message/ #   Commit Message authoring skill (#94)
│   │   ├── doc-sync/
│   │   ├── merge/       #   Safe PR Merge Skill (#146)
│   │   ├── reporting/   #   Completion-reporting process skill (#88)
│   │   └── self-review/
│   └── task/          #   Per-task-type work templates (#174)
│       ├── common/       #   Project-wide universal rules (the Common Skill)
│       ├── research/     #   Research / investigation procedure
│       ├── implementation/ # Implementation procedure
│       ├── review/       #   Standard review viewpoints + report format
│       └── issue/        #   Issue authoring / updating procedure
│       #   (release is intentionally NOT created yet — see the note below)
└── project/           # Per-project inputs and project-scoped skills
    └── psxrecomp-studio/
        ├── profile.md
        └── real-rom-analysis/  # Real-ROM analysis / verification flow (#213)
            └── SKILL.md
```

- `common/` skills must stay free of MIPS/R3000A/analyzer/test-command details.
  Everything project-specific belongs under `project/<name>/`: the profile that
  feeds the common skills, plus any skill that is inherently about this project's
  domain (disc images, R3000A decoding) and therefore cannot be made portable.
- Porting a skill to another project = copying `common/` and writing a new profile.

## Task skills vs. gate/execution skills

The skills under `common/` serve two deliberately distinct roles:

- **Task skills** (`common/task/`) prescribe *how an agent carries out a unit
  of work*: research, implementation, review, and issue authoring. They are
  the reusable work templates that standardize the agent's procedure and
  Definition of Done.
- **Gate and execution skills** (`common/process/`) prescribe *what must hold
  around that work*: the pre-PR self-review gate, the documentation
  synchronization gate, parallel issue orchestration (batch), and safe merge —
  and the completion-reporting process skill standardizes the final report an
  agent produces for the executed work.

Task and gate/execution skills have distinct, non-overlapping responsibilities
and are not merged into one "super" skill. The Common Skill
(`common/task/common/SKILL.md`) holds only project-wide universal rules and is
referenced (assumed) by the other task skills; it does not duplicate the process
gates.

## Current contents

| Skill | Responsibility | Introduced by |
|---|---|---|
| `common/process/adr` | ADR authoring / maintenance: the "is an ADR needed" decision, SSOT / matrix / existing-ADR preflight, sequential numbering, standard Context / Decision / Consequences structure, traceability to Issues / PRs / code / tests, consistency checks, the review-feedback loop, and agent rules | #84 |
| `common/process/doc-sync` | Documentation synchronization gate: impact mapping, minimal updates, recorded no-op decisions | #89 |
| `common/process/self-review` | Mandatory pre-PR self-review gate + external-review feedback loop | #85 |
| `common/process/batch` | Parallel Issue execution Orchestrator with dependency scheduling, retry, failure isolation, and serial merge via Merge Skill | #145, #155 |
| `common/process/commit-message` | Commit Message authoring skill: Conventional Commits format, type/scope/subject policy, per-commit change granularity and atomicity, Issue-linkage trailers (`Refs`/`Fixes`/`Closes`), amend/rebase handling, generated-commit marking, and AI judgement rules for deriving the message from the actual diff | #94 |
| `common/process/merge` | Safe PR Merge Skill with mandatory approval → rebase → validation → normal merge flow | #146 |
| `common/process/reporting` | Completion-reporting process skill: standard final report (investigation/SSOT, implementation summary, design decisions, changed files, targeted/related/full tests, build/analyzer/lint applicability, real PASS/FAIL/NOT RUN semantics, existing-vs-new failure separation, git status/diff/commit evidence, remaining work and Issue/PR state) | #88 |
| `common/task/common` | Project-wide universal rules (Architecture SSOT, Issue as SSOT, ground-truth verification, no fabricated results, scope discipline, Definition of Done) | #174 |
| `common/task/research` | Research / investigation procedure; standard report (Current State, Requirements, Findings, Constraints, Alternatives, Recommendation, Scope, Test Strategy, Risks, Open Questions); fact/inference/proposal separation | #174 |
| `common/task/implementation` | Implementation procedure (Issue confirm → repo state → related survey → SSOT check → approach → implement → test → build/analyzer/E2E → diff review → DoD); Definition of Done | #174 |
| `common/task/review` | Standard review viewpoints (requirements fit, SSOT, correctness, security, error handling, tests, regression, unintended changes) + report format | #174 |
| `common/task/issue` | Issue authoring / updating so an Issue serves as the SSOT of a work unit | #174 |
| `project/psxrecomp-studio/real-rom-analysis` | Repeatable real-ROM verification flow: fixture detection, staged analysis with stage-level progress, PASS/FAIL plus failing stage and reason, separated detailed log and summary report, findings routed to follow-up Issues (spec: `docs/development/real-rom-analysis.md`) | #213 |

## Release skill evaluation

A Release skill was **evaluated and deliberately not created** (Issue #174 makes
it optional: "Issue / Release Skill の必要性を評価し、必要なら作成").

Reasons:

1. **No established release process exists** in this repository — no git-tag
   release operation, no changelog, and no release workflow in CI. The only
   `release` usage is the CMake `Release` build type, which is a build
   configuration, not a release process. There is therefore no process to SSOT.
2. **Directory-name conflict**: the existing `.gitignore` `Release/` build-output
   rule matches a `release/` directory when `core.ignorecase=true` (the default
   for this Windows-originated repo), so a `skills/common/task/release/` directory
   would not be tracked by `git add .` and could be lost on a clean checkout.
   Tracking it would require `git add -f`, which is not an acceptable operating
   constraint.

A Release skill should be added later, under a non-conflicting directory name,
once a real release process is established (and its SSOT/operating rules exist).
This decision was not treated as implementation failure; it satisfies the DoD's
"evaluate and create if needed" condition.

## Relationship to Issue #81

Issue #81 plans the full infrastructure: machine-readable `registry.yaml`,
skill resolver, agent/environment adapters, validation in CI. That registry is
**intentionally not created yet**; this directory only fixes the storage layout
(`skills/common/<category>/<skill>/SKILL.md`, `skills/project/<name>/`) as a
compatible subset of the structure proposed there. When the registry lands,
these files become its entries without relocation.

## Conventions

- One directory per skill, containing `SKILL.md`.
- Frontmatter: `name`, `description`, `version`, plus optional `scope`,
  `platform`, `related-issues`.
- Skills evolve through normal PRs; recurring review findings should promote
  rules into the relevant skill checklist or an ADR (see the self-review skill's
  feedback loop).
