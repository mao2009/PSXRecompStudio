---
name: issue
description: >
  Standard procedure for creating / updating an implementation Issue so that it
  serves as the SSOT of a work unit: clear requirements, acceptance criteria,
  explicit non-goals, and a Definition of Done, aligned with the repository's
  Issue/PR conventions and authority ordering.
version: 1.0.0
scope: process
platform: agent-agnostic
related-issues: "#174"
---

# Issue Skill

A task template for **creating or updating a GitHub Issue** that will drive AI
development work. Because the Common Skill treats the Issue as the SSOT of a
work unit, an Issue must be written well enough that an agent can implement from
it without guesswork, and a reviewer can verify against it.

This skill is project-agnostic; repository-specific Issue / PR conventions
(closing keywords, PR body requirements, authority ordering) come from the
**Project Profile**. The [Common Skill](common/SKILL.md) rules apply.

## When to apply

Use this skill when:

- Creating a new Issue (feature, bug, research, docs/process change).
- Updating an existing Issue that will be the basis for implementation.

If the task is implementation (not issue authoring), use the
[Implementation Skill](implementation/SKILL.md), whose Step 1 consumes the
Issue this skill produces.

## Preconditions

- A clear intent for what the issue should accomplish (or the issue to update).
- Access to the repository so existing conventions and open issues can be
  checked (do not duplicate or contradict existing Issues).
- [Common Skill](common/SKILL.md) accepted (an Issue must respect the SSOT and
  must not state guessed requirements as fact).

## Inputs

1. The goal of the Issue and any prior discussion / motivation.
2. The Project Profile: authority ordering, Issue/PR conventions, closing-keyword
   policy, existing related Issues.
3. The actual state: relevant SSOT / ADR / code that the Issue concerns, so the
   requirements it states are grounded, not guessed.

## Standard Procedure

```text
1. Check for duplicates / related Issues
2. Identify the requirement source (SSOT / ADR / real code) — do not invent
3. Write the requirement, acceptance criteria, non-goals, and Definition of Done
4. Align with repository conventions (labels, closing keywords, structure)
5. Self-check the Issue for clarity and implementability
```

### 1. Check for duplicates / related

Search existing open (and recently closed) Issues to avoid duplicating a
request and to reference related work. Note the relationship (depends on /
related to / duplicates) where relevant.

### 2. Ground the requirement

An Issue is built on observed facts, not speculation. Identify:

- The relevant SSOT / ADR / architecture constraint the requirement must respect.
- The real current behavior / code the Issue concerns.
- What is uncertain (record it as an open question rather than asserting it).

This satisfies the Common rules 3–4 applied to requirement authoring: state only
what can be verified, and mark inferences as such.

### 3. Write the Issue

A well-formed implementation Issue contains:

- **Purpose / background**: why this work exists, in the problem space.
- **Requirements**: what must be true after the work; explicit and concrete.
- **Acceptance criteria**: checkable conditions that define done behaviorally.
- **Explicit non-goals / out-of-scope**: what the Issue intentionally does not
  cover, to prevent scope creep (Common rule 7).
- **Definition of Done**: the completion conditions for this work unit.
- **Related work**: linked Issues / PRs / ADRs.

Where a concrete implementation approach is not yet decided, the Issue may
defer it, but the *requirements* and *acceptance criteria* must be concrete.

### 4. Align with repository conventions

Apply the host repository's conventions (from the Project Profile):

- Issue classification (labels, template if one exists).
- Referencing not closing vs. closing keywords (close only when all completion
  criteria are met; otherwise reference and explain what remains).
- Naming / structure expected by the repository.

### 5. Self-check

The Issue is implementable if an agent could work from it alone:

- Requirements are concrete and grounded; no requirement rests on guesswork.
- Acceptance criteria are checkable (verifiable against the implementation).
- Non-goals are explicit; the Definition of Done is defined.
- No contradiction with the SSOT / ADR / existing Issues.

## Verification

- [ ] No duplicate/obsolete issue created; related issues referenced.
- [ ] Requirements grounded in verified state (SSOT/ADR/code), not guesses.
- [ ] Acceptance criteria are concrete and checkable.
- [ ] Non-goals and Definition of Done are explicit.
- [ ] Repository conventions (labels, closing keywords, structure) followed.
- [ ] The Issue would let an agent implement without guesswork (Common rules 2–4).

## Definition of Done

- [ ] The Issue states purpose, requirements, acceptance criteria, non-goals, and
      a Definition of Done.
- [ ] Requirements are grounded in the actual SSOT / ADR / repository state.
- [ ] Conventions are followed and related work is linked.
- [ ] The Issue is implementable from its content alone.

## Failure Handling

- If the required behavior cannot be grounded in verified state, record the
  missing fact as an open question; do not invent a requirement and present it
  as fact.
- If the Issue would contradict an SSOT / ADR, resolve the contradiction before
  authoring rather than writing a conflicting requirement.
- If an existing Issue covers the request, do not create a duplicate.

## Output / Reporting Requirements

The deliverable is the created/updated Issue, plus (in the task report) the
resulting issue reference, the requirement sources used, and confirmation of
convention alignment.

## Porting to another project

Copy this skill unchanged; supply a Project Profile (conventions, closing-keyword
policy, authority ordering) per the host `skills/` conventions.

## Non-goals

- Implementing the issue (that is the Implementation Skill) or researching
  design space (that is the Research Skill).
- Inventing requirements not grounded in the SSOT / real repository state.
- Redefining an established development process that the repository already
  documents.
