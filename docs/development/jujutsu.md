# Optional Jujutsu (jj) Workflow

**Status:** Stable

**Authority:** Reference

## Purpose

Describe how a contributor may use [Jujutsu](https://docs.jj-vcs.dev/) (`jj`) as a
local front end on top of the normal Git clone of PSXRecompStudio.

Jujutsu is **optional**. GitHub, the Git remote, GitHub Actions, PRs, Issues, tags
and Releases are unchanged, and `git` remains fully supported. Nothing in the
repository requires `jj`, and no agent is required to use it. Git operations
policy is still owned by
[`skills/common/process/git-workflow/SKILL.md`](../../skills/common/process/git-workflow/SKILL.md);
this document only maps that workflow onto `jj` commands.

```text
GitHub  <->  Git repository (.git)  <->  jj (colocated)
```

Commands below were verified against `jj 0.45.1` on Windows using a scratch clone.
The CLI changes between releases; if a command differs, trust `jj help <command>`.

## Why use it

- The working copy is always a commit, so there is no staging area and no
  "dirty tree blocks checkout" state.
- Rebasing onto a moved `main` is a single command, and conflicts are stored in
  the commit instead of stopping the rebase.
- `jj op log` / `jj undo` make history-changing mistakes recoverable.

## Installation

| Platform | Command |
|---|---|
| Windows | `winget install --id jj-vcs.jj -e` (restart the shell for `PATH`) |
| Fedora / Nobara | Unofficial COPR: `sudo dnf copr enable aldantanneo/jj-vcs`, then `sudo dnf install jj-cli` |
| Debian / Ubuntu | No maintained apt package is assumed; use a prebuilt binary or `cargo` |
| Any (Rust toolchain) | `cargo install --locked jj-cli` |
| Any (prebuilt) | Download the archive for your platform from <https://github.com/jj-vcs/jj/releases> and put `jj` on `PATH` |

Check with `jj --version`. The Fedora command above follows jj's official installation
guide but uses an **unofficial COPR**; distribution packaging can change, so verify the
[official installation guide](https://docs.jj-vcs.dev/latest/install-and-setup/).

Set your identity once (use the same identity as `git config user.name/user.email`):

```bash
jj config set --user user.name "Your Name"
jj config set --user user.email "you@example.com"
```

## Adding jj to an existing clone

From the repository root:

```bash
jj git init --colocate     # colocation is the default in 0.45; the flag is explicit
jj bookmark track main@origin
```

- `.git` is kept. Colocation adds `.jj/`, which contains its own `.gitignore`
  (`/*`), so **no change to the repository `.gitignore` is needed** and `git status`
  stays clean. Never commit `.jj/`.
- `jj git colocation status` shows the current mode; `jj git colocation disable`
  converts to a non-colocated repository (do not do this if you want `git`
  commands to keep working in the same directory).

### Windows: line endings

If `git config core.autocrlf` is `true`, jj's default
(`working-copy.eol-conversion = "none"`) reports **every file as modified**. Right
after `jj git init`, set the conversion for this repository and rebuild the
working copy (safe while the tree is clean):

```bash
jj config set --repo working-copy.eol-conversion input-output
jj restore
jj status          # expect: "The working copy has no changes."
```

Also keep the clone path short: a deeply nested path exceeding the Windows
`MAX_PATH` limit made `jj git init` fail while writing its index.

### Starting from a fresh clone

```bash
jj git clone https://github.com/mao2009/PSXRecompStudio.git   # colocated by default
```

Apply the same `eol-conversion` setting on Windows if needed.

## Verify Git and jj agree

```bash
git status -sb && git log --oneline -5 && git branch && git remote -v
jj status && jj log -n 5 && jj git remote list
git rev-parse main                                          # same commit id ...
jj log -r main --no-graph -T 'commit_id ++ "\n"'            # ... as this
```

Commit ids are identical because jj stores its commits in the Git object database.

## Basic workflow (Git to jj)

| Goal | Git | jj |
|---|---|---|
| Update main | `git switch main && git pull --ff-only` | `jj git fetch` (tracked `main` follows `main@origin`) |
| Start a task | `git switch -c feat/123-x` | `jj new main -m "feat: ..."` |
| Inspect | `git status` / `git diff` | `jj status` / `jj diff` |
| Commit | `git add` + `git commit` | Nothing to stage; `jj describe -m "..."`, then `jj new` to start the next change |
| Amend | `git commit --amend` | Just keep editing `@`; or `jj squash` from a child change |
| Pick a revision | branch / SHA | change id (stable across rewrites), commit id, bookmark, `@`, `@-` |
| Undo | `git reflog` | `jj undo`, `jj op log`, `jj op restore` |

Model in short: `@` is the working-copy commit and is snapshotted automatically
by every `jj` command. A *change id* stays stable when the change is rewritten;
the commit id does not. Commit messages must still follow the
[Commit Message Skill](../../skills/common/process/commit-message/SKILL.md).

## Bookmarks and Git branches

A jj **bookmark is a Git branch**: `jj bookmark create X` becomes `refs/heads/X`
on push, and Git branches (including ones made by `git switch -c`) appear as
bookmarks after the next jj command. Bookmarks do **not** move automatically when
you commit; move them explicitly.

Keep the existing branch convention, `<type>/<issue-number>-<summary>`:

```bash
jj bookmark create feat/123-example -r @        # before the first push
jj bookmark set feat/123-example -r @           # after adding more changes (or: jj bookmark advance)
jj git push --bookmark feat/123-example         # never push main
gh pr create --base main --head feat/123-example
```

`main` is merge-only (see the Git Workflow Skill). Do not `jj git push --bookmark main`
and do not create commits directly on `main`.

`jj git push` only pushes what you name. After a rewrite (rebase/amend) of an
already pushed bookmark it reports `move sideways`, and jj checks the expected
remote state itself, refusing to overwrite an unexpected remote move. Still follow
the Git Workflow Skill's rules for rewriting a published PR branch. Do not
bookmark or push unrelated local branches.

## Fetch, push and GitHub

- Fetch: `jj git fetch` (tags are imported too). `git fetch` also works; jj imports
  the result on its next command.
- Push: `jj git push --bookmark <name>`; `git push` on the same clone also works.
- PRs, Issues, Actions, tags and Releases keep using `gh`, `git tag` and the
  existing workflows. `gh` does not need to know about jj, but it needs a Git
  repository (see [Workspaces](#workspaces-and-git-worktrees)).

## Keeping up with main

Instead of `git fetch && git rebase origin/main`:

```bash
jj git fetch
jj rebase -b feat/123-example -o main      # -b: the whole branch of changes; -o: destination
jj git push --bookmark feat/123-example
```

Before a merge the [Merge Skill](../../skills/common/process/merge/SKILL.md)
still owns the mandatory latest-`main` rebase and re-validation. Any push after a
rebase invalidates review evidence bound to the previous HEAD.

## Conflicts

jj records a conflict inside the commit and lets the rebase finish.

```bash
jj status                                # lists conflicted paths
jj log -r 'conflicts()'                  # which changes are conflicted
jj new <conflicted-change-id>            # work on top of it
# edit the conflict markers (or use `jj resolve`)
jj squash                                # move the resolution into the conflicted change
jj log -r 'conflicts()'                  # empty output = resolved
```

Do not push while `jj log -r 'conflicts()'` still lists your change.

## Workspaces and Git worktrees

| | `jj workspace` | `git worktree` |
|---|---|---|
| Create | `jj workspace add ../psx-123 -r main` | `git worktree add ../psx-123 -b feat/123-x origin/main` |
| Directory has `.git` | **No** (only `.jj`) | Yes |
| `git` / `gh` inside it | `git` fails ("not a git repository"); `gh -R owner/repo ...` works | Works |
| Sees other workspaces' changes | Yes, one shared commit graph | Only via branches |
| Remove | `jj workspace forget`, then delete the directory | `git worktree remove` |

- Git worktrees **stay supported**, including the existing `.claude/worktrees/`
  and `PSXRecompStudio-wt-<issue>` layouts and the Batch Skill's lanes. They are not
  deprecated by this document.
- A Git worktree created from a colocated repository is a normal Git checkout;
  jj in the main directory imports its branch as a bookmark but does not manage
  the worktree. Avoid running `jj` inside it (there is no `.jj`).
- For humans running several tasks with jj, `jj workspace` is a good fit because
  concurrent changes share one graph. Any script or agent that needs `git` in
  its directory (`git rev-parse`, `gh pr create` without `-R`, hooks) should keep
  using a Git worktree. Run `git`/`gh pr create` from the main colocated directory
  instead if you use workspaces.
- Do not point two agents at the same workspace or worktree.

## Git commands in a colocated repository

Git tools see a normal repository, so agents that run `git` keep working:

- `git status`, `git diff`, `git log`, `git branch`, `git fetch` and `git switch -c`
  behave normally; `git commit` is picked up by jj on the next command.
- Git's `HEAD` follows the jj working copy and is frequently detached. Check
  `git branch --show-current` before committing.
- jj moves `HEAD` and the Git index for you. Avoid mixing per-file `git add`
  staging with an in-progress `jj` change, and avoid `git rebase`, `git reset --hard`,
  `git stash` and `git checkout -- .` on a clone with uncommitted jj work; use
  `jj undo` / `jj op restore` to recover instead.
- A `git commit` made while on `main` moves local `main`, which breaks the
  merge-only rule and shows `main*` (diverged from `origin`) in `jj log`. Recover
  with `jj bookmark set main -r main@origin --allow-backwards` and `jj abandon <id>`.

Agents are **not** required to use jj. An agent that uses `git` on a jj-enabled
clone is supported; an agent that detects `.jj/` should not delete it.

## Returning to Git only

`.jj/` is only metadata; bookmarks are already Git branches and every commit is in
`.git`. To stop using jj:

```bash
jj git push --bookmark <anything-unpushed>   # optional, if you want a remote copy
rm -rf .jj                                   # or Remove-Item -Recurse -Force .jj
git switch main                              # HEAD may be detached after jj; or your task branch
git status
```

Changes that exist only as jj commits without a bookmark are not reachable from
any Git branch; create a bookmark for them first (`jj bookmark create <name> -r <id>`).

## What not to commit

`.jj/`, repo/workspace jj config, and your identity or machine-specific paths.
For security reasons, jj stores repo/workspace config outside the repository/workspace;
inspect the actual location with `jj config path --repo` or
`jj config path --workspace`. Do not migrate, rewrite history, or force-push as
part of adopting jj.
