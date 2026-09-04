---
name: commit-message
description: >
  Commit Message authoring skill for AI agents: the single source of truth for
  writing git commit messages — Conventional Commits format, type/scope/subject
  policy, per-commit change granularity and atomicity, Issue-linkage trailers,
  amend/rebase handling, generated-commit marking, sensitive-data exclusion,
  and deriving type / scope / subject from the actual diff. Use whenever a
  commit message is created or rewritten.
version: 0.1.0
scope: process
platform: agent-agnostic
---

# Commit Message Skill

The single source of truth for **how a commit message is written**. It owns the
message content only: format, type/scope/subject, per-commit granularity,
Issue-linkage trailers, sensitive/session-data exclusion, amend/rebase handling,
and the AI judgement rules for producing a message from the actual diff. It does
not govern commit mechanics (branching, pushing, rebase flow), merge-time
operations, or the final task report.

This skill is project-agnostic; project-specific inputs (closing-keyword
policy, issue-tracker reference format, concrete examples, automation policy)
are loaded from the **Project Profile** (see
[Porting](#porting-to-another-project)). The [Common
Skill](../../../common/task/common/SKILL.md) rules apply throughout, especially
"verify the actual repository state" and "never fabricate results".

## When to apply

Apply whenever a commit message is **created or rewritten**:

- Composing a new commit (any type, including generated/automation commits).
- Amending, rewording, reordering, or squashing existing commit messages
  (local, unpublished history).
- Verifying a commit message against this skill's rules before push or PR.

This skill consumes the actual diff as ground truth; it does not prescribe how
the work itself is carried out (that is the task skills' job).

## Commit message anatomy

Standard [Conventional Commits] format:

```text
<type>(<optional scope>): <subject>

<optional body>

<optional trailers>
```

| Part | Rule |
|---|---|
| `type` | One of the fixed set in [Type policy](#type-policy), always lowercase. |
| `scope` | Optional; lowercase, short. See [Scope policy](#scope-policy). |
| `subject` | Imperative mood, ≤ ~72 chars, one concern, no trailing period. See [Subject granularity](#subject-granularity). |
| `body` | Optional; explains *why* / context the diff cannot show. Wrap ~72 chars. |
| `trailers` | Optional; `Refs #<n>` / `Fixes #<n>` / `Closes #<n>` at the end, separated from the body by a blank line. See [Issue linkage](#issue-linkage). |

[Conventional Commits]: https://www.conventionalcommits.org

## Sensitive data and session references

A commit message is durable Git history and may be copied into pull requests,
mirrors, release tooling, logs, or other public surfaces. Treat every part of the
message — subject, body, and trailers — as publishable text.

**Never include** any of the following in a commit message:

- ChatGPT, Claude, Codex, OpenCode, or other AI/chat **session or conversation
  URLs**, session IDs, conversation IDs, or equivalent session locators.
- API keys, access/refresh tokens, cookies, passwords, credentials, signing
  material, or other authentication secrets.
- Private URLs, temporary download links, signed URLs, pre-signed URLs, or URLs
  containing sensitive query parameters.
- Personal information or confidential/project-internal information that should
  not be preserved in public Git history.
- Internal environment identifiers, hostnames, filesystem paths, infrastructure
  details, or other values that are sensitive in the project's context.

Do not add generated attribution/footer text that exposes or references an
AI/chat session, such as `Generated with ... session: <url>`. Identifying that a
commit was automated, when project policy requires it, must use the durable
marker rules in [Generated and automation commits](#generated-and-automation-commits)
and must not include session identifiers or links.

When context from a private session explains *why* a change was made, rewrite
that rationale as durable project context. Commit messages should retain the
change, rationale, and verified Issue linkage — not the private conversation that
produced them.

If sensitive/session data is discovered in an unpublished commit message,
rewrite it before push. If it is already published/shared, do not silently
rewrite history; follow the repository's history-rewrite policy and obtain any
required approval.

## Type policy

Use one type from the fixed core set:

`feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `build`, `ci`, `perf`,
`revert`, `style`

| Type | Use for |
|---|---|
| `feat` | A new capability, behavior, or process artifact (what counts as a "feature" is a Project-Profile convention). |
| `fix` | Correction of wrong behavior, wrong output, or a defect. |
| `docs` | Documentation-only changes. |
| `refactor` | Behavior-preserving restructure of code. |
| `test` | Test-only changes (additions and updates). |
| `chore` | Housekeeping not affecting shipped behavior (dependency bumps, misc tooling). |
| `build` | Build system or build-dependency changes. |
| `ci` | CI workflow / configuration changes. |
| `perf` | Performance improvement. |
| `revert` | Reverting a previous change. |
| `style` | Formatting / whitespace-only changes with no behavior effect. |

Rules:

- A type **outside the core set must be justified and approved**, not invented
  silently. If no core type fits, prefer `chore` with a clear subject, or ask.
- Choosing between overlapping types (e.g. `feat` vs `docs` for a
  documentation artifact) is governed by the Project Profile's convention when
  one exists; otherwise pick the type that best describes the dominant content
  of the change.

## Scope policy

- **Optional.** Include a scope when the type alone is ambiguous across areas
  (e.g. a bug that could be in any of several subsystems) or when naming the
  area adds real information.
- **Form**: one short, lowercase noun phrase naming the touched area (e.g.
  `auth`, `parser`, `api`). No punctuation, no multiple scopes.
- **Multiple areas in one commit** → the commit is probably too large; split it
  (see [Per-commit change granularity](#per-commit-change-granularity)) instead
  of writing `type(a,b,c)`.
- **Omit** the scope when it adds nothing (repo-wide docs, release-wide tooling).

## Subject granularity

- **Imperative mood**: "add", "fix", "remove", "rename" — as if giving a
  command to the codebase.
- **≤ ~72 characters**; longer → shorten or move detail to the body.
- **Lowercase start** after the type/scope (proper nouns and naturally
  capitalized identifiers are exempt).
- **No trailing period.**
- **One concern.** If the subject needs "and" / "also" / ",", it combines
  multiple concerns — split into separate commits or wrap to the body.
- The subject states the *change* ("add X"), not the *intent* ("wanted to") or
  the *activity* ("worked on").

## Issue linkage

- `Refs #<n>` — the commit relates to issue `<n>` without closing it. Use for
  reference when the issue stays open.
- `Fixes #<n>` / `Closes #<n>` — **only** when the commit intentionally closes
  the issue **and** the Project Profile's Issue/PR conventions permit closing
  keywords here (many projects allow them only when every acceptance criterion
  is met). The concrete allowed-usage policy is a Project-Profile input; this
  skill does not set it.
- **Never fabricate an issue number.** If the issue's existence is not verified
  against the real issue tracker / driving task, omit the trailer.
- Trailer casing / keyword list follows the Project Profile's convention if it
  defines one.
- Trailers are placed after the body (or after the subject when there is no
  body), separated by a blank line, one trailer per issue.

## Per-commit change granularity

One commit = one logical change.

- **Atomicity**: a commit is self-contained; it does not depend on other
  unpublished commits and does not leave the tree in a knowingly broken state.
- **Belongs together**: changes that form a single logical unit — the same
  feature or fix where splitting would break the build, or where each part is
  meaningless without the others.
- **Must be split**:
  - unrelated fixes,
  - refactors mixed with behavior changes,
  - housekeeping (whitespace, formatting) mixed with functional changes,
  - documentation added mid-feature,
  - independently valuable, reviewable pieces of a large change.
- **Large PRs**: split into multiple logical commits; keep each commit
  self-contained and reviewable.
- **WIP / completion**: prefer a sequence of small, well-formed logical commits
  over a single large "WIP" commit. A temporary WIP commit is acceptable only
  while local and unpublished; rework it (fold / reword / split) into final
  commits before push, using the amend/rebase rules below.

## AI message-generation judgement

Generate the message from the **actual change**, never from intent, memory, or
a stale plan:

1. Inspect the real state first. For a new commit, inspect `git status`,
   `git diff --cached` (or `git diff --staged`) for staged content, and
   `git diff` for unstaged content. Derive the message from the content that
   will actually be committed; do not use unrelated unstaged changes.
2. For an amend, inspect the current commit with `git show HEAD` together with
   the staged amendment (`git diff --cached`) and any relevant unstaged diff;
   reason about the resulting commit.
3. For a reword, inspect the commit being reworded with `git show <commit>`.
   For a squash/fixup or interactive history rewrite, inspect the resulting
   combined commit or the relevant commit range (`git diff <base>..<tip>` /
   `git show <tip>`). Do not depend on a non-empty working-tree `git diff`.
4. **Type** ← the dominant category of the operation's actual change (`feat` / `fix` / `docs` / …).
5. **Scope** ← the area the change concentrates in, if naming it adds
   information.
6. **Subject** ← a ≤72-char imperative summary of the principal change, derived
   from the inspected content, not from the task title.
7. **Body** ← add when the change cannot convey the *why*: rationale, trade-offs,
   constraints, or a meaningful decision. Do not restate the diff line-by-line.
8. **Trailers** ← add only when linkage is verified (see
   [Issue linkage](#issue-linkage)).
9. **Sensitive-data check** ← inspect the complete resulting message and remove
   session references, credentials, private/signed URLs, personal data, or
   other sensitive information before committing.

When to ask vs. default:

- If the diff contains multiple distinct concerns, **split first** — do not
  force them under one message.
- If the type is genuinely ambiguous, pick the dominant one; if still ambiguous
  and policy does not settle it, ask rather than inventing a custom type.
- Never guess an issue number, and never assert that a commit closes an issue
  without verifying it (Common Skill: ground truth, no fabricated results).
- If the message would not accurately describe the diff, stop and reconcile the
  diff or the message — do not ship a message that contradicts the change.

## Examples

Good:

| Message | Why |
|---|---|
| `feat(auth): add refresh-token rotation` | One new capability, scoped, imperative, lowercase, ≤72 chars. |
| `fix(parser): handle empty input lines` | Clear defect fix in one area; actionable subject. |
| `docs: clarify install prerequisites` | Docs-only change; scope omitted (repo-wide); imperative. |
| `test(engine): cover scheduler timeout boundary` | Test-only; names the exact behavior being locked in. |

Bad:

| Message | Why |
|---|---|
| `Fix bug in the parser` | Capitalized type never used; vague subject; not Conventional Commits format. |
| `feat: add stuff to make it work better` | Subject not actionable; no concrete change named. |
| `feat(ui,api): add settings panel and refactor shared cache` | Two scopes + two unrelated concerns in one commit. |
| `fix: correct off-by-one and fix styling and add tests` | Mixed concerns (fix + style + tests) that must be split. |
| `docs: update guide\n\nGenerated with Claude session: https://example.invalid/session/123` | Leaks a session reference into durable Git history. |

## Amend and rebase handling

- **Amend is appropriate** for local, **unpublished** commits: fixing a
  message, adding/removing a trailer, or folding a follow-up fix into the same
  logical change. `git commit --amend` rewrites history and is safe only while
  the commit has not been shared.
- **Rewriting published / shared history requires explicit permission.** When
  permitted, use `--force-with-lease` (never plain `--force`), consistent with
  the [Merge Skill](../merge/SKILL.md) safety constraints. Project-specific
  force-push restrictions (which branches may never be force-pushed) are
  Project-Profile and merge-orchestration policy; this skill does not enumerate
  branches.
- Squashing/reordering **local** commits before push is fine and is the
  expected way to shape a PR's commit history.
- After any rewrite, re-check the affected message(s) against
  [Validation](#validation).

## Generated and automation commits

- Mark commits the agent or a tool created without per-message human wording
  with an **explicit marker** in the subject (e.g. a `chore(<tool>):` type plus
  a clear `[automated]` tag), and state in the body that the commit was
  generated and by what.
- Generated/automation attribution must never include a session URL, session
  identifier, conversation URL, conversation identifier, or other sensitive
  locator; use only durable tool/automation identification.
- **Humans must review the content** of generated commits before merge, the
  same as any other commit — the marker signals "review this", never "skip
  review".
- Which tools may commit, when automation may commit, and any required review
  are **project policy** (Project Profile); this skill only prescribes that the
  marker exist.

## Validation

Before pushing a commit (and again when rewriting), check its message:

- [ ] `type` is from the core set, lowercase; any other type is justified and
      approved.
- [ ] `scope`, if present, is lowercase, short, and a single noun phrase.
- [ ] `subject` is imperative, ≤ ~72 chars, lowercase-start, no trailing
      period, and captures exactly one concern.
- [ ] The message matches the diff: it names the change the hunks actually
      make, with nothing omitted and nothing invented.
- [ ] `body` explains *why* when needed and says nothing false.
- [ ] Trailers reference issues that actually exist; closing trailers only when
      the commit closes the issue and the Project Profile permits it.
- [ ] No fabricated issue numbers, no asserted-but-unverified closing.
- [ ] No session/conversation URLs or IDs, credentials, tokens, cookies,
      private/signed URLs, personal information, or other sensitive data.
- [ ] Generated attribution/footer text contains no session or conversation
      locator.
- [ ] The commit is one logical change (or the diff was already split).

Practical note: to avoid an interactive editor session (e.g. vim) blocking an
agent, provide the message via `git commit -m`, a `-F <file>`, or an editor
configured through `core.editor`.

## Relationship to other skills

| Concern | Owned by |
|---|---|
| Branch / commit / push mechanics, rebase flow | Git Workflow Skill *(future — not created yet)* |
| Commit message authoring (format, type, subject, trailers, granularity, sensitive-data exclusion) | **this skill** |
| Merge-time operations: approval, rebase-to-main, validation, merge | [Merge Skill](../merge/SKILL.md) |
| Merge / rebase orchestration across issues | [Batch Skill](../batch/SKILL.md) (reference) |
| Final task report (which cites commits) | [Reporting Skill](../reporting/SKILL.md) |
| Pre-PR self review gate | [Self-Review Skill](../self-review/SKILL.md) — referenced, not duplicated |
| Documentation sync gate | [Doc-Sync Skill](../doc-sync/SKILL.md) — referenced, not duplicated |
| Universal rules (ground truth, no fabricated results) | [Common Skill](../../../common/task/common/SKILL.md) |

Role boundaries:

- The commit message is **not** the report: the report cites commits and
  evidence (the Reporting Skill owns the report).
- Merge-time operations (rebase-to-main, approval, merge) are outside this
  skill's scope even though they can rewrite commit messages; follow the Merge
  Skill for those.
- This skill does not duplicate reporting duties, PR-body content, or merge
  discipline; it links to their owners.

## Porting to another project

Copy this skill unchanged. In the Project Profile provide:

1. Issue / PR conventions: closing-keyword policy (when `Fixes` / `Closes` may
   be used), allowed keyword list, and trailer casing.
2. The issue-tracker reference format and whether issue references are
   mandatory in commit trailers.
3. The convention for typing process artifacts (e.g. new skills), when one
   exists.
4. Automation / bot commit policy (which tools may commit, review
   requirements).
5. Worked examples from the repository's history (good and bad).

This skill adds no project-specific content of its own.

## Non-goals

- Prescribing how the work itself is carried out, tested, or reported (task
  skills and the Reporting Skill).
- Prescribing branch / commit / push mechanics or rebase workflows (future Git
  Workflow Skill) or merge-time operations (Merge Skill).
- Replacing human review of commit content, including for generated commits.
- Encoding any single project's issue numbers, branch names, or conventions as
  mandatory for all projects (those are Project Profile inputs).
