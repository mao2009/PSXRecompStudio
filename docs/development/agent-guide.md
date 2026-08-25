# Development Agent Guide

**Status:** Stable

**Authority:** Reference

## Purpose

Provide a predictable bootstrap path for AI development agents working on PSXRecompStudio.

## Before changing code

1. Read the repository-level AI guidance when present.
2. Read `docs/README.md`.
3. Read `docs/architecture/README.md`.
4. Identify the relevant subsystem SSOT.
5. Inspect related open Issues and recent implementation PRs.
6. Inspect the relevant code.
7. Check architectural constraints before proposing changes.

## Git Hook Setup (Required)

本リポジトリでは main ブランチを保護する Git hook を導入しています。作業開始前に必ず有効化してください：

```bash
git config core.hooksPath .githooks
```

これにより以下が適用されます：
- **pre-commit**: main ブランチでの直接 commit を拒否
- **pre-push**: main ブランチでの直接 push を拒否

開発フロー：
1. `git checkout -b feature/your-feature` で feature branch 作成
2. 変更をコミット・push
3. PR 作成 → レビュー・CI 通過 → main へマージ

main への直接操作は hook により防止されます。

## Before creating a pull request

When the change touched implementation, architecture, CI/build/test
infrastructure, developer process or repository policy, policy configuration,
or process artifacts (skills, profiles, agent guides), first run the
documentation synchronization gate defined in
`skills/common/process/doc-sync/SKILL.md`, loading project-specific inputs
from the matching `skills/project/<project>/profile.md`, and record its
update/no-update decisions for the PR body.

Then perform the mandatory pre-PR self review defined in
`skills/common/process/self-review/SKILL.md`, loading project-specific inputs
from the matching `skills/project/<project>/profile.md`. Do not open a PR until
that skill's completion criteria are met.

## Authority hierarchy

Prefer information in this order when determining current intent:

1. Current subsystem SSOT.
2. Top-level Architecture SSOT.
3. Current accepted architectural decisions.
4. Open implementation Issues.
5. Current code, interpreted against the documented architecture.
6. Closed Issues and historical discussions.

If code and SSOT disagree, do not silently redefine the architecture. Determine whether the code is incomplete, the documentation is stale, or an architectural decision is intentionally changing.

## Implementation discipline

- Keep domain responsibilities independent from UI concerns.
- Reuse established models and contracts instead of creating parallel representations.
- Preserve architecture and terminology defined by SSOT documentation.
- Prefer structured results over presentation-specific strings.
- Add or update tests when behavior or contracts change.
- Update SSOT documentation when an implementation establishes or changes an architectural decision.

## Completion check

Before reporting completion, verify:

- The implementation matches the relevant SSOT.
- No documented constraint was violated.
- Relevant tests/analyzers pass.
- Documentation was updated when required.
- The working tree/branch contains only intentional changes.
- The final report identifies commits, tests, and remaining limitations.
