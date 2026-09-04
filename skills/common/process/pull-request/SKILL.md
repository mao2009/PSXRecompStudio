---
name: pull-request
description: >
  Pull Request authoring skill for AI agents: the single source of truth for
  PR title, description, issue linkage, verification evidence, reviewer-facing
  context, known limitations, and pre-creation validation. Use whenever a pull
  request is created or its title/body is materially rewritten.
version: 0.1.0
scope: process
platform: agent-agnostic
---

# Pull Request Skill

The single source of truth for **how a pull request is presented to reviewers**.
It owns PR title/body content, Issue linkage, verification evidence, reviewer
context, breaking-change disclosure, known limitations, and creation-time
validation. It does not own branch/commit/push mechanics, commit-message format,
merge-time approval/rebase logic, or the implementation itself.

This skill is project-agnostic. Project-specific conventions such as required
CI names, issue-closing policy, PR templates, release-note policy, and branch
naming are supplied by the host repository's Project Profile.

## When to apply

Apply whenever a pull request is:

- created,
- materially rewritten after scope changes,
- updated after significant review-driven implementation changes,
- prepared for final review when the body no longer reflects the actual diff.

Do not fabricate PR content from the task description alone. The actual diff,
commits, Issue state, and verification results are the ground truth.

## Responsibility boundary

| Concern | Owned by |
|---|---|
| Branch creation, commits, push, rebase workflow | Git Workflow Skill *(when present)* |
| Commit-message format and trailers | [Commit Message Skill](../commit-message/SKILL.md) |
| Pre-PR implementation review | [Self-Review Skill](../self-review/SKILL.md) |
| PR title/body, Issue linkage, verification evidence, reviewer context | **this skill** |
| Merge-time rebase, approval, final validation, merge | [Merge Skill](../merge/SKILL.md) |
| Final task report | [Reporting Skill](../reporting/SKILL.md) |

The Pull Request Skill must not duplicate merge-gate logic. A PR body may report
current CI/review state, but final merge eligibility is determined by the Merge
Skill against the then-current PR HEAD and base HEAD.

## Inputs

Before writing or updating a PR, verify:

1. The current PR branch and target branch.
2. The full diff against the intended base.
3. The commit list that will appear in the PR.
4. The driving Issue(s), including acceptance criteria and non-goals.
5. The result of the mandatory pre-PR Self-Review.
6. Verification actually run after the final implementation edit.
7. Any known limitations, deferred items, breaking changes, migrations, or
   compatibility impacts.
8. Project Profile conventions for title style, issue closing keywords, required
   body sections, CI naming, and release-note requirements.

If any claimed fact cannot be verified, omit it or state that it was not run /
not verified. Never convert an unknown into `PASS`.

## PR title

The title should describe the **dominant user-visible or repository-visible
change**, not the activity performed by the agent.

Default format:

```text
<type>(<optional scope>): <concise imperative summary>
```

Use the same type vocabulary and granularity principles as the Commit Message
Skill unless the Project Profile defines a different PR-title convention.

Rules:

- concise and specific; target roughly 72 characters or fewer,
- imperative wording (`add`, `fix`, `document`, `remove`, `refactor`),
- no issue number prefix unless project policy requires it,
- no `WIP`, `misc`, `updates`, `changes`, or other low-information titles,
- one dominant concern; if the PR truly contains unrelated changes, split it,
- never include session URLs, credentials, private links, or sensitive data.

Examples:

Good:

- `feat(parser): add relocation-aware symbol recovery`
- `fix(ci): validate review state on current PR head`
- `docs: add prior-art provenance references`

Bad:

- `Update files`
- `Issue #123 changes and other fixes`
- `WIP final version`

## Standard PR description

Use the following logical structure unless the repository template requires a
compatible variant. Omit empty sections rather than filling them with noise.

```markdown
## Summary

<What changed and why, in 1-3 concise paragraphs.>

## Changes

- <reviewer-relevant change 1>
- <reviewer-relevant change 2>

## Issue

Closes #<issue>

## Verification

- `<command or check>` — PASS
- `<command or check>` — PASS

## Known limitations

- <only when applicable>

## Breaking changes

- <only when applicable>
```

The exact heading names may follow the Project Profile, but the semantic content
must remain recoverable: purpose, scope, issue linkage, verification, and any
reviewer-relevant risk or limitation.

## Summary section

The summary answers:

- What changed?
- Why was this change necessary?
- What is intentionally not changed?

Prefer outcome-oriented wording. Do not restate every file touched. Mention a
scope boundary when it prevents reviewer confusion, especially when a nearby
related change was deliberately excluded.

## Changes section

List only changes that help a reviewer understand the implementation. Group by
behavior or responsibility rather than enumerating filenames line-by-line.

Good:

- `Add fail-closed validation for ambiguous review state.`
- `Preserve current-head SHA evidence through the merge gate.`

Avoid:

- `Changed file A.`
- `Updated file B.`
- `Various cleanup.`

For large PRs, group bullets under small thematic subheadings.

## Issue linkage

Use a verified Issue reference only.

- `Closes #<n>` / `Fixes #<n>` only when the PR satisfies the Issue's completion
  criteria and project policy permits automatic closure.
- `Refs #<n>` when the PR contributes to an Issue that intentionally remains open.
- Multiple Issues may be listed when the diff genuinely satisfies each one.
- Never guess or infer an Issue number from branch names alone; verify it against
  the issue tracker.
- If acceptance criteria remain incomplete, do not use a closing keyword.

The PR body is normally the preferred place for issue-closing linkage because it
binds closure to the merged PR rather than to an individual intermediate commit,
unless the Project Profile specifies otherwise.

## Verification evidence

Verification statements are evidence, not decoration.

For every listed check, state only what actually happened:

- `PASS` — command/check completed successfully on the relevant final state.
- `FAIL` — command/check ran and failed; PR is not ready unless failure is an
  explicitly accepted known condition.
- `NOT RUN` — not executed; include a concise reason when reviewer-relevant.
- `N/A` — genuinely not applicable; do not use this to avoid running a required
  check.

Prefer exact command names or stable check names. Examples:

```markdown
## Verification

- `dotnet test` — PASS
- `dotnet format --verify-no-changes` — PASS
- GUI E2E — NOT RUN (no display server in the execution environment)
```

Rules:

- verification must reflect the state **after the final implementation edit**;
- if a review fix changes relevant code, update the PR body after re-verification;
- CI results may be added after PR creation, but distinguish local verification
  from CI rather than presenting pending CI as passed;
- do not claim CodeRabbit, CI, human review, or any external system passed unless
  the actual current evidence shows it;
- current status in the PR body is informative only; merge-time freshness is
  revalidated by the Merge Skill.

## Breaking changes

Create a `Breaking changes` section when the PR intentionally changes an
external contract, public API, persisted data format, CLI behavior, configuration
schema, compatibility promise, or other consumer-visible contract.

State:

1. what breaks,
2. who/what is affected,
3. migration or adaptation required,
4. whether compatibility is intentionally not preserved.

Do not hide a breaking change in the generic summary.

If there are no breaking changes, omit the section unless the Project Profile
requires an explicit `None` statement.

## Known limitations and deferred work

Document reviewer-relevant limitations when they affect correctness boundaries,
portability, coverage, compatibility, rollout, or follow-up work.

A limitation must not be used to disguise an unmet acceptance criterion. If the
Issue requires the behavior now, it is a blocker, not a limitation.

For deferred work:

- link an existing follow-up Issue when one exists,
- create a separate Issue only when project/task policy calls for it,
- avoid vague promises such as `will fix later` without a tracked scope.

## Reviewer-oriented context

A good PR description makes the decision easy for a reviewer without requiring
reconstruction of the implementation history.

Include when relevant:

- architectural or SSOT boundary affected,
- why a non-obvious approach was chosen,
- risk concentration (e.g. concurrency, persistence, parsing, ABI, generated code),
- meaningful compatibility constraints,
- migration implications,
- deliberately rejected in-scope alternatives when the choice is not obvious,
- generated artifacts and how they were produced/validated.

Do not include:

- chat/session URLs or IDs,
- private reasoning transcripts,
- credentials, tokens, cookies, or signed/private URLs,
- personal/confidential information,
- long chronological development diaries,
- raw logs when a concise result is enough.

Translate private-session context into durable project rationale.

## PR creation procedure

1. **Verify repository state**
   - confirm branch/base,
   - inspect full diff,
   - inspect commits,
   - confirm no unrelated changes.
2. **Verify Issue scope**
   - map each acceptance criterion to the diff,
   - determine `Closes` vs `Refs` from actual completion.
3. **Run Self-Review**
   - complete the Self-Review Skill,
   - resolve all blockers/scope findings before PR creation.
4. **Collect verification**
   - run the Project Profile's required local verification ladder,
   - record exact results without fabrication.
5. **Compose title/body**
   - derive title from the actual dominant change,
   - write summary, changes, Issue linkage, verification, and applicable
     limitation/breaking-change sections.
6. **Sensitive-data check**
   - inspect the complete title/body for session locators, secrets, private URLs,
     personal data, and internal-sensitive values.
7. **Create PR**
   - target the intended base,
   - do not merge as part of PR creation.
8. **Post-create verification**
   - re-fetch the PR,
   - verify title, body, head/base, linked Issue, and changed-file scope,
   - correct metadata mistakes immediately.
9. **External review / CI**
   - allow repository automation to run,
   - update the body only when doing so improves durable reviewer context,
   - handle merge eligibility through the Merge Skill, not this skill.

## Updating an existing PR

Update the title/body when the actual scope materially changes after creation.
Typical triggers:

- review fixes add or remove meaningful behavior,
- an acceptance criterion becomes intentionally deferred,
- verification results change,
- a breaking change or limitation is discovered,
- the Issue linkage changes from `Refs` to `Closes` or vice versa.

Do not churn the body for every tiny commit. The PR description should describe
the current proposed change, not preserve an edit history.

## Validation checklist

Before creating or materially updating the PR:

- [ ] Title accurately describes the dominant diff and follows project format.
- [ ] Summary explains what changed and why.
- [ ] Change list is reviewer-oriented, not a filename dump.
- [ ] Every Issue reference is verified.
- [ ] Closing keyword is used only when acceptance criteria are satisfied.
- [ ] Verification results are exact and current; unrun checks are not presented
      as passing.
- [ ] Breaking changes are explicitly disclosed when present.
- [ ] Known limitations/deferred work are explicit and do not conceal blockers.
- [ ] The body contains enough architectural/risk context for efficient review.
- [ ] No unrelated changes are included in the PR.
- [ ] No session/conversation locator, secret, credential, private/signed URL,
      personal information, or sensitive internal value appears in title/body.
- [ ] Self-Review completed before creation.
- [ ] Merge-time requirements are not duplicated or falsely asserted as final.

## Completion criteria

This skill is satisfied when:

- the PR title/body accurately represents the actual current diff,
- Issue linkage is correct and auditable,
- verification evidence is truthful and useful,
- breaking changes and limitations are visible,
- reviewers can understand scope, intent, risk, and evidence without reconstructing
  the task history,
- the PR has been re-fetched after creation and its metadata/scope were verified.

## Non-goals

- Performing implementation work.
- Choosing branch names or controlling push/rebase mechanics.
- Authoring commit messages.
- Replacing Self-Review or external review.
- Deciding final merge eligibility.
- Performing the merge.

## Porting to another project

Copy this skill unchanged and supply a Project Profile containing:

1. PR title convention.
2. PR body template / required semantic sections.
3. Issue-closing keyword policy.
4. Required local verification ladder.
5. CI/check names that reviewers expect to see.
6. Branch/base conventions.
7. Release-note / changelog rules.
8. Sensitive/publication constraints beyond the universal rules above.
