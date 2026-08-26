# Skills

**Status:** Draft

**Authority:** Reference

**Related Issues:** #85, #84, #81, #89

## Purpose

Reusable development-process knowledge for AI agents, kept as platform-agnostic
markdown so any agent (OpenCode / Codex / Claude Code / other) can read it
directly.

Two kinds of content are deliberately separated:

```text
skills/
├── common/            # Generic, portable process skills (no project specifics)
│   └── process/
│       ├── batch/       # Parallel Issue execution Orchestrator (#155)
│       ├── doc-sync/
│       ├── merge/       # Safe PR Merge Skill (#146)
│       └── self-review/
└── project/           # Per-project inputs consumed by common skills
    └── psxrecomp-studio/
        └── profile.md
```

- `common/` skills must stay free of MIPS/R3000A/analyzer/test-command details.
  Everything project-specific belongs in a `project/<name>/profile.md`.
- Porting a skill to another project = copying `common/` and writing a new profile.

## Current contents

| Skill | Scope | Introduced by |
|---|---|---|
| `common/process/doc-sync` | Documentation synchronization gate: impact mapping, minimal updates, recorded no-op decisions | #89 |
| `common/process/self-review` | Mandatory pre-PR self-review gate + external-review feedback loop | #85 |
| `common/process/batch` | Parallel Issue execution Orchestrator with dependency scheduling, retry, failure isolation, and serial merge via Merge Skill | #145, #155 |
| `common/process/merge` | Safe PR Merge Skill with mandatory approval → rebase → validation → normal merge flow | #146 |

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
